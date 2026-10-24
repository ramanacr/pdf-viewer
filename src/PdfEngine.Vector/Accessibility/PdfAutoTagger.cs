using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Text;

namespace PdfEngine.Vector.Accessibility;

/// <summary>How <see cref="PdfAutoTagger"/> tags a document.</summary>
public sealed class PdfAutoTagOptions
{
    /// <summary>The placeholder written as /Alt of every figure, so that it is clear a person must write the description.</summary>
    public const string DefaultAltPlaceholder = "Image: alternate text needs review";

    /// <summary>The document language (a BCP 47 tag such as "en-GB"); null keeps the document's own /Lang, or "en" when it has none.</summary>
    public string? Language { get; init; }

    /// <summary>Replaces the tags of a document that is already tagged; without it such a document is refused.</summary>
    public bool Retag { get; init; }

    /// <summary>
    /// Writes the PDF/UA identification (pdfuaid:part 1) into the metadata. Off by default: the
    /// claim is a person's to make, after the checks that need judgement (alternate text, reading order).
    /// </summary>
    public bool ClaimPdfUa { get; init; }

    /// <summary>The /Alt written on figures (reported as needing review); null writes none, so the checker reports each figure.</summary>
    public string? FigureAltPlaceholder { get; init; } = DefaultAltPlaceholder;

    /// <summary>Finds simple grid tables (ruled grids, and columns of single-line cells); uncertain regions stay paragraphs.</summary>
    public bool DetectTables { get; init; } = true;

    /// <summary>Finds bulleted and numbered lists.</summary>
    public bool DetectLists { get; init; } = true;

    /// <summary>Checks that every page's extracted text is unchanged, and refuses the result when it is not.</summary>
    public bool VerifyText { get; init; } = true;
}

public enum PdfAutoTagStatus
{
    /// <summary>The document was tagged; <see cref="PdfAutoTagResult.Bytes"/> holds the new file.</summary>
    Tagged,
    /// <summary>The document has a structure tree already (tag it again with <see cref="PdfAutoTagOptions.Retag"/>).</summary>
    AlreadyTagged,
    /// <summary>The document is encrypted and its permissions do not allow changing it.</summary>
    NotPermitted,
    /// <summary>A page's content cannot be read, so it cannot be tagged (<see cref="PdfAutoTagResult.Page"/>).</summary>
    ContentUnreadable,
    /// <summary>The tagged copy would read differently from the original (a safety check that should not trip).</summary>
    TextWouldChange,
    /// <summary>The document has no pages.</summary>
    NoPages,
}

/// <summary>Why a checkpoint still fails after tagging.</summary>
public enum PdfAutoTagIssueKind
{
    /// <summary>A person must decide (the wording of alternate text, a title, link descriptions, the conformance claim).</summary>
    HumanReview,
    /// <summary>Not about tags: fonts, encryption, forms technology, embedded files and the like.</summary>
    OutsideTagging,
    /// <summary>Tagging should have fixed it but did not: a gap in the tagger.</summary>
    NotFixed,
}

/// <summary>A Matterhorn checkpoint the tagged copy still fails, with how often and why.</summary>
public sealed record PdfAutoTagRemainingIssue(string Checkpoint, string Clause, string Message, int Count, PdfAutoTagIssueKind Kind);

/// <summary>A figure that was given placeholder (or no) alternate text.</summary>
/// <remarks>Bounds are in the page's default user space.</remarks>
public sealed record PdfAutoTagFigure(int Page, PdfRect Bounds);

/// <summary>A top-level element the tagger made (H1 to H6, P, L, Table, Figure, ...), with what it covers in the page's default user space.</summary>
public sealed record PdfAutoTagElement(int Page, string Type, PdfRect Bounds);

/// <summary>What the tagger found and tagged.</summary>
public sealed record PdfAutoTagSummary
{
    public int Pages { get; init; }
    public int Headings { get; init; }
    public int Paragraphs { get; init; }
    public int Lists { get; init; }
    public int ListItems { get; init; }
    public int Tables { get; init; }
    public int TablesWithHeaders { get; init; }
    public int Figures { get; init; }
    public int Links { get; init; }
    /// <summary>Annotations other than links (and form fields) tagged as Annot or Form.</summary>
    public int OtherAnnotations { get; init; }
    /// <summary>Repeated headers, footers and page numbers marked as artifacts.</summary>
    public int HeadersAndFooters { get; init; }
    /// <summary>Marked-content sequences written as artifacts (furniture, rules, backgrounds, vector graphics).</summary>
    public int ArtifactSequences { get; init; }
    public int MultiColumnPages { get; init; }
    public int RotatedPages { get; init; }
}

