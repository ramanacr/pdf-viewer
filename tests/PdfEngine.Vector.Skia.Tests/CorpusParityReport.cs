using System.Diagnostics;
using System.Globalization;
using System.Text;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Skia against Direct2D over a sample of a local corpus checkout (eng/vectorpdf/corpus/cache,
/// which is git-ignored: no corpus file is ever committed). Off unless SKIA_PARITY_CORPUS names
/// the folder; then the first page of a seeded random sample of its files is rendered by both
/// backends at 96 dpi in this one process, up to SKIA_PARITY_PAGES pages (default 200), and a
/// summary is written to SKIA_PARITY_OUT when set. The summary names files by their path in the
/// corpus and holds numbers only: no document text.
/// </summary>
/// <remarks>
/// To look at a page again: SKIA_PARITY_ONLY limits the run to files whose corpus path contains
/// the text, and SKIA_PARITY_DUMP writes both renders of every page that differs by a mean of
/// more than 3 to that folder as PNG files (they show the document: keep the folder local).
/// </remarks>
/// <remarks>
/// To look at a page again: SKIA_PARITY_ONLY limits the run to files whose corpus path contains
/// the text, and SKIA_PARITY_DUMP writes both renders of every page that differs by a mean of
/// more than 3 to that folder as PNG files (they show the document: keep the folder local).
/// </remarks>
public class CorpusParityReport
{
    private readonly ITestOutputHelper _output;
    public CorpusParityReport(ITestOutputHelper output) => _output = output;

    private sealed record PageResult(string File, PixelDiff Diff, int SkiaBackendFallbacks, bool ListFallback, double SkiaMs, double D2DMs);

    [Fact]
    public async Task SkiaMatchesDirect2D_OverACorpusSample()
    {
        string? root = Environment.GetEnvironmentVariable("SKIA_PARITY_CORPUS");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            _output.WriteLine("SKIA_PARITY_CORPUS is not set: corpus parity report skipped.");
            return;
        }
        int limit = int.TryParse(Environment.GetEnvironmentVariable("SKIA_PARITY_PAGES"), out int n) && n > 0 ? Math.Min(n, 200) : 200;
        const double Dpi = 96;

        // A fixed seed: the same sample on every run of the same checkout.
        var files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var random = new Random(20261023);
        for (int i = files.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (files[i], files[j]) = (files[j], files[i]);
        }

        var results = new List<PageResult>();
        int unreadable = 0, tooLarge = 0;
        // Own renderers with small image caches: the machine running this may be short of memory.
        using var skia = new SkiaVectorRenderer(imageCacheBytes: 64L * 1024 * 1024);
        using var d2d = new Direct2D.Direct2DVectorRenderer(bitmapCacheBytes: 64L * 1024 * 1024);
        // SKIA_PARITY_ONLY: compare only the files whose corpus path contains this text (to look at one page again).
        string? only = Environment.GetEnvironmentVariable("SKIA_PARITY_ONLY");
        foreach (var path in files)
        {
            if (results.Count >= limit)
                break;
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!string.IsNullOrEmpty(only) && !name.Contains(only, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var doc = await PdfVectorDocument.OpenAsync(await File.ReadAllBytesAsync(path, cts.Token));
                var list = await doc.GetPageDisplayListAsync(1, cts.Token);
                var request = new RenderRequest { PageNumber = 1, Dpi = Dpi };
                var (w, h) = SkiaVectorRenderer.OutputSize(list, request);
                if ((long)w * h > 16_000_000)
                {
                    tooLarge++;
                    continue;
                }
                var clock = Stopwatch.StartNew();
                var s = await skia.RenderAsync(list, request, null, null, false, cts.Token);
                double skiaMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var d = await d2d.RenderAsync(list, request, null, null, false, cts.Token);
                double d2dMs = clock.Elapsed.TotalMilliseconds;
                using (s.Page)
                using (d.Page)
                {
                    var diff = PixelDiff.Compare(s.Page, d.Page);
                    results.Add(new PageResult(name, diff, s.BackendFallbackCount, list.HasFallback, skiaMs, d2dMs));
                    if (diff.Mean > 3)
                        Parity.Dump(name.Replace('/', '_'), Dpi, s.Page, d.Page);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Encrypted, damaged or unsupported files the engine does not open: counted, not compared.
                unreadable++;
            }
        }

        var summary = Summarize(results, unreadable, tooLarge, Dpi);
        _output.WriteLine(summary);
        if (Environment.GetEnvironmentVariable("SKIA_PARITY_OUT") is { Length: > 0 } outPath)
            await File.WriteAllTextAsync(outPath, summary);
        Assert.NotEmpty(results);
    }

    private static string Summarize(List<PageResult> results, int unreadable, int tooLarge, double dpi)
    {
        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        static string P(double v) => (v * 100).ToString("0.00", CultureInfo.InvariantCulture) + " %";
        var means = results.Select(r => r.Diff.Mean).OrderBy(v => v).ToList();
        double Percentile(double q) => means.Count == 0 ? 0 : means[Math.Min(means.Count - 1, (int)Math.Floor(q * (means.Count - 1) + 0.5))];
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Pages compared: {results.Count} (first page of each file, {F(dpi)} dpi); files not opened: {unreadable}; pages over 16 Mpx skipped: {tooLarge}");
        if (results.Count == 0)
            return sb.ToString();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Mean channel difference per page: average {F(means.Average())}, median {F(Percentile(0.5))}, 95th percentile {F(Percentile(0.95))}, largest {F(means[^1])}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Pages with mean <= 1: {results.Count(r => r.Diff.Mean <= 1)}; <= 3: {results.Count(r => r.Diff.Mean <= 3)}; > 10: {results.Count(r => r.Diff.Mean > 10)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Share of pixels off by > 64, averaged over pages: {P(results.Average(r => r.Diff.Bad))}; largest {P(results.Max(r => r.Diff.Bad))}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Pages where the Skia backend classified content for fallback: {results.Count(r => r.SkiaBackendFallbacks > 0)}; pages whose display list has fallback regions: {results.Count(r => r.ListFallback)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Render time per page (median): Skia {F(Median(results.Select(r => r.SkiaMs)))} ms, Direct2D {F(Median(results.Select(r => r.D2DMs)))} ms");
        sb.AppendLine();
        sb.AppendLine("Worst pages (by mean channel difference):");
        sb.AppendLine();
        sb.AppendLine("| File | Mean | Max | > 16 | > 64 | Skia fallbacks |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results.OrderByDescending(r => r.Diff.Mean).Take(10))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.File} | {F(r.Diff.Mean)} | {r.Diff.Max} | {P(r.Diff.Visible)} | {P(r.Diff.Bad)} | {r.SkiaBackendFallbacks} |");
        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
