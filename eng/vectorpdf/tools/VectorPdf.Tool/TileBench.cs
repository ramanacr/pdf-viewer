using System.Diagnostics;
using System.Globalization;
using System.IO;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf tiles &lt;dir&gt; [--software] [--top N] [--pages-per-file N]
///
/// Measures what the viewer's Direct2D page host does on real documents: the page bitmap at the
/// viewer's DPI buckets (150/200/300) and the viewport detail tile (visible area plus half a
/// viewport on each side, at most 4096 px a side) at 400/800/1600 % for a 1200×900 DIP window at
/// 150 % display scaling. Runs over the heaviest pages of a corpus (by display-list command count),
/// with PDFium's full-page raster at 300 dpi as the baseline. --software renders on WARP, the tier
/// RDP sessions and VMs without a GPU get.
/// </summary>
internal static class TileBench
{
    private const double WindowWidthDip = 1200, WindowHeightDip = 900, DeviceScale = 1.5, MaxDetailPixels = 4096;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            Console.Error.WriteLine("tiles: directory not found");
            return 2;
        }

        bool software = args.Contains("--software");
        int top = int.TryParse(Program.Option(args, "--top"), out int t) ? t : 40;
        int pagesPerFile = int.TryParse(Program.Option(args, "--pages-per-file"), out int ppf) ? ppf : 3;
        var inv = CultureInfo.InvariantCulture;

        // Pass 1: rank pages by command count.
        var ranked = new List<(string File, int Page, int Commands, double BuildMs)>();
        foreach (var file in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = await PdfVectorDocument.OpenAsync(await File.ReadAllBytesAsync(file));
                for (int p = 1; p <= Math.Min(pagesPerFile, doc.PageCount); p++)
                {
                    var sw = Stopwatch.StartNew();
                    var list = await doc.GetPageDisplayListAsync(p);
                    ranked.Add((file, p, list.Commands.Count, sw.Elapsed.TotalMilliseconds));
                }
            }
            catch (Exception)
            {
                // Open and build failures are the corpus report's business.
            }
        }
        ranked.Sort((a, b) => b.Commands.CompareTo(a.Commands));
        var heavy = ranked.Take(top).ToList();

        using var d2d = new Direct2DVectorRenderer(forceSoftware: software);
        using var engine = new PdfiumEngine();
        Console.WriteLine($"# vectorpdf tiles ({(d2d.IsSoftware ? "WARP software" : "hardware GPU")}), {ranked.Count} pages ranked, heaviest {heavy.Count}");
        Console.WriteLine(string.Create(inv, $"# window {WindowWidthDip}x{WindowHeightDip} DIP at {DeviceScale * 100:0} % scaling; tile = visible + half a viewport per side, <= {MaxDetailPixels} px"));

        var columns = new[] { "build", "page150", "page200", "page300", "pdfium300", "tile400", "scroll400", "tile800", "scroll800", "tile1600", "scroll1600" };
        var samples = columns.ToDictionary(c => c, _ => new List<double>());
        var slow = new List<(double Ms, string What)>();

        static async Task<double> Time(Func<Task> body)
        {
            var sw = Stopwatch.StartNew();
            await body();
            return sw.Elapsed.TotalMilliseconds;
        }

        foreach (var (file, page, commands, buildMs) in heavy)
        {
            byte[] bytes = await File.ReadAllBytesAsync(file);
            using var doc = await PdfVectorDocument.OpenAsync(bytes);
            var list = await doc.GetPageDisplayListAsync(page);
            await using var pdoc = await engine.OpenDocumentAsync(bytes);
            string name = Path.GetFileName(file);
            samples["build"].Add(buildMs);

            // Warm-up: fonts and decoded images are cached per renderer, as in the viewer.
            (await d2d.RenderAsync(list, new RenderRequest { PageNumber = page, Dpi = 72 }, null, CancellationToken.None)).Page.Dispose();

            foreach (int dpi in new[] { 150, 200, 300 })
            {
                double ms = await Time(async () =>
                {
                    var r = await d2d.RenderAsync(list, new RenderRequest { PageNumber = page, Dpi = dpi }, null, CancellationToken.None);
                    r.Page.Dispose();
                });
                samples[$"page{dpi}"].Add(ms);
                if (ms > 250) slow.Add((ms, $"page{dpi} {name} p{page} ({commands} cmds)"));
            }
            samples["pdfium300"].Add(await Time(async () =>
            {
                using var r = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = page, Dpi = 300 });
            }));

            foreach (double zoom in new[] { 4.0, 8.0, 16.0 })
            {
                double pxPerPt = zoom * DeviceScale * 96.0 / 72.0;
                var request = new RenderRequest { PageNumber = page, Dpi = pxPerPt * 72 };
                var (fullW, fullH) = Direct2DVectorRenderer.OutputSize(list, request);
                double visW = Math.Min(fullW, WindowWidthDip * DeviceScale), visH = Math.Min(fullH, WindowHeightDip * DeviceScale);
                double mx = Math.Min(visW / 2, Math.Max(0, (MaxDetailPixels - visW) / 2));
                double my = Math.Min(visH / 2, Math.Max(0, (MaxDetailPixels - visH) / 2));
                // Centre of the page: the densest part of most drawings.
                double vx = (fullW - visW) / 2, vy = (fullH - visH) / 2;
                int x0 = (int)Math.Max(0, vx - mx), y0 = (int)Math.Max(0, vy - my);
                int w = (int)Math.Min(MaxDetailPixels, Math.Min(fullW, vx + visW + mx) - x0);
                int h = (int)Math.Min(MaxDetailPixels, Math.Min(fullH, vy + visH + my) - y0);
                double ms = await Time(async () =>
                {
                    var r = await d2d.RenderAsync(list, request, null, new PixelRegion(x0, y0, w, h), invertColors: false, CancellationToken.None);
                    r.Page.Dispose();
                });
                samples[$"tile{zoom * 100:0}"].Add(ms);
                if (ms > 250) slow.Add((ms, $"tile{zoom * 100:0} {w}x{h} {name} p{page} ({commands} cmds)"));

                // Scrolling on at this zoom: the next tile down (the viewer's next request).
                int y1 = (int)Math.Min(Math.Max(0, fullH - h), y0 + visH);
                double next = await Time(async () =>
                {
                    var r = await d2d.RenderAsync(list, request, null, new PixelRegion(x0, y1, w, h), invertColors: false, CancellationToken.None);
                    r.Page.Dispose();
                });
                samples[$"scroll{zoom * 100:0}"].Add(next);
            }
        }

        Console.WriteLine(string.Create(inv, $"commands: max {(heavy.Count > 0 ? heavy[0].Commands : 0)}, median of heaviest {(heavy.Count > 0 ? heavy[heavy.Count / 2].Commands : 0)}"));
        foreach (var c in columns)
        {
            var s = samples[c];
            s.Sort();
            double P(double q) => s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(q * s.Count))];
            Console.WriteLine(string.Create(inv, $"{c,-10} n={s.Count,3} p50={P(0.5),8:F1}ms p95={P(0.95),8:F1}ms max={(s.Count > 0 ? s[^1] : 0),8:F1}ms"));
        }
        foreach (var (ms, what) in slow.OrderByDescending(x => x.Ms).Take(15))
            Console.WriteLine(string.Create(inv, $"slow {ms,8:F1}ms {what}"));
        return 0;
    }
}
