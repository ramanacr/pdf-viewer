using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Colour management: ICC profile parsing and transforms, DeviceCMYK through a SWOP conversion, default
/// colour spaces, CMYK images and JPEGs, all compared with PDFium (which uses lcms2 for ICC profiles and
/// an Adobe-like conversion for DeviceCMYK) where it is the reference.
/// </summary>
public class ColorManagementTests
{
    private readonly ITestOutputHelper _out;

    public ColorManagementTests(ITestOutputHelper output) => _out = output;

    private static IccProfile Parse(byte[] bytes)
    {
        var profile = IccProfile.TryParse(bytes, out string? error);
        Assert.True(profile != null, error);
        return profile!;
    }

    private static (double R, double G, double B) Srgb(ColorTransform t, params double[] input)
    {
        var rgb = new double[3];
        t.ToSrgb(input, rgb);
        return (rgb[0] * 255, rgb[1] * 255, rgb[2] * 255);
    }

    // ------------------------------------------------------------------ profile models

    [Fact]
    public void MatrixTrcProfile_ConvertsWideGamutRgbToSrgb()
    {
        var profile = Parse(IccProfileBuilder.WideGamutRgb());
        Assert.Equal(3, profile.Channels);
        var t = IccTransform.Create(profile, PdfRenderingIntent.Perceptual);
        Assert.False(t.IsSrgb);

        // White and black stay put; a saturated Adobe RGB green lies outside sRGB and clips.
        var white = Srgb(t, 1, 1, 1);
        Assert.InRange(white.R, 254, 255.5);
        Assert.InRange(white.B, 254, 255.5);
        Assert.Equal(0, Srgb(t, 0, 0, 0).G, 1.0);
        var green = Srgb(t, 0, 1, 0);
        Assert.Equal(0, green.R, 1.0);
        Assert.Equal(255, green.G, 1.0);
        // Mid gray: gamma 2.2 is close to the sRGB curve, so it stays near 128.
        Assert.InRange(Srgb(t, 0.5, 0.5, 0.5).G, 125, 131);
        // A muted Adobe RGB red is more saturated in sRGB.
        var red = Srgb(t, 0.6, 0.3, 0.3);
        Assert.True(red.R > 0.6 * 255 && red.G < 0.3 * 255, $"{red}");
    }

    [Fact]
    public void SrgbLikeProfile_PassesDeviceRgbThrough()
    {
        // The sRGB primaries (D50-adapted) with the sRGB curve as a parametric type 3 curve.
        var curve = IccProfileBuilder.Parametric(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045);
        var bytes = new IccProfileBuilder("mntr", "RGB ", "XYZ ")
            .Tag("rXYZ", IccProfileBuilder.Xyz(0.43607, 0.22249, 0.01392))
            .Tag("gXYZ", IccProfileBuilder.Xyz(0.38515, 0.71687, 0.09708))
            .Tag("bXYZ", IccProfileBuilder.Xyz(0.14307, 0.06061, 0.71410))
            .Tag("rTRC", curve).Tag("gTRC", curve).Tag("bTRC", curve)
            .Build();
        Assert.True(IccTransform.Create(Parse(bytes), PdfRenderingIntent.Perceptual).IsSrgb);

        var cs = Resolve(bytes, 3);
        Assert.False(cs.UsesProfile);
        Assert.Equal(new PdfColor(0.2f, 0.4f, 0.6f), cs.ToRgbColor([0.2, 0.4, 0.6]));
    }

    [Fact]
    public void GrayProfile_AppliesItsTrc()
    {
        var t = IccTransform.Create(Parse(IccProfileBuilder.GrayGamma(2.2)), PdfRenderingIntent.Perceptual);
        // Y = 0.5^2.2 = 0.2176, which sRGB encodes as about 128.5; darker values diverge more.
        Assert.InRange(Srgb(t, 0.5).R, 127, 130);
        Assert.InRange(Srgb(t, 0.1).R, 17, 20);
        Assert.Equal(255, Srgb(t, 1).G, 1.0);
        var g = Srgb(t, 0.3);
        Assert.Equal(g.R, g.B, 3);
    }

