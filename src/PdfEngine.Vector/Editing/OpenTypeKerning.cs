using System;
using System.Collections.Generic;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Pair kerning from a font: the GPOS 'kern' feature's pair adjustments (formats 1 and 2, also
/// through extension lookups), else the legacy 'kern' table (format 0, horizontal). Values are in
/// font units: how much the first glyph's advance changes when the second follows it.
/// </summary>
internal sealed class OpenTypeKerning
{
    private readonly byte[]? _gpos;
    private readonly List<(int Offset, int Format)> _pairSubtables = new();
    private readonly Dictionary<(int, int), short>? _legacy;
    private readonly Dictionary<(int, int), int> _cache = new();

    private OpenTypeKerning(byte[]? gpos, Dictionary<(int, int), short>? legacy)
    {
        _gpos = gpos;
        _legacy = legacy;
    }

    /// <summary>The font's kerning; null when it has none (or its tables are damaged).</summary>
    public static OpenTypeKerning? Read(IReadOnlyDictionary<string, byte[]> tables)
    {
        try
        {
            if (tables.TryGetValue("GPOS", out var gpos))
            {
                var k = new OpenTypeKerning(gpos, null);
                k.FindPairSubtables();
                if (k._pairSubtables.Count > 0) return k;
            }
            if (tables.TryGetValue("kern", out var kern) && ReadLegacy(kern) is { Count: > 0 } pairs)
                return new OpenTypeKerning(null, pairs);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
        }
        return null;
    }

    /// <summary>The adjustment of <paramref name="left"/>'s advance before <paramref name="right"/>, in font units.</summary>
    public int Pair(int left, int right)
    {
        if (_cache.TryGetValue((left, right), out int cached)) return cached;
        int value = 0;
        try
        {
            if (_legacy != null) value = _legacy.TryGetValue((left, right), out short v) ? v : 0;
            else
                foreach (var (offset, format) in _pairSubtables)
                    if (PairValue(offset, format, left, right) is { } found)
                    {
                        value = found;
                        break; // the first subtable that has the pair applies
                    }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            value = 0;
        }
        if (_cache.Count < 65536) _cache[(left, right)] = value;
        return value;
    }

    // ------------------------------------------------------------------ GPOS

    private void FindPairSubtables()
    {
        var t = _gpos!;
        if (t.Length < 10 || BigEndian.U16(t, 0) != 1) return;
        int featureList = BigEndian.U16(t, 6), lookupList = BigEndian.U16(t, 8);
        var lookups = new SortedSet<int>();
        int featureCount = BigEndian.U16(t, featureList);
        for (int i = 0; i < featureCount; i++)
        {
            int rec = featureList + 2 + i * 6;
            if (t[rec] != 'k' || t[rec + 1] != 'e' || t[rec + 2] != 'r' || t[rec + 3] != 'n') continue;
            int feature = featureList + BigEndian.U16(t, rec + 4);
            int count = BigEndian.U16(t, feature + 2);
            for (int j = 0; j < count; j++) lookups.Add(BigEndian.U16(t, feature + 4 + j * 2));
        }
        int lookupCount = BigEndian.U16(t, lookupList);
        foreach (int index in lookups)
        {
            if (index >= lookupCount) continue;
            int lookup = lookupList + BigEndian.U16(t, lookupList + 2 + index * 2);
            int type = BigEndian.U16(t, lookup);
            int subtables = BigEndian.U16(t, lookup + 4);
            for (int s = 0; s < subtables; s++)
            {
                int sub = lookup + BigEndian.U16(t, lookup + 6 + s * 2);
                int subType = type;
                if (type == 9) // extension: the real subtable is further on
                {
                    subType = BigEndian.U16(t, sub + 2);
                    sub += (int)Math.Min(BigEndian.U32(t, sub + 4), int.MaxValue);
                }
                if (subType != 2 || sub + 10 > t.Length) continue;
                int format = BigEndian.U16(t, sub);
                if (format is 1 or 2) _pairSubtables.Add((sub, format));
            }
        }
    }

