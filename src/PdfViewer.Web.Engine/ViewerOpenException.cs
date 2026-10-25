namespace PdfViewer.Web.Engine;

/// <summary>Why a document could not be opened. A host decides what to show from this, never from a message.</summary>
public enum ViewerOpenFailure
{
    /// <summary>The bytes are not a PDF, or the file is damaged beyond repair.</summary>
    NotReadable,
    /// <summary>The document is encrypted and needs a password.</summary>
    PasswordRequired,
    /// <summary>The password given does not open the document.</summary>
    WrongPassword,
    /// <summary>The document is encrypted for certificate holders (public-key security), which a browser cannot use.</summary>
    CertificateRequired,
    /// <summary>
    /// The document is encrypted, and this platform's .NET runtime lacks a cipher, hash or
    /// certificate store the security handler needs and the engine has no fallback for.
    /// </summary>
    EncryptionNotSupportedHere,
    /// <summary>The file is larger than the host allows.</summary>
    TooLarge,
    /// <summary>A file read by ranges (<see cref="ViewerDocument.OpenRangedAsync"/>) could not be read: the network or the server failed.</summary>
    NetworkError,
}

/// <summary>A document that cannot be opened, with a reason and a message fit to show to the user.</summary>
/// <remarks>The message never contains document content.</remarks>
public sealed class ViewerOpenException : Exception
{
    public ViewerOpenFailure Failure { get; }

    public ViewerOpenException(ViewerOpenFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>
    /// The engine exception types behind the failure ("PdfSyntaxException ← InvalidDataException"),
    /// for a bug report: type names only, never messages, which may quote the document.
    /// </summary>
    public string Detail
    {
        get
        {
            var names = new List<string>();
            for (var e = InnerException; e != null && names.Count < 4; e = e.InnerException) names.Add(e.GetType().Name);
            return string.Join(" ← ", names);
        }
    }
}

/// <summary>A range reader of <see cref="ViewerDocument.OpenRangedAsync"/> could not read: the network, the server or the host page failed.</summary>
public sealed class ViewerRangeReadException(string message, Exception? inner = null) : IOException(message, inner);
