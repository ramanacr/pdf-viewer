using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfEngine.Vector.Fonts.Programs;

/// <summary>
/// A CFF font program (Adobe TN #5176) read as the parts a subset is made of: the charstrings,
/// the global subroutines, and each Font DICT with its Private DICT and local subroutines. A
/// subset is a CID-keyed CFF (for a FontFile3 /CIDFontType0C stream) with only the glyphs a
/// document uses, the Font DICTs they use, and the subroutines their charstrings call. In the
/// subset, the CID of a glyph is its glyph index in the original font, so a Type0 font with
/// Identity-H keeps CID = original glyph index; the charset maps each CID to its new index.
/// </summary>
internal sealed class CffSubsetter
{
    private readonly byte[] _d;
    private readonly List<CffFont.IndexEntry> _charStrings, _globalSubrs;
    private readonly FontDict[] _fds;
    private readonly byte[]? _fdSelect;
    private readonly double[]? _fontMatrix;
    private readonly double[] _fontBBox;

    /// <summary>A Font DICT: its FontMatrix (null when it has none), its Private DICT without the Subrs entry, and its local subroutines.</summary>
    private sealed record FontDict(double[]? Matrix, byte[] Private, List<CffFont.IndexEntry> Subrs);

    public int GlyphCount => _charStrings.Count;

    private CffSubsetter(byte[] d, List<CffFont.IndexEntry> charStrings, List<CffFont.IndexEntry> globalSubrs, FontDict[] fds, byte[]? fdSelect, double[]? fontMatrix, double[] fontBBox)
    {
        (_d, _charStrings, _globalSubrs, _fds, _fdSelect, _fontMatrix, _fontBBox) = (d, charStrings, globalSubrs, fds, fdSelect, fontMatrix, fontBBox);
    }

