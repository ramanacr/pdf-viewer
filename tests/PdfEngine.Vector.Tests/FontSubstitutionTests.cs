using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Non-embedded fonts: the installed font of the same family and style first, then the design
/// Windows ships under another name, then a face of the same class — never a serif for Verdana
/// because its descriptor flags claim one.
/// </summary>
public class FontSubstitutionTests
{
    private readonly ITestOutputHelper _output;
    public FontSubstitutionTests(ITestOutputHelper output) => _output = output;

    private static readonly SystemFontCatalog Fonts = SystemFontCatalog.Installed;

    /// <summary>
    /// <paramref name="family"/> when installed, otherwise <paramref name="fallback"/> (Office fonts
    /// such as Gill Sans MT and Century Gothic are missing on bare Windows installations).
    /// </summary>
    [Theory]
    [InlineData("Verdana", 34, "Verdana", "Verdana", false, false)]
    [InlineData("Verdana,Bold", 34, "Verdana", "Verdana", true, false)]
    [InlineData("ABCDEF+Verdana-BoldItalic", 34, "Verdana", "Verdana", true, true)]
    [InlineData("VerdanaBold", 32, "Verdana", "Verdana", true, false)]
    [InlineData("ArialNarrow,Bold", 32, "Arial Narrow", "Arial", true, false)]
    [InlineData("ArialNarrow", 32, "Arial Narrow", "Arial", false, false)]
    [InlineData("Arial-BoldMT", 32, "Arial", "Arial", true, false)]
    [InlineData("TimesNewRomanPS-ItalicMT", 98, "Times New Roman", "Times New Roman", false, true)]
    [InlineData("Helvetica-Oblique", 96, "Arial", "Arial", false, true)]
    [InlineData("Helvetica-Narrow-Bold", 32, "Arial Narrow", "Arial", true, false)]
    [InlineData("Times-Roman", 34, "Times New Roman", "Times New Roman", false, false)]
    [InlineData("Courier-Bold", 35, "Courier New", "Courier New", true, false)]
    [InlineData("Palatino-Italic", 98, "Palatino Linotype", "Times New Roman", false, true)]
    [InlineData("AvantGarde-Demi", 32, "Century Gothic", "Arial", true, false)]
    [InlineData("Bookman-Light", 34, "Bookman Old Style", "Times New Roman", false, false)]
    [InlineData("NewCenturySchlbk-Roman", 34, "Century Schoolbook", "Times New Roman", false, false)]
    [InlineData("GillSans-Bold", 34, "Gill Sans MT", "Arial", true, false)]
    [InlineData("Optima", 32, "Arial", "Arial", false, false)] // Candara is the closer design, but not metric-compatible: its glyphs leave gaps at Optima widths
    [InlineData("MyriadPro-Regular", 32, "Segoe UI", "Arial", false, false)]
    [InlineData("MinionPro-Regular", 34, "Cambria", "Times New Roman", false, false)]
    [InlineData("SegoeUI-Semibold", 32, "Segoe UI", "Segoe UI", true, false)]
    [InlineData("Melior", 34, "Times New Roman", "Times New Roman", false, false)]
    [InlineData("Cheltenham-Bold", 34, "Times New Roman", "Times New Roman", true, false)]
    [InlineData("FrobnicateSans", 34, "Arial", "Arial", false, false)] // the name says sans; the serif flag is wrong
    [InlineData("Frobnicate", 33, "Courier New", "Courier New", false, false)]
    [InlineData("Frobnicate", 32, "Arial", "Arial", false, false)]
    public void Match_ChoosesTheInstalledFaceOfTheSameFamilyAndStyle(string baseFont, int flags, string family, string fallback, bool bold, bool italic)
    {
        string expected = Fonts.FindFamily(family, false, false) != null ? family : fallback;
        if (Fonts.FindFamily(expected, false, false) == null)
            return; // not even the class family is installed here

        var face = Fonts.Match(baseFont, bold: false, italic: (flags & 64) != 0, serif: (flags & 2) != 0, fixedPitch: (flags & 1) != 0, script: (flags & 8) != 0);

        Assert.NotNull(face);
        _output.WriteLine($"{baseFont} → {face!.PostScriptName} ({face.Family} / {face.LegacyFamily}, {face.Weight})");
        Assert.True(Is(face, expected), $"{baseFont}: {face.Family} / {face.LegacyFamily}, expected {expected}");
        Assert.Equal(bold, face.Bold);
        Assert.Equal(italic, face.Italic);
    }

