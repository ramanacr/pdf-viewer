using System;

namespace PdfViewer.Services;

/// <summary>
/// Factory to instantiate IPdfDocumentService. Backed exclusively by Google PDFium.
/// </summary>
public static class PdfDocumentServiceFactory
{
    public static string CurrentEngine => "Pdfium";

    public static IPdfDocumentService CreateService()
    {
        return new PdfiumDocumentService();
    }
}
