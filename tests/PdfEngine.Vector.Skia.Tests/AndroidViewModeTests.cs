using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The view modes of the page layout (the Android viewer's ⋮ → View → Page layout, the desktop's
/// view modes): two pages side by side with and without the cover alone, one page at a time fitted
/// to the view with swipes that slide its neighbour in, and every mapping in every mode and rotation.
/// </summary>
public class AndroidViewModeTests
{
    private static ViewerPage[] Pages(int count, double width = 600, double height = 800) =>
        Enumerable.Range(1, count).Select(n => new ViewerPage(n, width, height)).ToArray();

    // Two 600-point pages, the gap and both margins are exactly 1240 pixels: facing fits at 1 pixel per point.
    private static ContinuousPageLayout Layout(PageLayoutMode mode, bool cover = false, double width = 1240, double height = 900, int pages = 5)
    {
        var layout = new ContinuousPageLayout(Pages(pages), gap: 20, margin: 10);
        layout.Resize(width, height);
        layout.SetMode(mode, cover);
        return layout;
    }

    [Fact]
    public void Facing_PutsPagesInRowsOfTwo_FittedToTheWidestRow()
    {
        var layout = Layout(PageLayoutMode.Facing);
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        Assert.Equal(new LayoutRect(10, 10, 600, 800), layout.PageRect(1));
        Assert.Equal(new LayoutRect(630, 10, 600, 800), layout.PageRect(2));
        Assert.Equal(new LayoutRect(10, 830, 600, 800), layout.PageRect(3));
        Assert.Equal(new LayoutRect(630, 830, 600, 800), layout.PageRect(4));
        // The odd last page is a row of its own, centred.
        Assert.Equal(new LayoutRect(320, 1650, 600, 800), layout.PageRect(5));
        Assert.Equal(1650 + 800 + 10, layout.ContentHeight, 9);
        Assert.Equal(1240, layout.ContentWidth, 9);
    }

    [Fact]
    public void FacingWithCover_ShowsPageOneAlone_ThenSpreads()
    {
        var layout = Layout(PageLayoutMode.Facing, cover: true);
        Assert.True(layout.CoverAlone);
        Assert.Equal(new LayoutRect(320, 10, 600, 800), layout.PageRect(1));
        Assert.Equal(new LayoutRect(10, 830, 600, 800), layout.PageRect(2));
        Assert.Equal(new LayoutRect(630, 830, 600, 800), layout.PageRect(3));
        Assert.Equal(new LayoutRect(10, 1650, 600, 800), layout.PageRect(4));
        Assert.Equal(new LayoutRect(630, 1650, 600, 800), layout.PageRect(5));
    }

    [Fact]
    public void CoverAlone_CountsInFacingModeOnly()
    {
        var layout = Layout(PageLayoutMode.Continuous, cover: true);
        Assert.False(layout.CoverAlone);
        Assert.Equal(PageLayoutMode.Continuous, layout.Mode);
        Assert.Equal(1220, layout.PageRect(1).Width, 9);
    }

    [Fact]
    public void Facing_RowIsAsTallAsItsTallerPage_WithTheShorterOneInItsMiddle()
    {
        var pages = new[] { new ViewerPage(1, 600, 800), new ViewerPage(2, 800, 400), new ViewerPage(3, 600, 800) };
        var layout = new ContinuousPageLayout(pages, gap: 20, margin: 10);
        layout.Resize(1440, 900);
        layout.SetMode(PageLayoutMode.Facing);
        // The widest row is 1400 points plus the gap and margins: 1440 pixels.
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        Assert.Equal(new LayoutRect(10, 10, 600, 800), layout.PageRect(1));
        Assert.Equal(new LayoutRect(630, 210, 800, 400), layout.PageRect(2));
        Assert.Equal(new LayoutRect(420, 830, 600, 800), layout.PageRect(3));
    }

    [Fact]
    public void Facing_TellsWhichPageOfARowIsUnderATouch()
    {
        var layout = Layout(PageLayoutMode.Facing);
        layout.ScrollTo(0, 820);
        Assert.Equal((3, new ViewerPoint(90, 40)), layout.PageAt(100, 50));
        Assert.Equal((4, new ViewerPoint(70, 40)), layout.PageAt(700, 50));
        Assert.Null(layout.PageAt(620, 50)); // the gap between the two
        // The gap's nearer side, clamped.
        Assert.Equal(new PageAnchor(4, 0, 50.0 / 800), layout.AnchorAt(622, 60));
        // The indicator: the row in the middle of the view, its first page when the middle is between them.
        Assert.Equal(3, layout.CurrentPage);
        Assert.Equal(new[] { 3, 4, 5 }, layout.VisiblePages().ToArray());
    }

    [Fact]
    public void Facing_ZoomedIn_ShowsAndCountsThePageInView()
    {
        var layout = Layout(PageLayoutMode.Facing);
        layout.ZoomAt(2.5, 0, 0);
        Assert.Equal(2.5, layout.PixelsPerPoint, 9);
        Assert.Equal(new[] { 1 }, layout.VisiblePages().ToArray());
        Assert.Equal(1, layout.CurrentPage);
        // Going to the right page scrolls it into view.
        layout.GoTo(2);
        var r = layout.PageRectInView(2);
        Assert.True(r.X >= 0 && r.X < layout.ViewWidth);
        Assert.Equal(2, layout.CurrentPage);
        Assert.Contains(2, layout.VisiblePages());
        Assert.DoesNotContain(1, layout.VisiblePages());
    }

    [Fact]
    public void Facing_PinchKeepsThePointUnderTheFingers()
    {
        var layout = Layout(PageLayoutMode.Facing);
        layout.ScrollTo(0, 400);
        var before = layout.AnchorAt(900, 300);
        layout.ZoomAt(3, 900, 300);
        var after = layout.AnchorAt(900, 300);
        Assert.Equal(before.Page, after.Page);
        Assert.Equal(before.U, after.U, 6);
        Assert.Equal(before.V, after.V, 6);
    }

    [Fact]
    public void SinglePage_FitsThePageToTheViewsWidthAndHeight()
    {
        // A wide view: the height decides.
        var layout = Layout(PageLayoutMode.SinglePage, width: 1240, height: 820);
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        Assert.Equal(new LayoutRect(320, 10, 600, 800), layout.PageRect(1));
        Assert.Equal(0, layout.MaxScrollX);
        Assert.Equal(0, layout.MaxScrollY);
        // A tall view: the width decides, and the page is in the middle of the height.
        layout.Resize(620, 2000);
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        Assert.Equal(new LayoutRect(10, 600, 600, 800), layout.PageRect(1));
        Assert.Equal(new[] { 1 }, layout.VisiblePages().ToArray());
    }

    [Fact]
    public void SinglePage_FitsEachPageOnItsOwn()
    {
        var pages = new[] { new ViewerPage(1, 600, 800), new ViewerPage(2, 800, 600) };
        var layout = new ContinuousPageLayout(pages, gap: 20, margin: 10);
        layout.Resize(620, 820);
        layout.SetMode(PageLayoutMode.SinglePage);
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        layout.GoTo(2);
        Assert.Equal(600.0 / 800, layout.PixelsPerPoint, 9);
        Assert.Equal(2, layout.CurrentPage);
    }

    [Fact]
    public void SinglePage_TurnsOnePageAtATime()
    {
        var layout = Layout(PageLayoutMode.SinglePage, width: 620, height: 820);
        Assert.Equal(1, layout.CurrentPage);
        Assert.False(layout.Turn(-1));
        Assert.True(layout.Turn(1));
        Assert.Equal(2, layout.CurrentPage);
        layout.GoTo(5);
        Assert.Equal(5, layout.CurrentPage);
        Assert.Equal(new[] { 5 }, layout.VisiblePages().ToArray());
        Assert.False(layout.Turn(1));
        Assert.Equal(5, layout.CurrentPage);
        // Zoomed in, going back shows the previous page from its end, going forward from its start.
        layout.ZoomAt(2, 0, 0);
        Assert.True(layout.Turn(-1));
        Assert.Equal(4, layout.CurrentPage);
        Assert.Equal(2, layout.Zoom);
        Assert.Equal(layout.MaxScrollX, layout.ScrollX);
        Assert.Equal(0, layout.ScrollY);
        Assert.True(layout.Turn(-1, vertical: true));
        Assert.Equal((0.0, layout.MaxScrollY), (layout.ScrollX, layout.ScrollY));
        Assert.True(layout.Turn(1));
        Assert.Equal((0.0, 0.0), (layout.ScrollX, layout.ScrollY));
    }

    [Fact]
    public void SinglePage_ASlideBringsTheNeighbourIn()
    {
        var layout = Layout(PageLayoutMode.SinglePage, width: 620, height: 820);
        layout.GoTo(3);
        layout.SetSlide(-100, 0);
        Assert.Equal(new LayoutRect(-90, 10, 600, 800), layout.PageRectInView(3));
        // The next page waits one gap to the right of the view.
        Assert.Equal(new LayoutRect(620 + 20 + 10 - 100, 10, 600, 800), layout.PageRectInView(4));
        Assert.Equal(new[] { 3, 4 }, layout.VisiblePages().ToArray());
        Assert.Equal(3, layout.CurrentPage);
        // Mappings follow the slide.
        var (x, y) = layout.ToViewPoint(4, new ViewerPoint(5, 5));
        Assert.Equal(new ViewerPoint(5, 5), layout.ToPagePoint(4, x, y));
        Assert.Equal((3, new ViewerPoint(150, 40)), layout.PageAt(60, 50));

        layout.SetSlide(0, 300);
        Assert.Equal(0, layout.SlideX);
        Assert.Equal(new LayoutRect(10, -820 - 20 + 10 + 300, 600, 800), layout.PageRectInView(2));
        Assert.Equal(new[] { 2, 3 }, layout.VisiblePages().ToArray());
        // The slide is along the longer axis only.
        layout.SetSlide(40, -30);
        Assert.Equal((40.0, 0.0), (layout.SlideX, layout.SlideY));
        // Going somewhere ends it.
        layout.GoTo(3);
        Assert.Equal((0.0, 0.0), (layout.SlideX, layout.SlideY));
    }

    [Fact]
    public void SinglePage_NoSlideTowardsASideWithoutAPage()
    {
        var layout = Layout(PageLayoutMode.SinglePage, width: 620, height: 820);
        layout.SetSlide(100, 0);
        Assert.Equal(0, layout.SlideX);
        layout.SetSlide(-100, 0);
        Assert.Equal(-100, layout.SlideX);
        layout.GoTo(5);
        layout.SetSlide(0, -100);
        Assert.Equal(0, layout.SlideY);
        // No slide in the other modes.
        layout.SetMode(PageLayoutMode.Facing);
        layout.SetSlide(-100, 0);
        Assert.Equal(0, layout.SlideX);
    }

    [Fact]
    public void ChangingTheMode_KeepsThePageInView()
    {
        var layout = Layout(PageLayoutMode.Continuous, width: 620, height: 820);
        layout.GoTo(4);
        layout.SetMode(PageLayoutMode.SinglePage);
        Assert.Equal(4, layout.CurrentPage);
        layout.SetMode(PageLayoutMode.Facing, coverAlone: true);
        var shown = layout.VisiblePages().ToArray();
        Assert.Contains(4, shown);
        // Back in one column, a page that was in view starts the view.
        layout.SetMode(PageLayoutMode.Continuous);
        Assert.Equal(PageLayoutMode.Continuous, layout.Mode);
        Assert.Contains(layout.CurrentPage, shown);
        Assert.Equal(1, layout.Zoom);
    }

    public static IEnumerable<object[]> ModesAndTurns() =>
        from mode in new[] { (PageLayoutMode.Continuous, false), (PageLayoutMode.Facing, false), (PageLayoutMode.Facing, true), (PageLayoutMode.SinglePage, false) }
        from rotation in new[] { 0, 90, 180, 270 }
        select new object[] { mode.Item1, mode.Item2, rotation };

    [Theory]
    [MemberData(nameof(ModesAndTurns))]
    public void EveryModeAndTurn_MapsPagePointsToTheViewAndBack(PageLayoutMode mode, bool cover, int rotation)
    {
        var pages = new[] { new ViewerPage(1, 600, 800), new ViewerPage(2, 500, 700), new ViewerPage(3, 800, 600), new ViewerPage(4, 600, 800) };
        var layout = new ContinuousPageLayout(pages, gap: 20, margin: 10);
        layout.Resize(1000, 900);
        layout.SetMode(mode, cover);
        layout.SetRotation(rotation);
        layout.ZoomAt(1.5, 300, 200);
        layout.GoTo(3);
        Assert.Equal(rotation, layout.Rotation);
        Assert.Contains(3, layout.VisiblePages());
        foreach (var p in new[] { new ViewerPoint(1, 1), new ViewerPoint(100, 50), new ViewerPoint(799, 599), new ViewerPoint(300.5, 10.25) })
        {
            var (x, y) = layout.ToViewPoint(3, p);
            var back = layout.ToPagePoint(3, x, y);
            Assert.Equal(p.X, back.X, 6);
            Assert.Equal(p.Y, back.Y, 6);
            // A touch on the page in view finds that page and point.
            if (x >= 0 && x <= layout.ViewWidth && y >= 0 && y <= layout.ViewHeight)
            {
                var at = layout.PageAt(x, y);
                Assert.NotNull(at);
                Assert.Equal(3, at.Value.Page);
                Assert.Equal(p.X, at.Value.Point.X, 6);
                Assert.Equal(p.Y, at.Value.Point.Y, 6);
            }
        }
        // The page's frame is the turned page, and a shown rectangle fills it.
        var r = layout.PageRectInView(3);
        var v = layout.ToView(3, new ViewerRect(0, 0, 800, 600));
        Assert.Equal(r.X, v.X, 6);
        Assert.Equal(r.Y, v.Y, 6);
        Assert.Equal(r.Width, v.Width, 6);
        Assert.Equal(r.Height, v.Height, 6);
        bool sideways = rotation is 90 or 270;
        Assert.Equal((sideways ? 600 : 800) * layout.PixelsPerPoint, r.Width, 6);
    }

    [Theory]
    [MemberData(nameof(ModesAndTurns))]
    public void EveryModeAndTurn_RestoresASavedView(PageLayoutMode mode, bool cover, int rotation)
    {
        ContinuousPageLayout Make()
        {
            var layout = new ContinuousPageLayout(Pages(7), gap: 20, margin: 10);
            layout.Resize(800, 1000);
            layout.SetMode(mode, cover);
            layout.SetRotation(rotation);
            return layout;
        }
        var first = Make();
        first.GoTo(5);
        first.ZoomAt(2.5, 400, 500);
        first.ScrollBy(37, 61);
        var saved = first.AnchorAt(0, 0);
        int page = first.CurrentPage;

        var second = Make();
        second.Restore(saved, first.Zoom);
        Assert.Equal(first.Zoom, second.Zoom);
        Assert.Equal(first.ScrollX, second.ScrollX, 6);
        Assert.Equal(first.ScrollY, second.ScrollY, 6);
        Assert.Equal(page, second.CurrentPage);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void FacingTurnedAQuarter_FitsTheTurnedPagesSideBySide(int rotation)
    {
        var layout = Layout(PageLayoutMode.Facing, width: 1640, height: 900);
        layout.SetRotation(rotation);
        // Two turned 800-point-wide pages, the gap and the margins: 1640 pixels.
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        Assert.Equal(new LayoutRect(10, 10, 800, 600), layout.PageRect(1));
        Assert.Equal(new LayoutRect(830, 10, 800, 600), layout.PageRect(2));
        Assert.Equal(new LayoutRect(10, 630, 800, 600), layout.PageRect(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void SinglePageTurned_FitsTheTurnedPage_AndSlides(int rotation)
    {
        var layout = Layout(PageLayoutMode.SinglePage, width: 820, height: 820);
        layout.SetRotation(rotation);
        Assert.Equal(1, layout.PixelsPerPoint, 9);
        layout.GoTo(2);
        layout.SetSlide(-300, 0);
        Assert.Equal(new[] { 2, 3 }, layout.VisiblePages().ToArray());
        var (x, y) = layout.ToViewPoint(3, new ViewerPoint(20, 30));
        Assert.Equal(new ViewerPoint(20, 30), layout.ToPagePoint(3, x, y));
    }

    [Fact]
    public void SinglePage_ResizeKeepsThePage()
    {
        var layout = Layout(PageLayoutMode.SinglePage, width: 620, height: 820);
        layout.GoTo(4);
        layout.Resize(1200, 700);
        Assert.Equal(4, layout.CurrentPage);
        Assert.Equal(680.0 / 800, layout.PixelsPerPoint, 9);
    }
}
