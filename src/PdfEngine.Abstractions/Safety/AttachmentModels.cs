using PdfEngine.Documents;

namespace PdfEngine.Safety;

/// <summary>
/// An embedded file carried inside a PDF.
/// </summary>
public record EmbeddedFileInfo
{
    /// <summary>Zero-based position in the document's embedded file list.</summary>
    public int Index { get; init; }

    /// <summary>The name the document gives the file. Treated as untrusted text.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Size of the embedded bytes, or -1 when it could not be determined.</summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// True when the file name ends in an extension Windows will execute or interpret. This
    /// drives the warning shown before extraction; it is a hint about the name, not a verdict
    /// about the contents.
    /// </summary>
    public bool HasExecutableExtension { get; init; }
}

/// <summary>
/// Lists and extracts the files embedded in a document.
///
/// Document Safety reports that attachments exist and Save Clean Copy removes them. This is
/// the deliberate middle path: sometimes the attachment is the thing you were sent, and you
/// need it out. Extraction is therefore explicit, one file at a time, to a path the user
/// picks - never automatic, never on open, and never executed.
/// </summary>
public interface IPdfAttachmentService
{
    ValueTask<IReadOnlyList<EmbeddedFileInfo>> GetAttachmentsAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one embedded file to disk. Returns the number of bytes written.
    /// The file is written as data; nothing about it is run.
    /// </summary>
    ValueTask<long> ExtractAttachmentAsync(
        IPdfDocument document,
        int index,
        string targetPath,
        CancellationToken cancellationToken = default);
}
