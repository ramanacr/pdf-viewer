using PdfEngine.Vector.Tests.Fixtures;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Milestone 1, images: samples the engine decodes (RGB, gray, indexed, stencil masks, soft masks,
/// /Decode arrays) and JPEG data each backend decodes with its own codec (Skia's against WIC),
/// enlarged with and without interpolation and reduced, against the Direct2D backend.
/// </summary>
public class ImageParityTests
{
    private readonly Parity _parity;
    public ImageParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    /// <summary>A w × h RGB gradient with a checker, so resampling differences show.</summary>
    private static byte[] Rgb(int w, int h)
    {
        var data = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                bool check = ((x / Math.Max(1, w / 8)) + (y / Math.Max(1, h / 8))) % 2 == 0;
                data[i] = (byte)(255 * x / Math.Max(1, w - 1));
                data[i + 1] = (byte)(255 * y / Math.Max(1, h - 1));
                data[i + 2] = (byte)(check ? 220 : 40);
            }
        return data;
    }

    private static byte[] Gray(int w, int h, Func<int, int, byte> f)
    {
        var data = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) data[y * w + x] = f(x, y);
        return data;
    }

    /// <summary>JPEG data encoded by Skia (WIC decodes it in the Direct2D backend).</summary>
    private static byte[] Jpeg(byte[] samples, int w, int h, bool gray)
    {
        var info = new SKImageInfo(w, h, gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Opaque);
        byte[] pixels = samples;
        if (!gray)
        {
            pixels = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                pixels[i * 4] = samples[i * 3]; pixels[i * 4 + 1] = samples[i * 3 + 1]; pixels[i * 4 + 2] = samples[i * 3 + 2]; pixels[i * 4 + 3] = 255;
            }
        }
        using var image = SKImage.FromPixelCopy(info, pixels, info.RowBytes);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return encoded.ToArray();
    }

    private static byte[] ImagePage(string imageDict, byte[] data, string placement = "160 0 0 160 20 20", string extraResources = "", string before = "")
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image " + imageDict, data);
        b.AddPage($"0.9 0.9 0.6 rg 0 0 200 100 re f {before} q {placement} cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> {extraResources} >>");
        return b.Build();
    }

    [Fact]
    public Task Rgb_EnlargedWithoutInterpolation() =>
        _parity.AssertMatches("rgb-8x8", ImagePage("/Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8", Rgb(8, 8)), 1.0, 0.005);

    [Fact]
    public Task Rgb_EnlargedWithInterpolation() =>
        _parity.AssertMatches("rgb-interpolate", ImagePage("/Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Interpolate true", Rgb(8, 8)), 2.0, 0.01);

    [Fact]
    public Task Rgb_Reduced() =>
        _parity.AssertMatches("rgb-reduced", ImagePage("/Width 400 /Height 300 /ColorSpace /DeviceRGB /BitsPerComponent 8", Rgb(400, 300), "120 0 0 90 40 50"), 3.0, 0.02);

    // Enlarged 7× and turned 45° without interpolation: the two rasterizers pick a different
    // sample for some pixels along each cell edge of this high-contrast checker (2 % of the page).
    [Fact]
    public Task Rgb_Rotated() =>
        _parity.AssertMatches("rgb-rotated", ImagePage("/Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8", Rgb(16, 16), "80 80 -80 80 100 20"), 2.0, 0.03);

    [Fact]
    public Task InlineImage() =>
        _parity.AssertMatches("inline", new Func<byte[]>(() =>
        {
            var b = new VectorPdfBuilder();
            b.AddPage("q 160 0 0 160 20 20 cm BI /W 2 /H 2 /CS /RGB /BPC 8 ID ÿ\u0000\u0000\u0000ÿ\u0000\u0000\u0000ÿÿÿ\u0000 EI Q");
            return b.Build();
        })(), 1.0, 0.005);

    [Fact]
    public Task Gray_WithDecodeArray() =>
        _parity.AssertMatches("gray-decode", ImagePage("/Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0]",
            Gray(16, 16, (x, y) => (byte)(x * 16 + y))), 1.0, 0.005);

    [Fact]
    public Task Indexed_FourBit() =>
        _parity.AssertMatches("indexed-4bit", ImagePage("/Width 4 /Height 2 /ColorSpace [/Indexed /DeviceRGB 3 <FF000000FF000000FFFFFF00>] /BitsPerComponent 4",
            new byte[] { 0x01, 0x23, 0x32, 0x10 }), 1.0, 0.005);

    [Fact]
    public Task StencilMask_PaintsTheFillColour() =>
        _parity.AssertMatches("stencil", ImagePage("/Width 8 /Height 8 /ImageMask true /BitsPerComponent 1",
            new byte[] { 0xF0, 0xF0, 0xF0, 0xF0, 0x0F, 0x0F, 0x0F, 0x0F }, before: "0.8 0.1 0.4 rg"), 1.0, 0.005);

    [Fact]
    public Task StencilMask_InvertedDecode() =>
        _parity.AssertMatches("stencil-decode", ImagePage("/Width 8 /Height 8 /ImageMask true /BitsPerComponent 1 /Decode [1 0]",
            new byte[] { 0xF0, 0xF0, 0xF0, 0xF0, 0x0F, 0x0F, 0x0F, 0x0F }, before: "0.1 0.3 0.8 rg"), 1.0, 0.005);

    [Fact]
    public Task SoftMask_FromGraySamples()
    {
        var b = new VectorPdfBuilder();
        int smask = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8",
            Gray(16, 16, (x, y) => (byte)(x * 17)));
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {smask} 0 R", Rgb(16, 16));
        b.AddPage("0 0 1 rg 0 0 100 200 re f q 160 0 0 160 20 20 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        return _parity.AssertMatches("smask", b.Build(), 1.5, 0.01);
    }

    [Fact]
    public Task ColourKeyMask()
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask [0 100]",
            Gray(8, 8, (x, y) => (byte)((x + y) % 2 == 0 ? 50 : 200)));
        b.AddPage("1 0 0 rg 0 0 200 200 re f q 160 0 0 160 20 20 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        return _parity.AssertMatches("colour-key", b.Build(), 1.0, 0.005);
    }

    [Fact]
    public Task ImageWithConstantAlpha() =>
        _parity.AssertMatches("image-ca", ImagePage("/Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8", Rgb(8, 8),
            extraResources: "/ExtGState << /G << /ca 0.5 >> >>", before: "/G gs"), 1.0, 0.005);

    [Fact]
    public Task Jpeg_Rgb() =>
        _parity.AssertMatches("jpeg-rgb", ImagePage("/Width 64 /Height 48 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode",
            Jpeg(Rgb(64, 48), 64, 48, gray: false)), 2.0, 0.01);

    [Fact]
    public Task Jpeg_Gray() =>
        _parity.AssertMatches("jpeg-gray", ImagePage("/Width 64 /Height 64 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode",
            Jpeg(Gray(64, 64, (x, y) => (byte)(x * 4)), 64, 64, gray: true)), 2.0, 0.01);

    [Fact]
    public Task Jpeg_GrayWithDecodeArray_IsMappedByTheCore() =>
        // /Decode [1 0] is not a default JPEG: its raw samples go through the core's colour mapping.
        _parity.AssertMatches("jpeg-gray-decode", ImagePage("/Width 64 /Height 64 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode /Decode [1 0]",
            Jpeg(Gray(64, 64, (x, y) => (byte)(y * 4)), 64, 64, gray: true)), 2.0, 0.01);

    [Fact]
    public Task Jpeg_SeparationSpace_IsMappedByTheCore() =>
        _parity.AssertMatches("jpeg-separation", ImagePage("/Width 64 /Height 64 /ColorSpace [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0.9 0.2 0.1] /N 1 >>] /BitsPerComponent 8 /Filter /DCTDecode",
            Jpeg(Gray(64, 64, (x, y) => (byte)((x + y) * 2)), 64, 64, gray: true)), 2.0, 0.01);

    [Fact]
    public Task Jpeg_SoftMask_IsAppliedByTheBackend()
    {
        var b = new VectorPdfBuilder();
        int smask = b.AddStream("/Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode",
            Jpeg(Gray(32, 32, (x, y) => (byte)(x < 16 ? 255 : 0)), 32, 32, gray: true));
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask {smask} 0 R", Rgb(8, 8));
        b.AddPage("0 0 1 rg 0 0 200 200 re f q 160 0 0 160 20 20 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        return _parity.AssertMatches("jpeg-smask", b.Build(), 2.0, 0.01);
    }

    [Fact]
    public async Task UndecodableJpeg_IsReportedForFallback()
    {
        var pdf = ImagePage("/Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6 });
        using var doc = await PdfEngine.Vector.Document.PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        var tokens = await Parity.Skia.AnalyzeAsync(list);
        var token = Assert.Single(tokens.Skip(list.FallbackTokens.Count));
        Assert.Equal(PdfFallbackReason.ImageDecode, token.Reason);
        Assert.Equal("skia", token.Metadata!["origin"]);
    }
}
