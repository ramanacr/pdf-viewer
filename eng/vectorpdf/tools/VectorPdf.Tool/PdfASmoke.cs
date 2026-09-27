using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PdfA;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf pdfa &lt;dir&gt; [--flavour 2b] [--clause 6.2.4] [--show fp|fn|all]: validates the veraPDF,
/// Isartor and TWG conformance test files and scores the validator against their labels. A "pass"
/// file must produce no issue; a "fail" file must produce one, ideally in the clause it tests.
/// </summary>
public static class PdfASmoke
{
    private sealed record Case(string Path, PdfAFlavour Flavour, bool ShouldPass, string? Clause);

    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        var only = PdfAFlavour.Parse(Program.Option(args, "--flavour"));
        string? clauseFilter = Program.Option(args, "--clause");
        string show = Program.Option(args, "--show") ?? "none";
        var cases = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).Select(Classify).Where(c => c != null).Select(c => c!)
            .Where(c => only == null || c.Flavour == only).Where(c => clauseFilter == null || (c.Clause ?? "").StartsWith(clauseFilter, StringComparison.Ordinal))
            .OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var clock = Stopwatch.StartNew();
        var stats = new Dictionary<string, int[]>(); // key: flavour|clause → passOk, passFp, failHit, failOtherClause, failMiss
        int errors = 0;
        foreach (var c in cases)
        {
            PdfAReport? report = null;
            string? error = null;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(c.Path);
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                report = PdfAValidator.Validate(doc, bytes, c.Flavour);
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }
            string key = $"{c.Flavour}|{c.Clause}";
            if (!stats.TryGetValue(key, out var s)) stats[key] = s = new int[5];
            string name = Path.GetFileName(c.Path);
            if (report == null)
            {
                // A file that cannot be opened is non-conforming: right for a fail case, wrong for a pass case.
                if (c.ShouldPass) { s[1]++; errors++; if (show is "fp" or "all") Console.WriteLine($"FP  {name}: cannot open ({error})"); }
                else s[3]++;
                continue;
            }
            if (c.ShouldPass)
            {
                if (report.IsCompliant) s[0]++;
                else
                {
                    s[1]++;
                    if (show is "fp" or "all") Console.WriteLine($"FP  {name}: {string.Join(" | ", report.Summary.Take(3).Select(x => x.First.ToString()))}");
                }
            }
            else
            {
                if (report.IsCompliant)
                {
                    s[4]++;
                    if (show is "fn" or "all") Console.WriteLine($"FN  {name}");
                }
                else if (report.Issues.Any(i => i.Clause == c.Clause)) s[2]++;
                else
                {
                    s[3]++;
                    if (show is "fn" or "all" or "other") Console.WriteLine($"OT  {name}: {string.Join(" | ", report.Summary.Take(3).Select(x => x.First.ToString()))}");
                }
            }
        }
        Console.WriteLine($"\n{cases.Count} files in {clock.Elapsed.TotalSeconds:F0} s ({errors} pass files could not be opened)\n");
        Console.WriteLine($"{"flavour",-9}{"clause",-14}{"pass ok",8}{"pass FP",8}{"fail hit",9}{"other",7}{"missed",7}");
        foreach (var group in stats.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var p = group.Key.Split('|');
            var v = group.Value;
            Console.WriteLine($"{p[0],-9}{p[1],-14}{v[0],8}{v[1],8}{v[2],9}{v[3],7}{v[4],7}");
        }
        foreach (var fl in stats.GroupBy(k => k.Key.Split('|')[0]))
        {
            var t = new int[5];
            foreach (var g in fl) for (int i = 0; i < 5; i++) t[i] += g.Value[i];
            int pass = t[0] + t[1], fail = t[2] + t[3] + t[4];
            Console.WriteLine($"{fl.Key}: pass files accepted {t[0]}/{pass} ({100.0 * t[0] / Math.Max(1, pass):F1}%), fail files rejected {t[2] + t[3]}/{fail} ({100.0 * (t[2] + t[3]) / Math.Max(1, fail):F1}%), in the tested clause {t[2]}/{fail}");
        }
        return 0;
    }

    /// <summary>
    /// vectorpdf pdfa-convert &lt;dir&gt; [--limit N]: converts every file to PDF/A-2b, validates the
    /// result and compares page 1 with the original (it must look the same). Reports what stays
    /// non-conforming by rule. Document text is never printed.
    /// </summary>
    public static async Task<int> ConvertAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        int limit = int.TryParse(Program.Option(args, "--limit"), out int l) ? l : int.MaxValue;
        var files = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Take(limit).ToList();
        using var renderer = new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer();
        int compliant = 0, remaining = 0, failed = 0, skipped = 0, looksDifferent = 0;
        var rules = new Dictionary<string, int>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            string name = Path.GetRelativePath(dir, file);
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(file);
                PdfVectorDocument doc;
                try { doc = await PdfVectorDocument.OpenAsync(bytes); }
                catch (Exception) { skipped++; continue; }
                using (doc)
                {
                    if (doc.PageCount == 0) { skipped++; continue; }
                    var result = PdfAConverter.Convert(doc, bytes);
                    if (Program.Option(args, "--out") is { } outDir) await File.WriteAllBytesAsync(Path.Combine(outDir, Path.GetFileName(file)), result.Bytes);
                    using var after = await PdfVectorDocument.OpenAsync(result.Bytes);
                    // Compared through PDFium on both sides, so our own renderer's font stand-ins do not count.
                    double diff = await PdfiumDifference(bytes, result.Bytes);
                    if (diff > 0.01) { looksDifferent++; Console.WriteLine($"looks-different  {name}  {diff:P2}"); }
                    if (result.Report.IsCompliant) compliant++;
                    else
                    {
                        remaining++;
                        foreach (var (first, _) in result.Report.Summary) rules[first.RuleId] = rules.GetValueOrDefault(first.RuleId) + 1;
                        if (Program.Option(args, "--show") == "remaining")
                            Console.WriteLine($"remaining  {name}: {string.Join(" | ", result.Report.Summary.Take(3).Select(x => x.First.ToString()))}");
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"error  {name}  {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
        Console.WriteLine($"\n{files.Count} files in {clock.Elapsed.TotalSeconds:F0} s: PDF/A-2b {compliant}, still non-conforming {remaining}, look different {looksDifferent}, errors {failed}, skipped {skipped}");
        foreach (var (rule, count) in rules.OrderByDescending(r => r.Value).Take(25)) Console.WriteLine($"  {rule,-16} {count}");
        return failed > 0 ? 1 : 0;
    }

    private static async Task<double> PdfiumDifference(byte[] before, byte[] after)
    {
        using var engine = new PdfEngine.Pdfium.PdfiumEngine();
        await using var a = await engine.OpenDocumentAsync(before);
        await using var b = await engine.OpenDocumentAsync(after);
        using var pa = await engine.Renderer.RenderPageAsync(a, new PdfEngine.Rendering.RenderRequest { PageNumber = 1, Dpi = 36 });
        using var pb = await engine.Renderer.RenderPageAsync(b, new PdfEngine.Rendering.RenderRequest { PageNumber = 1, Dpi = 36 });
        if (pa.WidthPixels != pb.WidthPixels || pa.HeightPixels != pb.HeightPixels) return 1;
        var sa = pa.Pixels.Span;
        var sb = pb.Pixels.Span;
        int differing = 0;
        for (int y = 0; y < pa.HeightPixels; y++)
            for (int x = 0; x < pa.WidthPixels; x++)
            {
                int i = y * pa.Stride + x * 4;
                if (Math.Abs(sa[i] - sb[i]) + Math.Abs(sa[i + 1] - sb[i + 1]) + Math.Abs(sa[i + 2] - sb[i + 2]) > 48) differing++;
            }
        return (double)differing / (pa.WidthPixels * pa.HeightPixels);
    }

    private static readonly Regex VeraName = new(@"(\d+(?:-\d+)*)-t\d+-(pass|fail)", RegexOptions.CultureInvariant);

    private static Case? Classify(string path)
    {
        string p = path.Replace('\\', '/');
        string name = Path.GetFileNameWithoutExtension(path);
        PdfAFlavour? flavour = p.Contains("/PDF_A-1b/") || p.Contains("/Isartor test files/") ? PdfAFlavour.A1b
            : p.Contains("/PDF_A-2b/") ? PdfAFlavour.A2b
            : p.Contains("/PDF_A-2u/") ? PdfAFlavour.A2u
            : p.Contains("/PDF_A-3b/") ? PdfAFlavour.A3b
            : p.Contains("/TWG test files/") ? (name.Contains("pdfa1") ? PdfAFlavour.A1b : name.Contains("pdfa2") ? PdfAFlavour.A2b : null)
            : null;
        if (flavour == null) return null;
        if (p.Contains("/Isartor test files/"))
        {
            var m = Regex.Match(name, @"isartor-(\d+(?:-\d+)*)-t\d+-(pass|fail)");
            return m.Success ? new Case(path, flavour, m.Groups[2].Value == "pass", m.Groups[1].Value.Replace('-', '.')) : null;
        }
        if (p.Contains("/TWG test files/"))
            return new Case(path, flavour, name.Contains("-pass-"), "TWG");
        var v = VeraName.Match(name);
        return v.Success ? new Case(path, flavour, v.Groups[2].Value == "pass", v.Groups[1].Value.Replace('-', '.')) : null;
    }
}
