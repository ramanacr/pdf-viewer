using System.Numerics;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Glyphs rasterized once each (by exact area, <see cref="GlyphRasterizer"/>) into one bitmap and
/// drawn from it as sprites. A text page repeats a few dozen glyphs thousands of times; drawing every
/// glyph run from its outlines made Direct2D tessellate every glyph of every run again, the main
/// cost of text pages. PDFium draws cached FreeType glyph bitmaps too, with exact-area coverage and
/// whole-pixel baselines, and the glyphs drawn this way are also much closer to its pages. Device
/// dependent; shared by every page rendered on the device.
/// </summary>
internal sealed class GlyphAtlas : IDisposable
{
    public const int Size = 2048;
    /// <summary>
    /// Glyphs larger than this (device pixels, either side) are drawn from their outlines: past body
    /// text sizes a glyph is rarely drawn twice in a tile (deep zoom), and rasterizing it costs more
    /// than Direct2D drawing it once.
    /// </summary>
    public const int MaxGlyphPixels = 64;

    /// <summary>Glyph positions are kept to a quarter of a device pixel across.</summary>
    public const int SubpixelSteps = 4;

    /// <summary>A glyph's cell: where it is in the bitmap and where its top-left corner sits relative to its origin.</summary>
    public readonly record struct Cell(RawRect Source, int Dx, int Dy, bool Empty);

    /// <summary>A face at one size and orientation: the face and its glyph space → device linear map.</summary>
    private readonly record struct Slot(IntPtr Face, float EmSize, float M11, float M12, float M21, float M22);

    private readonly D2DResources _res;
    private readonly ID2D1Device _device;
    // Per face, size and orientation, the cell of each glyph drawn so far (null: drawn from its
    // outlines instead), keyed by glyph index and subpixel bin (glyph << 4 | bin).
    private readonly Dictionary<Slot, Dictionary<int, Cell?>> _cells = new();
    private readonly GlyphRasterizer _rasterizer = new();
    private byte[] _pixels = new byte[64 * 64 * 4];
    private readonly ushort[] _glyph = new ushort[1];
    private readonly float[] _advance = new float[1];
    private readonly GlyphOffset[] _offset = new GlyphOffset[1];
    private ID2D1DeviceContext? _clearContext;
    private int _x = 1, _y = 1, _rowHeight;
    private bool _full;

    public ID2D1Bitmap1 Bitmap { get; }

