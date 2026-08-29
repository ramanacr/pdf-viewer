using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Forms;
using PdfEngine.Rendering;
using PdfEngine.Save;
using PdfEngine.Signatures;
using PdfEngine.Text;

namespace PdfEngine;

/// <summary>
/// Root PDF processing engine contract that opens documents and provides engine subsystems.
/// </summary>
public interface IPdfEngine : IDisposable
{
    string EngineName { get; }
    string EngineVersion { get; }

    ValueTask<IPdfDocument> OpenDocumentAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default);

    ValueTask<IPdfDocument> OpenDocumentAsync(
        byte[] pdfBytes,
        string? password = null,
        CancellationToken cancellationToken = default);

    IPdfRenderer Renderer { get; }
    IPdfTextService TextService { get; }
    IPdfAnnotationService AnnotationService { get; }
    IPdfSaveService SaveService { get; }
    IPdfFormService FormService { get; }
    IPdfSignatureService SignatureService { get; }
    Pages.IPdfPageOrganizerService PageOrganizer { get; }
    Redaction.IPdfRedactionService RedactionService { get; }
}
