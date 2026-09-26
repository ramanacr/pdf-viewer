using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector;
using PdfViewer.Core.Security;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The default page host: Direct2D renders the page bitmap, and when the page is zoomed past that
/// bitmap's resolution, a viewport detail tile at exact device resolution is laid over it.
/// </summary>
public class Direct2DPageHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Direct2DPageHostTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string SquarePdf()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "square.pdf");
        string content = "0 g 50 50 100 100 re f";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        using var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.ASCII.GetBytes(t));
        W("%PDF-1.7\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++) { offsets[i] = ms.Position; W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static byte[] Pixels(BitmapSource b)
    {
        BitmapSource bgra = b.Format == PixelFormats.Bgra32 || b.Format == PixelFormats.Pbgra32 ? b : new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var p = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(p, bgra.PixelWidth * 4, 0);
        return p;
    }

    [Fact]
    public async Task DefaultHost_IsDirect2DBitmaps()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        Assert.Equal(HybridVectorDocumentService.VectorHostMode.Direct2DTiles, service.HostMode);
        Assert.True(service.IsDirect2DActive);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 144, 0);
        Assert.Null(page.VectorSurface);
        var bmp = Assert.IsAssignableFrom<BitmapSource>(page.PageSurface);
        Assert.Equal(400, bmp.PixelWidth);
    }

    [Fact]
    public async Task ZoomedPage_GetsASharpDetailTileForTheVisibleArea()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 150, 0);
        page.UpdateScale(16); // 1600 %: the page is 3200 × 3200 DIPs, its bitmap only ~417 px

        // The viewport shows the square's left edge region.
        var visible = new Rect(700, 1400, 400, 300);
        await page.UpdateDetailAsync(service, visible, devicePixelsPerDip: 1.0, rotation: 0, nightMode: false);
        var tile = Assert.IsAssignableFrom<BitmapSource>(page.DetailImage);
        Assert.True(page.DetailRect.Contains(visible), "the tile covers the visible area");
        Assert.True(tile.PixelWidth <= PageViewModel.MaxDetailPixels && tile.PixelHeight <= PageViewModel.MaxDetailPixels);
        Assert.Equal(page.DetailRect.Width, tile.PixelWidth, 0);

        // The square's left edge is at x = 50 pt → 800 DIPs; it must be a hard edge in the tile.
        var px = Pixels(tile);
        int row = (int)(1550 - page.DetailRect.Y);
        int edge = (int)(800 - page.DetailRect.X);
        int soft = 0;
        for (int x = edge - 6; x <= edge + 6; x++)
        {
            byte v = px[(row * tile.PixelWidth + x) * 4];
            if (v > 20 && v < 235) soft++;
        }
        Assert.True(soft <= 2, $"edge spans {soft} intermediate pixels at 1600 %");

        // A small scroll inside the tile's margin reuses it.
        var same = page.DetailImage;
        await page.UpdateDetailAsync(service, new Rect(720, 1420, 400, 300), 1.0, 0, false);
        Assert.Same(same, page.DetailImage);

        // A zoom change drops the stale tile.
        page.UpdateScale(8);
        Assert.Null(page.DetailImage);
    }

    [Fact]
    public async Task UnzoomedPage_NeedsNoDetailTile()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 150, 0);
        page.UpdateScale(1.0);
        await page.UpdateDetailAsync(service, new Rect(0, 0, 200, 200), 1.0, 0, false);
        Assert.Null(page.DetailImage);
    }

    [Theory]
    [InlineData(PdfEngineMode.Auto)]
    [InlineData(PdfEngineMode.Pdfium)]
    public async Task NightModeDetailTile_IsInverted(PdfEngineMode mode)
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, mode);
        await service.OpenDocumentAsync(SquarePdf());
        var page = new PageViewModel(1, 200, 200);
        page.UpdateScale(8);
        await page.UpdateDetailAsync(service, new Rect(0, 0, 300, 300), 1.0, 0, nightMode: true);
        var tile = Assert.IsAssignableFrom<BitmapSource>(page.DetailImage);
        var px = Pixels(tile);
        Assert.True(px[0] < 30, "outside the square (white page) is dark in night mode");
        // (52.5 pt, 52.5 pt) = 420 DIPs from the top-left is inside the square (400..1200 DIPs at 800 %).
        int inside = ((int)(420 - page.DetailRect.Y) * tile.PixelWidth + (int)(420 - page.DetailRect.X)) * 4;
        Assert.True(px[inside] > 225, "the black square is light in night mode");
    }
    /// <summary>Forwards every call to the real service and records detail-region requests.</summary>
    public class RegionRecorder : DispatchProxy
    {
        public IPdfDocumentService Inner { get; set; } = null!;
        public List<Int32Rect> Regions { get; } = new();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdfDocumentService.RenderPageRegionAsync))
                Regions.Add(new Int32Rect((int)args![3]!, (int)args[4]!, (int)args[5]!, (int)args[6]!));
            return method.Invoke(Inner, args);
        }
    }

    [Fact]
    public async Task DetailTile_ShowsTheVisibleAreaFirst_ThenTheMarginTile()
    {
        using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
        await service.OpenDocumentAsync(SquarePdf());
        var proxy = DispatchProxy.Create<IPdfDocumentService, RegionRecorder>();
        var recorder = (RegionRecorder)(object)proxy;
        recorder.Inner = service;

        var page = new PageViewModel(1, 200, 200);
        await page.LoadImageAsync(new AsyncPageRenderer(service, new LruPageCache(8)), 150, 0);
        page.UpdateScale(16);
        var visible = new Rect(700, 1400, 400, 300);
        await page.UpdateDetailAsync(proxy, visible, 1.0, 0, false);

        Assert.Equal(2, recorder.Regions.Count);
        Assert.Equal(new Int32Rect(700, 1400, 400, 300), recorder.Regions[0]);          // exactly what is on screen
        Assert.True(recorder.Regions[1].Width * recorder.Regions[1].Height > 3 * 400 * 300); // then the margin
        Assert.True(page.DetailRect.Contains(new Rect(500, 1250, 800, 600)), "the final tile carries the margin");

        // Scrolling within the margin needs no render.
        await page.UpdateDetailAsync(proxy, new Rect(760, 1440, 400, 300), 1.0, 0, false);
        Assert.Equal(2, recorder.Regions.Count);
    }
    /// <summary>
    /// With a GPU presenter (the window sets one on a hardware tier), a zoomed page's detail tile
    /// is a D3DImage over a shared texture - no bitmap - and replacing or clearing it releases the
    /// previous surface. Runs in a real window on an STA thread.
    /// </summary>
    [Fact]
    public void DetailTile_UsesTheGpuPresenter_AndReleasesReplacedSurfaces()
    {
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // Continuations must come back to this thread, as they do in the viewer.
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
                var window = new Window { Width = 200, Height = 200, Left = -10000, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                using var presenter = GpuTilePresenter.TryCreate(window);
                if (presenter == null) { window.Close(); return; } // software tier or remote session: bitmap path only

                int presented = 0, released = 0;
                PageViewModel.GpuPresenter = (texture, lost) =>
                {
                    var shown = presenter.Present(texture, lost);
                    if (shown is not { } s) return null;
                    presented++;
                    return (s.Image, new Tracking(s.Lifetime, () => released++));
                };
                try
                {
                    using var service = new HybridVectorDocumentService(PdfSecurityPolicy.DefaultStrict, PdfEngineMode.Auto);
                    Pump(service.OpenDocumentAsync(SquarePdf()));
                    var page = new PageViewModel(1, 200, 200);
                    page.UpdateScale(16);
                    Pump(page.UpdateDetailAsync(service, new Rect(700, 1400, 400, 300), 1.0, 0, false));
                    Assert.IsType<System.Windows.Interop.D3DImage>(page.DetailImage);
                    Assert.Equal(2, presented);  // visible area, then the margin tile
                    Assert.Equal(1, released);   // the visible-area surface, once replaced
                    page.ClearDetail();
                    Assert.Equal(2, released);
                    Assert.Null(page.DetailImage);
                }
                finally
                {
                    PageViewModel.GpuPresenter = null;
                    window.Close();
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true; // a hung GPU or dispatcher must not keep the test host alive
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the GPU presenter test did not finish within 60 s");
        if (error != null) throw error;
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        // End the frame on the dispatcher itself, so PushFrame is woken reliably.
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private sealed class Tracking : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly Action _onDispose;
        public Tracking(IDisposable inner, Action onDispose) { _inner = inner; _onDispose = onDispose; }
        public void Dispose() { _inner.Dispose(); _onDispose(); }
    }
}
