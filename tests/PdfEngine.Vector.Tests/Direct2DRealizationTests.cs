using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Deep-zoom tessellation: geometries are realized only when a tile shows them and, when they
/// reach far beyond it, only near it; a zoom can be prepared in the background and cancelled.
/// Whatever is built, and however, the tile's pixels are those of the synchronous render.
/// </summary>
public class Direct2DRealizationTests
{
    private readonly ITestOutputHelper _output;
    public Direct2DRealizationTests(ITestOutputHelper output) => _output = output;

    /// <summary>Page-spanning strokes (every join and cap, translucent, closed) and fills (nonzero, even-odd, off the page).</summary>
    private static string SpanningContent(int curves = 60)
    {
        var inv = CultureInfo.InvariantCulture;
        var rng = new Random(1234);
        var s = new StringBuilder();
        for (int i = 0; i < curves; i++)
        {
            s.Append(inv, $"{rng.NextDouble():F2} {rng.NextDouble():F2} {rng.NextDouble():F2} RG {rng.Next(1, 6)} w {i % 3} J {i % 3} j ");
            if (i % 7 == 0) s.Append("/GS1 gs ");
            s.Append(inv, $"{rng.Next(0, 612)} {rng.Next(0, 792)} m {rng.Next(-100, 712)} {rng.Next(-100, 892)} {rng.Next(-100, 712)} {rng.Next(-100, 892)} {rng.Next(0, 612)} {rng.Next(0, 792)} c ");
            s.Append(i % 5 == 0 ? $"{rng.Next(0, 612)} {rng.Next(0, 792)} l h S\n" : "S\n");
            if (i % 7 == 0) s.Append("/GS0 gs ");
        }
        s.Append("0.2 0.6 0.3 rg -200 300 m 300 -400 900 1200 820 400 c 700 900 -100 1100 -200 300 c f\n");
        s.Append("0.8 0.3 0.1 rg 306 900 m 50 -100 l 700 500 l -100 500 l 560 -100 l h f*\n");
        s.Append("0.1 0.1 0.8 rg 100 200 m 100 700 600 700 600 200 c 600 -300 100 -300 100 200 c h 250 250 m 450 250 l 450 450 l 250 450 l h f*\n");
        return s.ToString();
    }

    private const string Resources = "<< /ExtGState << /GS1 << /CA 0.5 >> /GS0 << /CA 1 >> >> >>";

