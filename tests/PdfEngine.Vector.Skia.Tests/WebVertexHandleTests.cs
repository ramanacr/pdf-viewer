using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// One point of a line, arrow, polygon or polyline dragged on its own (the web viewer's vertex
/// handles, issue #139), in the shared editing core: only that point moves, the box follows it,
/// the point stays on the page, a measurement is measured again, one undo step, turned pages, and
/// what is refused. Every PDF is built here.
/// </summary>
public class WebVertexHandleTests
{
    private const string Inch = "/Measure << /Type /Measure /R (1 in = 1 in) /X [<< /U (in) /C 0.0138889 >>] /D [<< /U (in) /C 1 >>] /A [<< /U (sq in) /C 1 >>] >>";

    private static byte[] Blank(string extra = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: extra);
        return b.Build();
    }

    /// <summary>A Letter page with the given annotation dictionaries.</summary>
    private static byte[] WithAnnotations(params string[] annotations)
    {
        var b = new VectorPdfBuilder();
        var numbers = annotations.Select(a => b.Add(a)).ToList();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: "/Annots [" + string.Join(" ", numbers.Select(n => $"{n} 0 R")) + "]");
        return b.Build();
    }

    private static ViewerPoint P(double x, double y) => new(x, y);

    private static void Near(double expected, double actual, double tolerance = 0.01) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");

    private static ViewerBox Fitted(ViewerAnnotation a) =>
        ViewerBox.Around(a.Vertices, ViewerAnnotationStore.ShapeMargin(a.StrokeWidth, a.StartEnding, a.EndEnding));

    [Fact]
    public async Task Line_OnlyThePointMoves_TheBoxFollows_AndItIsOneUndoStep()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var line = session.AddLine(1, P(100, 100), P(300, 100), arrow: false)!;
        Assert.True(ViewerEditSession.HasVertexHandles(line));
        int version = session.Version;

        Assert.True(session.MoveVertex(line.Id, 1, P(300, 250)));
        var moved = session.Store.Find(line.Id)!;
        Assert.Equal([100.0, 692.0, 300.0, 542.0], moved.Vertices);
        Assert.Equal(Fitted(moved), moved.Box);
        Assert.True(session.Version > version);

        // One undo step gives the line back as it was; redo moves the point again.
        Assert.True(session.Undo());
        Assert.Equal(line.Vertices, session.Store.Find(line.Id)!.Vertices);
        Assert.Equal(line.Box, session.Store.Find(line.Id)!.Box);
        Assert.True(session.Redo());
        Assert.Equal(moved.Vertices, session.Store.Find(line.Id)!.Vertices);

        // Written: /L and /Rect follow the point.
        Assert.True((await session.ApplyAsync()).Written);
        await using var reopened = await ViewerDocument.OpenAsync(doc.Bytes.ToArray());
        var read = Assert.Single(await reopened.ReadAnnotationsAsync());
        Assert.Equal(moved.Vertices, read.Vertices);
        Near(moved.Box.X1, read.Box.X1, 0.001); Near(moved.Box.Y2, read.Box.Y2, 0.001);
    }

    [Fact]
    public async Task Arrow_KeepsItsEnding_AndTheBoxHoldsTheArrowhead()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var arrow = session.AddLine(1, P(100, 100), P(300, 300), arrow: true)!;

        Assert.True(session.MoveVertex(arrow.Id, 0, P(50, 400)));
        var moved = session.Store.Find(arrow.Id)!;
        Assert.Equal([50.0, 392.0, 300.0, 492.0], moved.Vertices);
        Assert.Equal(("None", "OpenArrow"), (moved.StartEnding, moved.EndEnding));
        Assert.Equal(Fitted(moved), moved.Box);
        Near(300 + ViewerAnnotationStore.ShapeMargin(moved.StrokeWidth, "None", "OpenArrow"), moved.Box.X2);
    }

    [Fact]
    public async Task PolygonAndPolyline_OnlyThatPointMoves_AndAPointOffThePageStaysOnIt()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var polygon = session.AddPolyline(ViewerAnnotationKind.Polygon, 1, [P(100, 100), P(200, 100), P(150, 200)])!;
        var polyline = session.AddPolyline(ViewerAnnotationKind.PolyLine, 1, [P(300, 300), P(400, 300), P(400, 400)])!;

        Assert.True(session.MoveVertex(polygon.Id, 2, P(150, 250)));
        var p = session.Store.Find(polygon.Id)!;
        Assert.Equal(polygon.Vertices.Take(4), p.Vertices.Take(4));
        Assert.Equal((150.0, 542.0), (p.Vertices[4], p.Vertices[5]));
        Assert.Equal(Fitted(p), p.Box);

        // Dragged past the page's corner: the point stops at the edge, the box is cut to the page.
        Assert.True(session.MoveVertex(polyline.Id, 0, P(-50, 900)));
        var l = session.Store.Find(polyline.Id)!;
        Assert.Equal((0.0, 0.0), (l.Vertices[0], l.Vertices[1]));
        Assert.Equal(polyline.Vertices.Skip(2), l.Vertices.Skip(2));
        Assert.Equal((0.0, 0.0), (l.Box.X1, l.Box.Y1));
        Assert.Equal(Fitted(l).X2, l.Box.X2);

        // Two moves, two undo steps.
        Assert.True(session.Undo());
        Assert.Equal(polyline.Vertices, session.Store.Find(polyline.Id)!.Vertices);
        Assert.Equal(p.Vertices, session.Store.Find(polygon.Id)!.Vertices);
        Assert.True(session.Undo());
        Assert.Equal(polygon.Vertices, session.Store.Find(polygon.Id)!.Vertices);
    }

    [Fact]
    public async Task WhatCannotMove_IsRefused_OrIsNoStep()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var polygon = session.AddPolyline(ViewerAnnotationKind.Polygon, 1, [P(100, 100), P(200, 100), P(150, 200)])!;
        var line = session.AddLine(1, P(100, 300), P(300, 300), arrow: false)!;
        var square = session.AddShape(ViewerAnnotationKind.Square, 1, P(400, 400), P(450, 450))!;
        int version = session.Store.Version;

        // An index that is not one of its points, another type, a point that is not a number, an id that is gone.
        Assert.Throws<ViewerAnnotationException>(() => session.MoveVertex(polygon.Id, 3, P(10, 10)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveVertex(polygon.Id, -1, P(10, 10)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveVertex(line.Id, 2, P(10, 10)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveVertex(square.Id, 0, P(10, 10)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveVertex(polygon.Id, 0, P(double.NaN, 10)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveVertex("nobody", 0, P(10, 10)));
        Assert.Throws<ViewerAnnotationException>(() => session.Store.MoveVertex(polygon.Id, 9, 10, 10, DateTimeOffset.UtcNow));
        Assert.Throws<ViewerAnnotationException>(() => session.Store.MoveVertex(polygon.Id, 0, double.PositiveInfinity, 10, DateTimeOffset.UtcNow));

        // The same point, and a line made shorter than a shape's least size, are no step.
        Assert.False(session.MoveVertex(polygon.Id, 0, P(100, 100)));
        Assert.False(session.MoveVertex(line.Id, 1, P(102, 300)));
        Assert.Equal(version, session.Store.Version);
        Assert.False(ViewerEditSession.HasVertexHandles(square));

        // A read-only session refuses.
        await using var ro = await ViewerDocument.OpenAsync(Blank());
        var readOnly = await ViewerEditSession.StartAsync(ro, new ViewerEditOptions { ReadOnlyReason = "Read-only here." });
        Assert.Throws<ViewerAnnotationException>(() => readOnly.MoveVertex(polygon.Id, 0, P(10, 10)));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task OnATurnedPage_ThePointGoesWhereTheReaderDraggedIt(int rotate)
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank($"/Rotate {rotate}"));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[0];
        var line = session.AddLine(1, P(100, 100), P(300, 100), arrow: false)!;
        var polygon = session.AddPolyline(ViewerAnnotationKind.Polygon, 1, [P(100, 200), P(200, 200), P(150, 300)])!;

        Assert.True(session.MoveVertex(line.Id, 1, P(300, 250)));
        Assert.True(session.MoveVertex(polygon.Id, 1, P(250, 220)));
        var l = session.Store.Find(line.Id)!;
        var shown = g.ToShown(l.Vertices[2], l.Vertices[3]);
        Near(300, shown.X); Near(250, shown.Y);
        var first = g.ToShown(l.Vertices[0], l.Vertices[1]);
        Near(100, first.X); Near(100, first.Y);
        var p = session.Store.Find(polygon.Id)!;
        var moved = g.ToShown(p.Vertices[2], p.Vertices[3]);
        Near(250, moved.X); Near(220, moved.Y);
        Assert.Equal(Fitted(p), p.Box);

        // Past the shown page's edge: kept on it.
        Assert.True(session.MoveVertex(polygon.Id, 0, P(g.ShownWidth + 80, -40)));
        var edge = g.ToShown(session.Store.Find(polygon.Id)!.Vertices[0], session.Store.Find(polygon.Id)!.Vertices[1]);
        Near(g.ShownWidth, edge.X); Near(0, edge.Y);
    }

    [Fact]
    public async Task DistanceMeasurement_IsMeasuredAgain_AndKeepsItsMeasure()
    {
        byte[] file = WithAnnotations(
            "<< /Type /Annot /Subtype /Line /Rect [60 190 160 210] /L [72 200 144 200] /LE [/OpenArrow /OpenArrow] /IT /LineDimension /Cap true "
            + "/Contents (1 in) /BS << /W 1 >> " + Inch + " >>");
        await using var doc = await ViewerDocument.OpenAsync(file);
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[0];
        var m = Assert.Single(session.Store.Current);
        Assert.True(m.IsMeasurement);
        Assert.NotNull(m.Measure);
        Assert.True(ViewerEditSession.HasVertexHandles(m));

        // The second point moved to 3 inches from the first.
        var to = g.ToShown(72 + 216, 200);
        Assert.True(session.MoveVertex(m.Id, 1, P(to.X, to.Y)));
        var moved = session.Store.Find(m.Id)!;
        Assert.Equal([72.0, 200.0, 288.0, 200.0], moved.Vertices);
        string expected = PdfMeasurements.Label(PdfMeasurementKind.Distance, [new PdfPoint(72, 200), new PdfPoint(288, 200)], m.Measure!);
        Assert.Equal(expected, moved.Contents);
        Assert.StartsWith("3", moved.Contents);
        Assert.True(moved.IsMeasurement);
        Assert.Equal(("OpenArrow", "OpenArrow"), (moved.StartEnding, moved.EndEnding));
        var bounds = PdfMeasurements.Bounds(PdfMeasurementKind.Distance, [new PdfPoint(72, 200), new PdfPoint(288, 200)], expected, 1);
        Assert.Equal(new ViewerBox(bounds.X, bounds.Y, bounds.Right, bounds.Bottom), moved.Box);

        // Written with its /Measure, its new /L and its result as /Contents.
        Assert.True((await session.ApplyAsync()).Written);
        using var saved = await PdfVectorDocument.OpenAsync(doc.Bytes.ToArray());
        var annot = Assert.Single(PdfAnnotationReader.Read(saved, 1));
        Assert.NotNull(annot.Measure);
        Assert.Equal([72.0, 200.0, 288.0, 200.0], annot.Line!);
        Assert.Equal(expected, annot.Contents);

        // Undo gives back the old result.
        Assert.True(session.Undo());
        Assert.Equal("1 in", session.Store.Find(m.Id)!.Contents);
    }

    [Fact]
    public async Task AreaMeasurement_IsMeasuredAgain_AlsoAfterKeptEditsWithoutItsMeasure()
    {
        byte[] file = WithAnnotations(
            "<< /Type /Annot /Subtype /Polygon /Rect [60 60 220 220] /Vertices [72 72 216 72 216 216 72 216] /IT /PolygonDimension "
            + "/Contents (4 sq in) /BS << /W 1 >> " + Inch + " >>");
        await using var doc = await ViewerDocument.OpenAsync(file);
        var fromFile = await doc.ReadAnnotationsAsync();
        var m = Assert.Single(fromFile);
        var store = new ViewerAnnotationStore(doc.Geometry, fromFile);

        // As Android gives back kept edits: the annotation without its scale (a snapshot does not hold it).
        store.Restore([m with { Measure = null, Opacity = 0.5 }]);
        Assert.Null(store.Current[0].Measure);
        // The top-right corner moved up an inch: 2 x 2 in becomes 2 x 2 plus a triangle of 1 sq in.
        var moved = store.MoveVertex(m.Id, 2, 216, 288, DateTimeOffset.UtcNow);
        Assert.NotNull(moved.Measure);
        Assert.Equal(
            PdfMeasurements.Label(PdfMeasurementKind.Area, [new PdfPoint(72, 72), new PdfPoint(216, 72), new PdfPoint(216, 288), new PdfPoint(72, 216)], m.Measure!),
            moved.Contents);
        Assert.StartsWith("5", moved.Contents);
        Assert.Equal(0.5, moved.Opacity);
    }
}
