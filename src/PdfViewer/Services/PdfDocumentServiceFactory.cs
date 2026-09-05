using System;
using PdfViewer.Core.Security;

namespace PdfViewer.Services;

/// <summary>
/// Factory to instantiate IPdfDocumentService. Backed exclusively by Google PDFium.
/// </summary>
public static class PdfDocumentServiceFactory
{
    public static string CurrentEngine => "Pdfium";

    /// <summary>
    /// Creates the document service under a security policy. Defaults to the strict policy
    /// so a caller that forgets to pass one still gets the size and render ceilings.
    /// </summary>
    public static IPdfDocumentService CreateService(PdfSecurityPolicy? securityPolicy = null)
    {
        return new PdfiumDocumentService(securityPolicy ?? PdfSecurityPolicy.DefaultStrict);
    }
}
