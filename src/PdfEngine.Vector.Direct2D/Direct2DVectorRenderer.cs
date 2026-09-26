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
public sealed class Direct2DVectorRenderer : IPdfVectorRenderer
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
    /// Replayers kept across renders, per display list, scale and colour mode. A replayer holds
    /// the page's geometries and its tessellations (realizations) at one scale, which dominate
    /// the cost of dense vector pages; a viewer scrolling at a fixed zoom asks for one tile after
    /// another at the same scale and pays tessellation once. Guarded by <see cref="_gate"/>.
    /// </summary>
    private const int ReplayerCacheCapacity = 6;
    private readonly LinkedList<((IPdfDisplayList List, float Scale, bool Invert) Key, Replayer Replayer)> _replayers = new();

    private Replayer AcquireReplayer(IPdfDisplayList list, float scale, bool invert, CancellationToken ct)
    {
        var key = (list, MathF.Round(scale, 4), invert);
        for (var node = _replayers.First; node != null; node = node.Next)
        {
            if (ReferenceEquals(node.Value.Key.List, list) && node.Value.Key.Scale == key.Item2 && node.Value.Key.Invert == invert)
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
            InvertColors = invert,
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

    public long CachedBitmapBytes => _res.CachedBitmapBytes;

    /// <summary>Test hook: drops every device-dependent resource as a real device reset would.</summary>
    public void SimulateDeviceLoss()
    {
        _gate.Wait();
        try { ClearReplayers(); _res.CreateDevice(); }
        finally { _gate.Release(); }
    }

    public async ValueTask<RenderedPage> RenderDisplayListAsync(
        IPdfDisplayList displayList, RenderRequest request, IPdfFallbackProvider? fallbackProvider = null,
        CancellationToken cancellationToken = default) =>
        (await RenderAsync(displayList, request, fallbackProvider, cancellationToken).ConfigureAwait(false)).Page;

    /// <summary>Regions this backend cannot draw faithfully (fonts it cannot load, glyphs a substitute lacks, undecodable images).</summary>
    public async Task<IReadOnlyList<PdfFallbackToken>> AnalyzeAsync(IPdfDisplayList displayList, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var backend = new List<PdfFallbackToken>();
            var probe = new Replayer(_res, displayList, backend, analyzeOnly: true, cancellationToken);
            probe.Analyze();
            var all = new List<PdfFallbackToken>(displayList.FallbackTokens);
            all.AddRange(backend);
            return all;
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
    /// device resolution. <paramref name="invertColors"/> produces the night-mode page (colour
    /// inversion commutes with source-over compositing, so this equals inverting the pixels).
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
                var replay = AcquireReplayer(list, D2D1.D2D1ComputeMaximumScaleFactor(ref pageToPixels), invertColors, ct);
                await Task.Run(() =>
                {
                    for (int ty = r.Y; ty < r.Y + r.Height; ty += TileSize)
                    {
                        for (int tx = r.X; tx < r.X + r.Width; tx += TileSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            int w = Math.Min(TileSize, r.X + r.Width - tx), h = Math.Min(TileSize, r.Y + r.Height - ty);
                            RenderTileWithRecovery(replay, list, geometry, overlays, tx, ty, w, h, owner.Array, stride, r.X, r.Y, invertColors, ct);
                        }
                    }
                }, ct).ConfigureAwait(false);

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
        int tx, int ty, int w, int h, byte[] dest, int destStride, int originX, int originY, bool invert, CancellationToken ct)
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
                _res.CreateDevice();
                foreach (var (_, cached) in _replayers)
                    if (!ReferenceEquals(cached, replay)) cached.ResetDeviceResources();
                replay.ResetDeviceResources();
            }
        }
    }

    private static bool IsDeviceLost(uint hr) => hr is D2DERR_RECREATE_TARGET or DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET;

    private int RenderTile(Replayer replay, IPdfDisplayList list, PageGeometry g, List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays,
        int tx, int ty, int w, int h, byte[] dest, int destStride, int originX, int originY, bool invert, CancellationToken ct)
    {
        var ctx = _res.Context!;
        var targetProps = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
        var stagingProps = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw);
        var size = new SizeI(w, h);
        using var target = ctx.CreateBitmap(size, IntPtr.Zero, 0, targetProps);
        using var staging = ctx.CreateBitmap(size, IntPtr.Zero, 0, stagingProps);

        var pageToDevice = g.PageUserToPixels() * Matrix3x2.CreateTranslation(-tx, -ty);
        // Page-space rectangle this tile shows (for conservative culling).
        Matrix3x2.Invert(pageToDevice, out var deviceToPage);
        var visible = TransformRect(deviceToPage, new Vector2(0, 0), new Vector2(w, h));

        ctx.Target = target;
        ctx.BeginDraw();
        int composited;
        try
        {
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(invert ? new Color4(0, 0, 0, 1) : new Color4(1, 1, 1, 1));
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            if (_res.DocumentTextParams != null) ctx.TextRenderingParams = _res.DocumentTextParams;

            replay.Draw(ctx, pageToDevice, visible);

            // Fallback regions last: provider pixels are the full truth for their region.
            composited = DrawOverlays(ctx, list, g.PagePointsToPixels() * Matrix3x2.CreateTranslation(-tx, -ty), overlays, invert);
        }
        finally
        {
            var hr = ctx.EndDraw();
            ctx.Target = null;
            if (hr.Failure) throw new SharpGenException(hr);
        }

        staging.CopyFromBitmap(target);
        var map = staging.Map(MapOptions.Read);
        try
        {
            for (int row = 0; row < h; row++)
                Marshal.Copy(map.Bits + (int)(row * map.Pitch), dest, (ty - originY + row) * destStride + (tx - originX) * 4, w * 4);
        }
        finally
        {
            staging.Unmap();
        }
        return composited;
    }

    private int DrawOverlays(ID2D1DeviceContext ctx, IPdfDisplayList list, Matrix3x2 pointsToDevice, List<(PdfFallbackToken Token, RenderedPage? Pixels)> overlays, bool invert)
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
                if (invert)
                {
                    for (int i = 0; i + 3 < data.Length; i += 4)
                    {
                        data[i] = (byte)(255 - data[i]);
                        data[i + 1] = (byte)(255 - data[i + 1]);
                        data[i + 2] = (byte)(255 - data[i + 2]);
                    }
                }
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
            else
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
        try { ClearReplayers(); _res.Dispose(); }
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
