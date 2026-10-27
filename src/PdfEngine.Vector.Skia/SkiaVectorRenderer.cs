using System.Numerics;
using System.Runtime.InteropServices;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using SkiaSharp;

namespace PdfEngine.Vector.Skia;

/// <summary>Pixel window of a full-page output.</summary>
/// <param name="X">The left edge in pixels of the full output.</param>
/// <param name="Y">The top edge in pixels of the full output.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public readonly record struct SkiaPixelRegion(int X, int Y, int Width, int Height);

/// <summary>Result of a Skia render, including every region that needed fallback.</summary>
/// <param name="Page">The rendered pixels. Dispose it to release the buffer.</param>
/// <param name="Fallbacks">Every region that needed fallback: the display list's tokens and the ones this backend found.</param>
/// <param name="BackendFallbackCount">The number of fallback regions that only this backend found.</param>
/// <param name="FallbackRegionsComposited">The number of fallback regions drawn from the provider's pixels.</param>
/// <param name="Elapsed">The time the render took.</param>
public sealed record SkiaRenderResult(
    RenderedPage Page,
    IReadOnlyList<PdfFallbackToken> Fallbacks,
    int BackendFallbackCount,
    int FallbackRegionsComposited,
    TimeSpan Elapsed);

/// <summary>
/// Portable Skia renderer for the vector display list: a CPU raster backend with no platform
/// dependency beyond SkiaSharp's native library, for hosts without Direct2D (the web viewer on
/// WebAssembly, Android). It replays the display list with the semantics of the Direct2D backend
/// (Replayer.cs there): hairlines one device pixel wide, embedded font programs loaded from memory,
/// JPEG data decoded by Skia's codec, groups composited offscreen, night mode as the inverted day
/// page, and fallback regions drawn from the provider's pixels last.
/// </summary>
/// <remarks>
/// Thread-safe: renders on one instance are serialized. Raster output is the output size itself
/// (no GPU texture limit), so there is no tiling.
/// </remarks>
public sealed class SkiaVectorRenderer : IPdfVectorRenderer
{
    private readonly SkiaResources _res;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Replayers kept across renders, per display list. A replayer holds the page's Skia paths
    /// (built once from the display list's geometry) and its decoded-image and font lookups; a
    /// viewer asking for one region after another of the same page reuses them. Guarded by <see cref="_gate"/>.
    /// </summary>
    private const int ReplayerCacheCapacity = 6;
    private readonly LinkedList<(IPdfDisplayList List, SkiaReplayer Replayer)> _replayers = new();

    /// <summary>Creates a renderer. One instance can render many pages; keep it for the life of the document view and dispose it at the end.</summary>
    /// <param name="imageCacheBytes">Budget for decoded images kept across renders.</param>
    /// <param name="fonts">
    /// Where substitutes for non-embedded fonts come from; null uses <see cref="SkiaFontSource"/>
    /// (the installed fonts). A host without installed fonts (WebAssembly) passes its own.
    /// </param>
    /// <param name="limits">
    /// Image limits for the JPEG data the backend decodes itself (<see cref="PdfEngine.Vector.Limits.PdfSecurityLimits.MaxImagePixels"/>
    /// and <see cref="PdfEngine.Vector.Limits.PdfSecurityLimits.MaxImagePixelsPerPage"/>); null uses the defaults.
    /// </param>
    public SkiaVectorRenderer(long imageCacheBytes = 256L * 1024 * 1024, SkiaFontSource? fonts = null,
        PdfEngine.Vector.Limits.PdfSecurityLimits? limits = null)
    {
        _res = new SkiaResources(imageCacheBytes, fonts ?? new SkiaFontSource(), limits);
    }

    /// <summary>
    /// Outline fallback regions that no provider drew (strict vector mode). Off, a region shows
    /// what the engine draws of it by itself, with nothing laid over it.
    /// </summary>
    public bool OutlineUnsupportedRegions { get; set; }

    /// <summary>Bytes of decoded images currently cached.</summary>
    public long CachedImageBytes => _res.CachedImageBytes;

