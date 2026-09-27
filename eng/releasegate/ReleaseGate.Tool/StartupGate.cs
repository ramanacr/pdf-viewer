using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReleaseGate.Tool;

/// <summary>One launch, as the application's <c>--startup-probe</c> reported it.</summary>
internal sealed class ProbeResult
{
    /// <summary>The milestone the gate measures: process creation to page 1 on screen.</summary>
    public const string Metric = "firstPageRendered";

    public required IReadOnlyDictionary<string, double> Milestones { get; init; }
    public string? Engine { get; init; }
    public string? Error { get; init; }

    public double FirstPageMs => Milestones[Metric];

    /// <summary>
    /// Reads a probe result, rejecting one that cannot be a real measurement: a reported error,
    /// no page rendered, a negative time, or page 1 drawn before the application started.
    /// </summary>
    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1)
            throw new FormatException("Unknown probe result schema.");

        string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (error != null) throw new FormatException($"The probe reported: {error}");

        var milestones = new Dictionary<string, double>(StringComparer.Ordinal);
        if (root.TryGetProperty("milestones", out var marks) && marks.ValueKind == JsonValueKind.Object)
        {
            foreach (var mark in marks.EnumerateObject())
            {
                double ms = mark.Value.GetDouble();
                if (ms < 0 || double.IsNaN(ms) || double.IsInfinity(ms))
                    throw new FormatException($"Milestone '{mark.Name}' has an impossible time ({ms}).");
                milestones[mark.Name] = ms;
            }
        }

        if (!milestones.TryGetValue(Metric, out double page))
            throw new FormatException($"The probe result has no '{Metric}' milestone.");
        if (milestones.TryGetValue("appStartup", out double app) && page < app)
            throw new FormatException("Page 1 cannot be rendered before the application started.");
        if (milestones.TryGetValue("firstPageSurface", out double surface) && page < surface)
            throw new FormatException("Page 1 cannot be on screen before it had a surface.");

        return new ProbeResult
        {
            Milestones = milestones,
            Engine = root.TryGetProperty("engine", out var engine) ? engine.GetString() : null,
        };
    }
}

/// <summary>A set of launches of one kind (warm or cold), summarised.</summary>
internal sealed class RunSet
{
    public int Runs { get; set; }
    public int Discarded { get; set; }
    public double Median { get; set; }
    public double P95 { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Mean { get; set; }
    public List<double> Samples { get; set; } = new();

    /// <summary>Median of every milestone, so a regression can be placed (runtime, window, document).</summary>
    public SortedDictionary<string, double> MilestoneMedians { get; set; } = new(StringComparer.Ordinal);

    public static RunSet From(IReadOnlyList<ProbeResult> runs, int discarded)
    {
        var summary = Stats.Summarize(runs.Select(r => r.FirstPageMs).ToList());
        var set = new RunSet
        {
            Runs = runs.Count,
            Discarded = discarded,
            Median = Round(summary.Median),
            P95 = Round(summary.P95),
            Min = Round(summary.Min),
            Max = Round(summary.Max),
            Mean = Round(summary.Mean),
            Samples = runs.Select(r => Round(r.FirstPageMs)).ToList(),
        };

        foreach (string name in runs.SelectMany(r => r.Milestones.Keys).Distinct())
        {
            var values = runs.Where(r => r.Milestones.ContainsKey(name)).Select(r => r.Milestones[name]).OrderBy(v => v).ToList();
            set.MilestoneMedians[name] = Round(Stats.Median(values));
        }

        return set;
    }

    private static double Round(double ms) => Math.Round(ms, 1);
}

internal sealed class MachineInfo
{
    public string? Os { get; set; }
    public string? Processor { get; set; }
    public int LogicalProcessors { get; set; }
    public string? Runtime { get; set; }

    public static MachineInfo Current() => new()
    {
        Os = RuntimeInformation.OSDescription,
        Processor = ProcessorName(),
        LogicalProcessors = Environment.ProcessorCount,
        Runtime = RuntimeInformation.FrameworkDescription,
    };

    private static string? ProcessorName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// A startup measurement: warm and cold launches of the published application to page 1 of the
/// synthetic probe document. Also the baseline format, when <see cref="Tolerance"/> is set.
/// </summary>
internal sealed class StartupReport
{
    public int Schema { get; set; } = 1;
    public string Metric { get; set; } = "ms from process creation to page 1 rendered (" + ProbeResult.Metric + ")";
    public DateTime RecordedUtc { get; set; }

    /// <summary>A label for where this was measured ("developer workstation", "github windows-latest").</summary>
    public string? Environment { get; set; }

