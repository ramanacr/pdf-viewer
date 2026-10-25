using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PrintProduction;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf printprod &lt;dir&gt; [--limit 150] [--dpi 36] [--pages 3] [--out report.jsonl]: print production on a
/// corpus, in one process. For every file: the colorants, the separation of the first pages (overprint simulated
/// and not), Digital printing and PDF/X-1a readiness preflight, and a sanity check: the process and spot plates
/// mixed back to colour (overprint off) against the page's normal render at the same size. Reports crashes,
/// timings and the distribution of findings. Never writes document text.
/// </summary>
public static class PrintSmoke
{
    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        var inv = CultureInfo.InvariantCulture;
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : 150;
        double dpi = double.TryParse(Program.Option(args, "--dpi"), NumberStyles.Float, inv, out double d) ? d : 36;
        int pagesPerFile = int.TryParse(Program.Option(args, "--pages"), out int pp) ? pp : 3;
        string? outPath = Program.Option(args, "--out");
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();

        using var renderer = new Direct2DVectorRenderer();
        using var rasterizer = new Direct2DDisplayListRasterizer(renderer);
        using var report = outPath != null ? new StreamWriter(outPath) : null;

        int unreadable = 0, crashes = 0, filesWithSpots = 0, filesOverInk = 0, pagesSeparated = 0;
        var findings = new SortedDictionary<string, (int Files, int Count)>(StringComparer.Ordinal);
        var separateMs = new List<double>();
        var preflightMs = new List<double>();
        var sanity = new List<(string File, int Page, double Mean, double P99, bool Spots, bool Rgb, bool Gray)>();
        var maxInk = new List<double>();
        var clock = Stopwatch.StartNew();

        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            PdfVectorDocument doc;
            byte[] bytes = await File.ReadAllBytesAsync(file);
            try
            {
                doc = await PdfVectorDocument.OpenAsync(bytes);
            }
            catch (Exception ex)
            {
                unreadable++;
                report?.WriteLine(JsonSerializer.Serialize(new { file = name, status = "unreadable", error = ex.GetType().Name }));
                continue;
            }

