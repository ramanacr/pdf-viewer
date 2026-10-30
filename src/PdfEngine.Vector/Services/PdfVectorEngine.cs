using System.Globalization;
using System.Xml.Linq;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using PdfEngine.Save;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>
/// The document-level services of <see cref="PdfEngine.IPdfEngine"/> without PDFium: opening,
/// annotations, page organisation, saving, embedded files, forms, signatures, redaction, and the
/// document safety inspector and sanitizer, all on <see cref="PdfEditableDocument"/>.
/// </summary>
public sealed class PdfVectorEngine
{
    public PdfVectorEngine(IPdfRenderer? renderer = null)
    {
        AnnotationService = new PdfAnnotationService();
        PageOrganizer = new PdfPageOrganizerService();
        SaveService = new PdfSaveService(renderer);
        AttachmentService = new PdfAttachmentService();
        FormService = new PdfFormService();
        SignatureService = new PdfSignatureService();
        RedactionService = new PdfRedactionService();
        SafetyInspector = new PdfSafetyInspector();
        Sanitizer = new PdfSanitizer(SafetyInspector);
    }

    public string EngineName => "Vector engine (native document services)";

    public PdfAnnotationService AnnotationService { get; }
    public PdfPageOrganizerService PageOrganizer { get; }
    public PdfSaveService SaveService { get; }
    public PdfAttachmentService AttachmentService { get; }
    public PdfFormService FormService { get; }
    public PdfSignatureService SignatureService { get; }
    public PdfRedactionService RedactionService { get; }
    public PdfSafetyInspector SafetyInspector { get; }
    public PdfSanitizer Sanitizer { get; }

    public async ValueTask<IPdfDocument> OpenDocumentAsync(string filePath, string? password = null, CancellationToken cancellationToken = default) =>
        await PdfEditableDocument.OpenAsync(filePath, password, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IPdfDocument> OpenDocumentAsync(byte[] pdfBytes, string? password = null, CancellationToken cancellationToken = default) =>
        await PdfEditableDocument.OpenAsync(pdfBytes, string.Empty, password, cancellationToken).ConfigureAwait(false);

    internal static PdfEditableDocument Require(IPdfDocument document)
    {
        if (document is not PdfEditableDocument native)
            throw new ArgumentException("Document must be a PdfEditableDocument instance.", nameof(document));
        if (!native.IsOpen) throw new ObjectDisposedException(nameof(document));
        return native;
    }

    internal static void RefuseOriginal(PdfEditableDocument doc, string targetPath, string what)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        if (!string.IsNullOrEmpty(doc.FilePath) &&
            string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(doc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException($"{what} must be written to a new file; the original is never modified.", targetPath);
    }

    internal static async ValueTask WriteAsync(string targetPath, byte[] bytes, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new PdfSaveException("Failed writing the document to disk.", ex, targetPath);
        }
    }
}