    public string? Engine { get; set; }
    public MachineInfo? Machine { get; set; }
    public string? Document { get; set; }
    public string? DocumentSha256 { get; set; }
    public RunSet? Warm { get; set; }
    public RunSet? Cold { get; set; }
    public StartupTolerance? Tolerance { get; set; }
}

/// <summary>
/// limit = baseline median x (1 + Relative) + AbsoluteMs, for warm (gated) and cold (reported).
/// A measurement from a different environment than the baseline's (a CI runner against a
/// workstation baseline) is held to the looser CrossEnvironment pair instead: the hardware
/// differs, so only a gross regression is evidence of anything.
/// </summary>
internal sealed class StartupTolerance
{
    public double Relative { get; set; } = 0.35;
    public double AbsoluteMs { get; set; } = 250;
    public double CrossEnvironmentRelative { get; set; } = 1.0;
    public double CrossEnvironmentAbsoluteMs { get; set; } = 500;

    /// <summary>Why these numbers - kept with them so a later change to them has to argue with it.</summary>
    public string? Rationale { get; set; }
}

internal static class StartupGate
{
    public static readonly JsonSerializerOptions Json = PayloadGate.Json;

    /// <summary>
    /// Fails when the warm median exceeds the baseline's by more than the tolerance. Cold
    /// starts are reported against the same rule but never fail the gate: without a way to
    /// empty the operating system's file cache (it needs administrator rights) a "cold" start
    /// on a shared runner is too variable to block a merge on.
    /// </summary>
    public static GateOutcome Compare(StartupReport baseline, StartupReport current)
    {
        var outcome = new GateOutcome();
        var t = baseline.Tolerance ?? new StartupTolerance();
        if (baseline.Warm == null) throw new InvalidOperationException("The baseline has no warm measurement.");
        if (current.Warm == null) throw new InvalidOperationException("Nothing was measured.");

        bool sameEnvironment = string.Equals(baseline.Environment, current.Environment, StringComparison.OrdinalIgnoreCase);
        var rule = sameEnvironment
            ? new Tolerance(t.Relative, t.AbsoluteMs)
            : new Tolerance(t.CrossEnvironmentRelative, t.CrossEnvironmentAbsoluteMs);

        if (!sameEnvironment)
        {
            outcome.Notes.Add($"baseline recorded on '{baseline.Environment}', measured on '{current.Environment}': " +
                              "different hardware, so the cross-environment tolerance applies. Record a baseline here " +
                              "(eng/releasegate/README.md) to hold this environment to the tight one");
        }

        if (!string.Equals(baseline.DocumentSha256, current.DocumentSha256, StringComparison.OrdinalIgnoreCase))
        {
            outcome.Notes.Add("the probe document differs from the baseline's: refresh the baseline");
        }

        double b = baseline.Warm.Median, c = current.Warm.Median, limit = rule.Limit(b);
        string change = Format.Percent(Tolerance.Change(b, c));
        string limitText = $"{Format.Ms(limit)} (+{rule.Relative * 100:0.#} % +{rule.Absolute:0} ms)";
        if (rule.IsExceeded(b, c))
        {
            outcome.Failures.Add($"warm start median regressed {change}: {Format.Ms(b)} -> {Format.Ms(c)}, limit {limitText}");
            outcome.Row("warm median", Format.Ms(b), $"{Format.Ms(c)} ({change})", limitText, "regressed");
        }
        else
        {
            outcome.Row("warm median", Format.Ms(b), $"{Format.Ms(c)} ({change})", limitText, "within tolerance");
            if (sameEnvironment && c < b / (1 + rule.Relative)) outcome.Notes.Add($"warm start improved {change}: refresh the baseline so the gate holds the gain");
        }

        outcome.Row("warm p95", Format.Ms(baseline.Warm.P95), Format.Ms(current.Warm.P95), "reported", "-");

        if (baseline.Cold != null && current.Cold != null)
        {
            double bc = baseline.Cold.Median, cc = current.Cold.Median;
            string coldChange = Format.Percent(Tolerance.Change(bc, cc));
            bool over = rule.IsExceeded(bc, cc);
            outcome.Row("cold median", Format.Ms(bc), $"{Format.Ms(cc)} ({coldChange})", $"{Format.Ms(rule.Limit(bc))} (reported)", over ? "slower (not gated)" : "within tolerance");
            outcome.Row("cold p95", Format.Ms(baseline.Cold.P95), Format.Ms(current.Cold.P95), "reported", "-");
            if (over) outcome.Notes.Add($"cold start median is {coldChange} on the baseline ({Format.Ms(bc)} -> {Format.Ms(cc)})");
        }

        // Where the time went, milestone by milestone, so a failure points at a phase.
        foreach (var (name, now) in current.Warm.MilestoneMedians.OrderBy(m => m.Value))
        {
            string before = baseline.Warm.MilestoneMedians.TryGetValue(name, out double was) ? Format.Ms(was) : "-";
            outcome.Details.Add($"{name,-18} {before,10} -> {Format.Ms(now),10}");
        }

        return outcome;
    }
}

/// <summary>Launches the published application with <c>--startup-probe</c>, cold and warm.</summary>
internal sealed class StartupHarness
{
    public required string AppDirectory { get; init; }
    public required string DocumentPath { get; init; }
    public required string WorkDirectory { get; init; }
    public int WarmRuns { get; init; } = 10;
    public int Warmup { get; init; } = 2;
    public int ColdRuns { get; init; } = 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    public const string ExecutableName = "PdfViewer.exe";

