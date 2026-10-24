using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>An error stops the profile's purpose; a warning is worth a look; info is a fact about the file.</summary>
public enum PdfPreflightSeverity { Error, Warning, Info }

/// <summary>
/// A preflight result: the rule (its stable id, as <c>PP-3.1</c>), what was found and where. Messages carry measures,
/// page numbers, object numbers and resource names (fonts, spot colorants); never text of the document's pages.
/// </summary>
public sealed record PdfPreflightFinding(string RuleId, PdfPreflightSeverity Severity, string Message)
{
    /// <summary>The 1-based page, when the finding is on one.</summary>
    public int? Page { get; init; }

    /// <summary>Where on the page, in default user space (points, y up), when known.</summary>
    public PdfRect? Bounds { get; init; }

    /// <summary>
    /// <see cref="Bounds"/> as fractions of the unrotated crop box from its top-left corner (the convention the
    /// viewer's page overlays use).
    /// </summary>
    public PdfRect? NormalizedBounds { get; init; }

    /// <summary>The object the finding is about (an image XObject), when known.</summary>
    public int? ObjectNumber { get; init; }

    /// <summary>The measured value (ppi, percent, points), with <see cref="Unit"/>.</summary>
    public double? Measure { get; init; }
    public string? Unit { get; init; }

    /// <summary>How many occurrences this finding stands for (more than one when they were summarised).</summary>
    public int Count { get; init; } = 1;

    public string? Location
    {
        get
        {
            var parts = new List<string>(2);
            if (Page is { } p) parts.Add($"page {p}");
            if (ObjectNumber is { } o) parts.Add($"object {o}");
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    public override string ToString() => Location == null ? $"{RuleId}: {Message}" : $"{RuleId}: {Message} ({Location})";
}

/// <summary>The outcome of running a preflight profile.</summary>
public sealed class PdfPreflightReport
{
    public required PdfPreflightProfile Profile { get; init; }
    public IReadOnlyList<PdfPreflightFinding> Findings { get; init; } = Array.Empty<PdfPreflightFinding>();
    public int PagesChecked { get; init; }
    public int PageCount { get; init; }

    /// <summary>Total ink was measured on rendered plates (at <see cref="InkCoverageDpi"/>), not per object.</summary>
    public bool InkCoverageRasterized { get; init; }
    public double InkCoverageDpi { get; init; }

    public int Errors => Findings.Count(f => f.Severity == PdfPreflightSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == PdfPreflightSeverity.Warning);
    public int Infos => Findings.Count(f => f.Severity == PdfPreflightSeverity.Info);

    /// <summary>No errors (warnings and information do not fail a profile).</summary>
    public bool Passed => Errors == 0;

    /// <summary>Findings grouped by rule, each with how often it occurs, as the PDF/A report summarises.</summary>
    public IEnumerable<(PdfPreflightFinding First, int Count)> Summary =>
        Findings.GroupBy(f => f.RuleId).Select(g => (g.First(), g.Sum(f => f.Count)))
            .OrderBy(g => g.Item1.Severity).ThenBy(g => g.Item1.RuleId, StringComparer.Ordinal);

    /// <summary>A plain-text report: summary, then the findings by severity.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"Preflight: {Profile.Name}");
        sb.AppendLine(inv, $"Pages checked: {PagesChecked} of {PageCount}");
        sb.AppendLine(inv, $"Result: {(Passed ? "passed" : "failed")} ({Errors} error(s), {Warnings} warning(s), {Infos} information)");
        if (InkCoverageRasterized)
            sb.AppendLine(inv, $"Total ink measured on rendered plates at {InkCoverageDpi:0} dpi.");
        sb.AppendLine(Profile.Disclaimer);
        foreach (var severity in new[] { PdfPreflightSeverity.Error, PdfPreflightSeverity.Warning, PdfPreflightSeverity.Info })
        {
            var group = Findings.Where(f => f.Severity == severity).ToList();
            if (group.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine(inv, $"{severity}s ({group.Count}):");
            foreach (var f in group)
            {
                sb.Append(inv, $"  [{f.RuleId}] {f.Message}");
                if (f.Location is { } where) sb.Append(inv, $" ({where})");
                if (f.Bounds is { } b) sb.Append(inv, $" at {b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#} pt");
                if (f.Count > 1) sb.Append(inv, $" x{f.Count}");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>The report as JSON (UTF-8): profile, counts and every finding with its page, bounds, object and measure.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("profile", Profile.Id);
            w.WriteString("profileName", Profile.Name);
            w.WriteNumber("pageCount", PageCount);
            w.WriteNumber("pagesChecked", PagesChecked);
            w.WriteBoolean("passed", Passed);
            w.WriteNumber("errors", Errors);
            w.WriteNumber("warnings", Warnings);
            w.WriteNumber("information", Infos);
            w.WriteBoolean("inkCoverageRasterized", InkCoverageRasterized);
            if (InkCoverageRasterized) w.WriteNumber("inkCoverageDpi", InkCoverageDpi);
            w.WriteStartArray("findings");
            foreach (var f in Findings)
            {
                w.WriteStartObject();
                w.WriteString("rule", f.RuleId);
                w.WriteString("severity", f.Severity.ToString().ToLowerInvariant());
                w.WriteString("message", f.Message);
                if (f.Page is { } p) w.WriteNumber("page", p);
                if (f.ObjectNumber is { } o) w.WriteNumber("object", o);
                if (f.Measure is { } m)
                {
                    w.WriteNumber("measure", Math.Round(m, 2));
                    if (f.Unit != null) w.WriteString("unit", f.Unit);
                }
                if (f.Count > 1) w.WriteNumber("count", f.Count);
                if (f.Bounds is { } b)
                {
                    w.WriteStartArray("bounds");
                    w.WriteNumberValue(Math.Round(b.X, 2));
                    w.WriteNumberValue(Math.Round(b.Y, 2));
                    w.WriteNumberValue(Math.Round(b.Width, 2));
                    w.WriteNumberValue(Math.Round(b.Height, 2));
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
