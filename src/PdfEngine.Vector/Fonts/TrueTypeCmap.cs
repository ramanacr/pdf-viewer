using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Minimal, bounds-checked reader for the sfnt <c>cmap</c> table (formats 0, 4, 6 and 12) of an
/// embedded TrueType/OpenType font program. Never throws on malformed data; unmapped codes give 0.
/// </summary>
internal sealed class TrueTypeCmap
{
    private readonly byte[] _data;
    private readonly Dictionary<(int Platform, int Encoding), int> _subtables = new();

    private TrueTypeCmap(byte[] data)
    {
        _data = data;
    }

    /// <summary>Platform/encoding pairs present, in table order (first entry wins on duplicates).</summary>
    public IReadOnlyCollection<(int Platform, int Encoding)> Subtables => _subtables.Keys;

    /// <summary>Parses the cmap of an sfnt (or the first font of a TTC). Returns null when absent/invalid.</summary>
    public static TrueTypeCmap? TryParse(byte[]? font)
    {
        if (font == null || font.Length < 12)
            return null;
        try
        {
            int dirOffset = 0;
            if (ReadU32(font, 0) == 0x74746366) // 'ttcf'
            {
                if (ReadU32(font, 8) == 0 || !InBounds(font, 12, 4))
                    return null;
                dirOffset = (int)Math.Min(ReadU32(font, 12), int.MaxValue);
            }

            if (!InBounds(font, dirOffset, 12))
                return null;
            int numTables = ReadU16(font, dirOffset + 4);
            int cmapOffset = -1;
            for (int i = 0; i < numTables; i++)
            {
                int rec = dirOffset + 12 + i * 16;
                if (!InBounds(font, rec, 16))
                    break;
                if (ReadU32(font, rec) == 0x636D6170) // 'cmap'
                {
                    uint off = ReadU32(font, rec + 8);
                    if (off < (uint)font.Length)
                        cmapOffset = (int)off;
                    break;
                }
            }
            if (cmapOffset < 0 || !InBounds(font, cmapOffset, 4))
                return null;

            var cmap = new TrueTypeCmap(font);
            int count = ReadU16(font, cmapOffset + 2);
            for (int i = 0; i < count; i++)
            {
                int rec = cmapOffset + 4 + i * 8;
                if (!InBounds(font, rec, 8))
                    break;
                int platform = ReadU16(font, rec);
                int encoding = ReadU16(font, rec + 2);
                long sub = (long)cmapOffset + ReadU32(font, rec + 4);
                if (sub + 2 > font.Length)
                    continue;
                int format = ReadU16(font, (int)sub);
                if (format is 0 or 4 or 6 or 12 && !cmap._subtables.ContainsKey((platform, encoding)))
                    cmap._subtables[(platform, encoding)] = (int)sub;
            }
            return cmap._subtables.Count > 0 ? cmap : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public bool Has(int platform, int encoding) => _subtables.ContainsKey((platform, encoding));

    /// <summary>Glyph id for <paramref name="code"/> in the (platform, encoding) subtable; 0 if unmapped.</summary>
    public int Lookup(int platform, int encoding, int code)
    {
        if (code < 0 || !_subtables.TryGetValue((platform, encoding), out int offset))
            return 0;
        try
        {
            return ReadU16(_data, offset) switch
            {
                0 => LookupFormat0(offset, code),
                4 => LookupFormat4(offset, code),
                6 => LookupFormat6(offset, code),
                12 => LookupFormat12(offset, code),
                _ => 0
            };
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return 0;
        }
    }

    /// <summary>Lookup in the first subtable of the font (any platform); 0 if unmapped.</summary>
    public int LookupFirst(int code)
    {
        foreach (var key in _subtables.Keys)
        {
            int gid = Lookup(key.Platform, key.Encoding, code);
            if (gid != 0)
                return gid;
        }
        return 0;
    }

    private int LookupFormat0(int offset, int code)
    {
        if (code > 255 || !InBounds(_data, offset + 6 + code, 1))
            return 0;
        return _data[offset + 6 + code];
    }

    private int LookupFormat4(int offset, int code)
    {
        if (code > 0xFFFF || !InBounds(_data, offset, 14))
            return 0;
        int segCount = ReadU16(_data, offset + 6) / 2;
        int endCodes = offset + 14;
        int startCodes = endCodes + segCount * 2 + 2;
        int idDeltas = startCodes + segCount * 2;
        int idRangeOffsets = idDeltas + segCount * 2;
        if (segCount == 0 || !InBounds(_data, idRangeOffsets, segCount * 2))
            return 0;

        // Binary search on endCode.
        int lo = 0, hi = segCount - 1, seg = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int end = ReadU16(_data, endCodes + mid * 2);
            if (end < code)
            {
                lo = mid + 1;
            }
            else
            {
                seg = mid;
                hi = mid - 1;
            }
        }
        if (seg < 0)
            return 0;

        int start = ReadU16(_data, startCodes + seg * 2);
        if (code < start)
            return 0;
        int delta = ReadU16(_data, idDeltas + seg * 2);
        int rangeOffsetPos = idRangeOffsets + seg * 2;
        int rangeOffset = ReadU16(_data, rangeOffsetPos);
        if (rangeOffset == 0)
            return (code + delta) & 0xFFFF;

        long glyphPos = (long)rangeOffsetPos + rangeOffset + 2L * (code - start);
        if (glyphPos + 2 > _data.Length)
            return 0;
        int glyph = ReadU16(_data, (int)glyphPos);
        return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
    }

    private int LookupFormat6(int offset, int code)
    {
        if (!InBounds(_data, offset, 10))
            return 0;
        int first = ReadU16(_data, offset + 6);
        int count = ReadU16(_data, offset + 8);
        int index = code - first;
        if (index < 0 || index >= count || !InBounds(_data, offset + 10 + index * 2, 2))
            return 0;
        return ReadU16(_data, offset + 10 + index * 2);
    }

    private int LookupFormat12(int offset, int code)
    {
        if (!InBounds(_data, offset, 16))
            return 0;
        uint groups = ReadU32(_data, offset + 12);
        long maxGroups = (_data.Length - (offset + 16L)) / 12;
        if (groups > maxGroups)
            groups = (uint)Math.Max(0, maxGroups);

        uint c = (uint)code;
        long lo = 0, hi = (long)groups - 1;
        while (lo <= hi)
        {
            long mid = (lo + hi) >> 1;
            int g = offset + 16 + (int)mid * 12;
            uint startChar = ReadU32(_data, g);
            uint endChar = ReadU32(_data, g + 4);
            if (c < startChar)
            {
                hi = mid - 1;
            }
            else if (c > endChar)
            {
                lo = mid + 1;
            }
            else
            {
                long gid = ReadU32(_data, g + 8) + (long)(c - startChar);
                return gid > 0xFFFF ? 0 : (int)gid;
            }
        }
        return 0;
    }

    private static bool InBounds(byte[] data, long offset, long length) =>
        offset >= 0 && length >= 0 && offset + length <= data.Length;

    private static int ReadU16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

    private static uint ReadU32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
}
