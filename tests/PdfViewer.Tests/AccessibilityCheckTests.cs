using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using PdfViewer.Views.Dialogs;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Tools > Accessibility Check: the open document's PDF/UA issues are listed by category, each with
/// what is wrong and how to fix it, and selecting one on a page shows that page with the element
/// highlighted.
/// </summary>
public class AccessibilityCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AccessibilityCheck_" + Guid.NewGuid().ToString("N"));
    public AccessibilityCheckTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Xmp =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">" +
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Report</rdf:li></rdf:Alt></dc:title><pdfuaid:part>1</pdfuaid:part></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private string Save(byte[] pdf, string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    /// <summary>Two tagged pages, each with a figure; the second figure has alternate text only when <paramref name="secondAlt"/>.</summary>
    private string Tagged(bool secondAlt)
    {
        var b = new VectorPdfBuilder();
        const string figure = "/Figure <</MCID 0>> BDC 0 0 1 rg 100 40 50 60 re f EMC";
        int p1 = b.AddPage(figure, extra: "/StructParents 0 /Tabs /S");
        int p2 = b.AddPage(figure, extra: "/StructParents 1 /Tabs /S");
        int f1 = b.Add($"<< /Type /StructElem /S /Figure /Pg {p1} 0 R /K 0 /Alt (A blue bar) >>");
        int f2 = b.Add($"<< /Type /StructElem /S /Figure /Pg {p2} 0 R /K 0 {(secondAlt ? "/Alt (Another blue bar)" : "")} >>");
        int doc = b.Add($"<< /Type /StructElem /S /Document /K [{f1} 0 R {f2} 0 R] >>");
        int root = b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R >>");
        int meta = b.AddStream("/Type /Metadata /Subtype /XML", Xmp);
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R /Lang (en-GB) /ViewerPreferences << /DisplayDocTitle true >> /Metadata {meta} 0 R");
        return Save(b.Build(), secondAlt ? "accessible.pdf" : "figure.pdf");
    }

    private string Untagged()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 16 Tf 40 100 Td (Quarterly figures) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
        return Save(b.Build(), "untagged.pdf");
    }

    private static async Task<(MainViewModel Main, AccessibilityCheckViewModel Check)> Open(string path)
    {
        var main = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await main.LoadDocumentAsync(path);
        var check = main.CreateAccessibilityCheckViewModel()!;
        await check.CheckAsync();
        return (main, check);
    }

    [Fact]
    public async Task UntaggedDocument_FailsWithTheIssuesGroupedByCategory()
    {
        var (main, check) = await Open(Untagged());
        using var mainScope = main;
        Assert.False(check.IsCompliant);
        Assert.StartsWith("The document fails the accessibility check", check.Summary);
        Assert.Equal("The document does not claim PDF/UA conformance.", check.ClaimText);
        Assert.Equal(new[] { "Document", "Page content", "Structure", "Alternate text", "Tables", "Lists", "Headings", "Fonts", "Annotations", "Forms" },
            check.Groups.Select(g => g.Name));
        var document = check.Groups.Single(g => g.Category == PdfUaCategory.Document);
        Assert.Contains(document.Issues, i => i.Checkpoint == "06-001");
        Assert.True(document.IsExpanded);
        Assert.Contains(check.Groups.Single(g => g.Category == PdfUaCategory.Fonts).Issues, i => i.Checkpoint == "31-008");
        Assert.Equal("Tables: passed", check.Groups.Single(g => g.Category == PdfUaCategory.Tables).Header);
        // Every issue says what is wrong, how to fix it and where, and a screen reader hears all of it.
        Assert.All(check.AllIssues, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Message));
            Assert.False(string.IsNullOrWhiteSpace(i.Fix));
            Assert.Contains(i.Message, i.AutomationName);
            Assert.Contains(i.Location, i.AutomationName);
        });
        Assert.DoesNotContain(check.AllIssues, i => i.Message.Contains("Quarterly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectingAnIssue_GoesToItsPageAndHighlightsTheElement()
    {
        var (main, check) = await Open(Tagged(secondAlt: false));
        using var mainScope = main;
        var figure = Assert.Single(check.Groups.Single(g => g.Category == PdfUaCategory.AlternateText).Issues);
        Assert.Equal("13-004", figure.Checkpoint);
        Assert.Equal(2, figure.Page);
        Assert.Contains("Document > Figure[2]", figure.Location);
        Assert.Equal(1, main.CurrentPageNumber);

        check.SelectedIssue = figure;
        Assert.Equal(2, main.CurrentPageNumber);
        var highlight = Assert.Single(main.Pages[1].MatchesOnPage);
        Assert.True(highlight.IsCurrentMatch);
        // The bar is 100..150 across and 40..100 up a 200 point page: from the top-left, x 0.5 and y 0.5.
        Assert.Equal(0.5, highlight.X, 3);
        Assert.Equal(0.5, highlight.Y, 3);
        Assert.Equal(0.25, highlight.Width, 3);
        Assert.Equal(0.3, highlight.Height, 3);
        Assert.True(main.HasAccessibilityHighlight(2));

        // Closing the window takes the highlight away.
        main.ClearAccessibilityHighlight();
        Assert.Empty(main.Pages[1].MatchesOnPage);
    }

    [Fact]
    public async Task Window_IsNavigableAndNamedForScreenReaders()
    {
        using var main = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await main.LoadDocumentAsync(Tagged(secondAlt: false));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var check = main.CreateAccessibilityCheckViewModel()!;
                bool closed = false;
                var window = new AccessibilityCheckWindow(check, () => closed = true) { Left = -10000, ShowActivated = false };
                window.Show();
                // The window checks when it loads; let the dispatcher run until the result is in.
                var until = DateTime.UtcNow.AddSeconds(30);
                while ((check.IsBusy || check.Groups.Count == 0) && DateTime.UtcNow < until)
                {
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(10);
                }
                window.UpdateLayout();
                var tree = (TreeView)window.FindName("IssueTree");
                Assert.Equal("Accessibility issues by category", AutomationProperties.GetName(tree));
                var summary = (TextBlock)window.FindName("SummaryText");
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(summary));
                var groups = Enumerable.Range(0, check.Groups.Count).Select(i => (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(i)).ToList();
                Assert.Equal(check.Groups.Select(g => g.Header), groups.Select(AutomationProperties.GetName));
                Assert.All(groups, g => Assert.True(g.Focusable));

                // Selecting the figure's issue in the tree goes to its page.
                int index = check.Groups.ToList().FindIndex(g => g.Category == PdfUaCategory.AlternateText);
                groups[index].IsExpanded = true;
                window.UpdateLayout();
                var item = (TreeViewItem)groups[index].ItemContainerGenerator.ContainerFromIndex(0);
                Assert.StartsWith("Failed: A figure has no alternate text", AutomationProperties.GetName(item));
                item.IsSelected = true;
                Assert.Equal("13-004", check.SelectedIssue?.Checkpoint);
                Assert.Equal(2, main.CurrentPageNumber);
                Assert.True(main.HasAccessibilityHighlight(2));
                window.Close();
                Assert.True(closed);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "window test timed out");
        if (error != null) throw error;
    }

    [Fact]
    public async Task AccessibleDocument_PassesEveryCategory()
    {
        var (main, check) = await Open(Tagged(secondAlt: true));
        using var mainScope = main;
        Assert.True(check.IsCompliant, string.Join("; ", check.AllIssues.Select(i => i.Checkpoint + " " + i.Message)));
        Assert.StartsWith("No accessibility failures found", check.Summary);
        Assert.Equal("The document claims PDF/UA-1 conformance.", check.ClaimText);
        Assert.All(check.Groups, g => Assert.EndsWith(": passed", g.AutomationName));
        Assert.Null(check.SelectedIssue);
        Assert.False(check.HasSelection);
        Assert.True(check.CanCheck);
        main.ShowAccessibilityCheck(); // without a window it just checks
    }
}
