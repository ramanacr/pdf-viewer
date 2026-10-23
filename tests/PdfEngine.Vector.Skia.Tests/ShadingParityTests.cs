using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Smooth shadings against the Direct2D backend: axial and radial ones with every combination
/// of /Extend (milestone 1), general two-circle radials, function-based and mesh shadings
/// (milestone 3).
/// </summary>
public class ShadingParityTests
{
    private readonly Parity _parity;
    public ShadingParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    private const string Function = "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.8 0] /C1 [0.1 0 0.7] /N 1 >>";

    private static byte[] ShadingPage(string shading, string content = "0.9 g 0 0 200 200 re f /Sh1 sh")
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, "<< /Shading << /Sh1 << " + shading + " >> >> >>");
        return b.Build();
    }

    [Theory]
    [InlineData("both", "[true true]")]
    [InlineData("none", "[false false]")]
    [InlineData("start", "[true false]")]
    [InlineData("end", "[false true]")]
    public Task Axial_MatchesDirect2D(string name, string extend) =>
        _parity.AssertMatches("axial-" + name, ShadingPage($"/ShadingType 2 /ColorSpace /DeviceRGB /Coords [40 30 160 170] /Extend {extend} {Function}"), 1.5, 0.01);

    [Fact]
    public Task Axial_Stitched_ClippedAndTransformed() =>
        _parity.AssertMatches("axial-stitched", ShadingPage(
            "/ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 0] /Extend [true true] /Function << /FunctionType 3 /Domain [0 1] /Bounds [0.3 0.7] /Encode [0 1 0 1 0 1] " +
            "/Functions [<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [1 1 1] /N 1 >>] >>",
            "q 100 100 m 190 100 190 190 100 190 c 10 190 10 100 100 100 c h W n 140 60 -60 140 40 20 cm /Sh1 sh Q"), 1.5, 0.01);

    [Theory]
    [InlineData("focal", "[70 80 0 100 100 80]", "[true true]")]
    [InlineData("focal-no-extend", "[70 80 0 100 100 80]", "[false false]")]
    [InlineData("concentric", "[100 100 0 100 100 90]", "[true true]")]
    public Task Radial_MatchesDirect2D(string name, string coords, string extend) =>
        _parity.AssertMatches("radial-" + name, ShadingPage($"/ShadingType 3 /ColorSpace /DeviceRGB /Coords {coords} /Extend {extend} {Function}"), 1.5, 0.01);

    [Theory]
    [InlineData("concentric-hole", "[100 100 40 100 100 90]", "[false false]")]
    [InlineData("concentric-hole-extend-end", "[100 100 40 100 100 70]", "[false true]")]
    [InlineData("cone", "[60 60 10 140 140 50]", "[false false]")]
    [InlineData("cone-extended", "[60 100 20 150 100 40]", "[true true]")]
    [InlineData("outside-start", "[30 30 5 120 120 60]", "[true false]")]
    public Task TwoCircleRadial_MatchesDirect2D(string name, string coords, string extend) =>
        _parity.AssertMatches("radial2-" + name, ShadingPage($"/ShadingType 3 /ColorSpace /DeviceRGB /Coords {coords} /Extend {extend} {Function}"), 1.5, 0.01);

    [Fact]
    public Task ShadingAsFillPattern() =>
        _parity.AssertMatches("shading-pattern", new Func<byte[]>(() =>
        {
            var b = new VectorPdfBuilder();
            int sh = b.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] {Function} /Extend [true true] >>");
            int pat = b.Add($"<< /Type /Pattern /PatternType 2 /Shading {sh} 0 R >>");
            b.AddPage("/Pattern cs /P1 scn 20 20 m 180 40 l 100 180 l h f", $"<< /Pattern << /P1 {pat} 0 R >> >>");
            return b.Build();
        })(), 1.5, 0.01);

    [Fact]
    public Task FunctionBased_Type1()
    {
        // f(x, y) = (x, y, x·y) over the unit square, mapped to 160 pt at (20, 20).
        var b = new VectorPdfBuilder();
        int fn = b.AddStream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", "{ 2 copy mul }");
        int sh = b.Add($"<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1] /Matrix [160 0 0 160 20 20] /Function {fn} 0 R >>");
        b.AddPage("0.95 g 0 0 200 200 re f /Sh1 sh", $"<< /Shading << /Sh1 {sh} 0 R >> >>");
        return _parity.AssertMatches("function-type1", b.Build(), 1.0, 0.005);
    }

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

    private static byte[] MeshPage(string dict, byte[] data)
    {
        var b = new VectorPdfBuilder();
        int sh = b.AddStream(dict, data);
        b.AddPage("0.95 g 0 0 200 200 re f /Sh1 sh", $"<< /Shading << /Sh1 {sh} 0 R >> >>");
        return b.Build();
    }

    [Fact]
    public Task FreeFormMesh_Type4()
    {
        var w = new Bits();
        void V(int f, int x, int y, int r, int g, int b) => w.Put(f, 8).Put(x, 8).Put(y, 8).Put(r, 8).Put(g, 8).Put(b, 8);
        V(0, 20, 20, 255, 0, 0); V(0, 230, 40, 0, 255, 0); V(0, 100, 230, 0, 0, 255);
        V(1, 240, 240, 255, 255, 0);
        return _parity.AssertMatches("mesh-type4", MeshPage("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " +
            "/Decode [0 200 0 200 0 1 0 1 0 1]", w.ToArray()), 1.5, 0.01);
    }

    [Fact]
    public Task CoonsPatch_Type6()
    {
        // One patch: 12 control points (a bulged square) and four corner colours.
        var w = new Bits();
        w.Put(0, 8);
        int[] pts = { 20, 20, 20, 80, 20, 140, 20, 180, 80, 200, 140, 160, 180, 180, 160, 120, 190, 60, 180, 20, 120, 40, 60, 0 };
        foreach (int p in pts) w.Put(p, 8);
        foreach (var (r, g, b) in new[] { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0) }) w.Put(r, 8).Put(g, 8).Put(b, 8);
        return _parity.AssertMatches("mesh-type6", MeshPage("/ShadingType 6 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 " +
            "/Decode [0 200 0 200 0 1 0 1 0 1]", w.ToArray()), 1.5, 0.01);
    }
}