public sealed class PdfAutoTagResult
{
    public required PdfAutoTagStatus Status { get; init; }
    /// <summary>What happened, for people (never quotes document text).</summary>
    public required string Message { get; init; }
    /// <summary>The tagged file (a complete rewrite), when <see cref="Status"/> is Tagged.</summary>
    public byte[]? Bytes { get; init; }
    /// <summary>The 1-based page a refusal is about, when it is about one.</summary>
    public int? Page { get; init; }
    public PdfAutoTagSummary Summary { get; init; } = new();
    /// <summary>The tagged copy checked against PDF/UA-1.</summary>
    public PdfUaReport? Report { get; init; }
    /// <summary>The checkpoints the tagged copy still fails, each classified.</summary>
    public IReadOnlyList<PdfAutoTagRemainingIssue> Remaining { get; init; } = Array.Empty<PdfAutoTagRemainingIssue>();
    /// <summary>What a person should still review (the Matterhorn checks that need judgement).</summary>
    public IReadOnlyList<string> Review { get; init; } = Array.Empty<string>();
    /// <summary>Figures whose alternate text is a placeholder (or missing).</summary>
    public IReadOnlyList<PdfAutoTagFigure> FiguresNeedingAlt { get; init; } = Array.Empty<PdfAutoTagFigure>();
    /// <summary>The top-level elements in reading order, with their bounds (for showing the tags over the pages).</summary>
    public IReadOnlyList<PdfAutoTagElement> Elements { get; init; } = Array.Empty<PdfAutoTagElement>();
    /// <summary>The language written into the catalog.</summary>
    public string? Language { get; init; }
    /// <summary>Every page's extracted text was compared with the original's and is the same (null when not checked).</summary>
    public bool? TextPreserved { get; init; }

    public bool Succeeded => Status == PdfAutoTagStatus.Tagged;

    /// <summary>Only checkpoints that need a person (or that tagging cannot fix) remain.</summary>
    public bool OnlyHumanChecksRemain => Succeeded && Remaining.All(r => r.Kind != PdfAutoTagIssueKind.NotFixed);
}

/// <summary>
/// "Make Accessible": tags an untagged document for assistive technology, writing a complete
/// copy (never over the original). Page content is wrapped in marked-content sequences (text
/// runs and images with MCIDs; headers, footers, page numbers, rules, backgrounds and vector
/// graphics as artifacts) without changing a byte of what is drawn, so pages look and read
/// exactly as before. A structure tree is built in reading order (columns found by cutting the
/// page along its white space): headings from font size and weight relative to the body text,
/// paragraphs, bulleted and numbered lists, simple grid tables (conservatively: uncertain regions
/// stay paragraphs), figures with placeholder alternate text, links and other annotations by
/// object reference. The catalog gets /MarkInfo, /Lang, /ViewerPreferences /DisplayDocTitle and
/// a title in the metadata. The result is checked with <see cref="PdfUaValidator"/> and what
/// still fails is reported, classified. No document text is ever written to diagnostics.
/// </summary>
public static class PdfAutoTagger
{
    public static PdfAutoTagResult Tag(PdfVectorDocument document, PdfAutoTagOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfAutoTagOptions();
        if (options.Language != null && !PdfUaValidator.IsValidLanguageTag(options.Language))
            throw new ArgumentException("The language is not a valid language tag (for example en, en-GB or de-CH).", nameof(options));

        if (document.PageCount == 0) return Refused(PdfAutoTagStatus.NoPages, "The document has no pages.");
        if (document.Security is { } security && !security.IsOwner && (security.Permissions & 8) == 0)
            return Refused(PdfAutoTagStatus.NotPermitted, "The document's security settings do not allow changing it, so it cannot be tagged. Open it with its permissions password.");
        if (IsTagged(document) && !options.Retag)
            return Refused(PdfAutoTagStatus.AlreadyTagged, "The document is already tagged. Choose to replace its tags to tag it again.");

        var run = new AutoTagRun(document, options, cancellationToken);
        if (run.Analyze() is { } unreadable)
            return new PdfAutoTagResult
            {
                Status = PdfAutoTagStatus.ContentUnreadable, Page = unreadable,
                Message = $"The content of page {unreadable} cannot be read, so the document cannot be tagged.",
            };
        byte[] bytes = run.Write();
        var summary = run.Summary();

        using var tagged = PdfVectorDocument.Open(new MemoryByteSource(bytes), document.FilePath);
        bool? textPreserved = null;
        if (options.VerifyText)
        {
            int? differs = FirstPageWithDifferentText(document, tagged, cancellationToken);
            textPreserved = differs == null;
            if (differs is { } page)
                return new PdfAutoTagResult
                {
                    Status = PdfAutoTagStatus.TextWouldChange, Page = page, Summary = summary, TextPreserved = false,
                    Message = $"Tagging would change how the text of page {page} reads, so no file was written. Please report this document.",
                };
        }
        var report = PdfUaValidator.Validate(tagged, bytes);
        var remaining = Classify(report, options);
        return new PdfAutoTagResult
        {
            Status = PdfAutoTagStatus.Tagged, Bytes = bytes, Summary = summary, Report = report, Remaining = remaining,
            Review = run.Review(remaining), FiguresNeedingAlt = run.FiguresNeedingAlt, Elements = run.Elements(), Language = run.Language, TextPreserved = textPreserved,
            Message = remaining.Count == 0
                ? "Tagged. The copy passes every machine check of PDF/UA-1; a person should still review it."
                : remaining.All(r => r.Kind != PdfAutoTagIssueKind.NotFixed)
                    ? "Tagged. What remains needs a person's judgement, or is not about tags."
                    : $"Tagged, but {remaining.Count(r => r.Kind == PdfAutoTagIssueKind.NotFixed)} checkpoint(s) that tags should fix still fail.",
        };
    }

