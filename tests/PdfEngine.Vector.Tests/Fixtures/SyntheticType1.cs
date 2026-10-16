using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// Builds small Type 1 font programs (Adobe Type 1 Font Format) for font fixtures: a clear-text
/// part with the FontMatrix and built-in encoding, then the eexec-encrypted Private dictionary
/// with Subrs and charstrings written from operands (ints) and operator names.
/// </summary>
internal static class SyntheticType1
{
    /// <summary>A program and the length of its clear-text part (/Length1).</summary>
    public sealed record Program(byte[] Data, int ClearLength);

    /// <summary>
    /// The default glyphs: "A" is a square built with a subroutine, "acute" a bar above it and
    /// "Aacute" a seac of the two. All are 1000 units wide.
    /// </summary>
    public static Program Default(string fontMatrix) => Build(fontMatrix, new[]
    {
        (".notdef", new object[] { 0, 500, "hsbw", "endchar" }),
        ("A", new object[] { 0, 1000, "hsbw", 100, 100, "rmoveto", 0, "callsubr", 0, 700, "rlineto", -800, 0, "rlineto", "closepath", "endchar" }),
        ("acute", new object[] { 0, 1000, "hsbw", 400, 850, "rmoveto", 200, 0, "rlineto", 0, 100, "rlineto", -200, 0, "rlineto", "closepath", "endchar" }),
        ("Aacute", new object[] { 0, 1000, "hsbw", 0, 0, 0, 65, 194, "seac" }),
    }, subrs: new[] { new object[] { 800, 0, "rlineto", "return" } });

    /// <param name="encoding">The clear-text /Encoding value: "StandardEncoding" or code → name pairs.</param>
    public static Program Build(string fontMatrix, IReadOnlyList<(string Name, object[] Ops)> glyphs,
        IReadOnlyList<object[]>? subrs = null, IReadOnlyDictionary<int, string>? encoding = null)
    {
        subrs ??= System.Array.Empty<object[]>();
        var priv = new MemoryStream();
        void W(string s) => priv.Write(Encoding.Latin1.GetBytes(s));
        W("dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n");
        W("/ND{noaccess def}executeonly def\n/NP{noaccess put}executeonly def\n/lenIV 4 def\n");
        W($"/Subrs {subrs.Count} array\n");
        for (int i = 0; i < subrs.Count; i++)
        {
            byte[] cs = EncryptCharString(CharString(subrs[i]));
            W($"dup {i} {cs.Length} RD "); priv.Write(cs); W(" NP\n");
        }
        W($"ND\n2 index /CharStrings {glyphs.Count} dict dup begin\n");
        foreach (var (name, ops) in glyphs)
        {
            byte[] cs = EncryptCharString(CharString(ops));
            W($"/{name} {cs.Length} RD "); priv.Write(cs); W(" ND\n");
        }
        W("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");

        string enc = encoding == null
            ? "/Encoding StandardEncoding def\n"
            : "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\n" +
              string.Concat(encoding.Select(e => $"dup {e.Key} /{e.Value} put\n")) + "readonly def\n";
        byte[] encrypted = Encrypt(priv.ToArray(), 55665);
        string clear = "%!PS-AdobeFont-1.0: TestType1 001.000\n12 dict begin\n/FontInfo 2 dict dup begin /FullName (Test Type 1) def end readonly def\n" +
                       "/FontName /TestType1 def\n/PaintType 0 def\n/FontType 1 def\n" +
                       $"/FontMatrix [{fontMatrix}] readonly def\n/FontBBox {{0 -200 1000 1000}} readonly def\n" +
                       enc + "currentdict end\ncurrentfile eexec\n";
        byte[] clearBytes = Encoding.Latin1.GetBytes(clear);
        return new Program(clearBytes.Concat(encrypted).ToArray(), clearBytes.Length);
    }

    private static readonly Dictionary<string, byte[]> Ops = new()
    {
        ["rlineto"] = new byte[] { 5 }, ["closepath"] = new byte[] { 9 }, ["callsubr"] = new byte[] { 10 },
        ["return"] = new byte[] { 11 }, ["hsbw"] = new byte[] { 13 }, ["endchar"] = new byte[] { 14 },
        ["rmoveto"] = new byte[] { 21 }, ["seac"] = new byte[] { 12, 6 }, ["sbw"] = new byte[] { 12, 7 },
    };

    /// <summary>Type 1 charstring from ints (operands) and names (operators).</summary>
    private static byte[] CharString(object[] ops)
    {
        var ms = new MemoryStream();
        foreach (var o in ops)
            ms.Write(o is string name ? Ops[name] : Number((int)o));
        return ms.ToArray();
    }

    private static byte[] Number(int v)
    {
        if (v is >= -107 and <= 107) return new[] { (byte)(v + 139) };
        if (v is >= 108 and <= 1131) { v -= 108; return new[] { (byte)((v >> 8) + 247), (byte)v }; }
        if (v is >= -1131 and <= -108) { v = -v - 108; return new[] { (byte)((v >> 8) + 251), (byte)v }; }
        return new byte[] { 255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static byte[] EncryptCharString(byte[] plain) => Encrypt(plain, 4330);

    /// <summary>Type 1 encryption with four leading random-ish bytes (lenIV 4 / eexec prefix).</summary>
    private static byte[] Encrypt(byte[] plain, ushort key)
    {
        const ushort c1 = 52845, c2 = 22719;
        ushort r = key;
        var input = new byte[] { 0x11, 0x22, 0x33, 0x44 }.Concat(plain).ToArray();
        var output = new byte[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            byte cipher = (byte)(input[i] ^ (r >> 8));
            r = (ushort)((cipher + r) * c1 + c2);
            output[i] = cipher;
        }
        return output;
    }
}
