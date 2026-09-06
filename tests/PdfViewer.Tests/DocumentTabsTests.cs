using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers tabbed documents. Each tab is a whole view model with its own document, pages,
/// annotations and unsaved state, so these tests are mostly about the two things that go
/// wrong in tabbed viewers: state leaking between tabs, and work being lost when one closes.
/// </summary>
public class DocumentTabsTests : IDisposable
{
    private readonly string _testDir;

    public DocumentTabsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfTabTests_" + Guid.NewGuid().ToString("N"));
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

    private string MakePdf(string name, int pages = 2) =>
        TestPdfBuilder.CreateSimplePdf(Path.Combine(_testDir, name), pages, "TabToken");

    private static AnnotationModel Highlight() => new()
    {
        PageNumber = 1,
        Type = AnnotationType.Highlight,
        X = 0.1, Y = 0.1, Width = 0.2, Height = 0.05,
        ColorHex = "#FFFF00", Opacity = 0.4
    };

    /// <summary>
    /// The window binds its entire document UI through the active tab, so there is always
    /// one - an empty tab is what the application looked like before tabs existed.
    /// </summary>
    [Fact]
    public void TestThereIsAlwaysATabToBindTo()
    {
        var shell = new ShellViewModel();

        Assert.Single(shell.Documents);
        Assert.NotNull(shell.ActiveDocument);
        Assert.False(shell.ActiveDocument!.IsDocumentLoaded);
        Assert.False(shell.ShowTabStrip);
    }

    [Fact]
    public async Task TestTheFirstDocumentFillsTheEmptyTabRatherThanOpeningAnother()
    {
        var shell = new ShellViewModel();
        await shell.OpenDocumentAsync(MakePdf("first.pdf"));

        Assert.Single(shell.Documents);
        Assert.True(shell.ActiveDocument!.IsDocumentLoaded);
        Assert.False(shell.ShowTabStrip);
    }

