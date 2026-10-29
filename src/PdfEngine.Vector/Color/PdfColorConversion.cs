using System;

namespace PdfEngine.Vector.Color;

/// <summary>Colour conversions the rendering backends share with the core, so every backend shows the same colours.</summary>
internal static class PdfColorConversion
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

    /// <summary>
    /// Converts 8-bit samples of a device space chosen by their component count (1 gray, 3 RGB,
    /// 4 CMYK) to opaque BGRA: how a JPEG whose components do not match its declared colour space
    /// is shown, in its own colours, as PDFium shows it.
    /// </summary>
    public static byte[] DeviceSamplesToBgra(byte[] samples, int components, int pixels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (pixels < 0 || (long)pixels * components > samples.Length)
            throw new ArgumentOutOfRangeException(nameof(pixels));
        if (components == 4)
            return CmykToBgra(samples, pixels, inverted: false);
        var bgra = new byte[pixels * 4];
        for (int i = 0, p = 0; i < pixels; i++, p += 4)
        {
            if (components >= 3)
            {
                bgra[p] = samples[i * components + 2];
                bgra[p + 1] = samples[i * components + 1];
                bgra[p + 2] = samples[i * components];
            }
            else
            {
                bgra[p] = bgra[p + 1] = bgra[p + 2] = samples[i * components];
            }
            bgra[p + 3] = 255;
        }
        return bgra;
    }
}