    public GlyphAtlas(D2DResources res, ID2D1Device device, ID2D1DeviceContext pageContext)
    {
        _res = res;
        _device = device;
        Bitmap = pageContext.CreateBitmap(new SizeI(Size, Size), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        Clear();
    }

    /// <summary>
    /// A glyph origin's whole pixel and the bin of its fraction across. Down, the origin is rounded
    /// to the nearest pixel, as PDFium places its glyphs: every glyph of a line then sits on the same
    /// pixel row, which matched PDFium's pages far better than keeping the fraction (mean difference
    /// 5.13 against 3.72 on a 120-file corpus sample); rounding across as well was worse (4.88).
    /// </summary>
    public static (int X, int Y, byte Bin) Snap(Vector2 p)
    {
        int ix = (int)MathF.Floor(p.X);
        int iy = (int)MathF.Round(p.Y, MidpointRounding.AwayFromZero);
        int bx = (int)MathF.Round((p.X - ix) * SubpixelSteps, MidpointRounding.AwayFromZero);
        if (bx == SubpixelSteps) { ix++; bx = 0; }
        return (ix, iy, (byte)bx);
    }

    /// <summary>True when the device the atlas lives on is still the renderer's device.</summary>
    public bool IsFor(ID2D1Device? device) => ReferenceEquals(_device, device);

    /// <summary>Empties the atlas. Only between renders: sprites drawn from it must have been flushed.</summary>
    public void Clear()
    {
        _res.Stages.AtlasClears++;
        _cells.Clear();
        _x = _y = 1;
        _rowHeight = 0;
        _full = false;
        _clearContext ??= _device.CreateDeviceContext(DeviceContextOptions.None);
        _clearContext.Target = Bitmap;
        _clearContext.BeginDraw();
        _clearContext.Clear(new Color4(0, 0, 0, 0));
        _clearContext.EndDraw();
        _clearContext.Target = null;
    }

    /// <summary>The atlas is nearly full (or filled up): it is emptied before the next render.</summary>
    public bool NeedsClearing => _full || _y > Size * 7 / 8;

    /// <summary>
    /// The cells of <paramref name="glyphs"/> at their subpixel <paramref name="bins"/> and the glyph
    /// space → device map <paramref name="linear"/> (no translation), rasterizing the ones not seen
    /// before; false when one cannot be placed (too large, or the atlas is full), and the run is
    /// then drawn from its outlines.
    /// </summary>
    public bool TryGetCells(IDWriteFontFace face, float emSize, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<byte> bins, Matrix3x2 linear, Span<Cell> cells)
    {
        var slot = new Slot(face.NativePointer, emSize, linear.M11, linear.M12, linear.M21, linear.M22);
        if (!_cells.TryGetValue(slot, out var known))
            _cells[slot] = known = new Dictionary<int, Cell?>();
        for (int i = 0; i < glyphs.Length; i++)
        {
            int key = glyphs[i] << 4 | bins[i];
            if (!known.TryGetValue(key, out var found))
            {
                if (_full)
                    return false;
                known[key] = found = Place(face, emSize, glyphs[i], bins[i], linear);
            }
            if (found is not { } cell)
                return false;
            cells[i] = cell;
        }
        return true;
    }

    private Cell? Place(IDWriteFontFace face, float emSize, ushort glyph, byte bin, Matrix3x2 linear)
    {
        // The glyph is drawn this far right of a whole pixel (the bin's subpixel position).
        float sx = bin / (float)SubpixelSteps;
        _glyph[0] = glyph;
        _rasterizer.Measure(linear);
        face.GetGlyphRunOutline(emSize, _glyph, _advance, _offset, false, false, _rasterizer);
        if (_rasterizer.MeasuredBounds is not { } bounds)
            return new Cell(default, 0, 0, Empty: true); // a space: nothing to draw
        // A pixel of margin all round: the outline's control points bound it, antialiasing reaches
        // into the next pixel.
        int left = (int)MathF.Floor(bounds.Left + sx) - 1, top = (int)MathF.Floor(bounds.Top) - 1;
        int w = (int)MathF.Ceiling(bounds.Right + sx) + 1 - left, h = (int)MathF.Ceiling(bounds.Bottom) + 1 - top;
        if (w > MaxGlyphPixels || h > MaxGlyphPixels)
            return null;

        // Shelf packing: left to right, rows as tall as their tallest glyph.
        if (_x + w + 1 > Size)
        {
            _x = 1;
            _y += _rowHeight + 1;
            _rowHeight = 0;
        }
        if (_y + h + 1 > Size)
        {
            _full = true;
            return null;
        }
        int x = _x, y = _y;
        _x += w + 1;
        _rowHeight = Math.Max(_rowHeight, h);

        _res.Stages.GlyphsRasterized++;
        // The glyph origin at its subpixel position in the cell; the cell is drawn at a whole pixel.
        _rasterizer.Reset(w, h, linear * Matrix3x2.CreateTranslation(sx - left, -top));
        face.GetGlyphRunOutline(emSize, _glyph, _advance, _offset, false, false, _rasterizer);
        int pitch = w * 4;
        if (_pixels.Length < pitch * h) _pixels = new byte[Math.Max(pitch * h, _pixels.Length * 2)];
        _rasterizer.WriteBgra(_pixels, pitch);
        Bitmap.CopyFromMemory(new System.Drawing.Rectangle(x, y, w, h), _pixels, (uint)pitch);
        return new Cell(new RawRect(x, y, x + w, y + h), left, top, Empty: false);
    }

    public void Dispose()
    {
        _rasterizer.Dispose();
        _clearContext?.Dispose();
        Bitmap.Dispose();
    }
}
