using System;

namespace PdfEngine.Vector.Diagnostics;

/// <summary>
/// Category of a typed vector-engine failure (see 11_CODING_AGENT_EXECUTION.md "Error policy").
/// Callers decide fallback from the category, never from the message text.
/// </summary>
public enum PdfVectorErrorKind
{
    Syntax,
    ObjectResolution,
    UnsupportedFeature,
    ResourceLimit,
    Encryption,
    RenderBackend,
}

/// <summary>
/// Base type for every classified failure raised by the vector engine.
/// </summary>
public class PdfVectorException : Exception
{
    public PdfVectorErrorKind Kind { get; }

    public PdfVectorException(PdfVectorErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}

/// <summary>Malformed PDF syntax that bounded recovery could not repair.</summary>
public sealed class PdfSyntaxException : PdfVectorException
{
    public PdfSyntaxException(string message, Exception? inner = null)
        : base(PdfVectorErrorKind.Syntax, message, inner) { }
}

/// <summary>A hostile-input ceiling from <see cref="Limits.PdfSecurityLimits"/> was hit.</summary>
public sealed class PdfResourceLimitException : PdfVectorException
{
    public string LimitName { get; }

    public PdfResourceLimitException(string limitName, string message)
        : base(PdfVectorErrorKind.ResourceLimit, message)
    {
        LimitName = limitName;
    }
}

/// <summary>A construct the vector engine does not yet render faithfully. Carries a stable reason.</summary>
public sealed class PdfUnsupportedFeatureException : PdfVectorException
{
    public PdfFallbackReason Reason { get; }

    public PdfUnsupportedFeatureException(PdfFallbackReason reason, string message)
        : base(PdfVectorErrorKind.UnsupportedFeature, message)
    {
        Reason = reason;
    }
}

/// <summary>The document is encrypted; the vector core has no security handler yet (05_PDF_CORE "Encryption").</summary>
public sealed class PdfEncryptedDocumentException : PdfVectorException
{
    public PdfEncryptedDocumentException()
        : base(PdfVectorErrorKind.Encryption, "Document is encrypted; the vector core does not implement a security handler yet.") { }
}
