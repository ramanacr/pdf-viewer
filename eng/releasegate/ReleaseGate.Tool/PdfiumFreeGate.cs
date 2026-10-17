using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ReleaseGate.Tool;

/// <summary>
/// The PDFium-free release gate (M10): the published application carries no PDFium and needs
/// none. Two checks, both on the payload <c>scripts/build_publish.ps1</c> stages:
///
/// 1. Files: no <c>pdfium*.dll</c> and no <c>PdfEngine.Pdfium</c> assembly anywhere in the payload,
///    loose or bundled inside a single-file executable, and no dependency manifest that names one.
/// 2. Launch: the application, started with <c>PDF_PDFIUM_DISABLED=1</c>, opens the probe document,
///    draws page 1 and finds the probe's heading on every page with its own search, and reports
///    that neither PDFium's managed adapter nor its native library was loaded into the process.
/// </summary>
internal static class PdfiumFreeGate
{
    /// <summary>The text the launch searches for: the heading on every page of the probe document.</summary>
    public const string SearchQuery = "Startup probe";

    /// <summary>A file, by its relative path, that is part of PDFium.</summary>
    public static bool IsPdfiumFile(string relativePath)
    {
        string name = Path.GetFileName(relativePath.Replace('\\', '/').Split('/').Last());
        return name.StartsWith("pdfium", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("PdfEngine.Pdfium", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every PDFium file in the payload: loose, inside a bundle ("PdfViewer.exe!pdfium.dll"), or named by a deps.json.</summary>
    public static List<string> FindPdfiumFiles(string payloadDir)
    {
        if (!Directory.Exists(payloadDir))
            throw new DirectoryNotFoundException($"No payload at '{payloadDir}'. Run scripts/build_publish.ps1 -DryRun first.");

        var found = new List<string>();
        foreach (string path in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(payloadDir, path).Replace('\\', '/');
            if (IsPdfiumFile(relative)) found.Add(relative);

            if (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && BundleManifest.TryRead(path) is { } bundled)
            {
                found.AddRange(bundled.Keys.Where(IsPdfiumFile).Select(f => $"{relative}!{f}"));
            }
            else if (relative.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) &&
                     File.ReadAllText(path).Contains("PdfEngine.Pdfium", StringComparison.OrdinalIgnoreCase))
            {
                found.Add($"{relative} (names PdfEngine.Pdfium)");
            }
        }

        return found;
    }

    /// <summary>Judges the payload's files and, when there was a launch, the probe's result file.</summary>
    public static GateOutcome Evaluate(IReadOnlyList<string> pdfiumFiles, string? probeJson, int expectedPages)
    {
        var outcome = new GateOutcome();

        if (pdfiumFiles.Count == 0)
        {
            outcome.Row("PDFium files in the payload", "none", "none", "none", "passed");
        }
        else
        {
            outcome.Row("PDFium files in the payload", "none", pdfiumFiles.Count.ToString(), "none", "FAILED");
            outcome.Failures.Add($"the payload ships PDFium: {string.Join(", ", pdfiumFiles)}");
        }

        if (probeJson == null)
        {
            outcome.Notes.Add("no launch: only the files were checked");
            return outcome;
        }

        ProbeResult probe;
        try
        {
            probe = ProbeResult.Parse(probeJson);
        }
        catch (FormatException ex)
        {
            outcome.Failures.Add($"the application did not open and draw the document with PDFium disabled: {ex.Message}");
            return outcome;
        }

        outcome.Row("page 1 drawn (PDF_PDFIUM_DISABLED=1)", "yes", Format.Ms(probe.FirstPageMs), "-", "passed");

        using var doc = JsonDocument.Parse(probeJson);
        var root = doc.RootElement;

        int matches = 0, pages = 0;
        if (root.TryGetProperty("search", out var search) && search.ValueKind == JsonValueKind.Object)
        {
            matches = search.TryGetProperty("matches", out var m) ? m.GetInt32() : 0;
            pages = search.TryGetProperty("pages", out var p) ? p.GetInt32() : 0;
        }
        bool searchOk = pages >= expectedPages && matches >= expectedPages;
        outcome.Row($"search \"{SearchQuery}\"", $"{expectedPages} pages", $"{matches} matches on {pages} pages", $"{expectedPages} pages", searchOk ? "passed" : "FAILED");
        if (!searchOk)
            outcome.Failures.Add($"search found \"{SearchQuery}\" on {pages} of {expectedPages} pages ({matches} matches) with PDFium disabled");

        if (!root.TryGetProperty("pdfium", out var pdfium) || pdfium.ValueKind != JsonValueKind.Object)
        {
            outcome.Failures.Add("the probe result does not say whether PDFium was loaded (an application older than this gate?)");
            return outcome;
        }

        var loaded = Strings(pdfium, "assemblies").Concat(Strings(pdfium, "nativeModules")).ToList();
        outcome.Row("PDFium loaded into the process", "nothing", loaded.Count == 0 ? "nothing" : string.Join(", ", loaded), "nothing", loaded.Count == 0 ? "passed" : "FAILED");
        if (loaded.Count > 0)
            outcome.Failures.Add($"PDFium was loaded into the published application: {string.Join(", ", loaded)}");

        if (pdfium.TryGetProperty("rendererCompiledIn", out var compiled) && compiled.ValueKind == JsonValueKind.True)
            outcome.Failures.Add("the published application was built with the PDFium renderer (PdfiumRenderer=true)");

        if (!(pdfium.TryGetProperty("disabledByEnvironment", out var disabled) && disabled.ValueKind == JsonValueKind.True))
            outcome.Failures.Add("the launch did not run with PDF_PDFIUM_DISABLED=1");

        return outcome;
    }

    private static IEnumerable<string> Strings(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0)
            : Enumerable.Empty<string>();

    /// <summary>
    /// One launch of the payload with PDFium disabled: a copy of the payload in the work folder,
    /// with its own single-file extraction directory, so the extracted files can be checked too.
    /// Returns the probe's result file, and adds any PDFium file the host extracted to <paramref name="extracted"/>.
    /// </summary>
    public static string Launch(string payloadDir, string documentPath, string workDir, TimeSpan timeout, List<string> extracted)
    {
        string app = Path.Combine(workDir, "app");
        string extract = Path.Combine(workDir, "extract");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        StartupHarness.CopyDirectory(payloadDir, app);
        Directory.CreateDirectory(extract);
        string resultPath = Path.Combine(workDir, "probe.json");

        var psi = new ProcessStartInfo(Path.Combine(app, StartupHarness.ExecutableName))
        {
            UseShellExecute = false,
            WorkingDirectory = app,
        };
        psi.ArgumentList.Add("--startup-probe");
        psi.ArgumentList.Add(resultPath);
        psi.ArgumentList.Add(documentPath);
        psi.ArgumentList.Add("--search");
        psi.ArgumentList.Add(SearchQuery);
        psi.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extract;
        psi.Environment["PDF_PDFIUM_DISABLED"] = "1";
        psi.Environment.Remove("PDF_ENGINE_MODE");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("The application did not start.");
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"The launch with PDFium disabled did not finish within {timeout.TotalSeconds:0} s.");
        }

        extracted.AddRange(Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories)
            .Select(f => "extracted: " + Path.GetRelativePath(extract, f).Replace('\\', '/'))
            .Where(IsPdfiumFile));

        if (!File.Exists(resultPath))
            throw new InvalidOperationException($"The launch with PDFium disabled exited with code {process.ExitCode} and wrote no probe result.");
        return File.ReadAllText(resultPath);
    }
}
