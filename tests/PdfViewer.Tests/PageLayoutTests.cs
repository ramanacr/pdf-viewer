using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers the page layout maths. Scrolling to a page, working out which page the viewport is
/// on, and deciding what to render all read one description of where pages sit; before facing
/// mode there were three copies of that loop in the view, and only one of them would have been
/// updated. These tests pin the shared description down.
/// </summary>
public class PageLayoutTests : IDisposable
{
    private readonly string _testDir;

    public PageLayoutTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfLayoutTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private async Task<MainViewModel> LoadedViewModel(int pageCount)
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, $"layout_{Guid.NewGuid():N}.pdf"), pageCount, "LayoutToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    [Fact]
    public async Task TestContinuousLayoutPutsOnePagePerRow()
    {
        var vm = await LoadedViewModel(4);
        vm.SetViewMode(ViewLayoutMode.Continuous);

        var rows = vm.BuildPageRows();

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.PageCount));
        Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(r => r.FirstPageIndex));

        // Rows stack with no gaps and no overlaps.
        for (int i = 1; i < rows.Count; i++)
        {
            Assert.Equal(rows[i - 1].Bottom, rows[i].Top, 3);
        }
        Assert.Equal(0, rows[0].Top);
    }

    [Fact]
    public async Task TestFacingLayoutPairsPagesAfterTheCover()
    {
        var vm = await LoadedViewModel(6);
        vm.SetViewMode(ViewLayoutMode.Facing);
        vm.ShowCoverPageAlone = true;

        var rows = vm.BuildPageRows();

        // Cover alone, then 2-3, 4-5, then 6 left over.
        Assert.Equal(4, rows.Count);
        Assert.Equal((0, 1), (rows[0].FirstPageIndex, rows[0].PageCount));
        Assert.Equal((1, 2), (rows[1].FirstPageIndex, rows[1].PageCount));
        Assert.Equal((3, 2), (rows[2].FirstPageIndex, rows[2].PageCount));
        Assert.Equal((5, 1), (rows[3].FirstPageIndex, rows[3].PageCount));
    }

    [Fact]
    public async Task TestFacingLayoutPairsFromTheFirstPageWhenTheCoverIsNotSeparate()
    {
        var vm = await LoadedViewModel(6);
        vm.SetViewMode(ViewLayoutMode.Facing);
        vm.ShowCoverPageAlone = false;

        var rows = vm.BuildPageRows();

        Assert.Equal(3, rows.Count);
        Assert.Equal((0, 2), (rows[0].FirstPageIndex, rows[0].PageCount));
        Assert.Equal((2, 2), (rows[1].FirstPageIndex, rows[1].PageCount));
        Assert.Equal((4, 2), (rows[2].FirstPageIndex, rows[2].PageCount));
    }

    /// <summary>
    /// Facing mode halves the number of rows, so the same page sits much higher up the
    /// document. Scrolling has to follow that or the viewer jumps to the wrong place.
    /// </summary>
    [Fact]
    public async Task TestPageOffsetFollowsTheLayout()
    {
        var vm = await LoadedViewModel(6);

        vm.SetViewMode(ViewLayoutMode.Continuous);
        double continuousOffsetOfPage5 = vm.GetPageOffset(5);

        vm.SetViewMode(ViewLayoutMode.Facing);
        vm.ShowCoverPageAlone = true;
        double facingOffsetOfPage5 = vm.GetPageOffset(5);

        Assert.True(facingOffsetOfPage5 < continuousOffsetOfPage5,
            "Pairing pages must bring later pages nearer the top, not leave them where they were.");

        // Page 4 and page 5 share a row, so they share an offset.
        Assert.Equal(vm.GetPageOffset(4), vm.GetPageOffset(5), 3);

        // The cover is always at the top.
        Assert.Equal(0, vm.GetPageOffset(1));
    }

    [Fact]
    public async Task TestOffsetAndPageLookupAgreeWithEachOther()
    {
        var vm = await LoadedViewModel(7);

        foreach (var mode in new[] { ViewLayoutMode.Continuous, ViewLayoutMode.Facing })
        {
            vm.SetViewMode(mode);

            foreach (var row in vm.BuildPageRows())
            {
                int firstPage = row.FirstPageIndex + 1;

                // Landing exactly on a row, and anywhere inside it, resolves to that row.
                Assert.Equal(firstPage, vm.GetPageAtOffset(row.Top));
                Assert.Equal(firstPage, vm.GetPageAtOffset(row.Top + (row.Height / 2)));

                // And scrolling to any page in the row lands on the row's top.
                for (int p = firstPage; p < firstPage + row.PageCount; p++)
                {
                    Assert.Equal(row.Top, vm.GetPageOffset(p), 3);
                }
            }
        }
    }

    /// <summary>
    /// An offset off either end has to resolve to a page that exists; the scroll handler feeds
    /// this straight into the page number the user sees.
    /// </summary>
    [Fact]
    public async Task TestOffsetsOutsideTheDocumentClampToRealPages()
    {
        var vm = await LoadedViewModel(5);
        vm.SetViewMode(ViewLayoutMode.Continuous);

        Assert.Equal(1, vm.GetPageAtOffset(-500));
        Assert.Equal(5, vm.GetPageAtOffset(1_000_000));

        // And a page number outside the document does not throw or wander.
        Assert.Equal(0, vm.GetPageOffset(0));
        Assert.Equal(0, vm.GetPageOffset(99));
    }

    /// <summary>
    /// Fitting one page's width while showing two would push the second off screen - the
    /// opposite of what "fit" means.
    /// </summary>
    [Fact]
    public async Task TestFitWidthAccountsForBothPagesWhenFacing()
    {
        var vm = await LoadedViewModel(4);
        vm.GetViewportSizeFunc = () => (1000, 800);

        vm.SetViewMode(ViewLayoutMode.Continuous);
        vm.FitWidth();
        double singleZoom = vm.ZoomLevel;

        vm.SetViewMode(ViewLayoutMode.Facing);
        vm.FitWidth();
        double facingZoom = vm.ZoomLevel;

        Assert.True(facingZoom < singleZoom,
            "Two pages across must be scaled down further than one.");

        // Roughly half, allowing for the gap between the pages.
        Assert.InRange(facingZoom, singleZoom * 0.40, singleZoom * 0.52);
    }

    /// <summary>
    /// The rows the view draws and the rows the scroll maths measures must be the same
    /// grouping. An earlier version let the panel wrap the pages itself, which paired the
    /// cover with page 1 on screen while the maths had the cover standing alone - so every
    /// scroll target after the cover was a row out. This is the assertion that catches that.
    /// </summary>
    [Fact]
    public async Task TestRenderedRowsMatchTheMeasuredRows()
    {
        var vm = await LoadedViewModel(7);

        foreach (var mode in new[] { ViewLayoutMode.Continuous, ViewLayoutMode.Facing })
        {
            vm.SetViewMode(mode);

            foreach (bool coverAlone in new[] { true, false })
            {
                vm.ShowCoverPageAlone = coverAlone;

                var drawn = vm.PageRows;
                var measured = vm.BuildPageRows();

                Assert.Equal(measured.Count, drawn.Count);

                for (int i = 0; i < drawn.Count; i++)
                {
                    Assert.Equal(measured[i].PageCount, drawn[i].Pages.Count);
                    Assert.Equal(measured[i].FirstPageIndex + 1, drawn[i].FirstPageNumber);
                }

                // Every page is drawn exactly once, in order.
                var drawnPageNumbers = drawn.SelectMany(r => r.Pages).Select(p => p.PageNumber).ToList();
                Assert.Equal(Enumerable.Range(1, vm.Pages.Count), drawnPageNumbers);
            }
        }
    }

    [Fact]
    public async Task TestFacingModeDrawsTwoPagesInARow()
    {
        var vm = await LoadedViewModel(5);
        vm.SetViewMode(ViewLayoutMode.Facing);
        vm.ShowCoverPageAlone = true;

        Assert.Equal(2, vm.PagesPerRow);

        // Cover alone, then pairs.
        Assert.Single(vm.PageRows[0].Pages);
        Assert.Equal(1, vm.PageRows[0].Pages[0].PageNumber);
        Assert.Equal(2, vm.PageRows[1].Pages.Count);
        Assert.Equal(new[] { 2, 3 }, vm.PageRows[1].Pages.Select(p => p.PageNumber));

        vm.ShowCoverPageAlone = false;
        Assert.Equal(new[] { 1, 2 }, vm.PageRows[0].Pages.Select(p => p.PageNumber));
    }

    [Fact]
    public async Task TestSinglePageModeIsNotAScrollingLayout()
    {
        var vm = await LoadedViewModel(3);

        vm.SetViewMode(ViewLayoutMode.SinglePage);
        Assert.False(vm.IsMultiPageLayout);

        vm.SetViewMode(ViewLayoutMode.Continuous);
        Assert.True(vm.IsMultiPageLayout);

        vm.SetViewMode(ViewLayoutMode.Facing);
        Assert.True(vm.IsMultiPageLayout);
    }

    [Fact]
    public async Task TestToggleCyclesThroughEveryLayout()
    {
        var vm = await LoadedViewModel(2);
        Assert.Equal(ViewLayoutMode.Continuous, vm.ViewMode);

        vm.ToggleViewMode();
        Assert.Equal(ViewLayoutMode.SinglePage, vm.ViewMode);

        vm.ToggleViewMode();
        Assert.Equal(ViewLayoutMode.Facing, vm.ViewMode);

        vm.ToggleViewMode();
        Assert.Equal(ViewLayoutMode.Continuous, vm.ViewMode);
    }

    [Fact]
    public async Task TestASinglePageDocumentStillLaysOutInFacingMode()
    {
        var vm = await LoadedViewModel(1);
        vm.SetViewMode(ViewLayoutMode.Facing);

        var rows = vm.BuildPageRows();

        Assert.Single(rows);
        Assert.Equal(1, rows[0].PageCount);
        Assert.Equal(1, vm.GetPageAtOffset(0));
    }
}
