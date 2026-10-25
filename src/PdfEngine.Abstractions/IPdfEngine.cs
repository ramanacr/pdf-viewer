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
    /// <summary>A display name of the engine.</summary>
    string EngineName { get; }
    /// <summary>The engine version, for diagnostics.</summary>
    string EngineVersion { get; }

    /// <summary>Opens a PDF file.</summary>
    /// <param name="filePath">The path of the PDF file.</param>
    /// <param name="password">The password of an encrypted document, or null.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The open document. Dispose it when you no longer need it.</returns>
    ValueTask<IPdfDocument> OpenDocumentAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a PDF from bytes in memory.</summary>
    /// <param name="pdfBytes">The complete PDF file.</param>
    /// <param name="password">The password of an encrypted document, or null.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The open document. Dispose it when you no longer need it.</returns>
    ValueTask<IPdfDocument> OpenDocumentAsync(
        byte[] pdfBytes,
        string? password = null,
        CancellationToken cancellationToken = default);

    /// <summary>Renders pages to pixels.</summary>
    IPdfRenderer Renderer { get; }
    /// <summary>Extracts and searches text.</summary>
    IPdfTextService TextService { get; }
    /// <summary>Reads and writes annotations.</summary>
    IPdfAnnotationService AnnotationService { get; }
    /// <summary>Saves documents and exports pages as images.</summary>
    IPdfSaveService SaveService { get; }
    /// <summary>Reads and fills interactive form fields.</summary>
    IPdfFormService FormService { get; }
    /// <summary>Reads and verifies digital signatures.</summary>
    IPdfSignatureService SignatureService { get; }
    /// <summary>Inserts, deletes, rotates, extracts and splits pages.</summary>
    Pages.IPdfPageOrganizerService PageOrganizer { get; }
    /// <summary>Marks and applies redactions.</summary>
    Redaction.IPdfRedactionService RedactionService { get; }
}
