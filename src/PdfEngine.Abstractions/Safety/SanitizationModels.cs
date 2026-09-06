using PdfEngine.Documents;

namespace PdfEngine.Safety;

/// <summary>
/// A category of content a sanitized copy drops.
/// </summary>
public enum SanitizedContentKind
{
    /// <summary>Document-level JavaScript, including anything wired to open-time events.</summary>
    DocumentScript,

    /// <summary>Embedded file attachments held at the document level.</summary>
    EmbeddedFile,

    /// <summary>An annotation whose action asks the reader to start an external program.</summary>
    LaunchAction,

    /// <summary>An action that reaches outside this document - a remote or embedded GoTo.</summary>
    RemoteAction,

    /// <summary>
    /// An annotation whose action PDFium does not classify. Unrecognised is not the same as
    /// harmless, so a clean copy does not keep it.
    /// </summary>
    UnrecognizedAction,

    /// <summary>Sound, movie, screen, 3D, rich media or file-attachment annotations.</summary>
    EmbeddedMedia
}

public record SanitizationChange
{
    public SanitizedContentKind Kind { get; init; }

    /// <summary>How many were removed.</summary>
    public int Count { get; init; }

    /// <summary>Plain-language description shown to the user.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// What a sanitization run produced, including what it cost.
/// </summary>
public record SanitizationResult
{
    public string OutputPath { get; init; } = string.Empty;

    public int PageCount { get; init; }

    /// <summary>Active content that was removed. Empty when the source was already inert.</summary>
    public IReadOnlyList<SanitizationChange> Changes { get; init; } = Array.Empty<SanitizationChange>();

    /// <summary>
    /// Harmless things the rebuild could not carry across - bookmarks, encryption, document
    /// metadata. Stated plainly so the user is never surprised by what the copy lost.
    /// </summary>
    public IReadOnlyList<string> SideEffects { get; init; } = Array.Empty<string>();

    public bool RemovedAnything => Changes.Count > 0;

    public int TotalRemoved => Changes.Sum(c => c.Count);
}

/// <summary>
/// Raised when a sanitized copy could not be produced, including when the output was written
/// but failed its own verification. The failure is loud on purpose: a "clean copy" that is
/// not clean is worse than no feature at all, because the user would trust it.
/// </summary>
public class PdfSanitizationException : Exception
{
    public PdfSanitizationException(string message) : base(message) { }
    public PdfSanitizationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Writes a copy of a document with its executable content removed.
///
/// This is the other half of <see cref="IPdfSafetyInspector"/>. Reporting what a document
/// carries is useful; being able to strip it and keep working with the pages is what makes
/// the report actionable. Both competitors put this behind a paid tier.
///
/// The source document is never modified. The output is always verified by re-inspecting it,
/// and a copy that still carries elevated risk is deleted rather than handed back.
/// </summary>
public interface IPdfSanitizer
{
    ValueTask<SanitizationResult> SanitizeAsync(
        IPdfDocument document,
        string targetPath,
        CancellationToken cancellationToken = default);
}
