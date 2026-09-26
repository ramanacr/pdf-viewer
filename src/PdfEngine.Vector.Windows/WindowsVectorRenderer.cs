using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
using PdfEngine.Rendering;

namespace PdfEngine.Vector.Windows;

/// <summary>Outcome of a vector render, including every region that needed fallback.</summary>
/// <param name="Fallbacks">Interpreter tokens plus regions the backend could not draw faithfully.</param>
/// <param name="BackendFallbackCount">How many of <paramref name="Fallbacks"/> the backend added.</param>
/// <param name="FallbackRegionsComposited">Regions filled with fallback-provider pixels (the rest show placeholders).</param>
public sealed record PdfVectorRenderResult(
    RenderedPage Page,
    IReadOnlyList<PdfFallbackToken> Fallbacks,
    int BackendFallbackCount,
    int FallbackRegionsComposited,
    TimeSpan CompileTime,
    TimeSpan RasterTime);

/// <summary>Frozen on-screen page surface and what it needed from fallback.</summary>
public sealed record PdfVectorSurfaceResult(
    ImageSource Surface,
    double WidthPoints,
    double HeightPoints,
    IReadOnlyList<PdfFallbackToken> Fallbacks,
    int BackendFallbackCount,
    int FallbackRegionsComposited,
    int CommandCount);

/// <summary>
/// Windows vector renderer: replays an immutable display list through WPF's retained drawing
/// stack and rasterizes at the requested resolution. Unsupported regions are composited from an
/// <see cref="IPdfFallbackProvider"/> (hybrid) or marked with placeholders (strict vector mode).
/// </summary>
public sealed class WindowsVectorRenderer : IPdfVectorRenderer
{
    private readonly StaRenderThread _sta = new("PdfVectorRender");
    private readonly WpfFontCache _fonts = new();
    private readonly WpfImageCache _images;
    private readonly WpfDisplayListCompiler _compiler;
    private bool _disposed;

    /// <param name="imageCacheBytes">Budget for decoded image bitmaps shared across pages.</param>
    public WindowsVectorRenderer(long imageCacheBytes = 256L * 1024 * 1024)
    {
        _images = new WpfImageCache(imageCacheBytes);
        _compiler = new WpfDisplayListCompiler(_fonts, _images);
    }

    /// <summary>Bytes currently held by decoded images (for diagnostics / memory tests).</summary>
    public long CachedImageBytes => _images.CachedBytes;

    public async ValueTask<RenderedPage> RenderDisplayListAsync(
        IPdfDisplayList displayList,
        RenderRequest request,
        IPdfFallbackProvider? fallbackProvider = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RenderAsync(displayList, request, fallbackProvider, cancellationToken).ConfigureAwait(false);
        return result.Page;
    }

    /// <summary>
    /// Compiles without rasterizing and returns every region that needs fallback, including those
    /// only the backend can detect (font programs it cannot load, glyphs a substitute lacks, images
    /// that fail to decode). Hosts use it to decide between vector, hybrid and full fallback.
    /// </summary>
    public Task<IReadOnlyList<PdfFallbackToken>> AnalyzeAsync(IPdfDisplayList displayList, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _sta.InvokeAsync<IReadOnlyList<PdfFallbackToken>>(
            () => _compiler.Compile(displayList, null, cancellationToken).Fallbacks,
            cancellationToken);
    }

    /// <summary>
    /// Frozen, resolution-independent drawing of the page in top-left page points (crop box, before
    /// /Rotate). A vector-capable host can display it at any zoom without re-rasterizing.
    /// Fallback regions are not included.
    /// </summary>
    public Task<Drawing> BuildPageDrawingAsync(IPdfDisplayList displayList, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _sta.InvokeAsync<Drawing>(() => _compiler.Compile(displayList, null, cancellationToken).Drawing, cancellationToken);
    }

