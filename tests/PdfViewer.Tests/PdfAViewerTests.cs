using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// PDF/A in the viewer: the open document is checked against a chosen part and level, converted
/// to a PDF/A copy (never over the original), and a PDF/A document shows its claim when opened.
/// </summary>
public class PdfAViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PdfAViewer_" + Guid.NewGuid().ToString("N"));
    public PdfAViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Document()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 16 Tf 0.1 0.2 0.6 rg 40 700 Td (Minutes of the meeting) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, "minutes.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    [Fact]
    public async Task Checking_ListsTheRequirementsNotMet()
    {
        var vm = await Open(Document());
        var pdfa = vm.CreatePdfAViewModel()!;
        Assert.Null(pdfa.Claimed);
        Assert.Equal("PDF/A-2b", pdfa.SelectedFlavour);
        await pdfa.CheckAsync();
        Assert.StartsWith("The document does not conform to PDF/A-2b", pdfa.Summary);
        Assert.Contains(pdfa.Issues, i => i.Rule == "6.6.2.1-1");
        Assert.Contains(pdfa.Issues, i => i.Rule == "6.2.11.4.1-1");

        pdfa.SelectedFlavour = "PDF/A-1b";
        Assert.False(pdfa.CanConvert);
        await pdfa.CheckAsync();
        Assert.Contains(pdfa.Issues, i => i.Rule.StartsWith("6.7.2-", StringComparison.Ordinal)); // PDF/A-1's metadata clause
    }

    [Fact]
    public async Task Converting_SavesAPdfACopy_ThatOpensWithItsClaim()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        var pdfa = vm.CreatePdfAViewModel()!;
        Assert.False(await pdfa.ConvertAsync(path)); // never over the original
        Assert.Equal(original, File.ReadAllBytes(path));

        string output = pdfa.SuggestedOutputPath;
        Assert.EndsWith("minutes_PDFA.pdf", output);
        Assert.True(await pdfa.ConvertAsync(output));
        Assert.Contains("passes the PDF/A-2b check", pdfa.Summary);
        Assert.Empty(pdfa.Issues);
        Assert.Contains(pdfa.Changes, c => c.Contains("Embedded", StringComparison.Ordinal));
        Assert.Equal(output, pdfa.ConvertedPath);

        var converted = await Open(output);
        for (int i = 0; i < 100 && converted.PdfAClaim == null; i++) await Task.Delay(20);
        Assert.Equal("PDF/A-2b", converted.PdfAClaim);
        Assert.True(converted.HasPdfAClaim);
        var check = converted.CreatePdfAViewModel()!;
        Assert.Equal("PDF/A-2b", check.SelectedFlavour);
        await check.CheckAsync();
        Assert.Equal("The document conforms to PDF/A-2b.", check.Summary);
    }
}
