using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Rotate View in the continuous page layout (the Android viewer's ⋮ → View → Rotate view, the
/// desktop's Ctrl+R): pages are laid out turned, shown-page points map to the view and back at
/// every quarter turn, the page in view stays in view, and an upright layout is unchanged.
/// </summary>
public class AndroidViewRotationTests
{
    private static readonly ViewerPage[] Letter3 = { new(1, 612, 792), new(2, 612, 792), new(3, 612, 792) };

    private static ContinuousPageLayout Layout(int rotation)
    {
        var layout = new ContinuousPageLayout(Letter3, gap: 20, margin: 10);
        layout.Resize(632, 800);
        layout.SetRotation(rotation);
        return layout;
    }

    [Fact]
    public void Upright_IsTheLayoutAsBefore()
    {
        var layout = Layout(0);
        Assert.Equal(0, layout.Rotation);
        Assert.Equal(new LayoutRect(10, 10, 612, 792), layout.PageRect(1));
        var r = layout.PageRectInView(1);
        Assert.Equal((r.X + 100, r.Y + 50), layout.ToViewPoint(1, new ViewerPoint(100, 50)));
        Assert.Equal(new LayoutRect(r.X + 100, r.Y + 50, 20, 30), layout.ToView(1, new ViewerRect(100, 50, 20, 30)));
    }

    [Fact]
    public void QuarterTurn_LaysPagesOutSideways_AndFitsTheirNewWidth()
    {
        var layout = Layout(90);
        // A Letter page turned is 792 wide: fit width scales it to 612 / 792.
        double s = 612.0 / 792;
        Assert.Equal(s, layout.PixelsPerPoint, 6);
        var r = layout.PageRect(1);
        Assert.Equal(792 * s, r.Width, 6);
        Assert.Equal(612 * s, r.Height, 6);
        Assert.Equal(10 + 612 * s + 20, layout.PageRect(2).Y, 6);
        Assert.Equal(10 + 3 * 612 * s + 2 * 20 + 10, layout.ContentHeight, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PointsGoToTheViewAndBack(int rotation)
    {
        var layout = Layout(rotation);
        layout.ScrollTo(0, 300);
        foreach (var p in new[] { new ViewerPoint(0, 0), new ViewerPoint(100, 50), new ViewerPoint(612, 792), new ViewerPoint(300.5, 10.25) })
        {
            var (x, y) = layout.ToViewPoint(2, p);
            var back = layout.ToPagePoint(2, x, y);
            Assert.Equal(p.X, back.X, 6);
            Assert.Equal(p.Y, back.Y, 6);
        }
    }

    [Theory]
    [InlineData(90, 1, 0)]   // the shown page's top-left corner is the view's top-right
    [InlineData(180, 1, 1)]  // ... bottom-right
    [InlineData(270, 0, 1)]  // ... bottom-left
    public void TheShownTopLeftCorner_TurnsWithTheView(int rotation, int right, int bottom)
    {
        var layout = Layout(rotation);
        var r = layout.PageRectInView(1);
        var (x, y) = layout.ToViewPoint(1, new ViewerPoint(0, 0));
        Assert.Equal(right == 1 ? r.Right : r.X, x, 6);
        Assert.Equal(bottom == 1 ? r.Bottom : r.Y, y, 6);
        // A rectangle stays a rectangle on the page in view.
        var v = layout.ToView(1, new ViewerRect(0, 0, 612, 792));
        Assert.Equal(r.X, v.X, 6);
        Assert.Equal(r.Y, v.Y, 6);
        Assert.Equal(r.Width, v.Width, 6);
        Assert.Equal(r.Height, v.Height, 6);
    }

    [Fact]
    public void Turning_KeepsThePageInView_AndFullTurnsAreUpright()
    {
        var layout = Layout(0);
        layout.GoTo(3);
        Assert.Equal(3, layout.CurrentPage);
        layout.SetRotation(90);
        Assert.Equal(3, layout.CurrentPage);
        layout.SetRotation(-90);
        Assert.Equal(270, layout.Rotation);
        layout.SetRotation(720);
        Assert.Equal(0, layout.Rotation);
        Assert.Equal(new LayoutRect(10, 10, 612, 792), layout.PageRect(1));
    }
}
