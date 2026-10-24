using Android.Content;
using Android.Graphics;
using Android.Views;
using SkiaSharp;

namespace PdfViewer.Android;

/// <summary>
/// A view drawn with SkiaSharp on the CPU: each frame, Skia draws straight into the pixels of an
/// Android bitmap the size of the view, which is then blitted to the view's canvas. This is what
/// SkiaSharp's own SKCanvasView does; it is written here because that view ships in a separate
/// package (SkiaSharp.Views), and the app needs nothing more than this from it.
/// </summary>
internal abstract class SkiaView(Context context) : View(context)
{
    private Bitmap? _bitmap;

    /// <summary>Draws the frame; the canvas is in view pixels.</summary>
    protected abstract void OnPaintSurface(SKCanvas canvas, int width, int height);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;
        if (_bitmap == null || _bitmap.Width != w || _bitmap.Height != h)
        {
            _bitmap?.Recycle();
            _bitmap?.Dispose();
            _bitmap = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
        }
        // Android's ARGB_8888 is RGBA bytes in memory, premultiplied.
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        IntPtr pixels = _bitmap.LockPixels();
        try
        {
            using var surface = SKSurface.Create(info, pixels, _bitmap.RowBytes);
            if (surface != null)
            {
                OnPaintSurface(surface.Canvas, w, h);
                surface.Canvas.Flush();
            }
        }
        finally
        {
            _bitmap.UnlockPixels();
        }
        canvas.DrawBitmap(_bitmap, 0, 0, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap?.Recycle();
            _bitmap?.Dispose();
            _bitmap = null;
        }
        base.Dispose(disposing);
    }
}