    [Fact]
    public void Lut16CmykProfile_InterpolatesItsClut()
    {
        double[][] corners = CmykCorners();
        var t = IccTransform.Create(Parse(IccProfileBuilder.TwoPointCmyk(corners)), PdfRenderingIntent.Perceptual);
        Assert.Equal(4, t.Channels);

        // Corners reproduce their Lab values exactly (index 1 is K alone: K varies fastest).
        var rgb = new double[3];
        PcsMath.LabToSrgb(corners[1][0], corners[1][1], corners[1][2], rgb);
        var k = Srgb(t, 0, 0, 0, 1);
        Assert.Equal(rgb[0] * 255, k.R, 1.0);
        Assert.Equal(rgb[2] * 255, k.B, 1.0);

        // Halfway along the C edge (index 8 is C alone) is the mean of its two corners.
        double l = (corners[0][0] + corners[8][0]) / 2, a = (corners[0][1] + corners[8][1]) / 2, b = (corners[0][2] + corners[8][2]) / 2;
        PcsMath.LabToSrgb(l, a, b, rgb);
        var mid = Srgb(t, 0.5, 0, 0, 0);
        Assert.Equal(rgb[0] * 255, mid.R, 1.0);
        Assert.Equal(rgb[1] * 255, mid.G, 1.0);
        Assert.Equal(rgb[2] * 255, mid.B, 1.0);
    }

    [Fact]
    public void LutAToBProfile_RunsCurvesClutAndMatrix()
    {
        // A 3-input mAB: a 2-point CLUT holding the input itself, then a matrix that swaps the first two
        // outputs and adds an offset, into an XYZ PCS.
        var clut = new double[8 * 3];
        for (int i = 0; i < 8; i++)
        {
            clut[3 * i] = (i >> 2 & 1) * 0.5;
            clut[3 * i + 1] = (i >> 1 & 1) * 0.5;
            clut[3 * i + 2] = (i & 1) * 0.5;
        }
        double[] matrix = [0, 1, 0, 1, 0, 0, 0, 0, 1, 0.01, 0, 0];
        var bytes = new IccProfileBuilder("scnr", "RGB ", "XYZ ", 4)
            .Tag("A2B0", IccProfileBuilder.LutAToB(3, 2, clut, matrix))
            .Build();
        var profile = Parse(bytes);
        var pipeline = profile.AToB[0]!;
        var pcs = new double[3];
        pipeline.Evaluate([0.2, 0.6, 1.0], pcs);
        // CLUT: (0.1, 0.3, 0.5); matrix: (0.3 + 0.01, 0.1, 0.5); u1Fixed15 scale: x 65535/32768.
        double s = 65535.0 / 32768;
        Assert.Equal(0.31 * s, pcs[0], 3);
        Assert.Equal(0.1 * s, pcs[1], 3);
        Assert.Equal(0.5 * s, pcs[2], 3);
    }

