using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Document;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf pdfua &lt;dir&gt; [--clause 7.18] [--show fp|fn|other|all]: checks veraPDF's PDF/UA-1 test
/// files and scores the checker against their labels. A "pass" file must produce no error; a "fail"
/// file must produce one, ideally in the ISO 14289-1 clause the file name gives.
/// </summary>
public static class PdfUaSmoke
{
    private sealed record Case(string Path, bool ShouldPass, string Clause);

    public static async Task<int> RunAsync(string[] args)
    {
        string dir = args.Length > 1 ? args[1] : ".";
        string? clauseFilter = Program.Option(args, "--clause");
        string show = Program.Option(args, "--show") ?? "none";
        var cases = Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories).Select(Classify).Where(c => c != null).Select(c => c!)
            .Where(c => clauseFilter == null || c.Clause.StartsWith(clauseFilter, StringComparison.Ordinal))
            .OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var clock = Stopwatch.StartNew();
        var stats = new SortedDictionary<string, int[]>(StringComparer.Ordinal); // clause → passOk, passFp, failHit, failOtherClause, failMiss
        int errors = 0;
        foreach (var c in cases)
        {
            PdfUaReport? report = null;
            string? error = null;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(c.Path);
                using var doc = await PdfVectorDocument.OpenAsync(bytes);
                report = PdfUaValidator.Validate(doc, bytes);
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message.Split('\n')[0]; }
            if (!stats.TryGetValue(c.Clause, out var s)) stats[c.Clause] = s = new int[5];
            string name = Path.GetFileName(c.Path);
            string Errors(PdfUaReport r) => string.Join(" | ", r.Errors.GroupBy(i => i.Checkpoint).Take(4).Select(g => $"{g.Key} [{g.First().Clause}] {g.First().Message}"));
            if (report == null)
            {
                // A file that cannot be opened cannot be read by assistive technology either.
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
                    if (show is "fp" or "all") Console.WriteLine($"FP  {name}: {Errors(report)}");
                }
            }
            else if (report.IsCompliant)
            {
                s[4]++;
                if (show is "fn" or "all") Console.WriteLine($"FN  {name}");
            }
            else if (report.Errors.Any(i => i.Clause == c.Clause)) s[2]++;
            else
            {
                s[3]++;
                if (show is "fn" or "other" or "all") Console.WriteLine($"OT  {name}: {Errors(report)}");
            }
        }
        Console.WriteLine($"\n{cases.Count} files in {clock.Elapsed.TotalSeconds:F0} s ({errors} pass files could not be opened)\n");
        Console.WriteLine($"{"clause",-12}{"pass ok",8}{"pass FP",8}{"fail hit",9}{"other",7}{"missed",7}");
        var t = new int[5];
        foreach (var (clause, v) in stats)
        {
            Console.WriteLine($"{clause,-12}{v[0],8}{v[1],8}{v[2],9}{v[3],7}{v[4],7}");
            for (int i = 0; i < 5; i++) t[i] += v[i];
        }
        int pass = t[0] + t[1], fail = t[2] + t[3] + t[4];
        Console.WriteLine($"PDF/UA-1: pass files accepted {t[0]}/{pass} ({100.0 * t[0] / Math.Max(1, pass):F1}%), fail files rejected {t[2] + t[3]}/{fail} ({100.0 * (t[2] + t[3]) / Math.Max(1, fail):F1}%), in the tested clause {t[2]}/{fail}");
        return 0;
    }

    // "7.18.5-t02-fail-a.pdf"; one file writes the clause as "7-18.3".
    private static readonly Regex VeraName = new(@"^(\d+(?:[.-]\d+)*)-t\d+-(pass|fail)", RegexOptions.CultureInvariant);

    private static Case? Classify(string path)
    {
        if (!path.Replace('\\', '/').Contains("/PDF_UA-1/")) return null;
        var m = VeraName.Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? new Case(path, m.Groups[2].Value == "pass", m.Groups[1].Value.Replace('-', '.')) : null;
    }
}
