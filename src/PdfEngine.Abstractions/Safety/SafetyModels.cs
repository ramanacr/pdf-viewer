using PdfEngine.Documents;

namespace PdfEngine.Safety;

/// <summary>
/// A category of active or risky content a PDF can carry.
/// </summary>
public enum DocumentRiskKind
{
    /// <summary>Document-level or field JavaScript. The vector behind most PDF reader RCEs.</summary>
    JavaScript,

    /// <summary>A /Launch action, which asks the reader to start an external program or file.</summary>
    LaunchAction,

    /// <summary>An embedded file attachment, which can carry an executable payload.</summary>
    EmbeddedFile,

    /// <summary>A URI action pointing at an external address.</summary>
    ExternalLink,

    /// <summary>The document is encrypted, so its permissions are enforced by the reader.</summary>
    Encryption
}

/// <summary>
/// How seriously a finding should be treated.
/// </summary>
public enum RiskSeverity
{
    /// <summary>Present and worth knowing about, but not dangerous on its own.</summary>
    Informational,

    /// <summary>Capable of harm if the reader acts on it.</summary>
    Elevated
}

public record DocumentRiskFinding
{
    public DocumentRiskKind Kind { get; init; }
    public RiskSeverity Severity { get; init; }

    /// <summary>How many instances were found.</summary>
    public int Count { get; init; }

    /// <summary>Plain-language description shown to the user.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Representative details - script names, attachment file names, link targets. Capped,
    /// because a hostile document can contain thousands.
    /// </summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();
}

/// <summary>
/// What a document contains that could act on the user's machine.
/// </summary>
public record DocumentSafetyReport
{
    public IReadOnlyList<DocumentRiskFinding> Findings { get; init; } = Array.Empty<DocumentRiskFinding>();

    /// <summary>True when nothing active was found: no script, launch action or attachment.</summary>
    public bool IsClean => !Findings.Any(f => f.Severity == RiskSeverity.Elevated);

    /// <summary>
    /// True when inspection could not cover the whole document, so "clean" is not a
    /// guarantee. Callers must not present a limited inspection as a clean bill of health.
    /// </summary>
    public bool InspectionWasLimited { get; init; }

    public string LimitationReason { get; init; } = string.Empty;

    public bool HasKind(DocumentRiskKind kind) => Findings.Any(f => f.Kind == kind);
}

/// <summary>
/// Inspects a document for content that can act on the user's machine.
///
/// This exists because the dominant PDF attack pattern is a document that executes as soon
/// as it is opened, with no indication to the user. A reader that simply refuses to run that
/// content, and says plainly what the document was carrying, removes the whole class of
/// surprise.
/// </summary>
public interface IPdfSafetyInspector
{
    ValueTask<DocumentSafetyReport> InspectAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default);
}
