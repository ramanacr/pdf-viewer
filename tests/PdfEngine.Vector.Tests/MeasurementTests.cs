using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Measuring (ISO 32000-2 12.9): scale ratios and units against values worked out by hand, number
/// formats, page viewports, the measurement annotations Acrobat reads (written, read back and drawn),
/// and snapping to the page's vector drawing.
/// </summary>
public class MeasurementTests
{
    private static readonly PdfPoint O = new(0, 0);

    private static void Near(double expected, double actual, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    // ------------------------------------------------------------------ scales and units

    [Fact]
    public void OneInchToTenFeet_AnInchOnThePageIsTenFeet()
    {
        var m = PdfMeasure.FromScale(1, "in", 10, "ft");
        Assert.Equal("1 in = 10 ft", m.Ratio);
        Near(10.0 / 72, m.X[0].Factor, 1e-12);          // one point is 1/72 in, 10/72 ft
        Near(10, m.DistanceBetween(O, new PdfPoint(72, 0)));
        // A 3-4-5 triangle: 36 by 48 points is 60 points, 60/72 in, 8.333 ft.
        Near(60.0 / 72 * 10, m.DistanceBetween(new PdfPoint(10, 10), new PdfPoint(46, 58)));
        Assert.Equal("8.33 ft", m.FormatDistance(m.DistanceBetween(new PdfPoint(10, 10), new PdfPoint(46, 58))));
    }

    [Fact]
    public void MetricScales_MatchHandWorkedValues()
    {
        // 1:1000 in millimetres to metres: 100 pt = 100/72 in = 35.2778 mm on paper = 35.2778 m.
        var m = PdfMeasure.FromScale(1, "mm", 1, "m");
        Near(100 / 72.0 * 25.4, m.DistanceBetween(O, new PdfPoint(100, 0)), 1e-9);
        // 2 cm = 5 km: 72 pt = 2.54 cm = 6.35 km.
        var k = PdfMeasure.FromScale(2, "cm", 5, "km");
        Near(6.35, k.DistanceBetween(O, new PdfPoint(0, 72)), 1e-9);
        // 1 in = 1 mi: 144 pt = 2 mi. 1 pt = 1 pt: 50 pt = 50 pt.
        Near(2, PdfMeasure.FromScale(1, "in", 1, "mi").DistanceBetween(O, new PdfPoint(144, 0)), 1e-9);
        Near(50, PdfMeasure.FromScale(1, "pt", 1, "pt").DistanceBetween(O, new PdfPoint(30, 40)), 1e-9);
        Assert.Throws<ArgumentException>(() => PdfMeasure.FromScale(1, "in", 1, "furlong"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfMeasure.FromScale(0, "in", 1, "ft"));
    }

    [Fact]
    public void PerimeterAreaAndAngle_MatchHandWorkedValues()
    {
        var m = PdfMeasure.FromScale(1, "in", 10, "ft");
        // A rectangle 1 in by 2 in on the page: 10 ft by 20 ft.
        var rect = new[] { new PdfPoint(100, 100), new PdfPoint(172, 100), new PdfPoint(172, 244), new PdfPoint(100, 244) };
        Near(60, m.LengthOf(rect, closed: true), 1e-9);
        Near(40, m.LengthOf(rect, closed: false), 1e-9);          // three sides of a polyline: 10 + 20 + 10
        Near(200, m.AreaOf(rect), 1e-9);
        Near(200, m.AreaOf(rect.Reverse().ToArray()), 1e-9);       // either winding
        Assert.Equal("200.00 sq ft", m.FormatArea(m.AreaOf(rect)));
        // A right triangle, legs 2 in and 1 in at 1 in = 1 in: 1 sq in.
        var unit = PdfMeasure.FromScale(1, "in", 1, "in");
        Near(1, unit.AreaOf(new[] { O, new PdfPoint(144, 0), new PdfPoint(0, 72) }), 1e-9);
        Near(45, m.AngleOf(O, new PdfPoint(10, 10)), 1e-9);
        Near(135, m.AngleOf(O, new PdfPoint(-10, 10)), 1e-9);
        Near(270, m.AngleOf(O, new PdfPoint(0, -5)), 1e-9);
        Assert.Equal("45.0°", m.FormatAngle(45));
        Assert.Equal("30°", new PdfMeasure().FormatAngle(30)); // a measure without /T: whole degrees as they are
    }

    [Fact]
    public void YAxisScaleAndCyx_AreApplied()
    {
        // x: 1 in per inch; y: 2 in per inch on the page, given in y units that CYX converts.
        var m = new PdfMeasure
        {
            X = new[] { new PdfNumberFormat { Unit = "in", Factor = 1 / 72.0 } },
            Y = new[] { new PdfNumberFormat { Unit = "yu", Factor = 4 / 72.0 } },
            Cyx = 0.5,
            Distance = new[] { new PdfNumberFormat { Unit = "in" } },
            Area = new[] { new PdfNumberFormat { Unit = "sq in" } },
        };
        Near(2, m.DistanceBetween(O, new PdfPoint(0, 72)), 1e-9);
        Near(5, m.DistanceBetween(O, new PdfPoint(216, 144)), 1e-9); // 3 in across, 4 in up
        Near(2, m.AreaOf(new[] { O, new PdfPoint(72, 0), new PdfPoint(72, 72), new PdfPoint(0, 72) }), 1e-9);
    }

    [Fact]
    public void NumberFormats_DecimalsFractionsSeparatorsAndChains()
    {
        Assert.Equal("1,234.57 m", new PdfNumberFormat { Unit = "m" }.FormatWithUnit(1234.5678));
        Assert.Equal("1.234,6 m", new PdfNumberFormat { Unit = "m", Precision = 10, ThousandsSeparator = ".", DecimalSeparator = "," }.FormatWithUnit(1234.56));
        Assert.Equal("2 9/16 in", new PdfNumberFormat { Unit = "in", Style = "F", Precision = 16 }.FormatWithUnit(2.5625));
        Assert.Equal("2 1/2 in", new PdfNumberFormat { Unit = "in", Style = "F", Precision = 16 }.FormatWithUnit(2.5));
        Assert.Equal("2 8/16 in", new PdfNumberFormat { Unit = "in", Style = "F", Precision = 16, FixedDenominator = true }.FormatWithUnit(2.5));
        Assert.Equal("3 in", new PdfNumberFormat { Unit = "in", Style = "R" }.FormatWithUnit(2.5));
        Assert.Equal("2 in", new PdfNumberFormat { Unit = "in", Style = "T" }.FormatWithUnit(2.99));
        Assert.Equal("$ 12.50", new PdfNumberFormat { Unit = "$", LabelFirst = true }.FormatWithUnit(12.5));

        // Feet and inches: the whole feet, then the rest converted to inches.
        var chain = new[] { new PdfNumberFormat { Unit = "ft", Factor = 1 }, new PdfNumberFormat { Unit = "in", Factor = 12, Precision = 1 } };
        Assert.Equal("5 ft 3 in", PdfMeasure.Format(chain, 5.25));
        Assert.Equal("6 ft 0 in", PdfMeasure.Format(chain, 5.999));
        var measure = new PdfMeasure { X = new[] { new PdfNumberFormat { Unit = "ft", Factor = 1 / 72.0 } }, Distance = chain, Area = new[] { new PdfNumberFormat { Unit = "sq ft" } } };
        Assert.Equal("1 ft 6 in", measure.FormatDistance(measure.DistanceBetween(O, new PdfPoint(108, 0))));
    }

    // ------------------------------------------------------------------ viewports

    private const string ViewportMeasure =
        "<< /Type /Measure /Subtype /RL /R (1 in = 2 m) /X [<< /Type /NumberFormat /U (m) /C 0.0277778 /D 100 >>] " +
        "/D [<< /Type /NumberFormat /U (m) /C 1 /D 100 >>] /A [<< /Type /NumberFormat /U (sq m) /C 1 /D 100 >>] >>";

    [Fact]
    public async Task Viewports_TheLastOneContainingThePointApplies_AndGeospatialIsFlagged()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(string.Empty, mediaBox: "[0 0 300 300]", extra:
            $"/VP [<< /Type /Viewport /BBox [0 0 100 100] /Name (Plan) /Measure {ViewportMeasure} >> " +
            "<< /Type /Viewport /BBox [50 50 200 200] /Name (Detail) /Measure << /Type /Measure /Subtype /RL /R (1 in = 1 in) " +
            "/X [<< /U (in) /C 0.0138889 >>] /D [<< /U (in) /C 1 >>] /A [<< /U (sq in) /C 1 >>] >> >> " +
            "<< /Type /Viewport /BBox [220 220 290 290] /Measure << /Type /Measure /Subtype /GEO >> >>]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        var viewports = PdfViewports.Read(doc, 1);
        Assert.Equal(3, viewports.Count);
        Assert.Equal("Plan", PdfViewports.At(viewports, new PdfPoint(10, 10))!.Name);
        Assert.Equal("Detail", PdfViewports.At(viewports, new PdfPoint(60, 60))!.Name);   // in both: the later one
        Assert.Null(PdfViewports.At(viewports, new PdfPoint(150, 10)));
        var plan = viewports[0].Measure!;
        Assert.Equal("1 in = 2 m", plan.Ratio);
        Near(2, plan.DistanceBetween(O, new PdfPoint(72, 0)), 1e-4);
        Assert.True(viewports[2].IsGeospatial);
        Assert.Null(viewports[2].Measure); // geospatial measures are not modelled
    }

    // ------------------------------------------------------------------ measurement annotations

    private static async Task<byte[]> Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(string.Empty, mediaBox: "[0 0 400 400]");
        return await Task.FromResult(b.Build());
    }

    private static string Decode(PdfStream form)
    {
        using var z = new ZLibStream(new MemoryStream(form.GetRawBytes().ToArray()), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return Encoding.Latin1.GetString(o.ToArray());
    }

    [Fact]
    public async Task MeasureDictionary_RoundTripsThroughSaveAndRead_AsAcrobatWritesIt()
    {
        byte[] bytes = await Blank();
        var scale = PdfMeasure.FromScale(1, "in", 10, "ft");
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var distance = PdfMeasurements.Create(PdfMeasurementKind.Distance, 1, new[] { new PdfPoint(50, 50), new PdfPoint(122, 50) }, scale, new[] { 1.0, 0, 0 });
        var perimeter = PdfMeasurements.Create(PdfMeasurementKind.Perimeter, 1, new[] { new PdfPoint(50, 100), new PdfPoint(122, 100), new PdfPoint(122, 172) }, scale);
        var area = PdfMeasurements.Create(PdfMeasurementKind.Area, 1, new[] { new PdfPoint(200, 200), new PdfPoint(272, 200), new PdfPoint(272, 344), new PdfPoint(200, 344) }, scale);
        Assert.Equal("10.00 ft", distance.Contents);
        Assert.Equal("20.00 ft", perimeter.Contents);
        Assert.Equal("200.00 sq ft", area.Contents);

        var written = PdfAnnotationEditor.Apply(doc, bytes, e => { e.Add(distance); e.Add(perimeter); e.Add(area); });
        using var reopened = await PdfVectorDocument.OpenAsync(written);
        var read = PdfAnnotationReader.Read(reopened, 1);
        Assert.Equal(new[] { "Line", "PolyLine", "Polygon" }, read.Select(a => a.Subtype));
        Assert.Equal(new[] { "LineDimension", "PolyLineDimension", "PolygonDimension" }, read.Select(a => a.Intent));
        Assert.All(read, a => Assert.True(scale.SameAs(a.Measure), $"{a.Subtype}: the measure did not round-trip"));
        Assert.All(read, a => Assert.True(a.HasAppearance));
        Assert.True(read[0].Caption);
        Assert.Equal(new[] { 50.0, 50, 122, 50 }, read[0].Line);
        Assert.Equal(new[] { "OpenArrow", "OpenArrow" }, read[0].LineEndings);
        Assert.Equal("10.00 ft", read[0].Contents);
        Assert.Equal(4, read[2].Vertices.Count);
        Near(200, read[2].Measure!.AreaOf(read[2].Vertices), 1e-6);

        // The dictionaries as Acrobat reads them.
        var r = reopened.Resolver;
        var annots = (PdfArray)r.Resolve(reopened.PageTree.Pages[0].Dictionary["Annots"])!;
        var line = (PdfDictionary)r.Resolve(annots[0])!;
        var measure = (PdfDictionary)r.Resolve(line["Measure"])!;
        Assert.Equal("Measure", measure.GetName("Type"));
        Assert.Equal("RL", measure.GetName("Subtype"));
        Assert.Equal("1 in = 10 ft", PdfTextStrings.Decode(r.Resolve(measure["R"])));
        foreach (string key in new[] { "X", "D", "A", "T" })
        {
            var format = (PdfDictionary)r.Resolve(((PdfArray)r.Resolve(measure[key])!)[0])!;
            Assert.Equal("NumberFormat", format.GetName("Type"));
            Assert.NotNull(format["U"]);
            Assert.NotNull(format["C"]);
        }
        Assert.True(r.Resolve(line["Cap"]) is PdfBoolean { Value: true });
        // Every measurement draws its result: the line as its caption, the others on a tab.
        foreach (var item in annots)
        {
            var d = (PdfDictionary)r.Resolve(item)!;
            var form = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(d["AP"])!)["N"])!;
            Assert.Contains("BT", Decode(form));
            var rect = PdfDocumentInfo.ReadRect(d["Rect"], r)!.Value;
            var vertices = PdfDocumentInfo.Numbers(r.Resolve(d["Vertices"] ?? d["L"]), r)!;
            for (int i = 0; i + 1 < vertices.Length; i += 2)
                Assert.True(rect.Contains(vertices[i], vertices[i + 1]), "the rectangle holds every point");
        }
    }

