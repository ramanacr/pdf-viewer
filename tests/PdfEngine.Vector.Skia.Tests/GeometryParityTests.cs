using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Milestone 1, geometry: transforms, save/restore, fills (both rules), strokes (dashes, caps,
/// joins, miter limit, hairlines), path clips and solid colours, against the Direct2D backend.
/// </summary>
public class GeometryParityTests
{
    private readonly Parity _parity;
    public GeometryParityTests(ITestOutputHelper output) => _parity = new Parity(output);

    private static byte[] Page(string content, string resources = "<< >>", string mediaBox = "[0 0 200 200]")
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, resources, mediaBox);
        return b.Build();
    }

    public static TheoryData<string, string, double, double> Fixtures => new()
    {
        // name, content, max mean channel difference, max share of pixels off by > 64
        { "fill-stroke", "0 0 1 rg 10 10 180 180 re f 1 0 0 RG 4 w 1 J 20 20 m 180 180 l S 0 1 0 RG 20 180 m 180 20 l S", 1.0, 0.005 },
        { "bezier-evenodd", "0.2 0.6 0.3 rg 100 20 m 180 20 180 180 100 180 c 20 180 20 20 100 20 c h 60 60 m 140 60 l 140 140 l 60 140 l h f*", 1.0, 0.005 },
        { "star-nonzero", "0.8 0.2 0.2 rg 100 190 m 155 20 l 10 125 l 190 125 l 45 20 l h f", 1.0, 0.005 },
        { "star-evenodd", "0.2 0.2 0.8 rg 100 190 m 155 20 l 10 125 l 190 125 l 45 20 l h f*", 1.0, 0.005 },
        { "clip-and-transform", "q 1 0 0 1 20 20 cm 0 0 80 80 re W n 0.8 0.1 0.1 rg 0 0 200 200 re f Q q 0.7071 0.7071 -0.7071 0.7071 140 60 cm 0 0 1 rg -20 -20 40 40 re f Q", 1.0, 0.005 },
        { "nested-clips", "q 20 20 160 160 re W n q 100 100 m 190 100 190 190 100 190 c 10 190 10 100 100 100 c h W* n 0.1 0.6 0.3 rg 0 0 200 200 re f Q 0 0 0 rg 0 0 40 200 re f Q", 1.0, 0.005 },
        { "rotated-clip", "q 0.866 0.5 -0.5 0.866 100 20 cm 0 0 80 80 re W n 0.9 0.6 0 rg -100 -100 400 400 re f Q", 1.0, 0.005 },
        { "skew", "q 1 0 0.5 1 0 0 cm 0.3 0.3 0.9 rg 10 40 100 100 re f Q", 1.0, 0.005 },
        { "save-restore", "q 2 0 0 2 0 0 cm q 0.5 0 0 0.5 0 0 cm 1 0 0 rg 10 10 30 30 re f Q 0 1 0 rg 10 10 30 30 re f Q 0 0 1 rg 150 150 30 30 re f", 1.0, 0.005 },
        { "dash-join", "0 0 0 RG 6 w 0 j [12 6] 0 d 20 30 m 100 170 l 180 30 l S [] 0 d 2 j 20 100 m 100 120 l 180 100 l S", 1.0, 0.005 },
        { "dash-phase-odd", "0 0 0.5 RG 5 w [9] 4 d 10 20 m 190 20 l S [6 3 2] 0 d 10 40 m 190 40 l S 1 J [0 10] 0 d 10 60 m 190 60 l S", 1.0, 0.005 },
        { "caps", "8 w 0 J 30 30 m 30 170 l S 1 J 80 30 m 80 170 l S 2 J 130 30 m 130 170 l S", 1.0, 0.005 },
        { "joins", "10 w 0 j 20 40 m 50 160 l 80 40 l S 1 j 90 40 m 120 160 l 150 40 l S 2 j 140 160 m 170 40 l 195 160 l S", 1.0, 0.005 },
        { "miter-limit", "8 w 0 j 1.5 M 20 40 m 60 160 l 100 40 l S 10 M 110 40 m 150 160 l 190 40 l S", 1.0, 0.005 },
        { "hairlines", "0 w 10 10 m 190 190 l S 0.1 w 10 190 m 190 10 l S 0.5 w 100 0 m 100 200 l S [4 2] 0 d 0 w 0 100 m 200 100 l S", 1.5, 0.01 },
        { "alpha", "/G1 gs 1 0 0 rg 20 20 120 120 re f 0 0 1 rg 60 60 120 120 re f", 1.0, 0.005 },
        { "stroke-alpha", "/G1 gs 0 0.5 0 RG 20 w 20 20 m 180 180 l S", 1.0, 0.005 },
        { "open-subpath-fill", "0.5 0 0.5 rg 20 20 m 180 20 l 100 180 l f 0 g 10 10 m 10 10 l 20 w 1 J S", 1.0, 0.005 },
        { "degenerate", "0 g 0 0 0 0 re f 5 w 50 50 m S 100 100 m 100 100 l 2 J S", 1.0, 0.005 },
    };

    private const string AlphaResources = "<< /ExtGState << /G1 << /ca 0.5 /CA 0.4 >> >> >>";

    [Theory]
    [MemberData(nameof(Fixtures))]
    public Task Geometry_MatchesDirect2D(string name, string content, double maxMean, double maxBad) =>
        _parity.AssertMatches(name, Page(content, content.Contains("/G1") ? AlphaResources : "<< >>"), maxMean, maxBad);

    [Theory]
    [InlineData(PageRotation.Rotate90)]
    [InlineData(PageRotation.Rotate180)]
    [InlineData(PageRotation.Rotate270)]
    public Task Rotation_MatchesDirect2D(PageRotation rotation) =>
        _parity.AssertMatches($"rotate-{(int)rotation}", Page("1 0 0 rg 0 0 40 40 re f 0 0 1 rg 150 50 30 90 re f 0 g 2 w 0 0 m 200 150 l S", mediaBox: "[0 0 200 150]"),
            1.0, 0.005, rotation: rotation);

    [Fact]
    public Task PageRotateAndCropBox_MatchDirect2D()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.2 0.4 0.8 rg 0 0 300 300 re f 1 1 0 rg 50 50 100 60 re f", mediaBox: "[0 0 300 300]", extra: "/CropBox [40 30 260 210] /Rotate 90");
        return _parity.AssertMatches("rotate+crop", b.Build(), 1.0, 0.005);
    }

    [Fact]
    public async Task ZeroWidthStroke_IsOneDevicePixel()
    {
        // A horizontal hairline centred on a pixel row covers exactly that row (as Direct2D draws it).
        var b = new VectorPdfBuilder();
        b.AddPage("0 G 0 w 0 100.5 m 200 100.5 l S");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var list = await doc.GetPageDisplayListAsync(1);
        foreach (double dpi in new[] { 72.0, 288.0 })
        {
            using var page = await Parity.Skia.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = dpi });
            var span = page.Pixels.Span;
            int column = page.WidthPixels / 2 * 4, darkRows = 0;
            for (int y = 0; y < page.HeightPixels; y++)
                if (span[y * page.Stride + column] < 160) darkRows++;
            Assert.True(darkRows is >= 1 and <= 2, $"@{dpi}: hairline covers {darkRows} rows");
        }
    }
}
