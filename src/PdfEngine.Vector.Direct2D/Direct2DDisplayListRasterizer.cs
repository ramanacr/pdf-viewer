using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.PrintProduction;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Draws display lists for print production (separation plates, preflight ink coverage) with the Direct2D
/// renderer, without a fallback renderer: areas the engine cannot draw exactly are drawn by its best effort.
/// </summary>
public sealed class Direct2DDisplayListRasterizer : IPdfDisplayListRasterizer, IDisposable
{
    private readonly Direct2DVectorRenderer _renderer;
    private readonly bool _ownsRenderer;

    public Direct2DDisplayListRasterizer(Direct2DVectorRenderer? renderer = null)
    {
        _ownsRenderer = renderer == null;
        _renderer = renderer ?? new Direct2DVectorRenderer();
    }

    public async Task<byte[]> RenderAsync(IPdfDisplayList list, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        bool sideways = ((list.RotationDegrees % 360) + 360) % 360 is 90 or 270;
        double widthPoints = sideways ? crop.Height : crop.Width;
        var request = new RenderRequest
        {
            PageNumber = list.PageNumber,
            Dpi = widthPoints > 0 ? width * 72.0 / widthPoints : 72,
            TargetWidthPixels = width,
            TargetHeightPixels = height,
        };
        var result = await _renderer.RenderAsync(list, request, null, cancellationToken).ConfigureAwait(false);
        using var page = result.Page;
        return Direct2DPageRasterizer.Crop(page, 0, 0, Math.Min(width, page.WidthPixels), Math.Min(height, page.HeightPixels));
    }

    public void Dispose()
    {
        if (_ownsRenderer) _renderer.Dispose();
    }
}
