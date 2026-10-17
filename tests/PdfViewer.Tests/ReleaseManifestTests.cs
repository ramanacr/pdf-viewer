using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// What ships is accounted for: every NuGet package whose assemblies end up in the application
/// is credited in THIRD_PARTY_NOTICES.md and listed, at its version, in the committed SBOM. A
/// new or updated reference fails here until the notices and the SBOM
/// (scripts/generate_sbom.ps1 -OutputDir sbom) are brought up to date.
/// </summary>
public class ReleaseManifestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>The PDFium oracle: in developer and test builds only, never in what ships.</summary>
    private const string PdfiumProject = "PdfEngine.Pdfium";

    /// <summary>
    /// Packages with runtime or native files that the published build (PdfiumRenderer=false)
    /// ships: the restore graph walked from the project's own references, never through
    /// PdfEngine.Pdfium, so the answer is the same whichever configuration was restored last.
    /// Build-only packages are excluded.
    /// </summary>
    private static IEnumerable<(string Id, string Version)> ShippedPackages(string assetsFile)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var root = json.RootElement;
        var libraries = root.GetProperty("libraries");
        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            var byId = target.Value.EnumerateObject().ToDictionary(l => l.Name.Split('/')[0], l => l, StringComparer.OrdinalIgnoreCase);

            // The roots: the project's direct package and project references.
            var pending = new Stack<string>();
            foreach (var fw in root.GetProperty("project").GetProperty("frameworks").EnumerateObject())
                if (fw.Value.TryGetProperty("dependencies", out var deps))
                    foreach (var d in deps.EnumerateObject()) pending.Push(d.Name);
            foreach (var fw in root.GetProperty("project").GetProperty("restore").GetProperty("frameworks").EnumerateObject())
                if (fw.Value.TryGetProperty("projectReferences", out var refs))
                    foreach (var r in refs.EnumerateObject()) pending.Push(Path.GetFileNameWithoutExtension(r.Name));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count > 0)
            {
                string id = pending.Pop();
                if (string.Equals(id, PdfiumProject, StringComparison.OrdinalIgnoreCase) || !seen.Add(id)) continue;
                if (!byId.TryGetValue(id, out var lib)) continue;
                if (lib.Value.TryGetProperty("dependencies", out var deps))
                    foreach (var d in deps.EnumerateObject()) pending.Push(d.Name);

                if (lib.Value.GetProperty("type").GetString() != "package") continue;
                bool ships = new[] { "runtime", "native", "runtimeTargets" }.Any(kind =>
                    lib.Value.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("_._", StringComparison.Ordinal)));
                if (!ships) continue;
                var parts = lib.Name.Split('/');
                if (libraries.TryGetProperty(lib.Name, out _)) yield return (parts[0], parts[1]);
            }
        }
    }

    private static List<(string Id, string Version)> AllShipped()
    {
        string root = RepoRoot();
        // The published configuration's restore (obj/nopdfium) where there is one; the walk in
        // ShippedPackages gives the same answer from the developer build's.
        var assets = new[] { "src/PdfViewer", "src/Installer" }
            .Select(p => new[] { "obj/nopdfium/project.assets.json", "obj/project.assets.json" }
                .Select(a => Path.Combine(root, p, a)).FirstOrDefault(File.Exists))
            .OfType<string>().ToList();
        Assert.NotEmpty(assets); // the application has been restored
        return assets.SelectMany(ShippedPackages).Distinct().OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public void EveryShippedPackage_IsCreditedInTheNotices()
    {
        string notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD_PARTY_NOTICES.md"));
        var missing = AllShipped().Where(p => notices.IndexOf(p.Id, StringComparison.OrdinalIgnoreCase) < 0).ToList();
        Assert.True(missing.Count == 0, "Not credited in THIRD_PARTY_NOTICES.md: " + string.Join(", ", missing.Select(p => p.Id)));
    }

    [Fact]
    public void EveryShippedPackage_IsInTheCommittedSbom()
    {
        using var sbom = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "sbom", "sbom.cyclonedx.json")));
        var purls = sbom.RootElement.GetProperty("components").EnumerateArray()
            .Select(c => c.TryGetProperty("purl", out var p) ? p.GetString() : null)
            .Where(p => p != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = AllShipped().Where(p => !purls.Contains($"pkg:nuget/{p.Id}@{p.Version}")).ToList();
        Assert.True(missing.Count == 0, "Missing from sbom/sbom.cyclonedx.json (run scripts/generate_sbom.ps1 -OutputDir sbom): "
            + string.Join(", ", missing.Select(p => $"{p.Id} {p.Version}")));
    }

    [Fact]
    public void ThePublishScript_VerifiesThePayload()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build_publish.ps1"));
        Assert.Contains("[switch]$DryRun", script);
        Assert.Contains("$AllowedPayload", script);
        Assert.Contains("*.pdb", script); // symbols leave the payload
    }

    // ------------------------------------------------------------------ the PDFium-free product (M10)

    [Fact]
    public void ThePublishScript_PublishesWithoutPdfium()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build_publish.ps1"));
        Assert.Contains("-p:PdfiumRenderer=false", script);
        Assert.Contains("pdfium-free", script); // the release gate runs on the payload
    }

    /// <summary>
    /// PdfViewer references PdfEngine.Pdfium and copies pdfium.dll only in a PdfiumRenderer=true
    /// build: every such item sits in an ItemGroup conditioned on it.
    /// </summary>
    [Fact]
    public void TheApplicationProject_ReferencesPdfiumOnlyInPdfiumBuilds()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot(), "src", "PdfViewer", "PdfViewer.csproj"));
        var pdfiumItems = project.Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "None" or "Content" or "PackageReference")
            .Where(e => (e.Attribute("Include")?.Value ?? string.Empty).Contains("pdfium", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(pdfiumItems); // the developer build still has the oracle
        foreach (var item in pdfiumItems)
        {
            string condition = item.Parent?.Attribute("Condition")?.Value ?? item.Attribute("Condition")?.Value ?? string.Empty;
            Assert.True(condition.Replace(" ", "") == "'$(PdfiumRenderer)'=='true'",
                $"{item.Attribute("Include")?.Value} is not conditioned on PdfiumRenderer=true (condition: '{condition}').");
        }
    }

    [Fact]
    public void TheNotices_DoNotCreditPdfium()
    {
        string notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD_PARTY_NOTICES.md"));
        Assert.DoesNotContain("pdfium", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Permission is hereby granted", notices); // the MIT text the MIT components refer to
    }

    [Theory]
    [InlineData("sbom.cyclonedx.json")]
    [InlineData("sbom.spdx.json")]
    public void TheCommittedSbom_ListsNoPdfium(string file)
    {
        string sbom = File.ReadAllText(Path.Combine(RepoRoot(), "sbom", file));
        Assert.DoesNotContain("pdfium", sbom, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PdfEngine.Vector", sbom);
    }

    /// <summary>The payload baseline is a PdfiumRenderer=false publish: nothing of PDFium is bundled.</summary>
    [Fact]
    public void ThePayloadBaseline_HasNoPdfium()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "eng", "releasegate", "baseline-payload.json")));
        var names = baseline.RootElement.GetProperty("files").EnumerateObject().Select(f => f.Name)
            .Concat(baseline.RootElement.GetProperty("bundles").EnumerateObject().SelectMany(b => b.Value.EnumerateObject().Select(f => f.Name)))
            .ToList();
        Assert.Contains("PdfEngine.Vector.dll", names);
        Assert.DoesNotContain(names, n => n.Contains("pdfium", StringComparison.OrdinalIgnoreCase));
    }
}
