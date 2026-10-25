using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Skia.Tests.Fixtures;
using PdfEngine.Vector.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Four-component (CMYK and YCCK) JPEG images in the Skia backend: the managed component decoder
/// (JpegComponentDecoder) against the samples it was given, against WIC's CMYK samples, and pages
/// with such images against the Direct2D backend. Every JPEG is written in code (JpegTestEncoder).
/// </summary>
public class CmykJpegTests
{
    private readonly Parity _parity;
    public CmykJpegTests(ITestOutputHelper output) => _parity = new Parity(output);

    /// <summary>
    /// Smooth CMYK ramps with a hard edge, as the JPEG stores them. In a PDF, stored samples are
    /// the colour (the engine and WIC agree with PDFium there); <paramref name="inverted"/> stores
    /// their inverse, which a /Decode [1 0 …] array then flips back.
    /// </summary>
    private static byte[] Cmyk(int w, int h, bool inverted = false)
    {
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte c = (byte)(255 * x / Math.Max(1, w - 1)), m = (byte)(255 * y / Math.Max(1, h - 1));
                byte yellow = (byte)(x < w / 2 ? 200 : 30), k = (byte)(y > h * 3 / 4 ? 120 : 0);
                data[i] = c; data[i + 1] = m; data[i + 2] = yellow; data[i + 3] = k;
                if (inverted)
                    for (int j = 0; j < 4; j++) data[i + j] = (byte)(255 - data[i + j]);
            }
        return data;
    }

    /// <summary>Stored CMYK samples as YCCK: the inverse of C, M, Y read as RGB, then YCbCr; K as it is.</summary>
    private static byte[] ToYcck(byte[] storedCmyk)
    {
        var data = new byte[storedCmyk.Length];
        for (int i = 0; i < storedCmyk.Length; i += 4)
        {
            // libjpeg decodes YCCK as C = 255 − R, so R = 255 − C (the stored, inverted value).
            double r = 255 - storedCmyk[i], g = 255 - storedCmyk[i + 1], b = 255 - storedCmyk[i + 2];
            double yy = 0.299 * r + 0.587 * g + 0.114 * b;
            data[i] = Byte(yy);
            data[i + 1] = Byte(128 + (b - yy) / 1.772);
            data[i + 2] = Byte(128 + (r - yy) / 1.402);
            data[i + 3] = storedCmyk[i + 3];
        }
        return data;

        static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    [Theory]
    [InlineData(64, 48)]
    [InlineData(37, 21)] // not whole blocks: the edge blocks are padded
    public void Decoder_Baseline_ReturnsTheStoredSamples(int w, int h)
    {
        var samples = Cmyk(w, h);
        var jpeg = JpegTestEncoder.Encode(samples, w, h, 4, new JpegTestEncoder.Options { AdobeTransform = 0 });
        var decoded = JpegComponentDecoder.Decode(jpeg, out int dw, out int dh, out int n);
        Assert.Equal((w, h, 4), (dw, dh, n));
        Assert.True(MeanDifference(samples, decoded) < 2.5, $"mean {MeanDifference(samples, decoded):F2}");
    }

    [Fact]
    public void Decoder_ProgressiveAndRestarts_DecodeTheSameCoefficientsAsBaseline()
    {
        var samples = Cmyk(45, 30);
        var baseline = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(samples, 45, 30, 4, new() { AdobeTransform = 0 }), out _, out _, out _);
        var progressive = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(samples, 45, 30, 4, new() { AdobeTransform = 0, Progressive = true }), out _, out _, out _);
        var restarts = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(samples, 45, 30, 4, new() { AdobeTransform = 0, RestartInterval = 3 }), out _, out _, out _);
        var both = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(samples, 45, 30, 4, new() { AdobeTransform = 0, Progressive = true, RestartInterval = 2 }), out _, out _, out _);
        Assert.Equal(baseline, progressive);
        Assert.Equal(baseline, restarts);
        Assert.Equal(baseline, both);
    }

    [Fact]
    public void Decoder_Ycck_IsConvertedToCmyk()
    {
        var stored = Cmyk(48, 32);
        var jpeg = JpegTestEncoder.Encode(ToYcck(stored), 48, 32, 4, new() { AdobeTransform = 2 });
        var decoded = JpegComponentDecoder.Decode(jpeg, out _, out _, out _);
        Assert.True(MeanDifference(stored, decoded) < 3, $"mean {MeanDifference(stored, decoded):F2}");
    }

    [Fact]
    public void Decoder_SubsampledComponents_AreEnlarged()
    {
        var stored = Cmyk(64, 64);
        var jpeg = JpegTestEncoder.Encode(ToYcck(stored), 64, 64, 4,
            new() { AdobeTransform = 2, Sampling = new[] { (2, 2), (1, 1), (1, 1), (2, 2) } });
        var decoded = JpegComponentDecoder.Decode(jpeg, out _, out _, out _);
        // Half-resolution colour: the ramps survive, the hard yellow edge softens.
        Assert.True(MeanDifference(stored, decoded) < 6, $"mean {MeanDifference(stored, decoded):F2}");
    }

    [Fact]
    public void Decoder_ThreeComponents_MatchSkiasCodec()
    {
        int w = 40, h = 24;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)(i % w * 6); rgba[i * 4 + 1] = (byte)(i / w * 10); rgba[i * 4 + 2] = (byte)(255 - i % w * 6); rgba[i * 4 + 3] = 255;
        }
        using var image = SKImage.FromPixelCopy(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), rgba, w * 4);
        // Skia's encoder writes 4:2:0 colour; smooth ramps, so replication and Skia's smoothing nearly agree.
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var mine = JpegComponentDecoder.Decode(encoded.ToArray(), out _, out _, out int n);
        Assert.Equal(3, n);
        using var bitmap = SKBitmap.Decode(encoded.ToArray());
        long sum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bitmap.GetPixel(x, y);
                int i = (y * w + x) * 3;
                sum += Math.Abs(mine[i] - c.Red) + Math.Abs(mine[i + 1] - c.Green) + Math.Abs(mine[i + 2] - c.Blue);
            }
        double mean = sum / (double)(w * h * 3);
        Assert.True(mean < 4, $"mean {mean:F2}");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(false, -1)]
    public void Decoder_AgreesWithWic(bool progressive, int transform)
    {
        var stored = Cmyk(48, 40);
        var input = transform == 2 ? ToYcck(stored) : stored;
        var jpeg = JpegTestEncoder.Encode(input, 48, 40, 4, new() { AdobeTransform = transform < 0 ? null : transform, Progressive = progressive });
        byte[]? wic = null;
        var image = new PdfDecodedImage(48, 40, PdfDecodedImageFormat.Jpeg, jpeg, MapJpegComponents: (samples, n, w, h) =>
        {
            Assert.Equal(4, n);
            wic = (byte[])samples.Clone();
            return new byte[w * h * 4];
        });
        Assert.NotNull(WicJpeg.TryDecode(image, out _, out _));
        var mine = JpegComponentDecoder.Decode(jpeg, out _, out _, out _);
        Assert.NotNull(wic);
        // WIC returns the inverse of the stored samples (InvertCmykJpeg is false here), with or without an Adobe marker.
        var inverse = mine.Select(b => (byte)(255 - b)).ToArray();
        Assert.True(MeanDifference(wic!, inverse) < 1.5, $"mean {MeanDifference(wic!, inverse):F2}");
    }

    [Fact]
    public void Decoder_RefusesArithmeticCoding()
    {
        byte[] sof9 = { 0xFF, 0xD8, 0xFF, 0xC9, 0x00, 0x14, 8, 0, 8, 0, 8, 4, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 4, 0x11, 0, 0xFF, 0xD9 };
        Assert.Throws<NotSupportedException>(() => JpegComponentDecoder.Decode(sof9, out _, out _, out _));
    }

    private static byte[] ImagePage(string imageDict, byte[] data)
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image " + imageDict, data);
        b.AddPage("0.9 0.9 0.6 rg 0 0 200 100 re f q 160 0 0 160 20 20 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        return b.Build();
    }

    [Fact]
    public Task Page_AdobeCmykJpeg_MatchesDirect2D() =>
        _parity.AssertMatches("jpeg-cmyk", ImagePage("/Width 64 /Height 48 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode",
            JpegTestEncoder.Encode(Cmyk(64, 48), 64, 48, 4, new() { AdobeTransform = 0 })), 2.0, 0.01);

    [Fact]
    public Task Page_ProgressiveCmykJpeg_MatchesDirect2D() =>
        _parity.AssertMatches("jpeg-cmyk-progressive", ImagePage("/Width 64 /Height 48 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode",
            JpegTestEncoder.Encode(Cmyk(64, 48), 64, 48, 4, new() { AdobeTransform = 0, Progressive = true, RestartInterval = 4 })), 2.0, 0.01);

    [Fact]
    public Task Page_YcckJpeg_MatchesDirect2D() =>
        _parity.AssertMatches("jpeg-ycck", ImagePage("/Width 64 /Height 48 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode",
            JpegTestEncoder.Encode(ToYcck(Cmyk(64, 48)), 64, 48, 4, new() { AdobeTransform = 2 })), 2.0, 0.01);

    [Fact]
    public Task Page_CmykJpegWithInvertedDecode_MatchesDirect2D() =>
        // /Decode [1 0 1 0 1 0 1 0] over inverted samples, without an Adobe marker: the core maps them.
        _parity.AssertMatches("jpeg-cmyk-decode", ImagePage("/Width 64 /Height 48 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode /Decode [1 0 1 0 1 0 1 0]",
            JpegTestEncoder.Encode(Cmyk(64, 48, inverted: true), 64, 48, 4)), 2.0, 0.01);

    [Fact]
    public async Task Page_CmykJpeg_IsDrawnNotClassified()
    {
        var pdf = ImagePage("/Width 64 /Height 48 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode",
            JpegTestEncoder.Encode(Cmyk(64, 48), 64, 48, 4, new() { AdobeTransform = 0 }));
        using var pair = await Parity.RenderBoth(pdf, 72);
        Assert.Empty(pair.SkiaBackendFallbacks);
        // Top-left of the image: cyan 0, magenta 0, yellow 200 → a light yellow, not black.
        var px = pair.Skia.Pixels.Span;
        int at = (30 * pair.Skia.Stride) + 25 * 4;
        Assert.True(px[at + 2] > 200 && px[at + 1] > 200 && px[at] < 150, $"BGR {px[at]} {px[at + 1]} {px[at + 2]}");
    }
}
