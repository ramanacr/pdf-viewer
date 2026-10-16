using System;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.PdfA;

namespace PdfViewer.Services;

/// <summary>
/// The rasterizer PDF/A-1b conversion flattens transparency with: the vector engine's Direct2D
/// renderer, and PDFium for pages the engine cannot draw by itself. It lives at the composition
/// root because only the viewer references PDFium (ADR-007).
/// </summary>
internal static class FlatteningRasterizer
{
    public static Direct2DPageRasterizer Create() => new(PdfiumPage);

    /// <summary>A page drawn by PDFium at exactly width × height pixels, without annotations.</summary>
    private static PdfRasterImage? PdfiumPage(byte[] bytes, int page, int width, int height) => Task.Run(async () =>
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(bytes, null);
        using var rendered = await engine.Renderer.RenderPageAsync(doc, new RenderRequest
        {
            PageNumber = page, TargetWidthPixels = width, TargetHeightPixels = height, RenderAnnotations = false, RenderForms = false,
        });
        if (rendered.WidthPixels != width || rendered.HeightPixels != height) return null;
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            rendered.Pixels.Span.Slice(y * rendered.Stride, width * 4).CopyTo(bgra.AsSpan(y * width * 4));
        return new PdfRasterImage(width, height, bgra, "PDFium");
    }).GetAwaiter().GetResult();
}
