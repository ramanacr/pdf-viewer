using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Text;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;
using AnnotationType = PdfViewer.Models.AnnotationType;

namespace PdfViewer.Tests;

public class TextSelectionAndSearchEventTests : IDisposable
{
    private readonly string _testDir;

    public TextSelectionAndSearchEventTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerSearchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        PdfiumNativeBridge.EnsureInitialized();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private string CreateSamplePdf(string name = "sample.pdf", int pageCount = 3)
    {
        string filePath = Path.Combine(_testDir, name);
        TestPdfBuilder.CreateSimplePdf(filePath, pageCount);
        return filePath;
    }

    [Fact]
    public async Task TestSearchQueryAndMatchNavigationUserEvents()
    {
        string pdfPath = CreateSamplePdf("search_nav.pdf", 3);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        // User enters search query and presses Enter / Search button
        vm.SearchQuery = "SearchableToken";
        await vm.ExecuteSearchAsync();

        Assert.NotEmpty(vm.SearchMatches);
        Assert.Equal(1, vm.CurrentSearchMatchIndex);
        Assert.Contains("Match 1 of", vm.SearchSummaryText);

        int totalMatches = vm.SearchMatches.Count;

        // User presses F3 / Next Search Match
        vm.NextSearchMatch();
        Assert.Equal(2, vm.CurrentSearchMatchIndex);

        // User navigates backwards (Shift+F3)
        vm.PreviousSearchMatch();
        Assert.Equal(1, vm.CurrentSearchMatchIndex);

        // Previous from 1 wraps to total
        vm.PreviousSearchMatch();
        Assert.Equal(totalMatches, vm.CurrentSearchMatchIndex);

        // Next wraps back to 1
        vm.NextSearchMatch();
        Assert.Equal(1, vm.CurrentSearchMatchIndex);

        // User clears search query
        vm.SearchQuery = string.Empty;
        await vm.ExecuteSearchAsync();
        Assert.Empty(vm.SearchMatches);
        Assert.Empty(vm.SearchSummaryText);
    }

    [Fact]
    public async Task TestSearchMatchDirectSelectionFromSidebar()
    {
        string pdfPath = CreateSamplePdf("search_sidebar.pdf", 3);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        vm.SearchQuery = "SearchableToken";
        await vm.ExecuteSearchAsync();

        if (vm.SearchMatches.Count >= 2)
        {
            var targetMatch = vm.SearchMatches[1];

            // User clicks second search match item in the sidebar list
            vm.SelectSearchMatch(targetMatch);

            Assert.Equal(2, vm.CurrentSearchMatchIndex);
            Assert.True(targetMatch.IsCurrentMatch);
            Assert.Equal(targetMatch.PageNumber, vm.CurrentPageNumber);
        }
    }

    [Fact]
    public async Task TestTextSelectionAndClearUserEvents()
    {
        string pdfPath = CreateSamplePdf("text_sel.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        var page = vm.Pages[0];

        // Opening the document extracts the page's real text. This test drives the selection
        // logic over known geometry, so replace it rather than selecting over whatever the
        // fixture happens to contain.
        page.TextSegments.Clear();

        var seg1 = new PageTextSegment { Text = "Hello", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.05, SegmentIndex = 0 };
        var seg2 = new PageTextSegment { Text = "World", X = 0.35, Y = 0.1, Width = 0.2, Height = 0.05, SegmentIndex = 1 };
        page.TextSegments.Add(seg1);
        page.TextSegments.Add(seg2);

        // User selects segment range
        page.SelectRange(new Point(0.11, 0.11), new Point(0.36, 0.11));
        vm.UpdateSelectionFromPages();

        Assert.True(vm.HasTextSelection);
        Assert.Contains("Hello", vm.SelectedText);

        // User clears selection
        vm.ClearSelection();

        Assert.False(vm.HasTextSelection);
        Assert.Empty(vm.SelectedText);
        Assert.Empty(page.SelectedSegments);
    }

    [Fact]
    public async Task TestConvertSelectionToHighlightUserEvents()
    {
        string pdfPath = CreateSamplePdf("sel_to_highlight.pdf", 2);
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(pdfPath);

        var page = vm.Pages[0];
        page.TextSegments.Clear();
        page.TextSegments.Add(new PageTextSegment { Text = "Important", X = 0.2, Y = 0.3, Width = 0.3, Height = 0.05, SegmentIndex = 0 });

        page.SelectRange(new Point(0.21, 0.31), new Point(0.4, 0.31));
        vm.UpdateSelectionFromPages();

        Assert.True(vm.HasTextSelection);

        // User right-clicks -> "Highlight Selected Text"
        vm.HighlightSelectedText();

        // Selection should be cleared and a highlight annotation added
        Assert.False(vm.HasTextSelection);
        Assert.NotEmpty(page.AnnotationsOnPage);

        var highlight = page.AnnotationsOnPage.FirstOrDefault(a => a.Type == AnnotationType.Highlight);
        Assert.NotNull(highlight);
        Assert.Equal("Important", highlight.Contents);
    }
}
