using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfViewer.Services;

/// <summary>
/// Turns a rendered page into its night-mode form.
///
/// The page is inverted rather than the window being darkened around it: a dark reader with a
/// blazing white page is worse than no dark mode at all, which is the state this application
/// was in. Photographs invert too, which is how every other reader's night mode behaves - the
/// alternative is guessing which parts of a page are "content", and guessing wrong on someone
/// else's document is worse than a predictable rule.
/// </summary>
public static class NightModeImage
{
    /// <summary>
    /// Returns an inverted, frozen copy. Returns the original unchanged if it cannot be
    /// converted - a page that renders slightly wrong beats a page that does not render.
    /// </summary>
    public static BitmapSource Invert(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            // Normalise to a known layout so the byte arithmetic below is always right,
            // whatever the renderer handed us.
            var bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            if (width <= 0 || height <= 0) return source;

            int stride = width * 4;
            long byteCount = (long)stride * height;

            // A page far larger than any real render is a sign something is wrong; leave it
            // alone rather than allocating hundreds of megabytes for it.
            if (byteCount > 512L * 1024 * 1024) return source;

            byte[] pixels = new byte[byteCount];
            bgra.CopyPixels(pixels, stride, 0);

            // Blue, green and red are inverted; alpha is left alone so transparent regions
            // stay transparent instead of turning into opaque black.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(255 - pixels[i]);
                pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                pixels[i + 2] = (byte)(255 - pixels[i + 2]);
            }

            var inverted = BitmapSource.Create(
                width, height, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null, pixels, stride);

            inverted.Freeze();
            return inverted;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                      or ArgumentException or OutOfMemoryException)
        {
            return source;
        }
    }
}
