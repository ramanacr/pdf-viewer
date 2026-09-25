using System;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Differential rendering against the PDFium oracle (06_PDFIUM_FALLBACK "Reference oracle",
/// backlog A6/K6). Pixel equality is not expected; the gate is a perceptual budget: mean absolute
/// channel difference and the share of pixels that differ by more than a quarter of the range.
/// </summary>
/// <remarks>
/// PDFium is referenced ONLY by this test project, as a validation oracle (ADR-007).
/// Thresholds are recorded with the observed values in 18_IMPLEMENTATION_STATUS.md.
/// </remarks>
public class DifferentialRenderingTests
{
    private readonly ITestOutputHelper _output;

    public DifferentialRenderingTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, string, string, double, double> Fixtures => new()
    {
        // name, content, resources, max mean diff (0..255), max share of pixels off by > 64
        { "paths-fill-stroke", "0 0 1 rg 10 10 180 180 re f 1 0 0 RG 4 w 1 J 20 20 m 180 180 l S 0 1 0 RG 20 180 m 180 20 l S", "<< >>", 1.5, 0.01 },
        { "bezier-evenodd", "0.2 0.6 0.3 rg 100 20 m 180 20 180 180 100 180 c 20 180 20 20 100 20 c h 60 60 m 140 60 l 140 140 l 60 140 l h f*", "<< >>", 1.5, 0.01 },
        { "clip-and-transform", "q 1 0 0 1 20 20 cm 0 0 80 80 re W n 0.8 0.1 0.1 rg 0 0 200 200 re f Q q 0.7071 0.7071 -0.7071 0.7071 140 60 cm 0 0 1 rg -20 -20 40 40 re f Q", "<< >>", 1.5, 0.01 },
        { "dash-join", "0 0 0 RG 6 w 0 j [12 6] 0 d 20 30 m 100 170 l 180 30 l S [] 0 d 2 j 20 100 m 100 120 l 180 100 l S", "<< >>", 1.5, 0.01 },
        { "alpha", "/G1 gs 1 0 0 rg 20 20 120 120 re f 0 0 1 rg 60 60 120 120 re f", "<< /ExtGState << /G1 << /ca 0.5 >> >> >>", 1.5, 0.01 },
        { "axial-shading", "q 0 0 200 200 re W n /Sh1 sh Q", "<< /Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 0.5 1] /N 1 >> /Extend [true true] >> >> >>", 3.0, 0.01 },
        { "inline-image", "q 160 0 0 160 20 20 cm BI /W 2 /H 2 /CS /RGB /BPC 8 ID ÿ\u0000\u0000\u0000ÿ\u0000\u0000\u0000ÿÿÿ\u0000 EI Q", "<< >>", 1.5, 0.01 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task VectorOutput_MatchesPdfiumWithinPerceptualBudget(string name, string content, string resources, double maxMean, double maxBadShare)
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, resources);
        byte[] pdf = b.Build();

        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            using var vectorDoc = await PdfVectorDocument.OpenAsync(pdf);
            var list = await vectorDoc.GetPageDisplayListAsync(1);
            Assert.False(list.HasFallback, $"{name}: fixture must be fully vector");
            using var vectorPage = await InterpreterCoverageTests.RenderAsync(list, dpi);

            using var engine = new PdfiumEngine();
            await using var pdfiumDoc = await engine.OpenDocumentAsync(pdf);
            using var pdfiumPage = await engine.Renderer.RenderPageAsync(pdfiumDoc, new RenderRequest { PageNumber = 1, Dpi = dpi });

            var (mean, bad) = Compare(vectorPage, pdfiumPage);
            _output.WriteLine($"{name} @{dpi}dpi: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= maxMean, $"{name} @{dpi}: mean channel diff {mean:F2} > {maxMean}");
            Assert.True(bad <= maxBadShare, $"{name} @{dpi}: {bad:P2} pixels off by >64 (budget {maxBadShare:P0})");
        }
    }

    [Fact]
    public async Task StandardFontText_StaysWithinTextBudget()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 24 Tf 10 150 Td (Hello, vector world) Tj 0 -40 Td [(Ke) 80 (rning) -200 (TJ)] TJ ET", $"<< /Font << /F1 {font} 0 R >> >>");
        byte[] pdf = b.Build();

        using var vectorDoc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await vectorDoc.GetPageDisplayListAsync(1);
        using var vectorPage = await InterpreterCoverageTests.RenderAsync(list, 144);

        using var engine = new PdfiumEngine();
        await using var pdfiumDoc = await engine.OpenDocumentAsync(pdf);
        using var pdfiumPage = await engine.Renderer.RenderPageAsync(pdfiumDoc, new RenderRequest { PageNumber = 1, Dpi = 144 });

        var (mean, bad) = Compare(vectorPage, pdfiumPage);
        _output.WriteLine($"text @144dpi: mean={mean:F2} bad={bad:P2}");
        // Substitute glyph shapes differ (Arial vs PDFium's built-in Helvetica); positions come from
        // the same AFM widths, so differences stay local to glyph outlines.
        Assert.True(mean <= 3.0, $"text mean diff {mean:F2}");
        Assert.True(bad <= 0.02, $"text bad pixels {bad:P2}");
    }

    internal static (double Mean, double BadShare) Compare(RenderedPage a, RenderedPage b)
    {
        Assert.Equal(b.WidthPixels, a.WidthPixels);
        Assert.Equal(b.HeightPixels, a.HeightPixels);
        var pa = a.Pixels.Span;
        var pb = b.Pixels.Span;
        long total = 0;
        long bad = 0;
        long pixels = (long)a.WidthPixels * a.HeightPixels;
        for (int y = 0; y < a.HeightPixels; y++)
        {
            for (int x = 0; x < a.WidthPixels; x++)
            {
                int ia = y * a.Stride + x * 4, ib = y * b.Stride + x * 4;
                int max = 0;
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(pa[ia + c] - pb[ib + c]);
                    total += d;
                    if (d > max) max = d;
                }
                if (max > 64) bad++;
            }
        }
        return (total / (double)(pixels * 3), bad / (double)pixels);
    }
}