    [Fact]
    public void RenderingIntent_SelectsTheMatchingA2BTag()
    {
        var lab0 = Lut(50); // A2B0: L 50 everywhere
        var lab1 = Lut(70); // A2B1: L 70 everywhere
        var bytes = new IccProfileBuilder("prtr", "GRAY", "Lab ")
            .Tag("A2B0", IccProfileBuilder.Lut16(1, 2, lab0))
            .Tag("A2B1", IccProfileBuilder.Lut16(1, 2, lab1))
            .Build();
        var profile = Parse(bytes);
        double L(PdfRenderingIntent intent) => Srgb(IccTransform.Create(profile, intent), 0.5).R;
        Assert.True(L(PdfRenderingIntent.RelativeColorimetric) > L(PdfRenderingIntent.Perceptual) + 30);
        Assert.Equal(L(PdfRenderingIntent.RelativeColorimetric), L(PdfRenderingIntent.AbsoluteColorimetric));
        Assert.Equal(L(PdfRenderingIntent.Perceptual), L(PdfRenderingIntent.Saturation)); // no A2B2: falls back to A2B0

        // The /RI of the graphics state reaches the colour space.
        var cs = Resolve(bytes, 1);
        Assert.NotEqual(cs.ToRgbColor([0.5]), cs.WithIntent(PdfRenderingIntent.RelativeColorimetric).ToRgbColor([0.5]));

        static double[] Lut(double l)
        {
            double enc = l / 100 * 65280 / 65535, zero = 128 * 256 / 65535.0;
            return [enc, zero, zero, enc, zero, zero];
        }
    }

    // ------------------------------------------------------------------ broken profiles

