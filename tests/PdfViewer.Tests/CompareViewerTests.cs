using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfViewer.Core.Comparison;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>Comparing two versions of a document side by side: word-level changes, on the right pages, highlighted where the words are.</summary>
public class CompareViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CompareViewer_" + Guid.NewGuid().ToString("N"));
    public CompareViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Pdf(string name, string page1, string page2, int rotate2 = 0)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage($"BT /F1 12 Tf 50 700 Td ({page1}) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        b.AddPage($"BT /F1 12 Tf 50 700 Td ({page2}) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]",
            extra: rotate2 != 0 ? $"/Rotate {rotate2}" : string.Empty);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public async Task Versions_AreComparedWordByWord_WithHighlightsWhereTheWordsAre()
    {
        string v1 = Pdf("v1.pdf", "Payment is due within 30 days", "Signed by the Buyer");
        string v2 = Pdf("v2.pdf", "Full payment is due within 45 days", "Signed by the Buyer and the Seller");
        using var vm = new CompareViewModel();
        await vm.LoadAsync(v1, v2);

        Assert.Equal(2, vm.OldPages.Count);
        Assert.Equal(2, vm.NewPages.Count);
        var kinds = vm.Changes.Select(c => (c.Change.Kind, c.Change.OldText, c.Change.NewText)).ToList();
        Assert.Contains((ChangeKind.Replaced, "Payment", "Full payment"), kinds);
        Assert.Contains((ChangeKind.Replaced, "30", "45"), kinds);
        Assert.Contains((ChangeKind.Inserted, "", "and the Seller"), kinds);
        Assert.StartsWith("3 difference(s)", vm.Summary);

        // "45" on page 1 of the new version, highlighted at its place (about 50 pt + the width of the words before it).
        var h45 = vm.NewPages[0].Highlights.Single(h => vm.Changes[h.ChangeIndex].Change.NewText == "45");
        Assert.InRange(h45.Bounds.X, 0.28, 0.40);
        Assert.InRange(h45.Bounds.Y, 0.09, 0.13);
        Assert.Equal(3, vm.NewPages[1].Highlights.Count);   // "and the Seller"
        Assert.Empty(vm.OldPages[1].Highlights);             // nothing removed on page 2
    }

    [Fact]
    public async Task Punctuation_IsItsOwnToken()
    {
        string a = Pdf("p1.pdf", "Support by email.", "x");
        string b = Pdf("p2.pdf", "Support by email and phone.", "x");
        using var vm = new CompareViewModel();
        await vm.LoadAsync(a, b);
        var change = Assert.Single(vm.Changes).Change;
        Assert.Equal((ChangeKind.Inserted, "and phone"), (change.Kind, change.NewText));
    }

    [Fact]
    public async Task IdenticalDocuments_SayTheTextIsTheSame()
    {
        string a = Pdf("a.pdf", "Same text", "Also the same");
        string b = Pdf("b.pdf", "Same text", "Also the same");
        using var vm = new CompareViewModel();
        await vm.LoadAsync(a, b);
        Assert.Empty(vm.Changes);
        Assert.StartsWith("The text is the same", vm.Summary);
    }

    [Fact]
    public async Task IgnoreCase_Recompares()
    {
        string a = Pdf("case1.pdf", "Hello World", "x");
        string b = Pdf("case2.pdf", "hello world", "x");
        using var vm = new CompareViewModel();
        await vm.LoadAsync(a, b);
        Assert.Single(vm.Changes);
        vm.IgnoreCase = true;
        for (int i = 0; i < 50 && vm.Changes.Count > 0; i++) await Task.Delay(50);
        Assert.Empty(vm.Changes);
    }

    [Fact]
    public void Highlights_FollowThePagesRotation()
    {
        var page = new ComparePageViewModel(new PdfViewer.Services.PdfiumDocumentService(), 1, 792, 612, rotation: 90);
        var r = page.ToDisplay(new System.Windows.Rect(0.1, 0.2, 0.3, 0.05)); // unrotated, top-left
        Assert.Equal(new System.Windows.Rect(1 - 0.2 - 0.05, 0.1, 0.05, 0.3), r);
    }
}
