using System;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Editing;

namespace PdfViewer.Services;

/// <summary>
/// The picture of a handwritten signature: read from a PNG or JPEG file, or drawn with the mouse,
/// pen or finger, and kept as a PNG with its transparency. It is remembered between signatures
/// only when the user asks for that, in their own profile (%LOCALAPPDATA%), never in a document;
/// forgetting it deletes the file.
/// </summary>
public static class SignatureImageStore
{
    /// <summary>The longest side a signature picture is kept at: sharp when printed, small in the file.</summary>
    public const int MaxSide = 1200;

    private static string FilePath => Path.Combine(SigningSettings.Directory, "signature-image.png");

    /// <summary>Where a remembered picture is kept (for telling the user).</summary>
    public static string Location => FilePath;

    /// <summary>The remembered picture (PNG), or null.</summary>
    public static byte[]? LoadRemembered()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Remember(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        try
        {
            Directory.CreateDirectory(SigningSettings.Directory);
            File.WriteAllBytes(FilePath, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience; signing does not depend on it.
        }
    }

    public static void Forget()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A picture file (PNG, JPEG, and what else Windows reads) as a PNG, upright and at most <see cref="MaxSide"/> pixels across.</summary>
    public static byte[] PngFromFile(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        using var ms = new MemoryStream(file, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > EditImageLoader.MaxPixels)
            throw new NotSupportedException($"The picture is {frame.PixelWidth} x {frame.PixelHeight} pixels, more than can be used.");
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        return Encode(new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0));
    }

    /// <summary>
    /// Ink strokes as a PNG: cropped to the ink with a small margin, drawn at <paramref name="scale"/>
    /// times their size on a transparent background, so the page shows through around the ink.
    /// Null when there is no ink. Must run on a UI (STA) thread.
    /// </summary>
    public static byte[]? PngFromStrokes(StrokeCollection strokes, double scale = 3)
    {
        if (strokes.Count == 0) return null;
        var bounds = strokes.GetBounds();
        if (bounds.IsEmpty || bounds.Width < 1 && bounds.Height < 1) return null;
        bounds.Inflate(4, 4);
        scale = Math.Min(scale, MaxSide / Math.Max(bounds.Width, bounds.Height));
        int w = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            strokes.Draw(dc);
        }
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return Encode(new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0));
    }

    /// <summary>The picture for the signature's appearance.</summary>
    public static PdfImageContent ToImage(byte[] png) => EditImageLoader.Load(png);

    /// <summary>A small preview of the picture for the dialog.</summary>
    public static BitmapSource Preview(byte[] png)
    {
        var image = new BitmapImage();
        using var ms = new MemoryStream(png, writable: false);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
