using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// Builds small bare CFF programs (Adobe TN #5176) for font fixtures: name-keyed or CID-keyed,
/// with an optional Top DICT FontMatrix and, for CID-keyed fonts, an FDArray whose Font DICTs
/// can carry their own FontMatrix and local subroutines. Offsets are 5-byte integers, so the
/// layout is computed once with placeholders and written again with the real values.
/// </summary>
internal static class SyntheticCff
{
    /// <summary>One Font DICT of a CID-keyed font: its FontMatrix (null: none) and local subrs.</summary>
    public sealed record FontDict(double[]? FontMatrix, IReadOnlyList<byte[]>? Subrs = null);

    /// <summary>A CID-keyed glyph: its CID, the Font DICT it uses, and its Type 2 charstring.</summary>
    public sealed record CidGlyph(int Cid, int Fd, byte[] CharString);

    /// <summary>A name-keyed font with an optional Top DICT FontMatrix (null: no entry at all).</summary>
    public static byte[] NameKeyed(double[]? topMatrix, IReadOnlyList<string> names, IReadOnlyList<byte[]> charStrings, IReadOnlyList<byte[]>? subrs = null)
    {
        // Glyph names: "A".."Z" are standard strings (SID 34 + letter); anything else is custom.
        var custom = new List<string>();
        var sids = new List<int>();
        for (int g = 1; g < names.Count; g++)
        {
            string n = names[g];
            if (n.Length == 1 && n[0] is >= 'A' and <= 'Z') sids.Add(34 + n[0] - 'A');
            else { sids.Add(391 + custom.Count); custom.Add(n); }
        }
        var charset = new MemoryStream();
        charset.WriteByte(0);
        foreach (int sid in sids) W16(charset, sid);
        byte[] priv = PrivateDict(subrs);
        return Assemble(custom, charset.ToArray(), fdSelect: null, charStrings, offsets => Dict(w =>
        {
            if (topMatrix != null) { foreach (double v in topMatrix) w.Real(v); w.Op(12, 7); }
            w.Int(0); w.Int(-200); w.Int(1000); w.Int(1000); w.Op(5);
            w.Int5(offsets.Charset); w.Op(15);
            w.Int5(offsets.CharStrings); w.Op(17);
            w.Int5(priv.Length); w.Int5(offsets.Privates[0]); w.Op(18);
        }), fdArray: null, new[] { priv }, new[] { subrs });
    }

    /// <summary>A CID-keyed font (ROS Adobe-Identity-0) with an FDArray, a format 0 FDSelect and optional global subroutines.</summary>
    public static byte[] CidKeyed(double[]? topMatrix, IReadOnlyList<FontDict> fds, IReadOnlyList<CidGlyph> glyphs, IReadOnlyList<byte[]>? globalSubrs = null)
    {
        var custom = new List<string> { "Adobe", "Identity" }; // SIDs 391 and 392
        var charStrings = new List<byte[]> { new byte[] { 14 } };
        var charset = new MemoryStream();
        charset.WriteByte(0);
        var fdSelect = new MemoryStream();
        fdSelect.WriteByte(0);
        fdSelect.WriteByte(0); // .notdef uses FD 0
        foreach (var g in glyphs)
        {
            W16(charset, g.Cid);
            fdSelect.WriteByte((byte)g.Fd);
            charStrings.Add(g.CharString);
        }
        var privs = new List<byte[]>();
        foreach (var fd in fds) privs.Add(PrivateDict(fd.Subrs));
        int maxCid = 0;
        foreach (var g in glyphs) maxCid = Math.Max(maxCid, g.Cid);

        return Assemble(custom, charset.ToArray(), fdSelect.ToArray(), charStrings, offsets => Dict(w =>
        {
            w.Int(391); w.Int(392); w.Int(0); w.Op(12, 30); // ROS comes first
            if (topMatrix != null) { foreach (double v in topMatrix) w.Real(v); w.Op(12, 7); }
            w.Int(0); w.Int(-200); w.Int(1000); w.Int(1000); w.Op(5);
            w.Int(maxCid + 1); w.Op(12, 34); // CIDCount
            w.Int5(offsets.Charset); w.Op(15);
            w.Int5(offsets.CharStrings); w.Op(17);
            w.Int5(offsets.FdArray); w.Op(12, 36);
            w.Int5(offsets.FdSelect); w.Op(12, 37);
        }), offsets =>
        {
            var dicts = new List<byte[]>();
            for (int i = 0; i < fds.Count; i++)
            {
                int k = i;
                dicts.Add(Dict(w =>
                {
                    if (fds[k].FontMatrix is { } m) { foreach (double v in m) w.Real(v); w.Op(12, 7); }
                    w.Int5(privs[k].Length); w.Int5(offsets.Privates[k]); w.Op(18);
                }));
            }
            return dicts;
        }, privs, ListOf(fds), globalSubrs);
    }

