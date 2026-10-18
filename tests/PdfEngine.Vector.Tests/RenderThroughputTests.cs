using System;
using System.Numerics;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The faster image and colour paths give the same bytes as the paths they replace.
/// </summary>
public class RenderThroughputTests
{
    private static PdfName N(string name) => new(name);

    private static PdfDictionary D(params (string Key, PdfObject Value)[] entries)
    {
        var d = new System.Collections.Generic.Dictionary<string, PdfObject>();
        foreach (var (k, v) in entries) d[k] = v;
        return new PdfDictionary(d);
    }

    private static PdfArray Numbers(params double[] values) => new(Array.ConvertAll(values, v => (PdfObject)new PdfReal(v)));

    private static byte Byte(double value) => double.IsNaN(value) ? (byte)0 : (byte)Math.Clamp((int)(value * 255.0 + 0.5), 0, 255);

    private static byte Reference(double linear) => Byte(Math.Clamp((float)PdfColorSpace.CieMath.Compand(linear), 0f, 1f));

    private static PdfColorSpace Cie(bool lab) => PdfColorSpace.Resolve(new PdfArray(new PdfObject[]
    {
        N(lab ? "Lab" : "CalRGB"),
        lab
            ? D(("WhitePoint", Numbers(0.9505, 1, 1.089)), ("Range", Numbers(-128, 127, -128, 127)))
            : D(("WhitePoint", Numbers(0.9505, 1, 1.089)), ("Gamma", Numbers(1.8, 2.2, 2.4)),
                ("Matrix", Numbers(0.4497, 0.2446, 0.0252, 0.3163, 0.672, 0.1412, 0.1845, 0.0833, 0.9227))),
    }), ColorSpaceTests.Empty);

    [Fact]
    public void LinearToByteMatchesTheCurveEverywhere()
    {
        var rng = new Random(7);
        for (int i = 0; i < 1_000_000; i++)
        {
            double v = rng.NextDouble() * 1.2 - 0.1;
            Assert.Equal(Reference(v), PdfColorSpace.CieMath.LinearToByte(v));
        }
        // Around every step of the curve, to the last bit: the linear value of each half level.
        for (int k = 0; k < 255; k++)
        {
            double encoded = (k + 0.5) / 255.0;
            double step = encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            long bits = BitConverter.DoubleToInt64Bits(step);
            for (long d = -2000; d <= 2000; d++)
            {
                double v = BitConverter.Int64BitsToDouble(bits + d);
                Assert.Equal(Reference(v), PdfColorSpace.CieMath.LinearToByte(v));
            }
        }
        foreach (double v in new[] { 0.0, -0.0, 1.0, 2.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.Epsilon })
            Assert.Equal(Reference(v), PdfColorSpace.CieMath.LinearToByte(v));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CieImageConversionMatchesPerPixelConversion(bool lab)
    {
        var cs = Cie(lab);
        Assert.Equal(lab ? "Lab" : "CalRGB", cs.Name);
        var rng = new Random(11);
        var comps = new double[3];
        Span<byte> fast = stackalloc byte[3];
        for (int i = 0; i < 200_000; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                var (min, max) = cs.GetComponentRange(c);
                // Image samples k / 255 (scaled to the range), and arbitrary values.
                double t = i % 2 == 0 ? rng.Next(256) / 255.0 : rng.NextDouble() * 1.2 - 0.1;
                comps[c] = min + t * (max - min);
            }
            var expected = cs.ToRgbColor(comps);
            cs.ToRgb24(comps, fast);
            Assert.Equal(Byte(expected.R), fast[0]);
            Assert.Equal(Byte(expected.G), fast[1]);
            Assert.Equal(Byte(expected.B), fast[2]);
        }
    }

    [Fact]
    public void CalRgbSampleConversionMatchesPerPixelConversion()
    {
        var cs = (PdfColorSpace.CalRgbColorSpace)Cie(lab: false);
        int width = 301, height = 257; // large enough for the parallel bands
        var samples = new byte[width * height * 3 - 5]; // short data reads as zero
        new Random(3).NextBytes(samples);
        var bgra = new byte[width * height * 4];
        cs.ToBgra8(samples, bgra, width, height, default);
        var comps = new double[3];
        for (int p = 0; p < width * height; p++)
        {
            for (int c = 0; c < 3; c++)
                comps[c] = (p * 3 + c < samples.Length ? samples[p * 3 + c] : 0) / 255.0;
            var expected = cs.ToRgbColor(comps);
            Assert.Equal(Byte(expected.B), bgra[p * 4]);
            Assert.Equal(Byte(expected.G), bgra[p * 4 + 1]);
            Assert.Equal(Byte(expected.R), bgra[p * 4 + 2]);
            Assert.Equal(255, bgra[p * 4 + 3]);
        }
    }

