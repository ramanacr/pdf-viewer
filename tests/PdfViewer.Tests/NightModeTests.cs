using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers night mode - inverting the pages themselves rather than only the window around
/// them. The inversion runs on every rendered page and thumbnail, so it has to be correct on
/// the pixels and safe on the awkward inputs.
/// </summary>
public class NightModeTests : IDisposable
{
    private readonly string _testDir;

    public NightModeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfNightTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private static BitmapSource SolidBgra32(byte b, byte g, byte r, byte a = 255, int size = 4)
    {
        int stride = size * 4;
        byte[] pixels = new byte[stride * size];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    [Fact]
    public void TestWhitePagesBecomeBlack()
    {
        var inverted = NightModeImage.Invert(SolidBgra32(255, 255, 255));
        var pixels = ReadPixels(inverted);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(0, pixels[i]);
            Assert.Equal(0, pixels[i + 1]);
            Assert.Equal(0, pixels[i + 2]);
        }
    }

    [Fact]
    public void TestBlackTextBecomesWhite()
    {
        var inverted = NightModeImage.Invert(SolidBgra32(0, 0, 0));
        var pixels = ReadPixels(inverted);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i]);
            Assert.Equal(255, pixels[i + 1]);
            Assert.Equal(255, pixels[i + 2]);
        }
    }

    /// <summary>
    /// Alpha is not a colour. Inverting it would turn transparent regions into opaque black,
    /// which is exactly where a page would sprout unexpected rectangles.
    /// </summary>
    [Fact]
    public void TestTransparencyIsLeftAlone()
    {
        var inverted = NightModeImage.Invert(SolidBgra32(10, 20, 30, a: 64));
        var pixels = ReadPixels(inverted);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(245, pixels[i]);
            Assert.Equal(235, pixels[i + 1]);
            Assert.Equal(225, pixels[i + 2]);
            Assert.Equal(64, pixels[i + 3]);
        }
    }

    [Fact]
    public void TestInvertingTwiceGivesBackTheOriginal()
    {
        var original = SolidBgra32(12, 200, 77, a: 200);
        var roundTripped = NightModeImage.Invert(NightModeImage.Invert(original));

        Assert.Equal(ReadPixels(original), ReadPixels(roundTripped));
    }

    [Fact]
    public void TestTheResultIsFrozenSoItCanCrossThreads()
    {
        var inverted = NightModeImage.Invert(SolidBgra32(255, 255, 255));

        // Page rendering happens off the UI thread; an unfrozen bitmap would throw when bound.
        Assert.True(inverted.IsFrozen);
    }

    /// <summary>
    /// The renderer does not promise a pixel format, so a page arriving as anything else must
    /// still invert rather than throw into the render path.
    /// </summary>
    [Fact]
    public void TestOtherPixelFormatsAreHandled()
    {
        int stride = 4 * 3;
        byte[] rgb = new byte[stride * 4];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = 255;

        var source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Rgb24, null, rgb, stride);
        source.Freeze();

        var inverted = NightModeImage.Invert(source);
        var pixels = ReadPixels(inverted);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(0, pixels[i]);
            Assert.Equal(0, pixels[i + 1]);
            Assert.Equal(0, pixels[i + 2]);
        }
    }

    [Fact]
    public void TestInvertRejectsNothingSilently()
    {
        Assert.Throws<ArgumentNullException>(() => NightModeImage.Invert(null!));
    }

    [Fact]
    public async Task TestNightModeIsSeparateFromTheWindowTheme()
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, "night.pdf"), 2, "NightToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);

        Assert.False(vm.IsNightMode);

        vm.ToggleNightMode();
        Assert.True(vm.IsNightMode);
        Assert.Contains("Night mode on", vm.StatusText);

        vm.ToggleNightMode();
        Assert.False(vm.IsNightMode);
        Assert.Contains("Night mode off", vm.StatusText);
    }

    /// <summary>Mean brightness of a rendered page, 0 (black) to 255 (white).</summary>
    private static double MeanLuminance(BitmapSource source)
    {
        var pixels = ReadPixels(source);
        double total = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            total += (0.114 * pixels[i]) + (0.587 * pixels[i + 1]) + (0.299 * pixels[i + 2]);
        }

        return total / (pixels.Length / 4);
    }

    private static async Task<BitmapSource> WaitForRender(MainViewModel vm)
    {
        for (int i = 0; i < 100 && vm.Pages[0].RenderedImage == null; i++)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(vm.Pages[0].RenderedImage);
        return vm.Pages[0].RenderedImage!;
    }

    /// <summary>
    /// The end-to-end claim: after toggling, the page a user is actually looking at is dark.
    /// Toggling also has to discard what is already drawn, or pages already on screen stay in
    /// the old mode until something else happens to evict them.
    /// </summary>
    [Fact]
    public async Task TestTogglingActuallyDarkensTheRenderedPage()
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, "reload.pdf"), 2, "NightToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        await vm.RenderVisiblePagesAsync();

        double daylight = MeanLuminance(await WaitForRender(vm));
        Assert.True(daylight > 200, $"A mostly blank page should render bright; got {daylight:F0}.");

        vm.ToggleNightMode();

        double night = MeanLuminance(await WaitForRender(vm));
        Assert.True(night < 55, $"Night mode should render the page dark; got {night:F0}.");

        // And back again, so the toggle is not one-way.
        vm.ToggleNightMode();

        double backToDaylight = MeanLuminance(await WaitForRender(vm));
        Assert.True(backToDaylight > 200, $"Turning night mode off should restore the page; got {backToDaylight:F0}.");
    }
}
