using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.WIC;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// Device-independent and device-dependent Direct2D/DirectWrite resources (04 "Device loss",
/// 08 "Caching model"). Device-dependent objects (context, bitmaps, brushes) are dropped and
/// recreated on device loss; document state and display lists are untouched.
/// </summary>
internal sealed class D2DResources : IDisposable
{
    public ID2D1Factory1 Factory { get; }
    public IDWriteFactory5 DWrite { get; }

    /// <summary>
    /// Document text is filled as glyph outlines (exact geometric coverage, no hinting, linear
    /// blending), so its weight is the same at every zoom. DirectWrite's default glyph rasterizer
    /// is tuned for UI text: on a real-world text page at 144 dpi it drew 13 % more ink than
    /// PDFium (outlines: 3.6 %), although at 600 dpi the two agree within 0.2 % - small document
    /// text looked bold. Same speed on text-heavy pages (`vectorpdf tiles`).
    /// </summary>
    public IDWriteRenderingParams? DocumentTextParams { get; }
    public IWICImagingFactory Wic { get; }
    private readonly IDWriteInMemoryFontFileLoader _fontLoader;

    public ID3D11Device? D3D { get; private set; }
    public ID2D1Device? Device { get; private set; }
    public ID2D1DeviceContext? Context { get; private set; }

    /// <summary>True when rendering on WARP (software) because no hardware device was available.</summary>
    public bool IsSoftware { get; private set; }

