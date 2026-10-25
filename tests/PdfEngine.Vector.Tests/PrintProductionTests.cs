using System;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PrintProduction;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Print production: separations (plates, overprint, total ink), colorant enumeration and preflight, on generated
/// PDFs whose plate values are known exactly. Plates are drawn by Direct2D (byte-repeatable) at 72 dpi.
/// </summary>
public class PrintProductionTests
{
    private readonly ITestOutputHelper _output;
    public PrintProductionTests(ITestOutputHelper output) => _output = output;

    // A spot colour whose alternate is 0 % C, 50 % M, 100 % Y, 0 % K at full tint.
    private const string SpotSpace = "[/Separation /Orange#20Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0.5 1 0] /N 1 >>]";

    private static async Task<(PdfSeparation Separation, PdfVectorDocument Doc)> Separate(byte[] pdf, bool overprint, PdfInkModel? model = null)
    {
        var doc = await PdfVectorDocument.OpenAsync(pdf);
        using var rasterizer = new Direct2DDisplayListRasterizer();
        var separation = await PdfSeparationRenderer.RenderAsync(doc, 1, 200, 200, rasterizer,
            new PdfSeparationOptions { Model = model, SimulateOverprint = overprint });
        return (separation, doc);
    }

    /// <summary>The ink of a plate (0..255) at a point in page points (y up) of a 200 × 200 page drawn at 72 dpi.</summary>
    private static int Ink(PdfSeparation s, string plate, double x, double y)
    {
        int c = s.Model.IndexOf(plate);
        Assert.True(c >= 0, $"no plate {plate}");
        return s.Plane(c)![(199 - (int)y) * 200 + (int)x];
    }

