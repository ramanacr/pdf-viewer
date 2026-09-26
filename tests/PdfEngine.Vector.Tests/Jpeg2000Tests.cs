using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreJ2K;
using CoreJ2K.Util;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// JPXDecode images (ISO 32000-2 8.9.5.3) decoded by CoreJ2K, against PDFium (OpenJPEG) on the
/// same codestream: explicit colour space, codestream colour (no /ColorSpace), and /SMaskInData.
/// </summary>
public class Jpeg2000Tests
{
    private readonly ITestOutputHelper _output;
    public Jpeg2000Tests(ITestOutputHelper output) => _output = output;

    private static byte[] Encode(int w, int h, int components, bool jp2 = false)
    {
        var planes = new int[components][];
        for (int c = 0; c < components; c++)
        {
            planes[c] = new int[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                planes[c][y * w + x] = c switch
                {
                    0 => x * 255 / (w - 1),
                    1 => y * 255 / (h - 1),
                    2 => ((x / 8 + y / 8) & 1) * 200 + 30,
                    _ => x < w / 2 ? 255 : 90, // alpha
                };
        }
        var source = new InterleavedImageSource(w, h, components, 8, new bool[components], planes);
        var p = J2kImage.GetDefaultEncoderParameterList();
        p["lossless"] = "on";
        p["file_format"] = jp2 ? "on" : "off";
        return J2kImage.ToBytes(source, p);
    }

    private async Task AssertMatches(string name, string dictEntries, byte[] jpx)
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width 64 /Height 48 /Filter /JPXDecode {dictEntries}", jpx);
        b.AddPage("0.2 0.6 0.9 rg 0 0 200 200 re f q 180 0 0 135 10 30 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason))}");
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var v = result.Page;
        Assert.Empty(result.Fallbacks);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"{name}: mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 3.0 && bad <= 0.02, $"{name}: mean {mean:F2} bad {bad:P2}");
    }

    [Fact]
    public Task Rgb_WithExplicitColourSpace() => AssertMatches("rgb", "/ColorSpace /DeviceRGB /BitsPerComponent 8", Encode(64, 48, 3));

    [Fact]
    public Task Gray_WithCodestreamColour() => AssertMatches("gray", "", Encode(64, 48, 1));

    [Fact]
    public Task Rgba_WithSMaskInData() => AssertMatches("rgba", "/SMaskInData 1", Encode(64, 48, 4, jp2: true));

    [Fact]
    public async Task MutatedCodestreams_FailOnlyWithTypedErrors()
    {
        // 09_SECURITY: a hostile codestream may fail to decode, but only as a classified region.
        byte[] valid = Encode(32, 24, 3);
        var rng = new Random(20260926);
        using var renderer = new Direct2DVectorRenderer();
        int decoded = 0, classified = 0;
        for (int i = 0; i < 200; i++)
        {
            var bytes = (byte[])valid.Clone();
            int flips = 1 + rng.Next(8);
            for (int f = 0; f < flips; f++)
                bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
            if (rng.Next(4) == 0) Array.Resize(ref bytes, rng.Next(1, bytes.Length));
            var b = new VectorPdfBuilder();
            int image = b.AddStream("/Type /XObject /Subtype /Image /Width 32 /Height 24 /Filter /JPXDecode /ColorSpace /DeviceRGB", bytes);
            b.AddPage("q 100 0 0 100 10 10 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
            using var doc = await PdfVectorDocument.OpenAsync(b.Build());
            var list = await doc.GetPageDisplayListAsync(1);
            var tokens = await renderer.AnalyzeAsync(list); // decodes; must not throw
            if (tokens.Count == 0) decoded++;
            else
            {
                Assert.All(tokens, t => Assert.True(t.Reason is PdfFallbackReason.ImageDecode or PdfFallbackReason.ResourceLimit, t.Reason.ToString()));
                classified++;
            }
        }
        _output.WriteLine($"decoded {decoded}, classified {classified}");
    }

    [Fact]
    public void HeaderSize_IsReadBeforeDecoding()
    {
        Assert.True(PdfEngine.Vector.Images.PdfImageSource.TryReadJpxSize(Encode(64, 48, 3), out long w, out long h, out int c));
        Assert.Equal((64L, 48L, 3), (w, h, c));
        Assert.True(PdfEngine.Vector.Images.PdfImageSource.TryReadJpxSize(Encode(64, 48, 4, jp2: true), out w, out h, out c));
        Assert.Equal((64L, 48L, 4), (w, h, c));

        // A header claiming 60 000 × 60 000 is refused before the decoder allocates anything.
        var huge = Encode(8, 8, 1);
        int siz = Array.IndexOf(huge, (byte)0x51) - 1 + 2; // FF 51 then Lsiz
        huge[siz + 4] = 0; huge[siz + 5] = 0; huge[siz + 6] = 0xEA; huge[siz + 7] = 0x60;
        huge[siz + 8] = 0; huge[siz + 9] = 0; huge[siz + 10] = 0xEA; huge[siz + 11] = 0x60;
        Assert.True(PdfEngine.Vector.Images.PdfImageSource.TryReadJpxSize(huge, out w, out h, out _));
        Assert.Equal(60000L, w);
    }

    [Fact]
    public async Task CorruptCodestream_IsClassified_NotThrown()
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /Filter /JPXDecode /ColorSpace /DeviceRGB", new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 1, 2, 3 });
        b.AddPage("q 100 0 0 100 10 10 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        result.Page.Dispose();
        Assert.Contains(result.Fallbacks, t => t.Reason == PdfFallbackReason.ImageDecode);
    
    }

    [Fact]
    public async Task Jpeg_WithBytesBeforeTheSoiMarker_StillDecodes()
    {
        // A tiny baseline JPEG encoded by WIC, prefixed with a stray end-of-line (seen in real files).
        var src = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null,
            Enumerable.Range(0, 8 * 8 * 3).Select(i => (byte)(i * 5)).ToArray(), 24);
        var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 95 };
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        var jpeg = new byte[] { 0x0A }.Concat(ms.ToArray()).Concat(new byte[] { 0x0D }).ToArray();
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
        b.AddPage("q 100 0 0 100 50 50 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        result.Page.Dispose();
        Assert.Empty(result.Fallbacks);
    }
}
