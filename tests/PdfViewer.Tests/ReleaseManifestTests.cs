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

    /// <summary>Packages with runtime or native files in a project's restore graph (build-only packages excluded).</summary>
    private static IEnumerable<(string Id, string Version)> ShippedPackages(string assetsFile)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var libraries = json.RootElement.GetProperty("libraries");
        foreach (var target in json.RootElement.GetProperty("targets").EnumerateObject())
            foreach (var lib in target.Value.EnumerateObject())
            {
                if (lib.Value.GetProperty("type").GetString() != "package") continue;
                bool ships = new[] { "runtime", "native", "runtimeTargets" }.Any(kind =>
                    lib.Value.TryGetProperty(kind, out var files) && files.EnumerateObject().Any(f => !f.Name.EndsWith("_._", StringComparison.Ordinal)));
                if (!ships) continue;
                var parts = lib.Name.Split('/');
                if (libraries.TryGetProperty(lib.Name, out _)) yield return (parts[0], parts[1]);
            }
    }

    private static List<(string Id, string Version)> AllShipped()
    {
        string root = RepoRoot();
        var assets = new[] { "src/PdfViewer/obj/project.assets.json", "src/Installer/obj/project.assets.json" }
            .Select(p => Path.Combine(root, p)).Where(File.Exists).ToList();
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
}
