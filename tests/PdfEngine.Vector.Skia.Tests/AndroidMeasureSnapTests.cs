using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Measuring points snapping to the page's drawing on Android (MainActivity.Measure.cs, PageView),
/// through the shared <see cref="ViewerMeasureSnap"/>: ends win over middles and middles over the
/// nearest point on a line, within the tolerance, in shown-page points on upright and turned pages.
/// </summary>
public class AndroidMeasureSnapTests
{
    /// <summary>A page with one stroked line from (100, 100) to (300, 100) in user space.</summary>
    private static byte[] Drawing(int rotate = 0)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 w 100 100 m 300 100 l S", mediaBox: "[0 0 612 792]", extra: rotate != 0 ? $"/Rotate {rotate}" : "");
        return b.Build();
    }

    private static async Task<(PdfSnapIndex? Index, ViewerPageGeometry Geometry)> Index(byte[] pdf)
    {
        await using var doc = await ViewerDocument.OpenAsync(pdf);
        using var vector = await PdfVectorDocument.OpenAsync(pdf);
        return (await ViewerMeasureSnap.BuildAsync(vector, 1), doc.Geometry[0]);
    }

    private static ViewerPoint Shown(ViewerPageGeometry g, double x, double y)
    {
        var (sx, sy) = g.ToShown(x, y);
        return new ViewerPoint(sx, sy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public async Task Points_SnapToEndsMiddlesAndTheLine_InShownPoints(int rotate)
    {
        var (index, g) = await Index(Drawing(rotate));
        Assert.NotNull(index);

        // Near an end: the end.
        var (p, kind) = ViewerMeasureSnap.Find(index, g, Shown(g, 104, 103), 8);
        Assert.Equal(PdfSnapKind.Endpoint, kind);
        var end = Shown(g, 100, 100);
        Assert.Equal(end.X, p.X, 3);
        Assert.Equal(end.Y, p.Y, 3);

        // Near the middle: the middle.
        (p, kind) = ViewerMeasureSnap.Find(index, g, Shown(g, 203, 105), 8);
        Assert.Equal(PdfSnapKind.Midpoint, kind);
        var middle = Shown(g, 200, 100);
        Assert.Equal(middle.X, p.X, 3);
        Assert.Equal(middle.Y, p.Y, 3);

        // On the line, away from both: the nearest point on it.
        (p, kind) = ViewerMeasureSnap.Find(index, g, Shown(g, 150, 106), 8);
        Assert.Equal(PdfSnapKind.OnPath, kind);
        var on = Shown(g, 150, 100);
        Assert.Equal(on.X, p.X, 3);
        Assert.Equal(on.Y, p.Y, 3);

        // Too far: the point as it is.
        var far = Shown(g, 150, 140);
        Assert.Equal((far, PdfSnapKind.None), ViewerMeasureSnap.Find(index, g, far, 8));
    }

    [Fact]
    public void WithoutAnIndex_OrATolerance_NothingSnaps()
    {
        var g = new ViewerPageGeometry(new PdfEngine.Geometry.PdfRect(0, 0, 612, 792), new PdfEngine.Geometry.PdfRect(0, 0, 612, 792), 0);
        var p = new ViewerPoint(10, 10);
        Assert.Equal((p, PdfSnapKind.None), ViewerMeasureSnap.Find(null, g, p, 8));
        Assert.Equal((new ViewerPoint(double.NaN, 1), PdfSnapKind.None).Item2, ViewerMeasureSnap.Find(null, g, new ViewerPoint(double.NaN, 1), 8).Kind);
    }

    [Fact]
    public void TheTolerance_IsTwelveDp_InPointsAtTheZoom()
    {
        // 12 dp at density 2 is 24 pixels; at 2 pixels a point that is 12 points.
        Assert.Equal(12, ViewerMeasureSnap.Tolerance(pixelsPerPoint: 2, density: 2), 9);
        Assert.Equal(3, ViewerMeasureSnap.Tolerance(pixelsPerPoint: 8, density: 2), 9);
    }

    [Fact]
    public async Task APageNumberOutOfRange_HasNoIndex()
    {
        using var vector = await PdfVectorDocument.OpenAsync(Drawing());
        Assert.Null(await ViewerMeasureSnap.BuildAsync(vector, 2));
    }
}
