using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Converters;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Annotation tools as a live test of the desktop app found them: choosing a tool (the Hand tool
/// too) turns every other one off and drops a shape half drawn; the shape drawn on screen has the
/// annotation's own line width at the page's zoom, its dash and its endings; the comment editor's
/// heading names what it edits; and text boxes and stamps made in a turned view read upright.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class AnnotationToolUxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ToolUx_" + Guid.NewGuid().ToString("N"));

    public AnnotationToolUxTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir); // stamps.json and measuring scales go here
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A blank 612 x 792 page (no /Rotate), made here.</summary>
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

    // ------------------------------------------------------------------ switching tools

    [Fact]
    public async Task TheHandTool_AfterAPolygon_TurnsThePolygonToolOff_AndAClickAddsNoPoint()
    {
        using var vm = await Open(Blank("hand.pdf"));
        vm.ToggleAnnotationTool("Polygon");
        foreach (var p in new[] { N(100, 100), N(200, 100), N(200, 200) }) vm.ShapeClick(1, p);
        Assert.True(vm.FinishShape());
        vm.ShapeClick(1, N(300, 300)); // a new polygon is begun
        Assert.True(vm.IsDrawingShapePoints);

        vm.IsPanningEnabled = true; // the toolbar's Hand button
        Assert.Null(vm.ActiveAnnotationTool);
        Assert.False(vm.IsDrawingShapePoints);
        Assert.Empty(vm.ShapePreviewPoints);

        vm.ShapeClick(1, N(320, 320)); // the next click on the page adds nothing
        Assert.False(vm.IsDrawingShapePoints);
        Assert.False(vm.FinishShape());
        Assert.Single(vm.AllAnnotations);
        Assert.DoesNotContain("polygon needs", vm.StatusText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Polygon")]
    [InlineData("PolyLine")]
    [InlineData("Line")]
    [InlineData("Arrow")]
    [InlineData("Rectangle")]
    [InlineData("Ink")]
    [InlineData("FreeText")]
    [InlineData("Highlight")]
    [InlineData("Underline")]
    [InlineData("Note")]
    public async Task EachTool_IsTurnedOffByTheHandTool_AndTurnsTheHandToolOff(string tool)
    {
        using var vm = await Open(Blank($"pan-{tool}.pdf"));
        vm.TogglePanTool();
        Assert.True(vm.IsPanningEnabled);

        vm.ToggleAnnotationTool(tool);
        Assert.Equal(Enum.Parse<AnnotationType>(tool), vm.ActiveAnnotationTool);
        Assert.False(vm.IsPanningEnabled);

        vm.TogglePanTool(); // Tools → Hand / Pan Tool
        Assert.True(vm.IsPanningEnabled);
        Assert.Null(vm.ActiveAnnotationTool);
    }

    [Theory]
    [InlineData("Polygon", "Line")]
    [InlineData("PolyLine", "Arrow")]
    [InlineData("Polygon", "Ink")]
    [InlineData("PolyLine", "FreeText")]
    [InlineData("Polygon", "Highlight")]
    [InlineData("Polygon", "Note")]
    [InlineData("PolyLine", "Polygon")]
    public async Task AnotherTool_TurnsTheFirstOff_AndDropsTheShapeHalfDrawn(string first, string second)
    {
        using var vm = await Open(Blank($"switch-{first}-{second}.pdf"));
        vm.ToggleAnnotationTool(first);
        vm.ShapeClick(1, N(100, 100));
        vm.ShapeClick(1, N(200, 100));
        Assert.True(vm.IsDrawingShapePoints);

        vm.ToggleAnnotationTool(second);
        Assert.Equal(Enum.Parse<AnnotationType>(second), vm.ActiveAnnotationTool);
        Assert.False(vm.IsDrawingShapePoints);
        Assert.Empty(vm.AllAnnotations);
    }

    [Fact]
    public async Task OtherModes_TurnTheHandToolOff_AndTheHandToolTurnsThemOff()
    {
        using var vm = await Open(Blank("modes.pdf"));
        vm.StartMeasure("Distance");
        vm.IsPanningEnabled = true;
        Assert.Null(vm.ActiveMeasureTool);

        vm.ChooseStamp("S:Approved");
        Assert.False(vm.IsPanningEnabled);
        vm.IsPanningEnabled = true;
        Assert.False(vm.IsPlacingStamp);

        vm.IsMarkingRedaction = true;
        Assert.False(vm.IsPanningEnabled);
        vm.IsPanningEnabled = true;
        Assert.False(vm.IsMarkingRedaction);
    }

    // ------------------------------------------------------------------ the shape drawn on screen

    [Fact]
    public async Task TheShapeBeingDrawn_HasTheAnnotationsLineWidthAndEndings()
    {
        using var vm = await Open(Blank("preview.pdf"));
        Assert.Null(vm.ShapePreviewModel(new[] { N(100, 100), N(200, 200) })); // no shape tool on
        vm.SelectedAnnotationThickness = 2;
        vm.ToggleAnnotationTool("Arrow");
        var arrow = vm.ShapePreviewModel(new[] { N(100, 100), N(300, 100) })!;
        Assert.Equal(2, arrow.StrokeThickness);
        Assert.Equal(new[] { "None", "OpenArrow" }, arrow.LineEndings);
        Assert.Equal(new Rect(0, 0, 1, 1), new Rect(arrow.X, arrow.Y, arrow.Width, arrow.Height));
        Assert.True(vm.AddLineShape(1, N(100, 100), N(300, 100)));
        var added = Assert.Single(vm.AllAnnotations);
        Assert.Equal(added.StrokeThickness, arrow.StrokeThickness);
        Assert.Equal(added.LineEndings, arrow.LineEndings);

        vm.ToggleAnnotationTool("Polygon");
        var polygon = vm.ShapePreviewModel(new[] { N(100, 100), N(200, 100), N(200, 200) })!;
        Assert.Equal(AnnotationType.Polygon, polygon.Type);
        Assert.Equal(3, polygon.Vertices.Count);
        Assert.Null(polygon.LineEndings);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(4.0)]
    public void OnScreen_TheLineWidthAndDashScaleWithTheZoom(double zoom)
    {
        var a = new AnnotationModel { Type = AnnotationType.Polygon, StrokeThickness = 2 };
        a.BorderDash = new[] { 4.0, 3.0 };
        double width = ScaledStrokeConverter.Scaled(a.StrokeThickness, zoom);
        Assert.Equal(2 * zoom, width, 9);
        // WPF counts a dash in line widths: on screen it is the PDF dash at the zoom, as the page draws it.
        Assert.Equal(new[] { 4 * zoom, 3 * zoom }, a.StrokeDashArray!.Select(d => Math.Round(d * width, 9)));
        Assert.Equal(0, ScaledStrokeConverter.Scaled(0, zoom));

        var converter = new ScaledStrokeConverter();
        Assert.Equal(2 * zoom, (double)converter.Convert(new object[] { 2.0, zoom }, typeof(double), null!, System.Globalization.CultureInfo.InvariantCulture), 9);
    }

    [Fact]
    public void LineEndings_OnScreen_GrowWithTheZoom()
    {
        var a = new AnnotationModel { Type = AnnotationType.Arrow, StrokeThickness = 2, LineEndings = new[] { "None", "OpenArrow" } };
        a.SetGeometry(new Rect(0, 0, 1, 1), Array.Empty<Rect>());
        a.Vertices = new() { new Point(0.1, 0.5), new Point(0.9, 0.5) };
        var atOne = a.ShapeGeometry(600, 600, body: false, ends: true, scale: 1)!.Bounds;
        var atTwo = a.ShapeGeometry(600, 600, body: false, ends: true, scale: 2)!.Bounds;
        // Nine line widths long at 30 degrees: 18 points across at 100%, twice that at 200%.
        Assert.Equal(18, atOne.Height, 6);
        Assert.Equal(36, atTwo.Height, 6);
    }

    // ------------------------------------------------------------------ the comment editor's heading

    [Theory]
    [InlineData(AnnotationType.Polygon, false, "Edit Polygon")]
    [InlineData(AnnotationType.Arrow, false, "Edit Arrow")]
    [InlineData(AnnotationType.PolyLine, false, "Edit Connected Lines")]
    [InlineData(AnnotationType.FreeText, true, "Add Text Box")]
    [InlineData(AnnotationType.FreeText, false, "Edit Text Box")]
    [InlineData(AnnotationType.Note, true, "Add Sticky Note")]
    [InlineData(AnnotationType.Rectangle, false, "Edit Rectangle")]
    [InlineData(AnnotationType.Ink, false, "Edit Drawing")]
    public void TheEditorsHeading_NamesTheAnnotationKind(AnnotationType type, bool isNew, string heading) =>
        Assert.Equal(heading, MainViewModel.EditDialogHeading(type, isNew));

    // ------------------------------------------------------------------ a turned view

    [Fact]
    public async Task InATurnedView_ATextBox_ReadsUpright_AndAnUnturnedOneIsAsBefore()
    {
        string path = Blank("textbox.pdf");
        using (var vm = await Open(path))
        {
            Assert.Null(vm.NewTextBoxRotation(1)); // not turned: the page's own /Rotate applies, as before
            vm.AddAnnotation(TextBox(1, 100, 600, "Plain", vm.NewTextBoxRotation(1)));

            vm.RotateClockwise(); // Ctrl+R: the view turns, the page has no /Rotate
            Assert.Equal(90, vm.UprightTurn(1));
            Assert.Equal(90, vm.NewTextBoxRotation(1));
            vm.AddAnnotation(TextBox(1, 300, 600, "Turned", vm.NewTextBoxRotation(1)));
            await vm.SaveAsync();
        }

        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var read = PdfAnnotationReader.Read(doc, 1).Where(a => a.Subtype == "FreeText").ToList();
        var plain = Assert.Single(read, a => a.Contents == "Plain");
        var turned = Assert.Single(read, a => a.Contents == "Turned");
        Assert.Null(plain.TextRotation);
        Assert.Equal(0, plain.AppearanceRotation);
        Assert.Equal(90, turned.TextRotation);
        Assert.Equal(90, turned.AppearanceRotation, 3); // drawn turned back, so the turned view shows it upright

        // Read back, the overlay turns it the same way.
        using var reopened = await Open(path);
        var model = Assert.Single(reopened.AllAnnotations, a => a.Contents == "Turned");
        Assert.Equal(90, model.TextRotation);
        Assert.Equal(-90, TextTurnConverter.Angle(model.TextRotation, 0));
        Assert.Equal(-90, TextTurnConverter.Angle(null, 90)); // none of its own: the page's /Rotate
    }

    [Theory]
    [InlineData(1, 90)]
    [InlineData(2, 180)]
    [InlineData(3, 270)]
    public async Task InATurnedView_ANewStamp_IsTurnedToReadUpright_AndOldOnesStay(int turns, int expected)
    {
        using var vm = await Open(Blank($"stamp{turns}.pdf"));
        vm.ChooseStamp("S:Approved");
        var before = (await vm.PlaceStampAsync(1, N(306, 600)))!;
        Assert.Equal(0, before.StampRotation);

        for (int i = 0; i < turns; i++) vm.RotateClockwise();
        vm.ChooseStamp("S:Approved");
        var stamp = (await vm.PlaceStampAsync(1, N(306, 300)))!;
        Assert.Equal(expected, stamp.StampRotation, 6);
        Assert.Equal(0, before.StampRotation); // the one already placed does not change
        Assert.Equal(before.StampSize, stamp.StampSize); // the same stamp, the same size before it is turned
        double w = stamp.StampSize.Width, h = stamp.StampSize.Height;
        // A quarter turn: its height runs across the page.
        double across = expected is 90 or 270 ? h : w;
        Assert.Equal(across / 612, stamp.Width, 6);
    }

    [Fact]
    public async Task InATurnedView_ADraggedStamp_FillsTheBoxAsItIsSeen()
    {
        using var vm = await Open(Blank("stampbox.pdf"));
        vm.RotateClockwise();
        vm.ChooseStamp("S:Approved");
        // 100 wide and 300 tall on the page: 300 wide and 100 tall as the turned view shows it.
        var stamp = (await vm.PlaceStampAsync(1, N(300, 400), new Rect(N(250, 550), N(350, 250))))!;
        Assert.Equal(90, stamp.StampRotation, 6);
        Assert.Equal(300, stamp.StampSize.Width, 6);
        Assert.Equal(100, stamp.StampSize.Height, 6);
        Assert.Equal(100 / 612.0, stamp.Width, 6);
        Assert.Equal(300 / 792.0, stamp.Height, 6);
    }

    /// <summary>A text box as the Text Box tool makes one, 200 x 60 points with its top-left corner at (x, y).</summary>
    private static AnnotationModel TextBox(int page, double x, double y, string text, int? rotation) => new()
    {
        PageNumber = page,
        Type = AnnotationType.FreeText,
        X = x / 612,
        Y = 1 - y / 792,
        Width = 200 / 612.0,
        Height = 60 / 792.0,
        ColorHex = "#FF0000",
        StrokeThickness = 1,
        Author = "Tester",
        Title = "FreeText",
        Contents = text,
        Opacity = 1,
        TextRotation = rotation,
    };
}
