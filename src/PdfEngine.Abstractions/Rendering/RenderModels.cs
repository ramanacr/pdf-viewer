using System.Buffers;
using PdfEngine.Geometry;

namespace PdfEngine.Rendering;

/// <summary>
/// Page rotation angle.
/// </summary>
public enum PageRotation
{
    /// <summary>No rotation.</summary>
    Rotate0 = 0,
    /// <summary>90 degrees clockwise.</summary>
    Rotate90 = 90,
    /// <summary>180 degrees.</summary>
    Rotate180 = 180,
    /// <summary>270 degrees clockwise (90 degrees counter-clockwise).</summary>
    Rotate270 = 270
}

/// <summary>
/// Priority for scheduling page render requests.
/// </summary>
public enum RenderPriority
{
    /// <summary>A page that is visible now.</summary>
    Visible = 0,
    /// <summary>A page next to a visible page.</summary>
    Adjacent = 1,
    /// <summary>A page the user selected.</summary>
    Selected = 2,
    /// <summary>A page near the visible area.</summary>
    NearViewport = 3,
    /// <summary>A thumbnail image.</summary>
    Thumbnail = 4,
    /// <summary>A page rendered ahead of need.</summary>
    Prefetch = 5
}

/// <summary>
/// Render request parameters.
/// </summary>
public record RenderRequest
{
    /// <summary>The page to render, starting at 1.</summary>
    public int PageNumber { get; init; }
    /// <summary>
    /// The resolution in pixels per inch (default 96). A page of W x H points renders at
    /// W x Dpi / 72 by H x Dpi / 72 pixels, unless <see cref="TargetWidthPixels"/> and
    /// <see cref="TargetHeightPixels"/> set the size.
    /// </summary>
    public double Dpi { get; init; } = 96.0;
    /// <summary>The output width in pixels; 0 (the default) computes it from <see cref="Dpi"/>.</summary>
    public int TargetWidthPixels { get; init; }
    /// <summary>The output height in pixels; 0 (the default) computes it from <see cref="Dpi"/>.</summary>
    public int TargetHeightPixels { get; init; }
    /// <summary>A viewer rotation added to the page's own /Rotate.</summary>
    public PageRotation Rotation { get; init; } = PageRotation.Rotate0;
    /// <summary>A scheduling hint for hosts that queue render requests.</summary>
    public RenderPriority Priority { get; init; } = RenderPriority.Visible;
    /// <summary>A hint to draw annotations. A display-list renderer draws what the display list holds.</summary>
    public bool RenderAnnotations { get; init; } = true;
    /// <summary>A hint to draw form fields. A display-list renderer draws what the display list holds.</summary>
    public bool RenderForms { get; init; } = true;
    /// <summary>A hint to prefer quality over speed.</summary>
    public bool HighQuality { get; init; } = true;
    /// <summary>The document revision the request belongs to, so a host can drop renders of an older revision.</summary>
    public long DocumentRevision { get; init; } = 0;
}

/// <summary>
/// Raw rendered pixel buffer in 32-bit BGRA format, completely UI-neutral and memory-managed.
/// </summary>
public class RenderedPage : IDisposable
{
    /// <summary>The rendered page number, starting at 1.</summary>
    public int PageNumber { get; }
    /// <summary>The width of the image in pixels.</summary>
    public int WidthPixels { get; }
    /// <summary>The height of the image in pixels.</summary>
    public int HeightPixels { get; }
    /// <summary>The number of bytes per pixel row (at least 4 x <see cref="WidthPixels"/>).</summary>
    public int Stride { get; }
    /// <summary>The resolution the page was rendered at.</summary>
    public double Dpi { get; }
    /// <summary>The viewer rotation of the request.</summary>
    public PageRotation Rotation { get; }
    /// <summary>The owner of the pixel buffer. <see cref="Dispose"/> releases it.</summary>
    public IMemoryOwner<byte> PixelMemory { get; }
    /// <summary>The pixels: 32-bit BGRA, premultiplied alpha, rows from top to bottom, <see cref="Stride"/> bytes per row.</summary>
    public ReadOnlyMemory<byte> Pixels => PixelMemory.Memory;
    /// <summary>The size of the pixel data in bytes (<see cref="Stride"/> x <see cref="HeightPixels"/>).</summary>
    public long ByteLength => (long)Stride * HeightPixels;

    /// <summary>Wraps a rendered pixel buffer. The page takes ownership of <paramref name="pixelMemory"/>.</summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="widthPixels">The width in pixels.</param>
    /// <param name="heightPixels">The height in pixels.</param>
    /// <param name="stride">The bytes per pixel row.</param>
    /// <param name="dpi">The resolution of the render.</param>
    /// <param name="rotation">The viewer rotation of the request.</param>
    /// <param name="pixelMemory">The BGRA pixel buffer.</param>
    public RenderedPage(
        int pageNumber,
        int widthPixels,
        int heightPixels,
        int stride,
        double dpi,
        PageRotation rotation,
        IMemoryOwner<byte> pixelMemory)
    {
        PageNumber = pageNumber;
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        Stride = stride;
        Dpi = dpi;
        Rotation = rotation;
        PixelMemory = pixelMemory;
    }

    /// <summary>Releases the pixel buffer.</summary>
    public void Dispose()
    {
        PixelMemory?.Dispose();
    }
}

/// <summary>
/// Engine-neutral PDF page rasterization contract.
/// </summary>
public interface IPdfRenderer
{
    /// <summary>Renders a page of <paramref name="document"/> to BGRA pixels.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="request">The page, resolution and rotation.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The pixels. Dispose the result to release its buffer.</returns>
    ValueTask<RenderedPage> RenderPageAsync(
        IPdfDocument document,
        RenderRequest request,
        CancellationToken cancellationToken = default);
}
