using System;
using PdfEngine.Vector;
using PdfViewer.Core.Security;

namespace PdfViewer.Services;

/// <summary>
/// Factory to instantiate IPdfDocumentService with centralized engine selection (vector, hybrid,
/// and PDFium in developer builds).
/// </summary>
/// <remarks>
/// The vector engine is the only renderer the viewer ships with (M9). The PDFium page renderer is
/// compiled in only when the <c>PDFIUM_RENDERER</c> constant is defined: in Debug builds (developer
/// and test runs, where it is the oracle the corpus and differential tests compare with) and in any
/// build made with <c>-p:PdfiumRenderer=true</c>. A Release build ignores
/// <see cref="PdfEngineMode.Pdfium"/>, from the setting or from <c>PDF_ENGINE_MODE</c>, and renders
/// with the vector engine (<see cref="PdfEngineMode.Auto"/>).
/// </remarks>
public static class PdfDocumentServiceFactory
{
    private static PdfEngineMode _configuredMode = PdfEngineMode.Auto;

    /// <summary>True when this build can render whole pages with PDFium (developer and test builds).</summary>
    public static bool IsPdfiumRendererAvailable =>
#if PDFIUM_RENDERER
        true;
#else
        false;
#endif

    /// <summary>The mode this build can run: PDFium becomes Auto where the PDFium renderer is not compiled in.</summary>
    public static PdfEngineMode Normalize(PdfEngineMode mode) =>
        mode == PdfEngineMode.Pdfium && !IsPdfiumRendererAvailable ? PdfEngineMode.Auto : mode;

    public static PdfEngineMode EngineMode
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("PDF_ENGINE_MODE");
            if (!string.IsNullOrEmpty(env) && Enum.TryParse<PdfEngineMode>(env, true, out var mode))
            {
                return Normalize(mode);
            }
            return Normalize(_configuredMode);
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

#if PDFIUM_RENDERER
        if (mode == PdfEngineMode.Pdfium)
        {
            return new PdfiumDocumentService(policy);
        }
#endif

        return new HybridVectorDocumentService(policy, mode);
    }
}