    [Fact]
    public void Match_WeightWordsChooseTheNearestInstalledWeight()
    {
        // A stand-in design keeps to its metric-compatible regular and bold: Helvetica-Black is Arial Bold, not Arial Black.
        Assert.Equal(700, Fonts.Match("Helvetica-Black", true, false, false, false)!.Weight);
        Assert.Equal(400, Fonts.Match("Helvetica-Light", false, false, false, false)!.Weight);
        if (Fonts.FindFamily("Arial Black", false, false) != null)
            Assert.Equal(Fonts.FindFamily("Arial Black", false, false), Fonts.Match("Arial-Black", true, false, false, false));
        if (Fonts.Faces.Count(f => Is(f, "Segoe UI")) < 4)
            return;
        Assert.Equal(600, Fonts.Match("SegoeUI-Semibold", false, false, false, false)!.Weight);
        Assert.Equal(300, Fonts.Match("SegoeUI-Light", false, false, false, false)!.Weight);
    }

    [Fact]
    public void Match_ScriptFlag_ChoosesAScriptFace()
    {
        var face = Fonts.Match("Frobnicate", false, false, serif: false, fixedPitch: false, script: true);
        if (Fonts.FindFamily("Monotype Corsiva", false, false) is { } corsiva) Assert.Equal(corsiva, face);
        else if (Fonts.FindFamily("Segoe Script", false, false) is { } segoe) Assert.Equal(segoe, face);
    }

    [Fact]
    public void Direct2D_SubstituteFamily_PutsTheInstalledFontBeforeTheClassFamily()
    {
        if (Fonts.FindFamily("Verdana", true, false) == null)
            return;
        var face = new PdfFontFace("fontsub:Verdana,Bold", "Verdana,Bold", PdfFontProgramFormat.None, ReadOnlyMemory<byte>.Empty, IsBold: true, IsSerif: true);
        var (installed, family, bold, _, symbol) = Replayer.SubstituteFamily(face, null);
        Assert.True(installed != null && Is(installed, "Verdana") && installed.Bold);
        Assert.Equal("Times New Roman", family); // the class family, tried only for glyphs Verdana lacks
        Assert.True(bold);
        Assert.False(symbol);

        var dingbats = Replayer.SubstituteFamily(new PdfFontFace("fontsub:ZapfDingbats", "ZapfDingbats", PdfFontProgramFormat.None, ReadOnlyMemory<byte>.Empty), null);
        Assert.Null(dingbats.Installed);
    }

