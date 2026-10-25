using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.PrintProduction;

/// <summary>
/// Draws a display list to pixels: tight 8-bit B, G, R, A rows of exactly <c>width</c> × <c>height</c>, the page's
/// crop box filling them with its /Rotate applied, over white. The engine has no rasterizer of its own; the
/// Direct2D backend provides one (<c>Direct2DDisplayListRasterizer</c>).
/// </summary>
public interface IPdfDisplayListRasterizer
{
    Task<byte[]> RenderAsync(IPdfDisplayList list, int width, int height, CancellationToken cancellationToken = default);
}

/// <summary>How a page is separated.</summary>
public sealed class PdfSeparationOptions
{
    /// <summary>The plates; by default the page's own colorants (<see cref="PdfColorantInventory"/>).</summary>
    public PdfInkModel? Model { get; init; }

    /// <summary>Honour /OP, /op and /OPM (one render per plate). Off: every object knocks out what is under it.</summary>
    public bool SimulateOverprint { get; init; } = true;

    /// <summary>Only these plates (channel indices of the model); all when null.</summary>
    public IReadOnlyCollection<int>? Channels { get; init; }
}

/// <summary>Total ink of a separated page: the most at any pixel, and how much of the page is above a limit.</summary>
/// <param name="OverBounds">The pixels over the limit, as a pixel rectangle (X, Y from the top-left), or null when none are.</param>
public sealed record PdfInkCoverage(double MaxPercent, double LimitPercent, long PixelsOver, double FractionOver, PdfRect? OverBounds);

/// <summary>
/// A separated page: one 8-bit ink plane per plate (0 = no ink, 255 = 100 %), at the size it was rendered.
/// </summary>
public sealed class PdfSeparation
{
    private readonly byte[]?[] _planes;

    internal PdfSeparation(PdfInkModel model, int width, int height, byte[]?[] planes, bool overprint, bool hasFallback)
    {
        Model = model;
        Width = width;
        Height = height;
        _planes = planes;
        SimulatedOverprint = overprint;
        HasFallback = hasFallback;
    }

    public PdfInkModel Model { get; }
    public int Width { get; }
    public int Height { get; }
    public bool SimulatedOverprint { get; }

    /// <summary>Part of the page needed a fallback renderer the separation cannot use: those areas are approximate.</summary>
    public bool HasFallback { get; }

    /// <summary>The ink plane of a plate (row-major, top row first), or null when it was not rendered.</summary>
    public byte[]? Plane(int channel) => channel >= 0 && channel < _planes.Length ? _planes[channel] : null;

    /// <summary>The ink (0..1) of a plate at a pixel.</summary>
    public double InkAt(int channel, int x, int y) =>
        Plane(channel) is { } p && x >= 0 && y >= 0 && x < Width && y < Height ? p[y * Width + x] / 255.0 : 0;

    /// <summary>Every plate's ink at a pixel, in percent, in channel order (plates not rendered are omitted).</summary>
    public IReadOnlyList<(string Plate, double Percent)> Readout(int x, int y)
    {
        var result = new List<(string, double)>(_planes.Length);
        for (int c = 0; c < _planes.Length; c++)
            if (_planes[c] != null)
                result.Add((Model.Channels[c], Math.Round(InkAt(c, x, y) * 100, 1)));
        return result;
    }

