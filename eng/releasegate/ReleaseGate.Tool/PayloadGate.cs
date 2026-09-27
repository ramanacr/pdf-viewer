using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReleaseGate.Tool;

/// <summary>
/// What the installer carries: every file of the payload build_publish.ps1 stages (and
/// Payload.zip embeds), its total, and its size once compressed the way the installer
/// compresses it. Also the baseline format, when <see cref="Tolerance"/> is set.
/// </summary>
internal sealed class PayloadReport
{
    public int Schema { get; set; } = 1;
    public DateTime RecordedUtc { get; set; }
    public string? Note { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>The payload zipped at optimal compression, as Payload.zip is: what the installer size follows.</summary>
    public long CompressedBytes { get; set; }

    /// <summary>PdfViewerSetup.exe, when one was built and measured.</summary>
    public long? InstallerBytes { get; set; }

    /// <summary>Relative path (forward slashes) to size, sorted so the committed file diffs cleanly.</summary>
    public SortedDictionary<string, long> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// For each single-file bundle in the payload (PdfViewer.exe), what is inside it and how many
    /// bytes each part occupies: the exe is nearly the whole payload, so this is where growth hides.
    /// </summary>
    public SortedDictionary<string, SortedDictionary<string, long>>? Bundles { get; set; }

    public PayloadTolerance? Tolerance { get; set; }

    /// <summary>Every bundled file as "exe > path", for diffing and listing alongside the loose files.</summary>
    public SortedDictionary<string, long> BundledFlat()
    {
        var flat = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (exe, files) in Bundles ?? new())
            foreach (var (path, bytes) in files)
                flat[$"{exe} > {path}"] = bytes;
        return flat;
    }
}

internal sealed class PayloadTolerance
{
    /// <summary>Growth allowed over the baseline before the gate fails (0.05 = 5 %).</summary>
    public double Relative { get; set; } = 0.05;

    /// <summary>A hard ceiling on the uncompressed payload that no baseline refresh can raise.</summary>
    public long? BudgetBytes { get; set; }

    /// <summary>A hard ceiling on the compressed payload that no baseline refresh can raise.</summary>
    public long? CompressedBudgetBytes { get; set; }
}

