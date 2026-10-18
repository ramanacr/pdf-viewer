using System;

namespace PdfEngine.Vector.Create;

/// <summary>The colour of an image's samples.</summary>
public enum PdfImageColor
{
    Gray,
    Rgb,
    Cmyk,
}

/// <summary>How an image's data is held.</summary>
public enum PdfImageData
{
    /// <summary>A complete JPEG file, written as it is (DCTDecode): no quality is lost and nothing is decoded.</summary>
    Jpeg,
    /// <summary>Uncompressed samples, row after row with no padding, written losslessly (FlateDecode with a PNG predictor).</summary>
    Samples,
}

/// <summary>
/// One image for a new page: either a JPEG file to carry as it is, or samples to compress
/// losslessly. Samples are 1, 8 or 16 bits per component (16-bit big-endian, as PDF wants them),
/// in the colour <see cref="Color"/> or, with a <see cref="Palette"/>, indices into it.
/// </summary>
public sealed class PdfCreateImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required PdfImageColor Color { get; init; }
    public int BitsPerComponent { get; init; } = 8;
    public required PdfImageData Kind { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>The source's ICC profile, used when it matches <see cref="Color"/>; device colour otherwise.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>RGB triplets (or grey bytes): <see cref="Data"/> holds indices of <see cref="BitsPerComponent"/> bits.</summary>
    public byte[]? Palette { get; init; }

    /// <summary>Opacity, 8 bits a pixel, row after row; null when the image is opaque.</summary>
    public ReadOnlyMemory<byte>? Alpha { get; init; }

    /// <summary>A four-component JPEG written by Adobe software, whose samples are stored inverted.</summary>
    public bool InvertedCmyk { get; init; }

    /// <summary>Pixels per inch; 0 means unknown (taken as 96, as Windows does).</summary>
    public double DpiX { get; init; }
    public double DpiY { get; init; }

    /// <summary>The data are the source file's own bytes, carried unchanged (a JPEG passed through).</summary>
    public bool Unchanged { get; init; }

    /// <summary>EXIF orientation 1 to 8: how the stored pixels are turned or mirrored to be seen upright.</summary>
    public int Orientation { get; init; } = 1;

    internal int Components => Palette != null ? 1 : Color switch { PdfImageColor.Gray => 1, PdfImageColor.Rgb => 3, _ => 4 };

    /// <summary>Orientations 5 to 8 turn the image a quarter: its width is seen as its height.</summary>
    internal bool Transposed => Orientation is >= 5 and <= 8;

    /// <summary>The size the image is seen at, in points, from its pixels and resolution.</summary>
    public (double Width, double Height) NaturalSize
    {
        get
        {
            double dx = DpiX is > 1 and < 100_000 ? DpiX : 96, dy = DpiY is > 1 and < 100_000 ? DpiY : dx;
            double w = Width * 72.0 / dx, h = Height * 72.0 / dy;
            return Transposed ? (h, w) : (w, h);
        }
    }
}
