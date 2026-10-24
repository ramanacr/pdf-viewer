using System.Numerics;
using System.Runtime.InteropServices;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace PdfEngine.Vector.Direct2D;

/// <summary>Pixel window of a full-page output.</summary>
public readonly record struct PixelRegion(int X, int Y, int Width, int Height);

/// <summary>Result of a Direct2D render, including every region that needed fallback.</summary>
public sealed record D2DRenderResult(
    RenderedPage Page,
    IReadOnlyList<PdfFallbackToken> Fallbacks,
    int BackendFallbackCount,
    int FallbackRegionsComposited,
    bool Software,
    TimeSpan Elapsed);

/// <summary>
/// Direct2D / DirectWrite renderer for the vector display list (ADR-005). GPU-accelerated
/// (WARP when no GPU), hairlines in device pixels, embedded fonts loaded from memory (no temp
/// files), tiled rendering for outputs beyond the device's bitmap limit, and recovery from
/// device loss without reparsing.
/// </summary>
public sealed partial class Direct2DVectorRenderer : IPdfVectorRenderer
{
    /// <summary>
    /// GPU tile side. Every tile replays the whole display list, so a viewer's detail tile
    /// (≈ 3600 × 2700 px) in one texture costs one replay instead of four at 2048.
    /// </summary>
    public const int DefaultTileSize = 4096;
    private const uint D2DERR_RECREATE_TARGET = 0x8899000C;
    private const uint DXGI_ERROR_DEVICE_REMOVED = 0x887A0005;
    private const uint DXGI_ERROR_DEVICE_RESET = 0x887A0007;

    private readonly D2DResources _res;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _tileSizeLimit;
    private bool _disposed;

    /// <summary>
    /// Replayers kept across renders, per display list and scale. A replayer holds the page's
    /// geometries and its tessellations (realizations) at one scale, which dominate the cost of
    /// dense vector pages; a viewer scrolling at a fixed zoom asks for one tile after another at
    /// the same scale and pays tessellation once. Day and night renders share them: night mode
    /// inverts the finished tile with a Direct2D effect. Guarded by <see cref="_gate"/>.
    /// </summary>
    private const int ReplayerCacheCapacity = 6;
    private readonly LinkedList<((IPdfDisplayList List, float Scale) Key, Replayer Replayer)> _replayers = new();

    private Replayer AcquireReplayer(IPdfDisplayList list, float scale, CancellationToken ct)
    {
        var key = (list, MathF.Round(scale, 4));
        for (var node = _replayers.First; node != null; node = node.Next)
        {
            if (ReferenceEquals(node.Value.Key.List, list) && node.Value.Key.Scale == key.Item2)
            {
                _replayers.Remove(node);
                _replayers.AddFirst(node);
                node.Value.Replayer.SetCancellation(ct);
                return node.Value.Replayer;
            }
        }
        var replayer = new Replayer(_res, list, backendFallbacks: null, analyzeOnly: false, ct)
        {
            UseRealizations = true,
            WindowedRealizations = WindowedRealizations,
            DrawSmallGeometriesDirectly = DrawSmallGeometriesDirectly,
            UseGlyphAtlas = UseGlyphAtlas,
        };
        _replayers.AddFirst((key, replayer));
        while (_replayers.Count > ReplayerCacheCapacity)
        {
            _replayers.Last!.Value.Replayer.Dispose();
            _replayers.RemoveLast();
        }
        return replayer;
    }

    private void ClearReplayers()
    {
        foreach (var (_, replayer) in _replayers) replayer.Dispose();
        _replayers.Clear();
    }

    /// <summary>
    /// Tessellate only near the region drawn (Replayer.Realizations.cs); off realizes every
    /// geometry whole. Applies to replayers created afterwards.
    /// </summary>
    internal bool WindowedRealizations { get; set; } = true;

