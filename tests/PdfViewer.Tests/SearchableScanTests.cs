using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Ocr;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Making scans searchable in the viewer: only pages without text are recognized, their words
/// become searchable at once, the change is a revision saved incrementally, and stopping midway
/// changes nothing.
/// </summary>
public class SearchableScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SearchableScan_" + Guid.NewGuid().ToString("N"));
    public SearchableScanTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A recognizer that "reads" a fixed word on each page it is asked about.</summary>
    private sealed class FakeOcr : IOcrEngine
    {
        public List<int> Pages { get; } = new();
        public Func<int, Task>? Before { get; init; }
        public string EngineName => "fake";
        public IReadOnlyList<string> SupportedLanguages => new[] { "en" };
        public async ValueTask<OcrPageResult> RecognizePageAsync(IPdfDocument document, int pageNumber, string language = "en", CancellationToken ct = default)
        {
            Pages.Add(pageNumber);
            if (Before != null) await Before(pageNumber);
            ct.ThrowIfCancellationRequested();
            return new OcrPageResult
            {
                PageNumber = pageNumber,
                UsedOpticalRecognition = true,
                Words = new[] { new OcrWord { Text = $"Scanned{pageNumber}", Bounds = new PdfRect(0.1, 0.1, 0.3, 0.03) } },
            };
        }
    }

    /// <summary>Page 1 has real text; pages 2 and 3 are images only.</summary>
    private string Document()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var px = new byte[16 * 16];
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8", px);
        b.AddPage("BT /F1 14 Tf 40 700 Td (Typed page) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        b.AddPage("q 612 0 0 792 0 0 cm /Im0 Do Q", $"<< /XObject << /Im0 {image} 0 R >> >>", mediaBox: "[0 0 612 792]");
        b.AddPage("q 612 0 0 792 0 0 cm /Im0 Do Q", $"<< /XObject << /Im0 {image} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public async Task ScannedPages_BecomeSearchable_AndSaveIncrementally()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        var ocr = new FakeOcr();
        vm.OcrEngineOverride = ocr;

        await vm.MakeSearchableAsync();
        Assert.Equal(new[] { 2, 3 }, ocr.Pages); // the typed page is left alone
        Assert.True(vm.HasUnsavedChanges);

        vm.SearchQuery = "Scanned3";
        await vm.ExecuteSearchAsync();
        var hit = Assert.Single(vm.SearchMatches);
        Assert.Equal(3, hit.PageNumber);
        Assert.InRange(hit.X, 0.09, 0.11);

        await vm.SaveAsync();
        byte[] saved = File.ReadAllBytes(path);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original), "saved as an incremental update");
        var reopened = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await reopened.LoadDocumentAsync(path);
        reopened.SearchQuery = "Scanned2";
        await reopened.ExecuteSearchAsync();
        Assert.Single(reopened.SearchMatches);
    }

    [Fact]
    public async Task Stopping_ChangesNothing()
    {
        string path = Document();
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        vm.OcrEngineOverride = new FakeOcr { Before = async page => { if (page == 3) await vm.MakeSearchableAsync(); } }; // "run again to stop"
        await vm.MakeSearchableAsync();
        Assert.False(vm.HasUnsavedChanges);
        Assert.False(vm.IsMakingSearchable);
        Assert.Contains("stopped", vm.StatusText);
    }

    [Fact]
    public async Task ADocumentWithText_NeedsNothing()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 14 Tf 40 700 Td (All typed) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>");
        string path = Path.Combine(_dir, "typed.pdf");
        File.WriteAllBytes(path, b.Build());
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        var ocr = new FakeOcr();
        vm.OcrEngineOverride = ocr;
        await vm.MakeSearchableAsync();
        Assert.Empty(ocr.Pages);
        Assert.False(vm.HasUnsavedChanges);
    }
}
