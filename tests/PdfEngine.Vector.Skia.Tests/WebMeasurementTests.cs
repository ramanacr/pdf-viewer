using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// New measurements in the shared editing core (the Android app's Measure → Add to page): written
/// as the desktop viewer and Acrobat write them (/Measure, /IT, /Subj, a distance's caption and
/// arrows), read back as measurements, measured again when a point moves, one undo step. Every PDF
/// is built here.
/// </summary>
public class WebMeasurementTests
{
    private static byte[] Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static ViewerPoint P(double x, double y) => new(x, y);

    /// <summary>1 inch on paper is 10 feet.</summary>
    private static PdfMeasure Scale() => PdfMeasure.FromScale(1, "in", 10, "ft");

    [Fact]
    public async Task Distance_IsWrittenAsAMeasurement_AndReadBackAsOne()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        int version = session.Version;

        // 144 points (2 in) across the shown page.
        var added = session.AddMeasurement(PdfMeasurementKind.Distance, 1, [P(100, 100), P(244, 100)], Scale())!;
        Assert.NotNull(added);
        Assert.True(session.Version > version);
        Assert.Equal(ViewerAnnotationKind.Line, added.Kind);
        Assert.True(added.IsMeasurement);
        Assert.Equal(ViewerEditSession.MeasurementColor, added.Color);
        Assert.Contains("20", added.Contents);
        Assert.Equal(("OpenArrow", "OpenArrow"), (added.StartEnding, added.EndEnding));

        Assert.True((await session.ApplyAsync()).Written);
        string text = System.Text.Encoding.Latin1.GetString(doc.Bytes);
        Assert.Contains("/Measure", text);
        Assert.Contains("/LineDimension", text);
        Assert.Contains("/Subj", text);

        await using var reopened = await ViewerDocument.OpenAsync(doc.Bytes.ToArray());
        var read = Assert.Single(await reopened.ReadAnnotationsAsync());
        Assert.True(read.IsMeasurement);
        Assert.NotNull(read.Measure);
        Assert.Equal(added.Contents, read.Contents);
        Assert.Equal(added.Vertices, read.Vertices);
    }

    [Fact]
    public async Task Area_IsAPolygonMeasurement_MeasuredAgainWhenAPointMoves()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");

        // A 1 in × 1 in square: 100 sq ft at 1 in = 10 ft.
        var area = session.AddMeasurement(PdfMeasurementKind.Area, 1, [P(100, 100), P(172, 100), P(172, 172), P(100, 172)], Scale())!;
        Assert.Equal(ViewerAnnotationKind.Polygon, area.Kind);
        Assert.Contains("100", area.Contents);
        Assert.False(area.HasShapeStyle);

        // A point moved: the area is measured again; undo gives the first result back.
        Assert.True(session.MoveVertex(area.Id, 2, P(244, 172)));
        string moved = session.Store.Find(area.Id)!.Contents;
        Assert.NotEqual(area.Contents, moved);
        Assert.True(session.Undo());
        Assert.Equal(area.Contents, session.Store.Find(area.Id)!.Contents);

        Assert.True((await session.ApplyAsync()).Written);
        Assert.Contains("/PolygonDimension", System.Text.Encoding.Latin1.GetString(doc.Bytes));
    }

    [Fact]
    public async Task TooFewPoints_AddNothing()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        Assert.Null(session.AddMeasurement(PdfMeasurementKind.Distance, 1, [P(100, 100)], Scale()));
        Assert.Null(session.AddMeasurement(PdfMeasurementKind.Area, 1, [P(100, 100), P(200, 100)], Scale()));
        Assert.Empty(session.Store.Current);
    }
}
