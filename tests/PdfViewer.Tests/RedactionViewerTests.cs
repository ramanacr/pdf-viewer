using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Redaction in the viewer: text, areas and every search match are marked, reviewed on the page
/// and applied into a new copy that is then opened. The content under the marks is gone from
/// that copy, and the original file is untouched.
/// </summary>
public class RedactionViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RedactionViewer_" + Guid.NewGuid().ToString("N"));
    public RedactionViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Document()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 14 Tf 40 700 Td (Client: Jane Roe, account 4411) Tj 0 -30 Td (Jane Roe signed on Monday.) Tj ET " +
                  "0.8 0.1 0.1 rg 40 500 200 80 re f", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]", flate: true);
        b.AddPage("BT /F1 14 Tf 40 700 Td (Page two mentions Jane Roe too.) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]", flate: true);
        string path = Path.Combine(_dir, "case.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    private static async Task<string> Text(string path)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(path);
        var sb = new StringBuilder();
        for (int p = 1; p <= doc.PageCount; p++) sb.Append(await engine.TextService.ExtractPageTextAsync(doc, p)).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public async Task MarkingEverySearchMatch_RemovesTheNameThroughoutTheDocument()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        vm.SearchQuery = "Jane Roe";
        await vm.ExecuteSearchAsync();
        Assert.Equal(3, vm.SearchMatches.Count);
        vm.MarkSearchResultsForRedactionCommand.Execute(null);
        Assert.Equal(3, vm.RedactionMarkCount);

        string output = Path.Combine(_dir, "case_redacted.pdf");
        var result = await vm.ApplyRedactionsToAsync(new RedactionApplyOptions(output, "Redacted", RemoveMetadata: true));
        Assert.NotNull(result);
        Assert.True(result!.GlyphsRemoved >= 24);

        string text = await Text(output);
        Assert.DoesNotContain("Jane", text);
        Assert.DoesNotContain("Roe", text);
        Assert.Contains("account 4411", text);
        Assert.Contains("signed on Monday", text);
        Assert.Contains("Redacted", text); // the label drawn in each box
        Assert.Equal(original, File.ReadAllBytes(path));   // the original is untouched
        Assert.Equal(output, vm.Metadata!.FilePath);       // the redacted copy is what is open now
        Assert.Equal(0, vm.RedactionMarkCount);
    }

    [Fact]
    public async Task AnArea_RemovesWhateverIsUnderIt()
    {
        string path = Document();
        var vm = await Open(path);
        // The red box (40..240 x 500..580 on a 612 x 792 page), in normalized top-left coordinates.
        vm.AddRedactionMark(1, new Rect(30 / 612.0, 1 - 590 / 792.0, 230 / 612.0, 100 / 792.0));
        Assert.Equal(1, vm.RedactionMarkCount);
        vm.RemoveRedactionMark(1, vm.Pages[0].RedactionMarks[0]);
        Assert.Equal(0, vm.RedactionMarkCount);
        vm.AddRedactionMark(1, new Rect(30 / 612.0, 1 - 590 / 792.0, 230 / 612.0, 100 / 792.0));
        string output = Path.Combine(_dir, "area.pdf");
        var result = await vm.ApplyRedactionsToAsync(new RedactionApplyOptions(output, null, false));
        Assert.Equal(1, result!.PathsCut);
    }

    [Fact]
    public async Task TheOriginal_IsNeverOverwritten()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        string? alert = null;
        vm.ShowMessageBoxAction = (m, _, _, _) => alert = m;
        vm.AddRedactionMark(1, new Rect(0.1, 0.1, 0.2, 0.05));
        vm.ShowApplyRedactionsFunc = (_, _) => new RedactionApplyOptions(path, null, false);
        await vm.ApplyRedactionsCommand.ExecuteAsync(null);
        Assert.Contains("new name", alert);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(1, vm.RedactionMarkCount);
    }

    [Fact]
    public async Task MarkingModes_AreExclusive_AndApplyingNeedsMarks()
    {
        var vm = await Open(Document());
        string? alert = null;
        vm.ShowMessageBoxAction = (m, _, _, _) => alert = m;
        await vm.ApplyRedactionsCommand.ExecuteAsync(null);
        Assert.Contains("Mark something", alert);
        vm.BeginSignatureCommand.Execute(null);
        vm.BeginMarkRedactionAreaCommand.Execute(null);
        Assert.True(vm.IsMarkingRedaction);
        Assert.False(vm.IsPlacingSignature);
        vm.ActiveAnnotationTool = PdfViewer.Models.AnnotationType.Rectangle;
        vm.BeginMarkRedactionAreaCommand.Execute(null);
        Assert.Null(vm.ActiveAnnotationTool);
    }

    [Fact]
    public async Task MarksLayer_ShowsEachMark()
    {
        var vm = await Open(Document());
        vm.AddRedactionMark(1, new Rect(0.1, 0.2, 0.3, 0.05));
        vm.AddRedactionMark(1, new Rect(0.5, 0.6, 0.1, 0.1));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var layer = new RedactionMarksLayer { Page = vm.Pages[0] };
                Assert.Equal(2, layer.Children.Count);
                Assert.All(layer.Children.OfType<System.Windows.Shapes.Rectangle>(),
                    r => Assert.Equal("Marked for redaction", System.Windows.Automation.AutomationProperties.GetName(r)));
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null) throw error;
    }
}
