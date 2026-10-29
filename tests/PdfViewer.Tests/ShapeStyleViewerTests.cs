using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The style of rectangles, ellipses, lines, polygons and connected lines: fill (/IC), line
/// endings (/LE) and dash (/BS /S /D). Each change is undoable, saved with an appearance, read
/// back, and drawn on screen as it is saved; shapes from other programs keep what is not changed.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class ShapeStyleViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ShapeStyle_" + Guid.NewGuid().ToString("N"));

    public ShapeStyleViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A 612 x 792 page with the given annotation dictionaries (made here, not taken from anywhere).</summary>
    private string WithAnnotations(string name, params string[] annotations)
    {
        var b = new VectorPdfBuilder();
        var numbers = annotations.Select(a => b.Add(a)).ToList();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: annotations.Length == 0 ? "" : "/Annots [" + string.Join(" ", numbers.Select(n => $"{n} 0 R")) + "]");
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

    private static Point N(double x, double y) => new(x / 612, 1 - y / 792);

    private static AnnotationModel Box(AnnotationType type, double x, double y, double w, double h) => new()
    {
        PageNumber = 1, Type = type, X = x / 612, Y = 1 - (y + h) / 792, Width = w / 612, Height = h / 792,
        ColorHex = "#FF0000", Opacity = 1, StrokeThickness = 2,
    };

    private static void Draw(MainViewModel vm, string tool, params Point[] points)
    {
        vm.ToggleAnnotationTool(tool);
        foreach (var p in points) vm.ShapeClick(1, p);
        Assert.True(vm.FinishShape());
    }

    /// <summary>The annotations of page 1 as read, with the content of each normal appearance.</summary>
    private static async Task<(PdfAnnotation Annotation, string Appearance)[]> ReadSaved(string path)
    {
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var r = doc.Resolver;
        var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        return PdfAnnotationReader.Read(doc, 1).Select((a, i) =>
        {
            string content = string.Empty;
            if (r.Resolve(((PdfDictionary)r.Resolve(annots[i])!)["AP"]) is PdfDictionary ap && r.Resolve(ap["N"]) is PdfStream form)
            {
                var raw = form.GetRawBytes().ToArray();
                if (form.Dictionary["Filter"] != null)
                {
                    using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                    using var o = new MemoryStream();
                    z.CopyTo(o);
                    raw = o.ToArray();
                }
                content = System.Text.Encoding.Latin1.GetString(raw);
            }
            return (a, content);
        }).ToArray();
    }

    private static string[] Tokens(string content) => content.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ new shapes

    [Fact]
    public async Task FillEndingsAndDash_AreUndoable_SavedWithAppearances_AndReadBack()
    {
        string path = WithAnnotations("styled.pdf");
        using (var vm = await Open(path))
        {
            var rect = Box(AnnotationType.Rectangle, 100, 600, 100, 50);
            var ellipse = Box(AnnotationType.Ellipse, 300, 600, 100, 50);
            vm.AddAnnotation(rect);
            vm.AddAnnotation(ellipse);
            vm.ToggleAnnotationTool("Line");
            Assert.True(vm.AddLineShape(1, N(100, 500), N(300, 500)));
            Draw(vm, "Polygon", N(100, 300), N(250, 300), N(175, 400));
            Draw(vm, "PolyLine", N(350, 300), N(400, 380), N(450, 300));
            var line = vm.AllAnnotations[2];
            var polygon = vm.AllAnnotations[3];
            var polyline = vm.AllAnnotations[4];

            vm.ChangeAnnotation(rect, a => a.InteriorColorHex = "#00E676", "Change Fill");
            Assert.Equal("#00E676", rect.InteriorColorHex);
            Assert.Equal("_Undo Change Fill", vm.UndoHeader);
            await vm.UndoAsync();
            Assert.Null(rect.InteriorColorHex);
            await vm.RedoAsync();
            Assert.Equal("#00E676", rect.InteriorColorHex);
            vm.ChangeAnnotation(rect, a => a.BorderDash = new[] { 4.0, 3.0 }, "Change Line Style");
            vm.ChangeAnnotation(ellipse, a => a.InteriorColorHex = "#2979FF", "Change Fill");

            // Endings on a line make it an arrow, and undo makes it a line again.
            vm.ChangeAnnotation(line, a => { a.SetLineEndings("Circle", "ClosedArrow"); a.InteriorColorHex = "#FF5252"; }, "Change Style");
            Assert.Equal(AnnotationType.Arrow, line.Type);
            await vm.UndoAsync();
            Assert.Equal(AnnotationType.Line, line.Type);
            Assert.Null(line.LineEndings);
            Assert.Null(line.InteriorColorHex);
            await vm.RedoAsync();
            Assert.Equal(new[] { "Circle", "ClosedArrow" }, line.LineEndings);

            vm.ChangeAnnotation(polygon, a => { a.InteriorColorHex = "#FFD700"; a.BorderDash = new[] { 1.0, 2.0 }; a.SetLineEndings("Circle", "Circle"); }, "Change Style");
            Assert.Null(polygon.LineEndings); // a polygon has no ends
            vm.ChangeAnnotation(polyline, a => { a.SetLineEndings("Diamond", "Slash"); a.BorderDash = new[] { 4.0, 3.0 }; }, "Change Style");
            Assert.Equal(AnnotationType.PolyLine, polyline.Type);
            await vm.SaveAsync();
        }

        var saved = await ReadSaved(path);
        Assert.Equal(new[] { "Square", "Circle", "Line", "Polygon", "PolyLine" }, saved.Select(s => s.Annotation.Subtype));
        Assert.All(saved, s => Assert.True(s.Annotation.HasAppearance, s.Annotation.Subtype));
        var (square, squareAp) = saved[0];
        Assert.Equal(new[] { 0.0, 0.902, 0.463 }, square.InteriorColor!.Select(v => Math.Round(v, 3)));
        Assert.Equal("D", square.BorderStyle);
        Assert.Equal(new[] { 4.0, 3.0 }, square.BorderDash);
        Assert.Contains("[4 3] 0 d", squareAp);
        Assert.Contains("f", Tokens(squareAp)); // the fill
        Assert.Equal(new[] { 0.161, 0.474, 1.0 }, saved[1].Annotation.InteriorColor!.Select(v => Math.Round(v, 3)));
        Assert.Null(saved[1].Annotation.BorderDash);

        var (savedLine, lineAp) = saved[2];
        Assert.Equal(new[] { "Circle", "ClosedArrow" }, savedLine.LineEndings);
        Assert.Equal(new[] { 1.0, 0.322, 0.322 }, savedLine.InteriorColor!.Select(v => Math.Round(v, 3)));
        Assert.Contains("B", Tokens(lineAp)); // closed endings filled and stroked
        // The rectangle holds the circle at the start (three line widths round the end point).
        Assert.True(savedLine.Rect.X <= 100 - 3 * 2);

        var (savedPolygon, polygonAp) = saved[3];
        Assert.Equal(new[] { 1.0, 0.843, 0.0 }, savedPolygon.InteriorColor!.Select(v => Math.Round(v, 3)));
        Assert.Equal(new[] { 1.0, 2.0 }, savedPolygon.BorderDash);
        Assert.Contains("[1 2] 0 d", polygonAp);
        Assert.Null(savedPolygon.LineEndings);

        var (savedPolyline, polylineAp) = saved[4];
        Assert.Equal(new[] { "Diamond", "Slash" }, savedPolyline.LineEndings);
        Assert.Equal(new[] { 4.0, 3.0 }, savedPolyline.BorderDash);
        Assert.Contains("[] 0 d", polylineAp); // the endings are drawn solid

        using var reopened = await Open(path);
        var models = reopened.AllAnnotations;
        Assert.Equal(new[] { AnnotationType.Rectangle, AnnotationType.Ellipse, AnnotationType.Arrow, AnnotationType.Polygon, AnnotationType.PolyLine }, models.Select(a => a.Type));
        Assert.Equal("#00E676", models[0].InteriorColorHex);
        Assert.Equal(new[] { 4.0, 3.0 }, models[0].BorderDash);
        Assert.Equal("#2979FF", models[1].InteriorColorHex);
        Assert.Equal(new[] { "Circle", "ClosedArrow" }, models[2].LineEndings);
        Assert.Equal(new[] { 1.0, 2.0 }, models[3].BorderDash);
        Assert.Equal("Slash", models[4].EndEnding);
        Assert.False(reopened.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ shapes from other programs

    [Fact]
    public async Task LoadedShapes_KeepTheirStyle_UnlessTheUserChangesIt()
    {
        string path = WithAnnotations("foreign-style.pdf",
            // A beveled square with a fill.
            "<< /Type /Annot /Subtype /Square /Rect [100 600 200 650] /C [0 0 1] /IC [1 0 0] /BS << /W 2 /S /B >> >>",
            // A dashed line without /C, with a square at its start.
            "<< /Type /Annot /Subtype /Line /Rect [90 490 310 510] /L [100 500 300 500] /LE [/Square /None] /BS << /W 1 /S /D /D [2] >> >>",
            // Connected lines dashed through /Border.
            "<< /Type /Annot /Subtype /PolyLine /Rect [90 290 310 410] /Vertices [100 300 200 400 300 300] /C [0 0 0] /Border [0 0 1 [5 2]] >>",
            // An ellipse nobody touches.
            "<< /Type /Annot /Subtype /Circle /Rect [300 600 400 650] /C [0 0 0] /IC [0 1 0] /BS << /W 1 /S /D /D [3 1] >> >>");

        using (var vm = await Open(path))
        {
            var models = vm.AllAnnotations;
            Assert.Equal("#FF0000", models[0].InteriorColorHex);
            Assert.Null(models[0].BorderDash); // beveled is not dashed
            Assert.Equal(new[] { 2.0 }, models[1].BorderDash);
            Assert.Equal("Square", models[1].StartEnding);
            Assert.Equal(new[] { 5.0, 2.0 }, models[2].BorderDash);
            Assert.Equal(new[] { 3.0, 1.0 }, models[3].BorderDash);

            vm.ChangeAnnotation(models[0], a => a.BorderDash = new[] { 4.0, 3.0 }, "Change Line Style");
            vm.ChangeAnnotation(models[1], a => a.InteriorColorHex = "#FFFF00", "Change Fill");
            vm.SelectAnnotation(models[2]);
            Assert.True(vm.NudgeSelectedAnnotation(1, 0, large: true));
            await vm.SaveAsync();
        }

        var saved = await ReadSaved(path);
        var square = saved[0].Annotation;
        Assert.Equal("D", square.BorderStyle);
        Assert.Equal(new[] { 4.0, 3.0 }, square.BorderDash);
        Assert.Equal(new[] { 1.0, 0, 0 }, square.InteriorColor); // the fill it had
        Assert.Equal(new[] { 0.0, 0, 1 }, square.Color);

        var line = saved[1].Annotation;
        Assert.Equal(new[] { 1.0, 1, 0 }, line.InteriorColor);
        Assert.Equal(new[] { "Square", "None" }, line.LineEndings);
        Assert.Equal("D", line.BorderStyle);
        Assert.Equal(new[] { 2.0 }, line.BorderDash);
        Assert.Null(line.Color); // still not given

        var polyline = saved[2].Annotation;
        Assert.Equal(110, polyline.Vertices[0].X, 3);
        Assert.Equal(new[] { 5.0, 2.0 }, polyline.BorderDash);
        Assert.Null(polyline.InteriorColor);
        Assert.Null(polyline.LineEndings);

        var circle = saved[3].Annotation;
        Assert.Equal(new[] { 0.0, 1, 0 }, circle.InteriorColor);
        Assert.Equal(new[] { 3.0, 1.0 }, circle.BorderDash);
    }

    [Fact]
    public void MeasurementsHaveNoShapeStyle()
    {
        var m = new AnnotationModel { Type = AnnotationType.Measurement };
        Assert.False(m.IsStyledShape);
        m.SetLineEndings("Circle", "Circle");
        Assert.Null(m.LineEndings);
    }

    // ------------------------------------------------------------------ on screen

    [Fact]
    public void ShapeGeometry_DrawsEveryEnding_FillsTheClosedOnes_AndSplitsLinesFromEnds()
    {
        var line = new AnnotationModel { Type = AnnotationType.Arrow, X = 0, Y = 0, Width = 1, Height = 1, StrokeThickness = 1, InteriorColorHex = "#FF0000" };
        line.Vertices = new() { new Point(0.1, 0.5), new Point(0.9, 0.5) };
        var closed = new[] { "ClosedArrow", "RClosedArrow", "Circle", "Square", "Diamond" };
        foreach (var style in AnnotationModel.LineEndingStyles.Where(s => s != "None"))
        {
            line.SetLineEndings("None", style);
            var all = (PathGeometry)line.ShapeGeometry(200, 200)!;
            Assert.Equal(2, all.Figures.Count);
            Assert.Equal(closed.Contains(style), all.Figures[1].IsFilled);
            // The ending is at the end point (x 180), not anywhere else.
            var bounds = new PathGeometry(new[] { all.Figures[1] }).Bounds;
            Assert.True(bounds.Left >= 180 - 9 && bounds.Right <= 180 + 9, style);
        }

        line.SetLineEndings("Circle", "Slash");
        Assert.Single(((PathGeometry)line.ShapeGeometry(200, 200, ends: false)!).Figures);
        Assert.Equal(2, ((PathGeometry)line.ShapeGeometry(200, 200, body: false)!).Figures.Count);
        line.SetLineEndings("None", "None");
        Assert.Equal(AnnotationType.Line, line.Type);
        Assert.Empty(((PathGeometry)line.ShapeGeometry(200, 200, body: false)!).Figures);

        // The dash is given to WPF in line widths: 4 and 3 points at a width of 2.
        line.StrokeThickness = 2;
        line.BorderDash = new[] { 4.0, 3.0 };
        Assert.Equal(new[] { 2.0, 1.5 }, line.StrokeDashArray!.ToArray());
        line.BorderDash = new[] { 3.0 }; // an odd PDF dash repeats
        Assert.Equal(new[] { 1.5, 1.5 }, line.StrokeDashArray!.ToArray());
        line.BorderDash = null;
        Assert.Null(line.StrokeDashArray);

        var rect = new AnnotationModel { Type = AnnotationType.Rectangle };
        Assert.True(rect.IsStyledShape);
        Assert.Equal("#00000000", rect.FillColorHex);
        rect.InteriorColorHex = "#00FF00";
        Assert.Equal("#00FF00", rect.FillColorHex);
    }
}
