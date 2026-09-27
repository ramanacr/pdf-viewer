using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts.Programs;

namespace PdfEngine.Vector.PdfA;

/// <summary>
/// Edits of a TrueType program for PDF/A width harmonisation: copies of glyphs appended after the
/// last one (each with its own advance), and cmap subtables given other mappings. A copy draws
/// exactly what its original draws, so a code moved to it looks the same.
/// </summary>
internal static class TrueTypeGlyphCopies
{
    /// <summary>
    /// The font with a copy of each <c>Source</c> glyph appended (with advance <c>Advance</c>, in
    /// font units), and the glyph id of the first copy; the others follow in order. Null when the
    /// program cannot take them.
    /// </summary>
    public static (byte[] Font, int FirstCopy)? Append(byte[] font, IReadOnlyList<(int Source, int Advance)> copies)
    {
        var tables = TrueTypeFontFile.ReadTables(font, 0);
        if (tables == null) return null;
        foreach (var required in new[] { "head", "hhea", "hmtx", "maxp", "loca", "glyf" })
            if (!tables.ContainsKey(required)) return null;
        var head = (byte[])tables["head"].Clone();
        var maxp = (byte[])tables["maxp"].Clone();
        var glyf = tables["glyf"];
        if (head.Length < 54 || maxp.Length < 6) return null;
        int count = BigEndian.U16(maxp, 4);
        if (count == 0 || count + copies.Count > 65535 || copies.Any(c => c.Source < 0 || c.Source >= count)) return null;
        bool longLoca = BigEndian.S16(head, 50) == 1;
        var loca = tables["loca"];
        if (loca.Length < (count + 1) * (longLoca ? 4 : 2)) return null;
        var offsets = new long[count + 1];
        for (int g = 0; g <= count; g++)
            offsets[g] = longLoca ? BigEndian.U32(loca, g * 4) : BigEndian.U16(loca, g * 2) * 2L;

        // The glyph data: the original's, padded, then each copy's.
        using var data = new MemoryStream();
        long end = Math.Min(offsets[count], glyf.Length);
        data.Write(glyf, 0, (int)end);
        while ((data.Length & 3) != 0) data.WriteByte(0);
        var newOffsets = new List<long>(offsets.Take(count)) { data.Length };
        foreach (var (source, _) in copies)
        {
            long start = offsets[source], stop = offsets[source + 1];
            if (stop > start && stop <= glyf.Length) data.Write(glyf, (int)start, (int)(stop - start));
            while ((data.Length & 3) != 0) data.WriteByte(0);
            newOffsets.Add(data.Length);
        }
        int total = count + copies.Count;
        var newLoca = new byte[(total + 1) * 4];
        for (int g = 0; g <= total; g++) BigEndian.Put32(newLoca, g * 4, (uint)newOffsets[g]);
        BigEndian.Put16(head, 50, 1);
        BigEndian.Put16(maxp, 4, total);

        var result = new Dictionary<string, byte[]>(tables, StringComparer.Ordinal)
        {
            ["head"] = head, ["maxp"] = maxp, ["loca"] = newLoca, ["glyf"] = data.ToArray(),
        };
        var (hhea, hmtx) = Metrics(tables["hhea"], tables["hmtx"], count, copies.Select(c => (c.Source, (int?)c.Advance)).ToList());
        result["hhea"] = hhea;
        result["hmtx"] = hmtx;
        // Vertical metrics, when present, give each copy its original's.
        if (tables.TryGetValue("vhea", out var vhea) && tables.TryGetValue("vmtx", out var vmtx) && vhea.Length >= 36)
            (result["vhea"], result["vmtx"]) = Metrics(vhea, vmtx, count, copies.Select(c => (c.Source, (int?)null)).ToList());
        // Tables with one record per glyph that are only hints to rasterizers.
        result.Remove("hdmx");
        result.Remove("LTSH");
        if (tables.TryGetValue("post", out var post) && Post(post, count, copies.Select(c => c.Source).ToList()) is { } newPost) result["post"] = newPost;
        return (Sfnt.Write(Sfnt.TrueTypeVersion, result), count);
    }

