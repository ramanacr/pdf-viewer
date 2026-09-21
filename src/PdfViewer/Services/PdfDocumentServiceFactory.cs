using System;
using PdfEngine.Vector;
using PdfViewer.Core.Security;

namespace PdfViewer.Services;

/// <summary>
/// Factory to instantiate IPdfDocumentService with centralized engine selection (pdfium, vector, hybrid).
/// </summary>
public static class PdfDocumentServiceFactory
{
    private static PdfEngineMode _configuredMode = PdfEngineMode.Auto;

    public static PdfEngineMode EngineMode
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("PDF_ENGINE_MODE");
            if (!string.IsNullOrEmpty(env) && Enum.TryParse<PdfEngineMode>(env, true, out var mode))
            {
                return mode;
            }
            return _configuredMode;
        }
        set => _configuredMode = value;
    }

    public static string CurrentEngine => EngineMode.ToString();

    /// <summary>
    /// Creates the document service under a security policy according to the active engine mode.
    /// </summary>
    public static IPdfDocumentService CreateService(PdfSecurityPolicy? securityPolicy = null)
    {
        var policy = securityPolicy ?? PdfSecurityPolicy.DefaultStrict;
        var mode = EngineMode;

        if (mode == PdfEngineMode.Pdfium)
        {
            return new PdfiumDocumentService(policy);
        }

        return new HybridVectorDocumentService(policy, mode);
    }
}
