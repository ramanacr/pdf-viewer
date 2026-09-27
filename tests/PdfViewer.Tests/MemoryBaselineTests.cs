using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using Xunit;
using Xunit.Abstractions;

namespace PdfViewer.Tests;

/// <summary>
/// Memory baselines (backlog A1): rendering hundreds of pages, and opening and closing
/// documents again and again, must reach a plateau. Native memory (PDFium documents and form
/// environments, Direct2D surfaces, decoded images) that is not released grows with every
/// page or document, which these catch; the numbers are written to the test output as the
/// baseline.
/// </summary>
public class MemoryBaselineTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MemoryBaseline_" + Guid.NewGuid().ToString("N"));

    public MemoryBaselineTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static long PrivateBytes()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.PrivateMemorySize64;
    }

    private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

    /// <summary>A long document: text, filled and stroked vector art, and an image on every page.</summary>
    private string LongDocument(int pages)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pixels = new byte[64 * 64 * 3];
        new Random(7).NextBytes(pixels);
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels, flate: true);
        for (int i = 0; i < pages; i++)
        {
            var content = new StringBuilder();
            content.Append($"BT /F1 14 Tf 40 760 Td (Page {i + 1} of a long document) Tj ET\n");
            for (int line = 0; line < 30; line++)
                content.Append($"BT /F1 9 Tf 40 {720 - line * 18} Td (Line {line} with enough words to wrap a little across the page width) Tj ET\n");
            content.Append("0.2 0.4 0.8 rg 40 60 200 120 re f 0 0 0 RG 2 w 300 60 m 560 180 l 400 300 l h S\n");
            content.Append("q 120 0 0 120 420 600 cm /Im0 Do Q\n");
            b.AddPage(content.ToString(), $"<< /Font << /F1 {font} 0 R >> /XObject << /Im0 {image} 0 R >> >>", mediaBox: "[0 0 612 792]", flate: true);
        }
        string path = Path.Combine(_dir, $"long-{pages}.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public async Task RenderingEveryPage_ReachesAPlateau()
    {
        string path = LongDocument(150);
        using var service = new HybridVectorDocumentService(PdfViewer.Core.Security.PdfSecurityPolicy.DefaultStrict);
        long start = PrivateBytes();
        var watch = Stopwatch.StartNew();
        await service.OpenDocumentAsync(path);
        long opened = PrivateBytes();
        double openMs = watch.Elapsed.TotalMilliseconds;

        long[] afterPass = new long[3];
        double firstPageMs = 0;
        for (int pass = 0; pass < afterPass.Length; pass++)
        {
            await Task.Run(() =>
            {
                for (int page = 1; page <= service.PageCount; page++)
                {
                    var t = Stopwatch.StartNew();
                    var bitmap = service.RenderPage(page, 150);
                    if (pass == 0 && page == 1) firstPageMs = t.Elapsed.TotalMilliseconds;
                    Assert.NotNull(bitmap);
                }
            });
            afterPass[pass] = PrivateBytes();
        }
        service.CloseDocument();
        long closed = PrivateBytes();

        _output.WriteLine($"open {openMs:F0} ms, first page {firstPageMs:F0} ms at 150 dpi");
        _output.WriteLine($"private MB: start {Mb(start):F0}, opened {Mb(opened):F0}, after passes {string.Join(" / ", Array.ConvertAll(afterPass, v => Mb(v).ToString("F0")))}, closed {Mb(closed):F0}");

        // 150 pages rendered twice more after the first pass: a per-page leak of even 0.3 MB would show as 90 MB.
        long growth = afterPass[2] - afterPass[0];
        Assert.True(growth < 48L * 1024 * 1024, $"memory kept growing across render passes: +{Mb(growth):F0} MB");
        Assert.True(Mb(afterPass[0] - start) < 600, $"rendering 150 pages held {Mb(afterPass[0] - start):F0} MB");
    }

    [Fact]
    public async Task OpeningAndClosingDocuments_ReleasesThem()
    {
        string path = Path.Combine(_dir, "form.pdf");
        File.WriteAllBytes(path, FormPdfFixture.Build()); // a form: PDFium's form-fill environment too
        using var service = new HybridVectorDocumentService(PdfViewer.Core.Security.PdfSecurityPolicy.DefaultStrict);

        async Task Cycle(int times)
        {
            for (int i = 0; i < times; i++)
            {
                await service.OpenDocumentAsync(path);
                await Task.Run(() => service.RenderPage(1, 96));
                service.CloseDocument();
            }
        }

        await Cycle(15); // warm up: caches, JIT, first allocations
        long warm = PrivateBytes();
        await Cycle(60);
        long after = PrivateBytes();
        _output.WriteLine($"private MB after 15 open/close cycles {Mb(warm):F0}, after 60 more {Mb(after):F0}");
        Assert.True(after - warm < 24L * 1024 * 1024, $"60 open/render/close cycles kept {Mb(after - warm):F0} MB");
    }
}
