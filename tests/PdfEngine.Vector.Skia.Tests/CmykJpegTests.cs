using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
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
    [InlineData(false, 1)]
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

    // ------------------------------------------------------------------ hostile data

    private const string CmykImageDict = "/Width 64 /Height 48 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode";

    /// <summary>A flat CMYK JPEG: every block is DC difference 0 (category 0) and an end of block.</summary>
    private static byte[] FlatCmykJpeg() =>
        JpegTestEncoder.Encode(Enumerable.Repeat((byte)128, 64 * 48 * 4).ToArray(), 64, 48, 4, new() { AdobeTransform = 0 });

    /// <summary>The position of the first <c>FF marker</c> pair whose segment starts with <paramref name="firstByte"/> (any when negative).</summary>
    private static int MarkerAt(byte[] jpeg, int marker, int firstByte = -1)
    {
        for (int i = 2; i + 4 < jpeg.Length; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker && (firstByte < 0 || jpeg[i + 4] == firstByte))
                return i;
        throw new InvalidOperationException($"No marker {marker:X2}.");
    }

    /// <summary>The flat JPEG with one header value out of range.</summary>
    private static byte[] Hostile(string damage)
    {
        var jpeg = FlatCmykJpeg();
        int sos = MarkerAt(jpeg, 0xDA);
        switch (damage)
        {
            case "dc-selector": jpeg[sos + 6] = 0x40; break; // first component: DC table 4
            case "ac-selector": jpeg[sos + 6] = 0x04; break; // AC table 4
            case "ac-selector-15": jpeg[sos + 6] = 0x0F; break;
            case "dqt-precision": jpeg[MarkerAt(jpeg, 0xDB) + 4] = 0x20; break; // Pq 2
            case "dht-class": jpeg[MarkerAt(jpeg, 0xC4, 0x00) + 4] = 0x20; break; // Tc 2
            case "dc-category":
                // The symbol of the DC table's first code (category 0, which every block uses) becomes 12.
                jpeg[MarkerAt(jpeg, 0xC4, 0x00) + 5 + 16] = 12;
                break;
            case "ac-size":
            {
                // The end-of-block symbol (0x00) of the AC table becomes run 0, size 11.
                int values = MarkerAt(jpeg, 0xC4, 0x10) + 5 + 16;
                jpeg[Array.IndexOf(jpeg, (byte)0x00, values)] = 0x0B;
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        return jpeg;
    }

    /// <summary>Renders page 1 with the Skia backend alone; returns the regions it classified.</summary>
    private static async Task<IReadOnlyList<PdfFallbackToken>> RenderSkia(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var result = await Parity.Skia.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var page = result.Page;
        // The page around the image is drawn: the light yellow background at its bottom-left corner.
        var px = page.Pixels.Span;
        int at = (page.HeightPixels - 5) * page.Stride + 5 * 4;
        Assert.True(px[at + 2] > 200 && px[at + 1] > 200 && px[at] < 200, $"BGR {px[at]} {px[at + 1]} {px[at + 2]}");
        return result.Fallbacks.Skip(list.FallbackTokens.Count).ToList();
    }

    [Theory]
    [InlineData("dc-selector", "Huffman table that does not exist")]
    [InlineData("ac-selector", "Huffman table that does not exist")]
    [InlineData("ac-selector-15", "Huffman table that does not exist")]
    [InlineData("dqt-precision", "Bad quantization table")]
    [InlineData("dht-class", "Bad Huffman table")]
    [InlineData("dc-category", "DC difference category")]
    [InlineData("ac-size", "AC coefficient size")]
    public async Task Hostile_OutOfRangeHeaderValue_SkipsTheImageNotThePage(string damage, string reason)
    {
        var jpeg = Hostile(damage);
        var ex = Assert.Throws<InvalidDataException>(() => JpegComponentDecoder.Decode(jpeg, out _, out _, out _));
        Assert.Contains(reason, ex.Message);
        var fallbacks = await RenderSkia(ImagePage(CmykImageDict, jpeg));
        Assert.Contains(fallbacks, f => f.Reason == PdfFallbackReason.ImageDecode);
    }

    [Fact]
    public void Hostile_UndamagedFlatImage_Decodes()
    {
        // The checks refuse only values out of range: the flat image itself decodes.
        var decoded = JpegComponentDecoder.Decode(FlatCmykJpeg(), out int w, out int h, out _);
        Assert.Equal((64, 48), (w, h));
        Assert.All(decoded, b => Assert.InRange(b, (byte)126, (byte)130));
    }

    /// <summary>
    /// A 65535 × 1525 four-component frame (just under the 100-megapixel limit, 1.6 GB to decode)
    /// with a few hundred bytes of scan data.
    /// </summary>
    private static byte[] HugeFrameTinyBody()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 20, 8, 1525 >> 8, 1525 & 0xFF, 0xFF, 0xFF, 4 };
        for (int c = 1; c <= 4; c++) jpeg.AddRange(new byte[] { (byte)c, 0x11, 0 });
        jpeg.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 14, 4, 1, 0x00, 2, 0x00, 3, 0x00, 4, 0x00, 0, 63, 0 });
        jpeg.AddRange(new byte[300]);
        jpeg.AddRange(new byte[] { 0xFF, 0xD9 });
        return jpeg.ToArray();
    }

    [Fact]
    public void Hostile_HugeFrameWithTinyBody_IsRefusedBeforeAllocating()
    {
        var jpeg = HugeFrameTinyBody();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<PdfResourceLimitException>(() => JpegComponentDecoder.Decode(jpeg, out _, out _, out _));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(nameof(PdfEngine.Vector.Limits.PdfSecurityLimits.MaxImagePixels), ex.LimitName);
        Assert.True(allocated < 1024 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public async Task Hostile_HugeFrameWithTinyBody_FallsBackAsResourceLimit()
    {
        var fallbacks = await RenderSkia(ImagePage(CmykImageDict, HugeFrameTinyBody()));
        Assert.Contains(fallbacks, f => f.Reason == PdfFallbackReason.ResourceLimit);
    }

    [Fact]
    public void Limits_PixelLimitAndPageBudget_AreTheEngines()
    {
        var jpeg = JpegTestEncoder.Encode(Cmyk(64, 48), 64, 48, 4, new() { AdobeTransform = 0 });
        var small = new PdfEngine.Vector.Limits.PdfSecurityLimits { MaxImagePixels = 64 * 48 - 1 };
        var single = Assert.Throws<PdfResourceLimitException>(() => JpegComponentDecoder.Decode(jpeg, out _, out _, out _, small));
        Assert.Equal(nameof(PdfEngine.Vector.Limits.PdfSecurityLimits.MaxImagePixels), single.LimitName);
        long nearlySpent = PdfEngine.Vector.Limits.PdfSecurityLimits.Default.MaxImagePixelsPerPage - 100;
        var page = Assert.Throws<PdfResourceLimitException>(() => JpegComponentDecoder.Decode(jpeg, out _, out _, out _, null, nearlySpent));
        Assert.Equal(nameof(PdfEngine.Vector.Limits.PdfSecurityLimits.MaxImagePixelsPerPage), page.LimitName);
    }

    [Fact]
    public void Decoder_Cancelled_ThrowsOperationCanceled()
    {
        var jpeg = JpegTestEncoder.Encode(Cmyk(64, 48), 64, 48, 4, new() { AdobeTransform = 0, Progressive = true });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => JpegComponentDecoder.Decode(jpeg, out _, out _, out _, cancellationToken: cts.Token));
    }

    [Fact]
    public void Decoder_FourComponents_TransformZeroIsCmykOtherValuesAreYcck()
    {
        var stored = Cmyk(48, 32);
        var ycck = ToYcck(stored);
        var asCmyk = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(ycck, 48, 32, 4, new() { AdobeTransform = 0 }), out _, out _, out _);
        var none = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(ycck, 48, 32, 4), out _, out _, out _);
        var asYcck = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(ycck, 48, 32, 4, new() { AdobeTransform = 2 }), out _, out _, out _);
        var other = JpegComponentDecoder.Decode(JpegTestEncoder.Encode(ycck, 48, 32, 4, new() { AdobeTransform = 1 }), out _, out _, out _);
        // Transform 0 or no marker: the samples as stored (here the YCCK values themselves).
        Assert.True(MeanDifference(ycck, asCmyk) < 2.5, $"mean {MeanDifference(ycck, asCmyk):F2}");
        Assert.Equal(asCmyk, none);
        // Transform 2: converted back to the CMYK the YCCK was made from; any other value is read
        // as YCCK too, as libjpeg and WIC read it (Decoder_AgreesWithWic checks WIC for transform 1).
        Assert.True(MeanDifference(stored, asYcck) < 3, $"mean {MeanDifference(stored, asYcck):F2}");
        Assert.True(MeanDifference(asCmyk, asYcck) > 10);
        Assert.Equal(asYcck, other);
    }
}