    private SkiaReplayer AcquireReplayer(IPdfDisplayList list, CancellationToken ct)
    {
        for (var node = _replayers.First; node != null; node = node.Next)
        {
            if (ReferenceEquals(node.Value.List, list))
            {
                _replayers.Remove(node);
                _replayers.AddFirst(node);
                node.Value.Replayer.SetCancellation(ct);
                return node.Value.Replayer;
            }
        }
        var replayer = new SkiaReplayer(_res, list, backendFallbacks: null, ct);
        _replayers.AddFirst((list, replayer));
        while (_replayers.Count > ReplayerCacheCapacity)
        {
            _replayers.Last!.Value.Replayer.Dispose();
            _replayers.RemoveLast();
        }
        return replayer;
    }

    /// <inheritdoc/>
    public async ValueTask<RenderedPage> RenderDisplayListAsync(
        IPdfDisplayList displayList, RenderRequest request, IPdfFallbackProvider? fallbackProvider = null,
        CancellationToken cancellationToken = default) =>
        (await RenderAsync(displayList, request, fallbackProvider, region: null, invertColors: false, cancellationToken).ConfigureAwait(false)).Page;

    /// <summary>
    /// Analyses kept per display list: the answer depends only on the list and this renderer's
    /// fonts, and a viewer that renders a page tile by tile would otherwise decode the page's
    /// images again for every tile. Guarded by <see cref="_gate"/>.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPdfDisplayList, IReadOnlyList<PdfFallbackToken>> _analyses = new();