    private static IReadOnlyList<byte[]>?[] ListOf(IReadOnlyList<FontDict> fds)
    {
        var r = new IReadOnlyList<byte[]>?[fds.Count];
        for (int i = 0; i < fds.Count; i++) r[i] = fds[i].Subrs;
        return r;
    }

    private sealed record Offsets(int Charset, int FdSelect, int CharStrings, int FdArray, int[] Privates);

    private static byte[] Assemble(List<string> strings, byte[] charset, byte[]? fdSelect, IReadOnlyList<byte[]> charStrings,
        Func<Offsets, byte[]> top, Func<Offsets, List<byte[]>>? fdArray, IReadOnlyList<byte[]> privates, IReadOnlyList<IReadOnlyList<byte[]>?> subrs,
        IReadOnlyList<byte[]>? globalSubrs = null)
    {
        byte[] nameIndex = Index(new[] { Encoding.ASCII.GetBytes("Synthetic") });
        byte[] stringIndex = Index(strings.ConvertAll(s => Encoding.ASCII.GetBytes(s)));
        byte[] gsubrs = Index(globalSubrs ?? Array.Empty<byte[]>());
        byte[] csIndex = Index(charStrings);
        var subrIndexes = new List<byte[]>();
        foreach (var s in subrs) subrIndexes.Add(s is { Count: > 0 } ? Index(s) : Array.Empty<byte>());

        Offsets Layout(Offsets probe)
        {
            int p = 4 + nameIndex.Length + Index(new[] { top(probe) }).Length + stringIndex.Length + gsubrs.Length;
            int cs = p; p += charset.Length;
            int sel = fdSelect != null ? p : 0; p += fdSelect?.Length ?? 0;
            int chars = p; p += csIndex.Length;
            int fda = fdArray != null ? p : 0;
            if (fdArray != null) p += Index(fdArray(probe)).Length;
            var privs = new int[privates.Count];
            for (int i = 0; i < privates.Count; i++) { privs[i] = p; p += privates[i].Length + subrIndexes[i].Length; }
            return new Offsets(cs, sel, chars, fda, privs);
        }
        var offsets = Layout(new Offsets(0, 0, 0, 0, new int[privates.Count]));

        var ms = new MemoryStream();
        ms.Write(new byte[] { 1, 0, 4, 4 });
        ms.Write(nameIndex);
        ms.Write(Index(new[] { top(offsets) }));
        ms.Write(stringIndex);
        ms.Write(gsubrs);
        ms.Write(charset);
        if (fdSelect != null) ms.Write(fdSelect);
        ms.Write(csIndex);
        if (fdArray != null) ms.Write(Index(fdArray(offsets)));
        for (int i = 0; i < privates.Count; i++) { ms.Write(privates[i]); ms.Write(subrIndexes[i]); }
        return ms.ToArray();
    }

