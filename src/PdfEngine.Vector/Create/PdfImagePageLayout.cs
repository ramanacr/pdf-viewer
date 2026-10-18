using System;

namespace PdfEngine.Vector.Create;

/// <summary>Where an image's page gets its size.</summary>
public enum PdfImagePageSize
{
    /// <summary>The page is the image's own size, from its pixels and resolution.</summary>
    FitImage,
    /// <summary>A paper size (<see cref="PdfImagePageOptions.PaperWidth"/> by <see cref="PdfImagePageOptions.PaperHeight"/>) with margins.</summary>
    Paper,
}

/// <summary>How each image is put on its page.</summary>
public sealed record PdfImagePageOptions
{
    public PdfImagePageSize Size { get; init; } = PdfImagePageSize.FitImage;

    /// <summary>The paper, in points (portrait); A4 by default.</summary>
    public double PaperWidth { get; init; } = 595.276;
    public double PaperHeight { get; init; } = 841.89;

    /// <summary>The margin on each side of the paper, in points.</summary>
    public double Margin { get; init; } = 36;

    /// <summary>Cover the area inside the margins (cropping what overflows) rather than fit inside it.</summary>
    public bool Fill { get; init; }

    /// <summary>Turn the paper to landscape for a landscape image.</summary>
    public bool AutoOrientation { get; init; } = true;

    public static readonly (double Width, double Height) A4 = (595.276, 841.89);
    public static readonly (double Width, double Height) Letter = (612, 792);
    public static readonly (double Width, double Height) Legal = (612, 1008);
}

/// <summary>Where an image goes: the page's size and the rectangle the upright image fills, in points.</summary>
public readonly record struct PdfImagePlacement(double PageWidth, double PageHeight, double X, double Y, double Width, double Height, bool Clip)
{
    /// <summary>The largest page PDF readers are required to show (200 inches, ISO 32000-2 Annex C).</summary>
    public const double MaxPageSide = 14_400;

    public static PdfImagePlacement For(PdfCreateImage image, PdfImagePageOptions options)
    {
        var (w, h) = image.NaturalSize;
        if (options.Size == PdfImagePageSize.FitImage)
        {
            double k = Math.Min(1, MaxPageSide / Math.Max(w, h));
            w = Math.Max(1, w * k);
            h = Math.Max(1, h * k);
            return new PdfImagePlacement(w, h, 0, 0, w, h, false);
        }

        double pw = options.PaperWidth, ph = options.PaperHeight;
        if (options.AutoOrientation && (w > h) != (pw > ph)) (pw, ph) = (ph, pw);
        double margin = Math.Clamp(options.Margin, 0, Math.Min(pw, ph) / 2 - 1);
        double bw = pw - 2 * margin, bh = ph - 2 * margin;
        double scale = options.Fill ? Math.Max(bw / w, bh / h) : Math.Min(bw / w, bh / h);
        double dw = w * scale, dh = h * scale;
        return new PdfImagePlacement(pw, ph, margin + (bw - dw) / 2, margin + (bh - dh) / 2, dw, dh, options.Fill);
    }

    /// <summary>
    /// The image matrix (a b c d e f) that draws the stored pixels upright in this rectangle: the
    /// EXIF orientation is applied here, so a JPEG keeps its bytes and still shows the right way up.
    /// </summary>
    public (double A, double B, double C, double D, double E, double F) Matrix(int orientation)
    {
        double x = X, y = Y, w = Width, h = Height;
        return orientation switch
        {
            2 => (-w, 0, 0, h, x + w, y),
            3 => (-w, 0, 0, -h, x + w, y + h),
            4 => (w, 0, 0, -h, x, y + h),
            5 => (0, -h, -w, 0, x + w, y + h),
            6 => (0, -h, w, 0, x, y + h),
            7 => (0, h, w, 0, x, y),
            8 => (0, h, -w, 0, x + w, y),
            _ => (w, 0, 0, h, x, y),
        };
    }
}