    /// <summary>
    /// Small geometries are drawn without realizations (tests turn it off to exercise realizations).
    /// Applies to replayers created afterwards.
    /// </summary>
    internal bool DrawSmallGeometriesDirectly { get; set; } = true;

    /// <summary>
    /// Draw opaque text from the device's glyph atlas (tests turn it off to draw every run from its
    /// outlines). Applies to replayers created afterwards.
    /// </summary>
    internal bool UseGlyphAtlas { get; set; } = true;

    /// <summary>Realizations built, windowed, and rebuilt for a region beyond their window, over the cached replayers (tests).</summary>
    internal (int Built, int Windowed, int Rebuilt) RealizationCounts
    {
        get
        {
            _gate.Wait();
            try
            {
                return (_replayers.Sum(n => n.Replayer.RealizationsBuilt), _replayers.Sum(n => n.Replayer.WindowedRealizationsBuilt),
                    _replayers.Sum(n => n.Replayer.RealizationsRebuilt));
            }
            finally { _gate.Release(); }
        }
    }

    /// <param name="forceSoftware">Render on WARP even when a GPU exists (the tier RDP sessions and VMs get).</param>
    /// <param name="tileSize">GPU tile side; bounded by the device's maximum bitmap size.</param>
    public Direct2DVectorRenderer(long bitmapCacheBytes = 256L * 1024 * 1024, bool forceSoftware = false, int tileSize = DefaultTileSize)
    {
        _res = new D2DResources(bitmapCacheBytes, forceSoftware);
        _tileSizeLimit = Math.Clamp(tileSize, 256, 16384);
    }

    /// <summary>Tile side for the current device (feature level 9.x devices allow 2048 or 4096).</summary>
    private int TileSize => (int)Math.Min(_tileSizeLimit, _res.Context?.MaximumBitmapSize ?? 2048u);

    /// <summary>True when running on WARP (no hardware device).</summary>
    public bool IsSoftware => _res.IsSoftware;

    /// <summary>
    /// Outline fallback regions that no provider drew (strict vector mode). Off, a region shows
    /// what the engine draws of it by itself, with nothing laid over it.
    /// </summary>
    public bool OutlineUnsupportedRegions { get; set; }

    public long CachedBitmapBytes => _res.CachedBitmapBytes;

    /// <summary>Time per render stage since the renderer was created (the corpus profiler).</summary>
    internal RenderStages Stages => _res.Stages;

    /// <summary>Test hook: drops every device-dependent resource as a real device reset would.</summary>
    public void SimulateDeviceLoss()
    {
        _gate.Wait();
        try { ClearReplayers(); DisposeTileSurfaces(); _res.CreateDevice(); }
        finally { _gate.Release(); }
    }

    public async ValueTask<RenderedPage> RenderDisplayListAsync(
        IPdfDisplayList displayList, RenderRequest request, IPdfFallbackProvider? fallbackProvider = null,
        CancellationToken cancellationToken = default) =>
        (await RenderAsync(displayList, request, fallbackProvider, cancellationToken).ConfigureAwait(false)).Page;

    // Analysis per display list, kept as long as the list is. Guarded by _gate.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPdfDisplayList, IReadOnlyList<PdfFallbackToken>> _analyses = new();

    /// <summary>Regions this backend cannot draw faithfully (fonts it cannot load, glyphs a substitute lacks, undecodable images).</summary>
    public async Task<IReadOnlyList<PdfFallbackToken>> AnalyzeAsync(IPdfDisplayList displayList, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A display list is immutable and its analysis depends on nothing device dependent, so
            // every render of the page after the first (thumbnail, page, tiles, print) reuses it.
            if (_analyses.TryGetValue(displayList, out var known))
            {
                _res.Stages.AnalysisCacheHits++;
                return known;
            }
            long start = RenderStages.Now;
            var backend = new List<PdfFallbackToken>();
            var probe = new Replayer(_res, displayList, backend, analyzeOnly: true, cancellationToken);
            probe.Analyze();
            var all = new List<PdfFallbackToken>(displayList.FallbackTokens);
            all.AddRange(backend);
            var result = all.AsReadOnly();
            _analyses.AddOrUpdate(displayList, result);
            _res.Stages.Analyze += RenderStages.Now - start;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<D2DRenderResult> RenderAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider, CancellationToken ct) =>
        RenderAsync(list, request, provider, region: null, invertColors: false, ct);

