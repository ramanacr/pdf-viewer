using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ReleaseGate.Tool;

/// <summary>
/// releasegate startup [--app publish/app] [--runs 10] [--warmup 2] [--cold 3] [--pdf file] [--environment name]
///                     [--out result.json] [--baseline file [--check] [--write-baseline]] [--from result.json] [--keep] [--work dir]
/// releasegate payload [--dir publish/app] [--installer PdfViewerSetup.exe] [--out result.json]
///                     [--baseline file [--check] [--write-baseline]] [--from result.json]
/// releasegate probe-pdf &lt;out.pdf&gt;
///
/// Exit codes: 0 passed (or measured), 1 the gate failed, 2 the measurement itself failed.
/// See eng/releasegate/README.md.
/// </summary>
public static class Program
{
    private const string Usage =
        "usage: releasegate startup [--app dir] [--runs N] [--warmup N] [--cold N] [--pdf file] [--environment name] [--timeout s]\n" +
        "                           [--out file] [--baseline file] [--check] [--write-baseline] [--from result.json] [--keep] [--work dir]\n" +
        "       releasegate payload [--dir dir] [--installer file] [--out file] [--baseline file] [--check] [--write-baseline] [--from result.json]\n" +
        "       releasegate probe-pdf <out.pdf>";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "startup" => Startup(args),
                "payload" => Payload(args),
                "probe-pdf" when args.Length >= 2 => ProbePdf(args[1]),
                _ => UsageError(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or FormatException
                                       or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"releasegate: {ex.Message}");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Console.WriteLine($"::error title=releasegate {args[0]}::{ex.Message}");
            return 2;
        }
    }

    private static int UsageError()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int ProbePdf(string path)
    {
        File.WriteAllBytes(path, ProbeDocument.Create());
        Console.WriteLine($"wrote {path} ({ProbeDocument.PageCount} pages)");
        return 0;
    }

    private static int Startup(string[] args)
    {
        StartupReport current;
        if (Option(args, "--from") is { } from)
        {
            current = Read<StartupReport>(from);
        }
        else
        {
            string work = Option(args, "--work") ?? Path.Combine(Path.GetTempPath(), "releasegate-startup-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(work);
                string pdf = Option(args, "--pdf") ?? Path.Combine(work, ProbeDocument.FileName);
                if (Option(args, "--pdf") == null) File.WriteAllBytes(pdf, ProbeDocument.Create());

                var harness = new StartupHarness
                {
                    AppDirectory = Path.GetFullPath(Option(args, "--app") ?? Path.Combine("publish", "app")),
                    DocumentPath = Path.GetFullPath(pdf),
                    WorkDirectory = work,
                    WarmRuns = IntOption(args, "--runs", 10, min: 3),
                    Warmup = IntOption(args, "--warmup", 2, min: 1),
                    ColdRuns = IntOption(args, "--cold", 3, min: 0),
                    Timeout = TimeSpan.FromSeconds(IntOption(args, "--timeout", 90, min: 5)),
                };

                Console.WriteLine($"startup: {harness.ColdRuns} cold, {harness.Warmup} warm-up + {harness.WarmRuns} warm launches of {harness.AppDirectory}");
                current = harness.Run(Option(args, "--environment") ?? "local");
            }
            finally
            {
                // Only the directory this run made up is removed; one named with --work is the caller's.
                if (!args.Contains("--keep") && Option(args, "--work") == null) TryDelete(work);
                else Console.WriteLine($"kept {work}");
            }
        }

        Console.WriteLine($"warm: median {Format.Ms(current.Warm!.Median)}, p95 {Format.Ms(current.Warm.P95)} over {current.Warm.Runs} launches");
        if (current.Cold != null)
            Console.WriteLine($"cold: median {Format.Ms(current.Cold.Median)}, p95 {Format.Ms(current.Cold.P95)} over {current.Cold.Runs} launches");

        if (Option(args, "--out") is { } outPath) Write(outPath, current);

        string? baselinePath = Option(args, "--baseline");
        if (baselinePath != null && args.Contains("--write-baseline"))
        {
            var previous = File.Exists(baselinePath) ? Read<StartupReport>(baselinePath) : null;
            current.Tolerance = previous?.Tolerance ?? new StartupTolerance();
            Write(baselinePath, current);
            Console.WriteLine($"baseline written: {baselinePath}");
            return 0;
        }

        if (baselinePath != null && args.Contains("--check"))
        {
            var outcome = StartupGate.Compare(Read<StartupReport>(baselinePath), current);
            outcome.Report("Startup gate");
            return outcome.Passed ? 0 : 1;
        }

        return 0;
    }

    private static int Payload(string[] args)
    {
        PayloadReport current = Option(args, "--from") is { } from
            ? Read<PayloadReport>(from)
            : PayloadGate.Measure(Option(args, "--dir") ?? Path.Combine("publish", "app"), Option(args, "--installer"));

        Console.WriteLine($"payload: {current.FileCount} files, {Format.Bytes(current.TotalBytes)}, {Format.Bytes(current.CompressedBytes)} compressed" +
                          (current.InstallerBytes is long i ? $", installer {Format.Bytes(i)}" : ""));

        if (Option(args, "--out") is { } outPath) Write(outPath, current);

        string? baselinePath = Option(args, "--baseline");
        if (baselinePath != null && args.Contains("--write-baseline"))
        {
            var previous = File.Exists(baselinePath) ? Read<PayloadReport>(baselinePath) : null;
            var tolerance = previous?.Tolerance ?? new PayloadTolerance();

            // A budget is the one limit a refresh cannot move: raising it is a separate, visible edit.
            if (tolerance.BudgetBytes is long cap && current.TotalBytes > cap)
                throw new InvalidOperationException($"The payload ({Format.Bytes(current.TotalBytes)}) is over its budget ({Format.Bytes(cap)}); raise budgetBytes in {baselinePath} deliberately, not by refreshing.");
            if (tolerance.CompressedBudgetBytes is long ccap && current.CompressedBytes > ccap)
                throw new InvalidOperationException($"The compressed payload ({Format.Bytes(current.CompressedBytes)}) is over its budget ({Format.Bytes(ccap)}); raise compressedBudgetBytes in {baselinePath} deliberately, not by refreshing.");

            current.Tolerance = tolerance;
            current.Note = previous?.Note;
            // A dry-run publish has no installer: keep the last measured one rather than lose it.
            current.InstallerBytes ??= previous?.InstallerBytes;
            Write(baselinePath, current);
            Console.WriteLine($"baseline written: {baselinePath}");
            return 0;
        }

        if (baselinePath != null && args.Contains("--check"))
        {
            var outcome = PayloadGate.Compare(Read<PayloadReport>(baselinePath), current);
            outcome.Report("Installer payload size gate");
            return outcome.Passed ? 0 : 1;
        }

        return 0;
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), PayloadGate.Json)
        ?? throw new FormatException($"'{path}' is empty.");

    private static void Write<T>(string path, T value)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, PayloadGate.Json) + "\n");
    }

    private static void TryDelete(string dir)
    {
        // The application has exited, but its extraction directory can stay locked for a moment.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }

        Console.WriteLine($"note: could not remove {dir}");
    }

    internal static string? Option(string[] args, string name) =>
        Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    private static int IntOption(string[] args, string name, int fallback, int min)
    {
        string? text = Option(args, name);
        if (text == null) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min)
            throw new ArgumentException($"{name} must be a whole number of at least {min}.");
        return value;
    }
}
