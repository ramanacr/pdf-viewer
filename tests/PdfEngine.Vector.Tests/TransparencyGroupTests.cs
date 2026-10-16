using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Non-isolated groups over transparent backdrops and knockout groups (ISO 32000-1 11.4.7,
/// 11.4.8, 11.6.6). Expected pixels are computed from the spec's equations by hand. PDFium is no
/// oracle here: it composites non-isolated groups as isolated and ignores knockout, so it agrees
/// only where those make no difference. Its pixels are logged next to ours.
/// </summary>
public class TransparencyGroupTests
{
    private readonly ITestOutputHelper _output;
    public TransparencyGroupTests(ITestOutputHelper output) => _output = output;

    // Opaque page backdrop: red, green, blue and 50 % grey strips, a yellow band across the top.
    private const string Backdrop =
        "1 0 0 rg 0 0 50 200 re f 0 1 0 rg 50 0 50 200 re f 0 0 1 rg 100 0 50 200 re f 0.5 0.5 0.5 rg 150 0 50 200 re f " +
        "1 1 0 rg 0 150 200 50 re f ";

    private static async Task<(RenderedPage Vector, RenderedPage Reference, IPdfDisplayList List, D2DRenderResult Result)> Render(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        return (result.Page, reference, list, result);
    }

    private static (int R, int G, int B) Pixel(RenderedPage page, int x, int y)
    {
        var px = page.Pixels.Span;
        int o = y * page.Stride + x * 4;
        return (px[o + 2], px[o + 1], px[o]);
    }

    /// <summary>Renders the page and checks image pixels (x, y from the top left) against hand-computed values.</summary>
    private async Task AssertPixels(string name, byte[] pdf, params (int X, int Y, int R, int G, int B)[] expected)
    {
        var (v, p, list, result) = await Render(pdf);
        using (v) using (p)
        {
            Assert.False(list.HasFallback, $"{name}: interpreter fell back: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
            Assert.Empty(result.Fallbacks);
            foreach (var (x, y, r, g, b) in expected)
            {
                var got = Pixel(v, x, y);
                _output.WriteLine($"{name} ({x},{y}): got {got.R},{got.G},{got.B} expected {r},{g},{b} (PDFium {Pixel(p, x, y)})");
                Assert.True(Math.Abs(got.R - r) <= 3 && Math.Abs(got.G - g) <= 3 && Math.Abs(got.B - b) <= 3,
                    $"{name} at ({x},{y}): got {got.R},{got.G},{got.B}, expected {r},{g},{b}");
            }
        }
    }

    /// <summary>A circle as four Bézier arcs, filled.</summary>
    private static string Circle(double cx, double cy, double r)
    {
        double k = 0.5523 * r;
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return $"{F(cx + r)} {F(cy)} m " +
               $"{F(cx + r)} {F(cy + k)} {F(cx + k)} {F(cy + r)} {F(cx)} {F(cy + r)} c " +
               $"{F(cx - k)} {F(cy + r)} {F(cx - r)} {F(cy + k)} {F(cx - r)} {F(cy)} c " +
               $"{F(cx - r)} {F(cy - k)} {F(cx - k)} {F(cy - r)} {F(cx)} {F(cy - r)} c " +
               $"{F(cx + k)} {F(cy - r)} {F(cx + r)} {F(cy - k)} {F(cx + r)} {F(cy)} c f ";
    }

    /// <summary>
    /// An isolated group (painted at 80 % opacity) holding a 50 % green square and a non-isolated
    /// group whose pink rectangle multiplies. Inside the isolated group the non-isolated group's
    /// backdrop is the half-transparent green, not the page.
    /// </summary>
    private static byte[] NestedNonIsolated(string innerDo, string pinkAlpha)
    {
        var b = new VectorPdfBuilder();
        int inner = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> " +
                                $"/Resources << /ExtGState << /M << /BM /Multiply {pinkAlpha} >> >> >>",
            "/M gs 0.9 0.3 0.6 rg 60 60 120 110 re f");
        int outer = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> " +
                                $"/Resources << /XObject << /N {inner} 0 R >> /ExtGState << /T << /ca 0.5 >> /D << {innerDo} >> >> >>",
            "/T gs 0.2 0.8 0.3 rg 30 30 100 100 re f /D gs /N Do");
        b.AddPage(Backdrop + "/A gs /O Do", $"<< /XObject << /O {outer} 0 R >> /ExtGState << /A << /ca 0.8 >> >> >>");
        return b.Build();
    }

    // Green G = (0.2, 0.8, 0.3) at α0 = 0.5 is the inner group's backdrop; pink P = (0.9, 0.3, 0.6)
    // multiplies against it: Cn = 0.5·P + 0.5·(P × G) = (0.54, 0.27, 0.39) with αn = 1, and the
    // backdrop removal C = Cn + (Cn − C0)·(α0/αg − α0) leaves Cn because αg = 1.
    [Fact]
    public Task NonIsolatedGroup_InIsolatedGroup_BlendsWithTheTransparentBackdrop() =>
        AssertPixels("nested non-isolated, alpha 0.7", NestedNonIsolated("/ca 0.7", ""),
            // 0.8·(0.7·Cn + 0.3·0.5·G) + 0.32·blue. Composited as isolated it would be 135,67,176.
            (110, 100, 83, 63, 146),
            // 0.8·0.7·P + 0.44·grey: no backdrop inside the outer group there.
            (160, 100, 185, 99, 142),
            // 0.8·0.5·G + 0.6·red.
            (40, 100, 173, 82, 31));

    // With the pink at 0.6: αn = 0.8, Cn = 0.25·G + 0.75·(0.54, 0.27, 0.39), αg = 0.6, and the
    // removal gives αg·C = Pn − P0·(1 − αg) = (0.324, 0.162, 0.234) (premultiplied).
    [Fact]
    public Task NonIsolatedGroup_InIsolatedGroup_TranslucentContent() =>
        AssertPixels("nested non-isolated, translucent", NestedNonIsolated("/ca 0.7", "/ca 0.6"),
            // 0.8·(0.7·R + 0.58·0.5·G) + 0.432·blue.
            (110, 100, 58, 70, 161),
            // The pink multiplies a transparent backdrop: 0.8·0.42·P + 0.664·grey.
            (160, 100, 162, 110, 136));

    [Fact]
    public Task NonIsolatedGroup_InIsolatedGroup_ScreenedOntoTheBackdrop() =>
        AssertPixels("nested non-isolated, screen", NestedNonIsolated("/ca 1 /BM /Screen", ""),
            // 0.8·(0.5·Cn + 0.5·screen(G, Cn)) + 0.2·blue.
            (110, 100, 120, 115, 149),
            // screen against a transparent backdrop is the source: 0.8·P + 0.2·grey.
            (160, 100, 209, 87, 148));

    /// <summary>A knockout group of two half-transparent shapes, orange then blue; the blue one multiplies.</summary>
    private static string KnockoutForm(VectorPdfBuilder b, string group, string blueBlend) =>
        b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true {group} >> " +
                    $"/Resources << /ExtGState << /H << /ca 0.5 >> /B << /ca 0.5 {blueBlend} >> >> >>",
            "/H gs 0.9 0.4 0.1 rg 60 40 80 80 re f /B gs 0.1 0.4 0.9 rg 100 80 80 80 re f") + " 0 R";

    // A non-isolated knockout group composites each element with the group's initial backdrop,
    // with the element's blend mode: the multiplying blue sees the page's blue strip, not orange.
    [Fact]
    public Task NonIsolatedKnockout_BlendedElementBlendsWithTheInitialBackdrop()
    {
        var b = new VectorPdfBuilder();
        string k = KnockoutForm(b, "", "/BM /Multiply");
        b.AddPage(Backdrop + "/K Do", $"<< /XObject << /K {k} >> >>");
        return AssertPixels("knockout, multiply element", b.Build(),
            // Pn = 0.5·C0 + 0.5·(C0 × blue) = (0, 0, 0.95), αg = 0.5, R = Pn − 0.5·C0; R + 0.5·C0 = Pn.
            (120, 100, 0, 0, 242),
            // Orange alone over green: 0.5·O + 0.5·green.
            (80, 140, 115, 179, 13));
    }

    // Inside an isolated group the knockout group's initial backdrop is the group's own
    // half-transparent green, and the blue multiplies that.
    [Fact]
    public Task NonIsolatedKnockout_InIsolatedGroup_BlendsWithTheTransparentBackdrop()
    {
        var b = new VectorPdfBuilder();
        string k = KnockoutForm(b, "", "/BM /Multiply");
        int outer = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> " +
                                $"/Resources << /XObject << /K {k} >> /ExtGState << /T << /ca 0.5 >> /U << /ca 1 >> >> >>",
            "/T gs 0.2 0.8 0.3 rg 30 30 100 100 re f /U gs /K Do");
        b.AddPage(Backdrop + "/A gs /O Do", $"<< /XObject << /O {outer} 0 R >> /ExtGState << /A << /ca 0.8 >> >> >>");
        return AssertPixels("knockout in isolated group", b.Build(),
            // Pn = 0.5·P0 + 0.5·0.5·blue + 0.25·(blue × G) over P0 = 0.5·G; the group result keeps
            // Pn there, and the page gets 0.8·Pn + 0.4·blue. As an isolated knockout: 20,82,209.
            (120, 100, 16, 78, 177),
            // Orange alone over the green: 0.8·(0.5·O + 0.25·G) + 0.4·green.
            (80, 140, 102, 184, 26));
    }

    [Theory]
    [InlineData("/I true")]
    [InlineData("")]
    public async Task Knockout_OverlappingTranslucentCircles(string group)
    {
        var b = new VectorPdfBuilder();
        int k = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true {group} >> " +
                            "/Resources << /ExtGState << /H << /ca 0.5 >> >> >>",
            "/H gs 0.9 0.4 0.1 rg " + Circle(80, 100, 50) + "0.1 0.4 0.9 rg " + Circle(120, 100, 50));
        b.AddPage(Backdrop + "/K Do", $"<< /XObject << /K {k} 0 R >> >>");
        var pdf = b.Build();
        await AssertPixels("knockout circles " + group, pdf,
            (45, 100, 242, 51, 13),    // orange only, over red
            (90, 100, 13, 179, 115),   // blue knocks orange out, over green: 0.5·blue + 0.5·green
            (105, 100, 13, 51, 242),   // over the blue strip
            (160, 100, 77, 115, 179)); // blue only, over grey

        // Inside the blue circle over the blue strip every pixel is the same, also where the
        // orange circle's edge crosses (x ≈ 130 on row 100, x ≈ 115 on row 65): no seam of
        // double-counted or missing colour along the knocked-out edge.
        var (v, p, _, _) = await Render(pdf);
        using (v) using (p)
        {
            foreach (int y in new[] { 100, 65 })
            {
                for (int x = 102; x <= 148; x++)
                {
                    var (r, g, bl) = Pixel(v, x, y);
                    Assert.True(Math.Abs(r - 13) <= 3 && Math.Abs(g - 51) <= 3 && Math.Abs(bl - 242) <= 3, $"seam at ({x},{y}): {r},{g},{bl}");
                }
            }
        }
    }

    // A knockout group painted through a luminosity soft mask: white over the left half, 50 % grey
    // over the right. The knockout result is computed first, then masked (11.6.5).
    [Fact]
    public Task Knockout_WithSoftMask()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>",
            "1 g 0 0 100 200 re f 0.5 g 100 0 100 200 re f");
        int k = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true /I true >> " +
                            "/Resources << /ExtGState << /H << /ca 0.5 >> >> >>",
            "/H gs 0.9 0.4 0.1 rg 20 20 120 120 re f 0.1 0.4 0.9 rg 60 60 120 120 re f");
        b.AddPage(Backdrop + "/S gs /K Do",
            $"<< /XObject << /K {k} 0 R >> /ExtGState << /S << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >> >> >>");
        return AssertPixels("knockout + soft mask", b.Build(),
            (40, 100, 242, 51, 13),   // orange alone, mask 1: 0.5·O + 0.5·red
            (90, 70, 13, 179, 115),   // blue knocks orange out, mask 1, over green
            (120, 70, 6, 26, 249));   // blue knocks orange out, mask 0.5: 0.25·blue + 0.75·blue strip
    }

    // A non-isolated blend group inside an alpha soft mask: the mask group is drawn on a
    // transparent surface, which the group sees as its backdrop.
    [Fact]
    public async Task NonIsolatedBlendGroup_InAlphaSoftMask_SeesTheMaskBackdrop()
    {
        var b = new VectorPdfBuilder();
        // The mask: a 50 % black square, then a non-isolated group (alpha 0.5) whose content
        // multiplies. Alpha of the mask = alpha of its group, which the blend does not change.
        int inner = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> " +
                                "/Resources << /ExtGState << /M << /BM /Multiply >> >> >>",
            "/M gs 0 g 100 0 100 200 re f");
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> " +
                               $"/Resources << /XObject << /N {inner} 0 R >> /ExtGState << /T << /ca 0.5 >> >> >>",
            "/T gs 0 g 0 0 200 100 re f /N Do");
        b.AddPage(Backdrop + "/S gs 0 0 0 rg 0 0 200 200 re f",
            $"<< /ExtGState << /S << /SMask << /Type /Mask /S /Alpha /G {mask} 0 R >> >> >> >>");
        await AssertPixels("non-isolated group in alpha mask", b.Build(),
            // Mask alpha 0.5 (square) ∪ 0.5 (inner group) = 0.75 → 0.25·grey.
            (160, 150, 32, 32, 32),
            // Inner group only: 0.5 → 0.5·grey.
            (160, 80, 64, 64, 64),
            // Square only: 0.5 → 0.5·green.
            (75, 150, 0, 128, 0));
    }

    [Fact]
    public async Task KnockoutGroup_WithTooManyElements_FallsBackToRegion()
    {
        var b = new VectorPdfBuilder();
        var content = new System.Text.StringBuilder("/H gs ");
        for (int i = 0; i <= Replayer.MaxKnockoutElements; i++)
            content.Append(CultureInfo.InvariantCulture, $"{i % 7 / 7.0:0.##} 0.4 0.6 rg {i % 150} {i % 120} 40 40 re f ");
        int k = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true >> " +
                            "/Resources << /ExtGState << /H << /ca 0.5 >> >> >>", content.ToString());
        b.AddPage(Backdrop + "/K Do", $"<< /XObject << /K {k} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var renderer = new Direct2DVectorRenderer();
        var token = Assert.Single(await renderer.AnalyzeAsync(list));
        Assert.Equal(PdfFallbackReason.TransparencyGroup, token.Reason);
        Assert.Equal("direct2d", token.Metadata!["origin"]);
    }

    [Fact]
    public async Task KnockoutGroup_AtTheElementLimit_StaysNative()
    {
        var b = new VectorPdfBuilder();
        var content = new System.Text.StringBuilder("/H gs ");
        for (int i = 0; i < Replayer.MaxKnockoutElements; i++)
            content.Append(CultureInfo.InvariantCulture, $"0.1 0.4 0.9 rg {i % 150} {i % 120} 40 40 re f ");
        int k = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true >> " +
                            "/Resources << /ExtGState << /H << /ca 0.5 >> >> >>", content.ToString());
        b.AddPage(Backdrop + "/K Do", $"<< /XObject << /K {k} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        Assert.Empty(await renderer.AnalyzeAsync(list));
    }
}