    public StartupReport Run(string? environment)
    {
        if (!File.Exists(Path.Combine(AppDirectory, ExecutableName)))
            throw new FileNotFoundException($"No {ExecutableName} in '{AppDirectory}'. Run scripts/build_publish.ps1 -DryRun first.");

        Directory.CreateDirectory(WorkDirectory);
        var cold = new List<ProbeResult>();
        var warm = new List<ProbeResult>();

        // Cold: every launch is a fresh install - a new copy of the payload in a new folder, and
        // a new single-file extraction directory, so the host unpacks its native libraries again
        // exactly as on a user's first launch. The operating system's file cache is not emptied
        // (that needs administrator rights), so this is "first launch after install", not
        // "first launch after boot".
        for (int i = 1; i <= ColdRuns; i++)
        {
            string runDir = Path.Combine(WorkDirectory, $"cold-{i}");
            string app = Install(runDir);
            var result = Launch(app, runDir, Path.Combine(runDir, "extract"), "cold", i);
            cold.Add(result);
        }

        // Warm: one install launched repeatedly; the first launches populate the extraction
        // directory and the caches and are discarded.
        string warmDir = Path.Combine(WorkDirectory, "warm");
        string warmApp = Install(warmDir);
        string warmExtract = Path.Combine(warmDir, "extract");
        for (int i = 1; i <= Warmup + WarmRuns; i++)
        {
            var result = Launch(warmApp, Path.Combine(warmDir, $"run-{i}"), warmExtract, i <= Warmup ? "warm-up" : "warm", i);
            if (i > Warmup) warm.Add(result);
        }

        return new StartupReport
        {
            RecordedUtc = DateTime.UtcNow,
            Environment = environment,
            Engine = warm.Select(w => w.Engine).FirstOrDefault(e => e != null),
            Machine = MachineInfo.Current(),
            Document = Path.GetFileName(DocumentPath),
            DocumentSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(DocumentPath))),
            Warm = RunSet.From(warm, Warmup),
            Cold = ColdRuns > 0 ? RunSet.From(cold, 0) : null,
        };
    }

    /// <summary>A fresh copy of the payload in an empty folder: nothing left from an earlier run can make a start warmer.</summary>
    private string Install(string runDir)
    {
        if (Directory.Exists(runDir)) Directory.Delete(runDir, recursive: true);
        string target = Path.Combine(runDir, "app");
        CopyDirectory(AppDirectory, target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private ProbeResult Launch(string appDir, string runDir, string extractDir, string kind, int index)
    {
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(extractDir);
        string resultPath = Path.Combine(runDir, "probe.json");
        if (File.Exists(resultPath)) File.Delete(resultPath);

        var psi = new ProcessStartInfo(Path.Combine(appDir, ExecutableName))
        {
            UseShellExecute = false,
            WorkingDirectory = appDir,
        };
        psi.ArgumentList.Add("--startup-probe");
        psi.ArgumentList.Add(resultPath);
        psi.ArgumentList.Add(DocumentPath);
        psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extractDir;
        // The shipped default engine is what users start with; a developer's override must not leak in.
        psi.Environment.Remove("PDF_ENGINE_MODE");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The application did not start.");
        if (!process.WaitForExit(Timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{kind} launch {index} did not finish within {Timeout.TotalSeconds:0} s.");
        }

        if (!File.Exists(resultPath))
            throw new InvalidOperationException($"{kind} launch {index} exited with code {process.ExitCode} and wrote no probe result.");

        ProbeResult result;
        try
        {
            result = ProbeResult.Parse(File.ReadAllText(resultPath));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{kind} launch {index} (exit code {process.ExitCode}): {ex.Message}", ex);
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{kind} launch {index} exited with code {process.ExitCode}.");

        Console.WriteLine($"  {kind,-7} {index,2}: {Format.Ms(result.FirstPageMs),9}");
        return result;
    }
}
