using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Windows;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf bench  [--pages N]               vector vs PDFium timings on a generated document
/// vectorpdf corpus &lt;dir&gt; [--out file.jsonl] [--diff]   per-file open/fallback/diff report
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: vectorpdf bench [--pages N] | corpus <dir> [--out report.jsonl] [--diff] | live <file.pdf> [--frames N] [--pages N]");
            return 2;
        }

        return args[0] switch
        {
            "bench" => await Bench.RunAsync(args),
            "corpus" => await Corpus.RunAsync(args),
            "live" => LiveBench.Run(args),
            "gen" when args.Length >= 2 => Gen(args),
            _ => 2,
        };
    }

    private static int Gen(string[] args)
    {
        int pages = int.TryParse(Option(args, "--pages"), out int p) ? p : 10;
        File.WriteAllBytes(args[1], BenchDocument.Create(pages));
        return 0;
    }

    internal static string? Option(string[] args, string name) =>
        Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

/// <summary>
/// 08_PERFORMANCE_MEMORY_BUDGETS "Benchmark additions": parse, display-list build, replay, zoom
/// 100→1600 %, and the PDFium baseline on the same machine and document.
/// </summary>
internal static class Bench
{
    public static async Task<int> RunAsync(string[] args)
    {
        int pages = int.TryParse(Program.Option(args, "--pages"), out int p) ? p : 20;
        byte[] pdf = BenchDocument.Create(pages);
        var inv = CultureInfo.InvariantCulture;
        var results = new List<(string Name, double Ms)>();

        async Task Measure(string name, Func<Task> body, int iterations = 1)
        {
            await body(); // warm-up
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) await body();
            sw.Stop();
            results.Add((name, sw.Elapsed.TotalMilliseconds / iterations));
        }

        await Measure("vector.open", async () => { using var d = await PdfVectorDocument.OpenAsync(pdf); }, 5);
        await Measure("pdfium.open", async () =>
        {
            using var engine = new PdfiumEngine();
            await using var d = await engine.OpenDocumentAsync(pdf);
        }, 5);

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var buildClock = Stopwatch.StartNew();
        var lists = new List<IPdfDisplayList>();
        for (int i = 1; i <= doc.PageCount; i++) lists.Add(await doc.GetPageDisplayListAsync(i));
        buildClock.Stop();
        results.Add(("vector.build.perPage", buildClock.Elapsed.TotalMilliseconds / doc.PageCount));

        using var renderer = new WindowsVectorRenderer();
        using var engine = new PdfiumEngine();
        await using var pdfiumDoc = await engine.OpenDocumentAsync(pdf);

        foreach (int zoom in new[] { 100, 200, 400, 800, 1600 })
        {
            double dpi = 72.0 * zoom / 100.0;
            await Measure($"vector.render.{zoom}%", async () =>
            {
                using var page = await renderer.RenderDisplayListAsync(lists[0], new RenderRequest { PageNumber = 1, Dpi = dpi });
            }, zoom >= 800 ? 1 : 3);
            await Measure($"pdfium.render.{zoom}%", async () =>
            {
                using var page = await engine.Renderer.RenderPageAsync(pdfiumDoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
            }, zoom >= 800 ? 1 : 3);
        }

        // Where the time goes: compile (display list → retained drawing) vs rasterization.
        foreach (int zoom in new[] { 100, 1600 })
        {
            var split = await renderer.RenderAsync(lists[0], new RenderRequest { PageNumber = 1, Dpi = 72.0 * zoom / 100.0 }, null, CancellationToken.None);
            results.Add(($"vector.compile.{zoom}%", split.CompileTime.TotalMilliseconds));
            results.Add(($"vector.raster.{zoom}%", split.RasterTime.TotalMilliseconds));
            split.Page.Dispose();
        }

        // Live page surface (what the viewer shows): built once per page and rotation, reused at every zoom.
        await Measure("vector.surface.build", async () =>
        {
            await renderer.BuildPageSurfaceAsync(lists[0], PageRotation.Rotate0, null, 200, CancellationToken.None);
        }, 5);

        // Warm replay: the display list is reused; nothing is reparsed.
        await Measure("vector.warmReplay.150dpi", async () =>
        {
            using var page = await renderer.RenderDisplayListAsync(lists[^1], new RenderRequest { PageNumber = lists[^1].PageNumber, Dpi = 150 });
        }, 5);

        // Rapid-scroll cancellation latency.
        var cancelClock = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource())
        {
            var task = renderer.RenderDisplayListAsync(lists[0], new RenderRequest { PageNumber = 1, Dpi = 600 }, null, cts.Token).AsTask();
            cts.CancelAfter(5);
            try { using var r = await task; } catch (OperationCanceledException) { }
        }
        cancelClock.Stop();
        results.Add(("vector.cancel.observedMs", cancelClock.Elapsed.TotalMilliseconds));

        Console.WriteLine($"commands/page={lists.Average(l => l.Commands.Count).ToString("F0", inv)} imageCacheBytes={renderer.CachedImageBytes}");
        foreach (var (name, ms) in results)
            Console.WriteLine($"{name,-28} {ms.ToString("F2", inv),10} ms");
        return 0;
    }
}

