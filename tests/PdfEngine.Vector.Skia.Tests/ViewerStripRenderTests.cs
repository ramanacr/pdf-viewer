using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Demo;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Drawing a page in strips (<see cref="ViewerDocument.RenderPageInStripsAsync"/>), which lets a
/// single-threaded browser host handle input between them: the same bitmap as one call, a hook
/// between strips, and a stop when the page leaves the view.
/// </summary>
public class ViewerStripRenderTests
{
    [Fact]
    public async Task Strips_MakeTheSameBitmapAsOneCall()
    {
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create());
        var whole = await doc.RenderPageAsync(1, 1.5);
        int calls = 0;
        var strips = await doc.RenderPageInStripsAsync(1, 1.5, night: false, () => { calls++; return Task.FromResult(true); }, stripPixels: 64 * 1024);
        Assert.NotNull(strips);
        Assert.Equal((whole.Width, whole.Height), (strips!.Width, strips.Height));
        int expectedStrips = (int)Math.Ceiling(whole.Height / (double)Math.Max(8, 64 * 1024 / whole.Width));
        Assert.Equal(expectedStrips, calls);
        Assert.True(calls > 4);

        // Anti-aliased edges may differ by a step where a strip ends; nothing else does.
        var a = whole.Pixels.AsSpan();
        var b = strips.Pixels.AsSpan();
        Assert.Equal(a.Length, b.Length);
        int differing = 0;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 2) differing++;
        Assert.True(differing < a.Length / 1000, $"{differing} of {a.Length} bytes differ");
    }

    [Fact]
    public async Task Strips_StopWhenThePageLeavesTheView()
    {
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create());
        int calls = 0;
        var stopped = await doc.RenderPageInStripsAsync(2, 1.5, night: false, () => Task.FromResult(++calls < 3), stripPixels: 64 * 1024);
        Assert.Null(stopped);
        Assert.Equal(3, calls);
        // The display list stays cached: the next draw does not interpret the page again.
        var again = await doc.RenderPageInStripsAsync(2, 1.5, night: false, () => Task.FromResult(true), stripPixels: 64 * 1024);
        Assert.True(again!.BuildTime < TimeSpan.FromMilliseconds(5), $"build {again.BuildTime.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task SmallBitmap_IsOneCall_AndBitmapCapStillApplies()
    {
        await using var doc = await ViewerDocument.OpenAsync(DemoDocument.Create(), options: new ViewerOptions { MaxBitmapPixels = 500_000 });
        int calls = 0;
        var small = await doc.RenderPageInStripsAsync(1, 0.2, night: false, () => { calls++; return Task.FromResult(true); });
        Assert.Equal(0, calls);
        Assert.NotNull(small);
        var capped = await doc.RenderPageInStripsAsync(1, 6, night: false, () => Task.FromResult(true), stripPixels: 64 * 1024);
        Assert.True((long)capped!.Width * capped.Height <= 500_000);
        Assert.True(capped.Scale < 6);
    }
}
