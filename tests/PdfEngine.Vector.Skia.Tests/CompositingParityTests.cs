using System.Globalization;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Milestone 3, transparency and patterns: the 16 blend modes, group alpha, luminosity and alpha
/// soft masks (with backdrop colour and transfer function), isolated, non-isolated and knockout
/// groups, and tiling patterns, against the Direct2D backend.
/// </summary>
public class CompositingParityTests
{
    private readonly Parity _parity;
    public CompositingParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    /// <summary>Coloured strips under every fixture, so blends and masks have a backdrop to show.</summary>
    private const string Backdrop =
        "1 0 0 rg 0 0 50 200 re f 0 1 0 rg 50 0 50 200 re f 0 0 1 rg 100 0 50 200 re f 0.5 0.5 0.5 rg 150 0 50 200 re f " +
        "1 1 0 rg 0 150 200 50 re f ";

    public static TheoryData<string> BlendModes => new()
    {
        "Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn",
        "HardLight", "SoftLight", "Difference", "Exclusion", "Hue", "Saturation", "Color", "Luminosity",
    };

    [Theory]
    [MemberData(nameof(BlendModes))]
    public Task BlendMode_OnPaths(string mode)
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "/G1 gs 0.9 0.5 0.2 rg 20 20 160 160 re f 0.2 0.3 0.8 rg 60 60 80 120 re f",
            $"<< /ExtGState << /G1 << /BM /{mode} >> >> >>");
        return _parity.AssertMatches("blend-" + mode, b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task BlendMode_WithAlphaStrokeAndClip()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(Backdrop + "q 100 100 m 180 100 180 180 100 180 c 20 180 20 100 100 100 c h W n " +
                  "/G1 gs 0.8 0.2 0.6 rg 0 0 200 200 re f 0 0 0 RG 8 w 10 10 m 190 190 l S Q",
            "<< /ExtGState << /G1 << /BM /Multiply /ca 0.7 /CA 0.5 >> >> >>");
        return _parity.AssertMatches("blend+clip+alpha", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task LuminositySoftMask()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>",
            "1 g 0 0 70 200 re f 0.5 g 70 0 60 200 re f 0.2 g 130 0 70 100 re f");
        b.AddPage(Backdrop + "/M1 gs 0 0 0.6 rg 10 10 180 180 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >> >> >>");
        return _parity.AssertMatches("luminosity", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task LuminositySoftMask_WithBackdropAndTransfer()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [40 40 160 160] /Group << /S /Transparency /CS /DeviceRGB >>",
            "0 0 0 rg 40 40 60 120 re f 1 1 1 rg 100 40 60 120 re f");
        b.AddPage(Backdrop + "/M1 gs 0.1 0.6 0.1 rg 0 0 200 200 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R /BC [1 1 1] " +
            "/TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> >> >> >> >>");
        return _parity.AssertMatches("luminosity+BC+TR", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task LuminositySoftMask_FromAGradient()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >> " +
                               "/Resources << /Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] " +
                               "/Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Extend [true true] >> >> >>",
            "/Sh sh");
        b.AddPage(Backdrop + "/M1 gs 0.6 0 0.6 rg 0 0 200 200 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >> >> >>");
        return _parity.AssertMatches("luminosity-gradient", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task AlphaSoftMask()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> /Resources << /ExtGState << /H << /ca 0.4 >> >> >>",
            "0 g 20 20 80 160 re f /H gs 100 20 80 160 re f");
        b.AddPage(Backdrop + "/M1 gs 0.9 0.1 0.9 rg 0 0 200 200 re f",
            $"<< /ExtGState << /M1 << /SMask << /Type /Mask /S /Alpha /G {mask} 0 R >> >> >> >>");
        return _parity.AssertMatches("alpha-mask", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task SoftMaskOnAnImage()
    {
        var b = new VectorPdfBuilder();
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Group << /S /Transparency /CS /DeviceGray >>",
            "1 g 0 0 0.5 1 re f 0.3 g 0.5 0 0.5 1 re f");
        byte[] pixels = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 };
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", pixels);
        b.AddPage(Backdrop + "q 160 0 0 160 20 20 cm /M1 gs /Im1 Do Q",
            $"<< /XObject << /Im1 {image} 0 R >> /ExtGState << /M1 << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >> >> >>");
        return _parity.AssertMatches("smask-image", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task IsolatedGroup_WithBlendAndAlpha()
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >>",
            "0.9 0.4 0.1 rg 20 20 120 120 re f 0.1 0.4 0.9 rg 60 60 120 120 re f");
        b.AddPage(Backdrop + "/G1 gs /Fm1 Do",
            $"<< /XObject << /Fm1 {form} 0 R >> /ExtGState << /G1 << /BM /Screen /ca 0.8 >> >> >>");
        return _parity.AssertMatches("group-screen", b.Build(), 1.0, 0.01);
    }

    [Fact]
    public Task BlendInsideAnAlphaGroup()
    {
        // An opacity group containing a blended object is composited offscreen as a whole.
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> /Resources << /ExtGState << /B << /BM /Multiply >> >> >>",
            "0.9 0.6 0.1 rg 20 20 120 120 re f /B gs 0.2 0.5 0.9 rg 60 60 120 120 re f");
        b.AddPage(Backdrop + "/A gs /Fm1 Do",
            $"<< /XObject << /Fm1 {form} 0 R >> /ExtGState << /A << /ca 0.6 >> >> >>");
        return _parity.AssertMatches("blend-in-alpha-group", b.Build(), 1.0, 0.01);
    }

    /// <summary>
    /// An isolated group (at 80 % opacity) holding a 50 % green square and a non-isolated group
    /// whose pink rectangle multiplies: the non-isolated group's backdrop is the half-transparent green.
    /// </summary>
    [Theory]
    [InlineData("opaque", "", "")]
    [InlineData("translucent-inner", "/ca 0.6", "")]
    [InlineData("masked-inner", "", "mask")]
    public Task NonIsolatedGroup_InsideAnIsolatedOne(string name, string pinkAlpha, string innerMask)
    {
        var b = new VectorPdfBuilder();
        int inner = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >> " +
                                $"/Resources << /ExtGState << /M << /BM /Multiply {pinkAlpha} >> >> >>",
            "/M gs 0.9 0.3 0.6 rg 60 60 120 110 re f");
        string innerDo = "/ca 0.7";
        if (innerMask == "mask")
        {
            int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>",
                "1 g 0 0 120 200 re f 0.4 g 120 0 80 200 re f");
            innerDo = $"/SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >>";
        }
        int outer = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> " +
                                $"/Resources << /XObject << /N {inner} 0 R >> /ExtGState << /T << /ca 0.5 >> /D << {innerDo} >> >> >>",
            "/T gs 0.2 0.8 0.3 rg 30 30 100 100 re f /D gs /N Do");
        b.AddPage(Backdrop + "/A gs /O Do", $"<< /XObject << /O {outer} 0 R >> /ExtGState << /A << /ca 0.8 >> >> >>");
        return _parity.AssertMatches("non-isolated-" + name, b.Build(), 1.0, 0.01);
    }

    /// <summary>A knockout group of two half-transparent shapes, orange then blue.</summary>
    private static byte[] Knockout(string group, string blueBlend)
    {
        var b = new VectorPdfBuilder();
        int k = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true {group} >> " +
                            $"/Resources << /ExtGState << /H << /ca 0.5 >> /B << /ca 0.5 {blueBlend} >> >> >>",
            "/H gs 0.9 0.4 0.1 rg 60 40 80 80 re f /B gs 0.1 0.4 0.9 rg 100 80 80 80 re f");
        b.AddPage(Backdrop + "/K Do", $"<< /XObject << /K {k} 0 R >> >>");
        return b.Build();
    }

    [Theory]
    [InlineData("isolated", "/I true", "")]
    [InlineData("non-isolated", "", "")]
    [InlineData("non-isolated-multiply", "", "/BM /Multiply")]
    [InlineData("isolated-screen", "/I true", "/BM /Screen")]
    public Task KnockoutGroup(string name, string group, string blueBlend) =>
        _parity.AssertMatches("knockout-" + name, Knockout(group, blueBlend), 1.0, 0.01);

    [Fact]
    public Task KnockoutGroup_WithSoftMaskAndText()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        int mask = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>",
            "1 g 0 0 200 100 re f 0.5 g 0 100 200 100 re f");
        int k = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true /I true >> " +
                            $"/Resources << /Font << /F1 {helv} 0 R >> /ExtGState << /H << /ca 0.6 >> >> >>",
            "/H gs 0.1 0.6 0.9 rg 20 20 160 160 re f 0.9 0.2 0.1 rg BT /F1 48 Tf 30 80 Td (KO) Tj ET");
        b.AddPage(Backdrop + "/S gs /K Do",
            $"<< /XObject << /K {k} 0 R >> /ExtGState << /S << /SMask << /Type /Mask /S /Luminosity /G {mask} 0 R >> >> >> >>");
        return _parity.AssertMatches("knockout+mask+text", b.Build(), 1.5, 0.01);
    }

    [Fact]
    public Task KnockoutGroup_OverTheElementLimit_IsDrawnAsAnOrdinaryGroup()
    {
        var b = new VectorPdfBuilder();
        var content = new System.Text.StringBuilder("/H gs ");
        for (int i = 0; i <= SkiaReplayer.MaxKnockoutElements; i++)
            content.Append(CultureInfo.InvariantCulture, $"{i % 7 / 7.0:0.##} 0.4 0.6 rg {i % 150} {i % 120} 40 40 re f ");
        int k = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /K true >> " +
                            "/Resources << /ExtGState << /H << /ca 0.5 >> >> >>", content.ToString());
        b.AddPage(Backdrop + "/K Do", $"<< /XObject << /K {k} 0 R >> >>");
        return _parity.AssertMatches("knockout-over-limit", b.Build(), 1.0, 0.01, new[] { 72.0 });
    }

    private static byte[] TilingPage(string patternDict, string cell, string content, string pageResources = "")
    {
        var b = new VectorPdfBuilder();
        int pat = b.AddStream("/Type /Pattern /PatternType 1 " + patternDict, cell);
        b.AddPage(content, $"<< /Pattern << /P1 {pat} 0 R >> {pageResources} >>");
        return b.Build();
    }

    [Fact]
    public Task TilingPattern_Coloured() =>
        _parity.AssertMatches("tiling-coloured", TilingPage("/PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >>",
            "1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f 0 g 1 w 0 0 m 20 20 l S", "/Pattern cs /P1 scn 10 10 180 180 re f"), 1.5, 0.02);

    [Fact]
    public Task TilingPattern_Uncoloured() =>
        _parity.AssertMatches("tiling-uncoloured", TilingPage("/PaintType 2 /TilingType 1 /BBox [0 0 12 12] /XStep 12 /YStep 12 /Resources << >>",
            "6 6 m 12 6 l 6 12 l h f", "/CS0 cs 0.1 0.6 0.3 /P1 scn 20 20 160 160 re f", "/ColorSpace << /CS0 [/Pattern /DeviceRGB] >>"), 1.5, 0.02);

    [Fact]
    public Task TilingPattern_TransformedAndOverlapping() =>
        _parity.AssertMatches("tiling-transformed", TilingPage(
            "/PaintType 1 /TilingType 1 /BBox [-5 -5 25 25] /XStep 20 /YStep 20 /Matrix [0.8 0.4 -0.4 0.8 10 0] /Resources << >>",
            // A disc in the cell and a triangle reaching beyond XStep into the next cell.
            "0.9 0.5 0 rg 10 0 m 15.5 0 20 4.5 20 10 c 20 15.5 15.5 20 10 20 c 4.5 20 0 15.5 0 10 c 0 4.5 4.5 0 10 0 c h f " +
            "0.1 0.3 0.8 rg 10 10 m 25 10 l 10 25 l h f", "/Pattern cs /P1 scn 10 10 180 180 re f"), 2.0, 0.03);

    [Fact]
    public Task TilingPattern_Stroke() =>
        _parity.AssertMatches("tiling-stroke", TilingPage("/PaintType 1 /TilingType 1 /BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >>",
            "1 0 0 rg 0 0 4 4 re f 0 0 1 rg 4 4 4 4 re f", "/Pattern CS /P1 SCN 16 w 1 J 30 30 m 170 170 l 30 170 m 170 30 l S"), 1.5, 0.02);
}
