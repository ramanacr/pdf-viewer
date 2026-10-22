using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Images;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// A damaged JPEG (valid markers, no frame or scan: the showcase's strip) never crashes the
/// renderer: it is a typed ImageDecode region, the rest of the page draws, and the codec's native
/// error path ("JPEG datastream contains no image", printed by libjpeg inside WIC) is not entered
/// again on every render.
/// </summary>
public class DamagedJpegRenderTests : IDisposable
{
    private static readonly byte[] Damaged = { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x10, 0x0B, 0x0C, 0x0E, 0xFF, 0xD9 };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DamagedJpegRenderTests_" + Guid.NewGuid().ToString("N"));

    public DamagedJpegRenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task DamagedJpeg_IsATypedImageDecodeFailure_BeforeAnyCodec()
    {
        string path = TestPdfBuilder.CreateEngineShowcasePdf(Path.Combine(_dir, "showcase.pdf"));
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var list = await doc.GetPageDisplayListAsync(2);
        var image = list.Commands.OfType<DrawImage>().Single(d => d.Image.Source != null).Image.Source!;

        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() => image.Decode());
        Assert.Equal(PdfFallbackReason.ImageDecode, ex.Reason);
    }

    [Fact]
    public async Task ValidJpeg_StillReachesTheCodec()
    {
        byte[] jpeg = EncodeJpeg(16, 8);
        Assert.True(PdfImageSource.HasFrameAndScan(jpeg));
        // Stray bytes between segments are skipped, as libjpeg skips them.
        var padded = jpeg.Take(2).Concat(new byte[] { 0x00, 0x0A }).Concat(jpeg.Skip(2)).ToArray();
        Assert.True(PdfImageSource.HasFrameAndScan(padded));
        Assert.False(PdfImageSource.HasFrameAndScan(Damaged));
        Assert.False(PdfImageSource.HasFrameAndScan(jpeg.AsSpan(0, 40)));

        var b = new VectorPdfBuilder();
        int im = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 8 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
        b.AddPage("q 100 0 0 50 10 10 cm /Im1 Do Q", $"<< /XObject << /Im1 {im} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        var decoded = list.Commands.OfType<DrawImage>().Single().Image.Source!.Decode();
        Assert.Equal(PdfDecodedImageFormat.Jpeg, decoded.Format);

        using var renderer = new WindowsVectorRenderer();
        Assert.Empty(await renderer.AnalyzeAsync(list));
    }

    [Fact]
    public void ImageThatFailsToDecode_IsDecodedOnce_NotOnEveryRender()
    {
        var source = new CountingSource(new PdfDecodedImage(64, 8, PdfDecodedImageFormat.Jpeg, Damaged));
        var cache = new WpfImageCache(1 << 20);
        Exception? unexpected = null;
        var sta = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    var ex = Record.Exception(() => cache.GetOrDecode(source, CancellationToken.None, invert: i % 2 == 1));
                    Assert.NotNull(ex);
                    Assert.True(WpfImageCache.IsDecodeFailure(ex), ex.GetType().FullName);
                }
            }
            catch (Exception ex)
            {
                unexpected = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        Assert.Null(unexpected);
        Assert.Equal(1, source.Decodes);
    }

    [Fact]
    public async Task DamagedJpegPage_RenderedManyTimesOnTheStaThread_DrawsTheRestOfThePage()
    {
        string path = TestPdfBuilder.CreateEngineShowcasePdf(Path.Combine(_dir, "showcase.pdf"));
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var list = await doc.GetPageDisplayListAsync(2);

        var renderers = Enumerable.Range(0, 2).Select(_ => new WindowsVectorRenderer()).ToArray();
        try
        {
            var work = renderers.Select(r => Task.Run(async () =>
            {
                for (int i = 0; i < 25; i++)
                {
                    var tokens = await r.AnalyzeAsync(list);
                    Assert.Contains(tokens, t => t.Reason == PdfFallbackReason.ImageDecode);
                    var surface = await r.BuildPageSurfaceAsync(list, PageRotation.Rotate0, null, 72, CancellationToken.None);
                    Assert.Contains(surface.Fallbacks, t => t.Reason == PdfFallbackReason.ImageDecode);
                    var result = await r.RenderAsync(list, new RenderRequest { PageNumber = 2, Dpi = 36 }, null, CancellationToken.None);
                    using var page = result.Page;
                    Assert.Contains(result.Fallbacks, t => t.Reason == PdfFallbackReason.ImageDecode);
                    // The coloured boxes and text around the strip are drawn.
                    var pixels = page.Pixels.Span;
                    int coloured = 0;
                    for (int p = 0; p + 3 < pixels.Length; p += 4)
                        if (pixels[p] < 200 || pixels[p + 1] < 200 || pixels[p + 2] < 200) coloured++;
                    Assert.True(coloured > pixels.Length / 4 / 20, $"only {coloured} drawn pixels");
                }
            })).ToArray();
            await Task.WhenAll(work);
        }
        finally
        {
            foreach (var r in renderers) r.Dispose();
        }
    }

    private static byte[] EncodeJpeg(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private sealed class CountingSource(PdfDecodedImage image) : IPdfImageSource
    {
        private int _decodes;
        public int Decodes => _decodes;
        public string CacheKey => "damaged-jpeg-test";
        public PdfDecodedImage Decode(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decodes);
            return image;
        }
    }
}