    /// <summary>Regions this backend cannot draw faithfully (fonts it cannot load, glyphs a substitute lacks, undecodable images).</summary>
    public async Task<IReadOnlyList<PdfFallbackToken>> AnalyzeAsync(IPdfDisplayList displayList, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_analyses.TryGetValue(displayList, out var known))
                return known;
            var backend = new List<PdfFallbackToken>();
            using var probe = new SkiaReplayer(_res, displayList, backend, cancellationToken);
            probe.Analyze();
            var all = new List<PdfFallbackToken>(displayList.FallbackTokens);
            all.AddRange(backend);
            _analyses.AddOrUpdate(displayList, all);
            return all;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renders the page, or only <paramref name="region"/> of it (pixel window of the full output at
    /// the request's resolution). <paramref name="invertColors"/> produces the night-mode page: the
    /// page is drawn as by day and every pixel inverted, so it is exactly the inverted day page
    /// (blend modes included), as the Direct2D backend's Invert effect makes it.
    /// </summary>
    public async Task<SkiaRenderResult> RenderAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider,
        SkiaPixelRegion? region, bool invertColors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var geometry = SkiaPageGeometry.From(list, request);
        var r = ClampRegion(region ?? new SkiaPixelRegion(0, 0, geometry.PixelWidth, geometry.PixelHeight), geometry);
        if (r.Width <= 0 || r.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "Region lies outside the page.");

        // Page-space area of the region, to fetch fallback pixels only where they show.
        Matrix3x2.Invert(geometry.PageUserToPixels(), out var pixelsToPage);
        var regionPage = TransformRect(pixelsToPage, new Vector2(r.X, r.Y), new Vector2(r.X + r.Width, r.Y + r.Height));

        // Analysis first (cheap), so fallback pixels can be fetched before drawing.
        var tokens = await AnalyzeAsync(list, ct).ConfigureAwait(false);
        int backendCount = tokens.Count - list.FallbackTokens.Count;
        var overlays = new List<(PdfFallbackToken Token, RenderedPage? Pixels)>(tokens.Count);
        try
        {
            foreach (var token in tokens)
            {
                if (!token.Bounds.IntersectsWith(regionPage))
                    continue;
                RenderedPage? pixels = provider == null ? null : await provider.RenderFallbackRegionAsync(token, geometry.Dpi, ct).ConfigureAwait(false);
                overlays.Add((token, pixels));
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var replay = AcquireReplayer(list, ct);
                var (owner, composited) = await Task.Run(() => DrawPage(replay, list, geometry, overlays, r, invertColors), ct).ConfigureAwait(false);
                var page = new RenderedPage(list.PageNumber, r.Width, r.Height, r.Width * 4, geometry.Dpi, request.Rotation, owner);
                return new SkiaRenderResult(page, tokens, backendCount, composited, clock.Elapsed);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            foreach (var (_, pixels) in overlays) pixels?.Dispose();
        }
    }

    /// <summary>Full-output pixel size of a request (page /Rotate plus viewer rotation).</summary>
    public static (int Width, int Height) OutputSize(IPdfDisplayList list, RenderRequest request)
    {
        var g = SkiaPageGeometry.From(list, request);
        return (g.PixelWidth, g.PixelHeight);
    }

    private static SkiaPixelRegion ClampRegion(SkiaPixelRegion r, SkiaPageGeometry g)
    {
        int x0 = Math.Clamp(r.X, 0, g.PixelWidth), y0 = Math.Clamp(r.Y, 0, g.PixelHeight);
        int x1 = Math.Clamp(r.X + r.Width, 0, g.PixelWidth), y1 = Math.Clamp(r.Y + r.Height, 0, g.PixelHeight);
        return new SkiaPixelRegion(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Draws the region into a new BGRA buffer: white page, display list, fallback pixels, then night inversion.</summary>
    private (PageBuffer Owner, int Composited) DrawPage(SkiaReplayer replay, IPdfDisplayList list, SkiaPageGeometry g,
        List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays, SkiaPixelRegion r, bool invert)
    {
        var owner = new PageBuffer(checked(r.Width * 4 * r.Height));
        var info = new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(owner.Array, GCHandleType.Pinned);
        int composited;
        try
        {
            // The canvas draws straight into the page buffer: no copy afterwards.
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidOperationException("Skia could not wrap the page buffer.");
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                var pageToDevice = g.PageUserToPixels() * Matrix3x2.CreateTranslation(-r.X, -r.Y);
                // Page-space rectangle this region shows (for conservative culling).
                Matrix3x2.Invert(pageToDevice, out var deviceToPage);
                var visible = TransformRect(deviceToPage, new Vector2(0, 0), new Vector2(r.Width, r.Height));
                replay.Draw(bitmap, canvas, pageToDevice, visible);

                // Fallback regions last: provider pixels are the full truth for their region.
                composited = DrawOverlays(canvas, list, g.PagePointsToPixels() * Matrix3x2.CreateTranslation(-r.X, -r.Y), overlays, OutlineUnsupportedRegions);
                canvas.Flush();
            }
        }
        finally
        {
            handle.Free();
        }
        if (invert)
            InvertOpaque(owner.Array);
        return (owner, composited);
    }

    /// <summary>Night mode: the finished page is opaque, so inverting is 255 − v on each colour channel.</summary>
    private static void InvertOpaque(byte[] bgra)
    {
        var words = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
        for (int i = 0; i < words.Length; i++)
            words[i] ^= 0x00FFFFFFu; // B, G, R inverted; alpha (the high byte) kept
    }

    /// <param name="markMissing">Outline regions without pixels (strict vector mode on screen); otherwise nothing is laid over them.</param>
    private static int DrawOverlays(SKCanvas canvas, IPdfDisplayList list, Matrix3x2 pointsToDevice, List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays,
        bool markMissing)
    {
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        int composited = 0;
        canvas.SetMatrix(SkiaReplayer.ToSk(pointsToDevice));
        using var outline = new SKPaint { Color = new SKColor(255, 140, 0, 255), IsStroke = true, StrokeWidth = 0.75f, IsAntialias = true };
        using var fill = new SKPaint { Color = new SKColor(255, 166, 0, 41), IsAntialias = true };
        foreach (var (token, pixels) in overlays)
        {
            var b = token.Bounds;
            var rect = new SKRect((float)(b.X - crop.X), (float)(crop.Y + crop.Height - (b.Y + b.Height)),
                (float)(b.X + b.Width - crop.X), (float)(crop.Y + crop.Height - b.Y));
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                continue;
            if (pixels != null && pixels.WidthPixels > 0 && pixels.HeightPixels > 0)
            {
                var info = new SKImageInfo(pixels.WidthPixels, pixels.HeightPixels, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var data = SKData.CreateCopy(pixels.Pixels.Span);
                using var image = SKImage.FromPixels(info, data, pixels.Stride);
                if (image == null)
                    continue;
                canvas.Save();
                canvas.ClipRect(rect, SKClipOperation.Intersect, antialias: false);
                canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear));
                canvas.Restore();
                composited++;
            }
            else if (markMissing)
            {
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, outline);
            }
        }
        canvas.ResetMatrix();
        return composited;
    }

