using System.Globalization;
using System.IO;
using System.Text;

namespace ReleaseGate.Tool;

/// <summary>Order statistics over a set of timings.</summary>
internal sealed record Summary(int Count, double Median, double P95, double Min, double Max, double Mean);

internal static class Stats
{
    /// <summary>Summarises samples. Throws on an empty set: a gate with nothing measured must not pass.</summary>
    public static Summary Summarize(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0) throw new ArgumentException("No samples to summarise.", nameof(samples));
        if (samples.Any(s => double.IsNaN(s) || double.IsInfinity(s)))
            throw new ArgumentException("Samples must be finite numbers.", nameof(samples));

        var sorted = samples.OrderBy(s => s).ToArray();
        return new Summary(sorted.Length, Median(sorted), Percentile(sorted, 95), sorted[0], sorted[^1], sorted.Average());
    }

    /// <summary>The middle value, or the mean of the two middle values for an even count. Input must be sorted.</summary>
    public static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) throw new ArgumentException("No samples.", nameof(sorted));
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// Nearest-rank percentile: the smallest sample with at least <paramref name="percent"/>% of
    /// the samples at or below it. Never interpolates, so it is always a time that was actually
    /// measured; with ten samples p95 is the slowest one. Input must be sorted.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sorted, double percent)
    {
        if (sorted.Count == 0) throw new ArgumentException("No samples.", nameof(sorted));
        if (percent is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        int rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}

/// <summary>
/// How far a measurement may exceed its baseline: limit = baseline x (1 + Relative) + Absolute.
/// The relative part scales with the thing measured; the absolute part keeps a small baseline
/// from failing on noise that is large only in proportion.
/// </summary>
internal sealed record Tolerance(double Relative, double Absolute)
{
    public double Limit(double baseline)
    {
        if (Relative < 0 || Absolute < 0) throw new InvalidOperationException("Tolerances cannot be negative.");
        return baseline * (1 + Relative) + Absolute;
    }

    public bool IsExceeded(double baseline, double current) => current > Limit(baseline);

    /// <summary>Relative change from baseline to current (0.10 = 10 % larger). Zero for a zero baseline.</summary>
    public static double Change(double baseline, double current) =>
        baseline == 0 ? 0 : (current - baseline) / baseline;
}

/// <summary>The verdict of one gate, plus everything needed to explain it.</summary>
internal sealed class GateOutcome
{
    public List<string> Failures { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Details { get; } = new();

    /// <summary>Markdown table rows: | measure | baseline | current | limit | verdict |</summary>
    public List<string> Rows { get; } = new();

    public bool Passed => Failures.Count == 0;

    public void Row(string measure, string baseline, string current, string limit, string verdict) =>
        Rows.Add($"| {measure} | {baseline} | {current} | {limit} | {verdict} |");

    /// <summary>Writes the report to the console and, on GitHub Actions, to the job summary.</summary>
    public void Report(string title)
    {
        var md = new StringBuilder();
        md.AppendLine($"### {title}: {(Passed ? "passed" : "FAILED")}");
        md.AppendLine();
        md.AppendLine("| measure | baseline | current | limit | verdict |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var row in Rows) md.AppendLine(row);
        md.AppendLine();
        foreach (var failure in Failures) md.AppendLine($"- FAIL: {failure}");
        foreach (var note in Notes) md.AppendLine($"- note: {note}");
        if (Details.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("```");
            foreach (var line in Details) md.AppendLine(line);
            md.AppendLine("```");
        }

        Console.WriteLine(md.ToString());

        string? summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
        {
            File.AppendAllText(summary, md.ToString());
        }

        // GitHub turns these into annotations on the run; elsewhere they are just lines.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            foreach (var failure in Failures) Console.WriteLine($"::error title={title}::{failure}");
        }
    }
}

internal static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Ms(double ms) => ms.ToString("0", Inv) + " ms";

    public static string Percent(double change) => (change >= 0 ? "+" : "") + (change * 100).ToString("0.0", Inv) + " %";

    /// <summary>Binary units, two decimals above a kilobyte: sizes differ in the details.</summary>
    public static string Bytes(double bytes)
    {
        double abs = Math.Abs(bytes);
        string sign = bytes < 0 ? "-" : "";
        return abs switch
        {
            >= 1024 * 1024 => sign + (abs / (1024 * 1024)).ToString("0.00", Inv) + " MiB",
            >= 1024 => sign + (abs / 1024).ToString("0.00", Inv) + " KiB",
            _ => sign + abs.ToString("0", Inv) + " B",
        };
    }

    public static string SignedBytes(long delta) => (delta >= 0 ? "+" : "") + Bytes(delta);
}
