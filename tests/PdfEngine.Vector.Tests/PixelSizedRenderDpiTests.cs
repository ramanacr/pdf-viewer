using System;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// A render sized in pixels (TargetWidthPixels / TargetHeightPixels) reports the resolution of its
/// bitmap, not the requested Dpi, and fetches fallback regions at that resolution (issue #78).
/// </summary>
public class PixelSizedRenderDpiTests
{
    public static TheoryData<string> Renderers => new() { "direct2d", "wpf" };

    private static IPdfVectorRenderer Create(string name) =>
        name == "direct2d" ? new Direct2DVectorRenderer() : new WindowsVectorRenderer();

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task ReportsTheDpiOfThePixelSize(string name)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 g 0 0 10 10 re f", mediaBox: "[0 0 612 792]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = Create(name);

        using var page = await renderer.RenderDisplayListAsync(list,
            new RenderRequest { PageNumber = 1, Dpi = 72, TargetWidthPixels = 1224, TargetHeightPixels = 1584 });

        Assert.Equal(1224, page.WidthPixels);
        Assert.Equal(1584, page.HeightPixels);
        Assert.Equal(144.0, page.Dpi, 6);
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task ReportsTheRequestedDpiWhenNotSizedInPixels(string name)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 g 0 0 10 10 re f", mediaBox: "[0 0 612 792]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = Create(name);

        using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 150 });

        Assert.Equal(150.0, page.Dpi, 6);
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task FetchesFallbackRegionsAtTheDpiOfThePixelSize(string name)
    {
        var provider = new RecordingProvider();
        using var renderer = Create(name);

        // A 200 x 200 pt page at 400 x 600 px: 144 dpi across, 216 dpi down; the larger keeps the region sharp.
        using var page = await renderer.RenderDisplayListAsync(ListWithFallback(),
            new RenderRequest { PageNumber = 1, Dpi = 72, TargetWidthPixels = 400, TargetHeightPixels = 600 }, provider);

        Assert.Equal(216.0, provider.LastDpi, 6);
        Assert.Equal(216.0, page.Dpi, 6);
    }

    private static PdfDisplayList ListWithFallback()
    {
        var token = new PdfFallbackToken(1, new PdfRect(50, 50, 100, 100), PdfFallbackReason.UnknownOperator, "test region");
        var commands = new PdfDrawCommand[]
        {
            new FillPath(new PdfPath(new PdfPathSegment[]
            {
                new PdfMoveTo(new PdfPoint(0, 0)), new PdfLineTo(new PdfPoint(200, 0)), new PdfLineTo(new PdfPoint(200, 200)), new PdfLineTo(new PdfPoint(0, 200)), new PdfCloseSubpath(),
            }), new PdfPaint(PdfColor.FromRgb(0, 0, 1)), PdfFillRule.NonZero, new PdfRect(0, 0, 200, 200)),
            new DrawFallbackRegion(token, token.Bounds),
        };
        return new PdfDisplayList(1, new PdfSize(200, 200), new PdfRect(0, 0, 200, 200), new PdfRect(0, 0, 200, 200), 0, commands,
            PdfFeatureSet.Paths | PdfFeatureSet.Fallback, new[] { token });
    }

    /// <summary>A provider that paints every fallback region red and records the resolution it was asked for.</summary>
    private sealed class RecordingProvider : IPdfFallbackProvider
    {
        public double LastDpi;

        public ValueTask<RenderedPage?> RenderFallbackRegionAsync(PdfFallbackToken token, double dpi, CancellationToken cancellationToken = default)
        {
            LastDpi = dpi;
            int w = Math.Max(1, (int)Math.Ceiling(token.Bounds.Width * dpi / 72));
            int h = Math.Max(1, (int)Math.Ceiling(token.Bounds.Height * dpi / 72));
            var owner = new ManagedMemoryOwner(w * h * 4);
            for (int i = 0; i < owner.Buffer.Length; i += 4)
            {
                owner.Buffer[i + 2] = 255;
                owner.Buffer[i + 3] = 255;
            }
            return ValueTask.FromResult<RenderedPage?>(new RenderedPage(token.PageNumber, w, h, w * 4, dpi, PageRotation.Rotate0, owner));
        }
    }
}
