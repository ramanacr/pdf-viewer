using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static PdfEngine.Vector.Tests.Fixtures.SyntheticCff;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Milestone 2, text: glyph runs from embedded TrueType and CFF programs (loaded by Skia from the
/// display list's bytes), substitutes for the standard fonts, every text rendering mode, text
/// clips and the text-state parameters, against the Direct2D backend.
/// </summary>
public class TextParityTests
{
    private readonly Parity _parity;
    public TextParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>A page with Times New Roman embedded as /F1 (FontFile2) and the standard fonts by name.</summary>
    private static byte[] TextPage(string content, string extraResources = "")
    {
        var b = new VectorPdfBuilder();
        byte[] times = File.ReadAllBytes(Path.Combine(FontsDir, "times.ttf"));
        int stream = b.AddStream($"/Length1 {times.Length}", times, flate: true);
        int desc = b.Add($"<< /Type /FontDescriptor /FontName /TimesNewRomanPSMT /Flags 34 /FontBBox [0 -300 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 {stream} 0 R >>");
        int embedded = b.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /TimesNewRomanPSMT /Encoding /WinAnsiEncoding /FontDescriptor {desc} 0 R >>");
        string Std(string name) => $"<< /Type /Font /Subtype /Type1 /BaseFont /{name} /Encoding /WinAnsiEncoding >>";
        int helv = b.Add(Std("Helvetica"));
        int helvBoldOblique = b.Add(Std("Helvetica-BoldOblique"));
        int timesRoman = b.Add(Std("Times-Roman"));
        int courier = b.Add(Std("Courier"));
        int symbol = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>");
        int dingbats = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>");
        b.AddPage(content, $"<< /Font << /F1 {embedded} 0 R /H {helv} 0 R /HBO {helvBoldOblique} 0 R /TR {timesRoman} 0 R /C {courier} 0 R " +
                           $"/S {symbol} 0 R /Z {dingbats} 0 R >> {extraResources} >>");
        return b.Build();
    }

    [Fact]
    public Task EmbeddedTrueType_AndSubstitutedHelvetica() =>
        _parity.AssertMatches("embedded+substituted", TextPage("BT /F1 24 Tf 10 150 Td (Embedded Times text) Tj /H 20 Tf 0 -50 Td (Substituted Helvetica) Tj ET"), 1.0, 0.01);

    // Small text shows the backends' one systematic difference: Direct2D draws thin features with
    // more ink (about a quarter more on this page at 96 dpi), so stems differ by a level or two.
    [Fact]
    public Task SmallBodyText() =>
        _parity.AssertMatches("body-text", TextPage("BT /F1 9 Tf 11 TL 10 185 Td (The quick brown fox jumps over the lazy dog, 0123456789.) Tj " +
            "T* (Pack my box with five dozen liquor jugs! \\(parentheses\\) and quotes.) Tj /H 7 Tf T* (Helvetica at seven points: kerning AV To Wa.) Tj " +
            "/TR 8 Tf T* (Times-Roman by name, at eight points.) Tj /C 8 Tf T* (Courier: monospaced 0O1lI) Tj ET"), 2.5, 0.015, new[] { 96.0, 150.0 });

    [Fact]
    public Task StandardFontSubstitutes() =>
        _parity.AssertMatches("substitutes", TextPage("BT /HBO 18 Tf 10 170 Td (Helvetica-BoldOblique) Tj /TR 18 Tf 0 -30 Td (Times-Roman) Tj " +
            "/C 16 Tf 0 -30 Td (Courier text) Tj /S 20 Tf 0 -35 Td (abgdp\\245\\267) Tj /Z 20 Tf 0 -35 Td (3456AB) Tj ET"), 1.5, 0.02);

    [Fact]
    public Task TextStateParameters() =>
        _parity.AssertMatches("text-state", TextPage("BT /F1 18 Tf 10 170 Td 3 Tc (Char spacing) Tj 0 Tc 12 Tw 0 -30 Td (word spacing here) Tj 0 Tw " +
            "150 Tz 0 -30 Td (Wide Tz 150) Tj 60 Tz 0 -30 Td (Narrow Tz 60) Tj 100 Tz 6 Ts 0 -30 Td (Rise) Tj -6 Ts (Fall) Tj 0 Ts ET"), 1.5, 0.02);

    [Fact]
    public Task RotatedAndSkewedTextMatrices() =>
        _parity.AssertMatches("text-matrix", TextPage("BT /F1 20 Tf 0.866 0.5 -0.5 0.866 30 40 Tm (Rotated thirty) Tj 1 0 0.4 1 20 150 Tm (Skewed text) Tj " +
            "-1 0 0 1 190 110 Tm (Mirrored) Tj ET"), 1.5, 0.02);

    [Fact]
    public Task AllRenderingModes()
    {
        // Modes 0–3 on their own lines; 4–7 add to the clip, which a full-width band then shows.
        var content = new System.Text.StringBuilder("0.2 0.4 0.8 rg 0.8 0.1 0.1 RG 0.6 w BT /F1 22 Tf ");
        for (int mode = 0; mode < 4; mode++)
            content.Append(System.Globalization.CultureInfo.InvariantCulture, $"{mode} Tr 1 0 0 1 10 {180 - mode * 24} Tm (Mode {mode} Ag) Tj ");
        content.Append("ET ");
        for (int mode = 4; mode < 8; mode++)
            content.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"q BT /F1 20 Tf {mode} Tr 1 0 0 1 {10 + (mode - 4) * 48} 40 Tm (M{mode}) Tj ET 0 0.6 0 rg 0 30 200 30 re f Q ");
        return _parity.AssertMatches("render-modes", TextPage(content.ToString()), 1.5, 0.02);
    }

    [Fact]
    public Task StrokedTextWithDashesAndWideLines() =>
        _parity.AssertMatches("stroked-text", TextPage("1 Tr 0 0 0.6 RG 2 w 1 j BT /F1 48 Tf 10 120 Td (Outline) Tj ET 2 Tr 0.9 0.8 0 rg 0 G 0.25 w BT /H 40 Tf 10 50 Td (Fill+Stroke) Tj ET"), 1.5, 0.02);

    [Fact]
    public Task TextClip_FilledWithAShading()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int fn = b.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        int sh = b.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function {fn} 0 R /Extend [true true] >>");
        int pat = b.Add($"<< /Type /Pattern /PatternType 2 /Shading {sh} 0 R >>");
        b.AddPage("BT /Pattern cs /P1 scn /F1 64 Tf 10 80 Td (Grad) Tj ET", $"<< /Font << /F1 {helv} 0 R >> /Pattern << /P1 {pat} 0 R >> >>");
        return _parity.AssertMatches("pattern-text", b.Build(), 1.5, 0.02);
    }

    [Fact]
    public Task Type3AndNormalGlyphs_Mode4()
    {
        var b = new VectorPdfBuilder();
        int bar = b.AddStream(string.Empty, "1000 0 0 0 900 900 d1 0 0 900 300 re f 0 500 900 300 re f");
        int ring = b.AddStream(string.Empty, "1000 0 0 0 900 900 d1 450 0 m 900 450 l 450 900 l 0 450 l h 450 250 m 250 450 l 450 650 l 650 450 l h f*");
        int t3 = b.Add($"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                       $"/CharProcs << /bar {bar} 0 R /ring {ring} 0 R >> /Encoding << /Type /Encoding /Differences [65 /bar /ring] >> " +
                       "/FirstChar 65 /LastChar 66 /Widths [1000 1000] >>");
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        b.AddPage("q BT 4 Tr 0 0.5 0 rg /T3 36 Tf 10 130 Td (BAB) Tj /F1 44 Tf 0 -90 Td (Clip) Tj ET 0 0 1 RG 4 w 0 0 m 200 200 l S Q",
            $"<< /Font << /T3 {t3} 0 R /F1 {helv} 0 R >> >>");
        return _parity.AssertMatches("type3+mode4", b.Build(), 1.5, 0.02);
    }

    [Fact]
    public Task TextWithAlpha() =>
        _parity.AssertMatches("text-alpha", TextPage("0 0 1 rg 0 0 100 200 re f /G gs 1 0 0 rg BT /F1 40 Tf 10 90 Td (Alpha) Tj ET", "/ExtGState << /G << /ca 0.5 >> >>"), 1.5, 0.02);

    [Fact]
    public async Task InvisibleText_PaintsNothing()
    {
        using var doc = await PdfVectorDocument.OpenAsync(TextPage("BT 3 Tr /F1 40 Tf 10 90 Td (Invisible) Tj ET"));
        var list = await doc.GetPageDisplayListAsync(1);
        using var page = await Parity.Skia.RenderDisplayListAsync(list, new PdfEngine.Rendering.RenderRequest { PageNumber = 1, Dpi = 72 });
        var span = page.Pixels.Span;
        for (int i = 0; i < span.Length; i++)
            Assert.Equal(255, span[i]);
    }

    [Theory]
    [InlineData("narrow", 0.0005, 0, 0, 0.001, 1000)]
    [InlineData("skew", 0.001, 0, 0.0003, 0.001, 1000)]
    [InlineData("units-2048", 1 / 2048.0, 0, 0, 1 / 2048.0, 2048)]
    [InlineData("rotated", 0, 0.001, -0.001, 0, 1000)]
    public Task EmbeddedCff_WithFontMatrix(string label, double a, double b, double c, double d, int u)
    {
        // A bare CFF program (FontFile3 /Type1C), which the engine wraps as OpenType: the glyph
        // matrix beyond 1/unitsPerEm must reach the backend's glyph frame.
        byte[] cff = NameKeyed(new[] { a, b, c, d, 0, 0 }, new[] { ".notdef", "A" }, new[] { new byte[] { 14 }, LShape(u, hinted: true) });
        var builder = new VectorPdfBuilder();
        int stream = builder.AddStream("/Subtype /Type1C", cff);
        int desc = builder.Add("<< /Type /FontDescriptor /FontName /Synthetic /Flags 4 /FontBBox [-1000 -1000 1000 1000] /ItalicAngle 0 " +
                               $"/Ascent 900 /Descent -200 /CapHeight 800 /StemV 80 /FontFile3 {stream} 0 R >>");
        int font = builder.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Synthetic /FirstChar 65 /LastChar 65 /Widths [1000] " +
                               $"/Encoding << /Type /Encoding /Differences [65 /A] >> /FontDescriptor {desc} 0 R >>");
        builder.AddPage("BT /F1 60 Tf 100 70 Td (A) Tj 7 Tr 20 0 Td (A) Tj ET 0 0 1 rg 100 0 120 160 re f", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 220 160]");
        return _parity.AssertMatches("cff-" + label, builder.Build(), 1.0, 0.01);
    }
}
