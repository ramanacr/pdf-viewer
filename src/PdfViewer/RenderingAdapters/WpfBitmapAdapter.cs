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

        var memory = renderedPage.Pixels;
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

        if (bitmapSource.CanFreeze)
        {
            bitmapSource.Freeze();
        }

        return bitmapSource;
    }
}
