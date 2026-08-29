using System.Runtime.InteropServices;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Forms;
using PdfEngine.Pages;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Pdfium.Native;
using PdfEngine.Redaction;
using PdfEngine.Rendering;
using PdfEngine.Save;
using PdfEngine.Signatures;
using PdfEngine.Text;

namespace PdfEngine.Pdfium;

/// <summary>
/// Root Google PDFium engine implementation providing document management and rendering subsystems.
/// </summary>
public sealed class PdfiumEngine : IPdfEngine
{
    public string EngineName => "Google PDFium Native";
    public string EngineVersion => "154.0.8021.0 (Chromium 8021)";

    public IPdfRenderer Renderer { get; }
    public IPdfTextService TextService { get; }
    public IPdfAnnotationService AnnotationService { get; }
    public IPdfSaveService SaveService { get; }
    public IPdfFormService FormService { get; }
    public IPdfSignatureService SignatureService { get; }
    public IPdfPageOrganizerService PageOrganizer { get; }
    public IPdfRedactionService RedactionService { get; }

    public PdfiumEngine()
    {
        PdfiumNativeBridge.EnsureInitialized();

        Renderer = new PdfiumRenderer();
        TextService = new PdfiumTextService();
        AnnotationService = new PdfiumAnnotationService();
        SaveService = new PdfiumSaveService(Renderer);
        FormService = new PdfiumFormService();
        SignatureService = new PdfiumSignatureService();
        PageOrganizer = new PdfiumPageOrganizerService();
        RedactionService = new PdfiumRedactionService();
    }

    public async ValueTask<IPdfDocument> OpenDocumentAsync(
        string filePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"PDF file not found: {filePath}", filePath);

        cancellationToken.ThrowIfCancellationRequested();

        byte[] fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return OpenDocumentFromBytesInternal(filePath, fileBytes, password);
    }

    public ValueTask<IPdfDocument> OpenDocumentAsync(
        byte[] pdfBytes,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (pdfBytes == null || pdfBytes.Length == 0)
            throw new ArgumentException("PDF bytes cannot be null or empty.", nameof(pdfBytes));

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(OpenDocumentFromBytesInternal(string.Empty, pdfBytes, password));
    }

    private static IPdfDocument OpenDocumentFromBytesInternal(string filePath, byte[] fileBytes, string? password)
    {
        IntPtr unmanagedBuf = Marshal.AllocHGlobal(fileBytes.Length);
        Marshal.Copy(fileBytes, 0, unmanagedBuf, fileBytes.Length);

        var docHandle = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, password);
        if (docHandle == null || docHandle.IsInvalid)
        {
            Marshal.FreeHGlobal(unmanagedBuf);
            uint err = PdfiumNativeBridge.FPDF_GetLastError();

            if (err == PdfiumNativeBridge.FPDF_ERR_PASSWORD)
            {
                throw new PdfPasswordRequiredException("Password required or invalid password provided.", filePath);
            }
            else if (err == PdfiumNativeBridge.FPDF_ERR_FORMAT || err == PdfiumNativeBridge.FPDF_ERR_FILE)
            {
                throw new PdfCorruptDocumentException($"Failed to parse PDF document (error code {err}).", filePath);
            }
            else
            {
                throw new PdfOpenException($"Failed to open PDF document (error code {err}).", filePath);
            }
        }

        int pageCount = PdfiumNativeBridge.FPDF_GetPageCount(docHandle);
        var meta = ExtractMetadata(docHandle, filePath, fileBytes.Length, pageCount);

        return new PdfiumDocument(filePath, docHandle, unmanagedBuf, meta, pageCount);
    }

    private static DocumentMetadata ExtractMetadata(SafeDocumentHandle docHandle, string filePath, long fileLength, int pageCount)
    {
        string GetTag(string tag)
        {
            uint len = PdfiumNativeBridge.FPDF_GetMetaText(docHandle, tag, null, 0);
            if (len <= 0) return string.Empty;
            byte[] buf = new byte[len];
            PdfiumNativeBridge.FPDF_GetMetaText(docHandle, tag, buf, len);
            return PdfiumNativeBridge.Utf16BytesToString(buf, (int)len);
        }

        string title = GetTag("Title");
        string author = GetTag("Author");
        string subject = GetTag("Subject");
        string keywords = GetTag("Keywords");
        string creator = GetTag("Creator");
        string producer = GetTag("Producer");

        int securityHandler = PdfiumNativeBridge.FPDF_GetSecurityHandlerRevision(docHandle);
        bool isEncrypted = securityHandler > 0;

        return new DocumentMetadata
        {
            Title = title,
            Author = author,
            Subject = subject,
            Keywords = keywords,
            Creator = creator,
            Producer = producer,
            PageCount = pageCount,
            FileSizeBytes = fileLength,
            FilePath = filePath,
            IsEncrypted = isEncrypted
        };
    }

    public void Dispose()
    {
        // Global library lifecycle is managed on process exit
    }
}
