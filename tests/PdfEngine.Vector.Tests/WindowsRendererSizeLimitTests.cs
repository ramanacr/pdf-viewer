using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The WPF renderer must refuse a page whose size asks for an unbounded bitmap with a typed
/// error, like the Direct2D backend, instead of an OutOfMemoryException or OverflowException
/// from RenderTargetBitmap (nightly fuzz seed 26500).
/// </summary>
public class WindowsRendererSizeLimitTests
{
    [Theory]
    // Fuzz seed 26500: a digit duplication turned the MediaBox height into 200001073 points.
    [InlineData("[0 0 200 200001073]", "")]
    // Both sides huge, turned sideways by /Rotate: the pixel product leaves long-safe int range.
    [InlineData("[0 0 99999999 99999999]", "/Rotate 90")]
    public async Task HugePage_IsRefusedWithATypedResourceLimit(string mediaBox, string extra)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 100 100 re f", mediaBox: mediaBox, extra: extra);
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);

        using var renderer = new WindowsVectorRenderer();
        var ex = await Assert.ThrowsAsync<PdfResourceLimitException>(
            () => renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 36 }).AsTask());

        Assert.Equal(PdfVectorErrorKind.ResourceLimit, ex.Kind);
        Assert.Equal(nameof(WindowsVectorRenderer.MaxRenderPixelCount), ex.LimitName);
    }

    [Fact]
    public async Task RequestedPixelSizeBeyondTheLimit_IsRefusedWithATypedResourceLimit()
    {
        // 23171 x 23171 is about 537M pixels: at 4 bytes a pixel past int.MaxValue.
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 100 100 re f");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);

        using var renderer = new WindowsVectorRenderer();
        var ex = await Assert.ThrowsAsync<PdfResourceLimitException>(
            () => renderer.RenderDisplayListAsync(list,
                new RenderRequest { PageNumber = 1, TargetWidthPixels = 23171, TargetHeightPixels = 23171 }).AsTask());

        Assert.Equal(nameof(WindowsVectorRenderer.MaxRenderPixelCount), ex.LimitName);
    }

    [Fact]
    public async Task NormalPage_StillRenders()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 100 100 re f");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);

        using var page = await InterpreterCoverageTests.RenderAsync(list, 72);
        Assert.Equal(200, page.WidthPixels);
        Assert.Equal(200, page.HeightPixels);
    }
}