    [Fact]
    public async Task ChangingAMeasurement_KeepsItsMeasure_AndADeleteRemovesIt()
    {
        byte[] bytes = await Blank();
        var scale = PdfMeasure.FromScale(1, "cm", 1, "m");
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var first = PdfAnnotationEditor.Apply(doc, bytes, e => e.Add(PdfMeasurements.Create(PdfMeasurementKind.Distance, 1, new[] { new PdfPoint(10, 10), new PdfPoint(100, 10) }, scale)));
        using var one = await PdfVectorDocument.OpenAsync(first);
        var a = PdfAnnotationReader.Read(one, 1).Single();
        a.Color = new[] { 0.0, 0.5, 0 };
        var second = PdfAnnotationEditor.Apply(one, first, e => e.Modify(a));
        using var two = await PdfVectorDocument.OpenAsync(second);
        var changed = PdfAnnotationReader.Read(two, 1).Single();
        Assert.True(scale.SameAs(changed.Measure));
        Assert.Equal(new[] { 0.0, 0.5, 0 }, changed.Color);
        var third = PdfAnnotationEditor.Apply(two, second, e => e.Delete(1, 0));
        using var three = await PdfVectorDocument.OpenAsync(third);
        Assert.Empty(PdfAnnotationReader.Read(three, 1));
    }

