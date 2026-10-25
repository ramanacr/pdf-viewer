using System;

namespace PdfEngine.Vector.Diagnostics;

/// <summary>
/// Category of a typed vector-engine failure (see 11_CODING_AGENT_EXECUTION.md "Error policy").
/// Callers decide fallback from the category, never from the message text.
/// </summary>
public enum PdfVectorErrorKind
{
    /// <summary>Malformed PDF syntax (<see cref="PdfSyntaxException"/>).</summary>
    Syntax,
    /// <summary>An indirect object could not be found or read.</summary>
    ObjectResolution,
    /// <summary>A construct the engine does not support (<see cref="PdfUnsupportedFeatureException"/>).</summary>
    UnsupportedFeature,
    /// <summary>A hostile-input ceiling was hit (<see cref="PdfResourceLimitException"/>).</summary>
    ResourceLimit,
    /// <summary>The document cannot be decrypted (<see cref="PdfEncryptedDocumentException"/>).</summary>
    Encryption,
    /// <summary>A renderer failed.</summary>
    RenderBackend,
}

/// <summary>
/// Base type for every classified failure raised by the vector engine.
/// </summary>
public class PdfVectorException : Exception
{
    /// <summary>The category of the failure.</summary>
    public PdfVectorErrorKind Kind { get; }

    /// <summary>Creates the exception with a category, a message and an optional cause.</summary>
    public PdfVectorException(PdfVectorErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}

/// <summary>Malformed PDF syntax that bounded recovery could not repair.</summary>
public sealed class PdfSyntaxException : PdfVectorException
{
    /// <summary>Creates the exception with a message and an optional cause.</summary>
    public PdfSyntaxException(string message, Exception? inner = null)
        : base(PdfVectorErrorKind.Syntax, message, inner) { }
}

/// <summary>A hostile-input ceiling from <see cref="Limits.PdfSecurityLimits"/> was hit.</summary>
public sealed class PdfResourceLimitException : PdfVectorException
{
    /// <summary>The name of the limit that was hit, usually a property name of <see cref="Limits.PdfSecurityLimits"/> such as <c>MaxImagePixels</c>.</summary>
    public string LimitName { get; }

    /// <summary>Creates the exception with the limit name and a message.</summary>
    public PdfResourceLimitException(string limitName, string message)
        : base(PdfVectorErrorKind.ResourceLimit, message)
    {
        LimitName = limitName;
    }
}

/// <summary>A construct the vector engine does not yet render faithfully. Carries a stable reason.</summary>
public sealed class PdfUnsupportedFeatureException : PdfVectorException
{
    /// <summary>The stable reason code of the unsupported construct.</summary>
    public PdfFallbackReason Reason { get; }

    /// <summary>Creates the exception with a reason code and a message.</summary>
    public PdfUnsupportedFeatureException(PdfFallbackReason reason, string message)
        : base(PdfVectorErrorKind.UnsupportedFeature, message)
    {
        Reason = reason;
    }
}

/// <summary>
/// The document is encrypted and cannot be opened: the password is missing or wrong
/// (<see cref="PasswordRequired"/>), or the security handler is not supported.
/// </summary>
public sealed class PdfEncryptedDocumentException : PdfVectorException
{
    /// <summary>True when a password is missing or wrong. Ask the user for the password and open the document again.</summary>
    public bool PasswordRequired { get; }

    /// <summary>Public-key encryption: none of the available certificates (with private keys) is a recipient.</summary>
    public bool CertificateRequired { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="passwordRequired">True when a password is missing or wrong.</param>
    /// <param name="certificateRequired">True when no available certificate can open a public-key encrypted document.</param>
    public PdfEncryptedDocumentException(string message = "Document is encrypted with an unsupported security handler.",
        bool passwordRequired = false, bool certificateRequired = false)
        : base(PdfVectorErrorKind.Encryption, message)
    {
        PasswordRequired = passwordRequired;
        CertificateRequired = certificateRequired;
    }
}
