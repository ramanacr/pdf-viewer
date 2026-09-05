using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Rendering;

namespace PdfViewer.RenderingAdapters;

/// <summary>
/// Converts engine-neutral RenderedPage pixel buffers into frozen WPF BitmapSource instances.
/// </summary>
public static class WpfBitmapAdapter
{
    public static BitmapSource ToBitmapSource(RenderedPage renderedPage)
    {
        if (renderedPage == null) throw new ArgumentNullException(nameof(renderedPage));

        int width = renderedPage.WidthPixels;
        int height = renderedPage.HeightPixels;
        int stride = renderedPage.Stride;
        double dpi = renderedPage.Dpi;

        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Rendered page has no pixels ({width}x{height}).", nameof(renderedPage));

        // Validate the buffer geometry before handing it to WPF. BitmapSource.Create reads
        // stride * height bytes; a mismatch here would either throw deep inside WPF or read
        // past the buffer.
        if (stride < width * 4)
            throw new ArgumentException($"Stride {stride} is smaller than {width} pixels * 4 bytes.", nameof(renderedPage));

        // Accessing Pixels throws ObjectDisposedException if the page's buffer was already
        // released, which is the correct, diagnosable failure.
        var memory = renderedPage.Pixels;
        if (memory.Length < (long)stride * height)
            throw new ArgumentException(
                $"Pixel buffer is {memory.Length} bytes, need {(long)stride * height}.", nameof(renderedPage));

        byte[] pixelArray = memory.ToArray();

        var bitmapSource = BitmapSource.Create(
            width,
            height,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            null,
            pixelArray,
            stride);

        // Freeze unconditionally: the result is always freezable, and silently skipping it
        // would hand an unfrozen, thread-affine bitmap to the UI thread from a worker.
        bitmapSource.Freeze();

        return bitmapSource;
    }
}