    /// <summary>A metrics header and table (hhea/hmtx or vhea/vmtx) with one long entry per glyph, the copies' appended.</summary>
    private static (byte[] Header, byte[] Table) Metrics(byte[] header, byte[] table, int count, List<(int Source, int? Advance)> copies)
    {
        int metrics = Math.Clamp(BigEndian.U16(header, 34), 1, count);
        var advance = new int[count];
        var side = new short[count];
        int last = 0;
        for (int g = 0; g < count; g++)
        {
            if (g < metrics) { last = BigEndian.U16(table, g * 4); side[g] = BigEndian.S16(table, g * 4 + 2); }
            else side[g] = BigEndian.S16(table, metrics * 4 + (g - metrics) * 2);
            advance[g] = last;
        }
        int total = count + copies.Count;
        var result = new byte[total * 4];
        int max = 0;
        for (int g = 0; g < total; g++)
        {
            int a, s;
            if (g < count) { a = advance[g]; s = side[g]; }
            else { var (source, wanted) = copies[g - count]; a = Math.Clamp(wanted ?? advance[source], 0, 65535); s = side[source]; }
            max = Math.Max(max, a);
            BigEndian.Put16(result, g * 4, a);
            BigEndian.Put16(result, g * 4 + 2, s);
        }
        var newHeader = (byte[])header.Clone();
        BigEndian.Put16(newHeader, 34, total);
        BigEndian.Put16(newHeader, 10, max);
        return (newHeader, result);
    }

    /// <summary>A format 2 post table with a name for each copy (its original's); other formats become format 3 (no names).</summary>
    private static byte[]? Post(byte[] post, int count, List<int> sources)
    {
        if (post.Length < 32) return null;
        uint format = BigEndian.U32(post, 0);
        if (format == 0x00030000) return null;
        if (format == 0x00020000 && post.Length >= 34 && BigEndian.U16(post, 32) == count && post.Length >= 34 + count * 2)
        {
            using var ms = new MemoryStream();
            ms.Write(post, 0, 32);
            BigEndian.W16(ms, count + sources.Count);
            ms.Write(post, 34, count * 2);
            foreach (int source in sources) BigEndian.W16(ms, BigEndian.U16(post, 34 + source * 2));
            ms.Write(post, 34 + count * 2, post.Length - 34 - count * 2);
            return ms.ToArray();
        }
        var header = post.AsSpan(0, 32).ToArray();
        BigEndian.Put32(header, 0, 0x00030000);
        return header;
    }

    // ------------------------------------------------------------------ cmap

    /// <summary>
    /// The font with the given subtables' mappings changed (code to glyph id; a subtable the cmap
    /// lacks is added). Subtables not named are kept as they are. Null when the cmap cannot be read
    /// or a changed subtable cannot be written.
    /// </summary>
    public static byte[]? WithCmap(byte[] font, IReadOnlyDictionary<(int Platform, int Encoding), IReadOnlyDictionary<int, int>> changes)
    {
        var tables = TrueTypeFontFile.ReadTables(font, 0);
        if (tables == null || !tables.TryGetValue("cmap", out var cmap) || cmap.Length < 4) return null;
        int n = BigEndian.U16(cmap, 2);
        var subtables = new SortedDictionary<(int, int), byte[]>();
        for (int i = 0; i < n; i++)
        {
            int rec = 4 + i * 8;
            if (rec + 8 > cmap.Length) break;
            var key = (BigEndian.U16(cmap, rec), BigEndian.U16(cmap, rec + 2));
            int offset = (int)Math.Min(BigEndian.U32(cmap, rec + 4), int.MaxValue);
            if (subtables.ContainsKey(key) || SubtableLength(cmap, offset) is not int length) continue;
            subtables[key] = cmap.AsSpan(offset, length).ToArray();
        }
        foreach (var (key, entries) in changes)
        {
            var map = subtables.TryGetValue(key, out var existing) ? Mappings(existing) : new Dictionary<int, int>();
            if (map == null) return null;
            foreach (var (code, gid) in entries)
            {
                if (gid == 0) map.Remove(code);
                else map[code] = gid;
            }
            if (WriteSubtable(map, key.Platform == 1) is not { } written) return null;
            subtables[key] = written;
        }
        using var ms = new MemoryStream();
        BigEndian.W16(ms, 0);
        BigEndian.W16(ms, subtables.Count);
        long offsetOfData = 4 + subtables.Count * 8L;
        foreach (var ((platform, encoding), bytes) in subtables)
        {
            BigEndian.W16(ms, platform);
            BigEndian.W16(ms, encoding);
            BigEndian.W32(ms, (uint)offsetOfData);
            offsetOfData += bytes.Length + ((4 - bytes.Length % 4) % 4);
        }
        foreach (var bytes in subtables.Values)
        {
            ms.Write(bytes);
            while ((ms.Length & 3) != 0) ms.WriteByte(0);
        }
        var result = new Dictionary<string, byte[]>(tables, StringComparer.Ordinal) { ["cmap"] = ms.ToArray() };
        if (result.TryGetValue("head", out var head)) result["head"] = (byte[])head.Clone();
        return Sfnt.Write(Sfnt.TrueTypeVersion, result);
    }

