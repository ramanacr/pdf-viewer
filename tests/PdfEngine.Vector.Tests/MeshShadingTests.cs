using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Function-based (type 1) and mesh shadings (types 4–7) rendered natively by Direct2D against
/// the PDFium oracle: free-form triangles with shared-edge flags, lattices, Coons and tensor
/// patches (including edge sharing), parametric meshes through a function, sub-byte data.
/// </summary>
public class MeshShadingTests
{
    private readonly ITestOutputHelper _output;
    public MeshShadingTests(ITestOutputHelper output) => _output = output;

    /// <summary>Big-endian bit packer with per-element byte alignment.</summary>
    private sealed class Bits
    {
        private readonly List<byte> _bytes = new();
        private int _bit;
        public Bits Put(long value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (_bit == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(1 << (7 - _bit));
                _bit = (_bit + 1) & 7;
            }
            return this;
        }
        public Bits Align() { _bit = 0; return this; }
        public byte[] ToArray() => _bytes.ToArray();
    }

    private async Task AssertMatches(string name, string shadingDict, byte[] data, double maxMean = 3.0, double maxBad = 0.02, bool isStream = true)
    {
        var b = new VectorPdfBuilder();
        int sh = isStream ? b.AddStream(shadingDict, data) : b.Add("<< " + shadingDict + " >>");
        b.AddPage("0.95 g 0 0 200 200 re f /Sh1 sh", $"<< /Shading << /Sh1 {sh} 0 R >> >>");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback, $"{name}: {string.Join(", ", list.FallbackTokens.Select(t => t.Reason + " " + t.Description))}");
        using var renderer = new Direct2DVectorRenderer();
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi }, null, null, false, CancellationToken.None);
            using var v = result.Page;
            Assert.Empty(result.Fallbacks);
            using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"{name} @{dpi}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= maxMean, $"{name} @{dpi}: mean {mean:F2} > {maxMean}");
            Assert.True(bad <= maxBad, $"{name} @{dpi}: {bad:P2} pixels off");
        }
    }

    private const string RgbDecode = "/Decode [0 200 0 200 0 1 0 1 0 1]";

    private static void Vertex(Bits w, int flag, int x, int y, int r, int g, int b, int flagBits = 8)
    {
        w.Put(flag, flagBits).Put(x, 8).Put(y, 8).Put(r, 8).Put(g, 8).Put(b, 8).Align();
    }

    [Fact]
    public Task FreeForm_WithSharedEdgeFlags()
    {
        var w = new Bits();
        // Coordinates 0..255 map to 0..200 pt.
        Vertex(w, 0, 20, 20, 255, 0, 0); Vertex(w, 0, 230, 30, 0, 255, 0); Vertex(w, 0, 120, 230, 0, 0, 255);
        Vertex(w, 1, 250, 240, 255, 255, 0); // (b, c, new)
        Vertex(w, 2, 10, 250, 0, 255, 255);  // (a, c, new)
        return AssertMatches("type4", "/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " + RgbDecode, w.ToArray());
    }

    [Fact]
    public Task FreeForm_ParametricThroughFunction()
    {
        var w = new Bits();
        void V(int f, int x, int y, int t) => w.Put(f, 8).Put(x, 8).Put(y, 8).Put(t, 8).Align();
        V(0, 10, 10, 0); V(0, 245, 20, 128); V(0, 128, 245, 255);
        return AssertMatches("type4-function", "/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " +
            "/Decode [0 200 0 200 0 1] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.9 0] /C1 [0.2 0 0.6] /N 2 >>", w.ToArray());
    }

    [Fact]
    public Task Lattice_ThreeByThree()
    {
        var w = new Bits();
        int[,] colors = { { 255, 0, 0 }, { 0, 255, 0 }, { 0, 0, 255 } };
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 3; col++)
            w.Put(20 + col * 100, 8).Put(20 + row * 100 + (col == 1 ? 15 : 0), 8)
             .Put(colors[(row + col) % 3, 0], 8).Put(colors[(row + col) % 3, 1], 8).Put(colors[(row + col) % 3, 2], 8).Align();
        return AssertMatches("type5", "/ShadingType 5 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /VerticesPerRow 3 " + RgbDecode, w.ToArray());
    }

    private static void Points(Bits w, params (int X, int Y)[] pts)
    {
        foreach (var (x, y) in pts) w.Put(x, 8).Put(y, 8);
    }

    private static void Colors(Bits w, params (int R, int G, int B)[] cs)
    {
        foreach (var (r, g, b) in cs) w.Put(r, 8).Put(g, 8).Put(b, 8);
    }

    [Fact]
    public Task Coons_WithCurvedEdges_AndASharedEdge()
    {
        var w = new Bits();
        // Patch 1, flag 0: 12 boundary points p00 p01 p02 p03 p13 p23 p33 p32 p31 p30 p20 p10.
        w.Put(0, 8);
        Points(w, (20, 20), (10, 60), (30, 100), (20, 140), (60, 160), (100, 130), (140, 140), (150, 100), (130, 60), (140, 20), (100, 40), (60, 10));
        Colors(w, (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0));
        w.Align();
        // Patch 2, flag 2: shares p33..p30 edge of patch 1; 8 new points and 2 colours.
        w.Put(2, 8);
        Points(w, (180, 150), (200, 120), (240, 140), (240, 100), (245, 60), (235, 20), (200, 10), (170, 30));
        Colors(w, (255, 0, 255), (0, 255, 255));
        w.Align();
        return AssertMatches("type6", "/ShadingType 6 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " + RgbDecode, w.ToArray());
    }

    [Fact]
    public Task TensorPatch_WithMovedInteriorPoints()
    {
        var w = new Bits();
        w.Put(0, 8);
        Points(w, (20, 20), (20, 90), (20, 160), (20, 230), (90, 230), (160, 230), (230, 230), (230, 160), (230, 90), (230, 20), (160, 20), (90, 20),
            (40, 60), (160, 200), (60, 150), (200, 60)); // p11 p12 p22 p21
        Colors(w, (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0));
        w.Align();
        return AssertMatches("type7", "/ShadingType 7 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " + RgbDecode, w.ToArray());
    }

    [Fact]
    public Task SubByteData_FourBitComponents()
    {
        var w = new Bits();
        void V(int f, int x, int y, int r, int g, int b) => w.Put(f, 2).Put(x, 8).Put(y, 8).Put(r, 4).Put(g, 4).Put(b, 4).Align();
        V(0, 20, 20, 15, 0, 0); V(0, 230, 40, 0, 15, 0); V(0, 100, 230, 0, 0, 15);
        return AssertMatches("type4-4bit", "/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 4 /BitsPerFlag 2 " + RgbDecode, w.ToArray());
    }

    [Fact]
    public async Task FunctionBased_Type1()
    {
        // f(x, y) = (x, y, x·y) over the unit square, mapped to 160 pt at (20, 20).
        var b = new VectorPdfBuilder();
        int fn = b.AddStream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ 2 copy mul }");
        int sh = b.Add($"<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [160 0 0 160 20 20] /Function {fn} 0 R >>");
        b.AddPage("0.95 g 0 0 200 200 re f /Sh1 sh", $"<< /Shading << /Sh1 {sh} 0 R >> >>");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var renderer = new Direct2DVectorRenderer();
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        foreach (double dpi in new[] { 72.0, 144.0 })
        {
            using var v = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi });
            using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = dpi });
            var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
            _output.WriteLine($"type1 @{dpi}: mean={mean:F2} bad={bad:P2}");
            Assert.True(mean <= 3.0 && bad <= 0.02, $"type1 @{dpi}: mean {mean:F2} bad {bad:P2}");
        }
    }

    [Fact]
    public async Task WpfBackend_ClassifiesMeshShadings()
    {
        var w = new Bits();
        Vertex(w, 0, 20, 20, 255, 0, 0); Vertex(w, 0, 230, 30, 0, 255, 0); Vertex(w, 0, 120, 230, 0, 0, 255);
        var b = new VectorPdfBuilder();
        int sh = b.AddStream("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " + RgbDecode, w.ToArray());
        b.AddPage("/Sh1 sh", $"<< /Shading << /Sh1 {sh} 0 R >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var wpf = new WindowsVectorRenderer();
        var token = Assert.Single(await wpf.AnalyzeAsync(list));
        Assert.Equal(PdfFallbackReason.Shading, token.Reason);
    }
}