    [Fact]
    public async Task TestASecondDocumentOpensBesideTheFirst()
    {
        var shell = new ShellViewModel();
        var first = await shell.OpenDocumentAsync(MakePdf("a.pdf"));
        var second = await shell.OpenDocumentAsync(MakePdf("b.pdf"));

        Assert.Equal(2, shell.Documents.Count);
        Assert.Same(second, shell.ActiveDocument);
        Assert.True(shell.ShowTabStrip);

        // The first is still open and still its own document.
        Assert.NotNull(first);
        Assert.True(first!.IsDocumentLoaded);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task TestOpeningTheSameFileTwiceFocusesTheTabItIsAlreadyIn()
    {
        var shell = new ShellViewModel();
        string path = MakePdf("once.pdf");

        var first = await shell.OpenDocumentAsync(path);
        await shell.OpenDocumentAsync(MakePdf("other.pdf"));
        var again = await shell.OpenDocumentAsync(path);

        Assert.Same(first, again);
        Assert.Equal(2, shell.Documents.Count);
        Assert.Same(first, shell.ActiveDocument);
    }

    /// <summary>
    /// The thing tabbed viewers get wrong. Each document keeps its own everything.
    /// </summary>
    [Fact]
    public async Task TestTabsDoNotShareState()
    {
        var shell = new ShellViewModel();
        var first = await shell.OpenDocumentAsync(MakePdf("one.pdf", pages: 5));
        var second = await shell.OpenDocumentAsync(MakePdf("two.pdf", pages: 3));

        first!.NavigateToPage(4);
        first.ZoomLevel = 2.0;
        first.AddAnnotation(Highlight());
        first.SetViewMode(ViewLayoutMode.Facing);

        Assert.Equal(4, first.CurrentPageNumber);
        Assert.Equal(1, second!.CurrentPageNumber);

        Assert.Equal(2.0, first.ZoomLevel);
        Assert.NotEqual(2.0, second.ZoomLevel);

        Assert.Single(first.AllAnnotations);
        Assert.Empty(second.AllAnnotations);

        Assert.True(first.HasUnsavedChanges);
        Assert.False(second.HasUnsavedChanges);

        Assert.Equal(ViewLayoutMode.Facing, first.ViewMode);
        Assert.Equal(ViewLayoutMode.Continuous, second.ViewMode);

        Assert.Equal(5, first.PageCount);
        Assert.Equal(3, second.PageCount);
    }

    [Fact]
    public async Task TestClosingATabFocusesItsNeighbour()
    {
        var shell = new ShellViewModel();
        var a = await shell.OpenDocumentAsync(MakePdf("a.pdf"));
        var b = await shell.OpenDocumentAsync(MakePdf("b.pdf"));
        var c = await shell.OpenDocumentAsync(MakePdf("c.pdf"));

        shell.ActiveDocument = b;
        Assert.True(await shell.CloseDocumentAsync(b!));

        Assert.Equal(2, shell.Documents.Count);
        Assert.DoesNotContain(b!, shell.Documents);
        Assert.Same(c, shell.ActiveDocument);
        Assert.Contains(a!, shell.Documents);
    }

    /// <summary>
    /// Closing the only document empties its tab instead of removing it, so the window still
    /// has something to bind to and the user lands back on the welcome screen.
    /// </summary>
    [Fact]
    public async Task TestClosingTheLastDocumentLeavesAnEmptyTab()
    {
        var shell = new ShellViewModel();
        var only = await shell.OpenDocumentAsync(MakePdf("only.pdf"));

        Assert.True(await shell.CloseDocumentAsync(only!));

        Assert.Single(shell.Documents);
        Assert.NotNull(shell.ActiveDocument);
        Assert.False(shell.ActiveDocument!.IsDocumentLoaded);
    }

    [Fact]
    public async Task TestClosingATabAsksAboutUnsavedWorkAndCancelKeepsIt()
    {
        var shell = new ShellViewModel();
        var a = await shell.OpenDocumentAsync(MakePdf("a.pdf"));
        await shell.OpenDocumentAsync(MakePdf("b.pdf"));

        a!.AddAnnotation(Highlight());
        a.ConfirmSaveBeforeClosingFunc = _ => null;   // cancel

        Assert.False(await shell.CloseDocumentAsync(a));

        Assert.Contains(a, shell.Documents);
        Assert.True(a.HasUnsavedChanges);
    }

    [Fact]
    public async Task TestClosingTheWindowAsksAboutEveryTabNotJustTheVisibleOne()
    {
        var shell = new ShellViewModel();
        var a = await shell.OpenDocumentAsync(MakePdf("a.pdf"));
        var b = await shell.OpenDocumentAsync(MakePdf("b.pdf"));

        a!.AddAnnotation(Highlight());
        b!.AddAnnotation(Highlight());

        int asked = 0;
        a.ConfirmSaveBeforeClosingFunc = _ => { asked++; return false; };
        b.ConfirmSaveBeforeClosingFunc = _ => { asked++; return false; };

        Assert.True(await shell.ConfirmCloseAllAsync());
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task TestCancellingAnyTabStopsTheWindowClosing()
    {
        var shell = new ShellViewModel();
        var a = await shell.OpenDocumentAsync(MakePdf("a.pdf"));
        var b = await shell.OpenDocumentAsync(MakePdf("b.pdf"));

        a!.AddAnnotation(Highlight());
        b!.AddAnnotation(Highlight());

        a.ConfirmSaveBeforeClosingFunc = _ => false;  // discard
        b.ConfirmSaveBeforeClosingFunc = _ => null;   // cancel

        Assert.False(await shell.ConfirmCloseAllAsync());
    }

    [Fact]
    public async Task TestSwitchingTabsWrapsAround()
    {
        var shell = new ShellViewModel();
        var a = await shell.OpenDocumentAsync(MakePdf("a.pdf"));
        var b = await shell.OpenDocumentAsync(MakePdf("b.pdf"));
        var c = await shell.OpenDocumentAsync(MakePdf("c.pdf"));

        shell.ActiveDocument = a;

        shell.NextDocument();
        Assert.Same(b, shell.ActiveDocument);

        shell.NextDocument();
        Assert.Same(c, shell.ActiveDocument);

        shell.NextDocument();
        Assert.Same(a, shell.ActiveDocument);

        shell.PreviousDocument();
        Assert.Same(c, shell.ActiveDocument);
    }

    /// <summary>
    /// Every open document holds its file in memory and a share of the page cache, so the
    /// budget is re-divided as tabs come and go rather than each helping itself to all of it.
    /// </summary>
    [Fact]
    public async Task TestTheMemoryBudgetIsSharedBetweenOpenDocuments()
    {
        var shell = new ShellViewModel();
        var a = await shell.OpenDocumentAsync(MakePdf("a.pdf"));

        long alone = LruPageCache.ShareOfTotal(1);
        Assert.Equal(LruPageCache.DefaultTotalByteCeiling, alone);

        var b = await shell.OpenDocumentAsync(MakePdf("b.pdf"));
        var c = await shell.OpenDocumentAsync(MakePdf("c.pdf"));

        // Three open documents each get a third, not the whole budget each.
        Assert.Equal(3, shell.Documents.Count);
        Assert.True(LruPageCache.ShareOfTotal(3) < alone);

        // And closing one gives the memory back to those left.
        await shell.CloseDocumentAsync(c!);
        Assert.True(LruPageCache.ShareOfTotal(shell.Documents.Count) > LruPageCache.ShareOfTotal(3));

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public async Task TestAFileThatWillNotOpenDoesNotLeaveAnEmptyTabBehind()
    {
        var shell = new ShellViewModel();
        await shell.OpenDocumentAsync(MakePdf("good.pdf"));

        string broken = Path.Combine(_testDir, "broken.pdf");
        File.WriteAllText(broken, "this is not a pdf");

        var result = await shell.OpenDocumentAsync(broken);

        Assert.Null(result);
        Assert.Single(shell.Documents);
        Assert.True(shell.ActiveDocument!.IsDocumentLoaded);
    }

    [Fact]
    public async Task TestTabTitlesShowTheFileAndItsUnsavedState()
    {
        var shell = new ShellViewModel();
        Assert.Equal("No document", shell.ActiveDocument!.TabTitle);

        var document = await shell.OpenDocumentAsync(MakePdf("named.pdf"));
        Assert.Equal("named.pdf", document!.TabTitle);

        document.AddAnnotation(Highlight());
        Assert.Equal("*named.pdf", document.TabTitle);
    }

    [Fact]
    public async Task TestOpeningPastTheLimitReplacesATabRatherThanGrowingForever()
    {
        var shell = new ShellViewModel();

        for (int i = 0; i < ShellViewModel.MaxOpenDocuments + 3; i++)
        {
            await shell.OpenDocumentAsync(MakePdf($"doc{i}.pdf", pages: 1));
        }

        Assert.True(shell.Documents.Count <= ShellViewModel.MaxOpenDocuments,
            $"Open documents grew to {shell.Documents.Count}.");
    }
}

