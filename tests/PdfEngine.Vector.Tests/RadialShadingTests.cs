using System.Linq;
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
/// Radial shadings between arbitrary circles (ISO 32000-2 8.7.4.5.4), which a centre/focus
/// gradient brush cannot express: Direct2D evaluates the largest t per pixel.
/// </summary>
public class RadialShadingTests
{
    private readonly ITestOutputHelper _output;
    public RadialShadingTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("concentric-hole", "[100 100 40 100 100 90]", "[false false]")]
    [InlineData("concentric-hole-extend-end", "[100 100 40 100 100 70]", "[false true]")]
    [InlineData("cone", "[60 60 10 140 140 50]", "[false false]")]
    [InlineData("cone-extended", "[60 100 20 150 100 40]", "[true true]")]
    [InlineData("outside-start", "[30 30 5 120 120 60]", "[true false]")]
    public async Task TwoCircleRadial_MatchesPdfium(string name, string coords, string extend)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.9 g 0 0 200 200 re f /Sh1 sh",
            "<< /Shading << /Sh1 << /ShadingType 3 /ColorSpace /DeviceRGB /Coords " + coords + " /Extend " + extend +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.8 0] /C1 [0.1 0 0.7] /N 1 >> >> >> >>");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        Assert.True(list.Commands.OfType<DrawShading>().Single().Shading is PdfRadialShading { IsGeneral: true });
        using var renderer = new Direct2DVectorRenderer();
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            using var v = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi });
            using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"{name} @{dpi}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 3.0 && bad <= 0.02, $"{name} @{dpi}: mean {mean:F2} bad {bad:P2}");
        }
    }
}
