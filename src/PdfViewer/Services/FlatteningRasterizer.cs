using PdfEngine.Vector.Direct2D;

namespace PdfViewer.Services;

/// <summary>
/// The rasterizer PDF/A-1b conversion flattens transparency with: the vector engine's Direct2D
/// renderer alone. A page with content the engine cannot reproduce exactly is flattened as the
/// engine draws it on screen (best effort), never with another renderer's pixels or placeholders.
/// </summary>
internal static class FlatteningRasterizer
{
    public static Direct2DPageRasterizer Create() => new(fallback: null, bestEffort: true);
}
