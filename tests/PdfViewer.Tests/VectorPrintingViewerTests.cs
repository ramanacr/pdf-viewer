using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Printing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using PdfEngine.Vector;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PdfViewer.Tests;

/// <summary>
/// Printing from the viewer: which method a job uses, the page list and sheet layout the print
/// preview builds, and whole jobs through Direct2D's print control to "Microsoft Print to PDF"
/// with the output redirected to a file (skipped when that printer is not installed).
/// </summary>
public class VectorPrintingViewerTests : IDisposable
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VectorPrintingViewerTests_" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public VectorPrintingViewerTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteDocument(string name, int pages, PdfTestEncryption? encryption = null)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int i = 1; i <= pages; i++)
            b.AddPage($"0.8 0.1 0.1 rg 72 600 200 100 re f BT /F1 30 Tf 72 700 Td (Printed page {i}) Tj ET",
                $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build(encryption));
        return path;
    }

    // ------------------------------------------------------------------ settings

    [Theory]
    [InlineData("1-3", 10, new[] { 1, 2, 3 })]
    [InlineData("1-3, 8, 11-13", 12, new[] { 1, 2, 3, 8, 11, 12 })]
    [InlineData("5", 3, new[] { 3 })]
    [InlineData("4-2", 5, new[] { 4, 3, 2 })]
    [InlineData("7-", 9, new[] { 7, 8, 9 })]
    [InlineData("", 3, new[] { 1, 2, 3 })]
    [InlineData("abc", 2, new[] { 1, 2 })]
    public void PageList_ParsesRangesAndLists(string text, int pageCount, int[] expected)
    {
        Assert.Equal(expected, PrintPreviewViewModel.ParsePageList(text, pageCount));
    }

    [Fact]
    public async Task Method_IsVectorUnlessImagesAreAskedForOrRequired()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict);
        await service.OpenDocumentAsync(WriteDocument("plain.pdf", 1));
        Assert.Equal(PrintMethod.Vector, DocumentPrinter.ChooseMethod(service, printAsImage: false));
        Assert.Equal(PrintMethod.Image, DocumentPrinter.ChooseMethod(service, printAsImage: true));
        Assert.Contains("vectors", DocumentPrinter.DescribeMethod(service, false));
    }

    [Fact]
    public async Task LowQualityOnlyDocument_PrintsAsImagesAt150Dpi()
    {
        // Every permission but high-quality printing (bit 12), for a reader without the owner password.
        int lowQuality = unchecked((int)(0xFFFFFFFCu & ~(1u << 11)));
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict);
        await service.OpenDocumentAsync(WriteDocument("low.pdf", 1, new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner", permissions: lowQuality)));
        Assert.True(service.Permissions.CanPrint);
        Assert.False(service.Permissions.CanPrintHighQuality);
        Assert.Equal(PrintMethod.Image, DocumentPrinter.ChooseMethod(service, printAsImage: false));
        Assert.Contains("150 dpi", DocumentPrinter.DescribeMethod(service, false));
    }

    [Theory, MemberData(nameof(TestEngines.Names), MemberType = typeof(TestEngines))]
    public async Task ImagePaginator_PlacesPagesByTheSameLayout(string engine)
    {
        using var service = TestEngines.CreateService(engine);
        await service.OpenDocumentAsync(WriteDocument("paginate.pdf", 2));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var printable = new PrintRect(48, 48, 720, 960);
                var paginator = new PageImagePaginator(service, new[] { 2 }, 816, 1056, printable, PrintScaling.Custom, 50, 0);
                Assert.Equal(1, paginator.PageCount);
                using var page = paginator.GetPage(0);
                Assert.Equal(new System.Windows.Size(816, 1056), page.Size);
                var bounds = ((DrawingVisual)page.Visual).ContentBounds;
                var expected = PrintLayout.Place(612, 792, printable, PrintScaling.Custom, 50);
                Assert.Equal(expected.X, bounds.X, 3);
                Assert.Equal(expected.Y, bounds.Y, 3);
                Assert.Equal(expected.Width, bounds.Width, 3);
                Assert.Equal(expected.Height, bounds.Height, 3);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    // ------------------------------------------------------------------ whole jobs

    private static PrintQueue? FindQueue(string name)
    {
        try
        {
            var server = new LocalPrintServer();
            return server.GetPrintQueues().FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A job for <paramref name="queue"/>; print queues belong to their thread, so it is found right before use.</summary>
    private PrintRequest Request(PrintQueue queue, int[] pages, string output) => new()
    {
        Queue = queue,
        Ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket,
        Pages = pages,
        JobName = "Viewer vector print test",
        OutputPath = output,
    };

    private static byte[] WaitForPdf(string path)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var bytes = new byte[file.Length];
                file.ReadExactly(bytes);
                if (bytes.AsSpan(Math.Max(0, bytes.Length - 32)).IndexOf("%%EOF"u8) >= 0)
                    return bytes;
            }
            catch (IOException)
            {
                // the spooler is still writing it
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException($"{path} was not written.");
    }

    private async Task AssertPrintedAsync(string output, params int[] sourcePages)
    {
        byte[] printed = WaitForPdf(output);
        using var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(printed);
        Assert.Equal(sourcePages.Length, doc.PageCount);
        for (int i = 0; i < sourcePages.Length; i++)
        {
            string text = await NativeReadBack.TextAsync(printed, i + 1);
            _output.WriteLine($"output page {i + 1}: {text.Trim()}");
            Assert.Contains($"Printed page {sourcePages[i]}", text);
        }
    }

    [Fact]
    public async Task VectorJob_PrintsTheChosenPagesInOrderAsText()
    {
        if (FindQueue(PdfPrinter) is null)
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict);
        await service.OpenDocumentAsync(WriteDocument("job.pdf", 3));
        string output = Path.Combine(_dir, "job-out.pdf");
        var reports = new System.Collections.Concurrent.ConcurrentBag<PrintProgress>();
        var outcome = await DocumentPrinter.PrintAsync(service, Request(FindQueue(PdfPrinter)!, new[] { 3, 1 }, output), new SyncProgress(reports.Add));
        Assert.Equal(PrintMethod.Vector, outcome.Method);
        Assert.Equal(2, outcome.VectorPages);
        Assert.Equal(0, outcome.ImagePages);
        Assert.Contains(reports, p => p.Done == 2 && p.Total == 2);
        await AssertPrintedAsync(output, 3, 1);
    }

#if PDFIUM_RENDERER
    // PDFium mode exists only in the developer build.
    [Fact]
    public async Task PagesThePdfiumEngineRenders_GoOutAsImagesInTheSameJob()
    {
        if (FindQueue(PdfPrinter) is null)
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Pdfium);
        await service.OpenDocumentAsync(WriteDocument("pdfium.pdf", 2));
        string output = Path.Combine(_dir, "pdfium-out.pdf");
        var outcome = await DocumentPrinter.PrintAsync(service, Request(FindQueue(PdfPrinter)!, new[] { 1, 2 }, output));
        Assert.Equal(PrintMethod.Vector, outcome.Method);
        Assert.Equal(0, outcome.VectorPages);
        Assert.Equal(2, outcome.ImagePages);

        byte[] printed = WaitForPdf(output);
        using var engine = new PdfEngine.Pdfium.PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(printed);
        Assert.Equal(2, doc.PageCount);
    }
#endif

    [Fact]
    public async Task CancelledJob_StopsWithoutPrinting()
    {
        if (FindQueue(PdfPrinter) is null)
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict);
        await service.OpenDocumentAsync(WriteDocument("cancel.pdf", 2));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentPrinter.PrintAsync(service, Request(FindQueue(PdfPrinter)!, new[] { 1, 2 }, Path.Combine(_dir, "cancel-out.pdf")), null, cts.Token));
    }

    [Fact]
    public async Task ImageJob_IsNeverRedirected_SoATestCannotReachThePrinterPort()
    {
        if (FindQueue(PdfPrinter) is null)
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict);
        await service.OpenDocumentAsync(WriteDocument("image.pdf", 1));
        var request = Request(FindQueue(PdfPrinter)!, new[] { 1 }, Path.Combine(_dir, "image-out.pdf")) with { PrintAsImage = true };
        await Assert.ThrowsAsync<NotSupportedException>(() => DocumentPrinter.PrintAsync(service, request));
    }

    [Fact]
    public async Task MainViewModel_PrintsInTheBackgroundAndReportsInTheStatusBar()
    {
        if (FindQueue(PdfPrinter) is null)
        {
            _output.WriteLine($"{PdfPrinter} is not installed: skipped.");
            return;
        }
        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(WriteDocument("vm.pdf", 2));
        string output = Path.Combine(_dir, "vm-out.pdf");
        Assert.False(vm.CancelPrintCommand.CanExecute(null));
        var outcome = await vm.PrintAsync(Request(FindQueue(PdfPrinter)!, new[] { 2 }, output));
        Assert.NotNull(outcome);
        Assert.False(vm.IsPrinting);
        Assert.Equal("Print job sent to printer.", vm.StatusText);
        await AssertPrintedAsync(output, 2);
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<PrintProgress> report) : IProgress<PrintProgress>
    {
        public void Report(PrintProgress value) => report(value);
    }
}
