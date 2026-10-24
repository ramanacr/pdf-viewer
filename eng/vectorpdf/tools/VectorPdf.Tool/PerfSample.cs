using System.Diagnostics;
using System.Globalization;
using System.IO;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf sample &lt;dir&gt; [--pages N] [--software]: per page of a corpus sample (files in name
/// order, at most 4 pages each, N pages in all, one process), the display-list build (interpret),
/// the draw of the whole page at 100 % and of a 1024 × 1024 window at its centre at 1600 %, each
/// from a newly opened document; prints the median and 95th percentile of each. Public API only,
/// so the same file measures any revision of the engine.
/// </summary>
internal static class PerfSample
{
    public static async Task<int> RunAsync(string[] args)
    {
        int limit = int.TryParse(Program.Option(args, "--pages"), out int n) ? n : 200;
        var interpret = new List<double>();
        var draw100 = new List<double>();
        var draw1600 = new List<double>();
        using var renderer = new Direct2DVectorRenderer(forceSoftware: args.Contains("--software"));
        // --no-atlas: text drawn from outlines (by reflection, so the file still builds against revisions without the atlas).
        if (args.Contains("--no-atlas"))
            typeof(Direct2DVectorRenderer).GetProperty("UseGlyphAtlas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(renderer, false);
        // Warm-up: JIT and device creation are not part of any page.
        await MeasureAsync(renderer, BenchDocument.Create(1), 1, null, null, null);

        foreach (var file in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (interpret.Count >= limit) break;
            byte[] bytes = await File.ReadAllBytesAsync(file);
            int pages;
            try
            {
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                pages = doc.PageCount;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            for (int page = 1; page <= Math.Min(4, pages) && interpret.Count < limit; page++)
            {
                try { await MeasureAsync(renderer, bytes, page, interpret, draw100, draw1600); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }

        var inv = CultureInfo.InvariantCulture;
        void Row(string name, List<double> v) =>
            Console.WriteLine(string.Create(inv, $"{name,-18} median {Q(v, 0.5),8:F2} ms   p95 {Q(v, 0.95),8:F2} ms   total {v.Sum(),9:F0} ms"));
        Console.WriteLine($"pages {interpret.Count}, software {renderer.IsSoftware}");
        Row("interpret", interpret);
        Row("draw 100 %", draw100);
        Row("draw 1600 % tile", draw1600);
        return 0;
    }

    private static async Task MeasureAsync(Direct2DVectorRenderer renderer, byte[] bytes, int page,
        List<double>? interpret, List<double>? draw100, List<double>? draw1600)
    {
        // A new document per measure: nothing of the page is cached before it.
        var clock = Stopwatch.StartNew();
        using (var doc = await PdfVectorDocument.OpenAsync(bytes))
        {
            clock.Restart();
            var list = await doc.GetPageDisplayListAsync(page);
            interpret?.Add(clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            using (await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = page, Dpi = 72 })) { }
            draw100?.Add(clock.Elapsed.TotalMilliseconds);
        }
        using (var doc = await PdfVectorDocument.OpenAsync(bytes))
        {
            var list = await doc.GetPageDisplayListAsync(page);
            var request = new RenderRequest { PageNumber = page, Dpi = 72 * 16 };
            var (w, h) = Direct2DVectorRenderer.OutputSize(list, request);
            var region = new PixelRegion(Math.Max(0, w / 2 - 512), Math.Max(0, h / 2 - 512), Math.Min(1024, w), Math.Min(1024, h));
            clock.Restart();
            var result = await renderer.RenderAsync(list, request, null, region, invertColors: false, CancellationToken.None);
            result.Page.Dispose();
            draw1600?.Add(clock.Elapsed.TotalMilliseconds);
        }
    }

    private static double Q(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[(int)Math.Min(s.Count - 1, Math.Floor(q * s.Count))];
    }
}