    /// <summary>Total ink at a pixel over the plates in <paramref name="channels"/> (all when 0), in percent.</summary>
    public double TotalInkAt(int x, int y, ulong channels = 0)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        int sum = 0, i = y * Width + x;
        for (int c = 0; c < _planes.Length; c++)
            if (_planes[c] is { } p && (channels == 0 || (channels >> c & 1) != 0)) sum += p[i];
        return sum * 100.0 / 255;
    }

    /// <summary>Total ink coverage over the plates in <paramref name="channels"/> (all when 0) against a limit in percent.</summary>
    public PdfInkCoverage Coverage(double limitPercent = 300, ulong channels = 0)
    {
        int n = Width * Height;
        int limit = (int)Math.Floor(limitPercent * 255 / 100.0);
        int max = 0;
        long over = 0;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        var planes = Enumerable.Range(0, _planes.Length).Where(c => _planes[c] != null && (channels == 0 || (channels >> c & 1) != 0))
            .Select(c => _planes[c]!).ToArray();
        for (int i = 0; i < n; i++)
        {
            int sum = 0;
            foreach (var p in planes) sum += p[i];
            if (sum > max) max = sum;
            if (sum > limit)
            {
                over++;
                int x = i % Width, y = i / Width;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }
        return new PdfInkCoverage(Math.Round(max * 100.0 / 255, 1), limitPercent, over, n == 0 ? 0 : over / (double)n,
            over > 0 ? new PdfRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1) : null);
    }

    /// <summary>
    /// One plate as BGRA: black ink on white (<paramref name="tinted"/> false), or the plate's own colour.
    /// </summary>
    public byte[] PlateToBgra(int channel, bool tinted, PdfColor? appearance = null)
    {
        var bgra = new byte[Width * Height * 4];
        var plane = Plane(channel);
        var colour = appearance ?? DefaultAppearance(channel);
        for (int i = 0, p = 0; i < Width * Height; i++, p += 4)
        {
            double ink = plane == null ? 0 : plane[i] / 255.0;
            if (tinted)
            {
                bgra[p] = Byte(1 - ink * (1 - colour.B));
                bgra[p + 1] = Byte(1 - ink * (1 - colour.G));
                bgra[p + 2] = Byte(1 - ink * (1 - colour.R));
            }
            else
            {
                bgra[p] = bgra[p + 1] = bgra[p + 2] = Byte(1 - ink);
            }
            bgra[p + 3] = 255;
        }
        return bgra;
    }

    /// <summary>
    /// The visible plates mixed to an on-screen colour: the process plates through the engine's DeviceCMYK
    /// conversion, then each spot multiplied over them in its appearance colour (an approximation of how inks
    /// overprint; Acrobat mixes in Lab). Pixels whose total ink over the visible plates exceeds
    /// <paramref name="highlightOverPercent"/> are painted in <paramref name="highlight"/>.
    /// </summary>
    public byte[] CompositeToBgra(ulong visible, IReadOnlyList<PdfColor>? spotAppearances = null, double? highlightOverPercent = null, PdfColor? highlight = null)
    {
        int n = Width * Height;
        var cmyk = new byte[n * 4];
        for (int c = 0; c < 4; c++)
        {
            if ((visible >> c & 1) == 0 || _planes[c] is not { } plane) continue;
            for (int i = 0; i < n; i++) cmyk[i * 4 + c] = plane[i];
        }
        var bgra = PdfColorConversion.CmykToBgra(cmyk, n, inverted: false);
        for (int c = 4; c < _planes.Length; c++)
        {
            if ((visible >> c & 1) == 0 || _planes[c] is not { } plane) continue;
            var colour = spotAppearances != null && c - 4 < spotAppearances.Count ? spotAppearances[c - 4] : PdfColor.FromGray(0.5f);
            for (int i = 0, p = 0; i < n; i++, p += 4)
            {
                if (plane[i] == 0) continue;
                double ink = plane[i] / 255.0;
                bgra[p] = Byte(bgra[p] / 255.0 * (1 - ink * (1 - colour.B)));
                bgra[p + 1] = Byte(bgra[p + 1] / 255.0 * (1 - ink * (1 - colour.G)));
                bgra[p + 2] = Byte(bgra[p + 2] / 255.0 * (1 - ink * (1 - colour.R)));
            }
        }
        if (highlightOverPercent is double limitPercent)
        {
            var h = highlight ?? new PdfColor(0, 1, 0);
            int limit = (int)Math.Floor(limitPercent * 255 / 100.0);
            for (int i = 0, p = 0; i < n; i++, p += 4)
            {
                int sum = 0;
                for (int c = 0; c < _planes.Length; c++)
                    if ((visible >> c & 1) != 0 && _planes[c] is { } plane) sum += plane[i];
                if (sum <= limit) continue;
                bgra[p] = Byte(h.B);
                bgra[p + 1] = Byte(h.G);
                bgra[p + 2] = Byte(h.R);
            }
        }
        return bgra;
    }

    private PdfColor DefaultAppearance(int channel) => channel switch
    {
        0 => PdfInkModel.ShowCmyk(1, 0, 0, 0),
        1 => PdfInkModel.ShowCmyk(0, 1, 0, 0),
        2 => PdfInkModel.ShowCmyk(0, 0, 1, 0),
        3 => PdfInkModel.ShowCmyk(0, 0, 0, 1),
        _ => PdfColor.FromGray(0.5f),
    };

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}