    /// <summary>The (platform, encoding) pairs of the font's cmap subtables.</summary>
    public static List<(int Platform, int Encoding)> Subtables(byte[] font)
    {
        var result = new List<(int, int)>();
        if (TrueTypeFontFile.ReadTables(font, 0) is not { } tables || !tables.TryGetValue("cmap", out var cmap) || cmap.Length < 4) return result;
        int n = BigEndian.U16(cmap, 2);
        for (int i = 0; i < n && 4 + i * 8 + 8 <= cmap.Length; i++)
            result.Add((BigEndian.U16(cmap, 4 + i * 8), BigEndian.U16(cmap, 4 + i * 8 + 2)));
        return result;
    }

    private static int? SubtableLength(byte[] cmap, int offset)
    {
        if (offset < 0 || offset + 4 > cmap.Length) return null;
        int format = BigEndian.U16(cmap, offset);
        long length = format switch
        {
            0 or 2 or 4 or 6 => BigEndian.U16(cmap, offset + 2),
            8 or 10 or 12 or 13 => offset + 8 <= cmap.Length ? BigEndian.U32(cmap, offset + 4) : 0,
            14 => offset + 6 <= cmap.Length ? BigEndian.U32(cmap, offset + 2) : 0,
            _ => 0,
        };
        if (length <= 0) return null;
        return (int)Math.Min(length, cmap.Length - offset);
    }

    /// <summary>Every code a format 0, 4, 6 or 12 subtable maps, with its glyph; null for other formats.</summary>
    private static Dictionary<int, int>? Mappings(byte[] t)
    {
        var map = new Dictionary<int, int>();
        switch (BigEndian.U16(t, 0))
        {
            case 0:
                for (int c = 0; c < 256 && 6 + c < t.Length; c++) if (t[6 + c] != 0) map[c] = t[6 + c];
                return map;
            case 4:
            {
                int segments = BigEndian.U16(t, 6) / 2;
                int ends = 14, starts = ends + segments * 2 + 2, deltas = starts + segments * 2, ranges = deltas + segments * 2;
                for (int s = 0; s < segments; s++)
                {
                    int endCode = BigEndian.U16(t, ends + s * 2), startCode = BigEndian.U16(t, starts + s * 2);
                    int delta = BigEndian.U16(t, deltas + s * 2), range = BigEndian.U16(t, ranges + s * 2);
                    for (int c = startCode; c <= endCode && c <= 0xFFFF; c++)
                    {
                        int gid;
                        if (range == 0) gid = (c + delta) & 0xFFFF;
                        else
                        {
                            int at = ranges + s * 2 + range + 2 * (c - startCode);
                            if (at + 2 > t.Length) continue;
                            gid = BigEndian.U16(t, at);
                            if (gid != 0) gid = (gid + delta) & 0xFFFF;
                        }
                        if (gid != 0 && c != 0xFFFF) map[c] = gid;
                    }
                }
                return map;
            }
            case 6:
            {
                int first = BigEndian.U16(t, 6), entries = BigEndian.U16(t, 8);
                for (int i = 0; i < entries && 10 + i * 2 + 2 <= t.Length; i++)
                    if (BigEndian.U16(t, 10 + i * 2) is int g and not 0) map[first + i] = g;
                return map;
            }
            case 12:
            {
                long groups = Math.Min(BigEndian.U32(t, 12), (t.Length - 16) / 12);
                for (int i = 0; i < groups; i++)
                {
                    int g = 16 + i * 12;
                    uint start = BigEndian.U32(t, g), stop = BigEndian.U32(t, g + 4), gid = BigEndian.U32(t, g + 8);
                    if (stop < start || stop > 0x10FFFF || map.Count > 1_200_000) return null;
                    for (uint c = start; c <= stop; c++) if (gid + (c - start) is var v and > 0 and <= 0xFFFF) map[(int)c] = (int)v;
                }
                return map;
            }
            default:
                return null;
        }
    }

