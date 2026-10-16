using System;
using System.IO;
using System.IO.Packaging;
using System.Threading.Tasks;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>File > Export > Word Document: the .docx is written where chosen and opened only when the user says so.</summary>
public class ExportWordViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ExportWord_" + Guid.NewGuid().ToString("N"));
    public ExportWordViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Document()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 14 Tf 72 700 Td (Exported words) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Export_WritesTheDocx_AndOffersToOpenIt(bool open)
    {
        string target = Path.Combine(_dir, "out.docx");
        string? suggested = null, opened = null, asked = null;
        var vm = new MainViewModel
        {
            ShowMessageBoxAction = (_, _, _, _) => { },
            ChooseWordTargetFunc = s => { suggested = s; return target; },
            ConfirmFunc = (message, _) => { asked = message; return open; },
            OpenWithDefaultAppAction = p => opened = p,
        };
        await vm.LoadDocumentAsync(Document());
        await vm.ExportWordAsync();

        Assert.Equal("doc.docx", suggested);
        Assert.True(File.Exists(target));
        using (var package = Package.Open(target, FileMode.Open, FileAccess.Read))
            Assert.True(package.PartExists(new Uri("/word/document.xml", UriKind.Relative)));
        Assert.Contains("Open it now?", asked);
        Assert.Equal(open ? target : null, opened);
        Assert.False(vm.IsExportingWord);
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing()
    {
        string target = Path.Combine(_dir, "none.docx");
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { }, ChooseWordTargetFunc = _ => null };
        await vm.LoadDocumentAsync(Document());
        await vm.ExportWordAsync();
        Assert.False(File.Exists(target));
    }
}
