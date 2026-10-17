using System;

namespace PdfEngine.Vector.Color;

/// <summary>Colour conversions the rendering backends share with the core, so every backend shows the same colours.</summary>
public static class PdfColorConversion
{
    /// <summary>
    /// Converts 8-bit CMYK pixels (C, M, Y, K per pixel) to opaque BGRA through the DeviceCMYK conversion.
    /// <paramref name="inverted"/> undoes Adobe-style inverted CMYK JPEG samples first.
    /// </summary>
    public static byte[] CmykToBgra(byte[] cmyk, int pixels, bool inverted)
    {
        ArgumentNullException.ThrowIfNull(cmyk);
        if (pixels < 0 || (long)pixels * 4 > cmyk.Length)
            throw new ArgumentOutOfRangeException(nameof(pixels));
        byte[] samples = cmyk;
        if (inverted)
        {
            samples = new byte[pixels * 4];
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(255 - cmyk[i]);
        }
        var bgra = new byte[pixels * 4];
        var lut = DefaultCmyk.For(PdfColorSpace.DefaultCmykIntent).ImageLut!;
        lut.ToBgra(samples, 0, bgra, 0, pixels);
        return bgra;
    }
}