    /// <summary>The glyph rasterizer's coverage is the exact area of the outline in each pixel.</summary>
    [Fact]
    public void GlyphRasterizerCoversByExactArea()
    {
        using var r = new GlyphRasterizer();
        r.Reset(6, 6, Matrix3x2.Identity);
        // A rectangle from (1.25, 1.5) to (3.75, 4): columns 1 and 3 are three quarters covered,
        // column 2 whole; row 1 half, rows 2 and 3 whole.
        r.BeginFigure(new Vector2(1.25f, 1.5f), Vortice.Direct2D1.FigureBegin.Filled);
        r.AddLines(new[] { new Vector2(3.75f, 1.5f), new Vector2(3.75f, 4f), new Vector2(1.25f, 4f) });
        r.EndFigure(Vortice.Direct2D1.FigureEnd.Closed);
        var px = new byte[6 * 6 * 4];
        r.WriteBgra(px, 6 * 4);
        double Coverage(int x, int y) => px[(y * 6 + x) * 4] / 255.0;
        double[] cols = { 0, 0.75, 1, 0.75, 0, 0 }, rows = { 0, 0.5, 1, 1, 0, 0 };
        double total = 0;
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 6; x++)
        {
            Assert.Equal(cols[x] * rows[y], Coverage(x, y), 1.0 / 255);
            total += Coverage(x, y);
        }
        Assert.Equal(2.5 * 2.5, total, 0.02);
    }

    /// <summary>
    /// Two forms whose inline images sit at the same offset of their content are two images: an
    /// inline image is known by its content buffer as well as its offset (it was known by the
    /// offset alone, so every Type 3 glyph drawn by an inline image showed the first one).
    /// </summary>
    [Fact]
    public async Task InlineImagesOfDifferentContentStreamsAreDifferentImages()
    {
        var b = new VectorPdfBuilder();
        string Form(string rgb) => $"q 20 0 0 20 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /RGB ID {rgb} EI Q";
        int red = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", Form("\xff\x00\x00"));
        int green = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20]", Form("\x00\xff\x00"));
        b.AddPage("/A Do q 1 0 0 1 30 0 cm /B Do Q", $"<< /XObject << /A {red} 0 R /B {green} 0 R >> >>", mediaBox: "[0 0 60 20]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        using var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var px = page.Pixels.ToArray();
        int At(int x, int c) => px[10 * page.Stride + x * 4 + c]; // B, G, R
        Assert.True(At(10, 2) > 200 && At(10, 1) < 50, "the first form's image is red");
        Assert.True(At(40, 1) > 200 && At(40, 2) < 50, "the second form's image is green");
    }

    /// <summary>
    /// A page rendered again decodes no image and analyses nothing again (the renderer keeps the
    /// analysis per display list and the decode outcome per image), and gives the same pixels.
    /// </summary>
    [Fact]
    public async Task RenderingAgainReusesAnalysisAndDecodedImages()
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB",
            "\xff\x00\x00\x00\xff\x00\x00\x00\xff\xff\xff\x00");
        b.AddPage("q 100 0 0 100 50 50 cm /Im Do Q BT /F1 12 Tf 10 10 Td (Text) Tj ET",
            $"<< /XObject << /Im {image} 0 R >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var request = new RenderRequest { PageNumber = 1, Dpi = 72 };
        using var first = await renderer.RenderDisplayListAsync(list, request);
        int decoded = renderer.Stages.ImagesDecoded, hits = renderer.Stages.AnalysisCacheHits;
        Assert.Equal(1, decoded);
        using var second = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 96 });
        using var third = await renderer.RenderDisplayListAsync(list, request);
        Assert.Equal(decoded, renderer.Stages.ImagesDecoded);
        Assert.Equal(hits + 2, renderer.Stages.AnalysisCacheHits);
        Assert.Equal(first.Pixels.ToArray(), third.Pixels.ToArray());
    }

    /// <summary>A rendered page's pixels live until it is disposed; reading them afterwards throws.</summary>
    [Fact]
    public async Task RenderedPagePixelsAreReleasedOnDispose()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 10 10 50 50 re f");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var page = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        Assert.Equal(255, page.Pixels.Span[160 * page.Stride + 30 * 4]); // blue
        page.Dispose();
        Assert.Throws<ObjectDisposedException>(() => page.Pixels.Span[0]);
    }

    [Fact]
    public void DeviceRgbPassesEightBitSamplesThrough()
    {
        Span<byte> rgb = stackalloc byte[3];
        for (int k = 0; k < 256; k++)
        {
            PdfColorSpace.DeviceRgb.ToRgb24([k / 255.0, k / 255.0, k / 255.0], rgb);
            Assert.Equal(k, rgb[0]);
        }
    }
}
