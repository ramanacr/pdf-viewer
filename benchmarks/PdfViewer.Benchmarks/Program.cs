using System.Diagnostics;
using PdfEngine;
using PdfEngine.Documents;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfViewer.Core.Cache;
using PdfViewer.Core.Rendering;
using PdfViewer.Core.Session;

namespace PdfViewer.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("===================================================================");
        Console.WriteLine("       PDF Platform Architecture Benchmarks & Performance Metrics  ");
        Console.WriteLine("===================================================================");

        string sampleDoc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Benchmark_Doc.pdf");
        CreateBenchmarkPdf(sampleDoc, 50);

        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(sampleDoc);

        Console.WriteLine($"[Benchmark 1] Opening Document (50 pages)... Status: Success");

        // 1. Raw Render Throughput
        var sw = Stopwatch.StartNew();
        int renderCount = 20;
        for (int i = 1; i <= renderCount; i++)
        {
            var req = new RenderRequest { PageNumber = (i % doc.PageCount) + 1, Dpi = 150.0 };
            using var rendered = await engine.Renderer.RenderPageAsync(doc, req);
        }
        sw.Stop();
        double msPerPage = (double)sw.ElapsedMilliseconds / renderCount;
        Console.WriteLine($"[Benchmark 2] 150 DPI Raw Render: {msPerPage:F2} ms/page ({renderCount * 1000.0 / sw.ElapsedMilliseconds:F1} pages/sec)");

        // 2. Memory Budgeted Priority Scheduler
        using var session = new DocumentSession();
        session.AttachDocument(doc);
        using var scheduler = new RenderPriorityScheduler(engine.Renderer, new MultiTierCache(64 * 1024 * 1024));

        sw.Restart();
        for (int i = 1; i <= 50; i++)
        {
            var req = new RenderRequest { PageNumber = (i % doc.PageCount) + 1, Dpi = 96.0 };
            var page = await scheduler.GetOrRenderPageAsync(session, req);
        }
        sw.Stop();
        Console.WriteLine($"[Benchmark 3] Priority Scheduler + LRU Cache (50 access requests): {sw.ElapsedMilliseconds} ms total");
        Console.WriteLine($"              Cache Hits: {scheduler.Cache.HitCount}, Misses: {scheduler.Cache.MissCount}, Evictions: {scheduler.Cache.EvictionCount}");
        Console.WriteLine($"              Memory in Use: {scheduler.Cache.CurrentMemoryBytes / 1024 / 1024} MB / {scheduler.Cache.MaxMemoryBytes / 1024 / 1024} MB");

        // 3. Text Extraction
        sw.Restart();
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var text = await engine.TextService.ExtractPageTextAsync(doc, i);
        }
        sw.Stop();
        Console.WriteLine($"[Benchmark 4] Full Document Text Extraction: {sw.ElapsedMilliseconds} ms for {doc.PageCount} pages ({sw.ElapsedMilliseconds / (double)doc.PageCount:F2} ms/page)");

        Console.WriteLine("===================================================================");
        Console.WriteLine("                      Benchmarks Completed                         ");
        Console.WriteLine("===================================================================");
    }

    private static void CreateBenchmarkPdf(string filePath, int pageCount)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var writer = new StreamWriter(fs, System.Text.Encoding.ASCII);

        var offsets = new List<long>();
        void WriteObj(int objNum, string content)
        {
            writer.Flush();
            offsets.Add(fs.Position);
            writer.WriteLine($"{objNum} 0 obj");
            writer.WriteLine(content);
            writer.WriteLine("endobj");
        }

        writer.WriteLine("%PDF-1.7");
        writer.WriteLine("%\xAA\xBB\xCC\xDD");

        int currentObj = 1;
        int catalogObj = currentObj++;
        int outlinesObj = currentObj++;
        int pagesObj = currentObj++;

        var pageObjs = new List<int>();
        var contentObjs = new List<int>();

        for (int i = 0; i < pageCount; i++)
        {
            pageObjs.Add(currentObj++);
            contentObjs.Add(currentObj++);
        }

        WriteObj(catalogObj, $"<< /Type /Catalog /Pages {pagesObj} 0 R /Outlines {outlinesObj} 0 R >>");
        WriteObj(outlinesObj, "<< /Type /Outlines /Count 0 >>");

        string kids = string.Join(" ", pageObjs.Select(p => $"{p} 0 R"));
        WriteObj(pagesObj, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        for (int i = 0; i < pageCount; i++)
        {
            int pNum = i + 1;
            string streamText = $"BT /F1 16 Tf 72 700 Td (Benchmark Page {pNum}: High Throughput Text Extraction & Rendering Token) Tj ET";
            byte[] streamBytes = System.Text.Encoding.ASCII.GetBytes(streamText);

            WriteObj(pageObjs[i], $"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 612 792] /Contents {contentObjs[i]} 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            WriteObj(contentObjs[i], $"<< /Length {streamBytes.Length} >>\nstream\n{streamText}\nendstream");
        }

        long xrefPos = fs.Position;
        writer.Flush();
        writer.WriteLine($"xref\n0 {currentObj}\n0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }
        writer.WriteLine($"trailer\n<< /Size {currentObj} /Root {catalogObj} 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
        writer.Flush();
    }
}
