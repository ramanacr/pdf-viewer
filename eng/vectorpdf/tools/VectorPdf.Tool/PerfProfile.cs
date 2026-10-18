using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf perf &lt;dir&gt; [--dpi N] [--software] [--limit N] [--out pages.jsonl] [--top N]
///
/// Where render time goes on a corpus, against PDFium in the same run: per page the vector
/// engine's display-list build (content interpretation and font preparation), classification,
/// and render split into fonts, images, replay, Direct2D flush and readback; PDFium's page render;
/// and the first-page latency of each document (open, build and render page 1 against PDFium's
/// open and render). At most 50 pages per file, as the corpus runner. The report holds file names,
/// page numbers and times only; nothing leaves the machine.
/// </summary>
internal static class PerfProfile
{
    private sealed record PageTimes(string File, int Page, double Build, double Classify, double Render, double Pdfium,
        double Fonts, double Images, double Replay, double Flush, double Readback, double ReadbackWait = 0, double Surfaces = 0)
    {
        public double Diff { get; init; } = -1;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--micro"))
            return await MicroAsync(args);
        if (args.Contains("--lex"))
            return await LexAsync(args);
        if (args.Length < 2 || !Directory.Exists(args[1]))
        {
            Console.Error.WriteLine("perf: directory not found");
            return 2;
        }
        var inv = CultureInfo.InvariantCulture;
        double dpi = double.TryParse(Program.Option(args, "--dpi"), NumberStyles.Float, inv, out double d) ? d : 72;
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        int top = int.TryParse(Program.Option(args, "--top"), out int t) ? t : 20;
        string? outPath = Program.Option(args, "--out");
        using var output = outPath != null ? new StreamWriter(outPath, append: false, Encoding.UTF8) : null;
        using var renderer = new Direct2DVectorRenderer(forceSoftware: args.Contains("--software"));
        if (args.Contains("--no-atlas")) renderer.UseGlyphAtlas = false;
        var stages = renderer.Stages;

        var pages = new List<PageTimes>();
        var firstVector = new List<double>();
        var firstPdfium = new List<double>();
        double vectorOpenMs = 0, pdfiumOpenMs = 0;
        // --images: every distinct image decoded once more on its own, timed by filter and colour space.
        bool imagesOnly = args.Contains("--images-only");
        // --diff: mean channel difference against PDFium per page (the corpus runner's measure).
        bool diff = args.Contains("--diff");
        var diffs = new List<double>();
        var imageStats = args.Contains("--images") || imagesOnly ?new Dictionary<string, (int Count, double Pixels, double CoreMs, double WicMs)>(StringComparer.Ordinal) : null;
        var seenImages = new HashSet<string>(StringComparer.Ordinal);
        int files = 0, failed = 0;
        var wall = Stopwatch.StartNew();

        foreach (var file in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit))
        {
            files++;
            string name = Path.GetRelativePath(args[1], file);
            byte[] bytes = await File.ReadAllBytesAsync(file);
            try
            {
                var clock = Stopwatch.StartNew();
                using var engine = new PdfiumEngine();
                await using var pdoc = await engine.OpenDocumentAsync(bytes);
                double pOpen = clock.Elapsed.TotalMilliseconds;

                clock.Restart();
                using var doc = await PdfVectorDocument.OpenAsync(bytes, Path.GetFileName(file));
                double vOpen = clock.Elapsed.TotalMilliseconds;
                vectorOpenMs += vOpen;
                pdfiumOpenMs += pOpen;

                int count = Math.Min(Math.Min(doc.PageCount, pdoc.PageCount), 50);
                for (int page = 1; page <= count; page++)
                {
                    clock.Restart();
                    var list = await doc.GetPageDisplayListAsync(page);
                    double build = clock.Elapsed.TotalMilliseconds;
                    if (imagesOnly)
                    {
                        TimeImages(list, seenImages, imageStats!);
                        continue;
                    }

                    // The render comes first and includes the page's analysis, as in the viewer, where
                    // the first render of a page analyses it; classification afterwards is what the
                    // corpus runner asks for again.
                    var before = stages.Snapshot();
                    clock.Restart();
                    using var vectorPage = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = page, Dpi = dpi });
                    double render = clock.Elapsed.TotalMilliseconds;
                    var after = stages.Snapshot();

                    clock.Restart();
                    await renderer.AnalyzeAsync(list);
                    double classify = clock.Elapsed.TotalMilliseconds;

                    clock.Restart();
                    using var pdfiumPage = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = page, Dpi = dpi });
                    double pdfium = clock.Elapsed.TotalMilliseconds;
                    double pageDiff = diff ? MeanDiff(vectorPage, pdfiumPage) : -1;
                    if (diff) diffs.Add(pageDiff);

                    var times = new PageTimes(name, page, build, classify, render, pdfium,
                        RenderStages.Ms(after.Fonts - before.Fonts), RenderStages.Ms(after.Images - before.Images),
                        RenderStages.Ms(after.Replay - before.Replay), RenderStages.Ms(after.Flush - before.Flush),
                        RenderStages.Ms(after.Readback - before.Readback), RenderStages.Ms(after.ReadbackWait - before.ReadbackWait),
                        RenderStages.Ms(after.Surfaces - before.Surfaces)) { Diff = pageDiff };
                    pages.Add(times);
                    if (page == 1)
                    {
                        // First page of the document: what a cold open costs before anything shows.
                        firstVector.Add(vOpen + build + render);
                        firstPdfium.Add(pOpen + pdfium);
                    }
                    if (output != null) await output.WriteLineAsync(JsonSerializer.Serialize(times));
                    if (imageStats != null)
                        TimeImages(list, seenImages, imageStats);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
            }
        }

        double Sum(Func<PageTimes, double> f) => pages.Sum(f);
        string F(double v) => v.ToString("F0", inv).PadLeft(9);
        Console.WriteLine($"files {files} (failed {failed}), pages {pages.Count}, dpi {dpi.ToString(inv)}, software {renderer.IsSoftware}, wall {wall.Elapsed.TotalSeconds:F0} s");
        Console.WriteLine();
        Console.WriteLine("stage                         total ms   per page");
        void Row(string label, double ms) => Console.WriteLine($"{label,-28}{F(ms)}  {(pages.Count > 0 ? ms / pages.Count : 0).ToString("F2", inv),9}");
        Row("vector open", vectorOpenMs);
        Row("vector build (interpret)", Sum(p => p.Build));
        Row("vector classify", Sum(p => p.Classify));
        Row("vector render", Sum(p => p.Render));
        Row("  fonts", Sum(p => p.Fonts));
        Row("  images", Sum(p => p.Images));
        Row("  replay (CPU submit)", Sum(p => p.Replay));
        Row("  flush (EndDraw)", Sum(p => p.Flush));
        Row("  readback", Sum(p => p.Readback));
        Row("    of which GPU wait", Sum(p => p.ReadbackWait));
        Row("  tile surfaces", Sum(p => p.Surfaces));
        string[] kinds = { "paths", "clips", "text", "images", "groups", "other" };
        for (int k = 0; k < kinds.Length; k++)
            Row("  replay: " + kinds[k], RenderStages.Ms(stages.Commands[k]));
        Console.WriteLine($"analysis cache hits {stages.AnalysisCacheHits}, images decoded {stages.ImagesDecoded}, decodes skipped {stages.ImageDecodesSkipped}, glyphs rasterized {stages.GlyphsRasterized}, atlas clears {stages.AtlasClears}");
        Row("vector build+classify+render", Sum(p => p.Build + p.Classify + p.Render));
        Row("pdfium open", pdfiumOpenMs);
        Row("pdfium render", Sum(p => p.Pdfium));
        Console.WriteLine();
        if (diff) Console.WriteLine($"mean pixel diff vs pdfium {diffs.DefaultIfEmpty().Average():F4} over {diffs.Count} pages");
        Console.WriteLine($"render ratio vector/pdfium            {Sum(p => p.Render) / Math.Max(1e-9, Sum(p => p.Pdfium)):F3}");
        Console.WriteLine($"build+render ratio vector/pdfium      {Sum(p => p.Build + p.Render) / Math.Max(1e-9, Sum(p => p.Pdfium)):F3}");
        Console.WriteLine($"first page median ms vector / pdfium  {Median(firstVector):F2} / {Median(firstPdfium):F2}   (p90 {Percentile(firstVector, 0.9):F1} / {Percentile(firstPdfium, 0.9):F1})");
        Console.WriteLine();
        Console.WriteLine($"slowest {top} pages by vector build+render (ms): build classify render [fonts images replay flush readback] | pdfium");
        foreach (var p in pages.OrderByDescending(p => p.Build + p.Render).Take(top))
            Console.WriteLine($"  {p.Build,8:F1} {p.Classify,7:F1} {p.Render,8:F1} [{p.Fonts,7:F1} {p.Images,7:F1} {p.Replay,7:F1} {p.Flush,7:F1} {p.Readback,6:F1}] | {p.Pdfium,7:F1}  {p.File} p{p.Page}");
        if (imageStats != null)
        {
            Console.WriteLine();
            Console.WriteLine("image decode by filter / colour space: count, megapixels, core ms, WIC ms (JPEG)");
            foreach (var (key, v) in imageStats.OrderByDescending(kv => kv.Value.CoreMs + kv.Value.WicMs))
                Console.WriteLine($"  {key,-40} {v.Count,6} {v.Pixels / 1e6,8:F1} {v.CoreMs,9:F0} {v.WicMs,9:F0}");
        }
        var slow = pages.OrderByDescending(p => p.Build + p.Render).Take(top).ToList();
        Console.WriteLine($"  slowest {top} totals: build {slow.Sum(p => p.Build):F0}, render {slow.Sum(p => p.Render):F0} (fonts {slow.Sum(p => p.Fonts):F0}, images {slow.Sum(p => p.Images):F0}, replay {slow.Sum(p => p.Replay):F0}, flush {slow.Sum(p => p.Flush):F0}, readback {slow.Sum(p => p.Readback):F0}), pdfium {slow.Sum(p => p.Pdfium):F0}");
        return 0;
    }

    /// <summary>
    /// vectorpdf perf --micro [file.pdf]: the fixed cost of one render, the same page rendered 200
    /// times at 72 dpi (median per stage) against PDFium's render of it.
    /// </summary>
    public static async Task<int> MicroAsync(string[] args)
    {
        string? file = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        byte[] bytes = file != null ? await File.ReadAllBytesAsync(file) : BenchDocument.Create(1);
        using var renderer = new Direct2DVectorRenderer(forceSoftware: args.Contains("--software"));
        if (args.Contains("--realize-all")) renderer.DrawSmallGeometriesDirectly = false;
        if (args.Contains("--no-atlas")) renderer.UseGlyphAtlas = false;
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var list = await doc.GetPageDisplayListAsync(1);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(bytes);
        var request = new RenderRequest { PageNumber = 1, Dpi = 72 };
        var stages = renderer.Stages;
        var rows = new List<double[]>();
        var pdfium = new List<double>();
        for (int i = 0; i < 220; i++)
        {
            if (args.Contains("--cold"))
            {
                // A new display list each time: nothing the renderer keeps per page is reused.
                using var fresh = await PdfVectorDocument.OpenAsync(bytes);
                list = await fresh.GetPageDisplayListAsync(1);
            }
            var before = stages.Snapshot();
            var clock = Stopwatch.StartNew();
            using (await renderer.RenderDisplayListAsync(list, request)) { }
            double total = clock.Elapsed.TotalMilliseconds;
            var after = stages.Snapshot();
            clock.Restart();
            using (await engine.Renderer.RenderPageAsync(pdoc, request)) { }
            if (i < 20) continue; // warm-up
            pdfium.Add(clock.Elapsed.TotalMilliseconds);
            rows.Add([total, RenderStages.Ms(after.Replay - before.Replay), RenderStages.Ms(after.Flush - before.Flush),
                RenderStages.Ms(after.ReadbackWait - before.ReadbackWait), RenderStages.Ms(after.Readback - before.Readback - (after.ReadbackWait - before.ReadbackWait)),
                RenderStages.Ms(after.Surfaces - before.Surfaces), RenderStages.Ms(after.Analyze - before.Analyze), RenderStages.Ms(after.Fonts - before.Fonts),
                RenderStages.Ms(after.Commands[2] - before.Commands[2]), RenderStages.Ms(after.Commands[0] - before.Commands[0]),
                RenderStages.Ms(after.Setup - before.Setup), RenderStages.Ms(after.Hop - before.Hop), RenderStages.Ms(after.Tiles - before.Tiles)]);
        }
        string[] names = { "total", "replay", "flush", "GPU wait", "copy", "surfaces", "analyze", "fonts", "text", "paths", "setup", "hop", "tiles" };
        Console.WriteLine($"gen0/1/2 collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, pause {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms");
        for (int c = 0; c < names.Length; c++)
            Console.WriteLine($"{names[c],-10} median {Median(rows.Select(r => r[c]).ToList()):F3} ms");
        Console.WriteLine($"{"pdfium",-10} median {Median(pdfium):F3} ms   (commands {list.Commands.Count}, software {renderer.IsSoftware})");
        return 0;
    }

    /// <summary>
    /// vectorpdf perf --lex &lt;dir&gt;: how long tokenizing the page content takes (decoding the content
    /// streams, then the interpreter's token and operand loop without executing anything) against
    /// building the display lists.
    /// </summary>
    public static async Task<int> LexAsync(string[] args)
    {
        double decodeMs = 0, lexMs = 0, buildMs = 0;
        int pages = 0;
        foreach (var file in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                var parser = new PdfEngine.Vector.Parsing.PdfParser();
                for (int page = 1; page <= Math.Min(doc.PageCount, 50); page++)
                {
                    var clock = Stopwatch.StartNew();
                    await doc.GetPageDisplayListAsync(page);
                    buildMs += clock.Elapsed.TotalMilliseconds;
                    var node = doc.PageTree.Pages[page - 1];
                    var dec = new PdfEngine.Vector.Streams.PdfStreamDecoder(null, o => doc.Resolver.Resolve(o));
                    foreach (var stream in node.Contents)
                    {
                        clock.Restart();
                        byte[] content;
                        try { content = dec.DecodeStream(stream); } catch { continue; }
                        decodeMs += clock.Elapsed.TotalMilliseconds;
                        clock.Restart();
                        var src = new PdfEngine.Vector.Parsing.MemoryByteSource(content);
                        var lexer = new PdfEngine.Vector.Parsing.PdfLexer(src);
                        while (true)
                        {
                            long pos = src.Position;
                            var token = lexer.NextToken();
                            if (token.Type == PdfEngine.Vector.Parsing.PdfTokenType.EndOfFile) break;
                            if (token.Type != PdfEngine.Vector.Parsing.PdfTokenType.Keyword)
                            {
                                src.Position = pos;
                                if (parser.ParseObject(lexer) == null && src.Position == pos) src.Position = pos + 1;
                            }
                        }
                        lexMs += clock.Elapsed.TotalMilliseconds;
                    }
                    pages++;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        Console.WriteLine($"pages {pages}: build {buildMs:F0} ms, content decode {decodeMs:F0} ms, tokenize {lexMs:F0} ms");
        return 0;
    }

    private static void TimeImages(IPdfDisplayList list, HashSet<string> seen, Dictionary<string, (int Count, double Pixels, double CoreMs, double WicMs)> stats)
    {
        foreach (var draw in list.Commands.OfType<DrawImage>())
        {
            var image = draw.Image;
            if (image.Source == null || !seen.Add(image.Source.CacheKey))
                continue;
            string key = $"{image.Filter ?? "none"} {image.ColorSpaceName} {image.BitsPerComponent}";
            var clock = Stopwatch.StartNew();
            double core, wic = 0;
            try
            {
                var decoded = image.Source.Decode();
                core = clock.Elapsed.TotalMilliseconds;
                if (decoded.Format == PdfDecodedImageFormat.Jpeg)
                {
                    clock.Restart();
                    WicJpeg.TryDecode(decoded, out _, out _);
                    wic = clock.Elapsed.TotalMilliseconds;
                    if (decoded.MapJpegComponents != null) key += " mapped";
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                core = clock.Elapsed.TotalMilliseconds;
                key += " failed";
            }
            var s = stats.GetValueOrDefault(key);
            stats[key] = (s.Count + 1, s.Pixels + (double)image.Width * image.Height, s.CoreMs + core, s.WicMs + wic);
        }
    }

    private static double Median(List<double> v) => Percentile(v, 0.5);

    private static double MeanDiff(RenderedPage v, RenderedPage p)
    {
        if (v.WidthPixels != p.WidthPixels || v.HeightPixels != p.HeightPixels)
            return 255;
        var a = v.Pixels.Span;
        var b = p.Pixels.Span;
        long total = 0;
        for (int y = 0; y < v.HeightPixels; y++)
            for (int x = 0; x < v.WidthPixels; x++)
            {
                int ia = y * v.Stride + x * 4, ib = y * p.Stride + x * 4;
                total += Math.Abs(a[ia] - b[ib]) + Math.Abs(a[ia + 1] - b[ib + 1]) + Math.Abs(a[ia + 2] - b[ib + 2]);
            }
        return total / (3.0 * v.WidthPixels * v.HeightPixels);
    }

    private static double Percentile(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[(int)Math.Min(s.Count - 1, Math.Floor(q * s.Count))];
    }
}
