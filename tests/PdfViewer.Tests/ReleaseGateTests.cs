using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PdfEngine;
using PdfEngine.Pdfium;
using PdfViewer.Services;
using ReleaseGate.Tool;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The release gates (eng/releasegate): the statistics and tolerance arithmetic they decide
/// with, the comparisons against a baseline, the probe result contract between the application
/// and the harness, the synthetic probe document, and the committed baselines themselves.
/// </summary>
public class ReleaseGateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    // ---- statistics -------------------------------------------------------------------------

    [Fact]
    public void Median_IsTheMiddleValue_OrTheMeanOfTheTwoMiddleValues()
    {
        Assert.Equal(3, Stats.Median(new double[] { 1, 3, 9 }));
        Assert.Equal(4, Stats.Median(new double[] { 1, 3, 5, 9 }));
        Assert.Equal(7, Stats.Median(new double[] { 7 }));
    }

    [Fact]
    public void Percentile_IsNearestRank_AndAlwaysAMeasuredValue()
    {
        var ten = Enumerable.Range(1, 10).Select(i => (double)i * 100).ToArray();
        Assert.Equal(1000, Stats.Percentile(ten, 95)); // ceil(9.5) = 10th
        Assert.Equal(500, Stats.Percentile(ten, 50));  // ceil(5) = 5th
        Assert.Equal(100, Stats.Percentile(ten, 1));

        var twenty = Enumerable.Range(1, 20).Select(i => (double)i).ToArray();
        Assert.Equal(19, Stats.Percentile(twenty, 95)); // ceil(19) = 19th
        Assert.Throws<ArgumentOutOfRangeException>(() => Stats.Percentile(ten, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stats.Percentile(ten, 101));
    }

    [Fact]
    public void Summarize_SortsItsInput()
    {
        var s = Stats.Summarize(new double[] { 900, 100, 500, 300, 700 });
        Assert.Equal(5, s.Count);
        Assert.Equal(500, s.Median);
        Assert.Equal(900, s.P95);
        Assert.Equal(100, s.Min);
        Assert.Equal(900, s.Max);
        Assert.Equal(500, s.Mean);
    }

    [Fact]
    public void Summarize_RefusesNothingOrNonsense()
    {
        // A gate that measured nothing must not pass by default.
        Assert.Throws<ArgumentException>(() => Stats.Summarize(Array.Empty<double>()));
        Assert.Throws<ArgumentException>(() => Stats.Summarize(new[] { 1.0, double.NaN }));
        Assert.Throws<ArgumentException>(() => Stats.Summarize(new[] { double.PositiveInfinity }));
    }

    // ---- tolerance --------------------------------------------------------------------------

    [Fact]
    public void Tolerance_LimitIsRelativePlusAbsolute()
    {
        var t = new Tolerance(0.5, 250);
        Assert.Equal(1000 * 1.5 + 250, t.Limit(1000), 9);
        Assert.False(t.IsExceeded(1000, 1750)); // exactly at the limit passes
        Assert.True(t.IsExceeded(1000, 1750.1));
        Assert.Equal(1.05 * 2000, new Tolerance(0.05, 0).Limit(2000), 9);
    }

    [Fact]
    public void Tolerance_RejectsNegativeValues()
    {
        Assert.Throws<InvalidOperationException>(() => new Tolerance(-0.1, 0).Limit(100));
        Assert.Throws<InvalidOperationException>(() => new Tolerance(0.1, -1).Limit(100));
    }

    [Fact]
    public void Change_IsRelative_AndZeroForAZeroBaseline()
    {
        Assert.Equal(0.1, Tolerance.Change(100, 110), 9);
        Assert.Equal(-0.25, Tolerance.Change(100, 75), 9);
        Assert.Equal(0, Tolerance.Change(0, 50));
    }

    // ---- probe result contract --------------------------------------------------------------

    private static string ProbeJson(string milestones, string error = "null") =>
        $$"""{ "schema": 1, "engine": "Auto", "milestones": { {{milestones}} }, "error": {{error}} }""";

    [Fact]
    public void ProbeResult_ReadsMilestones()
    {
        var r = ProbeResult.Parse(ProbeJson("\"appStartup\": 400.5, \"firstPageSurface\": 900, \"firstPageRendered\": 950.25"));
        Assert.Equal(950.25, r.FirstPageMs);
        Assert.Equal(400.5, r.Milestones["appStartup"]);
        Assert.Equal("Auto", r.Engine);
    }

    [Theory]
    [InlineData("\"appStartup\": 400", "null")]                                    // page 1 never drawn
    [InlineData("\"appStartup\": 400, \"firstPageRendered\": 300", "null")]        // drawn before the app started
    [InlineData("\"firstPageSurface\": 900, \"firstPageRendered\": 800", "null")]  // on screen before it had a surface
    [InlineData("\"appStartup\": -1, \"firstPageRendered\": 300", "null")]         // negative time
    [InlineData("\"firstPageRendered\": 300", "\"page 1 was not rendered within 60 s\"")]
    public void ProbeResult_RejectsWhatCannotBeAMeasurement(string milestones, string error)
    {
        Assert.Throws<FormatException>(() => ProbeResult.Parse(ProbeJson(milestones, error)));
    }

    [Fact]
    public void ProbeResult_RejectsAnUnknownSchema()
    {
        Assert.Throws<FormatException>(() => ProbeResult.Parse("""{ "schema": 2, "milestones": { "firstPageRendered": 1 } }"""));
    }

    [Fact]
    public void ApplicationProbe_WritesWhatTheHarnessReads()
    {
        // The two ends of the contract are separate programs; this is the one place they meet.
        string dir = Path.Combine(Path.GetTempPath(), "probe-contract-" + Guid.NewGuid().ToString("N"));
        var probe = StartupProbe.TryCreate(new[] { "--startup-probe", Path.Combine(dir, "r.json"), Path.Combine(dir, "d.pdf") });
        Assert.NotNull(probe);
        Assert.StartsWith(dir, probe!.SettingsDirectory, StringComparison.OrdinalIgnoreCase);

        foreach (string m in new[] { "appStartup", "windowLoaded", "documentOpenStart", "windowShown", "firstPageSurface", "firstPageRendered" })
            probe.Mark(m);

        var parsed = ProbeResult.Parse(probe.ToJson(null));
        Assert.Equal(6, parsed.Milestones.Count);
        Assert.True(parsed.FirstPageMs >= parsed.Milestones["appStartup"]);
        Assert.Throws<FormatException>(() => ProbeResult.Parse(probe.ToJson("document not found")));
    }

    [Fact]
    public void ApplicationProbe_IsOffWithoutTheSwitch()
    {
        Assert.Null(StartupProbe.TryCreate(Array.Empty<string>()));
        Assert.Null(StartupProbe.TryCreate(new[] { "document.pdf" }));
        Assert.Null(StartupProbe.TryCreate(new[] { "--startup-probe", "result.json" })); // no document
        Assert.Null(StartupProbe.TryCreate(new[] { "/p", "a.pdf", "b" }));
    }

    // ---- startup gate -----------------------------------------------------------------------

    private static ProbeResult Run(double firstPage, double app = 500) => new()
    {
        Milestones = new Dictionary<string, double> { ["appStartup"] = app, ["firstPageRendered"] = firstPage },
    };

    private static StartupReport Report(double warmMedian, double? coldMedian = null, string env = "ci", string sha = "AB") => new()
    {
        Environment = env,
        DocumentSha256 = sha,
        Warm = RunSet.From(new[] { Run(warmMedian - 10), Run(warmMedian), Run(warmMedian + 10) }, 2),
        Cold = coldMedian is double c ? RunSet.From(new[] { Run(c) }, 0) : null,
        Tolerance = new StartupTolerance { Relative = 0.5, AbsoluteMs = 250 },
    };

    [Fact]
    public void RunSet_SummarisesTheMeasuredLaunchesAndEveryMilestone()
    {
        var set = RunSet.From(new[] { Run(1200, 400), Run(1000, 300), Run(1100, 500) }, discarded: 2);
        Assert.Equal(3, set.Runs);
        Assert.Equal(2, set.Discarded);
        Assert.Equal(1100, set.Median);
        Assert.Equal(1200, set.P95);
        Assert.Equal(new[] { 1200.0, 1000, 1100 }, set.Samples);
        Assert.Equal(400, set.MilestoneMedians["appStartup"]);
        Assert.Equal(1100, set.MilestoneMedians["firstPageRendered"]);
    }

    [Fact]
    public void StartupGate_PassesWithinTolerance()
    {
        // limit = 1000 * 1.5 + 250 = 1750
        var outcome = StartupGate.Compare(Report(1000), Report(1750));
        Assert.True(outcome.Passed);
        Assert.Contains(outcome.Rows, r => r.Contains("within tolerance"));
    }

    [Fact]
    public void StartupGate_FailsWhenTheWarmMedianRegressesPastTheLimit()
    {
        var outcome = StartupGate.Compare(Report(1000), Report(1800));
        Assert.False(outcome.Passed);
        string failure = Assert.Single(outcome.Failures);
        Assert.Contains("1000 ms -> 1800 ms", failure);
        Assert.Contains("+80.0 %", failure);
        Assert.Contains("1750 ms", failure);
    }

    [Fact]
    public void StartupGate_ReportsButDoesNotFailOnAColdRegression()
    {
        var outcome = StartupGate.Compare(Report(1000, coldMedian: 2000), Report(1000, coldMedian: 9000));
        Assert.True(outcome.Passed);
        Assert.Contains(outcome.Notes, n => n.StartsWith("cold start median", StringComparison.Ordinal));
    }

    [Fact]
    public void StartupGate_AsksForARefreshWhenStartupImproved()
    {
        var outcome = StartupGate.Compare(Report(3000), Report(1000));
        Assert.True(outcome.Passed);
        Assert.Contains(outcome.Notes, n => n.Contains("refresh the baseline so the gate holds the gain"));
    }

    [Fact]
    public void StartupGate_HoldsAnotherEnvironmentToTheCrossEnvironmentTolerance()
    {
        var baseline = Report(1000, env: "developer workstation", sha: "AA");
        baseline.Tolerance!.CrossEnvironmentRelative = 1.0;
        baseline.Tolerance.CrossEnvironmentAbsoluteMs = 500;

        // limit = 1000 * 2 + 500 = 2500, where the same environment would stop at 1750.
        var within = StartupGate.Compare(baseline, Report(2400, env: "github windows-latest", sha: "AA"));
        Assert.True(within.Passed);
        Assert.Contains(within.Notes, n => n.Contains("'developer workstation'") && n.Contains("cross-environment tolerance"));
        Assert.Contains(within.Rows, r => r.Contains("2500 ms (+100 % +500 ms)"));

        var beyond = StartupGate.Compare(baseline, Report(2600, env: "github windows-latest", sha: "AA"));
        Assert.False(beyond.Passed);

        // Faster hardware is not an improvement to lock in.
        var faster = StartupGate.Compare(baseline, Report(300, env: "github windows-latest", sha: "AA"));
        Assert.DoesNotContain(faster.Notes, n => n.Contains("refresh the baseline so the gate holds the gain"));
    }

    [Fact]
    public void StartupGate_FlagsADifferentProbeDocument()
    {
        var outcome = StartupGate.Compare(Report(1000, sha: "AA"), Report(1000, sha: "BB"));
        Assert.Contains(outcome.Notes, n => n.Contains("probe document differs"));
    }

    [Fact]
    public void StartupReport_RoundTripsThroughTheBaselineFormat()
    {
        var report = Report(1234, 5678);
        report.Tolerance!.Rationale = "why";
        string json = JsonSerializer.Serialize(report, StartupGate.Json);
        var back = JsonSerializer.Deserialize<StartupReport>(json, StartupGate.Json)!;
        Assert.Equal(1234, back.Warm!.Median);
        Assert.Equal(5678, back.Cold!.Median);
        Assert.Equal(0.5, back.Tolerance!.Relative);
        Assert.Equal("why", back.Tolerance.Rationale);
        Assert.Contains("\"warm\"", json); // camelCase, as committed
    }

    // ---- payload gate -----------------------------------------------------------------------

    private static PayloadReport Payload(Dictionary<string, long> files, double compressedRatio = 0.4, long? installer = null)
    {
        var report = new PayloadReport
        {
            Files = new SortedDictionary<string, long>(files, StringComparer.Ordinal),
            FileCount = files.Count,
            TotalBytes = files.Values.Sum(),
            InstallerBytes = installer,
            Tolerance = new PayloadTolerance { Relative = 0.05 },
        };
        report.CompressedBytes = (long)(report.TotalBytes * compressedRatio);
        return report;
    }

    private static readonly Dictionary<string, long> BaseFiles = new()
    {
        ["PdfViewer.exe"] = 40_000_000,
        ["THIRD_PARTY_NOTICES.md"] = 8_000,
        ["assets/app_icon.ico"] = 12_000,
    };

    [Fact]
    public void PayloadGate_PassesUpToTheRelativeTolerance()
    {
        var grown = new Dictionary<string, long>(BaseFiles) { ["PdfViewer.exe"] = 40_000_000 + 2_000_000 }; // +5.0 %
        var outcome = PayloadGate.Compare(Payload(BaseFiles), Payload(grown));
        Assert.True(outcome.Passed, string.Join("\n", outcome.Failures));
        Assert.Empty(outcome.Details); // nothing to explain: same files, within tolerance
    }

    [Fact]
    public void PayloadGate_FailsOnGrowth_AndNamesTheAddedAndLargestFiles()
    {
        var grown = new Dictionary<string, long>(BaseFiles)
        {
            ["PdfViewer.exe"] = 41_000_000,
            ["runtimes/win-x64/native/stray.dll"] = 3_000_000,
        };
        var outcome = PayloadGate.Compare(Payload(BaseFiles), Payload(grown));

        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Failures, f => f.StartsWith("payload grew", StringComparison.Ordinal));
        string details = string.Join("\n", outcome.Details);
        Assert.Contains("Added files (1", details);
        Assert.Contains("runtimes/win-x64/native/stray.dll", details);
        Assert.Contains("Largest growth:", details);
        Assert.Contains("PdfViewer.exe", details);
        Assert.Contains("Largest files (4 files", details);
        // The largest file is listed first.
        var largest = outcome.Details.SkipWhile(l => !l.StartsWith("Largest files", StringComparison.Ordinal)).Skip(1).First();
        Assert.Contains("PdfViewer.exe", largest);
    }

    [Fact]
    public void PayloadGate_FailsWhenOnlyTheCompressedSizeGrows()
    {
        // Same uncompressed bytes, but content that no longer compresses (e.g. a pre-compressed blob).
        var outcome = PayloadGate.Compare(Payload(BaseFiles, 0.40), Payload(BaseFiles, 0.43));
        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Failures, f => f.StartsWith("payload compressed grew", StringComparison.Ordinal));
    }

    [Fact]
    public void PayloadGate_EnforcesTheBudget_EvenWithinTolerance()
    {
        var baseline = Payload(BaseFiles);
        baseline.Tolerance!.BudgetBytes = 40_050_000;
        var grown = new Dictionary<string, long>(BaseFiles) { ["PdfViewer.exe"] = 40_100_000 }; // +0.25 %
        var outcome = PayloadGate.Compare(baseline, Payload(grown));
        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Failures, f => f.Contains("hard budget"));
    }

    [Fact]
    public void PayloadGate_ComparesTheInstallerWhenBothHaveOne()
    {
        var outcome = PayloadGate.Compare(Payload(BaseFiles, installer: 20_000_000), Payload(BaseFiles, installer: 22_000_000));
        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Failures, f => f.StartsWith("installer grew", StringComparison.Ordinal));

        var noBaselineInstaller = PayloadGate.Compare(Payload(BaseFiles), Payload(BaseFiles, installer: 22_000_000));
        Assert.True(noBaselineInstaller.Passed);
        Assert.Contains(noBaselineInstaller.Notes, n => n.Contains("no installer size"));
    }

    [Fact]
    public void PayloadGate_ExplainsARemovedFile_AndAsksForARefreshWhenItShrank()
    {
        var smaller = new Dictionary<string, long>(BaseFiles) { ["PdfViewer.exe"] = 30_000_000 };
        smaller.Remove("THIRD_PARTY_NOTICES.md");
        var outcome = PayloadGate.Compare(Payload(BaseFiles), Payload(smaller));
        Assert.True(outcome.Passed);
        Assert.Contains(outcome.Notes, n => n.Contains("shrank"));
        Assert.Contains(outcome.Details, l => l.Contains("- ") && l.Contains("THIRD_PARTY_NOTICES.md"));
    }

    [Fact]
    public void Diff_OrdersBySizeThenPath()
    {
        var baseline = Payload(new Dictionary<string, long> { ["a"] = 10, ["b"] = 10, ["gone"] = 5 });
        var current = Payload(new Dictionary<string, long> { ["a"] = 30, ["b"] = 11, ["new2"] = 7, ["new1"] = 7 });
        var diff = PayloadGate.Diff(baseline.Files, current.Files);
        Assert.Equal(new[] { "new1", "new2" }, diff.Added.Select(a => a.Path));
        Assert.Equal("gone", Assert.Single(diff.Removed).Path);
        Assert.Equal(new[] { "a", "b" }, diff.Changed.Select(c => c.Path));
    }

    [Fact]
    public void PayloadGate_NamesWhatGrewInsideTheExecutable()
    {
        PayloadReport WithBundle(long dll, long? extra)
        {
            var report = Payload(BaseFiles);
            var inside = new SortedDictionary<string, long>(StringComparer.Ordinal) { ["PdfViewer.dll"] = dll, ["Microsoft.Windows.SDK.NET.dll"] = 25_000_000 };
            if (extra is long e) inside["Newtonsoft.Json.dll"] = e;
            report.Bundles = new(StringComparer.Ordinal) { ["PdfViewer.exe"] = inside };
            return report;
        }

        var baseline = WithBundle(5_000_000, null);
        var grown = WithBundle(7_000_000, 700_000);
        grown.TotalBytes += 2_700_000;
        grown.CompressedBytes += 1_000_000;

        var outcome = PayloadGate.Compare(baseline, grown);
        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Notes, n => n.Contains("bundled into the executable changed: 1 added"));
        string details = string.Join("\n", outcome.Details);
        Assert.Contains("Added bundled files (1", details);
        Assert.Contains("PdfViewer.exe > Newtonsoft.Json.dll", details);
        Assert.Contains("PdfViewer.exe > PdfViewer.dll  (4.77 MiB -> 6.68 MiB)", details);
        Assert.Contains("Largest inside PdfViewer.exe (3 bundled files):", details);
    }

    /// <summary>A single-file bundle as the .NET host writes it: apphost bytes, header offset and signature, then the manifest.</summary>
    private static byte[] FakeBundle(uint major, params (string Path, long Size, long Compressed)[] files)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes("MZ fake apphost "));
        long offsetSlot = ms.Position;
        w.Write(0L);
        w.Write(BundleManifest.Signature);
        w.Write(new byte[64]);
        long payloadStart = ms.Position;
        foreach (var f in files) w.Write(new byte[f.Compressed > 0 ? f.Compressed : f.Size]);

        long header = ms.Position;
        w.Write(major);
        w.Write(0u);
        w.Write(files.Length);
        w.Write("bundle-id");
        if (major >= 2) { w.Write(0L); w.Write(0L); w.Write(0L); w.Write(0L); w.Write(0UL); }
        long at = payloadStart;
        foreach (var f in files)
        {
            w.Write(at);
            w.Write(f.Size);
            if (major >= 6) w.Write(f.Compressed);
            w.Write((byte)1);
            w.Write(f.Path);
            at += f.Compressed > 0 ? f.Compressed : f.Size;
        }

        ms.Position = offsetSlot;
        w.Write(header);
        return ms.ToArray();
    }

    [Fact]
    public void BundleManifest_ListsTheBundledFiles_WithTheBytesTheyOccupy()
    {
        var files = BundleManifest.TryRead(FakeBundle(6, ("PdfViewer.dll", 300, 0), (@"runtimes\win-x64\native\pdfium.dll", 900, 400)));
        Assert.NotNull(files);
        Assert.Equal(300, files!["PdfViewer.dll"]);
        Assert.Equal(400, files["runtimes/win-x64/native/pdfium.dll"]); // compressed: what it costs on disk

        var v1 = BundleManifest.TryRead(FakeBundle(1, ("a.dll", 10, 0)));
        Assert.Equal(10, Assert.Single(v1!).Value);
    }

    [Fact]
    public void BundleManifest_IsNullForAnythingElse()
    {
        Assert.Null(BundleManifest.TryRead(Encoding.ASCII.GetBytes("MZ just an ordinary executable")));

        // An apphost that was never bundled carries the signature with a zero offset.
        byte[] unbundled = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0, 0, 0, 0, 0 }.Concat(BundleManifest.Signature).ToArray();
        Assert.Null(BundleManifest.TryRead(unbundled));

        // A manifest that runs off the end of the file.
        byte[] bundle = FakeBundle(6, ("a.dll", 10, 0), ("b.dll", 20, 0));
        Assert.Null(BundleManifest.TryRead(bundle[..^6]));
    }

    [Fact]
    public void Measure_SizesEveryFile_WithForwardSlashes_AndCompresses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "payload-measure-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Fonts", "CMaps"));
            File.WriteAllText(Path.Combine(dir, "app.txt"), new string('x', 100_000));
            File.WriteAllBytes(Path.Combine(dir, "Fonts", "CMaps", "LICENSE.md"), Encoding.ASCII.GetBytes("licence"));

            var report = PayloadGate.Measure(dir, installerPath: null);
            Assert.Equal(2, report.FileCount);
            Assert.Equal(100_007, report.TotalBytes);
            Assert.Equal(7, report.Files["Fonts/CMaps/LICENSE.md"]);
            Assert.InRange(report.CompressedBytes, 1, report.TotalBytes / 10); // highly repetitive content
            Assert.Null(report.InstallerBytes);

            // Deterministic: the same payload measures the same.
            Assert.Equal(report.CompressedBytes, PayloadGate.Measure(dir, null).CompressedBytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Measure_RefusesAMissingOrEmptyPayload()
    {
        string dir = Path.Combine(Path.GetTempPath(), "payload-empty-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => PayloadGate.Measure(dir, null));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Throws<InvalidOperationException>(() => PayloadGate.Measure(dir, null));
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Format_UsesBinaryUnitsAndSignedChanges()
    {
        Assert.Equal("512 B", Format.Bytes(512));
        Assert.Equal("1.50 KiB", Format.Bytes(1536));
        Assert.Equal("43.61 MiB", Format.Bytes(45_730_000));
        Assert.Equal("+1.00 MiB", Format.SignedBytes(1024 * 1024));
        Assert.Equal("-2.00 KiB", Format.SignedBytes(-2048));
        Assert.Equal("+5.0 %", Format.Percent(0.05));
        Assert.Equal("-12.5 %", Format.Percent(-0.125));
    }

    // ---- probe document ---------------------------------------------------------------------

    [Fact]
    public void ProbeDocument_IsDeterministic()
    {
        Assert.Equal(ProbeDocument.Create(), ProbeDocument.Create());
    }

    [Fact]
    public async Task ProbeDocument_OpensWithEveryPageAndItsText()
    {
        using IPdfEngine engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(ProbeDocument.Create());
        Assert.Equal(ProbeDocument.PageCount, doc.PageCount);
        string text = await engine.TextService.ExtractPageTextAsync(doc, 1);
        Assert.Contains("Startup probe - page 1 of 12", text);
    }

    // ---- committed baselines ----------------------------------------------------------------

    [Fact]
    public void CommittedStartupBaseline_IsARealMeasurementWithATolerance()
    {
        string path = Path.Combine(RepoRoot(), "eng", "releasegate", "baseline-startup.json");
        var baseline = JsonSerializer.Deserialize<StartupReport>(File.ReadAllText(path), StartupGate.Json)!;

        Assert.NotNull(baseline.Warm);
        Assert.True(baseline.Warm!.Runs >= 5);
        Assert.Equal(baseline.Warm.Runs, baseline.Warm.Samples.Count);
        Assert.Equal(Stats.Summarize(baseline.Warm.Samples).Median, baseline.Warm.Median, 1);
        Assert.NotNull(baseline.Tolerance);
        Assert.False(string.IsNullOrWhiteSpace(baseline.Tolerance!.Rationale));
        // It was measured on the document the harness generates today.
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ProbeDocument.Create())), baseline.DocumentSha256);
    }

    [Fact]
    public void CommittedPayloadBaseline_IsConsistent_AndWithinItsBudget()
    {
        string path = Path.Combine(RepoRoot(), "eng", "releasegate", "baseline-payload.json");
        var baseline = JsonSerializer.Deserialize<PayloadReport>(File.ReadAllText(path), PayloadGate.Json)!;

        Assert.Equal(baseline.Files.Count, baseline.FileCount);
        Assert.Equal(baseline.Files.Values.Sum(), baseline.TotalBytes);
        Assert.InRange(baseline.CompressedBytes, 1, baseline.TotalBytes);
        Assert.Contains("PdfViewer.exe", baseline.Files.Keys);
        // The executable's contents are itemised, so growth inside it can be named.
        Assert.NotNull(baseline.Bundles);
        Assert.Contains("PdfViewer.dll", baseline.Bundles!["PdfViewer.exe"].Keys);
        Assert.True(baseline.Bundles["PdfViewer.exe"].Values.Sum() <= baseline.Files["PdfViewer.exe"]);
        Assert.NotNull(baseline.Tolerance);
        if (baseline.Tolerance!.BudgetBytes is long cap) Assert.True(baseline.TotalBytes <= cap);
        if (baseline.Tolerance.CompressedBudgetBytes is long ccap) Assert.True(baseline.CompressedBytes <= ccap);

        // No symbols and no optional component: build_publish.ps1 moves those out of the payload.
        Assert.DoesNotContain(baseline.Files.Keys, f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("System.Speech.dll", baseline.Files.Keys);
    }
}