    private static void Near(int expected, int actual, int tolerance = 1) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} ± {tolerance}, got {actual}");

    [Fact]
    public async Task SpotColour_GoesOnItsOwnPlate_AndOnTheProcessPlatesWhenConverted()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("/CS1 cs 0.6 scn 20 20 60 60 re f", $"<< /ColorSpace << /CS1 {SpotSpace} >> >>");
        var pdf = b.Build();

        var (s, doc) = await Separate(pdf, overprint: false);
        using (doc)
        {
            Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "Orange Spot"], s.Model.Channels);
            Near(153, Ink(s, "Orange Spot", 50, 50)); // 60 % tint
            foreach (var process in PdfInkModel.ProcessColorants)
                Assert.Equal(0, Ink(s, process, 50, 50));
            Assert.Equal(0, Ink(s, "Orange Spot", 150, 150));
        }

        // The spot plate disabled: the tint transform's alternate, 0.6 × (0, 0.5, 1, 0).
        var (p, doc2) = await Separate(pdf, overprint: false, PdfInkModel.ProcessOnly);
        using (doc2)
        {
            Assert.Equal(4, p.Model.ChannelCount);
            Assert.Equal(0, Ink(p, "Cyan", 50, 50));
            Near(77, Ink(p, "Magenta", 50, 50));
            Near(153, Ink(p, "Yellow", 50, 50));
            Assert.Equal(0, Ink(p, "Black", 50, 50));
        }
    }

    private const string CyanBackground = "0 0 0 0 k 1 0 0 0 k 0 0 200 200 re f ";

    [Fact]
    public async Task Overprint_KeepsThePlatesTheObjectDoesNotPaint_OnlyWhenSimulated()
    {
        // Magenta overprinting cyan in overprint mode 1: the zero C, Y and K components leave those plates alone.
        var b = new VectorPdfBuilder();
        b.AddPage(CyanBackground + "/OP1 gs 0 1 0 0 k 50 50 100 100 re f", "<< /ExtGState << /OP1 << /OP true /op true /OPM 1 >> >> >>");
        var pdf = b.Build();

        var (on, d1) = await Separate(pdf, overprint: true, PdfInkModel.ProcessOnly);
        using (d1)
        {
            Assert.Equal(255, Ink(on, "Cyan", 100, 100));
            Assert.Equal(255, Ink(on, "Magenta", 100, 100));
            Assert.Equal(255, Ink(on, "Cyan", 20, 20));
            Assert.Equal(0, Ink(on, "Magenta", 20, 20));
            Assert.Equal(510 * 100.0 / 255, on.TotalInkAt(100, 100), 1);
        }

        var (off, d2) = await Separate(pdf, overprint: false, PdfInkModel.ProcessOnly);
        using (d2)
        {
            Assert.Equal(0, Ink(off, "Cyan", 100, 100)); // knocked out
            Assert.Equal(255, Ink(off, "Magenta", 100, 100));
        }
    }

    [Fact]
    public async Task OverprintMode0_DeviceCmykPaintsEveryProcessPlate()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(CyanBackground + "/OP0 gs 0 1 0 0 k 50 50 100 100 re f", "<< /ExtGState << /OP0 << /OP true >> >> >>");
        var (s, doc) = await Separate(b.Build(), overprint: true, PdfInkModel.ProcessOnly);
        using (doc)
        {
            // OPM 0: a DeviceCMYK colour paints all four process plates, zeros included (8.6.7).
            Assert.Equal(0, Ink(s, "Cyan", 100, 100));
            Assert.Equal(255, Ink(s, "Magenta", 100, 100));
        }
    }

    [Fact]
    public async Task OverprintingSpot_LeavesProcessPlates_EvenInMode0()
    {
        var b = new VectorPdfBuilder();
        // /op only: fills overprint, and /OP (strokes) stays off.
        b.AddPage(CyanBackground + "/OPF gs /CS1 cs 1 scn 50 50 100 100 re f 0 0 0 1 K 10 w 0 10 m 200 10 l S",
            $"<< /ColorSpace << /CS1 {SpotSpace} >> /ExtGState << /OPF << /op true >> >> >>");
        var (s, doc) = await Separate(b.Build(), overprint: true);
        using (doc)
        {
            Assert.Equal(255, Ink(s, "Cyan", 100, 100)); // kept under the overprinting spot
            Assert.Equal(255, Ink(s, "Orange Spot", 100, 100));
            Assert.Equal(0, Ink(s, "Cyan", 100, 10)); // the stroke does not overprint: knocks cyan out
            Assert.Equal(255, Ink(s, "Black", 100, 10));
        }
    }

    [Fact]
    public async Task RichBlack_TotalInkIsTheSumOfThePlates()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.6 0.4 0.4 1 k 0 0 100 200 re f 1 1 1 1 k 100 0 100 100 re f");
        var (s, doc) = await Separate(b.Build(), overprint: false, PdfInkModel.ProcessOnly);
        using (doc)
        {
            Near(153, Ink(s, "Cyan", 50, 50));
            Near(102, Ink(s, "Magenta", 50, 50));
            Near(102, Ink(s, "Yellow", 50, 50));
            Assert.Equal(255, Ink(s, "Black", 50, 50));
            Assert.Equal(240, s.TotalInkAt(50, 150), 1);
            var coverage = s.Coverage(300);
            Assert.Equal(400, coverage.MaxPercent, 1);
            // Exactly the 100 × 100 point square is over 300 %: a quarter of the page.
            Assert.Equal(0.25, coverage.FractionOver, 2);
            Assert.Equal(new Geometry.PdfRect(100, 100, 100, 100), coverage.OverBounds);
            var page = PdfSeparationRenderer.PixelsToPage(coverage.OverBounds!.Value, new Geometry.PdfRect(0, 0, 200, 200), 0, 200, 200);
            Assert.Equal(new Geometry.PdfRect(100, 0, 100, 100), page);
        }
    }

    [Fact]
    public async Task DeviceN_ProcessAndSpotColorantsGoOnTheirPlates()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("/DN cs 0.5 0.8 scn 20 20 60 60 re f",
            "<< /ColorSpace << /DN [/DeviceN [/Cyan /Varnish] /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>] >> >>");
        // The tint transform is not valid for two inputs: the plates do not need it, every colorant has one.
        var (s, doc) = await Separate(b.Build(), overprint: false);
        using (doc)
        {
            Assert.Contains("Varnish", s.Model.Channels);
            Near(128, Ink(s, "Cyan", 50, 50));
            Near(204, Ink(s, "Varnish", 50, 50));
            Assert.Equal(0, Ink(s, "Magenta", 50, 50));
        }
    }

    [Fact]
    public async Task GrayIsBlackInkOnly_AndRgbSeparatesThroughTheSwopModel()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.25 g 0 0 100 200 re f 0.6 0.5 0.4 rg 100 0 100 200 re f");
        var (s, doc) = await Separate(b.Build(), overprint: false, PdfInkModel.ProcessOnly);
        using (doc)
        {
            Near(191, Ink(s, "Black", 50, 100));
            Assert.Equal(0, Ink(s, "Cyan", 50, 100));

            // The RGB colour's plates, shown through the DeviceCMYK conversion, give the colour back.
            var c = Enumerable.Range(0, 4).Select(i => s.InkAt(i, 150, 100)).ToArray();
            var shown = PdfInkModel.ShowCmyk(c[0], c[1], c[2], c[3]);
            _output.WriteLine($"RGB 0.6/0.5/0.4 -> CMYK {c[0]:F3}/{c[1]:F3}/{c[2]:F3}/{c[3]:F3} -> {shown.R:F3}/{shown.G:F3}/{shown.B:F3}");
            Assert.Equal(0.6, shown.R, 1);
            Assert.Equal(0.5, shown.G, 1);
            Assert.Equal(0.4, shown.B, 1);
        }
    }

    [Fact]
    public void RgbSeparation_RoundTripsInsideTheGamut()
    {
        // CMYK colours shown on screen, separated back and shown again: within 1.5 ΔE76 on average.
        double sum = 0, worst = 0;
        int n = 0;
        foreach (double c in new[] { 0.0, 0.3, 0.6 })
            foreach (double m in new[] { 0.0, 0.3, 0.6 })
                foreach (double y in new[] { 0.0, 0.3, 0.6 })
                    foreach (double k in new[] { 0.0, 0.3 })
                    {
                        var shown = PdfInkModel.ShowCmyk(c, m, y, k);
                        var sep = PdfInkModel.SeparateRgb(shown.R, shown.G, shown.B);
                        var back = PdfInkModel.ShowCmyk(sep.C, sep.M, sep.Y, sep.K);
                        double de = DeltaE(shown, back);
                        sum += de;
                        worst = Math.Max(worst, de);
                        n++;
                    }
        _output.WriteLine($"round trip over {n} colours: mean ΔE76 {sum / n:F2}, worst {worst:F2}");
        Assert.True(sum / n < 1.5, $"mean ΔE {sum / n:F2}");
        Assert.True(worst < 5, $"worst ΔE {worst:F2}");

        // Neutral grays separate with balanced C = M = Y (no colour cast) and black from the medium GCR.
        var gray = PdfInkModel.SeparateRgb(0.5, 0.5, 0.5);
        _output.WriteLine($"50 % gray: {gray}");
        Assert.True(gray.K > 0.1);
        var black = PdfInkModel.SeparateRgb(0, 0, 0);
        _output.WriteLine($"black: {black}");
        Assert.True(black.C + black.M + black.Y + black.K <= CmykSeparation.InkLimit + 1e-6);
        Assert.True(black.K > 0.9);
    }

    private static double DeltaE(PdfColor a, PdfColor b)
    {
        static double Lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        CmykSeparation.LinearToLab(Lin(a.R), Lin(a.G), Lin(a.B), out double l1, out double a1, out double b1);
        CmykSeparation.LinearToLab(Lin(b.R), Lin(b.G), Lin(b.B), out double l2, out double a2, out double b2);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    [Fact]
    public async Task SeparationImage_SamplesAreTints()
    {
        // A 2 × 1 image of tints 0x40 and 0xFF in the spot space.
        var b = new VectorPdfBuilder();
        int img = b.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace {SpotSpace}", new byte[] { 0x40, 0xFF });
        b.AddPage("q 200 0 0 200 0 0 cm /Im1 Do Q", $"<< /XObject << /Im1 {img} 0 R >> >>");
        var (s, doc) = await Separate(b.Build(), overprint: false);
        using (doc)
        {
            Near(0x40, Ink(s, "Orange Spot", 50, 100));
            Near(0xFF, Ink(s, "Orange Spot", 150, 100));
            Assert.Equal(0, Ink(s, "Magenta", 50, 100));
        }
    }

    [Fact]
    public async Task Inventory_FindsSpotsInFormsPatternsShadingsAndImages()
    {
        var b = new VectorPdfBuilder();
        string Sep(string name) => $"[/Separation /{name} /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>]";
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /ColorSpace << /F {Sep("InForm")} >> >>",
            "/F cs 1 scn 0 0 10 10 re f");
        int cell = b.AddStream($"/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /ColorSpace << /P {Sep("InPattern")} >> >>",
            "/P cs 1 scn 0 0 5 5 re f");
        int shading = b.Add($"<< /ShadingType 2 /ColorSpace {Sep("InShading")} /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>");
        int img = b.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace [/Indexed {Sep("InImage")} 0 <FF>]", new byte[] { 0 });
        b.AddPage("/Fm1 Do /Pattern cs /P1 scn 20 20 50 50 re f /Sh1 sh q 10 0 0 10 100 100 cm /Im1 Do Q",
            $"<< /XObject << /Fm1 {form} 0 R /Im1 {img} 0 R >> /Pattern << /P1 {cell} 0 R >> /Shading << /Sh1 {shading} 0 R >> " +
            $"/ColorSpace << /Unused {Sep("NeverPainted")} >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var inventory = await PdfColorantInventory.ReadAsync(doc);
        var names = inventory.Spots.Select(s => s.Name).ToList();
        _output.WriteLine(string.Join(", ", names));
        Assert.Equal(new[] { "InForm", "InPattern", "InShading", "InImage" }.OrderBy(n => n), names.OrderBy(n => n));
        Assert.DoesNotContain("NeverPainted", names);
        Assert.All(inventory.Spots, s => Assert.Equal(new[] { 1 }, s.Pages));
        // Full tint of a cyan alternate shows as the engine's cyan.
        var cyan = PdfInkModel.ShowCmyk(1, 0, 0, 0);
        Assert.Equal(cyan.R, inventory.Spots[0].Appearance.R, 2);
    }

    [Fact]
    public async Task Composite_OfProcessPlates_MatchesTheNormalRenderOfCmykContent()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.2 0.7 0.1 0 k 0 0 100 200 re f 0.9 0.1 0.5 0.2 k 100 0 100 200 re f");
        var pdf = b.Build();
        var (s, doc) = await Separate(pdf, overprint: false, PdfInkModel.ProcessOnly);
        using (doc)
        {
            var composite = s.CompositeToBgra(s.Model.AllMask);
            var list = await doc.GetPageDisplayListAsync(1);
            using var rasterizer = new Direct2DDisplayListRasterizer();
            var normal = await rasterizer.RenderAsync(list, 200, 200);
            int worst = 0;
            for (int i = 0; i < normal.Length; i++)
                if (i % 4 != 3) worst = Math.Max(worst, Math.Abs(normal[i] - composite[i]));
            _output.WriteLine($"largest channel difference: {worst}");
            Assert.True(worst <= 3, $"composite differs from the normal render by {worst}");
        }
    }

    [Fact]
    public async Task PlateDisplayList_DoesNotChangeTheNormalDisplayList()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("/CS1 cs 0.6 scn 20 20 60 60 re f", $"<< /ColorSpace << /CS1 {SpotSpace} >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var before = await doc.GetPageDisplayListAsync(1);
        var fill = Assert.IsType<FillPath>(before.Commands.First(c => c is FillPath));
        var plate = await doc.GetPlateDisplayListAsync(1, new PdfPlateRequest(new PdfInkModel(["Orange Spot"]), [4], false));
        var plateFill = Assert.IsType<FillPath>(plate.Commands.First(c => c is FillPath));
        Assert.Equal(0.4f, plateFill.Paint.Color.R, 3);
        Assert.Same(before, await doc.GetPageDisplayListAsync(1));
        Assert.Equal(fill.Paint.Color, ((FillPath)before.Commands.First(c => c is FillPath)).Paint.Color);
        Assert.NotEqual(fill.Paint.Color, plateFill.Paint.Color);
    }

    // ------------------------------------------------------------------ preflight

    private static byte[] PreflightSample()
    {
        var b = new VectorPdfBuilder();
        int img = b.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray", new byte[] { 0, 64, 128, 255 });
        b.AddPage("BT /F1 12 Tf 10 180 Td (SECRET TEXT) Tj ET 1 0 0 rg 10 10 50 50 re f 0 0 0 1 K 0.1 w 0 100 m 200 100 l S " +
                  "q 100 0 0 100 100 50 cm /Im1 Do Q 1 1 1 1 k 0 150 20 20 re f /T gs 0 0 1 0 k 120 160 40 30 re f",
            $"<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> /XObject << /Im1 {img} 0 R >> " +
            "/ExtGState << /T << /ca 0.5 >> >> /ColorSpace << >> >>");
        return b.Build();
    }

    [Fact]
    public async Task Preflight_DigitalPrinting_FindsEachProblemWithItsPage()
    {
        using var doc = await PdfVectorDocument.OpenAsync(PreflightSample());
        using var rasterizer = new Direct2DDisplayListRasterizer();
        var report = await PdfPreflight.RunAsync(doc, PdfPreflightProfile.DigitalPrinting, new PdfPreflightOptions { Rasterizer = rasterizer, InkCoverageDpi = 72 });
        _output.WriteLine(report.ToText());

        var font = Assert.Single(report.Findings, f => f.RuleId == "PP-1.1");
        Assert.Equal(PdfPreflightSeverity.Error, font.Severity);
        Assert.Contains("Helvetica", font.Message);

        var image = Assert.Single(report.Findings, f => f.RuleId == "PP-2.1");
        Assert.Equal(1.44, image.Measure!.Value, 2); // 2 pixels over 100 pt
        Assert.Equal(3, image.ObjectNumber);
        Assert.Equal(new Geometry.PdfRect(100, 50, 100, 100), image.Bounds);

        var rgb = Assert.Single(report.Findings, f => f.RuleId == "PP-3.1");
        Assert.Equal(new Geometry.PdfRect(10, 10, 50, 50), rgb.Bounds);
        Assert.Equal(1, rgb.Page);

        var hair = Assert.Single(report.Findings, f => f.RuleId == "PP-5.1");
        Assert.Equal(0.1, hair.Measure!.Value, 3);

        Assert.Contains(report.Findings, f => f.RuleId == "PP-6.1" && f.Message.Contains("opacity 50 %"));
        var ink = Assert.Single(report.Findings, f => f.RuleId == "PP-4.1");
        Assert.Equal(400, ink.Measure!.Value, 0);
        // Only the rich black square: the text's initial black is black ink alone (100 %).
        Assert.Equal(new Geometry.PdfRect(0, 150, 20, 20), ink.Bounds);
        Assert.Contains(report.Findings, f => f.RuleId == "PP-7.1");
        Assert.False(report.Passed);

        // No text of the page in either export.
        Assert.DoesNotContain("SECRET", report.ToJson());
        Assert.DoesNotContain("SECRET", report.ToText());
        Assert.Contains("\"rule\": \"PP-2.1\"", report.ToJson());
    }

    [Fact]
    public async Task Preflight_WithoutRasterizer_MeasuresInkPerColour()
    {
        using var doc = await PdfVectorDocument.OpenAsync(PreflightSample());
        var report = await PdfPreflight.RunAsync(doc, PdfPreflightProfile.DigitalPrinting);
        Assert.False(report.InkCoverageRasterized);
        var ink = Assert.Single(report.Findings, f => f.RuleId == "PP-4.1");
        Assert.Equal(400, ink.Measure!.Value, 0);
        Assert.Equal(new Geometry.PdfRect(0, 150, 20, 20), ink.Bounds);
    }

    [Fact]
    public async Task Preflight_PdfX1aReadiness_ChecksOnly()
    {
        using var doc = await PdfVectorDocument.OpenAsync(PreflightSample());
        var report = await PdfPreflight.RunAsync(doc, PdfPreflightProfile.PdfX1aReadiness);
        _output.WriteLine(report.ToText());
        Assert.Contains(report.Findings, f => f.RuleId == "PP-8.1" && f.Severity == PdfPreflightSeverity.Error);
        Assert.Contains(report.Findings, f => f.RuleId == "PP-3.1" && f.Severity == PdfPreflightSeverity.Error);
        Assert.Contains(report.Findings, f => f.RuleId == "PP-6.1" && f.Severity == PdfPreflightSeverity.Error);
        Assert.Contains(report.Findings, f => f.RuleId == "PP-7.3");
        Assert.DoesNotContain(report.Findings, f => f.RuleId is "PP-2.1" or "PP-5.1"); // not in this profile
        Assert.Contains("nothing is converted", report.ToText());
    }

    [Fact]
    public async Task Preflight_CleanCmykFile_Passes()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0.1 0.2 0.3 0.4 k 10 10 100 100 re f", extra: "/TrimBox [0 0 200 200] /BleedBox [0 0 200 200]");
        var pdf = b.WithCatalog("/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (CGATS TR 001) >>]").Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        using var rasterizer = new Direct2DDisplayListRasterizer();
        foreach (var profile in PdfPreflightProfile.BuiltIn)
        {
            var report = await PdfPreflight.RunAsync(doc, profile, new PdfPreflightOptions { Rasterizer = rasterizer });
            _output.WriteLine(report.ToText());
            Assert.True(report.Passed);
            Assert.Equal(0, report.Warnings);
        }
    }
}