            using (doc)
            {
                var record = new Dictionary<string, object?> { ["file"] = name, ["pages"] = doc.PageCount };
                try
                {
                    var sw = Stopwatch.StartNew();
                    int pages = Math.Min(pagesPerFile, doc.PageCount);
                    var inventory = await PdfColorantInventory.ReadAsync(doc, Enumerable.Range(1, pages));
                    var model = inventory.CreateModel();
                    record["spots"] = inventory.Spots.Count;
                    if (inventory.Spots.Count > 0) filesWithSpots++;
                    double fileMaxInk = 0;
                    for (int page = 1; page <= pages; page++)
                    {
                        var node = doc.PageTree.Pages[page - 1];
                        var crop = node.CropBox.IsEmpty ? new PdfRect(0, 0, node.PageSize.Width, node.PageSize.Height) : node.CropBox;
                        bool sideways = ((node.RotationDegrees % 360) + 360) % 360 is 90 or 270;
                        int w = Math.Clamp((int)Math.Round((sideways ? crop.Height : crop.Width) * dpi / 72), 1, 3000);
                        int h = Math.Clamp((int)Math.Round((sideways ? crop.Width : crop.Height) * dpi / 72), 1, 3000);

                        var knockout = await PdfSeparationRenderer.RenderAsync(doc, page, w, h, rasterizer,
                            new PdfSeparationOptions { Model = model, SimulateOverprint = false });
                        var overprint = await PdfSeparationRenderer.RenderAsync(doc, page, w, h, rasterizer,
                            new PdfSeparationOptions { Model = model, SimulateOverprint = true });
                        fileMaxInk = Math.Max(fileMaxInk, overprint.Coverage(300).MaxPercent);
                        pagesSeparated++;

                        // Sanity: the plates mixed back against the page drawn normally (as it prints).
                        var printList = await doc.GetPagePrintDisplayListAsync(page);
                        var normal = await rasterizer.RenderAsync(printList, w, h);
                        var composite = knockout.CompositeToBgra(model.AllMask, inventory.Spots.Select(s => s.Appearance).ToList());
                        var (mean, p99) = Difference(normal, composite);
                        var rgbSeen = new RgbProbe();
                        await doc.ObservePageAsync(page, rgbSeen);
                        sanity.Add((name, page, mean, p99, inventory.Spots.Count > 0, rgbSeen.Seen, rgbSeen.Gray));
                    }
                    separateMs.Add(sw.Elapsed.TotalMilliseconds);
                    record["maxInk"] = Math.Round(fileMaxInk, 1);
                    maxInk.Add(fileMaxInk);
                    if (fileMaxInk > 300) filesOverInk++;

                    foreach (var profile in PdfPreflightProfile.BuiltIn)
                    {
                        sw.Restart();
                        var result = await PdfPreflight.RunAsync(doc, profile, new PdfPreflightOptions { Rasterizer = rasterizer, InkCoverageDpi = dpi, MaxPages = 20 });
                        preflightMs.Add(sw.Elapsed.TotalMilliseconds);
                        foreach (var group in result.Findings.GroupBy(f => $"{profile.Id} {f.RuleId} {f.Severity}"))
                        {
                            findings.TryGetValue(group.Key, out var tally);
                            findings[group.Key] = (tally.Files + 1, tally.Count + group.Sum(f => f.Count));
                        }
                        record[profile.Id] = new { errors = result.Errors, warnings = result.Warnings, info = result.Infos };
                    }
                    record["status"] = "ok";
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    crashes++;
                    record["status"] = "crash";
                    record["error"] = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
                    record["at"] = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
                    Console.WriteLine($"CRASH {name}: {record["error"]} {record["at"]}");
                }
                report?.WriteLine(JsonSerializer.Serialize(record));
            }
        }

        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: crashes {crashes}, unreadable {unreadable}, pages separated {pagesSeparated} (at {dpi:0} dpi)");
        Console.WriteLine($"files with spot colours {filesWithSpots}, files with total ink over 300 % (first pages, overprint simulated) {filesOverInk}");
        Console.WriteLine($"separation per file (inventory, two separations and the sanity render of up to {pagesPerFile} pages): {Stats(separateMs)} ms");
        Console.WriteLine($"preflight per profile and file (up to 20 pages, ink rasterized): {Stats(preflightMs)} ms");
        Console.WriteLine($"max total ink per file: {Stats(maxInk)} %");

        Console.WriteLine("\nsanity: plates mixed back (overprint off) against the normal render, mean / 99th percentile channel difference (0..255)");
        foreach (var (label, subset) in new[]
                 {
                     ("all pages", sanity),
                     ("pages without RGB or spot content", sanity.Where(s => !s.Rgb && !s.Spots).ToList()),
                     ("pages with CMYK content only (no RGB, gray or spots)", sanity.Where(s => !s.Rgb && !s.Spots && !s.Gray).ToList()),
                     ("pages with gray content (gray is black ink only)", sanity.Where(s => s.Gray).ToList()),
                     ("pages with RGB content", sanity.Where(s => s.Rgb).ToList()),
                     ("pages of files with spots", sanity.Where(s => s.Spots).ToList()),
                 })
        {
            if (subset.Count == 0) { Console.WriteLine($"  {label}: none"); continue; }
            Console.WriteLine($"  {label} ({subset.Count}): mean {Stats(subset.Select(s => s.Mean).ToList())}; p99 {Stats(subset.Select(s => s.P99).ToList())}");
        }
        foreach (var worst in sanity.OrderByDescending(s => s.Mean).Take(8))
            Console.WriteLine($"    {worst.File} p{worst.Page}: mean {worst.Mean:F1}, p99 {worst.P99:F0}{(worst.Rgb ? " rgb" : string.Empty)}{(worst.Spots ? " spots" : string.Empty)}");

        Console.WriteLine("\nfindings (profile rule severity: files, occurrences)");
        foreach (var (key, (f, c)) in findings)
            Console.WriteLine($"  {key,-48} {f,5} {c,8}");
        return crashes > 0 ? 1 : 0;
    }

    private sealed class RgbProbe : IPdfPaintObserver
    {
        public bool Seen { get; private set; }
        public bool Gray { get; private set; }

        public void OnPaint(PdfPaintEvent paint)
        {
            var s = paint.ColorSpace;
            while (s is PdfEngine.Vector.Color.PdfColorSpace.IndexedColorSpace i) s = i.BaseColorSpace;
            if (s != null && !paint.Invisible && (s.Name is "DeviceRGB" or "CalRGB" or "Lab" || s.NumberOfComponents == 3 && s.Name == "ICCBased"))
                Seen = true;
            if (s != null && !paint.Invisible && (s.Name is "DeviceGray" or "CalGray" || s.NumberOfComponents == 1 && s.Name == "ICCBased"))
                Gray = true;
        }
    }

    private static (double Mean, double P99) Difference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length) / 4;
        if (n == 0) return (0, 0);
        var histogram = new long[256];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            int diff = Math.Max(Math.Abs(a[p] - b[p]), Math.Max(Math.Abs(a[p + 1] - b[p + 1]), Math.Abs(a[p + 2] - b[p + 2])));
            histogram[diff]++;
            sum += diff;
        }
        long target = (long)Math.Ceiling(n * 0.99), seen = 0;
        int p99 = 0;
        for (int v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= target) { p99 = v; break; }
        }
        return (sum / n, p99);
    }

    private static string Stats(List<double> values)
    {
        if (values.Count == 0) return "n/a";
        var sorted = values.OrderBy(v => v).ToList();
        double Q(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * sorted.Count))];
        return string.Create(CultureInfo.InvariantCulture, $"median {Q(0.5):F1}, p90 {Q(0.9):F1}, max {sorted[^1]:F1}");
    }
}
