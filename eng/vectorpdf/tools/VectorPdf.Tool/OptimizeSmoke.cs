using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Optimize;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf optimize &lt;dir&gt; [--limit N] [--ppi 150] [--out report.jsonl]: reduces every PDF's size
/// and checks the result: it opens in both engines with the same pages, PDFium reads the same text
/// on the first pages, and those pages render as before (images downsampled with --ppi excepted).
/// Nothing leaves the machine; document text is never written to the report.
/// </summary>
public static class OptimizeSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        double? ppi = double.TryParse(Program.Option(args, "--ppi"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p) ? p : null;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var engine = new PdfiumEngine();
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        using var report = outPath != null ? new StreamWriter(outPath) : null;
        long before = 0, after = 0;
        int ok = 0, skipped = 0, failed = 0, differs = 0, textDiffers = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            string status;
            string detail = string.Empty;
            byte[] bytes = await File.ReadAllBytesAsync(file);
            try
            {
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                if (doc.IsEncrypted || doc.WasRepaired || doc.PageCount == 0) { skipped++; continue; }
                var result = PdfOptimizer.Optimize(doc, bytes, new PdfOptimizeOptions { ImageResolution = ppi });
                using var optimized = await PdfVectorDocument.OpenAsync(result.Bytes);
                await using var pa = await engine.OpenDocumentAsync(bytes);
                await using var pb = await engine.OpenDocumentAsync(result.Bytes);
                if (optimized.PageCount != doc.PageCount || pb.PageCount != pa.PageCount) { status = "pages-differ"; failed++; }
                else
                {
                    status = "ok";
                    double worst = 0;
                    for (int page = 1; page <= Math.Min(3, doc.PageCount); page++)
                    {
                        string ta = await engine.TextService.ExtractPageTextAsync(pa, page), tb = await engine.TextService.ExtractPageTextAsync(pb, page);
                        if (ta != tb) { status = "text-differs"; break; }
                        if (result.ImagesDownsampled == 0) worst = Math.Max(worst, await Differ(doc, optimized, renderer, page));
                    }
                    if (status == "text-differs") textDiffers++;
                    else if (worst > 0.001) { status = "renders-differently"; differs++; detail = $"{worst:P2} of page pixels"; }
                    else ok++;
                }
                before += bytes.Length;
                after += result.Bytes.Length;
                detail = $"{bytes.Length} -> {result.Bytes.Length} bytes ({100.0 * result.Bytes.Length / bytes.Length:F1}%), merged {result.DuplicatesMerged}, recompressed {result.StreamsRecompressed}, images {result.ImagesDownsampled} {detail}";
            }
            catch (Exception ex)
            {
                status = "error";
                detail = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
                failed++;
            }
            if (status != "ok") Console.WriteLine($"{status,-20} {name}  {detail}");
            report?.WriteLine(JsonSerializer.Serialize(new { file = name, status, detail }));
        }
        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: ok {ok}, skipped {skipped}, renders differently {differs}, text differs {textDiffers}, errors {failed}");
        if (before > 0) Console.WriteLine($"total {before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB ({100.0 * after / before:F1}%)");
        return failed + textDiffers > 0 ? 1 : 0;
    }

    private static async Task<double> Differ(PdfVectorDocument a, PdfVectorDocument b, PdfEngine.Vector.Direct2D.Direct2DVectorRenderer renderer, int page)
    {
        var ra = await renderer.RenderAsync(await a.GetPageDisplayListAsync(page), new RenderRequest { PageNumber = page, Dpi = 36 }, null, CancellationToken.None);
        var rb = await renderer.RenderAsync(await b.GetPageDisplayListAsync(page), new RenderRequest { PageNumber = page, Dpi = 36 }, null, CancellationToken.None);
        using var pa = ra.Page;
        using var pb = rb.Page;
        if (pa.WidthPixels != pb.WidthPixels || pa.HeightPixels != pb.HeightPixels) return 1;
        var sa = pa.Pixels.Span;
        var sb = pb.Pixels.Span;
        long diff = 0, total = (long)pa.WidthPixels * pa.HeightPixels;
        for (int y = 0; y < pa.HeightPixels; y++)
            for (int x = 0; x < pa.WidthPixels; x++)
            {
                int i = y * pa.Stride + x * 4;
                if (Math.Abs(sa[i] - sb[i]) > 24 || Math.Abs(sa[i + 1] - sb[i + 1]) > 24 || Math.Abs(sa[i + 2] - sb[i + 2]) > 24) diff++;
            }
        return (double)diff / total;
    }
}
