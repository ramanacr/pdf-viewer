using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Windows;

namespace VectorPdf.Tool;

/// <summary>
/// vectorpdf live &lt;file.pdf&gt; [--frames N] [--pages N]
///
/// Opens a real on-screen window laid out like the viewer (pages stacked in a ScrollViewer) and
/// drives it: scrolling at 100 %, then zoom steps up to 1600 % with scrolling at each. Frame
/// cadence is taken from CompositionTarget.Rendering, which WPF throttles when the render thread
/// falls behind, so it reflects what a user sees. Runs twice: live vector surfaces, then PDFium
/// bitmaps at the viewer's DPI buckets (150/200/300), and prints p50/p95/max frame times.
/// </summary>
internal static class LiveBench
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("live: pdf file not found");
            return 2;
        }

        int frames = int.TryParse(Program.Option(args, "--frames"), out int f) ? f : 240;
        int maxPages = int.TryParse(Program.Option(args, "--pages"), out int p) ? p : 6;
        byte[] bytes = File.ReadAllBytes(args[1]);
        int exit = 0;

        Console.WriteLine($"render tier={RenderCapability.Tier >> 16} (2 = full GPU)");
        foreach (var mode in new[] { Mode.Live, Mode.LiveCached, Mode.Bitmap })
        {
            bool live = mode != Mode.Bitmap;
            // A WPF dispatcher cannot restart on a thread once shut down: one STA thread per run.
            var thread = new Thread(() =>
            {
                try
                {
                    var inv = CultureInfo.InvariantCulture;
                    var stats = RunOnce(bytes, mode, frames, maxPages);
                    foreach (var (phase, samples) in stats)
                    {
                        samples.Sort();
                        double P(double q) => samples.Count == 0 ? 0 : samples[Math.Min(samples.Count - 1, (int)(q * samples.Count))];
                        Console.WriteLine(string.Create(inv,
                            $"{mode,-14} {phase,-20} frames={samples.Count,4} p50={P(0.5),6:F1}ms p95={P(0.95),6:F1}ms max={(samples.Count > 0 ? samples[^1] : 0),7:F1}ms"));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    exit = 1;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        return exit;
    }

    internal enum Mode { Live, LiveCached, Bitmap }

    private static List<(string Phase, List<double> Samples)> RunOnce(byte[] bytes, Mode mode, int framesPerPhase, int maxPages)
    {
        bool live = mode != Mode.Bitmap;
        var results = new List<(string, List<double>)>();
        using var doc = PdfVectorDocument.OpenAsync(bytes).AsTask().GetAwaiter().GetResult();
        using var renderer = new WindowsVectorRenderer();
        using var engine = new PdfiumEngine();
        var pdfiumDoc = engine.OpenDocumentAsync(bytes).AsTask().GetAwaiter().GetResult();

        int pages = Math.Min(maxPages, doc.PageCount);
        var images = new List<(Image Image, double W, double H, int Page)>();
        var stack = new StackPanel { Background = Brushes.Gray };
        for (int i = 1; i <= pages; i++)
        {
            var node = doc.PageTree.Pages[i - 1];
            var img = new Image { Stretch = Stretch.Fill, Margin = new Thickness(0, 0, 0, 20) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            images.Add((img, node.PageSize.Width, node.PageSize.Height, i));
            stack.Children.Add(img);
        }

        var scroller = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = scroller,
            Title = live ? "vectorpdf live (vector)" : "vectorpdf live (bitmap)",
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = false,
        };

        void Load(double zoom)
        {
            int dpi = zoom <= 1.0 ? 150 : zoom <= 2.0 ? 200 : 300; // the viewer's DPI buckets
            foreach (var (img, w, h, page) in images)
            {
                img.Width = w * zoom;
                img.Height = h * zoom;
                if (mode == Mode.LiveCached)
                    img.CacheMode = PdfViewerSurfaceCache.For(img.Width, img.Height, VisualTreeHelper.GetDpi(img).DpiScaleX);
                if (live)
                {
                    if (img.Source == null)
                    {
                        var list = doc.GetPageDisplayListAsync(page).AsTask().GetAwaiter().GetResult();
                        img.Source = renderer.BuildPageSurfaceAsync(list, PageRotation.Rotate0, null, 200, CancellationToken.None)
                            .GetAwaiter().GetResult().Surface;
                    }
                }
                else
                {
                    using var rendered = engine.Renderer.RenderPageAsync(pdfiumDoc, new RenderRequest { PageNumber = page, Dpi = dpi })
                        .AsTask().GetAwaiter().GetResult();
                    var bmp = BitmapSource.Create(rendered.WidthPixels, rendered.HeightPixels, 96, 96, PixelFormats.Bgra32, null,
                        rendered.Pixels.ToArray(), rendered.Stride);
                    bmp.Freeze();
                    img.Source = bmp;
                }
            }
        }

        var phases = new Queue<double>(new[] { 1.0, 2.0, 4.0, 8.0, 16.0 });
        var clock = Stopwatch.StartNew();
        double last = -1;
        int frame = 0;
        List<double>? current = null;
        double zoomNow = 0;

        void Next()
        {
            if (phases.Count == 0)
            {
                window.Close();
                return;
            }
            zoomNow = phases.Dequeue();
            Load(zoomNow);
            scroller.ScrollToVerticalOffset(0);
            scroller.ScrollToHorizontalOffset(0);
            current = new List<double>(framesPerPhase);
            results.Add(($"zoom {zoomNow * 100:0}% scroll", current));
            frame = -10; // let layout settle; skip warm-up frames
            last = -1;
        }

        EventHandler onRendering = (_, _) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (current == null) return;
            if (frame >= 0 && last >= 0) current.Add(now - last);
            last = now;
            frame++;
            // Scroll a steady 40 device-independent pixels per frame, like a fast wheel/drag.
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 40);
            if (scroller.VerticalOffset >= scroller.ScrollableHeight - 1) scroller.ScrollToVerticalOffset(0);
            if (frame >= framesPerPhase) Next();
        };

        window.ContentRendered += (_, _) =>
        {
            Next();
            CompositionTarget.Rendering += onRendering;
        };
        window.Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= onRendering;
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        };
        window.Show();
        Dispatcher.Run();
        pdfiumDoc.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return results;
    }
}

/// <summary>Mirror of the viewer's rule (kept identical to PageSurfaceCacheMode in PdfViewer).</summary>
internal static class PdfViewerSurfaceCache
{
    public const double MaxCachedPixels = 4096;

    public static CacheMode? For(double width, double height, double dpiScale) =>
        Math.Max(width, height) * dpiScale <= MaxCachedPixels ? new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = false } : null;
}