    private static async Task<IPdfDisplayList> ListAsync(string content, string mediaBox = "[0 0 612 792]")
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, Resources, mediaBox);
        var doc = await PdfVectorDocument.OpenAsync(b.Build());
        return await doc.GetPageDisplayListAsync(1);
    }

    /// <summary>1600 % at 150 % display scaling, as the viewer asks for it.</summary>
    private static RenderRequest Deep(double zoom = 16) => new() { PageNumber = 1, Dpi = zoom * 1.5 * 96 };

    private static PixelRegion Centre(IPdfDisplayList list, RenderRequest request, int w = 1800, int h = 1350)
    {
        var (fw, fh) = Direct2DVectorRenderer.OutputSize(list, request);
        return new PixelRegion((fw - w) / 2, (fh - h) / 2, w, h);
    }

    private static async Task<RenderedPage> Tile(Direct2DVectorRenderer r, IPdfDisplayList list, RenderRequest request, PixelRegion region) =>
        (await r.RenderAsync(list, request, null, region, invertColors: false, CancellationToken.None)).Page;

    private static bool SamePixels(RenderedPage a, RenderedPage b)
    {
        if (a.WidthPixels != b.WidthPixels || a.HeightPixels != b.HeightPixels)
            return false;
        var pa = a.Pixels.Span;
        var pb = b.Pixels.Span;
        for (int y = 0; y < a.HeightPixels; y++)
            if (!pa.Slice(y * a.Stride, a.WidthPixels * 4).SequenceEqual(pb.Slice(y * b.Stride, b.WidthPixels * 4)))
                return false;
        return true;
    }

    [Fact]
    public async Task Realizations_AreBuiltOnlyForGeometriesTheTileShows()
    {
        // Four shapes, one per quadrant of a 200 pt page.
        var list = await ListAsync("1 0 0 RG 4 w 10 10 m 40 90 60 10 90 90 c S 0 0 1 rg 110 10 m 190 10 l 150 90 l h f " +
                                   "0 1 0 RG 3 w 10 110 m 90 190 l S 0 g 110 110 m 190 110 190 190 110 190 c h f", "[0 0 200 200]");
        using var renderer = new Direct2DVectorRenderer();
        var request = new RenderRequest { PageNumber = 1, Dpi = 8 * 72 }; // 1600 × 1600 px
        using (await Tile(renderer, list, request, new PixelRegion(0, 0, 700, 700)))
            Assert.Equal(1, renderer.RealizationCounts.Built);   // top left on screen: the green line only
        using (await Tile(renderer, list, request, new PixelRegion(900, 900, 700, 700)))
            Assert.Equal(2, renderer.RealizationCounts.Built);   // bottom right: the blue triangle
        using (await Tile(renderer, list, request, new PixelRegion(0, 0, 1600, 1600)))
            Assert.Equal(4, renderer.RealizationCounts.Built);   // everything, nothing built twice
        Assert.Equal(0, renderer.RealizationCounts.Windowed);    // no shape reaches far beyond a tile
    }

    [Fact]
    public async Task PageSpanningPaths_AreRealizedNearTheTile_AndMatchWholeRealizations()
    {
        var list = await ListAsync(SpanningContent());
        var request = Deep();
        var region = Centre(list, request);
        using var windowed = new Direct2DVectorRenderer();
        using var whole = new Direct2DVectorRenderer { WindowedRealizations = false };
        using var a = await Tile(windowed, list, request, region);
        using var b = await Tile(whole, list, request, region);
        var counts = windowed.RealizationCounts;
        _output.WriteLine($"built {counts.Built}, windowed {counts.Windowed}");
        Assert.True(counts.Windowed > counts.Built / 2, $"only {counts.Windowed} of {counts.Built} realizations windowed");
        Assert.Equal(0, whole.RealizationCounts.Windowed);

        // Tessellating a piece instead of the whole curve moves vertices by less than the flattening
        // tolerance: antialiasing may differ by a few levels, nothing else.
        var (mean, bad) = DifferentialRenderingTests.Compare(a, b);
        _output.WriteLine($"windowed vs whole: mean={mean:F4} bad={bad:P4}");
        Assert.True(mean <= 0.05, $"mean {mean:F4}");
        Assert.True(bad <= 0.0005, $"bad {bad:P4}");
    }

    [Theory]
    [InlineData(PageRotation.Rotate90)]
    [InlineData(PageRotation.Rotate180)]
    public async Task WindowedRealizations_MatchUnderRotation(PageRotation rotation)
    {
        var list = await ListAsync(SpanningContent(20));
        var request = Deep(8) with { Rotation = rotation };
        var region = Centre(list, request);
        using var windowed = new Direct2DVectorRenderer();
        using var whole = new Direct2DVectorRenderer { WindowedRealizations = false };
        using var a = await Tile(windowed, list, request, region);
        using var b = await Tile(whole, list, request, region);
        Assert.True(windowed.RealizationCounts.Windowed > 0);
        var (mean, bad) = DifferentialRenderingTests.Compare(a, b);
        Assert.True(mean <= 0.05 && bad <= 0.0005, $"{rotation}: mean {mean:F4} bad {bad:P4}");
    }

    [Fact]
    public async Task ScrollingWithinTheWindow_ReusesRealizations_BeyondItRebuildsThem()
    {
        var list = await ListAsync(SpanningContent(20));
        var request = Deep();
        var region = Centre(list, request);
        using var renderer = new Direct2DVectorRenderer();
        using (await Tile(renderer, list, request, region)) { }
        Assert.True(renderer.RealizationCounts.Windowed > 0);

        // One viewport down: inside the window (1.5 region heights below). Paths that come into
        // view are realized; none is rebuilt.
        using (await Tile(renderer, list, request, region with { Y = region.Y + region.Height })) { }
        Assert.Equal(0, renderer.RealizationCounts.Rebuilt);

        // Far down the page: the windowed realizations are rebuilt there, and still correct.
        var far = region with { Y = region.Y + 6 * region.Height };
        using var rebuilt = await Tile(renderer, list, request, far);
        Assert.True(renderer.RealizationCounts.Rebuilt > 0);
        using var whole = new Direct2DVectorRenderer { WindowedRealizations = false };
        using var reference = await Tile(whole, list, request, far);
        var (mean, bad) = DifferentialRenderingTests.Compare(rebuilt, reference);
        Assert.True(mean <= 0.05 && bad <= 0.0005, $"mean {mean:F4} bad {bad:P4}");
    }

    [Fact]
    public async Task PreparedTile_EqualsTheSynchronousRender_AndBuildsNothingMore()
    {
        var list = await ListAsync(SpanningContent());
        var request = Deep();
        var region = Centre(list, request);

        using var prepared = new Direct2DVectorRenderer();
        await prepared.PrepareAsync(list, request, region, invertColors: false, CancellationToken.None);
        var built = prepared.RealizationCounts.Built;
        Assert.True(built > 0);
        using var crisp = await Tile(prepared, list, request, region);
        Assert.Equal(built, prepared.RealizationCounts.Built); // the tile only drew

        using var synchronous = new Direct2DVectorRenderer();
        using var reference = await Tile(synchronous, list, request, region);
        Assert.True(SamePixels(crisp, reference), "prepared tile differs from the synchronous render");
    }

    [Fact]
    public async Task PreparedNightModeTile_EqualsTheSynchronousRender()
    {
        var list = await ListAsync(SpanningContent(20));
        var request = Deep(8);
        var region = Centre(list, request);
        using var prepared = new Direct2DVectorRenderer();
        await prepared.PrepareAsync(list, request, region, invertColors: true, CancellationToken.None);
        using var crisp = (await prepared.RenderAsync(list, request, null, region, invertColors: true, CancellationToken.None)).Page;
        using var synchronous = new Direct2DVectorRenderer();
        using var reference = (await synchronous.RenderAsync(list, request, null, region, invertColors: true, CancellationToken.None)).Page;
        Assert.True(SamePixels(crisp, reference));
    }

    [Fact]
    public async Task CancelledPreparation_Throws_AndLeavesTheRendererCorrect()
    {
        var list = await ListAsync(SpanningContent(400));
        var request = Deep();
        var region = Centre(list, request);
        using var renderer = new Direct2DVectorRenderer();

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.PrepareAsync(list, request, region, false, cancelled.Token));
        }
        Assert.Equal(0, renderer.RealizationCounts.Built);

        // Cancelled mid-way (the zoom moved on): it stops between geometries.
        using (var cts = new CancellationTokenSource())
        {
            var work = renderer.PrepareAsync(list, request, region, false, cts.Token);
            await Task.Delay(15);
            cts.Cancel();
            try { await work; } catch (OperationCanceledException) { }
        }
        var partial = renderer.RealizationCounts.Built;
        _output.WriteLine($"built before cancellation: {partial}");

        // The renderer is free, and what was built is valid: the tile equals a fresh render.
        var render = Tile(renderer, list, request, region);
        Assert.Same(render, await Task.WhenAny(render, Task.Delay(TimeSpan.FromSeconds(30))));
        using var crisp = await render;
        using var fresh = new Direct2DVectorRenderer();
        using var reference = await Tile(fresh, list, request, region);
        Assert.True(SamePixels(crisp, reference), "render after a cancelled preparation differs");
    }

    [Fact]
    public async Task PreparationForAStaleZoom_DoesNotChangeTheCurrentZoomsTile()
    {
        var list = await ListAsync(SpanningContent(40));
        var stale = Deep(12);
        var current = Deep();
        var region = Centre(list, current);
        using var renderer = new Direct2DVectorRenderer();
        await renderer.PrepareAsync(list, stale, Centre(list, stale), false, CancellationToken.None);
        using var crisp = await Tile(renderer, list, current, region);
        using var fresh = new Direct2DVectorRenderer();
        using var reference = await Tile(fresh, list, current, region);
        Assert.True(SamePixels(crisp, reference));
    }
}