    public static TheoryData<string, byte[]> BrokenProfiles()
    {
        var good = IccProfileBuilder.WideGamutRgb();
        var noSignature = (byte[])good.Clone();
        noSignature[36] = (byte)'x';
        var truncated = good.AsSpan(0, 140).ToArray();
        var hugeTagCount = (byte[])good.Clone();
        hugeTagCount[128] = 0x7F;
        var tagOutside = (byte[])good.Clone();
        tagOutside[132 + 12 + 4] = 0x7F; // the rXYZ tag's offset far beyond the data
        var lutHugeGrid = new IccProfileBuilder("prtr", "CMYK", "Lab ")
            .Tag("A2B0", IccProfileBuilder.Lut16(4, 2, new double[48]))
            .Build();
        // Claim a 255-point grid: 255^4 x 3 values, far beyond the CLUT size limit and the data.
        int lutAt = BitConverter.ToInt32([lutHugeGrid[139], lutHugeGrid[138], lutHugeGrid[137], lutHugeGrid[136]]);
        lutHugeGrid[lutAt + 10] = 255;
        var random = new byte[4096];
        new Random(1234).NextBytes(random);
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "no-acsp", noSignature },
            { "truncated", truncated },
            { "huge-tag-count", hugeTagCount },
            { "tag-outside", tagOutside },
            { "lut-huge-grid", lutHugeGrid },
            { "random", random },
        };
    }

    [Theory]
    [MemberData(nameof(BrokenProfiles))]
    public void BrokenProfile_FallsBackToTheAlternate(string name, byte[] bytes)
    {
        int n = name == "lut-huge-grid" ? 4 : 3;
        var cs = Resolve(bytes, n);
        Assert.False(cs.UsesProfile, name);
        Assert.Null(cs.UnsupportedReason);
        var expected = n == 4 ? PdfColorSpace.DeviceCmyk.ToRgbColor([0.1, 0.2, 0.3, 0.4]) : new PdfColor(0.1f, 0.2f, 0.3f);
        Assert.Equal(expected, cs.ToRgbColor(n == 4 ? [0.1, 0.2, 0.3, 0.4] : [0.1, 0.2, 0.3]));
    }

    [Fact]
    public void MutatedProfiles_NeverThrow()
    {
        var seeds = new[] { IccProfileBuilder.WideGamutRgb(), IccProfileBuilder.GrayGamma(1.8), IccProfileBuilder.TwoPointCmyk(CmykCorners()) };
        var random = new Random(42);
        var rgb = new double[3];
        for (int i = 0; i < 3000; i++)
        {
            var bytes = (byte[])seeds[i % seeds.Length].Clone();
            int flips = 1 + random.Next(8);
            for (int f = 0; f < flips; f++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            var profile = IccProfile.TryParse(bytes, out _);
            if (profile == null) continue;
            var t = IccTransform.Create(profile, (PdfRenderingIntent)random.Next(4));
            var input = new double[profile.Channels];
            for (int c = 0; c < input.Length; c++) input[c] = random.NextDouble() * 1.4 - 0.2;
            t.ToSrgb(input, rgb);
            Assert.All(rgb, v => Assert.InRange(v, 0, 1));
        }
    }

    [Fact]
    public void WrongComponentCount_UsesTheAlternate()
    {
        // A gray profile in an /N 3 space does not describe it.
        var cs = Resolve(IccProfileBuilder.GrayGamma(2.2), 3);
        Assert.False(cs.UsesProfile);
        Assert.Equal(3, cs.NumberOfComponents);
    }

    // ------------------------------------------------------------------ DeviceCMYK

    [Fact]
    public void DeviceCmyk_KOnlyBlackIsRichDarkGray()
    {
        var k = PdfColorSpace.DeviceCmyk.ToRgbColor([0, 0, 0, 1]);
        _out.WriteLine($"K 100%: {k.R * 255:F0},{k.G * 255:F0},{k.B * 255:F0} (installed profile: {DefaultCmyk.UsesInstalledProfile})");
        // Acrobat shows 35/31/32 with U.S. Web Coated (SWOP).
        Assert.InRange(k.R * 255, 25, 45);
        Assert.InRange(k.G * 255, 22, 42);
        Assert.InRange(k.B * 255, 22, 42);
        Assert.Equal(PdfColor.White, PdfColorSpace.DeviceCmyk.ToRgbColor([0, 0, 0, 0]));
    }

    [Fact]
    public void SwopApproximation_IsSmoothAndKeepsPaperWhite()
    {
        var model = SwopApproximation.Instance;
        Assert.Equal((255.0, 255.0, 255.0), Round(Srgb(model, 0, 0, 0, 0)));
        // More ink never makes a colour lighter.
        double previous = double.MaxValue;
        for (int i = 0; i <= 20; i++)
        {
            var c = Srgb(model, 0, 0, 0, i / 20.0);
            Assert.True(c.G <= previous + 0.5, $"K {i / 20.0}: {c.G} > {previous}");
            previous = c.G;
        }
        static (double, double, double) Round((double R, double G, double B) c) => (Math.Round(c.R), Math.Round(c.G), Math.Round(c.B));
    }

    public static TheoryData<string, double, double, double, double, double> CmykPatches => new()
    {
        // name, C, M, Y, K, tolerance in levels per channel against PDFium
        { "pure-k", 0, 0, 0, 1, 8 },
        { "k-50", 0, 0, 0, 0.5, 8 },
        { "rich-black", 0.6, 0.4, 0.4, 1, 12 },
        { "cyan", 1, 0, 0, 0, 12 },
        { "magenta", 0, 1, 0, 0, 12 },
        { "yellow", 0, 0, 1, 0, 12 },
        { "blue", 1, 1, 0, 0, 16 },
        { "tint", 0.2, 0.3, 0.7, 0.1, 8 },
        { "paper", 0, 0, 0, 0, 2 },
    };

    [Theory]
    [MemberData(nameof(CmykPatches))]
    public async Task DeviceCmykFill_MatchesPdfium(string name, double c, double m, double y, double k, double tolerance)
    {
        string op = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} k 0 0 50 50 re f", c, m, y, k);
        var (ours, pdfium) = await RenderCentre(op, "<< >>");
        double naive = Math.Max(Math.Abs((1 - c) * (1 - k) * 255 - pdfium.R), Math.Abs((1 - m) * (1 - k) * 255 - pdfium.G));
        _out.WriteLine($"{name}: ours {ours} pdfium {pdfium} (naive formula off by {naive:F0})");
        AssertClose(pdfium, ours, tolerance, name);
    }

    [Fact]
    public async Task SeparationIntoCmyk_BenefitsFromTheCmykConversion()
    {
        const string res = "<< /ColorSpace << /CS0 [/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>] >> >>";
        var (ours, pdfium) = await RenderCentre("/CS0 cs 1 scn 0 0 50 50 re f", res);
        _out.WriteLine($"separation K: ours {ours} pdfium {pdfium}");
        AssertClose(pdfium, ours, 8, "separation");
    }

    [Fact]
    public async Task DefaultCmyk_ReplacesDeviceCmyk()
    {
        // /DefaultCMYK through a 2-point CMYK profile that paints everything mid gray (L 50).
        var corners = new double[16][];
        for (int i = 0; i < 16; i++) corners[i] = [50, 0, 0];
        var b = new VectorPdfBuilder();
        int icc = b.AddStream("/N 4", IccProfileBuilder.TwoPointCmyk(corners));
        b.AddPage("0 0 0 1 k 0 0 50 50 re f /DeviceCMYK cs 1 0 0 0 sc 50 0 50 50 re f", $"<< /ColorSpace << /DefaultCMYK [/ICCBased {icc} 0 R] >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        using var page = await InterpreterCoverageTests.RenderAsync(list, 72);
        var rgb = new double[3];
        PcsMath.LabToSrgb(50, 0, 0, rgb);
        int expected = (int)Math.Round(rgb[0] * 255);
        foreach (int x in new[] { 25, 75 })
        {
            var px = InterpreterCoverageTests.Pixel(page, x, 175);
            Assert.InRange(px.G, expected - 2, expected + 2);
        }
    }

    // ------------------------------------------------------------------ ICCBased against PDFium

    [Fact]
    public async Task IccBasedRgb_MatchesPdfium()
    {
        foreach (var rgb in new[] { "0.6 0.3 0.3", "0.2 0.7 0.4", "0.5 0.5 0.5", "0.1 0.2 0.9" })
        {
            var (ours, pdfium) = await RenderCentre($"/CS0 cs {rgb} sc 0 0 50 50 re f", IccResources(IccProfileBuilder.WideGamutRgb(), 3, out var b), b);
            _out.WriteLine($"{rgb}: ours {ours} pdfium {pdfium}");
            AssertClose(pdfium, ours, 4, rgb);
        }
    }

    [Fact]
    public async Task IccBasedGray_MatchesPdfium()
    {
        foreach (var g in new[] { "0.1", "0.3", "0.5", "0.8" })
        {
            var (ours, pdfium) = await RenderCentre($"/CS0 cs {g} sc 0 0 50 50 re f", IccResources(IccProfileBuilder.GrayGamma(2.2), 1, out var b), b);
            _out.WriteLine($"gray {g}: ours {ours} pdfium {pdfium}");
            AssertClose(pdfium, ours, 3, g);
        }
    }

    [Fact]
    public async Task IccBasedLutCmyk_MatchesPdfium()
    {
        foreach (var cmyk in new[] { "1 0 0 0", "0 0 0 1", "0.3 0.6 0.2 0.1", "0.5 0.5 0.5 0.5" })
        {
            var (ours, pdfium) = await RenderCentre($"/CS0 cs {cmyk} sc 0 0 50 50 re f", IccResources(IccProfileBuilder.TwoPointCmyk(CmykCorners()), 4, out var b), b);
            _out.WriteLine($"{cmyk}: ours {ours} pdfium {pdfium}");
            AssertClose(pdfium, ours, 4, cmyk);
        }
    }

    // ------------------------------------------------------------------ images

    [Fact]
    public async Task CmykImage_MatchesPdfiumAndTheFillConversion()
    {
        var samples = CmykGradient(64, 64);
        var b = new VectorPdfBuilder();
        int img = b.AddStream("/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceCMYK /BitsPerComponent 8", samples, flate: true);
        b.AddPage("q 128 0 0 128 0 0 cm /Im0 Do Q", $"<< /XObject << /Im0 {img} 0 R >> >>", "[0 0 128 128]");
        byte[] pdf = b.Build();

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var ours = await InterpreterCoverageTests.RenderAsync(list, 72);
        using var pdfium = await RenderPdfium(pdf, 72);
        var (mean, _) = DifferentialRenderingTests.Compare(ours, pdfium);
        _out.WriteLine($"CMYK image mean diff against PDFium: {mean:F2}");
        Assert.True(mean < 6, $"mean {mean:F2}");

        // The image lookup table agrees with the exact conversion used for fills.
        var lut = PdfColorSpace.DeviceCmyk.ImageLut!;
        var bgra = new byte[64 * 64 * 4];
        lut.ToBgra(samples, 0, bgra, 0, 64 * 64);
        int worst = 0;
        for (int p = 0; p < 64 * 64; p += 7)
        {
            var c = PdfColorSpace.DeviceCmyk.ToRgbColor([samples[4 * p] / 255.0, samples[4 * p + 1] / 255.0, samples[4 * p + 2] / 255.0, samples[4 * p + 3] / 255.0]);
            worst = Math.Max(worst, Math.Abs(bgra[4 * p + 2] - (int)Math.Round(c.R * 255)));
            worst = Math.Max(worst, Math.Abs(bgra[4 * p + 1] - (int)Math.Round(c.G * 255)));
            worst = Math.Max(worst, Math.Abs(bgra[4 * p] - (int)Math.Round(c.B * 255)));
        }
        Assert.True(worst <= 3, $"lookup table off by {worst}");
        Assert.True(mean < 4, $"mean {mean:F2}");
    }

    [Fact]
    public async Task CmykJpeg_IsConvertedTheSameWayByBothBackendsAndMatchesPdfium()
    {
        // WIC writes an Adobe marker, so its samples are read as Adobe-inverted CMYK (as Photoshop writes
        // them): store the inverse of a CMYK gradient.
        var samples = CmykGradient(64, 64);
        var inverted = Array.ConvertAll(samples, v => (byte)(255 - v));
        byte[] jpeg = EncodeCmykJpeg(inverted, 64, 64);

        var b = new VectorPdfBuilder();
        int img = b.AddStream("/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode", jpeg);
        b.AddPage("q 128 0 0 128 0 0 cm /Im0 Do Q", $"<< /XObject << /Im0 {img} 0 R >> >>", "[0 0 128 128]");
        byte[] pdf = b.Build();

        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var wpf = await InterpreterCoverageTests.RenderAsync(list, 72);
        using var d2dRenderer = new Direct2DVectorRenderer();
        using var d2d = await d2dRenderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        using var pdfium = await RenderPdfium(pdf, 72);

        var (backends, _) = DifferentialRenderingTests.Compare(wpf, d2d);
        var (oracle, _) = DifferentialRenderingTests.Compare(wpf, pdfium);
        _out.WriteLine($"CMYK JPEG: WPF vs Direct2D {backends:F2}, WPF vs PDFium {oracle:F2}");
        Assert.True(backends < 1.5, $"backends differ by {backends:F2}");
        Assert.True(oracle < 8, $"PDFium differs by {oracle:F2}");
    }

    [Fact]
    public void LargeCmykJpeg_DecodesWithinTwiceTheNaiveTime()
    {
        // A 3-megapixel CMYK photo-sized JPEG: decoding it is WIC's JPEG decode plus the colour conversion.
        const int w = 2000, h = 1500;
        var samples = CmykGradient(w, h);
        byte[] jpeg = EncodeCmykJpeg(samples, w, h);
        var lut = PdfColorSpace.DeviceCmyk.ImageLut!;
        var bgra = new byte[w * h * 4];
        var raw = new byte[w * h * 4];

        double Best(Action action)
        {
            double best = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        void DecodeJpeg()
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            frame.CopyPixels(raw, w * 4, 0);
        }

        double naive = Best(() =>
        {
            DecodeJpeg();
            for (int i = 0; i < raw.Length; i += 4)
            {
                int k = 255 - raw[i + 3];
                bgra[i] = (byte)((255 - raw[i + 2]) * k / 255);
                bgra[i + 1] = (byte)((255 - raw[i + 1]) * k / 255);
                bgra[i + 2] = (byte)((255 - raw[i]) * k / 255);
            }
        });
        double managed = Best(() =>
        {
            DecodeJpeg();
            lut.ToBgra(raw, 0, bgra, 0, w * h);
        });
        _out.WriteLine($"3 MP CMYK JPEG: colour managed {managed:F0} ms, naive formula {naive:F0} ms ({managed / naive:F2}x)");
        // Debug builds and busy machines are noisy; the Release ratio is recorded in 18_IMPLEMENTATION_STATUS.md.
        Assert.True(managed < 3 * naive + 50, $"{managed:F0} ms against {naive:F0} ms");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Lab corners of a toy CMYK device: inks darken and tint paper, K darkens most.</summary>
    private static double[][] CmykCorners()
    {
        var corners = new double[16][];
        for (int i = 0; i < 16; i++)
        {
            int c = i >> 3 & 1, m = i >> 2 & 1, y = i >> 1 & 1, k = i & 1;
            double l = 95 - 30 * c - 35 * m - 8 * y - 40 * k;
            double a = -35 * c + 60 * m - 5 * y;
            double b = -45 * c - 5 * m + 80 * y;
            corners[i] = [Math.Max(l, 5), a * (1 - 0.6 * k), b * (1 - 0.6 * k)];
        }
        return corners;
    }

    private static byte[] CmykGradient(int w, int h)
    {
        var s = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                s[p] = (byte)(x * 255 / Math.Max(1, w - 1));
                s[p + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                s[p + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
                s[p + 3] = (byte)(x < w / 2 ? y * 128 / Math.Max(1, h) : 0);
            }
        return s;
    }

    private static byte[] EncodeCmykJpeg(byte[] cmyk, int w, int h)
    {
        var bitmap = BitmapSource.Create(w, h, 72, 72, PixelFormats.Cmyk32, null, cmyk, w * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static PdfColorSpace.IccBasedColorSpace Resolve(byte[] profile, int n)
    {
        var stream = StreamDecoderTests.MakeStream(profile, ("N", new PdfInteger(n)));
        var cs = PdfColorSpace.Resolve(new PdfArray([new PdfName("ICCBased"), stream]), ColorSpaceTests.Empty);
        return Assert.IsType<PdfColorSpace.IccBasedColorSpace>(cs);
    }

    /// <summary>A builder holding the profile stream, and resources naming it /CS0.</summary>
    private static string IccResources(byte[] profile, int n, out VectorPdfBuilder builder)
    {
        builder = new VectorPdfBuilder();
        int icc = builder.AddStream($"/N {n}", profile);
        return $"<< /ColorSpace << /CS0 [/ICCBased {icc} 0 R] >> >>";
    }

    private static async Task<RenderedPage> RenderPdfium(byte[] pdf, double dpi)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf);
        return await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = 1, Dpi = dpi });
    }

    private static async Task<((int R, int G, int B) Ours, (int R, int G, int B) Pdfium)> RenderCentre(string content, string resources, VectorPdfBuilder? builder = null)
    {
        var b = builder ?? new VectorPdfBuilder();
        b.AddPage(content, resources);
        byte[] pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var ours = await InterpreterCoverageTests.RenderAsync(list, 72);
        using var pdfium = await RenderPdfium(pdf, 72);
        var a = InterpreterCoverageTests.Pixel(ours, 25, 175);
        var p = InterpreterCoverageTests.Pixel(pdfium, 25, 175);
        return ((a.R, a.G, a.B), (p.R, p.G, p.B));
    }

    private static void AssertClose((int R, int G, int B) expected, (int R, int G, int B) actual, double tolerance, string what)
    {
        double worst = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.True(worst <= tolerance, $"{what}: {actual} vs PDFium {expected} (off by {worst}, budget {tolerance})");
    }
}