    /// <summary>Private DICT with zero default/nominal widths; Subrs (when present) follow it directly.</summary>
    private static byte[] PrivateDict(IReadOnlyList<byte[]>? subrs)
    {
        // Two passes: the Subrs offset is relative to the Private DICT, which it is part of.
        byte[] Build(int size) => Dict(w =>
        {
            w.Int(0); w.Op(20);
            w.Int(0); w.Op(21);
            if (subrs is { Count: > 0 }) { w.Int5(size); w.Op(19); }
        });
        return Build(Build(0).Length);
    }

    private static byte[] Index(IReadOnlyList<byte[]> items)
    {
        var ms = new MemoryStream();
        W16(ms, items.Count);
        if (items.Count == 0) return ms.ToArray();
        ms.WriteByte(4);
        uint off = 1;
        W32(ms, off);
        foreach (var it in items) { off += (uint)it.Length; W32(ms, off); }
        foreach (var it in items) ms.Write(it);
        return ms.ToArray();
    }

    private static void W16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    private static void W32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    private sealed class DictWriter
    {
        public readonly MemoryStream S = new();
        public void Op(int op) => S.WriteByte((byte)op);
        public void Op(int esc, int op) { S.WriteByte((byte)esc); S.WriteByte((byte)op); }
        public void Int5(int v) { S.WriteByte(29); W32(S, (uint)v); }
        public void Int(int v)
        {
            if (v is >= -107 and <= 107) S.WriteByte((byte)(v + 139));
            else { S.WriteByte(28); W16(S, v); }
        }
        public void Real(double v)
        {
            string s = v.ToString("0.###########", CultureInfo.InvariantCulture);
            var nibbles = new List<int>();
            foreach (char c in s)
                nibbles.Add(c switch { '.' => 0xA, '-' => 0xE, _ => c - '0' });
            nibbles.Add(0xF);
            if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
            S.WriteByte(30);
            for (int i = 0; i < nibbles.Count; i += 2) S.WriteByte((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }
    }

    private static byte[] Dict(Action<DictWriter> build)
    {
        var w = new DictWriter();
        build(w);
        return w.S.ToArray();
    }

    /// <summary>Writes Type 2 charstrings: numbers then operators, as the program reads them.</summary>
    public sealed class CharString
    {
        private readonly MemoryStream _s = new();

        public CharString N(params int[] values)
        {
            foreach (int v in values)
            {
                if (v is >= -107 and <= 107) _s.WriteByte((byte)(v + 139));
                else if (v is >= 108 and <= 1131) { int u = v - 108; _s.WriteByte((byte)((u >> 8) + 247)); _s.WriteByte((byte)u); }
                else if (v is >= -1131 and <= -108) { int u = -v - 108; _s.WriteByte((byte)((u >> 8) + 251)); _s.WriteByte((byte)u); }
                else { _s.WriteByte(28); W16(_s, v); }
            }
            return this;
        }

        public CharString Op(int op) { _s.WriteByte((byte)op); return this; }
        public CharString Raw(params byte[] bytes) { _s.Write(bytes); return this; }
        public byte[] ToArray() => _s.ToArray();
    }

    /// <summary>
    /// An L-shaped outline in units of <paramref name="u"/> per em, so a skew, a scale or a
    /// rotation shows in its edges. With <paramref name="hinted"/> it starts with stems and a
    /// hintmask; a <paramref name="width"/> is written as the charstring's leading width operand.
    /// </summary>
    public static byte[] LShape(int u, bool hinted = false, int? width = null)
    {
        var c = new CharString();
        if (width is { } w) c.N(w);
        if (hinted) c.N(0, u / 5, u / 2, u / 10).Op(18).N(u / 10, u / 5).Op(23).Op(19).Raw(0xE0); // 3 stems: one mask byte
        c.N(u / 10, 0).Op(21)
         .N(u * 6 / 10, 0).Op(5)
         .N(0, u / 5).Op(5)
         .N(-u * 4 / 10, 0).Op(5)
         .N(0, u / 2).Op(5)
         .N(-u / 5, 0).Op(5)
         .Op(14);
        return c.ToArray();
    }
}
