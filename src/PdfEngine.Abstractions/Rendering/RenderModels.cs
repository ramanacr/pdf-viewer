using System.Buffers;
using PdfEngine.Geometry;

namespace PdfEngine.Rendering;

/// <summary>
/// Page rotation angle.
/// </summary>
public enum PageRotation
{
    Rotate0 = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}

/// <summary>
/// Priority for scheduling page render requests.
/// </summary>
public enum RenderPriority
{
    Visible = 0,
    Adjacent = 1,
    Selected = 2,
    NearViewport = 3,
    Thumbnail = 4,
    Prefetch = 5
}

/// <summary>
/// Render request parameters.
/// </summary>
public record RenderRequest
{
    public int PageNumber { get; init; }
    public double Dpi { get; init; } = 96.0;
    public int TargetWidthPixels { get; init; }
    public int TargetHeightPixels { get; init; }
    public PageRotation Rotation { get; init; } = PageRotation.Rotate0;
    public RenderPriority Priority { get; init; } = RenderPriority.Visible;
    public bool RenderAnnotations { get; init; } = true;
    public bool RenderForms { get; init; } = true;
    public bool HighQuality { get; init; } = true;
    public long DocumentRevision { get; init; } = 0;
}

/// <summary>
/// Raw rendered pixel buffer in 32-bit BGRA format, completely UI-neutral and memory-managed.
/// </summary>
public class RenderedPage : IDisposable
{
    public int PageNumber { get; }
    public int WidthPixels { get; }
    public int HeightPixels { get; }
    public int Stride { get; }
    public double Dpi { get; }
    public PageRotation Rotation { get; }
    public IMemoryOwner<byte> PixelMemory { get; }
    public ReadOnlyMemory<byte> Pixels => PixelMemory.Memory;
    public long ByteLength => (long)Stride * HeightPixels;

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
    ValueTask<RenderedPage> RenderPageAsync(
        IPdfDocument document,
        RenderRequest request,
        CancellationToken cancellationToken = default);
}
