using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Blend modes, soft masks and composited transparency groups (ISO 32000-2 11.3–11.6): the
/// interpreter emits compositing groups instead of fallback, Direct2D composites them natively
/// against the PDFium oracle, and the WPF backend classifies them as backend fallback.
/// </summary>
public class CompositingTests
{
    private readonly ITestOutputHelper _output;
    public CompositingTests(ITestOutputHelper output) => _output = output;

    // A colourful backdrop: every blend mode has something to work against.
    private const string Backdrop =
        "1 0 0 rg 0 0 50 200 re f 0 1 0 rg 50 0 50 200 re f 0 0 1 rg 100 0 50 200 re f 0.5 0.5 0.5 rg 150 0 50 200 re f " +
        "1 1 0 rg 0 150 200 50 re f ";

    public static TheoryData<string> BlendModes => new()
    {
        "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn",
        "HardLight", "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    };

    private async Task<(RenderedPage Vector, RenderedPage Reference, IPdfDisplayList List, D2DRenderResult Result)> Render(
        byte[] pdf, double dpi, bool invert = false)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi }, null, null, invert, CancellationToken.None);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
        return (result.Page, reference, list, result);
    }

    private async Task AssertMatches(string name, byte[] pdf, double maxMean = 2.0, double maxBad = 0.01)
    {
        var (v, p, list, result) = await Render(pdf, 72);
        using (v) using (p)
        {
            Assert.False(list.HasFallback, $"{name}: interpreter fell back: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
            Assert.Empty(result.Fallbacks);
            Assert.Contains(list.Commands, c => c is BeginCompositingGroup);
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"{name}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= maxMean, $"{name}: mean {mean:F2} > {maxMean}");
            Assert.True(bad <= maxBad, $"{name}: {bad:P2} pixels off");
        }
    }

    [Theory]
    [MemberData(nameof(BlendModes))]
    public async Task BlendMode_OnPath_MatchesPdfium(string mode)
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "/G1 gs 0.9 0.5 0.2 rg 20 20 160 160 re f 0.2 0.3 0.8 rg 60 60 80 120 re f",
            $"<< /ExtGState << /G1 << /BM /{mode} >> >> >>");
        await AssertMatches(mode, b.Build());
    }

    [Fact]
    public async Task BlendMode_WithAlphaAndStrokeAndClip_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "q 100 100 m 180 100 180 180 100 180 c 20 180 20 100 100 100 c h W n " +
                  "/G1 gs 0.8 0.2 0.6 rg 0 0 200 200 re f 0 0 0 RG 8 w 10 10 m 190 190 l S Q",
            "<< /ExtGState << /G1 << /BM /Multiply /ca 0.7 /CA 0.5 >> >> >>");
        await AssertMatches("clip+alpha", b.Build());
    }

    [Fact]
    public async Task LuminositySoftMask_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>",
            "1 g 0 0 70 200 re f 0.5 g 70 0 60 200 re f 0.2 g 130 0 70 100 re f");
        b.AddPage(Backdrop + "/M1 gs 0 0 0.6 rg 10 10 180 180 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >> >> >>");
        await AssertMatches("luminosity", b.Build());
    }

    [Fact]
    public async Task LuminositySoftMask_WithBackdropAndTransfer_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [40 40 160 160] /Group << /S /Transparency /CS /DeviceRGB >>",
            "0 0 0 rg 40 40 60 120 re f 1 1 1 rg 100 40 60 120 re f");
        // BC white: outside the mask's BBox the mask is white (opaque) before TR; TR inverts it.
        b.AddPage(Backdrop + "/M1 gs 0.1 0.6 0.1 rg 0 0 200 200 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R /BC [1 1 1] " +
            "/TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> >> >> >> >>");
        await AssertMatches("luminosity+BC+TR", b.Build());
    }

    [Fact]
    public async Task AlphaSoftMask_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> /Resources << /ExtGState << /H << /ca 0.4 >> >> >>",
            "0 g 20 20 80 160 re f /H gs 100 20 80 160 re f");
        b.AddPage(Backdrop + "/M1 gs 0.9 0.1 0.9 rg 0 0 200 200 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Alpha /G {mask} 0 R >> >> >> >>");
        await AssertMatches("alpha-mask", b.Build());
    }

    [Fact]
    public async Task TransparencyGroupXObject_WithBlendAndAlpha_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >>",
            "0.9 0.4 0.1 rg 20 20 120 120 re f 0.1 0.4 0.9 rg 60 60 120 120 re f");
        b.AddPage(Backdrop + "/G1 gs /Fm1 Do",
            $"<< /XObject << /Fm1 {form} 0 R >> /ExtGState << /G1 << /BM /Screen /ca 0.8 >> >> >>");
        await AssertMatches("group-screen", b.Build());
    }

    [Fact]
    public async Task BlendInsideAlphaGroup_MatchesPdfium()
    {
        // An opacity group containing a blended object is composited offscreen as a whole.
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> /Resources << /ExtGState << /B << /BM /Multiply >> >> >>",
            "0.2 0.8 0.2 rg 20 20 120 120 re f /B gs 0.9 0.2 0.2 rg 60 60 120 120 re f");
        b.AddPage(Backdrop + "/A gs /Fm1 Do",
            $"<< /XObject << /Fm1 {form} 0 R >> /ExtGState << /A << /ca 0.6 >> >> >>");
        var (v, p, list, result) = await Render(b.Build(), 72);
        using (v) using (p)
        {
            Assert.False(list.HasFallback);
            Assert.Empty(result.Fallbacks);
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"nested: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 2.0 && bad <= 0.01, $"nested: mean {mean:F2} bad {bad:P2}");
        }
    }

    [Fact]
    public async Task SoftMaskedImage_MatchesPdfium()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Group << /S /Transparency /CS /DeviceGray >>",
            "1 g 0 0 0.5 1 re f 0.3 g 0.5 0 0.5 1 re f");
        var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels);
        b.AddPage(Backdrop + "q 160 0 0 160 20 20 cm /M1 gs /Im1 Do Q",
            $"<< /XObject << /Im1 {image} 0 R >> /ExtGState << /M1 << /SMask << /S /Luminosity /G {mask} 0 R >> >> >> >>");
        await AssertMatches("image-smask", b.Build(), maxMean: 3.0, maxBad: 0.02);
    }

    [Fact]
    public async Task NightMode_BlendsTrueColoursThenInverts()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "/G1 gs 0.9 0.5 0.2 rg 20 20 160 160 re f",
            "<< /ExtGState << /G1 << /BM /Multiply >> >> >>");
        var pdf = b.Build();
        var (day, dayRef, _, _) = await Render(pdf, 72);
        var (night, nightRef, _, _) = await Render(pdf, 72, invert: true);
        using (day) using (dayRef) using (night) using (nightRef)
        {
            var d = day.Pixels.Span;
            var n = night.Pixels.Span;
            double diff = 0;
            int count = 0;
            for (int i = 0; i < day.ByteLength; i += 4)
            {
                for (int c = 0; c < 3; c++) diff += Math.Abs(255 - d[i + c] - n[i + c]);
                count += 3;
            }
            Assert.True(diff / count < 1.0, $"night render is not the inverse of the day render ({diff / count:F2})");
        }
    }

    [Fact]
    public async Task WpfBackend_ClassifiesCompositingGroupsAsFallback()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "/G1 gs 0.9 0.5 0.2 rg 20 20 60 60 re f", "<< /ExtGState << /G1 << /BM /Multiply >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var wpf = new WindowsVectorRenderer();
        var tokens = await wpf.AnalyzeAsync(list);
        var token = Assert.Single(tokens);
        Assert.Equal(PdfFallbackReason.BlendMode, token.Reason);
        Assert.True(token.Bounds.Width >= 60 && token.Bounds.Width < 70);
    }

    [Fact]
    public async Task KnockoutGroupOfOpaqueElements_IsExactAsSourceOver()
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true /I true >>",
            "0.9 0.4 0.1 rg 20 20 120 120 re f 0.1 0.4 0.9 rg 60 60 120 120 re f 0 0 0 RG 4 w 20 180 m 180 20 l S");
        b.AddPage(Backdrop + "/Fm1 Do", $"<< /XObject << /Fm1 {form} 0 R >> >>");
        var (v, p, list, result) = await Render(b.Build(), 72);
        using (v) using (p)
        {
            Assert.False(list.HasFallback);
            Assert.Empty(result.Fallbacks);
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"knockout opaque: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 2.0 && bad <= 0.01, $"mean {mean:F2} bad {bad:P2}");
        }
    }

    /// <summary>
    /// Renders a transparency-group form over the backdrop and checks pixels against values
    /// computed from ISO 32000-2 11.4 by hand. PDFium is no oracle here: it composites
    /// non-isolated groups as isolated and does not implement knockout.
    /// </summary>
    private async Task AssertFormPixels(string name, string groupDict, string formResources, string formContent, string doExtGState,
        params (int X, int Y, int R, int G, int B)[] expected)
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency {groupDict} >> /Resources << {formResources} >>", formContent);
        b.AddPage(Backdrop + (doExtGState.Length > 0 ? "/D gs " : "") + "/Fm1 Do",
            $"<< /XObject << /Fm1 {form} 0 R >> /ExtGState << /D << {doExtGState} >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var v = result.Page;
        Assert.Empty(result.Fallbacks);
        foreach (var (x, y, r, g, bl) in expected)
        {
            int o = y * v.Stride + x * 4;
            var px = v.Pixels.Span;
            _output.WriteLine($"{name} ({x},{y}): got {px[o + 2]},{px[o + 1]},{px[o]} expected {r},{g},{bl}");
            Assert.True(Math.Abs(px[o + 2] - r) <= 3 && Math.Abs(px[o + 1] - g) <= 3 && Math.Abs(px[o] - bl) <= 3,
                $"{name} at ({x},{y}): got {px[o + 2]},{px[o + 1]},{px[o]}, expected {r},{g},{bl}");
        }
    }

    private const string Translucent = "/ExtGState << /T << /ca 0.5 /CA 0.6 >> >>";
    // Orange O (0.9 0.4 0.1) at [20,140]², then at ca 0.5 blue B (0.1 0.4 0.9) at [60,180]², then a 60 % black diagonal.
    private const string TwoRects = "0.9 0.4 0.1 rg 20 20 120 120 re f /T gs 0.1 0.4 0.9 rg 60 60 120 120 re f 0 0 0 RG 6 w 20 180 m 180 20 l S";

    [Theory]
    [InlineData("/K true /I true")]
    [InlineData("/K true")]
    public Task Knockout_TranslucentElementsReplaceEarlierOnes(string group) => AssertFormPixels("knockout " + group, group, Translucent, TwoRects, "",
        (40, 100, 229, 102, 25),   // orange only
        (120, 70, 13, 51, 242),    // blue knocks orange out: 0.5·B + 0.5·(blue strip), not blue over orange
        (170, 100, 77, 115, 179),  // blue only over grey
        (100, 100, 0, 0, 102));    // the 60 % black line knocks out both rectangles over the blue strip

    [Fact]
    public Task Knockout_WithGroupAlpha() => AssertFormPixels("knockout + alpha", "/K true", Translucent, TwoRects, "/ca 0.7",
        (40, 100, 237, 71, 18),    // 0.7·O + 0.3·red
        (120, 70, 9, 36, 246),     // 0.35·B + 0.65·blue
        (100, 100, 0, 0, 148));    // 0.42·black + 0.58·blue

    [Fact]
    public Task NonIsolatedGroup_InnerBlendSeesTheBackdrop_WithGroupAlpha() =>
        AssertFormPixels("non-isolated multiply, alpha 0.6", "", "/ExtGState << /M << /BM /Multiply >> >>",
            "0.2 0.8 0.3 rg 30 30 100 100 re f /M gs 0.9 0.3 0.6 rg 60 60 120 110 re f", "/ca 0.6",
            (170, 100, 120, 74, 97),   // 0.6·(pink × grey) + 0.4·grey — the isolated result would be 189,97,143
            (120, 35, 239, 148, 0),    // 0.6·(pink × yellow) + 0.4·yellow
            (100, 100, 28, 37, 130),   // pink × green over the blue strip
            (40, 100, 133, 122, 46));  // green only

    [Fact]
    public Task NonIsolatedGroup_InnerBlend_WithGroupBlendMode() =>
        AssertFormPixels("non-isolated difference, group screen", "", "/ExtGState << /M << /BM /Difference >> >>",
            "0.2 0.8 0.3 rg 30 30 100 100 re f /M gs 0.9 0.3 0.6 rg 60 60 120 110 re f", "/BM /Screen",
            (170, 100, 179, 153, 140), // screen(grey, |pink − grey|)
            (120, 35, 255, 255, 153),  // screen(yellow, |pink − yellow|)
            (100, 100, 179, 128, 255), // screen(blue, |pink − green|)
            (40, 100, 255, 204, 77));  // screen(red, green)

    [Fact]
    public async Task WpfBackend_ClassifiesKnockoutAsTransparencyGroup()
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true >> /Resources << {Translucent} >>", TwoRects);
        b.AddPage("/Fm1 Do", $"<< /XObject << /Fm1 {form} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        Assert.Contains(list.Commands, c => c is BeginCompositingGroup { Knockout: true });
        using var wpf = new WindowsVectorRenderer();
        Assert.Contains(await wpf.AnalyzeAsync(list), t => t.Reason == PdfFallbackReason.TransparencyGroup);
    }

    [Fact]
    public async Task UncapturableSoftMask_StillFallsBack()
    {
        var b = new VectorPdfBuilder();
        // /S /Bogus is not a soft-mask subtype.
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "1 g 0 0 200 200 re f");
        b.AddPage("/M1 gs 0 0 1 rg 10 10 100 100 re f",
            $"<< /ExtGState << /M1 << /SMask << /S /Bogus /G {mask} 0 R >> >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.Contains(list.FallbackTokens, t => t.Reason == PdfFallbackReason.SoftMask);
    }
}
