using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The Skia renderer must refuse a render whose size asks for an unbounded bitmap with a typed
/// error, like the Direct2D and WPF backends, instead of an OverflowException from the buffer
/// size or an OutOfMemoryException from the allocation. The PDFs are built in code.
/// </summary>
public class SkiaRenderSizeLimitTests
{
    private static async Task<(PdfVectorDocument Doc, IPdfDisplayList List)> OpenAsync(string mediaBox = "[0 0 200 200]", string extra = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 100 100 re f", mediaBox: mediaBox, extra: extra);
        var doc = await PdfVectorDocument.OpenAsync(b.Build());
        return (doc, await doc.GetPageDisplayListAsync(1));
    }

    private static async Task<PdfResourceLimitException> AssertRefusedAsync(IPdfDisplayList list, RenderRequest request)
    {
        using var renderer = new SkiaVectorRenderer();
        var ex = await Assert.ThrowsAsync<PdfResourceLimitException>(
            () => renderer.RenderDisplayListAsync(list, request).AsTask());
        Assert.Equal(PdfVectorErrorKind.ResourceLimit, ex.Kind);
        Assert.Equal(nameof(PdfSecurityLimits.MaxRenderPixelCount), ex.LimitName);
        Assert.True(ViewerRenderFailure.IsTooLarge(ex));
        return ex;
    }

    [Theory]
    // Fuzz seed 26500: a digit duplication turned the MediaBox height into 200001073 points.
    [InlineData("[0 0 200 200001073]", "")]
    // Both sides huge, turned sideways by /Rotate.
    [InlineData("[0 0 99999999 99999999]", "/Rotate 90")]
    public async Task HugeMediaBox_IsRefusedWithATypedResourceLimit(string mediaBox, string extra)
    {
        var (doc, list) = await OpenAsync(mediaBox, extra);
        using (doc)
            await AssertRefusedAsync(list, new RenderRequest { PageNumber = 1, Dpi = 36 });
    }

    [Theory]
    [InlineData(1_000_000.0)]
    // Infinite pixel sizes saturate, then fail the limit (not an integer conversion).
    [InlineData(double.MaxValue)]
    public async Task HugeResolution_IsRefusedWithATypedResourceLimit(double dpi)
    {
        var (doc, list) = await OpenAsync();
        using (doc)
            await AssertRefusedAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi });
    }

    [Fact]
    public async Task RequestedPixelSizeBeyondTheLimit_IsRefusedWithATypedResourceLimit()
    {
        // 23171 x 23171 is about 537M pixels: at 4 bytes a pixel past int.MaxValue.
        var (doc, list) = await OpenAsync();
        using (doc)
        {
            var ex = await AssertRefusedAsync(list, new RenderRequest { PageNumber = 1, TargetWidthPixels = 23171, TargetHeightPixels = 23171 });
            Assert.Contains("23171 x 23171", ex.Message);
        }
    }

    [Fact]
    public async Task HugePage_RendersARegionUnderTheLimit()
    {
        var (doc, list) = await OpenAsync("[0 0 200 200001073]");
        using (doc)
        {
            using var renderer = new SkiaVectorRenderer();
            var request = new RenderRequest { PageNumber = 1, Dpi = 72 };
            var (w, h) = SkiaVectorRenderer.OutputSize(list, request);
            Assert.Equal(200, w);
            Assert.Equal(200001073, h);
            // The bottom of the page holds the blue square.
            var region = new SkiaPixelRegion(0, h - 200, 200, 200);
            using var page = (await renderer.RenderAsync(list, request, null, region, false, CancellationToken.None)).Page;
            Assert.Equal(200, page.WidthPixels);
            Assert.Equal(200, page.HeightPixels);
            var px = page.Pixels.Span;
            int bottomLeft = (150 * page.Stride) + (50 * 4);
            Assert.Equal(255, px[bottomLeft]);     // B
            Assert.Equal(0, px[bottomLeft + 2]);   // R
        }
    }

    [Fact]
    public async Task NormalPage_StillRenders()
    {
        var (doc, list) = await OpenAsync();
        using (doc)
        {
            using var renderer = new SkiaVectorRenderer();
            using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 144 });
            Assert.Equal(400, page.WidthPixels);
            Assert.Equal(400, page.HeightPixels);
        }
    }

    [Fact]
    public void Viewer_ShowsTooLargeInsteadOfTheExceptionType()
    {
        var tooLarge = new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxRenderPixelCount), "too large");
        Assert.Equal("Page 3 is too large to show.", ViewerRenderFailure.Describe(3, tooLarge));

        var other = new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxImagePixels), "image");
        Assert.Equal("Page 3 could not be drawn (PdfResourceLimitException).", ViewerRenderFailure.Describe(3, other));
        Assert.Equal("Page 3 could not be drawn (InvalidOperationException).", ViewerRenderFailure.Describe(3, new InvalidOperationException()));
    }
}
