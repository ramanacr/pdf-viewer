using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;

namespace PdfViewer.Web.Engine;

/// <summary>What a host shows when a page could not be drawn. The message never contains document content.</summary>
public static class ViewerRenderFailure
{
    /// <summary>
    /// True when the page (or the part asked for) needs a bitmap larger than one render can hold:
    /// a huge MediaBox or UserUnit, or a resolution past
    /// <see cref="PdfSecurityLimits.MaxRenderPixelCount"/>, or a buffer the process could not allocate.
    /// </summary>
    public static bool IsTooLarge(Exception ex) =>
        ex is PdfResourceLimitException { LimitName: nameof(PdfSecurityLimits.MaxRenderPixelCount) };

    /// <summary>A short message for a page that could not be drawn, fit to show to the user.</summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="ex">The failure.</param>
    public static string Describe(int page, Exception ex) =>
        IsTooLarge(ex)
            ? $"Page {page} is too large to show."
            // Diagnostics name the failure type only, never document content.
            : $"Page {page} could not be drawn ({ex.GetType().Name}).";
}
