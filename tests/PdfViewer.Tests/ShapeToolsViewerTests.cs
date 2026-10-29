using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The line, arrow, polygon and connected-lines tools: drawn, moved, undone and redone, saved as
/// Line, Polygon and PolyLine annotations with appearances, and read back as the same tools,
/// while measurements stay measurements.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class ShapeToolsViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ShapeTools_" + Guid.NewGuid().ToString("N"));

    public ShapeToolsViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir); // measuring scales go here
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A blank 612 x 792 page, made here.</summary>
    private string Blank(string name)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 w 72 720 m 272 720 l S", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    /// <summary>A point of default user space on page 1 (612 x 792), normalized as the viewer keeps it.</summary>
    private static Point N(double x, double y) => new(x / 612, 1 - y / 792);

    private static async Task<PdfAnnotation[]> ReadSaved(string path)
    {
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        return PdfAnnotationReader.Read(doc, 1).ToArray();
    }

    private static void Draw(MainViewModel vm, string tool, params Point[] points)
    {
        vm.ToggleAnnotationTool(tool);
        foreach (var p in points) vm.ShapeClick(1, p);
        Assert.True(vm.FinishShape());
    }

    // ------------------------------------------------------------------ drawing

    [Fact]
    public async Task Line_IsAddedByADrag_AndUndoable()
    {
        using var vm = await Open(Blank("line.pdf"));
        vm.ToggleAnnotationTool("Line");
        Assert.Equal(AnnotationType.Line, vm.ActiveAnnotationTool);
        Assert.False(vm.AddLineShape(1, N(100, 400), N(101, 400))); // a click is not a line
        Assert.Empty(vm.AllAnnotations);

        Assert.True(vm.AddLineShape(1, N(100, 400), N(300, 500)));
        var line = Assert.Single(vm.AllAnnotations);
        Assert.Equal(AnnotationType.Line, line.Type);
        Assert.True(line.IsVertexShape);
        Assert.Null(line.LineEndings);
        Assert.Equal(new[] { N(100, 400), N(300, 500) }, line.Vertices);
        // The box holds both ends.
        Assert.True(line.X < N(100, 400).X && line.X + line.Width > N(300, 500).X);
        Assert.Equal("_Undo Add Line", vm.UndoHeader);
        Assert.True(vm.HasUnsavedChanges);

        await vm.UndoAsync();
        Assert.Empty(vm.AllAnnotations);
        Assert.Empty(vm.Pages[0].AnnotationsOnPage);
        await vm.RedoAsync();
        Assert.Same(line, Assert.Single(vm.Pages[0].AnnotationsOnPage));
        Assert.Equal(2, line.Vertices.Count);
    }

    [Fact]
    public async Task PolygonAndConnectedLines_TakeAPointPerClick_WithBackspaceAndEsc()
    {
        using var vm = await Open(Blank("poly.pdf"));
        vm.ToggleAnnotationTool("Polygon");
        vm.ShapeClick(1, N(100, 100));
        vm.ShapeClick(1, N(100, 100)); // the first click of a double-click: the same point
        vm.ShapeClick(1, N(200, 100));
        Assert.Equal(2, vm.ShapePreviewPoints.Count);
        Assert.False(vm.FinishShape()); // a polygon needs three points
        Assert.Empty(vm.AllAnnotations);
        vm.ShapeClick(1, N(250, 150));
        vm.UndoShapePoint();             // Backspace
        Assert.Equal(2, vm.ShapePreviewPoints.Count);
        vm.ShapeClick(1, N(200, 200));
        Assert.True(vm.FinishShape());
        var polygon = Assert.Single(vm.AllAnnotations);
        Assert.Equal(AnnotationType.Polygon, polygon.Type);
        Assert.Equal(3, polygon.Vertices.Count);
        Assert.False(vm.IsDrawingShapePoints);
        Assert.Equal(AnnotationType.Polygon, vm.ActiveAnnotationTool); // the tool stays on

        vm.ToggleAnnotationTool("PolyLine");
        vm.ShapeClick(1, N(300, 300));
        vm.ShapeClick(1, N(350, 320));
        vm.CancelShape();                // Esc
        Assert.False(vm.IsDrawingShapePoints);
        Assert.Single(vm.AllAnnotations);
        vm.ShapeClick(1, N(300, 300));
        Assert.False(vm.FinishShape()); // one point is not enough
        vm.ShapeClick(1, N(350, 320));
        Assert.True(vm.FinishShape());
        var lines = vm.AllAnnotations[1];
        Assert.Equal(AnnotationType.PolyLine, lines.Type);
        Assert.Equal("_Undo Add Connected Lines", vm.UndoHeader);

        // Switching tools drops points placed and not finished.
        vm.ShapeClick(1, N(400, 400));
        vm.ToggleAnnotationTool("Rectangle");
        Assert.False(vm.IsDrawingShapePoints);
    }

    [Fact]
    public void ShapeGeometry_DrawsArrowHeads_AndClosesOnlyThePolygon()
    {
        var arrow = new AnnotationModel { Type = AnnotationType.Arrow, X = 0, Y = 0, Width = 0.5, Height = 0.5, StrokeThickness = 2, LineEndings = new[] { "None", "OpenArrow" } };
        arrow.Vertices = new() { new Point(0.1, 0.25), new Point(0.4, 0.25) };
        var path = Assert.IsType<PathGeometry>(arrow.ShapeGeometry(100, 100));
        Assert.Equal(2, path.Figures.Count); // the line and its head
        Assert.Equal(new Point(80, 50), path.Figures[1].Segments.OfType<LineSegment>().First().Point); // the head's tip is the end

        var line = new AnnotationModel { Type = AnnotationType.Line, X = 0, Y = 0, Width = 1, Height = 1 };
        line.Vertices = new() { new Point(0.1, 0.1), new Point(0.9, 0.9) };
        Assert.Single(((PathGeometry)line.ShapeGeometry(100, 100)!).Figures);

        var polygon = new AnnotationModel { Type = AnnotationType.Polygon, X = 0, Y = 0, Width = 1, Height = 1 };
        polygon.Vertices = new() { new Point(0.1, 0.1), new Point(0.9, 0.1), new Point(0.5, 0.9) };
        Assert.True(((PathGeometry)polygon.ShapeGeometry(100, 100)!).Figures[0].IsClosed);
        var open = new AnnotationModel { Type = AnnotationType.PolyLine, X = 0, Y = 0, Width = 1, Height = 1 };
        open.Vertices = polygon.Vertices.ToList();
        Assert.False(((PathGeometry)open.ShapeGeometry(100, 100)!).Figures[0].IsClosed);

        // A measurement is not drawn by this path.
        var measurement = new AnnotationModel { Type = AnnotationType.Measurement, X = 0, Y = 0, Width = 1, Height = 1 };
        measurement.Vertices = line.Vertices.ToList();
        Assert.Null(measurement.ShapeGeometry(100, 100));
    }

    // ------------------------------------------------------------------ saving and reopening

    [Fact]
    public async Task AllFourShapes_AreSavedWithAppearances_AndReopenAsTheSameTools()
    {
        string path = Blank("saved.pdf");
        using (var vm = await Open(path))
        {
            vm.SelectedAnnotationColor = "#2979FF";
            vm.SelectedAnnotationThickness = 3;
            vm.ToggleAnnotationTool("Line");
            Assert.True(vm.AddLineShape(1, N(100, 700), N(300, 700)));
            vm.ToggleAnnotationTool("Arrow");
            Assert.True(vm.AddLineShape(1, N(100, 600), N(300, 650)));
            Draw(vm, "Polygon", N(100, 300), N(250, 300), N(175, 400));
            Draw(vm, "PolyLine", N(350, 300), N(400, 380), N(450, 300), N(500, 380));
            await vm.SaveAsync();
            Assert.False(vm.HasUnsavedChanges);
        }

        var saved = await ReadSaved(path);
        Assert.Equal(new[] { "Line", "Line", "Polygon", "PolyLine" }, saved.Select(a => a.Subtype));
        Assert.All(saved, a => Assert.Null(a.Measure));
        Assert.All(saved, a => Assert.True(a.HasAppearance, a.Subtype + " has no appearance"));
        Assert.All(saved, a => Assert.Equal(3, a.BorderWidth));
        Assert.Equal(new[] { 100.0, 700, 300, 700 }, saved[0].Line!.Select(v => Math.Round(v, 3)));
        Assert.Null(saved[0].LineEndings);
        Assert.Equal(new[] { "None", "OpenArrow" }, saved[1].LineEndings);
        Assert.Equal(new[] { 100.0, 600, 300, 650 }, saved[1].Line!.Select(v => Math.Round(v, 3)));
        // The rectangle holds the arrow head (nine line widths long, half of that each side).
        Assert.True(saved[1].Rect.X + saved[1].Rect.Width >= 300 && saved[1].Rect.Y + saved[1].Rect.Height >= 650 + 4.5 * 3 * 0.5);
        Assert.Equal(3, saved[2].Vertices.Count);
        Assert.Equal(4, saved[3].Vertices.Count);
        Assert.Equal(175, saved[2].Vertices[2].X, 3);
        Assert.Equal(400, saved[2].Vertices[2].Y, 3);

        using var reopened = await Open(path);
        Assert.Equal(new[] { AnnotationType.Line, AnnotationType.Arrow, AnnotationType.Polygon, AnnotationType.PolyLine },
            reopened.AllAnnotations.Select(a => a.Type));
        Assert.All(reopened.AllAnnotations, a => Assert.True(a.HasOwnAppearance));
        Assert.All(reopened.AllAnnotations, a => Assert.Equal(3, a.StrokeThickness));
        var arrow = reopened.AllAnnotations[1];
        Assert.Equal(new[] { "None", "OpenArrow" }, arrow.LineEndings);
        Assert.Equal(N(300, 650).X, arrow.Vertices[1].X, 6);
        Assert.Equal(N(300, 650).Y, arrow.Vertices[1].Y, 6);
        Assert.Equal(4, reopened.AllAnnotations[3].Vertices.Count);
        Assert.False(reopened.HasUnsavedChanges);

        // Saved again untouched, the file keeps them as they were.
        await reopened.SaveAsync();
        Assert.Equal(saved.Select(a => a.Subtype), (await ReadSaved(path)).Select(a => a.Subtype));
    }

    [Fact]
    public async Task AMovedShape_IsSavedWhereItWasMoved_AndCannotBeResized()
    {
        string path = Blank("moved.pdf");
        using (var vm = await Open(path))
        {
            vm.ToggleAnnotationTool("Arrow");
            vm.AddLineShape(1, N(100, 400), N(200, 400));
            Draw(vm, "Polygon", N(300, 300), N(400, 300), N(350, 380));
            await vm.SaveAsync();
        }

        using (var vm = await Open(path))
        {
            var arrow = vm.AllAnnotations[0];
            var polygon = vm.AllAnnotations[1];
            Assert.False(MainViewModel.CanResizeAnnotation(arrow));
            Assert.False(MainViewModel.CanResizeAnnotation(polygon));
            Assert.False(MainViewModel.HasVertexHandles(polygon)); // its points are not edited one by one

            vm.SelectAnnotation(arrow);
            Assert.True(vm.NudgeSelectedAnnotation(1, 0, large: true)); // 10 pt right
            Assert.Equal(N(110, 400).X, arrow.Vertices[0].X, 6);
            Assert.False(arrow.IsDrawnByPage); // changed: the overlay draws it until it is saved
            Assert.Equal("_Undo Move Arrow", vm.UndoHeader);
            await vm.UndoAsync();
            Assert.Equal(N(100, 400).X, arrow.Vertices[0].X, 6);
            Assert.True(arrow.IsDrawnByPage);
            await vm.RedoAsync();

            vm.SelectAnnotation(polygon);
            Assert.True(vm.BeginAnnotationDrag(AnnotationHandle.Body, N(350, 330)));
            vm.UpdateAnnotationDrag(N(350, 310), shift: false); // 20 pt down
            Assert.True(vm.EndAnnotationDrag());
            Assert.Equal(N(300, 280).Y, polygon.Vertices[0].Y, 6);
            Assert.False(vm.BeginAnnotationDrag(AnnotationHandle.BottomRight, N(400, 280)));

            // A new colour is kept too.
            vm.ChangeAnnotation(polygon, a => a.ColorHex = "#FF5252", "Change Colour");
            await vm.SaveAsync();
        }

        var saved = await ReadSaved(path);
        Assert.Equal(new[] { 110.0, 400, 210, 400 }, saved[0].Line!.Select(v => Math.Round(v, 3)));
        Assert.Equal(new[] { "None", "OpenArrow" }, saved[0].LineEndings);
        Assert.Equal(280, saved[1].Vertices[0].Y, 3);
        Assert.Equal(360, saved[1].Vertices[2].Y, 3);
        Assert.Equal(1.0, saved[1].Color![0], 2);
        Assert.True(saved[1].Rect.Y <= 280 && saved[1].Rect.Y + saved[1].Rect.Height >= 360);
        Assert.All(saved, a => Assert.True(a.HasAppearance));
    }

    [Fact]
    public async Task DeletingAShape_RemovesItFromTheFile()
    {
        string path = Blank("delete.pdf");
        using (var vm = await Open(path))
        {
            Draw(vm, "PolyLine", N(100, 100), N(200, 200));
            vm.ToggleAnnotationTool("Line");
            vm.AddLineShape(1, N(100, 500), N(200, 500));
            await vm.SaveAsync();
        }
        using (var vm = await Open(path))
        {
            vm.ActiveAnnotationTool = null;
            vm.SelectAnnotation(vm.AllAnnotations[0]);
            Assert.True(vm.DeleteSelectedAnnotation());
            await vm.SaveAsync();
        }
        Assert.Equal("Line", Assert.Single(await ReadSaved(path)).Subtype);
    }

    [Fact]
    public async Task Measurements_StayMeasurements_NextToLinesAndPolygons()
    {
        string path = Blank("both.pdf");
        using (var vm = await Open(path))
        {
            vm.MeasureScale = new MeasureScale(1, "in", 1, "in");
            vm.StartMeasure("Distance");
            await vm.MeasureClickAsync(1, N(100, 400));
            await vm.MeasureClickAsync(1, N(172, 400));
            vm.StartMeasure("Area");
            foreach (var p in new[] { N(300, 300), N(372, 300), N(372, 372) }) await vm.MeasureClickAsync(1, p);
            Assert.True(vm.FinishMeasurement());
            vm.ToggleAnnotationTool("Line");
            vm.AddLineShape(1, N(100, 600), N(300, 600));
            Draw(vm, "Polygon", N(100, 100), N(200, 100), N(150, 200));
            await vm.SaveAsync();
        }

        var saved = await ReadSaved(path);
        Assert.Equal(new[] { "Line", "Polygon", "Line", "Polygon" }, saved.Select(a => a.Subtype));
        Assert.NotNull(saved[0].Measure);
        Assert.NotNull(saved[1].Measure);
        Assert.Null(saved[2].Measure);
        Assert.Null(saved[3].Measure);
        Assert.Equal("LineDimension", saved[0].Intent);
        Assert.Null(saved[2].Intent);

        using var reopened = await Open(path);
        Assert.Equal(new[] { AnnotationType.Measurement, AnnotationType.Measurement, AnnotationType.Line, AnnotationType.Polygon },
            reopened.AllAnnotations.Select(a => a.Type));
        Assert.Equal("1.00 in", reopened.AllAnnotations[0].Contents);
        Assert.Equal(PdfMeasurementKind.Area, reopened.AllAnnotations[1].MeasureKind);
        Assert.True(MainViewModel.HasVertexHandles(reopened.AllAnnotations[0])); // a measurement's points still move one by one
    }

    [Fact]
    public async Task XfdfExport_WritesLinesWithTheirEnds_AndPolygonsWithTheirPoints()
    {
        using var vm = await Open(Blank("xfdf.pdf"));
        vm.ToggleAnnotationTool("Arrow");
        vm.AddLineShape(1, N(100, 400), N(200, 400));
        Draw(vm, "Polygon", N(300, 300), N(400, 300), N(350, 380));
        Draw(vm, "PolyLine", N(100, 100), N(200, 200));
        string xfdf = Path.Combine(_dir, "shapes.xfdf");
        AnnotationExchange.ExportAnnotationsToXfdf(xfdf, vm.AllAnnotations, new System.Collections.Generic.Dictionary<int, (double, double)> { [1] = (612, 792) });

        var root = System.Xml.Linq.XDocument.Load(xfdf).Root!;
        System.Xml.Linq.XNamespace ns = "http://ns.adobe.com/xfdf/";
        var line = Assert.Single(root.Descendants(ns + "line"));
        Assert.Equal("100.0000,400.0000", (string?)line.Attribute("start"));
        Assert.Equal("200.0000,400.0000", (string?)line.Attribute("end"));
        Assert.Equal("OpenArrow", (string?)line.Attribute("tail"));
        Assert.Null(line.Attribute("head"));
        Assert.Equal("300.0000,300.0000;400.0000,300.0000;350.0000,380.0000", (string?)Assert.Single(root.Descendants(ns + "polygon")).Element(ns + "vertices"));
        Assert.Single(root.Descendants(ns + "polyline"));

        // The engine reads the file back as the same annotations.
        var read = PdfAnnotationXfdf.Import(File.ReadAllText(xfdf));
        Assert.Equal(new[] { "Line", "Polygon", "PolyLine" }, read.Select(a => a.Subtype));
        Assert.Equal(new[] { "None", "OpenArrow" }, read[0].LineEndings);
        Assert.Equal(3, read[1].Vertices.Count);
    }
}