    /// <summary>
    /// Verdana, not embedded and flagged serif (as govdocs1 001041.pdf has it), draws the installed
    /// Verdana: nearly the same pixels as the same text with verdana.ttf embedded, far from Times
    /// New Roman at the same /Widths, and close to PDFium.
    /// </summary>
    [Fact]
    public async Task Direct2D_NonEmbeddedVerdana_RendersSans()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string verdanaPath = Path.Combine(fonts, "verdana.ttf"), timesPath = Path.Combine(fonts, "times.ttf");
        if (!File.Exists(verdanaPath) || !File.Exists(timesPath))
            return;
        byte[] verdana = File.ReadAllBytes(verdanaPath);
        var metrics = TrueTypeFontFile.TryLoad(verdana)!;
        string widths = string.Join(" ", Enumerable.Range(32, 95).Select(c => ((int)Math.Round(metrics.Advance(metrics.GlyphFor(c)))).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        byte[] Pdf(byte[]? program)
        {
            var b = new VectorPdfBuilder();
            string file = program != null ? $" /FontFile2 {b.AddStream($"/Length1 {program.Length}", program, flate: true)} 0 R" : string.Empty;
            int desc = b.Add($"<< /Type /FontDescriptor /FontName /Verdana /Flags 34 /FontBBox [-50 -210 1450 1000] /ItalicAngle 0 /Ascent 1005 /Descent -210 /CapHeight 727 /StemV 90{file} >>");
            int font = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /Verdana /FirstChar 32 /LastChar 126 /Widths [{widths}] /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
            b.AddPage("BT /F1 22 Tf 8 150 Td (Illegible jig) Tj 0 -40 Td (HIJ lift 1741) Tj 0 -40 Td (Mill river) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
            return b.Build();
        }

        using var renderer = new Direct2DVectorRenderer();
        async Task<RenderedPage> Vector(byte[] pdf)
        {
            using var doc = await PdfVectorDocument.OpenAsync(pdf);
            var list = await doc.GetPageDisplayListAsync(1);
            Assert.Empty(await renderer.AnalyzeAsync(list)); // drawn by the vector path, not handed to PDFium
            return await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 });
        }

        byte[] substituted = Pdf(null);
        using var sub = await Vector(substituted);
        using var embedded = await Vector(Pdf(verdana));
        using var times = await Vector(Pdf(File.ReadAllBytes(timesPath)));
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(substituted);
        using var pdfium = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 144 });

        var (toVerdana, badVerdana) = DifferentialRenderingTests.Compare(sub, embedded);
        var (toTimes, _) = DifferentialRenderingTests.Compare(sub, times);
        var (toPdfium, badPdfium) = DifferentialRenderingTests.Compare(sub, pdfium);
        _output.WriteLine($"vs embedded Verdana {toVerdana:F2} ({badVerdana:P2}), vs Times {toTimes:F2}, vs PDFium {toPdfium:F2} ({badPdfium:P2})");
        Assert.True(toVerdana < 0.5, $"substitute differs from Verdana by {toVerdana:F2}");
        Assert.True(toTimes > 4 * Math.Max(toVerdana, 0.25), $"substitute is as close to Times ({toTimes:F2}) as to Verdana ({toVerdana:F2})");
        Assert.True(toPdfium <= 2.0 && badPdfium <= 0.02, $"vs PDFium mean {toPdfium:F2}, {badPdfium:P2} off");
    }

    /// <summary>
    /// A substitute's glyphs are fitted to the document's /Widths: four Ms at 400/1000 em (Arial's
    /// is 833) are narrowed so they do not run into each other; four is at 500 (Arial's is 222) are
    /// widened a little and centred, so the gaps fall between them evenly.
    /// </summary>
    [Fact]
    public async Task Direct2D_SubstituteGlyphs_AreFittedToTheirWidths()
    {
        var b = new VectorPdfBuilder();
        string widths = string.Join(" ", Enumerable.Range(32, 95).Select(c => c == 'M' ? "400" : "500"));
        int desc = b.Add("<< /Type /FontDescriptor /FontName /Arial /Flags 32 /FontBBox [-665 -325 2000 1040] /ItalicAngle 0 /Ascent 905 /Descent -212 /CapHeight 716 /StemV 88 >>");
        int font = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /Arial /FirstChar 32 /LastChar 126 /Widths [{widths}] /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
        b.AddPage("BT /F1 40 Tf 20 150 Td (MMMM) Tj 0 -100 Td (iiii) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
        byte[] pdf = b.Build();

        using var renderer = new Direct2DVectorRenderer();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.Empty(await renderer.AnalyzeAsync(list));
        using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var px = page.Pixels.ToArray();
        // Ink columns of a band of rows (y down).
        List<int> Columns(int top, int bottom)
        {
            var columns = new List<int>();
            for (int x = 0; x < page.WidthPixels; x++)
                for (int y = top; y < bottom; y++)
                    if (px[y * page.Stride + x * 4] < 128) { columns.Add(x); break; }
            return columns;
        }
        var ms = Columns(200 - 150 - 32, 200 - 150 + 2);
        // Four advances of 16 pt: the Ms' ink spans at most 64 pt (one of Arial's is 33 pt wide at this size).
        Assert.InRange(ms.Max() - ms.Min(), 55, 64);
        var iis = Columns(200 - 50 - 30, 200 - 50 + 2);
        // Each i sits in the middle of its 20 pt: the first starts well after the text does.
        Assert.InRange(iis.Min(), 20 + 5, 20 + 10);
        Assert.InRange(iis.Max(), 20 + 60 + 5, 20 + 60 + 15);
    }

    private static bool Is(SystemFontFace face, string family) =>
        string.Equals(face.Family, family, StringComparison.OrdinalIgnoreCase) || string.Equals(face.LegacyFamily, family, StringComparison.OrdinalIgnoreCase);
}