/// <summary>
/// Corpus runner (07_TEST_CORPUS "Reproducibility"): one JSON line per file with SHA-256, open
/// outcome for both engines, page count, fallback reasons/area and optional pixel differences.
/// Documents are read locally; nothing is uploaded.
/// </summary>
internal static class Corpus
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            Console.Error.WriteLine("corpus: directory not found");
            return 2;
        }

        string? outPath = Program.Option(args, "--out");
        bool diff = args.Contains("--diff");
        using var output = outPath != null ? new StreamWriter(outPath, append: false, Encoding.UTF8) : null;
        using var renderer = new WindowsVectorRenderer();
        int files = 0, vectorOpen = 0, pdfiumOpen = 0, pagesTotal = 0, pagesVector = 0;

        foreach (var file in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            files++;
            byte[] bytes = await File.ReadAllBytesAsync(file);
            var record = new Dictionary<string, object?>
            {
                ["file"] = Path.GetRelativePath(args[1], file),
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                ["bytes"] = bytes.Length,
            };

            int pdfiumPages = -1;
            try
            {
                using var engine = new PdfiumEngine();
                await using var pdoc = await engine.OpenDocumentAsync(bytes);
                pdfiumPages = pdoc.PageCount;
                pdfiumOpen++;
            }
            catch (Exception ex)
            {
                record["pdfiumError"] = ex.GetType().Name;
            }
            record["pdfiumPages"] = pdfiumPages;

            var clock = Stopwatch.StartNew();
            try
            {
                using var doc = await PdfVectorDocument.OpenAsync(bytes, Path.GetFileName(file));
                vectorOpen++;
                record["vectorPages"] = doc.PageCount;
                record["repaired"] = doc.WasRepaired;
                var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
                var areas = new List<double>();
                var diffs = new List<double>();

                for (int page = 1; page <= doc.PageCount; page++)
                {
                    pagesTotal++;
                    var list = await doc.GetPageDisplayListAsync(page);
                    var tokens = await renderer.AnalyzeAsync(list);
                    if (tokens.Count == 0) pagesVector++;
                    foreach (var t in tokens)
                        reasons[t.Reason.ToString()] = reasons.GetValueOrDefault(t.Reason.ToString()) + 1;
                    areas.Add(new PdfDisplayList(list.PageNumber, list.PageSize, list.MediaBox, list.CropBox, list.RotationDegrees,
                        Array.Empty<PdfDrawCommand>(), list.Features, tokens).ComputeFallbackAreaRatio());

                    if (diff && tokens.Count == 0 && pdfiumPages >= page)
                        diffs.Add(await DiffAsync(bytes, list, renderer, page));
                }

                record["fallbackReasons"] = reasons;
                record["fallbackAreaMean"] = areas.Count > 0 ? Math.Round(areas.Average(), 4) : 0;
                if (diff) record["meanPixelDiff"] = diffs.Count > 0 ? Math.Round(diffs.Average(), 3) : null;
            }
            catch (Exception ex)
            {
                record["vectorError"] = ex is PdfVectorException pve ? $"{ex.GetType().Name}:{pve.Kind}" : ex.GetType().Name;
            }
            record["vectorMs"] = Math.Round(clock.Elapsed.TotalMilliseconds, 1);

            string line = JsonSerializer.Serialize(record);
            if (output != null) await output.WriteLineAsync(line); else Console.WriteLine(line);
        }

        Console.Error.WriteLine($"files={files} vectorOpen={vectorOpen} pdfiumOpen={pdfiumOpen} pages={pagesTotal} pagesFullyVector={pagesVector}");
        return 0;
    }

    private static async Task<double> DiffAsync(byte[] bytes, IPdfDisplayList list, WindowsVectorRenderer renderer, int page)
    {
        using var v = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = page, Dpi = 72 });
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(bytes);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = page, Dpi = 72 });
        if (v.WidthPixels != p.WidthPixels || v.HeightPixels != p.HeightPixels)
            return 255;
        var a = v.Pixels.Span;
        var b = p.Pixels.Span;
        long total = 0;
        for (int y = 0; y < v.HeightPixels; y++)
        {
            for (int x = 0; x < v.WidthPixels; x++)
            {
                int ia = y * v.Stride + x * 4, ib = y * p.Stride + x * 4;
                total += Math.Abs(a[ia] - b[ib]) + Math.Abs(a[ia + 1] - b[ib + 1]) + Math.Abs(a[ia + 2] - b[ib + 2]);
            }
        }
        return total / (3.0 * v.WidthPixels * v.HeightPixels);
    }
}

/// <summary>Deterministic, path- and text-heavy benchmark document.</summary>
internal static class BenchDocument
{
    public static byte[] Create(int pages)
    {
        var inv = CultureInfo.InvariantCulture;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var kids = new StringBuilder();
        var rng = new Random(1234);
        for (int p = 0; p < pages; p++)
        {
            var content = new StringBuilder();
            for (int i = 0; i < 400; i++)
            {
                content.Append(inv, $"{rng.NextDouble():F2} {rng.NextDouble():F2} {rng.NextDouble():F2} RG {rng.Next(1, 4)} w ");
                content.Append(inv, $"{rng.Next(0, 612)} {rng.Next(0, 792)} m {rng.Next(0, 612)} {rng.Next(0, 792)} {rng.Next(0, 612)} {rng.Next(0, 792)} {rng.Next(0, 612)} {rng.Next(0, 792)} c S\n");
            }
            for (int line = 0; line < 50; line++)
                content.Append(inv, $"BT /F1 10 Tf 40 {760 - line * 14} Td (Line {line} of page {p + 1}: the quick brown fox jumps over the lazy dog) Tj ET\n");
            string stream = content.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream");
            int contentObj = objects.Count;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObj} 0 R >>");
            kids.Append(objects.Count).Append(" 0 R ");
        }
        objects[1] = $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>";

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W(o.ToString("D10", inv) + " 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