    [Fact]
    public async Task AMeasurement_IsDrawnAndFlattened()
    {
        byte[] bytes = await Blank();
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var scale = PdfMeasure.FromScale(1, "in", 1, "in");
        var area = PdfMeasurements.Create(PdfMeasurementKind.Area, 1, new[] { new PdfPoint(100, 100), new PdfPoint(300, 100), new PdfPoint(300, 300), new PdfPoint(100, 300) }, scale, new[] { 0.0, 0, 1 }, 2);
        var written = PdfAnnotationEditor.Apply(doc, bytes, e => { e.Add(area); e.FlattenAll(); });
        using var flat = await PdfVectorDocument.OpenAsync(written);
        Assert.Empty(PdfAnnotationReader.Read(flat, 1));
        var list = await flat.GetPageDisplayListAsync(1);
        Assert.Contains(list.Commands, c => c is StrokePath);
        Assert.Contains(list.Commands, c => c is DrawGlyphRun);  // the label "7.72 sq in"
    }

    // ------------------------------------------------------------------ snapping

    private static async Task<PdfSnapIndex> Snaps(string content)
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, mediaBox: "[0 0 400 400]");
        using var doc = await PdfVectorDocument.OpenAsync(b.Build());
        return PdfSnapIndex.Build(await doc.GetPageDisplayListAsync(1));
    }

    [Fact]
    public async Task Snapping_PrefersEndpoints_ThenMidpoints_ThenThePath()
    {
        var index = await Snaps("1 w 10 10 m 110 10 l S");
        Assert.Equal(new PdfSnap(new PdfPoint(10, 10), PdfSnapKind.Endpoint), index.Find(new PdfPoint(12, 11), 5));
        Assert.Equal(new PdfSnap(new PdfPoint(60, 10), PdfSnapKind.Midpoint), index.Find(new PdfPoint(61, 12), 5));
        var on = index.Find(new PdfPoint(85, 13), 5);
        Assert.Equal(PdfSnapKind.OnPath, on.Kind);
        Near(85, on.Point.X); Near(10, on.Point.Y);
        Assert.Equal(PdfSnapKind.None, index.Find(new PdfPoint(85, 30), 5).Kind);
        // Within the tolerance of an end point and the path, the end point wins.
        Assert.Equal(PdfSnapKind.Endpoint, index.Find(new PdfPoint(106, 9), 5).Kind);
    }

    [Fact]
    public async Task Snapping_FollowsTransforms_ClosedPaths_AndCurves()
    {
        // A rectangle drawn at half size and moved: its corners are in page space where it shows.
        var index = await Snaps("q 2 0 0 2 100 100 cm 0 0 50 25 re f Q 0 0 1 RG 300 300 m 300 355.23 344.77 400 400 400 c S");
        Assert.Equal(new PdfPoint(200, 150), index.Find(new PdfPoint(198, 149), 4).Point);
        Assert.Equal(PdfSnapKind.Endpoint, index.Find(new PdfPoint(101, 101), 4).Kind);
        Assert.Equal(new PdfPoint(100, 125), index.Find(new PdfPoint(102, 124), 4).Point); // the closing edge's middle
        Assert.Equal(PdfSnapKind.Endpoint, index.Find(new PdfPoint(399, 399), 3).Kind);   // the curve's end
        var onCurve = index.Find(new PdfPoint(316, 361), 4);                               // near the quarter circle
        Assert.Equal(PdfSnapKind.OnPath, onCurve.Kind);
        double r = Math.Sqrt(Math.Pow(onCurve.Point.X - 400, 2) + Math.Pow(onCurve.Point.Y - 300, 2));
        Near(100, r, 0.6);
    }
}
