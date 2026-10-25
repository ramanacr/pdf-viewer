using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Text;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The status bar shows how many characters are selected. Every way the selection ends must
/// take that count away again: before, the bar kept the old count after a click elsewhere,
/// Esc, a new document, a search or a drawing tool.
/// </summary>
public class SelectionStatusTests : IDisposable
{
    private readonly string _testDir;

    public SelectionStatusTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerSelStatus_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
#if PDFIUM_RENDERER
        PdfiumNativeBridge.EnsureInitialized();
#endif
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    private string Pdf(string name) => TestPdfBuilder.CreateSimplePdf(Path.Combine(_testDir, name), 2);

    /// <summary>An open document whose pages hold known words, so the selection is exact.</summary>
    private async Task<MainViewModel> Open(string name)
    {
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(Pdf(name));
        foreach (var page in vm.Pages)
        {
            // Let the opening extraction finish, then stand in for it with known geometry.
            await page.LoadTextSegmentsAsync(vm.DocumentService);
            page.TextSegments.Clear();
            page.IsTextExtracted = true;
            page.TextSegments.Add(new PageTextSegment { Text = "Hello", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.05, SegmentIndex = 0 });
            page.TextSegments.Add(new PageTextSegment { Text = "World", X = 0.35, Y = 0.1, Width = 0.2, Height = 0.05, SegmentIndex = 1 });
        }
        vm.StatusText = "Loaded";
        return vm;
    }

    /// <summary>Selects page 1's words as a drag does, and checks the bar shows the count.</summary>
    private static void Select(MainViewModel vm)
    {
        var page = vm.Pages[0];
        vm.TextSelection.PointerDown(page, new Point(0.09, 0.12), 1, shift: false, ctrl: false);
        vm.TextSelection.PointerMove(page, new Point(0.56, 0.12));
        Assert.True(vm.HasTextSelection);
        Assert.Contains("character(s) selected", vm.StatusText);
    }

    private static void AssertCountGone(MainViewModel vm, string expected = "Loaded")
    {
        Assert.False(vm.HasTextSelection);
        Assert.DoesNotContain("selected", vm.StatusText);
        Assert.Equal(expected, vm.StatusText);
    }

    [Fact]
    public async Task TestClearSelectionCommandRestoresTheStatus()
    {
        using var vm = await Open("esc.pdf");
        Select(vm);
        vm.ClearSelectionCommand.Execute(null); // Esc and Edit > Deselect All
        AssertCountGone(vm);
    }

    [Fact]
    public async Task TestAClickOnAnEmptyPartOfThePageRestoresTheStatus()
    {
        using var vm = await Open("click.pdf");
        Select(vm);
        vm.TextSelection.PointerDown(vm.Pages[0], new Point(0.5, 0.8), 1, shift: false, ctrl: false);
        AssertCountGone(vm);
    }

    [Fact]
    public async Task TestAClickOnAnotherPageRestoresTheStatus()
    {
        using var vm = await Open("other_page.pdf");
        Select(vm);
        vm.TextSelection.PointerDown(vm.Pages[1], new Point(0.5, 0.5), 1, shift: false, ctrl: false);
        AssertCountGone(vm);
    }

    [Fact]
    public async Task TestASecondSelectionShowsItsOwnCountAndThenTheFirstStatus()
    {
        using var vm = await Open("twice.pdf");
        Select(vm);
        string first = vm.StatusText;
        vm.TextSelection.PointerDown(vm.Pages[0], new Point(0.36, 0.12), 1, shift: false, ctrl: false);
        vm.TextSelection.PointerMove(vm.Pages[0], new Point(0.56, 0.12));
        Assert.Contains("character(s) selected", vm.StatusText);
        Assert.NotEqual(first, vm.StatusText);
        vm.ClearSelection();
        AssertCountGone(vm);
    }

    [Fact]
    public async Task TestANewSearchRestoresTheStatus()
    {
        using var vm = await Open("search.pdf");
        Select(vm);
        vm.SearchQuery = "SearchableToken";
        await vm.ExecuteSearchAsync();
        AssertCountGone(vm);
    }

    [Fact]
    public async Task TestStartingAnAnnotationToolRestoresTheStatus()
    {
        using var vm = await Open("tool.pdf");
        Select(vm);
        vm.ToggleAnnotationTool("Rectangle");
        Assert.NotNull(vm.ActiveAnnotationTool);
        AssertCountGone(vm);
    }

    [Fact]
    public async Task TestHighlightingTheSelectionTakesTheCountAway()
    {
        using var vm = await Open("highlight.pdf");
        Select(vm);
        vm.HighlightSelectedText();
        Assert.False(vm.HasTextSelection);
        Assert.DoesNotContain("character(s) selected", vm.StatusText);
    }

    [Fact]
    public async Task TestClosingTheDocumentTakesTheCountAway()
    {
        using var vm = await Open("close.pdf");
        Select(vm);
        vm.CloseDocument();
        Assert.False(vm.HasTextSelection);
        Assert.DoesNotContain("character(s) selected", vm.StatusText);
    }

    [Fact]
    public async Task TestOpeningAnotherDocumentEndsTheSelection()
    {
        using var vm = await Open("first.pdf");
        Select(vm);
        await vm.LoadDocumentAsync(Pdf("second.pdf"));
        Assert.False(vm.HasTextSelection);
        Assert.Empty(vm.SelectedText);
        Assert.DoesNotContain("character(s) selected", vm.StatusText);
    }

    /// <summary>A status written while the text was selected (Copied...) is newer news: it stays.</summary>
    [Fact]
    public async Task TestAStatusWrittenDuringTheSelectionStays()
    {
        using var vm = await Open("newer.pdf");
        Select(vm);
        vm.StatusText = "Copied 11 character(s) to clipboard";
        vm.ClearSelection();
        AssertCountGone(vm, "Copied 11 character(s) to clipboard");
    }
}