    private int? PairValue(int sub, int format, int left, int right)
    {
        var t = _gpos!;
        int coverage = Coverage(sub + BigEndian.U16(t, sub + 2), left);
        if (coverage < 0) return null;
        int vf1 = BigEndian.U16(t, sub + 4), vf2 = BigEndian.U16(t, sub + 6);
        int size1 = ValueSize(vf1), size2 = ValueSize(vf2);
        if (format == 1)
        {
            int pairSets = BigEndian.U16(t, sub + 8);
            if (coverage >= pairSets) return null;
            int set = sub + BigEndian.U16(t, sub + 10 + coverage * 2);
            int count = BigEndian.U16(t, set);
            int record = 2 + size1 + size2;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int at = set + 2 + mid * record;
                int second = BigEndian.U16(t, at);
                if (second == right) return XAdvance(at + 2, vf1);
                if (second < right) lo = mid + 1; else hi = mid - 1;
            }
            return null;
        }
        int class1 = ClassOf(sub + BigEndian.U16(t, sub + 8), left), class2 = ClassOf(sub + BigEndian.U16(t, sub + 10), right);
        int class1Count = BigEndian.U16(t, sub + 12), class2Count = BigEndian.U16(t, sub + 14);
        if (class1 >= class1Count || class2 >= class2Count) return null;
        int value = sub + 16 + (class1 * class2Count + class2) * (size1 + size2);
        return XAdvance(value, vf1);
    }

    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    // XAdvance (0x0004) follows XPlacement (0x0001) and YPlacement (0x0002) when they are there.
    private int XAdvance(int record, int format)
    {
        if ((format & 0x0004) == 0) return 0;
        int skip = 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0x0003));
        return BigEndian.S16(_gpos!, record + skip);
    }

    private int Coverage(int table, int glyph)
    {
        var t = _gpos!;
        int format = BigEndian.U16(t, table), count = BigEndian.U16(t, table + 2);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (format == 1)
            {
                int g = BigEndian.U16(t, table + 4 + mid * 2);
                if (g == glyph) return mid;
                if (g < glyph) lo = mid + 1; else hi = mid - 1;
            }
            else if (format == 2)
            {
                int at = table + 4 + mid * 6;
                int start = BigEndian.U16(t, at), end = BigEndian.U16(t, at + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return BigEndian.U16(t, at + 4) + glyph - start;
            }
            else return -1;
        }
        return -1;
    }

    private int ClassOf(int table, int glyph)
    {
        var t = _gpos!;
        int format = BigEndian.U16(t, table);
        if (format == 1)
        {
            int start = BigEndian.U16(t, table + 2), count = BigEndian.U16(t, table + 4);
            return glyph >= start && glyph < start + count ? BigEndian.U16(t, table + 6 + (glyph - start) * 2) : 0;
        }
        if (format == 2)
        {
            int count = BigEndian.U16(t, table + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int at = table + 4 + mid * 6;
                int start = BigEndian.U16(t, at), end = BigEndian.U16(t, at + 2);
                if (glyph < start) hi = mid - 1;
                else if (glyph > end) lo = mid + 1;
                else return BigEndian.U16(t, at + 4);
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ legacy 'kern'

    private static Dictionary<(int, int), short>? ReadLegacy(byte[] t)
    {
        if (t.Length < 4 || BigEndian.U16(t, 0) != 0) return null; // Apple's version 1 tables are not read
        int tables = BigEndian.U16(t, 2), at = 4;
        var pairs = new Dictionary<(int, int), short>();
        for (int i = 0; i < tables && at + 6 <= t.Length; i++)
        {
            int length = BigEndian.U16(t, at + 2), coverage = BigEndian.U16(t, at + 4);
            if ((coverage >> 8) == 0)
            {
                // Format 0: its length is known from the pair count (the 16-bit length field overflows in large tables).
                int count = BigEndian.U16(t, at + 6);
                length = 14 + count * 6;
                // Horizontal, not cross-stream and not minimum values.
                if ((coverage & 0x0007) != 0x0001) { at += length; continue; }
                for (int p = 0; p < count; p++)
                {
                    int rec = at + 14 + p * 6;
                    if (rec + 6 > t.Length) break;
                    pairs.TryAdd((BigEndian.U16(t, rec), BigEndian.U16(t, rec + 2)), BigEndian.S16(t, rec + 4));
                }
            }
            if (length < 6) break;
            at += length;
        }
        return pairs;
    }
}
