using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The Header &amp; Footer, Bates Numbering, Watermark and Background dialogs: their view models
/// build the designs, preview the current page without changing the document, and Add, Update
/// and Remove make a revision that undoes and saves like a content edit.
/// </summary>
public class PageMarksViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PageMarksViewer_" + Guid.NewGuid().ToString("N"));
    public PageMarksViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Document(int pages = 2)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int i = 1; i <= pages; i++)
            b.AddPage($"BT /F1 14 Tf 72 700 Td (Chapter {i}) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, "contract.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    private static async Task<string> Text(byte[] pdf, int page = 1)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, page);
    }

    [Fact]
    public async Task HeaderFooter_Add_IsARevisionThatUndoesAndSaves()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        vm.ShowPageMarkDialogFunc = dialog =>
        {
            var hf = Assert.IsType<HeaderFooterViewModel>(dialog);
            Assert.False(hf.HasExisting);
            Assert.False(hf.UpdateCommand.CanExecute(null));
            hf.SelectedBox = PageMarkPosition.BottomCenter;
            hf.InsertPageNumberCommand.Execute(null);
            hf.TopRight = "<<File>>";
            hf.AddCommand.Execute(null);
            return true;
        };
        await vm.ShowHeaderFooterAsync();
        Assert.True(vm.HasUnsavedChanges);
        string text = await Text(vm.CurrentDocumentBytes()!, 2);
        Assert.Contains("Page 2 of 2", text);
        Assert.Contains("contract.pdf", text);

        await vm.UndoContentEditAsync();
        Assert.DoesNotContain("Page 2 of 2", await Text(vm.CurrentDocumentBytes()!, 2));
        await vm.RedoContentEditAsync();

        await vm.SaveAsync();
        byte[] saved = File.ReadAllBytes(path);
        Assert.Contains("Page 1 of 2", await Text(saved));
        Assert.Equal(original, saved.AsSpan(0, original.Length).ToArray()); // an incremental update
    }

    [Fact]
    public async Task Update_StartsFromTheDocumentsSettings_AndRemoveTakesTheMarksAway()
    {
        var vm = await Open(Document());
        Assert.True(await vm.ApplyPageMarksAsync(new PageMarkRequest(PdfMarkAction.Add, PdfMarkKind.HeaderFooter, new PdfHeaderFooter { TopLeft = "Draft <<1>>" }, null)));

        var dialog = Assert.IsType<HeaderFooterViewModel>(await vm.CreatePageMarkViewModelAsync(PdfMarkKind.HeaderFooter));
        Assert.True(dialog.HasExisting);
        Assert.True(dialog.UpdateCommand.CanExecute(null));
        Assert.Equal("Draft <<1>>", dialog.TopLeft);
        dialog.TopLeft = "Final <<1>>";
        dialog.UpdateCommand.Execute(null);
        Assert.Equal(PdfMarkAction.Update, dialog.Result!.Action);
        Assert.True(await vm.ApplyPageMarksAsync(dialog.Result));
        string text = await Text(vm.CurrentDocumentBytes()!);
        Assert.Contains("Final 1", text);
        Assert.DoesNotContain("Draft", text);

        var again = await vm.CreatePageMarkViewModelAsync(PdfMarkKind.HeaderFooter);
        again!.RemoveCommand.Execute(null);
        Assert.True(await vm.ApplyPageMarksAsync(again.Result!));
        text = await Text(vm.CurrentDocumentBytes()!);
        Assert.DoesNotContain("Final", text);
        Assert.Contains("Chapter 1", text);
    }

    [Fact]
    public async Task Preview_DrawsTheCurrentPage_WithoutChangingTheDocument()
    {
        var vm = await Open(Document());
        byte[] before = vm.CurrentDocumentBytes()!;
        var dialog = Assert.IsType<WatermarkViewModel>(await vm.CreatePageMarkViewModelAsync(PdfMarkKind.Watermark));
        dialog.Text = "SAMPLE";
        await dialog.RefreshPreviewAsync();
        Assert.NotNull(dialog.PreviewImage);
        Assert.Same(before, vm.CurrentDocumentBytes());
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Bates_NumbersTheRange_FromTheDialog()
    {
        var vm = await Open(Document(3));
        var dialog = Assert.IsType<BatesNumberingViewModel>(await vm.CreatePageMarkViewModelAsync(PdfMarkKind.Bates));
        dialog.Prefix = "SMITH";
        dialog.Start = 7;
        dialog.Digits = 4;
        dialog.Position = PageMarkPosition.TopRight;
        Assert.Equal("SMITH0007", dialog.Sample);
        dialog.SomePages = true;
        dialog.RangeFrom = 2;
        dialog.RangeTo = 3;
        dialog.AddCommand.Execute(null);
        var request = dialog.Result!;
        Assert.Equal("<<Bates>>", request.HeaderFooter!.TopRight);
        Assert.Equal(new PdfPageRange(2, 3), request.HeaderFooter.Pages);
        Assert.True(await vm.ApplyPageMarksAsync(request));
        Assert.DoesNotContain("SMITH", await Text(vm.CurrentDocumentBytes()!, 1));
        Assert.Contains("SMITH0007", await Text(vm.CurrentDocumentBytes()!, 2));
        Assert.Contains("SMITH0008", await Text(vm.CurrentDocumentBytes()!, 3));
        Assert.True((await vm.CreatePageMarkViewModelAsync(PdfMarkKind.Bates))!.HasExisting);
        Assert.False((await vm.CreatePageMarkViewModelAsync(PdfMarkKind.HeaderFooter))!.HasExisting);
    }

    [Fact]
    public void Dialogs_SayWhatIsMissing_InsteadOfClosing()
    {
        var hf = new HeaderFooterViewModel(3, 1, false, null, null);
        hf.AddCommand.Execute(null);
        Assert.Null(hf.Result);
        Assert.NotEmpty(hf.Message);

        var wm = new WatermarkViewModel(3, 1, false, null, null) { Text = " " };
        wm.AddCommand.Execute(null);
        Assert.Null(wm.Result);
        wm.Source = PdfMarkSource.Image;
        wm.AddCommand.Execute(null);
        Assert.Null(wm.Result);
        Assert.Contains("picture", wm.Message);
    }

    [Fact]
    public void WatermarkAndBackground_BuildTheirDesigns()
    {
        var picture = new PdfImageContent(2, 2, new byte[12], PdfImageEncoding.Rgb);
        var wm = new WatermarkViewModel(4, 1, false, null, null) { LoadImage = _ => picture };
        wm.Source = PdfMarkSource.Image;
        wm.SourcePath = "logo.png";
        wm.OpacityPercent = 30;
        wm.Behind = true;
        wm.UseRelativeScale = true;
        wm.RelativeScalePercent = 40;
        wm.Subset = PdfPageSubset.Odd;
        wm.AddCommand.Execute(null);
        var design = wm.Result!.Watermark!;
        Assert.Same(picture, design.Image);
        Assert.Equal(0.3, design.Opacity, 3);
        Assert.True(design.Behind);
        Assert.False(wm.OnTop);
        Assert.Equal(0.4, design.RelativeScale!.Value, 3);
        Assert.Equal(new PdfPageRange(1, null, PdfPageSubset.Odd), design.Pages);
        Assert.Equal(PdfMarkKind.Watermark, wm.Result.Kind);

        var bg = new WatermarkViewModel(4, 1, false, null, null, background: true) { FillColor = "#FFEECC" };
        Assert.Equal("Background", bg.Title);
        Assert.True(bg.IsColorSource);
        bg.AddCommand.Execute(null);
        var fill = bg.Result!.Watermark!;
        Assert.True(fill.IsBackground);
        Assert.Equal(PdfMarkKind.Background, bg.Result.Kind);
        Assert.Equal(new[] { 1.0, 0xEE / 255.0, 0xCC / 255.0 }, fill.Color.Select(c => Math.Round(c, 4)), new Tolerance());
    }

    [Fact]
    public async Task Watermark_FromTheDialog_IsDrawnAndRemoved()
    {
        var vm = await Open(Document());
        vm.ShowPageMarkDialogFunc = dialog =>
        {
            var wm = Assert.IsType<WatermarkViewModel>(dialog);
            wm.Text = "CONFIDENTIAL";
            wm.AddCommand.Execute(null);
            return true;
        };
        await vm.ShowWatermarkAsync();
        Assert.Contains("CONFIDENTIAL", await Text(vm.CurrentDocumentBytes()!, 2));
        var dialog = Assert.IsType<WatermarkViewModel>(await vm.CreatePageMarkViewModelAsync(PdfMarkKind.Watermark));
        Assert.Equal("CONFIDENTIAL", dialog.Text);
        dialog.RemoveCommand.Execute(null);
        Assert.True(await vm.ApplyPageMarksAsync(dialog.Result!));
        Assert.DoesNotContain("CONFIDENTIAL", await Text(vm.CurrentDocumentBytes()!, 2));
    }

    private sealed class Tolerance : System.Collections.Generic.IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < 1e-3;
        public int GetHashCode(double d) => 0;
    }
}
