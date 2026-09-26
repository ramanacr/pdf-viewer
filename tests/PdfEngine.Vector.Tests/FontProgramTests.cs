using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Embedded font programs are normalized into sfnts the Windows backend loads (TrueType repair,
/// CFF → OpenType, Type 1 → CFF → OpenType) and must render like PDFium does.
/// </summary>
public class FontProgramTests
{
    private readonly ITestOutputHelper _output;
    public FontProgramTests(ITestOutputHelper output) => _output = output;

    private static string SystemFont(string file) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);

    // ------------------------------------------------------------------ sfnt writer

    [Fact]
    public void SfntRewrite_OfASystemFont_StillLoads()
    {
        byte[] arial = File.ReadAllBytes(SystemFont("arial.ttf"));
        var tables = Sfnt.ReadTables(arial, out uint version)!;
        tables["name"] = Sfnt.Name("RewriteTest", "RewriteTest", false, false);
        tables["cmap"] = Sfnt.PrivateUseCmap(BigEndian.U16(tables["maxp"], 4));
        byte[] rewritten = Sfnt.Write(version, tables);
        Assert.True(TryLoad(rewritten, out int glyphs), "rewritten sfnt rejected by WPF");
        Assert.Equal(BigEndian.U16(tables["maxp"], 4), glyphs);
    }

    [Fact]
    public void Cff_StandardStrings_AreComplete()
    {
        Assert.Equal(391, CffStandardStrings.Names.Length);
        Assert.Equal("exclamsmall", CffStandardStrings.Names[229]);
        Assert.Equal("001.000", CffStandardStrings.Names[379]);
        Assert.Equal("Semibold", CffStandardStrings.Names[390]);
        Assert.Equal("A", CffStandardStrings.Names[CffStandardStrings.StandardEncoding['A']]);
        Assert.Equal("quoteright", CffStandardStrings.Names[CffStandardStrings.StandardEncoding['\'']]);
        Assert.Equal("acute", CffStandardStrings.Names[CffStandardStrings.StandardEncoding[194]]);
    }

    // ------------------------------------------------------------------ Type 1

    [Fact]
    public void Type1_ConvertsSubrsSeacAndSkewedMatrix()
    {
        byte[] program = SyntheticType1(fontMatrix: "0.001 0 0.0002 0.001 0 0");
        var result = Type1Converter.TryConvert(program);
        Assert.NotNull(result);
        var cff = CffFont.TryParse(result!.Cff);
        Assert.NotNull(cff);
        Assert.Equal(new[] { 0.001, 0, 0, 0.001, 0, 0 }, cff!.FontMatrix); // skew baked into outlines
        Assert.True(cff.GidForName("A") > 0);
        Assert.True(cff.GidForName("Aacute") > 0);
        Assert.Equal("A", result.BuiltInEncoding['A']);
    }

    [Fact]
    public async Task Type1Font_RendersLikePdfium()
    {
        byte[] program = SyntheticType1(fontMatrix: "0.001 0 0 0.001 0 0");
        var pdf = FontPdf(program, "FontFile", extraStreamDict: $"/Length1 {Type1ClearLength} /Length2 {program.Length - Type1ClearLength} /Length3 0",
            subtype: "Type1", baseFont: "TestType1", text: "(AB)");
        await AssertRendersLikePdfium(pdf, "type1");
    }

    [Fact]
    public async Task BareCffFont_RendersLikePdfium()
    {
        byte[] cff = Type1Converter.TryConvert(SyntheticType1(fontMatrix: "0.001 0 0 0.001 0 0"))!.Cff;
        var pdf = FontPdf(cff, "FontFile3", extraStreamDict: "/Subtype /Type1C", subtype: "Type1", baseFont: "TestType1", text: "(AB)");
        await AssertRendersLikePdfium(pdf, "type1c");
    }

    [Fact]
    public async Task TrueTypeSubsetWithoutBookkeepingTables_RendersLikePdfium()
    {
        // Strip what subsetters commonly drop; the preparer must restore them.
        byte[] arial = File.ReadAllBytes(SystemFont("arial.ttf"));
        var tables = Sfnt.ReadTables(arial, out uint version)!;
        foreach (var tag in new[] { "name", "OS/2", "post", "GSUB", "GPOS", "GDEF", "kern", "DSIG", "hdmx", "VDMX", "LTSH", "gasp" })
            tables.Remove(tag);
        byte[] stripped = Sfnt.Write(version, tables);
        var pdf = FontPdf(stripped, "FontFile2", extraStreamDict: $"/Length1 {stripped.Length}", subtype: "TrueType", baseFont: "ABCDEF+ArialMT",
            text: "(Hello)", encoding: "/WinAnsiEncoding", nonSymbolic: true);
        await AssertRendersLikePdfium(pdf, "truetype", maxMean: 3.0);
    }

    [Fact]
    public async Task SeveralEmbeddedFontsOnOnePage_AllLoad()
    {
        // Regression: WPF's font cache failed for every font file after the first in one folder,
        // so only the first embedded font per process rendered with its own glyphs.
        var b = new VectorPdfBuilder();
        var refs = new List<string>();
        int i = 0;
        foreach (var file in new[] { "arial.ttf", "times.ttf", "cour.ttf" })
        {
            byte[] data = File.ReadAllBytes(SystemFont(file));
            int stream = b.AddStream($"/Length1 {data.Length}", data, flate: true);
            int desc = b.Add($"<< /Type /FontDescriptor /FontName /F{i} /Flags 32 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {stream} 0 R >>");
            int font = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /F{i} /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
            refs.Add($"/F{i} {font} 0 R");
            i++;
        }
        b.AddPage("BT /F0 20 Tf 10 150 Td (Arial) Tj /F1 20 Tf 0 -40 Td (Times) Tj /F2 20 Tf 0 -40 Td (Courier) Tj ET",
            $"<< /Font << {string.Join(" ", refs)} >> >>");

        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new WindowsVectorRenderer();
        var tokens = await renderer.AnalyzeAsync(list);
        Assert.Empty(tokens);
        Assert.All(list.Commands.OfType<DrawGlyphRun>(), r => Assert.Equal(PdfFontProgramFormat.TrueType, r.Run.Face!.Format));
    }

    // ------------------------------------------------------------------ helpers

    private async Task AssertRendersLikePdfium(byte[] pdf, string label, double maxMean = 2.0)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new WindowsVectorRenderer();
        var tokens = await renderer.AnalyzeAsync(list);
        Assert.True(tokens.Count == 0, $"{label}: {string.Join("; ", tokens.Select(t => t.Description))}");
        var run = Assert.Single(list.Commands.OfType<DrawGlyphRun>()).Run;
        Assert.NotEqual(PdfFontProgramFormat.None, run.Face!.Format);

        using var vector = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 });
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });
        var (mean, bad) = DifferentialRenderingTests.Compare(vector, reference);
        _output.WriteLine($"{label}: mean={mean:F2} bad={bad:P2}");

        // Guard against a vacuous pass: the glyphs must actually be drawn.
        int dark = 0;
        var span = vector.Pixels.Span;
        for (int p = 0; p < span.Length; p += 4) if (span[p] < 128) dark++;
        Assert.True(dark > 200, $"{label}: no glyph pixels drawn");
        Assert.True(mean <= maxMean, $"{label}: mean diff {mean:F2}");
        Assert.True(bad <= 0.02, $"{label}: {bad:P2} pixels off");
    }

    private static byte[] FontPdf(byte[] program, string fontFileKey, string extraStreamDict, string subtype, string baseFont, string text,
        string? encoding = null, bool nonSymbolic = false)
    {
        var b = new VectorPdfBuilder();
        int stream = b.AddStream(extraStreamDict, program);
        int flags = nonSymbolic ? 32 : 4;
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /{baseFont} /Flags {flags} /FontBBox [0 -200 1000 1000] /ItalicAngle 0 " +
                         $"/Ascent 900 /Descent -200 /CapHeight 800 /StemV 80 /{fontFileKey} {stream} 0 R >>");
        string enc = encoding ?? "<< /Type /Encoding /Differences [65 /A /Aacute] >>";
        string widths = nonSymbolic ? "" : "/FirstChar 65 /LastChar 66 /Widths [1000 1000]";
        int font = b.Add($"<< /Type /Font /Subtype /{subtype} /BaseFont /{baseFont} {widths} /Encoding {enc} /FontDescriptor {desc} 0 R >>");
        b.AddPage($"BT /F1 60 Tf 20 60 Td {text} Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 300 200]");
        return b.Build();
    }

    private static bool TryLoad(byte[] sfnt, out int glyphs)
    {
        glyphs = 0;
        string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fonttest-" + Guid.NewGuid().ToString("N"))).FullName;
        string file = Path.Combine(dir, "font.ttf");
        File.WriteAllBytes(file, sfnt);
        try
        {
            glyphs = new GlyphTypeface(new Uri(file)).GlyphCount;
            return true;
        }
        catch (Exception ex) when (ex is NullReferenceException or FileFormatException or ArgumentException)
        {
            return false;
        }
    }

    // ---- synthetic Type 1 font: "A" is a square built with a subroutine; "Aacute" is a seac of A + acute.

    private static int Type1ClearLength;

    private static byte[] SyntheticType1(string fontMatrix)
    {
        byte[] Cs(params object[] ops) => EncryptCharString(Type1Ops(ops));
        var notdef = Cs(0, 500, "hsbw", "endchar");
        var subr0 = Cs(800, 0, "rlineto", "return");
        var glyphA = Cs(0, 1000, "hsbw", 100, 100, "rmoveto", 0, "callsubr", 0, 700, "rlineto", -800, 0, "rlineto", "closepath", "endchar");
        var acute = Cs(0, 1000, "hsbw", 400, 850, "rmoveto", 200, 0, "rlineto", 0, 100, "rlineto", -200, 0, "rlineto", "closepath", "endchar");
        var aacute = Cs(0, 1000, "hsbw", 0, 0, 0, 65, 194, "seac");

        var priv = new MemoryStream();
        void W(string s) => priv.Write(Encoding.Latin1.GetBytes(s));
        W("dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n");
        W("/ND{noaccess def}executeonly def\n/NP{noaccess put}executeonly def\n/lenIV 4 def\n");
        W("/Subrs 1 array\n");
        W($"dup 0 {subr0.Length} RD "); priv.Write(subr0); W(" NP\n");
        W("ND\n2 index /CharStrings 4 dict dup begin\n");
        foreach (var (name, cs) in new[] { (".notdef", notdef), ("A", glyphA), ("acute", acute), ("Aacute", aacute) })
        {
            W($"/{name} {cs.Length} RD "); priv.Write(cs); W(" ND\n");
        }
        W("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");

        byte[] encrypted = Encrypt(priv.ToArray(), 55665);
        string clear = "%!PS-AdobeFont-1.0: TestType1 001.000\n12 dict begin\n/FontInfo 2 dict dup begin /FullName (Test Type 1) def end readonly def\n" +
                       "/FontName /TestType1 def\n/PaintType 0 def\n/FontType 1 def\n" +
                       $"/FontMatrix [{fontMatrix}] readonly def\n/FontBBox {{0 -200 1000 1000}} readonly def\n" +
                       "/Encoding StandardEncoding def\ncurrentdict end\ncurrentfile eexec\n";
        byte[] clearBytes = Encoding.Latin1.GetBytes(clear);
        Type1ClearLength = clearBytes.Length;
        return clearBytes.Concat(encrypted).ToArray();
    }

    private static readonly Dictionary<string, byte[]> Ops = new()
    {
        ["rlineto"] = new byte[] { 5 }, ["closepath"] = new byte[] { 9 }, ["callsubr"] = new byte[] { 10 },
        ["return"] = new byte[] { 11 }, ["hsbw"] = new byte[] { 13 }, ["endchar"] = new byte[] { 14 },
        ["rmoveto"] = new byte[] { 21 }, ["seac"] = new byte[] { 12, 6 },
    };

    /// <summary>Type 1 charstring from ints (operands) and names (operators).</summary>
    private static byte[] Type1Ops(object[] ops)
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