    /// <summary>
    /// Renders the page, or only <paramref name="region"/> of it (pixel window of the full output at
    /// the request's resolution) — how a viewer draws the visible part of a zoomed page at exact
    /// device resolution. <paramref name="invertColors"/> produces the night-mode page: the tile is
    /// drawn as by day and inverted on the GPU by a Direct2D Invert effect, so it is exactly the
    /// inverted day page (blend modes included) at the cost of one extra pass over the tile.
    /// </summary>
    public async Task<D2DRenderResult> RenderAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider,
        PixelRegion? region, bool invertColors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var geometry = PageGeometry.From(list, request);
        var r = ClampRegion(region ?? new PixelRegion(0, 0, geometry.PixelWidth, geometry.PixelHeight), geometry);
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
                int stride = r.Width * 4;
                var owner = new PageBuffer(checked(stride * r.Height));
                // Tessellation is always worth keeping: a realization is drawn at least once per
                // tile, and is reused by the next render at this scale (the next viewport tile).
                var pageToPixels = geometry.PageUserToPixels();
                var replay = AcquireReplayer(list, D2D1.D2D1ComputeMaximumScaleFactor(ref pageToPixels), ct);
                _res.Stages.Setup += clock.ElapsedTicks;
                long tilesStart = RenderStages.Now;
                await Task.Run(() =>
                {
                    _res.Stages.Hop += RenderStages.Now - tilesStart;
                    for (int ty = r.Y; ty < r.Y + r.Height; ty += TileSize)
                    {
                        for (int tx = r.X; tx < r.X + r.Width; tx += TileSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            int w = Math.Min(TileSize, r.X + r.Width - tx), h = Math.Min(TileSize, r.Y + r.Height - ty);
                            RenderTileWithRecovery(replay, list, geometry, overlays, tx, ty, w, h, owner, stride, r.X, r.Y, invertColors, ct);
                        }
                    }
                }, ct).ConfigureAwait(false);
                _res.Stages.Tiles += RenderStages.Now - tilesStart;

                int composited = overlays.Count(o => o.Pixels != null);
                var page = new RenderedPage(list.PageNumber, r.Width, r.Height, stride, geometry.Dpi, request.Rotation, owner);
                return new D2DRenderResult(page, tokens, backendCount, composited, _res.IsSoftware, clock.Elapsed);
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