    private readonly Dictionary<string, IDWriteFontFace?> _embeddedFaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDWriteFontFace?> _systemFaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ID2D1StrokeStyle1> _strokeStyles = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, ID2D1Bitmap1 Bitmap, long Bytes)> _bitmapLru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ID2D1Bitmap1 Bitmap, long Bytes)>> _bitmaps = new(StringComparer.Ordinal);
    private readonly long _maxBitmapBytes;
    private long _bitmapBytes;
    private IDWriteFontCollection? _systemCollection;

    private readonly bool _forceSoftware;

    public D2DResources(long maxBitmapBytes, bool forceSoftware = false)
    {
        _maxBitmapBytes = maxBitmapBytes;
        _forceSoftware = forceSoftware;
        Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded, DebugLevel.None);
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory5>(Vortice.DirectWrite.FactoryType.Shared);
        _fontLoader = DWrite.CreateInMemoryFontFileLoader();
        DWrite.RegisterFontFileLoader(_fontLoader);
        Wic = new IWICImagingFactory();
        DocumentTextParams = DWrite.CreateCustomRenderingParams(1.0f, 0f, 0f, 0f, PixelGeometry.Flat, RenderingMode1.Outline, GridFitMode.Disabled);
        CreateDevice();
    }

    /// <summary>(Re)creates the D3D11 device (hardware, else WARP) and a D2D context on it.</summary>
    public void CreateDevice()
    {
        ReleaseDeviceResources();
        var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0, Vortice.Direct3D.FeatureLevel.Level_9_3 };
        ID3D11Device? device = null;
        foreach (var driver in _forceSoftware ? new[] { DriverType.Warp } : new[] { DriverType.Hardware, DriverType.Warp })
        {
            if (D3D11.D3D11CreateDevice((IDXGIAdapter?)null!, driver, DeviceCreationFlags.BgraSupport, levels, out device).Success && device != null)
            {
                IsSoftware = driver == DriverType.Warp;
                break;
            }
        }
        D3D = device ?? throw new InvalidOperationException("No Direct3D 11 device (hardware or WARP) could be created.");
        using var dxgi = D3D.QueryInterface<IDXGIDevice>();
        Device = Factory.CreateDevice(dxgi);
        Context = Device.CreateDeviceContext(DeviceContextOptions.None);
    }

    private void ReleaseDeviceResources()
    {
        foreach (var (_, bitmap, _) in _bitmapLru) bitmap.Dispose();
        _bitmapLru.Clear();
        _bitmaps.Clear();
        _bitmapBytes = 0;
        Context?.Dispose();
        Device?.Dispose();
        D3D?.Dispose();
        Context = null;
        Device = null;
        D3D = null;
    }

    // ------------------------------------------------------------------ fonts (device independent)

    /// <summary>DirectWrite face for an embedded program, loaded from memory; null when rejected.</summary>
    public IDWriteFontFace? GetEmbeddedFace(PdfFontFace face)
    {
        if (face.ProgramData.IsEmpty || face.Format is not (PdfFontProgramFormat.TrueType or PdfFontProgramFormat.OpenTypeCff))
            return null;
        if (_embeddedFaces.TryGetValue(face.Key, out var cached))
            return cached;

        IDWriteFontFace? result = null;
        IDWriteFontFile? file = null;
        var data = face.ProgramData.ToArray();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            // ownerObject null: DirectWrite copies the bytes, so the pin can be released afterwards.
            file = _fontLoader.CreateInMemoryFontFileReference(DWrite, handle.AddrOfPinnedObject(), (uint)data.Length, null);
            var type = face.Format == PdfFontProgramFormat.OpenTypeCff ? FontFaceType.Cff : FontFaceType.Truetype;
            result = DWrite.CreateFontFace(type, new[] { file }, 0, FontSimulations.None);
            _ = result!.GlyphCount; // force validation now
        }
        catch (SharpGenException)
        {
            result?.Dispose();
            result = null;
        }
        finally
        {
            handle.Free();
            file?.Dispose();
        }
        _embeddedFaces[face.Key] = result;
        return result;
    }

    /// <summary>Installed family face, e.g. a metric-compatible substitute; null when not installed.</summary>
    public IDWriteFontFace? GetSystemFace(string family, bool bold, bool italic)
    {
        string key = $"{family}|{bold}|{italic}";
        if (_systemFaces.TryGetValue(key, out var cached))
            return cached;
        IDWriteFontFace? result = null;
        try
        {
            _systemCollection ??= DWrite.GetSystemFontCollection(false);
            if (_systemCollection.FindFamilyName(family, out uint index))
            {
                using var fam = _systemCollection.GetFontFamily(index);
                using var font = fam.GetFirstMatchingFont(bold ? FontWeight.Bold : FontWeight.Normal, FontStretch.Normal, italic ? FontStyle.Italic : FontStyle.Normal);
                result = font.CreateFontFace();
            }
        }
        catch (SharpGenException)
        {
            result = null;
        }
        _systemFaces[key] = result;
        return result;
    }

    // ------------------------------------------------------------------ device resources

    public ID2D1StrokeStyle1 StrokeStyle(StrokeStyleProperties1 props, float[]? dashes)
    {
        string key = $"{props.StartCap}|{props.LineJoin}|{props.MiterLimit}|{props.DashStyle}|{props.DashOffset}|{props.TransformType}|{(dashes == null ? "" : string.Join(",", dashes))}";
        if (_strokeStyles.TryGetValue(key, out var style))
            return style;
        style = dashes is { Length: > 0 } ? Factory.CreateStrokeStyle(props, dashes) : Factory.CreateStrokeStyle(props);
        _strokeStyles[key] = style;
        return style;
    }

    public ID2D1Bitmap1? GetBitmap(string key)
    {
        if (_bitmaps.TryGetValue(key, out var node))
        {
            _bitmapLru.Remove(node);
            _bitmapLru.AddFirst(node);
            return node.Value.Bitmap;
        }
        return null;
    }

    public void AddBitmap(string key, ID2D1Bitmap1 bitmap, long bytes)
    {
        if (_bitmaps.ContainsKey(key))
            return;
        _bitmaps[key] = _bitmapLru.AddFirst((key, bitmap, bytes));
        _bitmapBytes += bytes;
        while (_bitmapBytes > _maxBitmapBytes && _bitmapLru.Count > 1)
        {
            var last = _bitmapLru.Last!;
            _bitmapLru.RemoveLast();
            _bitmaps.Remove(last.Value.Key);
            _bitmapBytes -= last.Value.Bytes;
            last.Value.Bitmap.Dispose();
        }
    }

    public long CachedBitmapBytes => _bitmapBytes;

    public void Dispose()
    {
        ReleaseDeviceResources();
        foreach (var s in _strokeStyles.Values) s.Dispose();
        foreach (var f in _embeddedFaces.Values) f?.Dispose();
        foreach (var f in _systemFaces.Values) f?.Dispose();
        _systemCollection?.Dispose();
        DWrite.UnregisterFontFileLoader(_fontLoader);
        _fontLoader.Dispose();
        DocumentTextParams?.Dispose();
        DWrite.Dispose();
        Wic.Dispose();
        Factory.Dispose();
    }
}