    /// <summary>Reads a bare CFF program (the 'CFF ' table of an OpenType font); null when it cannot be read.</summary>
    public static CffSubsetter? TryParse(byte[] data)
    {
        try
        {
            return Parse(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private static CffSubsetter? Parse(byte[] d)
    {
        if (d.Length < 4 || d[0] != 1) return null;
        int pos = d[2]; // hdrSize
        CffFont.ReadIndex(d, ref pos); // names
        var tops = CffFont.ReadIndex(d, ref pos);
        CffFont.ReadIndex(d, ref pos); // strings: the subset writes its own
        var globalSubrs = CffFont.ReadIndex(d, ref pos);
        if (tops.Count == 0) return null;
        var top = CffFont.ParseDict(d, tops[0].Offset, tops[0].Length);
        if (!top.TryGetValue(17, out var cs) || cs.Length < 1) return null;
        int p = (int)cs[0];
        var charStrings = CffFont.ReadIndex(d, ref p);
        if (charStrings.Count == 0) return null;
        double[]? matrix = top.TryGetValue(1207, out var fm) && fm.Length >= 6 ? fm[..6] : null;
        double[] bbox = top.TryGetValue(5, out var bb) && bb.Length >= 4 ? bb[..4] : new double[] { 0, -200, 1000, 800 };

        if (!top.ContainsKey(1230)) // name-keyed: one Private DICT
            return new CffSubsetter(d, charStrings, globalSubrs, new[] { ReadFontDict(d, top, null) }, null, matrix, bbox);

        if (!top.TryGetValue(1236, out var fa) || fa.Length < 1 || !top.TryGetValue(1237, out var fs) || fs.Length < 1) return null;
        int q = (int)fa[0];
        var dicts = CffFont.ReadIndex(d, ref q);
        if (dicts.Count is 0 or > 256) return null;
        var fds = new FontDict[dicts.Count];
        for (int i = 0; i < dicts.Count; i++)
        {
            var fd = CffFont.ParseDict(d, dicts[i].Offset, dicts[i].Length);
            fds[i] = ReadFontDict(d, fd, fd.TryGetValue(1207, out var m) && m.Length >= 6 ? m[..6] : null);
        }
        var select = ReadFdSelect(d, (int)fs[0], charStrings.Count, fds.Length);
        return select == null ? null : new CffSubsetter(d, charStrings, globalSubrs, fds, select, matrix, bbox);
    }

    private static FontDict ReadFontDict(byte[] d, Dictionary<int, double[]> dict, double[]? matrix)
    {
        if (!dict.TryGetValue(18, out var pv) || pv.Length < 2) return new FontDict(matrix, Array.Empty<byte>(), new());
        int size = (int)pv[0], offset = (int)pv[1];
        if (size < 0 || offset < 0 || (long)offset + size > d.Length) throw new InvalidDataException("CFF Private DICT");
        var (start, end) = CffFont.FindOperator(d, offset, size, 19);
        if (start < 0) return new FontDict(matrix, d.AsSpan(offset, size).ToArray(), new());
        // Subrs is an offset from the start of the Private DICT; the subset writes its own.
        int subrs = offset + (int)CffFont.ParseDict(d, offset, size)[19][0];
        var withoutSubrs = d.AsSpan(offset, start - offset).ToArray().Concat(d.AsSpan(end, offset + size - end).ToArray()).ToArray();
        return new FontDict(matrix, withoutSubrs, CffFont.ReadIndex(d, ref subrs));
    }

    private static byte[]? ReadFdSelect(byte[] d, int offset, int glyphs, int fdCount)
    {
        var select = new byte[glyphs];
        int q = offset, format = d[q++];
        if (format == 0)
        {
            for (int g = 0; g < glyphs; g++) select[g] = d[q + g];
        }
        else if (format == 3)
        {
            int ranges = BigEndian.U16(d, q);
            q += 2;
            for (int r = 0; r < ranges; r++, q += 3)
            {
                int first = BigEndian.U16(d, q), next = BigEndian.U16(d, q + 3);
                for (int g = first; g < next && g < glyphs; g++) select[g] = d[q + 2];
            }
        }
        else return null;
        foreach (byte fd in select)
            if (fd >= fdCount) return null;
        return select;
    }

    private int FdOf(int gid) => _fdSelect != null && gid < _fdSelect.Length ? _fdSelect[gid] : 0;

    /// <summary>The glyph draws something (its charstring, with the subroutines it calls, has a moveto).</summary>
    public bool HasOutline(int gid)
    {
        if (gid <= 0 || gid >= GlyphCount) return false;
        var scan = new Scanner(this, FdOf(gid), new HashSet<int>(), new HashSet<int>());
        scan.Run(_charStrings[gid], 0);
        return !scan.Seac && (scan.Drew || scan.Failed);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _seac = new();

    /// <summary>
    /// The glyph is an accented character built from two standard-encoding glyphs (endchar with
    /// the seac operands, name-keyed fonts only). A CID-keyed subset cannot express that, so such a
    /// glyph is not used: <see cref="Editing.TrueTypeFontFile.GlyphFor"/> gives 0 and the next font is tried.
    /// </summary>
    public bool UsesSeac(int gid)
    {
        if (_fdSelect != null || gid <= 0 || gid >= GlyphCount) return false;
        return _seac.GetOrAdd(gid, g =>
        {
            var scan = new Scanner(this, 0, new HashSet<int>(), new HashSet<int>());
            scan.Run(_charStrings[g], 0);
            return scan.Seac;
        });
    }

    /// <summary>
    /// A CID-keyed CFF with glyph 0 and <paramref name="glyphs"/>, each glyph's CID its index in
    /// this font. Subroutines no kept charstring calls are replaced by an empty one, so the
    /// subroutine numbers in the charstrings stay valid; a charstring that cannot be followed
    /// (the old arithmetic operators) keeps every subroutine.
    /// </summary>
    public byte[] Subset(IEnumerable<int> glyphs, string fontName)
    {
        var order = new List<int> { 0 };
        order.AddRange(glyphs.Where(g => g > 0 && g < GlyphCount).Distinct().OrderBy(g => g));

        var usedGlobal = new HashSet<int>();
        var usedLocal = _fds.Select(_ => new HashSet<int>()).ToArray();
        bool keepAll = false;
        foreach (int g in order)
        {
            var scan = new Scanner(this, FdOf(g), usedGlobal, usedLocal[FdOf(g)]);
            scan.Run(_charStrings[g], 0);
            keepAll |= scan.Failed;
        }

        var fdIndex = new Dictionary<int, int>();
        var fds = new List<(double[]?, byte[], IReadOnlyList<byte[]>)>();
        foreach (int g in order)
        {
            int fd = FdOf(g);
            if (fdIndex.ContainsKey(fd)) continue;
            fdIndex[fd] = fds.Count;
            var f = _fds[fd];
            fds.Add((f.Matrix, f.Private, Kept(f.Subrs, usedLocal[fd], keepAll)));
        }
        return WriteCid(fontName, _fontMatrix, _fontBBox, GlyphCount, order,
            order.Select(g => Bytes(_charStrings[g])).ToList(), order.Select(g => fdIndex[FdOf(g)]).ToList(), fds, Kept(_globalSubrs, usedGlobal, keepAll));
    }

    private static readonly byte[] Return = { 11 };

    private List<byte[]> Kept(List<CffFont.IndexEntry> subrs, HashSet<int> used, bool keepAll) =>
        subrs.Select((e, i) => keepAll || used.Contains(i) ? Bytes(e) : Return).ToList();

    private byte[] Bytes(CffFont.IndexEntry e) => _d.AsSpan(e.Offset, e.Length).ToArray();

    /// <summary>Subroutine number bias (Type 2 charstrings, 4.7).</summary>
    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    /// <summary>
    /// Follows a Type 2 charstring (Adobe TN #5177) through its subroutine calls, keeping the
    /// operand stack (subroutine numbers are operands) and the stem count (hintmask and cntrmask
    /// are followed by one bit per stem). Records the subroutines called and whether a path starts.
    /// </summary>
    private sealed class Scanner
    {
        private readonly CffSubsetter _font;
        private readonly List<CffFont.IndexEntry> _local;
        private readonly HashSet<int> _usedGlobal, _usedLocal;
        private readonly double[] _stack = new double[48];
        private int _sp, _stems;

        public bool Drew { get; private set; }
        /// <summary>The glyph ends with the seac form of endchar (an accented character made of two glyphs).</summary>
        public bool Seac { get; private set; }
        /// <summary>An operator the scan does not follow (arithmetic and storage), or a bad subroutine number.</summary>
        public bool Failed { get; private set; }

        public Scanner(CffSubsetter font, int fd, HashSet<int> usedGlobal, HashSet<int> usedLocal)
        {
            _font = font;
            _local = font._fds[fd].Subrs;
            (_usedGlobal, _usedLocal) = (usedGlobal, usedLocal);
        }

        /// <summary>Returns true when the glyph ended (endchar) or the scan stopped.</summary>
        public bool Run(CffFont.IndexEntry cs, int depth)
        {
            var d = _font._d;
            int p = cs.Offset, end = cs.Offset + cs.Length;
            while (p < end)
            {
                int b0 = d[p];
                if (b0 >= 32 || b0 == 28)
                {
                    double v;
                    if (b0 == 28) { v = (short)(d[p + 1] << 8 | d[p + 2]); p += 3; }
                    else if (b0 <= 246) { v = b0 - 139; p++; }
                    else if (b0 <= 250) { v = (b0 - 247) * 256 + d[p + 1] + 108; p += 2; }
                    else if (b0 <= 254) { v = -(b0 - 251) * 256 - d[p + 1] - 108; p += 2; }
                    else { v = (d[p + 1] << 24 | d[p + 2] << 16 | d[p + 3] << 8 | d[p + 4]) / 65536.0; p += 5; }
                    if (_sp < _stack.Length) _stack[_sp++] = v;
                    continue;
                }
                p++;
                switch (b0)
                {
                    case 1 or 3 or 18 or 23: // hstem, vstem, hstemhm, vstemhm
                        _stems += _sp / 2;
                        _sp = 0;
                        break;
                    case 19 or 20: // hintmask, cntrmask: a vstem may come before them on the stack
                        _stems += _sp / 2;
                        _sp = 0;
                        p += (_stems + 7) / 8;
                        break;
                    case 10 or 29: // callsubr, callgsubr
                    {
                        var list = b0 == 10 ? _local : _font._globalSubrs;
                        if (_sp == 0 || depth >= 10) return Stop();
                        int index = (int)_stack[--_sp] + Bias(list.Count);
                        if (index < 0 || index >= list.Count) return Stop();
                        (b0 == 10 ? _usedLocal : _usedGlobal).Add(index);
                        if (Run(list[index], depth + 1)) return true;
                        break;
                    }
                    case 11: // return
                        return false;
                    case 14: // endchar; with 4 operands (5 with a width) it is seac: adx ady bchar achar
                        Seac = _sp >= 4;
                        return true;
                    case 21 or 22 or 4: // rmoveto, hmoveto, vmoveto
                        Drew = true;
                        _sp = 0;
                        break;
                    case 12:
                    {
                        int b1 = p < end ? d[p++] : 0;
                        // 3..30 are the arithmetic and storage operators: their results could be subroutine numbers.
                        if (b1 is >= 3 and <= 30) return Stop();
                        _sp = 0;
                        break;
                    }
                    default:
                        _sp = 0;
                        break;
                }
            }
            return false;
        }

        private bool Stop()
        {
            Failed = true;
            return true;
        }
    }

    /// <summary>
    /// Writes a CID-keyed CFF (registry Adobe, ordering Identity). <paramref name="cids"/>[i] is the
    /// CID of glyph i (glyph 0 must have CID 0, the others in increasing order) and
    /// <paramref name="fdSelect"/>[i] its Font DICT; each Font DICT's Private DICT comes without its
    /// Subrs entry, which is written to point at its local subroutines.
    /// </summary>
    internal static byte[] WriteCid(string fontName, double[]? fontMatrix, double[] fontBBox, int cidCount, IReadOnlyList<int> cids,
        IReadOnlyList<byte[]> charStrings, IReadOnlyList<int> fdSelect, IReadOnlyList<(double[]? Matrix, byte[] Private, IReadOnlyList<byte[]> Subrs)> fds,
        IReadOnlyList<byte[]> globalSubrs)
    {
        int sidAdobe = CffStandardStrings.Names.Length, sidIdentity = sidAdobe + 1;
        byte[] nameIndex = CffWriter.Index(new[] { Encoding.Latin1.GetBytes(fontName) });
        byte[] stringIndex = CffWriter.Index(new[] { "Adobe"u8.ToArray(), "Identity"u8.ToArray() });
        byte[] gsubrIndex = CffWriter.Index(globalSubrs);

        using var charset = new MemoryStream();
        charset.WriteByte(0);
        for (int g = 1; g < cids.Count; g++) BigEndian.W16(charset, cids[g]);
        byte[] charsetBytes = charset.ToArray();
        byte[] fdSelectBytes = new byte[1 + fdSelect.Count]; // format 0
        for (int g = 0; g < fdSelect.Count; g++) fdSelectBytes[1 + g] = (byte)fdSelect[g];
        byte[] charStringsIndex = CffWriter.Index(charStrings);

        // Each Private DICT is followed by its local subroutines: Subrs = the DICT's own length.
        var privates = new List<(byte[] Dict, byte[] Subrs)>();
        foreach (var (_, priv, subrs) in fds)
        {
            if (subrs.Count == 0) { privates.Add((priv, Array.Empty<byte>())); continue; }
            byte[] dict = priv.Concat(CffWriter.Dict(w => { w.Int5(priv.Length + 6); w.Op(19); })).ToArray();
            privates.Add((dict, CffWriter.Index(subrs)));
        }

        byte[] FdArray(int privateStart)
        {
            var dicts = new List<byte[]>();
            int at = privateStart;
            for (int i = 0; i < fds.Count; i++)
            {
                var matrix = fds[i].Matrix;
                int size = privates[i].Dict.Length, offset = at;
                dicts.Add(CffWriter.Dict(w =>
                {
                    if (matrix != null) { foreach (double v in matrix) w.RealExact(v); w.Op(12, 7); }
                    w.Int5(size); w.Int5(offset); w.Op(18);
                }));
                at += size + privates[i].Subrs.Length;
            }
            return CffWriter.Index(dicts);
        }

        // Offsets are 5-byte integers, so the Top DICT and the FDArray have the same size whatever they hold.
        byte[] Top(int charsetOff, int fdSelectOff, int charStringsOff, int fdArrayOff) => CffWriter.Dict(w =>
        {
            w.Int(sidAdobe); w.Int(sidIdentity); w.Int(0); w.Op(12, 30); // ROS comes first
            if (fontMatrix != null) { foreach (double v in fontMatrix) w.RealExact(v); w.Op(12, 7); }
            foreach (double v in fontBBox) w.Int((int)Math.Round(v));
            w.Op(5);
            w.Int5(cidCount); w.Op(12, 34);
            w.Int5(charsetOff); w.Op(15);
            w.Int5(fdSelectOff); w.Op(12, 37);
            w.Int5(charStringsOff); w.Op(17);
            w.Int5(fdArrayOff); w.Op(12, 36);
        });

        int topSize = CffWriter.Index(new[] { Top(0, 0, 0, 0) }).Length;
        int charsetOff = 4 + nameIndex.Length + topSize + stringIndex.Length + gsubrIndex.Length;
        int fdSelectOff = charsetOff + charsetBytes.Length;
        int charStringsOff = fdSelectOff + fdSelectBytes.Length;
        int fdArrayOff = charStringsOff + charStringsIndex.Length;
        int privateStart = fdArrayOff + FdArray(0).Length;

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 1, 0, 4, 4 });
        ms.Write(nameIndex);
        ms.Write(CffWriter.Index(new[] { Top(charsetOff, fdSelectOff, charStringsOff, fdArrayOff) }));
        ms.Write(stringIndex);
        ms.Write(gsubrIndex);
        ms.Write(charsetBytes);
        ms.Write(fdSelectBytes);
        ms.Write(charStringsIndex);
        ms.Write(FdArray(privateStart));
        foreach (var (dict, subrs) in privates) { ms.Write(dict); ms.Write(subrs); }
        return ms.ToArray();
    }
}
