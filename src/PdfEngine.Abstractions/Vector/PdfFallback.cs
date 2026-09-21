using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;

namespace PdfEngine.Vector;

/// <summary>
/// Classified reason why a PDF construct or region requires fallback rendering.
/// Never use free-form strings for fallback decisions.
/// </summary>
public enum PdfFallbackReason
{
    None = 0,
    UnsupportedFontType,
    UnsupportedCMap,
    Type3Font,
    UnsupportedColorSpace,
    Pattern,
    Shading,
    SoftMask,
    TransparencyGroup,
    BlendMode,
    UnsupportedImageFilter,
    MalformedContentRecovery,
    EncryptedContent,
    UnsupportedAnnotationAppearance,
    UnknownOperator,
    InternalCompatibilityGuard
}

/// <summary>
/// Operational mode for PDF processing and rendering.
/// </summary>
public enum PdfEngineMode
{
    /// <summary>
    /// Hybrid mode (production default): Vector-preferred, with localized fallback for unsupported constructs.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Existing baseline behavior: uses PDFium exclusively.
    /// </summary>
    Pdfium = 1,

    /// <summary>
    /// Strict vector mode: unsupported features yield explicit diagnostic errors/placeholders. Used for dev & CI.
    /// </summary>
    Vector = 2,

    /// <summary>
    /// Explicit hybrid mode.
    /// </summary>
    Hybrid = 3
}

/// <summary>
/// Stable token identifying a region requiring fallback rendering.
/// Contains no PDFium types or handles.
/// </summary>
public sealed record PdfFallbackToken(
    int PageNumber,
    PdfRect Bounds,
    PdfFallbackReason Reason,
    string Description,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Contract for rendering fallback regions (e.g. via PDFium adapter or offscreen rasterizer).
/// </summary>
public interface IPdfFallbackProvider
{
    ValueTask<RenderedPage?> RenderFallbackRegionAsync(
        PdfFallbackToken token,
        double dpi,
        CancellationToken cancellationToken = default);
}