    /// <summary>A subtable for the mappings: format 6 for a Macintosh one that fits, 4 when every code is 16-bit, else 12.</summary>
    private static byte[]? WriteSubtable(Dictionary<int, int> map, bool macintosh)
    {
        var codes = map.Keys.OrderBy(c => c).ToList();
        if (macintosh && codes.All(c => c <= 0xFFFF))
        {
            int first = codes.Count > 0 ? codes[0] : 0, entries = codes.Count > 0 ? codes[^1] - first + 1 : 0;
            if (entries <= 1024)
            {
                using var ms6 = new MemoryStream();
                BigEndian.W16(ms6, 6);
                BigEndian.W16(ms6, 10 + entries * 2);
                BigEndian.W16(ms6, 0);
                BigEndian.W16(ms6, first);
                BigEndian.W16(ms6, entries);
                for (int c = first; c < first + entries; c++) BigEndian.W16(ms6, map.GetValueOrDefault(c));
                return ms6.ToArray();
            }
        }
        if (codes.All(c => c < 0xFFFF)) return Format4(codes, map);
        using var ms = new MemoryStream();
        var groups = new List<(int Start, int End, int Gid)>();
        foreach (int c in codes)
        {
            if (groups.Count > 0 && groups[^1].End == c - 1 && groups[^1].Gid + (c - groups[^1].Start) == map[c]) groups[^1] = groups[^1] with { End = c };
            else groups.Add((c, c, map[c]));
        }
        BigEndian.W16(ms, 12);
        BigEndian.W16(ms, 0);
        BigEndian.W32(ms, (uint)(16 + groups.Count * 12));
        BigEndian.W32(ms, 0);
        BigEndian.W32(ms, (uint)groups.Count);
        foreach (var (start, stop, gid) in groups) { BigEndian.W32(ms, (uint)start); BigEndian.W32(ms, (uint)stop); BigEndian.W32(ms, (uint)gid); }
        return ms.ToArray();
    }

    /// <summary>Format 4: one segment per run of consecutive codes, by delta when their glyphs are consecutive too, else by a glyph array.</summary>
    private static byte[]? Format4(List<int> codes, Dictionary<int, int> map)
    {
        var runs = new List<(int Start, int End)>();
        foreach (int c in codes)
        {
            if (runs.Count > 0 && runs[^1].End == c - 1) runs[^1] = runs[^1] with { End = c };
            else runs.Add((c, c));
        }
        runs.Add((0xFFFF, 0xFFFF));
        int segments = runs.Count;
        var byDelta = runs.Select(r => r.Start == 0xFFFF || Enumerable.Range(r.Start, r.End - r.Start + 1).All(c => map[c] - c == map[r.Start] - r.Start)).ToList();
        int arrayWords = runs.Where((r, i) => !byDelta[i]).Sum(r => r.End - r.Start + 1);
        int length = 16 + segments * 8 + arrayWords * 2;
        if (length > 65535) return null;
        using var ms = new MemoryStream();
        int search = 1, selector = 0;
        while (search * 2 <= segments) { search *= 2; selector++; }
        BigEndian.W16(ms, 4);
        BigEndian.W16(ms, length);
        BigEndian.W16(ms, 0);
        BigEndian.W16(ms, segments * 2);
        BigEndian.W16(ms, search * 2);
        BigEndian.W16(ms, selector);
        BigEndian.W16(ms, segments * 2 - search * 2);
        foreach (var r in runs) BigEndian.W16(ms, r.End);
        BigEndian.W16(ms, 0);
        foreach (var r in runs) BigEndian.W16(ms, r.Start);
        for (int i = 0; i < segments; i++)
            BigEndian.W16(ms, runs[i].Start == 0xFFFF ? 1 : byDelta[i] ? (map[runs[i].Start] - runs[i].Start) & 0xFFFF : 0);
        // Range offsets point from their own slot into the glyph array that follows them.
        int arrayAt = 0;
        for (int i = 0; i < segments; i++)
        {
            if (byDelta[i]) { BigEndian.W16(ms, 0); continue; }
            BigEndian.W16(ms, (segments - i) * 2 + arrayAt * 2);
            arrayAt += runs[i].End - runs[i].Start + 1;
        }
        for (int i = 0; i < segments; i++)
            if (!byDelta[i])
                for (int c = runs[i].Start; c <= runs[i].End; c++) BigEndian.W16(ms, map[c]);
        return ms.ToArray();
    }
}