    /// <summary>
    /// Renders a region (at most one GPU tile) into a new Direct3D 11 texture that other devices
    /// can open (a DXGI shared handle), and waits (up to 2 s) for the GPU to finish drawing it; past
    /// that the texture is returned anyway, so a hung GPU shows a stale tile, never a frozen UI. A viewer
    /// shows it with a Direct3D 9Ex surface in a WPF <c>D3DImage</c>: the pixels never leave the
    /// GPU. Returns null when the region is larger than a tile or the device cannot share
    /// (WARP, remote sessions); the caller then uses <see cref="RenderAsync(IPdfDisplayList, RenderRequest, IPdfFallbackProvider?, PixelRegion?, bool, CancellationToken)"/>.
    /// </summary>
    public async Task<SharedTexture?> RenderToSharedTextureAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider,
        PixelRegion region, bool invertColors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_res.IsSoftware)
            return null;
        var geometry = PageGeometry.From(list, request);
        var r = ClampRegion(region, geometry);
        if (r.Width <= 0 || r.Height <= 0 || r.Width > TileSize || r.Height > TileSize)
            return null;

        Matrix3x2.Invert(geometry.PageUserToPixels(), out var pixelsToPage);
        var regionPage = TransformRect(pixelsToPage, new Vector2(r.X, r.Y), new Vector2(r.X + r.Width, r.Y + r.Height));
        var tokens = await AnalyzeAsync(list, ct).ConfigureAwait(false);
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
                var pageToPixels = geometry.PageUserToPixels();
                var replay = AcquireReplayer(list, D2D1.D2D1ComputeMaximumScaleFactor(ref pageToPixels), ct);
                return await Task.Run(() =>
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            return DrawShared(replay, list, geometry, overlays, r, invertColors);
                        }
                        catch (SharpGenException ex) when (attempt == 0 && IsDeviceLost((uint)ex.HResult))
                        {
                            _res.CreateDevice();
                            foreach (var (_, cached) in _replayers) cached.ResetDeviceResources();
                        }
                    }
                }, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Builds, off the caller's thread, what a render of <paramref name="region"/> at this scale
    /// reuses: the page's replayer and the tessellations of every path the region shows. A viewer
    /// calls it as soon as a new zoom is requested, while the scaled previous tiles are shown, so
    /// the crisp tile then only draws. The replay goes to a one-pixel target: nothing is
    /// rasterized that the render would redo. Cancel it when the zoom changes again; the
    /// tessellations built until then stay valid.
    /// </summary>
    public async Task PrepareAsync(IPdfDisplayList list, RenderRequest request, PixelRegion region, bool invertColors, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var geometry = PageGeometry.From(list, request);
        var r = ClampRegion(region, geometry);
        if (r.Width <= 0 || r.Height <= 0)
            return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pageToPixels = geometry.PageUserToPixels();
            var replay = AcquireReplayer(list, D2D1.D2D1ComputeMaximumScaleFactor(ref pageToPixels), ct);
            await Task.Run(() =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        DrawProbe(replay, geometry, r);
                        return;
                    }
                    catch (SharpGenException ex) when (attempt == 0 && IsDeviceLost((uint)ex.HResult))
                    {
                        DisposeTileSurfaces();
                        _res.CreateDevice();
                        foreach (var (_, cached) in _replayers) cached.ResetDeviceResources();
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ID2D1Bitmap1? _probeTarget;
    private ID2D1DeviceContext? _probeContext;

    /// <summary>Replays the region's transform and culling onto a one-pixel target.</summary>
    private void DrawProbe(Replayer replay, PageGeometry g, PixelRegion r)
    {
        var ctx = _res.Context!;
        if (_probeTarget == null || !ReferenceEquals(_probeContext, ctx))
        {
            _probeTarget?.Dispose();
            _probeTarget = ctx.CreateBitmap(new SizeI(1, 1), IntPtr.Zero, 0,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            _probeContext = ctx;
        }
        var pageToDevice = g.PageUserToPixels() * Matrix3x2.CreateTranslation(-r.X, -r.Y);
        Matrix3x2.Invert(pageToDevice, out var deviceToPage);
        var visible = TransformRect(deviceToPage, new Vector2(0, 0), new Vector2(r.Width, r.Height));
        if (UseGlyphAtlas) _res.PrepareAtlas();
        ctx.Target = _probeTarget;
        ctx.BeginDraw();
        try
        {
            ctx.Transform = Matrix3x2.Identity;
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            if (_res.DocumentTextParams != null) ctx.TextRenderingParams = _res.DocumentTextParams;
            replay.Draw(ctx, pageToDevice, visible);
        }
        finally
        {
            var hr = ctx.EndDraw();
            ctx.Target = null;
            if (hr.Failure) throw new SharpGenException(hr);
        }
    }

    private SharedTexture? DrawShared(Replayer replay, IPdfDisplayList list, PageGeometry g,
        List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays, PixelRegion r, bool invert)
    {
        var d3d = _res.D3D!;
        var desc = new Vortice.Direct3D11.Texture2DDescription
        {
            Width = (uint)r.Width,
            Height = (uint)r.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource,
            MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.Shared,
        };
        var texture = d3d.CreateTexture2D(desc);
        try
        {
            IntPtr handle;
            using (var resource = texture.QueryInterface<IDXGIResource>())
                handle = resource.SharedHandle;
            if (handle == IntPtr.Zero)
            {
                texture.Dispose();
                return null;
            }
            using (var surface = texture.QueryInterface<IDXGISurface>())
            using (var target = _res.Context!.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw)))
            {
                DrawTile(replay, list, g, overlays, r.X, r.Y, r.Width, r.Height, target, invert);
            }

            // Another device reads it next: the drawing must have finished on the GPU.
            var immediate = d3d.ImmediateContext; // cached by the device wrapper: not ours to release
            using var query = d3d.CreateQuery(new Vortice.Direct3D11.QueryDescription(Vortice.Direct3D11.QueryType.Event));
            immediate.End(query);
            immediate.Flush();
            var spin = System.Diagnostics.Stopwatch.StartNew();
            while (immediate.GetData(query, IntPtr.Zero, 0, Vortice.Direct3D11.AsyncGetDataFlags.None).Code == 1 /* S_FALSE */)
            {
                if (spin.ElapsedMilliseconds > 2000) break; // a hung GPU shows a stale tile, never a frozen UI
                Thread.Yield();
            }
            return new SharedTexture(texture, handle, r.Width, r.Height);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>Full-output pixel size of a request (page /Rotate plus viewer rotation).</summary>
    public static (int Width, int Height) OutputSize(IPdfDisplayList list, RenderRequest request)
    {
        var g = PageGeometry.From(list, request);
        return (g.PixelWidth, g.PixelHeight);
    }

    private static PixelRegion ClampRegion(PixelRegion r, PageGeometry g)
    {
        int x0 = Math.Clamp(r.X, 0, g.PixelWidth), y0 = Math.Clamp(r.Y, 0, g.PixelHeight);
        int x1 = Math.Clamp(r.X + r.Width, 0, g.PixelWidth), y1 = Math.Clamp(r.Y + r.Height, 0, g.PixelHeight);
        return new PixelRegion(x0, y0, x1 - x0, y1 - y0);
    }

    private int RenderTileWithRecovery(Replayer replay, IPdfDisplayList list, PageGeometry g, List<(PdfFallbackToken, RenderedPage?)> overlays,
        int tx, int ty, int w, int h, PageBuffer dest, int destStride, int originX, int originY, bool invert, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return RenderTile(replay, list, g, overlays, tx, ty, w, h, dest, destStride, originX, originY, invert, ct);
            }
            catch (SharpGenException ex) when (attempt == 0 && IsDeviceLost((uint)ex.HResult))
            {
                // Device loss: rebuild device-dependent resources and replay; the display list is intact.
                DisposeTileSurfaces();
                _res.CreateDevice();
                foreach (var (_, cached) in _replayers)
                    if (!ReferenceEquals(cached, replay)) cached.ResetDeviceResources();
                replay.ResetDeviceResources();
            }
        }
    }

    private ID2D1Bitmap1? _tileTarget, _tileStaging;
    private SizeI _tileSize;
    private ID2D1DeviceContext? _tileContext;

    /// <summary>
    /// The target and CPU-readable staging bitmaps a tile of w × h is drawn in (its top-left
    /// corner), created once and reused while they are large enough and not much larger. Creating
    /// a pair per render leaked: GPU drivers keep released CPU-readable staging textures, so every
    /// 150 dpi page render held on to about 8 MB of native memory for good (4 GB after three passes
    /// over 150 pages). Sizes are rounded up to <see cref="SurfaceStep"/>, so pages of different
    /// sizes (letter and A4, a landscape page) share one pair instead of creating their own.
    /// </summary>
    private (ID2D1Bitmap1 Target, ID2D1Bitmap1 Staging) TileSurfaces(int w, int h)
    {
        var ctx = _res.Context!;
        bool fits = _tileSize.Width >= w && _tileSize.Height >= h
                    && (long)_tileSize.Width * _tileSize.Height <= 4L * Step(w) * Step(h);
        if (_tileTarget == null || _tileStaging == null || !ReferenceEquals(_tileContext, ctx) || !fits)
        {
            DisposeTileSurfaces();
            var size = new SizeI(Math.Min(Step(w), TileSize), Math.Min(Step(h), TileSize));
            var targetProps = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
            var stagingProps = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
            _tileTarget = ctx.CreateBitmap(size, IntPtr.Zero, 0, targetProps);
            _tileStaging = ctx.CreateBitmap(size, IntPtr.Zero, 0, stagingProps);
            _tileSize = size;
            _tileContext = ctx;
        }
        return (_tileTarget, _tileStaging);
    }

    /// <summary>Tile surfaces grow in steps of this many pixels.</summary>
    private const int SurfaceStep = 256;

    private static int Step(int v) => (v + SurfaceStep - 1) / SurfaceStep * SurfaceStep;

    private void DisposeTileSurfaces()
    {
        _tileTarget?.Dispose();
        _tileStaging?.Dispose();
        _tileTarget = _tileStaging = null;
        _tileContext = null;
        _nightSource?.Dispose();
        _nightSource = null;
        _nightSourceContext = null;
        _probeTarget?.Dispose();
        _probeTarget = null;
        _probeContext = null;
    }

    private static bool IsDeviceLost(uint hr) => hr is D2DERR_RECREATE_TARGET or DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET;

    private int RenderTile(Replayer replay, IPdfDisplayList list, PageGeometry g, List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays,
        int tx, int ty, int w, int h, PageBuffer dest, int destStride, int originX, int originY, bool invert, CancellationToken ct)
    {
        long surfacesStart = RenderStages.Now;
        var (target, staging) = TileSurfaces(w, h);
        _res.Stages.Surfaces += RenderStages.Now - surfacesStart;

        int composited = DrawTile(replay, list, g, overlays, tx, ty, w, h, target, invert);

        long start = RenderStages.Now;
        // Only the tile's corner of the (possibly larger) surfaces is copied and read.
        staging.CopyFromBitmap(new System.Drawing.Point(0, 0), target, new System.Drawing.Rectangle(0, 0, w, h));
        var map = staging.Map(MapOptions.Read);
        _res.Stages.ReadbackWait += RenderStages.Now - start;
        try
        {
            int offset = (ty - originY) * destStride + (tx - originX) * 4;
            var pixels = dest.GetSpan();
            unsafe
            {
                var source = new ReadOnlySpan<byte>((void*)map.Bits, (int)(map.Pitch * (h - 1)) + w * 4);
                if (map.Pitch == destStride && w * 4 == destStride)
                    source[..(destStride * h)].CopyTo(pixels[offset..]); // whole rows: one copy
                else
                    for (int row = 0; row < h; row++)
                        source.Slice((int)(row * map.Pitch), w * 4).CopyTo(pixels[(offset + row * destStride)..]);
            }
        }
        finally
        {
            staging.Unmap();
            _res.Stages.Readback += RenderStages.Now - start;
        }
        return composited;
    }

    /// <summary>Draws the page's tile at (tx, ty) into <paramref name="target"/> (w × h device pixels).</summary>
    private int DrawTile(Replayer replay, IPdfDisplayList list, PageGeometry g, List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays,
        int tx, int ty, int w, int h, ID2D1Bitmap1 target, bool invert)
    {
        var ctx = _res.Context!;
        var pageToDevice = g.PageUserToPixels() * Matrix3x2.CreateTranslation(-tx, -ty);
        // Page-space rectangle this tile shows (for conservative culling).
        Matrix3x2.Invert(pageToDevice, out var deviceToPage);
        var visible = TransformRect(deviceToPage, new Vector2(0, 0), new Vector2(w, h));

        if (UseGlyphAtlas) _res.PrepareAtlas();
        // Night mode: the day tile goes to an offscreen bitmap first, then through the Invert effect.
        var dayTarget = invert ? NightSource(w, h) : target;
        ctx.Target = dayTarget;
        ctx.BeginDraw();
        int composited;
        try
        {
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(new Color4(1, 1, 1, 1));
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            if (_res.DocumentTextParams != null) ctx.TextRenderingParams = _res.DocumentTextParams;

            long replayStart = RenderStages.Now;
            replay.Draw(ctx, pageToDevice, visible);

            // Fallback regions last: provider pixels are the full truth for their region.
            composited = DrawOverlays(ctx, list, g.PagePointsToPixels() * Matrix3x2.CreateTranslation(-tx, -ty), overlays, OutlineUnsupportedRegions);
            _res.Stages.Replay += RenderStages.Now - replayStart;
        }
        finally
        {
            long flushStart = RenderStages.Now;
            var hr = ctx.EndDraw();
            _res.Stages.Flush += RenderStages.Now - flushStart;
            ctx.Target = null;
            if (hr.Failure) throw new SharpGenException(hr);
        }

        if (invert)
        {
            ctx.Target = target;
            ctx.BeginDraw();
            try
            {
                ctx.Transform = Matrix3x2.Identity;
                using var effect = new Vortice.Direct2D1.Effects.Invert(ctx);
                effect.SetInput(0, dayTarget, true);
                using var output = effect.Output;
                ctx.DrawImage(output, Vector2.Zero, null, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            }
            finally
            {
                var hr = ctx.EndDraw();
                ctx.Target = null;
                if (hr.Failure) throw new SharpGenException(hr);
            }
        }
        return composited;
    }

    private ID2D1Bitmap1? _nightSource;
    private SizeI _nightSourceSize;
    private ID2D1DeviceContext? _nightSourceContext;

    /// <summary>The offscreen day tile of a night render, kept while the tile size stays the same.</summary>
    private ID2D1Bitmap1 NightSource(int w, int h)
    {
        var ctx = _res.Context!;
        if (_nightSource == null || !ReferenceEquals(_nightSourceContext, ctx) || _nightSourceSize.Width != w || _nightSourceSize.Height != h)
        {
            _nightSource?.Dispose();
            _nightSourceSize = new SizeI(w, h);
            _nightSource = ctx.CreateBitmap(_nightSourceSize, IntPtr.Zero, 0,
                new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            _nightSourceContext = ctx;
        }
        return _nightSource;
    }

    /// <param name="markMissing">Outline regions without pixels (strict vector mode on screen); otherwise nothing is laid over them.</param>
    private static int DrawOverlays(ID2D1DeviceContext ctx, IPdfDisplayList list, Matrix3x2 pointsToDevice, List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays,
        bool markMissing)
    {
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        int composited = 0;
        ctx.Transform = pointsToDevice;
        using var outline = ctx.CreateSolidColorBrush(new Color4(1f, 0.55f, 0f, 1f));
        using var fill = ctx.CreateSolidColorBrush(new Color4(1f, 0.65f, 0f, 0.16f));
        foreach (var (token, pixels) in overlays)
        {
            var b = token.Bounds;
            var rect = new RawRectF((float)(b.X - crop.X), (float)(crop.Y + crop.Height - (b.Y + b.Height)),
                (float)(b.X + b.Width - crop.X), (float)(crop.Y + crop.Height - b.Y));
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                continue;
            if (pixels != null && pixels.WidthPixels > 0 && pixels.HeightPixels > 0)
            {
                var data = pixels.Pixels.ToArray();
                var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    using var bmp = ctx.CreateBitmap(new SizeI(pixels.WidthPixels, pixels.HeightPixels), handle.AddrOfPinnedObject(), (uint)pixels.Stride,
                        new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));
                    ctx.PushAxisAlignedClip(rect, AntialiasMode.Aliased);
                    ctx.DrawBitmap(bmp, rect, 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                    ctx.PopAxisAlignedClip();
                    composited++;
                }
                finally
                {
                    handle.Free();
                }
            }
            else if (markMissing)
            {
                ctx.FillRectangle(rect, fill);
                ctx.DrawRectangle(rect, outline, 0.75f);
            }
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try { ClearReplayers(); DisposeTileSurfaces(); _res.Dispose(); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// A rendered page's pixels in native memory, as PDFium's renders are (NativeMemoryOwner): pages
    /// of several megabytes on the managed heap made the garbage collector run a full collection
    /// every few renders. Freed on Dispose, or by the finalizer when a caller forgets; reading the
    /// pixels after that throws instead of reading freed memory. Every byte is written by the
    /// render, so the memory is not cleared first.
    /// </summary>
    internal sealed unsafe class PageBuffer : System.Buffers.MemoryManager<byte>
    {
        private byte* _pointer;
        private readonly int _length;

        public PageBuffer(int length)
        {
            _length = length;
            _pointer = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, length));
        }

        ~PageBuffer() => Dispose(false);

        public override Span<byte> GetSpan()
        {
            var p = _pointer;
            if (p == null)
                throw new ObjectDisposedException(nameof(PageBuffer), "The rendered page has already been disposed.");
            return new Span<byte>(p, _length);
        }

        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0)
        {
            var p = _pointer;
            if (p == null)
                throw new ObjectDisposedException(nameof(PageBuffer), "The rendered page has already been disposed.");
            if ((uint)elementIndex > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new System.Buffers.MemoryHandle(p + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            var p = _pointer;
            _pointer = null;
            if (p != null)
                NativeMemory.Free(p);
        }
    }
}

/// <summary>Output size and transforms for a render request (page /Rotate plus viewer rotation).</summary>
internal readonly record struct PageGeometry(double Dpi, int PixelWidth, int PixelHeight, int TotalRotation, PdfRect Crop)
{
    public static PageGeometry From(IPdfDisplayList list, RenderRequest request)
    {
        double dpi = request.Dpi > 0 ? request.Dpi : 96.0;
        var crop = list.CropBox.IsEmpty ? new PdfRect(0, 0, list.PageSize.Width, list.PageSize.Height) : list.CropBox;
        int total = (((list.RotationDegrees + (int)request.Rotation) % 360) + 360) % 360;
        bool sideways = total is 90 or 270;
        double wPts = sideways ? crop.Height : crop.Width, hPts = sideways ? crop.Width : crop.Height;
        int pw = request.TargetWidthPixels > 0 ? request.TargetWidthPixels : (int)Math.Max(1, Math.Round(wPts * dpi / 72.0));
        int ph = request.TargetHeightPixels > 0 ? request.TargetHeightPixels : (int)Math.Max(1, Math.Round(hPts * dpi / 72.0));
        return new PageGeometry(dpi, pw, ph, total, crop);
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

/// <summary>A GPU texture another Direct3D device can open by <see cref="SharedHandle"/>; dispose after it is no longer shown.</summary>
public sealed class SharedTexture : IDisposable
{
    private Vortice.Direct3D11.ID3D11Texture2D? _texture;
    public IntPtr SharedHandle { get; }
    public int Width { get; }
    public int Height { get; }

    internal SharedTexture(Vortice.Direct3D11.ID3D11Texture2D texture, IntPtr handle, int width, int height)
    {
        _texture = texture;
        SharedHandle = handle;
        Width = width;
        Height = height;
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
    }
}
