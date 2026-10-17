using System.Numerics;
using System.Runtime.InteropServices;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using SharpGen.Runtime;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace PdfEngine.Vector.Direct2D;

/// <summary>Recording pages as command lists for Direct2D's print control (<see cref="Direct2DPrintJob"/>).</summary>
public sealed partial class Direct2DVectorRenderer
{
    /// <summary>The Direct2D device pages are recorded on; a print control must be created on it.</summary>
    internal ID2D1Device Device => _res.Device ?? throw new InvalidOperationException("The Direct2D device is not available.");

    internal Vortice.WIC.IWICImagingFactory Wic => _res.Wic;

    /// <summary>
    /// Records a page as vectors, placed at <paramref name="destination"/> on the sheet (DIPs).
    /// The page is replayed in the pixel space of <paramref name="request"/>, so what Direct2D
    /// cannot keep as vectors (transparency groups, soft masks, pattern cells, shadings evaluated
    /// per pixel) is rasterized at the request's resolution rather than at 96 dpi; fallback regions
    /// are PDFium pixels at that resolution. The recording is then drawn, scaled, into the sheet's
    /// command list, which keeps it resolution independent.
    /// </summary>
    /// <remarks>
    /// Page-level groups that read the page under them (blend modes, non-isolated knockout) cannot
    /// be composited in a command list, which has no pixels. Their areas are printed as images of
    /// the composited page instead, rendered as on screen at the recording resolution, and the
    /// rest of the page stays vectors: see <see cref="PrintBackdropRegions"/>. Returns null when
    /// those areas cover so much of the page that one image of it is the better print.
    /// </remarks>
    internal async Task<ID2D1CommandList?> RecordPrintPageAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider,
        PrintRect destination, CancellationToken ct) =>
        (await RecordPrintPageWithRegionsAsync(list, request, provider, destination, ct).ConfigureAwait(false))?.Sheet;

    /// <summary>Largest share of the page that backdrop regions may cover before the page prints as one image.</summary>
    internal const double MaxBackdropShare = 0.6;

    /// <summary>Most separate backdrop regions per page; each costs one more pass over the page.</summary>
    internal const int MaxBackdropRegions = 16;

    /// <summary>As <see cref="RecordPrintPageAsync"/>, with the regions printed as images (recording pixels).</summary>
    internal async Task<(ID2D1CommandList Sheet, IReadOnlyList<RawRect> Regions)?> RecordPrintPageWithRegionsAsync(IPdfDisplayList list,
        RenderRequest request, IPdfFallbackProvider? provider, PrintRect destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var geometry = PageGeometry.From(list, request);
        // Planned first: a page that prints as one image needs no fallback pixels at this point.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        List<(RawRect Rect, int Last)>? plan;
        try
        {
            using var probe = new Replayer(_res, list, backendFallbacks: null, analyzeOnly: false, ct);
            plan = PrintBackdropRegions(probe.BackdropGroups(), geometry);
        }
        finally
        {
            _gate.Release();
        }
        if (plan == null)
            return null;

        var tokens = await AnalyzeAsync(list, ct).ConfigureAwait(false);
        var overlays = new List<(PdfFallbackToken Token, RenderedPage? Pixels)>(tokens.Count);
        try
        {
            foreach (var token in tokens)
            {
                RenderedPage? pixels = provider == null ? null : await provider.RenderFallbackRegionAsync(token, geometry.Dpi, ct).ConfigureAwait(false);
                overlays.Add((token, pixels));
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ctx = _res.Context!;
                using var page = ctx.CreateCommandList();
                var pageToPixels = geometry.PageUserToPixels();
                Matrix3x2.Invert(pageToPixels, out var pixelsToPage);
                var visible = TransformRect(pixelsToPage, Vector2.Zero, new Vector2(geometry.PixelWidth, geometry.PixelHeight));
                var size = new SizeI(geometry.PixelWidth, geometry.PixelHeight);
                var images = new List<ID2D1Bitmap1>(plan.Count);
                var masks = new List<ID2D1Geometry>(plan.Count);
                // Geometry stays geometry: realizations are tessellations at one scale.
                using var replay = new Replayer(_res, list, backendFallbacks: null, analyzeOnly: false, ct) { UseRealizations = false };
                try
                {
                    // The images first: the context records one command list at a time.
                    foreach (var (rect, last) in plan)
                        images.Add(RenderPrintRegion(replay, pageToPixels, rect, last + 1));
                    for (int k = 0; k < plan.Count; k++)
                        masks.Add(OutsideOf(geometry.PixelWidth, geometry.PixelHeight, plan.Skip(k).Select(p => p.Rect)));
                    var skip = new HashSet<int>(replay.BackdropGroups().Select(g => g.Begin));

                    Record(ctx, page, () =>
                    {
                        ctx.PushAxisAlignedClip(new RawRectF(0, 0, geometry.PixelWidth, geometry.PixelHeight), AntialiasMode.Aliased);
                        if (plan.Count == 0)
                        {
                            replay.DrawToCommandList(ctx, pageToPixels, visible, size);
                        }
                        else
                        {
                            // Pass k draws the page-level units after region k − 1's last group up to
                            // region k's, clipped out of regions k onwards: their images already hold
                            // that content composited, so nothing is drawn twice. After each pass the
                            // next region's image goes down, and later content draws over it.
                            int from = -1;
                            for (int k = 0; k <= plan.Count; k++)
                            {
                                int to = k < plan.Count ? plan[k].Last : int.MaxValue;
                                if (k < plan.Count) PushMask(ctx, masks[k]);
                                replay.DrawPassToCommandList(ctx, pageToPixels, visible, size, from, to, skip);
                                ctx.Transform = Matrix3x2.Identity;
                                if (k < plan.Count)
                                {
                                    ctx.PopLayer();
                                    var r = plan[k].Rect;
                                    ctx.DrawBitmap(images[k], new RawRectF(r.Left, r.Top, r.Right, r.Bottom), 1f,
                                        Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
                                }
                                from = to;
                            }
                        }
                        DrawOverlays(ctx, list, geometry.PagePointsToPixels(), overlays, invert: false, markMissing: false);
                        ctx.Transform = Matrix3x2.Identity;
                        ctx.PopAxisAlignedClip();
                    });
                }
                finally
                {
                    foreach (var m in masks) m.Dispose();
                    foreach (var image in images) image.Dispose(); // the command list keeps its own references
                }
                return (RecordSheet(ctx, page, geometry.PixelWidth, geometry.PixelHeight, destination), plan.Select(p => p.Rect).ToArray());
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
    /// Where a page's backdrop groups print as images: the device rectangles of the page-level
    /// groups that read their backdrop (recording pixels, clamped to the page), merged until no two
    /// touch, each with the index of the last End command among its groups, in that order. Empty
    /// when the page has none; null when they cover more than <see cref="MaxBackdropShare"/> of
    /// the page or are more than <see cref="MaxBackdropRegions"/> apart, and the page prints as
    /// one image.
    /// </summary>
    internal static List<(RawRect Rect, int Last)>? PrintBackdropRegions(List<(int Begin, int End, PdfRect? Bounds)> groups, PageGeometry geometry)
    {
        var regions = new List<(RawRect Rect, int Last)>();
        if (groups.Count == 0)
            return regions;
        var pageToPixels = geometry.PageUserToPixels();
        int width = geometry.PixelWidth, height = geometry.PixelHeight;
        foreach (var (_, end, bounds) in groups)
        {
            RawRect rect;
            if (bounds is PdfRect b)
            {
                // As the screen renderer sizes the group's bitmap: its bounds, a pixel either side.
                var corners = new[]
                {
                    Vector2.Transform(new Vector2((float)b.X, (float)b.Y), pageToPixels),
                    Vector2.Transform(new Vector2((float)b.Right, (float)b.Y), pageToPixels),
                    Vector2.Transform(new Vector2((float)b.X, (float)b.Bottom), pageToPixels),
                    Vector2.Transform(new Vector2((float)b.Right, (float)b.Bottom), pageToPixels),
                };
                rect = new RawRect(
                    Math.Max(0, (int)Math.Floor(corners.Min(c => c.X)) - 1), Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y)) - 1),
                    Math.Min(width, (int)Math.Ceiling(corners.Max(c => c.X)) + 1), Math.Min(height, (int)Math.Ceiling(corners.Max(c => c.Y)) + 1));
            }
            else
            {
                rect = new RawRect(0, 0, width, height);
            }
            if (rect.Right > rect.Left && rect.Bottom > rect.Top)
                regions.Add((rect, end));
        }

        // Regions that overlap or touch become one, so the images never cover each other.
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < regions.Count && !merged; i++)
            {
                for (int j = i + 1; j < regions.Count; j++)
                {
                    var (a, b) = (regions[i].Rect, regions[j].Rect);
                    if (a.Left > b.Right || b.Left > a.Right || a.Top > b.Bottom || b.Top > a.Bottom)
                        continue;
                    regions[i] = (new RawRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)),
                        Math.Max(regions[i].Last, regions[j].Last));
                    regions.RemoveAt(j);
                    merged = true;
                    break;
                }
            }
        }

        double area = regions.Sum(r => (double)(r.Rect.Right - r.Rect.Left) * (r.Rect.Bottom - r.Rect.Top));
        if (regions.Count > MaxBackdropRegions || area > MaxBackdropShare * width * height)
            return null;
        regions.Sort((x, y) => x.Last.CompareTo(y.Last));
        return regions;
    }

    /// <summary>
    /// The page composited up to command <paramref name="end"/> (exclusive) over white paper, in
    /// <paramref name="rect"/> of the recording's pixels: the screen renderer's output there.
    /// </summary>
    private ID2D1Bitmap1 RenderPrintRegion(Replayer replay, Matrix3x2 pageToPixels, RawRect rect, int end)
    {
        var ctx = _res.Context!;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        var bitmap = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        var pageToDevice = pageToPixels * Matrix3x2.CreateTranslation(-rect.Left, -rect.Top);
        Matrix3x2.Invert(pageToDevice, out var deviceToPage);
        var visible = TransformRect(deviceToPage, Vector2.Zero, new Vector2(w, h));
        ctx.Target = bitmap;
        ctx.BeginDraw();
        try
        {
            ctx.Transform = Matrix3x2.Identity;
            ctx.Clear(new Color4(1, 1, 1, 1));
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            ctx.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            if (_res.DocumentTextParams != null) ctx.TextRenderingParams = _res.DocumentTextParams;
            replay.DrawPrefix(ctx, pageToDevice, visible, end);
        }
        catch
        {
            ctx.EndDraw();
            ctx.Target = null;
            bitmap.Dispose();
            throw;
        }
        var hr = ctx.EndDraw();
        ctx.Target = null;
        if (hr.Failure)
        {
            bitmap.Dispose();
            throw new SharpGenException(hr);
        }
        return bitmap;
    }

    /// <summary>The page rectangle with <paramref name="holes"/> (disjoint, inside it) cut out.</summary>
    private ID2D1Geometry OutsideOf(int width, int height, IEnumerable<RawRect> holes)
    {
        var parts = new List<ID2D1Geometry> { _res.Factory.CreateRectangleGeometry(new RawRectF(0, 0, width, height)) };
        foreach (var r in holes) parts.Add(_res.Factory.CreateRectangleGeometry(new RawRectF(r.Left, r.Top, r.Right, r.Bottom)));
        try
        {
            return _res.Factory.CreateGeometryGroup(FillMode.Alternate, parts.ToArray());
        }
        finally
        {
            foreach (var p in parts) p.Dispose(); // the group keeps its own references
        }
    }

    /// <summary>Clips what follows to <paramref name="mask"/> (device pixels), with hard edges that meet the region images exactly.</summary>
    private static void PushMask(ID2D1DeviceContext ctx, ID2D1Geometry mask)
    {
        ctx.Transform = Matrix3x2.Identity;
        ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new RawRectF(-1e7f, -1e7f, 1e7f, 1e7f),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.Aliased,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            OpacityBrush = null!,
            LayerOptions = LayerOptions1.None,
        }, null!);
    }

    /// <summary>Records a rendered page (BGRA, premultiplied) as one image placed at <paramref name="destination"/>.</summary>
    internal ID2D1CommandList RecordPrintImage(RenderedPage image, PrintRect destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            var ctx = _res.Context!;
            var data = image.Pixels.ToArray();
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                using var bitmap = ctx.CreateBitmap(new SizeI(image.WidthPixels, image.HeightPixels), handle.AddrOfPinnedObject(), (uint)image.Stride,
                    new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));
                var sheet = ctx.CreateCommandList();
                Record(ctx, sheet, () =>
                {
                    ctx.DrawBitmap(bitmap, new RawRectF((float)destination.X, (float)destination.Y, (float)destination.Right, (float)destination.Bottom),
                        1f, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
                });
                return sheet;
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The page recording drawn into a sheet-sized command list, scaled from its pixels to <paramref name="destination"/>.</summary>
    private static ID2D1CommandList RecordSheet(ID2D1DeviceContext ctx, ID2D1CommandList page, int pixelWidth, int pixelHeight, PrintRect destination)
    {
        var sheet = ctx.CreateCommandList();
        try
        {
            Record(ctx, sheet, () =>
            {
                ctx.Transform = Matrix3x2.CreateScale((float)(destination.Width / pixelWidth), (float)(destination.Height / pixelHeight)) *
                                Matrix3x2.CreateTranslation((float)destination.X, (float)destination.Y);
                ctx.DrawImage(page, Vector2.Zero, null, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
                ctx.Transform = Matrix3x2.Identity;
            });
            return sheet;
        }
        catch
        {
            sheet.Dispose();
            throw;
        }
    }

    /// <summary>Draws into <paramref name="list"/> and closes it; the context keeps no target afterwards.</summary>
    private static void Record(ID2D1DeviceContext ctx, ID2D1CommandList list, Action draw)
    {
        ctx.Target = list;
        ctx.BeginDraw();
        try
        {
            ctx.Transform = Matrix3x2.Identity;
            ctx.AntialiasMode = AntialiasMode.PerPrimitive;
            draw();
        }
        finally
        {
            var hr = ctx.EndDraw();
            ctx.Target = null;
            if (hr.Failure) throw new SharpGenException(hr);
        }
        list.Close();
    }
}