internal static class PayloadGate
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>How many of the largest and most-grown files a report lists.</summary>
    public const int ListLength = 10;

    public static PayloadReport Measure(string payloadDir, string? installerPath)
    {
        if (!Directory.Exists(payloadDir))
            throw new DirectoryNotFoundException($"No payload at '{payloadDir}'. Run scripts/build_publish.ps1 -DryRun first.");

        string root = Path.GetFullPath(payloadDir);
        var report = new PayloadReport { RecordedUtc = DateTime.UtcNow };
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            report.Files[rel] = new FileInfo(file).Length;
        }

        if (report.Files.Count == 0) throw new InvalidOperationException($"The payload at '{payloadDir}' is empty.");

        report.FileCount = report.Files.Count;
        report.TotalBytes = report.Files.Values.Sum();
        report.CompressedBytes = CompressedSize(root, report.Files.Keys);

        foreach (string exe in report.Files.Keys.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (BundleManifest.TryRead(Path.Combine(root, exe)) is { } bundled)
            {
                report.Bundles ??= new(StringComparer.Ordinal);
                report.Bundles[exe] = bundled;
            }
        }

        if (installerPath != null)
        {
            if (!File.Exists(installerPath)) throw new FileNotFoundException("No installer to measure.", installerPath);
            report.InstallerBytes = new FileInfo(installerPath).Length;
        }

        return report;
    }

    /// <summary>
    /// The size of the payload as a zip at optimal compression - the setting Compress-Archive
    /// uses for Payload.zip - counted without writing the archive anywhere.
    /// </summary>
    private static long CompressedSize(string root, IEnumerable<string> relativePaths)
    {
        using var counter = new CountingStream();
        using (var zip = new ZipArchive(counter, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string rel in relativePaths)
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = File.OpenRead(Path.Combine(root, rel));
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }

        return counter.Length;
    }

    /// <summary>
    /// Fails when the payload (uncompressed or compressed) or the installer grew past the
    /// baseline's tolerance, or past a hard budget. Whenever it fails, or the file set changed,
    /// the report names the files responsible.
    /// </summary>
    public static GateOutcome Compare(PayloadReport baseline, PayloadReport current)
    {
        var tolerance = baseline.Tolerance ?? new PayloadTolerance();
        var rule = new Tolerance(tolerance.Relative, 0);
        var outcome = new GateOutcome();

        void Check(string measure, double b, double c, long? budget)
        {
            double limit = rule.Limit(b);
            string limitText = Format.Bytes(limit) + (budget is long cap ? $", budget {Format.Bytes(cap)}" : "");
            string change = Format.Percent(Tolerance.Change(b, c));

            if (c > limit)
            {
                outcome.Failures.Add($"{measure} grew {change}, from {Format.Bytes(b)} to {Format.Bytes(c)} (limit {Format.Bytes(limit)}, +{tolerance.Relative * 100:0.#} %)");
                outcome.Row(measure, Format.Bytes(b), $"{Format.Bytes(c)} ({change})", limitText, "regressed");
            }
            else if (budget is long cap2 && c > cap2)
            {
                outcome.Failures.Add($"{measure} is {Format.Bytes(c)}, over its hard budget of {Format.Bytes(cap2)}");
                outcome.Row(measure, Format.Bytes(b), $"{Format.Bytes(c)} ({change})", limitText, "over budget");
            }
            else
            {
                bool shrank = c < b * (1 - tolerance.Relative);
                if (shrank) outcome.Notes.Add($"{measure} shrank {change}: refresh the baseline so the gate holds the gain");
                outcome.Row(measure, Format.Bytes(b), $"{Format.Bytes(c)} ({change})", limitText, shrank ? "improved" : "within tolerance");
            }
        }

        Check("payload", baseline.TotalBytes, current.TotalBytes, tolerance.BudgetBytes);
        Check("payload compressed", baseline.CompressedBytes, current.CompressedBytes, tolerance.CompressedBudgetBytes);
        if (baseline.InstallerBytes is long bi && current.InstallerBytes is long ci)
        {
            Check("installer", bi, ci, null);
        }
        else if (current.InstallerBytes is long only)
        {
            outcome.Notes.Add($"installer is {Format.Bytes(only)}; the baseline has no installer size to compare with");
        }
        else if (baseline.InstallerBytes is long notBuilt)
        {
            // A dry-run publish builds no installer; the compressed payload is what it would carry.
            outcome.Row("installer", Format.Bytes(notBuilt), "not built", "-", "follows payload compressed");
        }

        var files = Diff(baseline.Files, current.Files);
        var bundled = Diff(baseline.BundledFlat(), current.BundledFlat());
        if (files.Added.Count > 0 || files.Removed.Count > 0)
        {
            outcome.Notes.Add($"the file set changed: {files.Added.Count} added, {files.Removed.Count} removed");
        }

        if (bundled.Added.Count > 0 || bundled.Removed.Count > 0)
        {
            outcome.Notes.Add($"the files bundled into the executable changed: {bundled.Added.Count} added, {bundled.Removed.Count} removed");
        }

        if (!outcome.Passed || !files.IsEmpty || bundled.Added.Count > 0 || bundled.Removed.Count > 0)
        {
            outcome.Details.AddRange(Explain(current, files, bundled));
        }

        return outcome;
    }

    internal sealed record FileDiff(
        List<(string Path, long Bytes)> Added,
        List<(string Path, long Bytes)> Removed,
        List<(string Path, long Before, long After)> Changed)
    {
        public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
    }

    public static FileDiff Diff(IReadOnlyDictionary<string, long> baseline, IReadOnlyDictionary<string, long> current)
    {
        var added = current.Where(f => !baseline.ContainsKey(f.Key))
            .Select(f => (f.Key, f.Value)).OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).ToList();
        var removed = baseline.Where(f => !current.ContainsKey(f.Key))
            .Select(f => (f.Key, f.Value)).OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).ToList();
        var changed = current
            .Where(f => baseline.TryGetValue(f.Key, out long before) && before != f.Value)
            .Select(f => (f.Key, baseline[f.Key], f.Value))
            .OrderByDescending(f => f.Item3 - f.Item2).ThenBy(f => f.Key, StringComparer.Ordinal).ToList();
        return new FileDiff(added, removed, changed);
    }

    /// <summary>
    /// The lines a failure shows: what was added or removed, what grew most, and what is largest -
    /// for the loose files and for what is bundled inside the executable.
    /// </summary>
    public static List<string> Explain(PayloadReport current, FileDiff files, FileDiff bundled)
    {
        var lines = new List<string>();

        void AddedAndRemoved(FileDiff diff, string what)
        {
            if (diff.Added.Count > 0)
            {
                lines.Add($"Added {what} ({diff.Added.Count}, {Format.Bytes(diff.Added.Sum(f => f.Bytes))}):");
                foreach (var (path, bytes) in diff.Added) lines.Add($"  + {Format.Bytes(bytes),12}  {path}");
            }

            if (diff.Removed.Count > 0)
            {
                lines.Add($"Removed {what} ({diff.Removed.Count}, {Format.Bytes(diff.Removed.Sum(f => f.Bytes))}):");
                foreach (var (path, bytes) in diff.Removed) lines.Add($"  - {Format.Bytes(bytes),12}  {path}");
            }
        }

        AddedAndRemoved(files, "files");
        AddedAndRemoved(bundled, "bundled files");

        var grown = files.Changed.Concat(bundled.Changed).Where(c => c.After > c.Before)
            .OrderByDescending(c => c.After - c.Before).ThenBy(c => c.Path, StringComparer.Ordinal).Take(ListLength).ToList();
        if (grown.Count > 0)
        {
            lines.Add("Largest growth:");
            foreach (var (path, before, after) in grown)
                lines.Add($"  {Format.SignedBytes(after - before),12}  {path}  ({Format.Bytes(before)} -> {Format.Bytes(after)})");
        }

        lines.Add($"Largest files ({current.FileCount} files, {Format.Bytes(current.TotalBytes)} in total):");
        foreach (var (path, bytes) in current.Files.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(ListLength))
            lines.Add($"  {Format.Bytes(bytes),12}  {path}");

        foreach (var (exe, inside) in current.Bundles ?? new())
        {
            lines.Add($"Largest inside {exe} ({inside.Count} bundled files):");
            foreach (var (path, bytes) in inside.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(ListLength))
                lines.Add($"  {Format.Bytes(bytes),12}  {path}");
        }

        return lines;
    }

    /// <summary>A write-only stream that only counts, so the archive size is known without keeping the archive.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
