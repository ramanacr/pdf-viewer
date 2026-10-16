using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Accessibility;

/// <summary>The groups Acrobat's Accessibility Checker reports under.</summary>
public enum PdfUaCategory { Document, PageContent, Structure, AlternateText, Tables, Lists, Headings, Fonts, Annotations, Forms }

/// <summary>An error breaks PDF/UA; a warning is worth a person's look but does not.</summary>
public enum PdfUaSeverity { Error, Warning }

/// <summary>
/// A failure condition: the Matterhorn Protocol checkpoint (for example 13-004), the ISO 14289-1
/// clause it belongs to, what is wrong and where. Messages never quote document text.
/// </summary>
public sealed record PdfUaIssue(string Checkpoint, string Clause, PdfUaCategory Category, string Message)
{
    public PdfUaSeverity Severity { get; init; } = PdfUaSeverity.Error;

    /// <summary>The 1-based page, when the issue is on one.</summary>
    public int? Page { get; init; }

    /// <summary>The object the issue is about (an annotation, a structure element, a font, ...).</summary>
    public int? ObjectNumber { get; init; }

    /// <summary>The structure element's path from the root, as "Document > Table > TR[2] > TD[3]".</summary>
    public string? StructurePath { get; init; }

    /// <summary>The element's bounds in the page's default user space, when known.</summary>
    public PdfRect? Bounds { get; init; }

    /// <summary>
    /// <see cref="Bounds"/> as fractions of the unrotated crop box, measured from its top-left corner
    /// (the convention the viewer's page overlays use).
    /// </summary>
    public PdfRect? NormalizedBounds { get; init; }

    /// <summary>How to fix it, for the checkpoint.</summary>
    public string Fix => PdfUaCheckpoints.Fix(Checkpoint);

    /// <summary>Page, object and structure path, as far as known.</summary>
    public string? Location
    {
        get
        {
            var parts = new List<string>(3);
            if (Page is { } p) parts.Add($"page {p}");
            if (ObjectNumber is { } o) parts.Add($"object {o}");
            if (StructurePath != null) parts.Add(StructurePath);
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    public override string ToString() => Location == null ? $"{Checkpoint}: {Message}" : $"{Checkpoint}: {Message} ({Location})";
}

public sealed class PdfUaReport
{
    public IReadOnlyList<PdfUaIssue> Issues { get; init; } = Array.Empty<PdfUaIssue>();

    /// <summary>Whether the XMP metadata claims PDF/UA-1 (pdfuaid:part 1).</summary>
    public bool ClaimsPdfUa { get; init; }

    public IEnumerable<PdfUaIssue> Errors => Issues.Where(i => i.Severity == PdfUaSeverity.Error);
    public IEnumerable<PdfUaIssue> Warnings => Issues.Where(i => i.Severity == PdfUaSeverity.Warning);

    /// <summary>No machine-checkable failure condition is met (the Matterhorn human checks still apply).</summary>
    public bool IsCompliant => !Errors.Any();

    /// <summary>Issues grouped by checkpoint, each with how often it occurs.</summary>
    public IEnumerable<(PdfUaIssue First, int Count)> Summary =>
        Issues.GroupBy(i => (i.Checkpoint, i.Severity)).Select(g => (g.First(), g.Count()))
            .OrderBy(g => g.Item1.Severity).ThenBy(g => g.Item1.Checkpoint, StringComparer.Ordinal);
}