    /// <summary>Whether the document has a structure tree.</summary>
    public static bool IsTagged(PdfVectorDocument document) =>
        document.Resolver.Resolve(document.Catalog["StructTreeRoot"]) is PdfDictionary;

    /// <summary>The language the document declares (catalog /Lang), when it is a valid tag.</summary>
    public static string? DocumentLanguage(PdfVectorDocument document) =>
        document.Resolver.Resolve(document.Catalog["Lang"]) is PdfString s && s.AsDecodedString().Trim() is var lang && PdfUaValidator.IsValidLanguageTag(lang) ? lang : null;

    private static PdfAutoTagResult Refused(PdfAutoTagStatus status, string message) => new() { Status = status, Message = message };

    private static int? FirstPageWithDifferentText(PdfVectorDocument before, PdfVectorDocument after, CancellationToken ct)
    {
        for (int p = 1; p <= before.PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            string a, b;
            try { a = PdfTextExtractor.LoadPage(before, p, ct).GetAllText(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { continue; } // unreadable before: nothing to keep
            b = PdfTextExtractor.LoadPage(after, p, ct).GetAllText();
            if (!string.Equals(a, b, StringComparison.Ordinal)) return p;
        }
        return null;
    }

    /// <summary>Checkpoints that need a person's judgement once the tags are there.</summary>
    private static readonly HashSet<string> HumanCheckpoints = new(StringComparer.Ordinal)
    {
        "06-002", // the PDF/UA claim: made by a person after review
        "06-003", "11-006", // a title, when the document gives none
        "13-004", "17-002", // alternate text of figures and formulas
        "28-004", "28-005", "28-012", // descriptions of annotations, form fields and links
        "19-003", "19-004",
    };

    /// <summary>Checkpoint prefixes that are not about tags.</summary>
    private static readonly string[] OutsidePrefixes = { "31-", "10-", "26-", "25-", "20-", "21-", "30-", "XX-" };
    private static readonly HashSet<string> OutsideCheckpoints = new(StringComparer.Ordinal) { "28-006", "28-007", "28-014", "28-015", "28-016" };

    internal static IReadOnlyList<PdfAutoTagRemainingIssue> Classify(PdfUaReport report, PdfAutoTagOptions options)
    {
        var list = new List<PdfAutoTagRemainingIssue>();
        foreach (var (first, count) in report.Summary)
        {
            if (first.Severity != PdfUaSeverity.Error) continue;
            string cp = first.Checkpoint;
            var kind = HumanCheckpoints.Contains(cp) ? PdfAutoTagIssueKind.HumanReview
                : OutsideCheckpoints.Contains(cp) || OutsidePrefixes.Any(p => cp.StartsWith(p, StringComparison.Ordinal)) ? PdfAutoTagIssueKind.OutsideTagging
                : PdfAutoTagIssueKind.NotFixed;
            list.Add(new PdfAutoTagRemainingIssue(cp, first.Clause, first.Message, count, kind));
        }
        return list;
    }
}