    public async Task<PdfVectorRenderResult> RenderAsync(
        IPdfDisplayList displayList,
        RenderRequest request,
        IPdfFallbackProvider? fallbackProvider,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(displayList);

        var geometry = RenderGeometry.From(displayList, request);
        var compileClock = System.Diagnostics.Stopwatch.StartNew();
        var compiled = await _sta.InvokeAsync(() => _compiler.Compile(displayList, null, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        compileClock.Stop();

        // Fetch fallback pixels off the STA thread (the provider may call into PDFium).
        var overlays = new List<(PdfFallbackToken Token, RenderedPage? Pixels)>(compiled.Fallbacks.Count);
        foreach (var token in compiled.Fallbacks)
        {
            RenderedPage? pixels = null;
            if (fallbackProvider != null)
            {
                pixels = await fallbackProvider.RenderFallbackRegionAsync(token, geometry.Dpi, cancellationToken).ConfigureAwait(false);
            }
            overlays.Add((token, pixels));
        }

        try
        {
            var rasterClock = System.Diagnostics.Stopwatch.StartNew();
            var (page, composited) = await _sta.InvokeAsync(
                () => Rasterize(displayList, compiled, geometry, overlays, request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            rasterClock.Stop();

            return new PdfVectorRenderResult(page, compiled.Fallbacks, compiled.BackendFallbacks.Count, composited,
                compileClock.Elapsed, rasterClock.Elapsed);
        }
        finally
        {
            foreach (var (_, pixels) in overlays)
                pixels?.Dispose();
        }
    }

    /// <summary>Output size and page-points → pixel transform for a request.</summary>
    private readonly record struct RenderGeometry(double Dpi, int PixelWidth, int PixelHeight, int TotalRotation, double WidthPoints, double HeightPoints)
    {
        public static RenderGeometry From(IPdfDisplayList list, RenderRequest request)
        {
            double dpi = request.Dpi > 0 ? request.Dpi : 96.0;
            var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;

            // The page's own /Rotate plus the viewer's rotation, like FPDF_RenderPageBitmap.
            int total = (((list.RotationDegrees + (int)request.Rotation) % 360) + 360) % 360;
            bool sideways = total is 90 or 270;
            double wPts = sideways ? crop.Height : crop.Width;
            double hPts = sideways ? crop.Width : crop.Height;

            int pw = request.TargetWidthPixels > 0 ? request.TargetWidthPixels : (int)Math.Max(1, Math.Round(wPts * dpi / 72.0));
            int ph = request.TargetHeightPixels > 0 ? request.TargetHeightPixels : (int)Math.Max(1, Math.Round(hPts * dpi / 72.0));
            return new RenderGeometry(dpi, pw, ph, total, crop.Width, crop.Height);
        }

        /// <summary>Unrotated page points (top-left origin) → output pixels.</summary>
        public Matrix PageToPixels()
        {
            var m = Matrix.Identity;
            switch (TotalRotation)
            {
                case 90:
                    m.Rotate(90);
                    m.Translate(HeightPoints, 0);
                    break;
                case 180:
                    m.Rotate(180);
                    m.Translate(WidthPoints, HeightPoints);
                    break;
                case 270:
                    m.Rotate(270);
                    m.Translate(0, WidthPoints);
                    break;
            }
            bool sideways = TotalRotation is 90 or 270;
            double sx = PixelWidth / (sideways ? HeightPoints : WidthPoints);
            double sy = PixelHeight / (sideways ? WidthPoints : HeightPoints);
            m.Scale(sx, sy);
            return m;
        }
    }

    private static (RenderedPage Page, int Composited) Rasterize(
        IPdfDisplayList list,
        WpfDisplayListCompiler.CompiledPage compiled,
        RenderGeometry geometry,
        IReadOnlyList<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays,
        RenderRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        int composited = 0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, geometry.PixelWidth, geometry.PixelHeight));
            dc.PushTransform(new MatrixTransform(geometry.PageToPixels()));
            dc.DrawDrawing(compiled.Drawing);

            composited = DrawOverlays(dc, crop, overlays);

            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(geometry.PixelWidth, geometry.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        int stride = geometry.PixelWidth * 4;
        var memoryOwner = new ManagedMemoryOwner(checked(stride * geometry.PixelHeight));
        rtb.CopyPixels(new Int32Rect(0, 0, geometry.PixelWidth, geometry.PixelHeight), memoryOwner.Buffer, stride, 0);

        return (new RenderedPage(list.PageNumber, geometry.PixelWidth, geometry.PixelHeight, stride, geometry.Dpi, request.Rotation, memoryOwner), composited);
    }

    /// <summary>
    /// Fallback regions are drawn last: the provider's pixels are the full truth for the region
    /// (every object in it), so nothing vector may paint over them. Coordinates are unrotated
    /// top-left page points.
    /// </summary>
    private static int DrawOverlays(DrawingContext dc, PdfRect crop, IReadOnlyList<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays, bool invert = false)
    {
        int composited = 0;
        foreach (var (token, pixels) in overlays)
        {
            var b = token.Bounds;
            var rect = new Rect(b.X - crop.X, crop.Y + crop.Height - (b.Y + b.Height), b.Width, b.Height);
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            if (pixels != null && pixels.WidthPixels > 0 && pixels.HeightPixels > 0)
            {
                var data = pixels.Pixels.ToArray();
                if (invert)
                    WpfImageCache.InvertBgra(data);
                var bmp = BitmapSource.Create(pixels.WidthPixels, pixels.HeightPixels, 96, 96, PixelFormats.Bgra32, null,
                    data, pixels.Stride);
                bmp.Freeze();
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawImage(bmp, rect);
                dc.Pop();
                composited++;
            }
            else
            {
                // Strict vector mode: make the unsupported region visible, never silently wrong.
                var fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 165, 0));
                fill.Freeze();
                var pen = new Pen(Brushes.DarkOrange, 0.75);
                pen.Freeze();
                dc.DrawRectangle(fill, pen, rect);
            }
        }
        return composited;
    }

    /// <summary>
    /// Builds a frozen, resolution-independent page surface for on-screen display (backlog E8):
    /// white page, vector content, and fallback regions composited from <paramref name="fallbackProvider"/>
    /// rasterized at <paramref name="fallbackDpi"/> (or outlined when no provider is given).
    /// Its size is the page in points after /Rotate and <paramref name="rotation"/>; a host stretches
    /// it to any zoom and WPF re-renders the vectors at that scale — no page bitmap exists.
    /// </summary>
    public async Task<PdfVectorSurfaceResult> BuildPageSurfaceAsync(
        IPdfDisplayList displayList,
        PageRotation rotation,
        IPdfFallbackProvider? fallbackProvider,
        double fallbackDpi,
        CancellationToken cancellationToken,
        bool invertColors = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(displayList);

        var compiled = await _sta.InvokeAsync(() => _compiler.Compile(displayList, null, cancellationToken, invertColors), cancellationToken)
            .ConfigureAwait(false);

        var overlays = new List<(PdfFallbackToken Token, RenderedPage? Pixels)>(compiled.Fallbacks.Count);
        try
        {
            foreach (var token in compiled.Fallbacks)
            {
                RenderedPage? pixels = fallbackProvider == null
                    ? null
                    : await fallbackProvider.RenderFallbackRegionAsync(token, fallbackDpi, cancellationToken).ConfigureAwait(false);
                overlays.Add((token, pixels));
            }

            return await _sta.InvokeAsync(() =>
            {
                var crop = displayList.CropBox.IsEmpty ? new PdfRect(0, 0, displayList.PageSize.Width, displayList.PageSize.Height) : displayList.CropBox;
                int total = (((displayList.RotationDegrees + (int)rotation) % 360) + 360) % 360;
                bool sideways = total is 90 or 270;
                double w = sideways ? crop.Height : crop.Width;
                double h = sideways ? crop.Width : crop.Height;

                var group = new DrawingGroup();
                int composited;
                using (var dc = group.Open())
                {
                    dc.DrawRectangle(invertColors ? Brushes.Black : Brushes.White, null, new Rect(0, 0, w, h));
                    dc.PushTransform(new MatrixTransform(RotationMatrix(total, crop.Width, crop.Height)));
                    dc.DrawDrawing(compiled.Drawing);
                    composited = DrawOverlays(dc, crop, overlays, invertColors);
                    dc.Pop();
                }
                // Pin the bounds: an empty page must still measure as the full page.
                group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, w, h));
                group.Freeze();

                var image = new DrawingImage(group);
                image.Freeze();
                return new PdfVectorSurfaceResult(image, w, h, compiled.Fallbacks, compiled.BackendFallbacks.Count, composited,
                    displayList.Commands.Count);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, pixels) in overlays)
                pixels?.Dispose();
        }
    }

    /// <summary>Unrotated top-left page points → rotated top-left page points.</summary>
    private static Matrix RotationMatrix(int totalRotation, double widthPoints, double heightPoints)
    {
        var m = Matrix.Identity;
        switch (totalRotation)
        {
            case 90: m.Rotate(90); m.Translate(heightPoints, 0); break;
            case 180: m.Rotate(180); m.Translate(widthPoints, heightPoints); break;
            case 270: m.Rotate(270); m.Translate(0, widthPoints); break;
        }
        return m;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _images.Clear();
        _sta.InvokeAsync(() => { _fonts.Dispose(); return 0; }, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
        _sta.Dispose();
    }
}