    internal static PdfRect TransformRect(Matrix3x2 m, Vector2 p0, Vector2 p1)
    {
        var a = Vector2.Transform(p0, m);
        var b = Vector2.Transform(new Vector2(p1.X, p0.Y), m);
        var c = Vector2.Transform(p1, m);
        var d = Vector2.Transform(new Vector2(p0.X, p1.Y), m);
        float x0 = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), x1 = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        float y0 = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)), y1 = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Releases the cached paths, images and typefaces.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try
        {
            foreach (var (_, replayer) in _replayers) replayer.Dispose();
            _replayers.Clear();
            _res.Dispose();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Plain array-backed pixel owner for rendered pages.</summary>
    private sealed class PageBuffer : System.Buffers.IMemoryOwner<byte>
    {
        public byte[] Array { get; }
        public PageBuffer(int length) => Array = new byte[length];
        public Memory<byte> Memory => Array;
        public void Dispose() { }
    }
}

/// <summary>Output size and transforms for a render request (page /Rotate plus viewer rotation), as the Direct2D backend computes them.</summary>
internal readonly record struct SkiaPageGeometry(double Dpi, int PixelWidth, int PixelHeight, int TotalRotation, PdfRect Crop)
{
    public static SkiaPageGeometry From(IPdfDisplayList list, RenderRequest request)
    {
        double dpi = request.Dpi > 0 ? request.Dpi : 96.0;
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        int total = (((list.RotationDegrees + (int)request.Rotation) % 360) + 360) % 360;
        bool sideways = total is 90 or 270;
        double wPts = sideways ? crop.Height : crop.Width, hPts = sideways ? crop.Width : crop.Height;
        int pw = request.TargetWidthPixels > 0 ? request.TargetWidthPixels : (int)Math.Max(1, Math.Round(wPts * dpi / 72.0));
        int ph = request.TargetHeightPixels > 0 ? request.TargetHeightPixels : (int)Math.Max(1, Math.Round(hPts * dpi / 72.0));
        // A size in pixels sets the resolution: it is reported, and fallback regions are fetched at it.
        // When the horizontal and vertical resolutions differ, the larger keeps fallback pixels sharp.
        double dpiX = request.TargetWidthPixels > 0 && wPts > 0 ? pw * 72.0 / wPts : dpi;
        double dpiY = request.TargetHeightPixels > 0 && hPts > 0 ? ph * 72.0 / hPts : dpi;
        return new SkiaPageGeometry(Math.Max(dpiX, dpiY), pw, ph, total, crop);
    }

    /// <summary>Unrotated top-left page points → output pixels.</summary>
    public Matrix3x2 PagePointsToPixels()
    {
        double w = Crop.Width, h = Crop.Height;
        Matrix3x2 rot = TotalRotation switch
        {
            90 => new Matrix3x2(0, 1, -1, 0, (float)h, 0),
            180 => new Matrix3x2(-1, 0, 0, -1, (float)w, (float)h),
            270 => new Matrix3x2(0, -1, 1, 0, 0, (float)w),
            _ => Matrix3x2.Identity,
        };
        bool sideways = TotalRotation is 90 or 270;
        float sx = (float)(PixelWidth / (sideways ? h : w)), sy = (float)(PixelHeight / (sideways ? w : h));
        return rot * Matrix3x2.CreateScale(sx, sy);
    }

    /// <summary>PDF default user space (y-up, crop origin) → output pixels.</summary>
    public Matrix3x2 PageUserToPixels()
    {
        var flip = new Matrix3x2(1, 0, 0, -1, (float)-Crop.X, (float)(Crop.Y + Crop.Height));
        return flip * PagePointsToPixels();
    }
}
