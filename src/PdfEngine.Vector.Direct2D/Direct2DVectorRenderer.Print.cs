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
    internal async Task<ID2D1CommandList> RecordPrintPageAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider,
        PrintRect destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var geometry = PageGeometry.From(list, request);
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
                // Geometry stays geometry: realizations are tessellations at one scale.
                using (var replay = new Replayer(_res, list, backendFallbacks: null, analyzeOnly: false, ct) { UseRealizations = false })
                {
                    Record(ctx, page, () =>
                    {
                        ctx.PushAxisAlignedClip(new RawRectF(0, 0, geometry.PixelWidth, geometry.PixelHeight), AntialiasMode.Aliased);
                        replay.DrawToCommandList(ctx, pageToPixels, visible, new SizeI(geometry.PixelWidth, geometry.PixelHeight));
                        DrawOverlays(ctx, list, geometry.PagePointsToPixels(), overlays, invert: false, markMissing: false);
                        ctx.Transform = Matrix3x2.Identity;
                        ctx.PopAxisAlignedClip();
                    });
                }
                return RecordSheet(ctx, page, geometry.PixelWidth, geometry.PixelHeight, destination);
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