/// <summary>
/// Separates pages into plates by drawing plate display lists (<see cref="PdfVectorDocument.GetPlateDisplayListAsync"/>)
/// with a rasterizer. Without overprint simulation three plates share one render (red, green and blue); with it,
/// each plate is drawn on its own, since an overprinting object keeps the backdrop of some plates only.
/// </summary>
public static class PdfSeparationRenderer
{
    public static async Task<PdfSeparation> RenderAsync(PdfVectorDocument document, int pageNumber, int width, int height,
        IPdfDisplayListRasterizer rasterizer, PdfSeparationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rasterizer);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        options ??= new PdfSeparationOptions();
        var model = options.Model ?? (await PdfColorantInventory.ReadAsync(document, [pageNumber], cancellationToken).ConfigureAwait(false)).CreateModel();
        var channels = (options.Channels ?? Enumerable.Range(0, model.ChannelCount).ToList()).Where(c => c >= 0 && c < model.ChannelCount).Distinct().OrderBy(c => c).ToList();
        var planes = new byte[]?[model.ChannelCount];
        bool fallback = false;

        var groups = options.SimulateOverprint
            ? channels.Select(c => (IReadOnlyList<int>)new[] { c }).ToList()
            : channels.Chunk(3).Select(g => (IReadOnlyList<int>)g).ToList();
        foreach (var group in groups)
        {
            var list = await document.GetPlateDisplayListAsync(pageNumber, new PdfPlateRequest(model, group, options.SimulateOverprint), cancellationToken)
                .ConfigureAwait(false);
            fallback |= list.HasFallback;
            if (group.Count > 1 && HasNonSeparableBlend(list))
            {
                // Hue, Saturation, Color and Luminosity mix the channels: draw these plates one by one.
                foreach (int c in group)
                {
                    var single = await document.GetPlateDisplayListAsync(pageNumber, new PdfPlateRequest(model, [c], false), cancellationToken).ConfigureAwait(false);
                    planes[c] = Extract(await rasterizer.RenderAsync(single, width, height, cancellationToken).ConfigureAwait(false), width, height, 0);
                }
                continue;
            }
            var bgra = await rasterizer.RenderAsync(list, width, height, cancellationToken).ConfigureAwait(false);
            for (int slot = 0; slot < group.Count; slot++)
                planes[group[slot]] = Extract(bgra, width, height, slot);
        }
        return new PdfSeparation(model, width, height, planes, options.SimulateOverprint, fallback);
    }

    /// <summary>Ink from one channel of BGRA pixels: slot 0 is red, 1 green, 2 blue; ink = 255 − value.</summary>
    private static byte[] Extract(byte[] bgra, int width, int height, int slot)
    {
        int offset = 2 - slot;
        var plane = new byte[width * height];
        for (int i = 0; i < plane.Length && i * 4 + offset < bgra.Length; i++)
            plane[i] = (byte)(255 - bgra[i * 4 + offset]);
        return plane;
    }

    private static bool HasNonSeparableBlend(IPdfDisplayList list)
    {
        foreach (var command in list.Commands)
            if (command is BeginCompositingGroup { Blend: >= PdfBlendMode.Hue })
                return true;
        return false;
    }

    /// <summary>
    /// A pixel rectangle of a page rendered at <paramref name="width"/> × <paramref name="height"/> (with its /Rotate)
    /// back in default user space (points, y up).
    /// </summary>
    public static PdfRect PixelsToPage(PdfRect pixels, PdfRect crop, int rotation, int width, int height)
    {
        rotation = ((rotation % 360) + 360) % 360;
        bool sideways = rotation is 90 or 270;
        double sx = width / (sideways ? crop.Height : crop.Width), sy = height / (sideways ? crop.Width : crop.Height);
        var corners = new[] { (pixels.X, pixels.Y), (pixels.X + pixels.Width, pixels.Y), (pixels.X, pixels.Y + pixels.Height), (pixels.X + pixels.Width, pixels.Y + pixels.Height) };
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (px, py) in corners)
        {
            double u = px / sx, v = py / sy; // rotated points, top-left origin
            // Undo the rotation: page points from the crop's top-left (x right, y down).
            var (x, y) = rotation switch
            {
                90 => (v, crop.Height - u),
                180 => (crop.Width - u, crop.Height - v),
                270 => (crop.Width - v, u),
                _ => (u, v),
            };
            double ux = crop.X + x, uy = crop.Y + crop.Height - y;
            minX = Math.Min(minX, ux); maxX = Math.Max(maxX, ux);
            minY = Math.Min(minY, uy); maxY = Math.Max(maxY, uy);
        }
        return new PdfRect(minX, minY, maxX - minX, maxY - minY);
    }
}
