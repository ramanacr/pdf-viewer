using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The annotation edit layer: a placed annotation is selected, moved, resized, turned (stamps) and
/// changed point by point (measurements, small drawings); arrow keys nudge it as it looks on a
/// turned page; Delete removes it; Tab goes through the page's annotations. Each change is one
/// undo step, saves with its geometry and appearance, and reopens as it was left.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class AnnotationEditingViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AnnotEdit_" + Guid.NewGuid().ToString("N"));

    public AnnotationEditingViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A 612 x 792 page with a horizontal line from (72, 720) to (272, 720), optionally turned by /Rotate.</summary>
    private string Drawing(string name, int rotate = 0)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 w 72 720 m 272 720 l S", mediaBox: "[0 0 612 792]", extra: rotate != 0 ? $"/Rotate {rotate}" : string.Empty);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        await vm.PrepareMeasurePageAsync(1);
        return vm;
    }

    /// <summary>A point of default user space on page 1 (612 x 792), normalized as the viewer keeps it.</summary>
    private static Point N(double x, double y) => new(x / 612, 1 - y / 792);

    private static AnnotationModel Box(AnnotationType type, double x, double y, double w, double h) => new()
    {
        PageNumber = 1, Type = type, X = x / 612, Y = 1 - (y + h) / 792, Width = w / 612, Height = h / 792,
        ColorHex = "#FFFF0000", Opacity = 1, StrokeThickness = 2,
    };

    private static void Drag(MainViewModel vm, AnnotationHandle handle, Point from, Point to, bool shift = false, int vertex = -1)
    {
        Assert.True(vm.BeginAnnotationDrag(handle, from, vertex));
        vm.UpdateAnnotationDrag(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), shift, 6);
        vm.UpdateAnnotationDrag(to, shift, 6);
        Assert.True(vm.EndAnnotationDrag());
    }

    private static PdfRect PageRect(AnnotationModel a) =>
        new(a.X * 612, (1 - a.Y - a.Height) * 792, a.Width * 612, a.Height * 792);

    // ------------------------------------------------------------------ moving and resizing

    [Fact]
    public async Task DraggingTheBody_MovesIt_AsOneUndoStep()
    {
        using var vm = await Open(Drawing("move.pdf"));
        var rect = Box(AnnotationType.Rectangle, 100, 500, 100, 50);
        vm.AddAnnotation(rect);
        vm.SelectAnnotation(rect);
        Assert.True(rect.IsSelected);

        Drag(vm, AnnotationHandle.Body, N(150, 525), N(200, 425)); // 50 right, 100 down
        Assert.Equal(new PdfRect(150, 400, 100, 50), Round(PageRect(rect)));
        Assert.Equal("_Undo Move Rectangle", vm.UndoHeader);
        Assert.True(vm.HasUnsavedChanges);

        await vm.UndoAsync();
        Assert.Equal(new PdfRect(100, 500, 100, 50), Round(PageRect(rect)));
        await vm.RedoAsync();
        Assert.Equal(new PdfRect(150, 400, 100, 50), Round(PageRect(rect)));
    }

    [Fact]
    public async Task CornersAndEdges_Resize_ShiftKeepsTheProportions()
    {
        using var vm = await Open(Drawing("resize.pdf"));
        var rect = Box(AnnotationType.Ellipse, 100, 500, 100, 50);
        vm.AddAnnotation(rect);
        vm.SelectAnnotation(rect);

        Drag(vm, AnnotationHandle.BottomRight, N(200, 500), N(250, 480)); // free: 150 wide, 70 tall, top-left fixed
        Assert.Equal(new PdfRect(100, 480, 150, 70), Round(PageRect(rect)));
        Assert.Equal("_Undo Resize Ellipse", vm.UndoHeader);

        Drag(vm, AnnotationHandle.Right, N(250, 515), N(400, 515), shift: true); // twice as wide, so twice as tall about the middle
        Assert.Equal(new PdfRect(100, 445, 300, 140), Round(PageRect(rect)));

        Drag(vm, AnnotationHandle.TopLeft, N(100, 585), N(500, 100)); // past the far corner: the smallest box is left
        Assert.True(PageRect(rect).Width >= 3.99 && PageRect(rect).Height >= 3.99);

        await vm.UndoAsync();
        await vm.UndoAsync();
        Assert.Equal(new PdfRect(100, 480, 150, 70), Round(PageRect(rect)));
    }

    [Fact]
    public async Task EscWhileDragging_PutsItBack_WithNothingToUndo()
    {
        using var vm = await Open(Drawing("cancel.pdf"));
        var rect = Box(AnnotationType.Rectangle, 100, 500, 100, 50);
        vm.AddAnnotation(rect);
        vm.SelectAnnotation(rect);
        Assert.True(vm.BeginAnnotationDrag(AnnotationHandle.Body, N(150, 525)));
        vm.UpdateAnnotationDrag(N(300, 300), false);
        Assert.NotEqual(new PdfRect(100, 500, 100, 50), Round(PageRect(rect)));
        vm.CancelAnnotationDrag();
        Assert.False(vm.IsDraggingAnnotation);
        Assert.Equal(new PdfRect(100, 500, 100, 50), Round(PageRect(rect)));
        Assert.Equal("_Undo Add Rectangle", vm.UndoHeader);
    }

    [Fact]
    public async Task MovedAndResizedShapes_SaveAndReopen_WhereTheyWereLeft()
    {
        string path = Drawing("saved-shape.pdf");
        using (var vm = await Open(path))
        {
            var rect = Box(AnnotationType.Rectangle, 100, 500, 100, 50);
            vm.AddAnnotation(rect);
            await vm.SaveAsync();
        }
        using (var vm = await Open(path))
        {
            var rect = Assert.Single(vm.AllAnnotations);
            Assert.True(vm.DocumentService.IsDrawnAsLoaded(rect));
            vm.SelectAnnotation(rect);
            Drag(vm, AnnotationHandle.Body, N(150, 525), N(250, 525));
            Assert.False(vm.DocumentService.IsDrawnAsLoaded(rect)); // the page leaves the old drawing out now
            Drag(vm, AnnotationHandle.Top, N(250, 550), N(250, 600));
            await vm.SaveAsync();
        }
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var saved = Assert.Single(PdfAnnotationReader.Read(doc, 1));
        Assert.Equal("Square", saved.Subtype);
        Assert.Equal(new PdfRect(200, 500, 100, 100), Round(saved.Rect));
        Assert.True(saved.HasAppearance);
    }

    // ------------------------------------------------------------------ stamps

    [Fact]
    public async Task AStamp_ResizesInProportion_AndTurnsByItsHandle_SnappingTo15Degrees()
    {
        using var vm = await Open(Drawing("stamp.pdf"));
        vm.ChooseStamp("S:Approved");
        var stamp = (await vm.PlaceStampAsync(1, N(306, 396)))!;
        var (w, h) = PdfStamps.DefaultSize("APPROVED", false);
        vm.SelectAnnotation(stamp);

        var r = PageRect(stamp);
        Drag(vm, AnnotationHandle.BottomRight, N(r.Right, r.Bottom), N(r.Right + w, r.Bottom - 5)); // twice as wide: twice as tall too
        Assert.Equal(2 * w, stamp.StampSize.Width, 3);
        Assert.Equal(2 * h, stamp.StampSize.Height, 3);
        Assert.Equal(2 * w, PageRect(stamp).Width, 3);
        Assert.Equal(2 * h, PageRect(stamp).Height, 3);

        // From above the centre round to its left: a quarter turn counter-clockwise.
        r = PageRect(stamp);
        var centre = new PdfPoint(r.Left + r.Width / 2, r.Y + r.Height / 2);
        Drag(vm, AnnotationHandle.Rotate, N(centre.X, r.Y + r.Height + 24), N(centre.X - 80, centre.Y));
        Assert.Equal(90, stamp.StampRotation, 3);
        Assert.Equal(2 * h, PageRect(stamp).Width, 3); // the turned box
        Assert.Equal(centre.X, PageRect(stamp).Left + PageRect(stamp).Width / 2, 3);
        Assert.Equal("_Undo Rotate Stamp", vm.UndoHeader);
        await Task.Delay(50);

        // Shift snaps: 50 degrees on from 90 is 140, so 135 at the nearest 15.
        double a = 230 * Math.PI / 180;
        Drag(vm, AnnotationHandle.Rotate, N(centre.X - 80, centre.Y), N(centre.X + 80 * Math.Cos(a), centre.Y + 80 * Math.Sin(a)), shift: true);
        Assert.Equal(135, stamp.StampRotation, 3);
        Assert.Equal(4, vm.StampOutline(stamp).Count);

        await vm.UndoAsync();
        Assert.Equal(90, stamp.StampRotation, 3);
        await vm.UndoAsync();
        Assert.Equal(0, stamp.StampRotation, 3);
        Assert.Equal(2 * w, PageRect(stamp).Width, 3);
    }

    [Fact]
    public async Task ANewStampTurned_IsDrawnTurned_WhenSaved()
    {
        string path = Drawing("new-turned.pdf");
        using (var vm = await Open(path))
        {
            vm.ChooseStamp("S:SBVoid");
            var stamp = (await vm.PlaceStampAsync(1, N(306, 396)))!;
            vm.SelectAnnotation(stamp);
            var r = PageRect(stamp);
            var centre = new PdfPoint(r.Left + r.Width / 2, r.Y + r.Height / 2);
            Drag(vm, AnnotationHandle.Rotate, N(centre.X + 50, centre.Y), N(centre.X, centre.Y + 50)); // +90
            await vm.SaveAsync();
        }
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var saved = Assert.Single(PdfAnnotationReader.Read(doc, 1));
        Assert.Equal(90, saved.AppearanceRotation, 3);
        var (w, h) = PdfStamps.DefaultSize("VOID", false);
        Assert.Equal(h, saved.Rect.Width, 2);
        Assert.Equal(w, saved.Rect.Height, 2);
        Assert.Equal(w, saved.AppearanceSize!.Value.Width, 2);
    }

    [Fact]
    public async Task ALoadedStamp_KeepsItsAppearanceWhenMoved_AndGetsANewMatrixWhenTurned()
    {
        string path = Drawing("loaded-stamp.pdf");
        using (var vm = await Open(path))
        {
            vm.ChooseStamp("S:Approved");
            await vm.PlaceStampAsync(1, N(306, 396));
            await vm.SaveAsync();
        }
        (string Data, string BBox, string? Matrix) formBefore;
        using (var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path))) formBefore = NormalForm(doc);

        using (var vm = await Open(path))
        {
            var stamp = Assert.Single(vm.AllAnnotations);
            Assert.True(stamp.IsDrawnByPage);
            vm.SelectAnnotation(stamp);
            Drag(vm, AnnotationHandle.Body, N(306, 396), N(206, 296));
            Assert.False(stamp.IsDrawnByPage); // the overlay draws it now, from its own appearance
            for (int i = 0; i < 100 && stamp.StampPreview == null; i++) await Task.Delay(20);
            Assert.NotNull(stamp.StampPreview);
            await vm.SaveAsync();
        }
        using (var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path)))
        {
            Assert.Equal(formBefore, NormalForm(doc)); // moved: the appearance is kept, only /Rect changes
            var moved = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal(206, moved.Rect.Left + moved.Rect.Width / 2, 2);
        }

        using (var vm = await Open(path))
        {
            var stamp = Assert.Single(vm.AllAnnotations);
            var (w, h) = PdfStamps.DefaultSize("APPROVED", false);
            Assert.Equal(w, stamp.StampSize.Width, 2);
            vm.SelectAnnotation(stamp);
            var r = PageRect(stamp);
            var centre = new PdfPoint(r.Left + r.Width / 2, r.Y + r.Height / 2);
            Drag(vm, AnnotationHandle.Rotate, N(centre.X + 50, centre.Y), N(centre.X, centre.Y - 50)); // -90: 270
            Assert.Equal(270, stamp.StampRotation, 3);
            await vm.SaveAsync();
        }
        using (var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path)))
        {
            var form = NormalForm(doc);
            Assert.Equal(formBefore.Data, form.Data);     // turned: the same drawing...
            Assert.Equal("0 -1 1 0 0 0", form.Matrix);    // ...with the turn in its /Matrix
            var turned = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal(270, turned.AppearanceRotation, 3);
            var (w, h) = PdfStamps.DefaultSize("APPROVED", false);
            Assert.Equal(h, turned.Rect.Width, 2);
            Assert.Equal(w, turned.Rect.Height, 2);
            Assert.Equal("Approved", turned.Icon);
        }
    }

    /// <summary>The first annotation's normal appearance: its data, and its /BBox and /Matrix as written.</summary>
    private static (string Data, string BBox, string? Matrix) NormalForm(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        var form = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(((PdfDictionary)r.Resolve(annots[0])!)["AP"])!)["N"])!;
        string Numbers(PdfObject? o) => string.Join(" ", PdfDocumentInfo.Numbers(r.Resolve(o), r)!.Select(v => Math.Round(v, 3)));
        return (Convert.ToBase64String(form.GetRawBytes().ToArray()), Numbers(form.Dictionary["BBox"]), form.Dictionary["Matrix"] != null ? Numbers(form.Dictionary["Matrix"]) : null);
    }

    [Fact]
    public async Task UndoingAChangeToALoadedAnnotation_GivesItBackToThePage()
    {
        string path = Drawing("undo-loaded.pdf");
        using (var vm = await Open(path))
        {
            vm.AddAnnotation(Box(AnnotationType.Rectangle, 100, 500, 100, 50));
            await vm.SaveAsync();
        }
        using var again = await Open(path);
        var rect = Assert.Single(again.AllAnnotations);
        again.SelectAnnotation(rect);
        Assert.True(again.NudgeSelectedAnnotation(1, 0));
        Assert.False(again.DocumentService.IsDrawnAsLoaded(rect));
        await again.UndoAsync();
        Assert.True(again.DocumentService.IsDrawnAsLoaded(rect));
        Assert.False(again.HasUnsavedChanges);
    }

    // ------------------------------------------------------------------ measurements and drawings

    [Fact]
    public async Task DraggingAMeasurementsPoint_MeasuresAgain_SnapsToThePage_AndUndoes()
    {
        using var vm = await Open(Drawing("measure.pdf"));
        vm.MeasureScale = new MeasureScale(1, "in", 1, "in");
        vm.StartMeasure("Distance");
        await vm.MeasureClickAsync(1, N(72, 600));
        await vm.MeasureClickAsync(1, N(144, 600));
        vm.StartMeasure("Distance"); // off again
        var m = Assert.Single(vm.AllAnnotations);
        Assert.Equal("1.00 in", m.Contents);
        vm.SelectAnnotation(m);
        Assert.True(MainViewModel.HasVertexHandles(m));
        Assert.False(MainViewModel.CanResizeAnnotation(m));
        Assert.Equal(2, MainViewModel.VertexHandles(m).Count);

        Drag(vm, AnnotationHandle.Vertex, N(144, 600), N(216, 600), vertex: 1);
        Assert.Equal("2.00 in", m.Contents);
        Assert.Equal(N(216, 600).X, m.Vertices[1].X, 9);
        Assert.Equal("_Undo Move Measurement Point", vm.UndoHeader);
        Assert.True(PageRect(m).Right > 216); // the box holds the new point and its arrow head

        // Near the line's end on the page (272, 720): the point lands on it exactly.
        Drag(vm, AnnotationHandle.Vertex, N(72, 600), N(274, 718), vertex: 0);
        Assert.Equal(N(272, 720).X, m.Vertices[0].X, 9);
        Assert.Equal(N(272, 720).Y, m.Vertices[0].Y, 9);
        // Shift: no snapping.
        Drag(vm, AnnotationHandle.Vertex, N(272, 720), N(274, 718), shift: true, vertex: 0);
        Assert.Equal(N(274, 718).X, m.Vertices[0].X, 9);

        await vm.UndoAsync();
        await vm.UndoAsync();
        await vm.UndoAsync();
        Assert.Equal("1.00 in", m.Contents);
        Assert.Equal(N(144, 600).X, m.Vertices[1].X, 9);
    }

    [Fact]
    public async Task AnAreasPoint_Moved_SavesTheNewVerticesAndResult_WithANewAppearance()
    {
        string path = Drawing("area.pdf");
        using (var vm = await Open(path))
        {
            vm.MeasureScale = new MeasureScale(1, "in", 1, "in");
            vm.StartMeasure("Area");
            foreach (var p in new[] { N(100, 100), N(244, 100), N(244, 172) }) await vm.MeasureClickAsync(1, p);
            Assert.True(vm.FinishMeasurement());
            await vm.SaveAsync();
        }
        (string Data, string BBox, string? Matrix) formBefore;
        using (var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path))) formBefore = NormalForm(doc);

        using (var vm = await Open(path))
        {
            var area = Assert.Single(vm.AllAnnotations);
            Assert.Equal("1.00 sq in", area.Contents);
            vm.SelectAnnotation(area);
            Drag(vm, AnnotationHandle.Vertex, N(244, 172), N(244, 244), vertex: 2); // twice as tall
            Assert.Equal("2.00 sq in", area.Contents);
            Assert.False(area.IsDrawnByPage); // the overlay draws the new outline
            await vm.SaveAsync();
        }
        using var saved = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var read = Assert.Single(PdfAnnotationReader.Read(saved, 1));
        Assert.Equal("Polygon", read.Subtype);
        Assert.Equal("2.00 sq in", read.Contents);
        Assert.Equal(244, read.Vertices[2].Y, 3);
        Assert.Equal("1 in = 1 in", read.Measure!.Ratio);
        Assert.True(read.HasAppearance);
        Assert.NotEqual(formBefore.Data, NormalForm(saved).Data); // drawn again
        Assert.NotEqual(formBefore.BBox, NormalForm(saved).BBox);
        Assert.True(read.Rect.Top + read.Rect.Height >= 244);

        using var reopened = await Open(path);
        Assert.Equal("2.00 sq in", reopened.AllAnnotations[0].Contents);
        Assert.Equal(N(244, 244).Y, reopened.AllAnnotations[0].Vertices[2].Y, 6);
    }

    [Fact]
    public async Task ADrawingWithFewPoints_GetsPointHandles_AndItsBoxFollows()
    {
        using var vm = await Open(Drawing("ink.pdf"));
        var ink = new AnnotationModel
        {
            PageNumber = 1, Type = AnnotationType.Ink, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1, ColorHex = "#FF0000FF", Opacity = 1,
            InkStrokes = { new() { new Point(0.1, 0.1), new Point(0.3, 0.2) }, new() { new Point(0.2, 0.15) } },
        };
        vm.AddAnnotation(ink);
        vm.SelectAnnotation(ink);
        Assert.Equal(3, MainViewModel.VertexHandles(ink).Count);
        Drag(vm, AnnotationHandle.Vertex, new Point(0.2, 0.15), new Point(0.5, 0.4), vertex: 2);
        Assert.Equal(new Point(0.5, 0.4), ink.InkStrokes[1][0]);
        Assert.Equal(0.5, ink.X + ink.Width, 9);
        Assert.Equal(0.4, ink.Y + ink.Height, 9);

        // A freehand stroke is sized as a whole instead: its points scale with the box.
        var freehand = new AnnotationModel
        {
            PageNumber = 1, Type = AnnotationType.Ink, X = 0.5, Y = 0.5, Width = 0.2, Height = 0.2, Opacity = 1,
            InkStrokes = { Enumerable.Range(0, 40).Select(i => new Point(0.5 + i * 0.2 / 39, 0.5 + i * 0.2 / 39)).ToList() },
        };
        vm.AddAnnotation(freehand);
        vm.SelectAnnotation(freehand);
        Assert.Empty(MainViewModel.VertexHandles(freehand));
        Drag(vm, AnnotationHandle.BottomRight, new Point(0.7, 0.7), new Point(0.9, 0.9));
        Assert.Equal(0.9, freehand.InkStrokes[0][^1].X, 9);
        Assert.Equal(0.5, freehand.InkStrokes[0][0].X, 9);
    }

    // ------------------------------------------------------------------ the keyboard, and turned pages

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task ArrowKeys_NudgeAsThePageIsSeen_ShiftFurther(int rotate)
    {
        using var vm = await Open(Drawing($"nudge{rotate}.pdf", rotate));
        Assert.Equal(rotate, (int)vm.Pages[0].OverlayRotationAngle);
        var rect = Box(AnnotationType.Rectangle, 100, 500, 100, 50);
        vm.AddAnnotation(rect);
        vm.SelectAnnotation(rect);

        Assert.True(vm.NudgeSelectedAnnotation(1, 0));              // Right, as seen
        Assert.True(vm.NudgeSelectedAnnotation(0, 1, large: true)); // Shift+Down, as seen
        var moved = PageRect(rect);
        // The step on the page that shows as right, and as down, for each turn (default user space, y up).
        var (rx, ry, dx, dy) = rotate switch
        {
            90 => (0, 1, 1, 0),
            180 => (-1, 0, 0, 1),
            270 => (0, -1, -1, 0),
            _ => (1, 0, 0, -1),
        };
        Assert.Equal(100 + rx + 10 * dx, moved.Left, 6);
        Assert.Equal(500 + ry + 10 * dy, moved.Y, 6);

        // Where it shows: right of where it was, and further down.
        var before = vm.Pages[0].ToDisplayNormalized(100 / 612.0, 1 - 550 / 792.0);
        var after = vm.Pages[0].ToDisplayNormalized(rect.X, rect.Y);
        double sw = rotate is 90 or 270 ? 792 : 612, sh = rotate is 90 or 270 ? 612 : 792;
        Assert.Equal(1, (after.X - before.X) * sw, 6);
        Assert.Equal(10, (after.Y - before.Y) * sh, 6);

        await vm.UndoAsync();
        await vm.UndoAsync();
        Assert.Equal(new PdfRect(100, 500, 100, 50), Round(PageRect(rect)));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task OnATurnedPage_ADraggedStampAndMeasurement_SaveWhereTheyShow(int rotate)
    {
        string path = Drawing($"turned{rotate}.pdf", rotate);
        using (var vm = await Open(path))
        {
            vm.MeasureScale = new MeasureScale(1, "in", 1, "in");
            vm.StartMeasure("Distance");
            await vm.MeasureClickAsync(1, N(72, 300));
            await vm.MeasureClickAsync(1, N(144, 300));
            vm.StartMeasure("Distance");
            var m = vm.AllAnnotations[0];
            vm.SelectAnnotation(m);
            Drag(vm, AnnotationHandle.Vertex, N(144, 300), N(144, 372), vertex: 1); // now 72 x 72: 1.41 in
            Assert.Equal("1.41 in", m.Contents);

            vm.ChooseStamp("S:Final");
            var stamp = (await vm.PlaceStampAsync(1, N(300, 500)))!;
            vm.SelectAnnotation(stamp);
            Drag(vm, AnnotationHandle.Body, N(300, 500), N(350, 450));
            await vm.SaveAsync();
        }
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        var read = PdfAnnotationReader.Read(doc, 1);
        Assert.Equal(new[] { "Line", "Stamp" }, read.Select(a => a.Subtype));
        Assert.Equal(new[] { 72.0, 300, 144, 372 }, read[0].Line!.Select(v => Math.Round(v, 3)));
        Assert.Equal("1.41 in", read[0].Contents);
        Assert.Equal(350, read[1].Rect.Left + read[1].Rect.Width / 2, 2);
        Assert.Equal(450, read[1].Rect.Y + read[1].Rect.Height / 2, 2);
    }

    [Fact]
    public async Task Delete_RemovesTheSelection_AndTabGoesThroughThePage()
    {
        using var vm = await Open(Drawing("keys.pdf"));
        var a = Box(AnnotationType.Rectangle, 100, 500, 100, 50);
        var b = Box(AnnotationType.Ellipse, 300, 500, 100, 50);
        var c = Box(AnnotationType.Rectangle, 100, 200, 100, 50);
        vm.AddAnnotation(a);
        vm.AddAnnotation(b);
        vm.AddAnnotation(c);

        Assert.True(vm.SelectNextAnnotation());
        Assert.Same(a, vm.SelectedAnnotation);
        Assert.True(vm.SelectNextAnnotation());
        Assert.Same(b, vm.SelectedAnnotation);
        Assert.False(a.IsSelected);
        Assert.True(vm.SelectNextAnnotation(backwards: true));
        Assert.Same(a, vm.SelectedAnnotation);
        Assert.False(vm.SelectNextAnnotation(backwards: true)); // before the first: Tab leaves the page
        Assert.Null(vm.SelectedAnnotation);
        Assert.True(vm.SelectNextAnnotation(backwards: true));
        Assert.Same(c, vm.SelectedAnnotation);

        vm.SelectAnnotation(b);
        Assert.True(vm.DeleteSelectedAnnotation());
        Assert.Null(vm.SelectedAnnotation);
        Assert.Equal(new[] { a, c }, vm.Pages[0].AnnotationsOnPage);
        Assert.Equal("_Undo Delete Ellipse", vm.UndoHeader);
        await vm.UndoAsync();
        Assert.Equal(new[] { a, b, c }, vm.Pages[0].AnnotationsOnPage);

        // Undoing the addition of the selected one lets go of it.
        vm.SelectAnnotation(c);
        await vm.UndoAsync(); // takes back adding c
        Assert.DoesNotContain(c, vm.AllAnnotations);
        Assert.Null(vm.SelectedAnnotation);

        Assert.False(vm.NudgeSelectedAnnotation(1, 0)); // nothing selected
    }

    [Fact]
    public async Task ASecuredDocument_ThatForbidsComments_IsNotChanged()
    {
        using var vm = await Open(Drawing("rights.pdf"));
        var rect = Box(AnnotationType.Rectangle, 100, 500, 100, 50);
        vm.AddAnnotation(rect);
        vm.SelectAnnotation(rect);
        vm.DocumentPermissions = PdfEngine.Documents.PdfDocumentPermissions.FromFlags(unchecked((int)0xFFFFFFC4) & ~(1 << 5), 3, false);
        Assert.False(vm.DocumentPermissions.CanAnnotate);
        Assert.False(vm.BeginAnnotationDrag(AnnotationHandle.Body, N(150, 525)));
        Assert.False(vm.NudgeSelectedAnnotation(1, 0));
        Assert.False(vm.DeleteSelectedAnnotation());
        Assert.Equal(new PdfRect(100, 500, 100, 50), Round(PageRect(rect)));
    }

    // ------------------------------------------------------------------ the layer

    [Fact]
    public async Task TheLayer_ShowsHandles_OfAConstantSize_WithAutomationNames()
    {
        using var vm = await Open(Drawing("layer.pdf"));
        vm.ChooseStamp("S:Approved");
        var stamp = (await vm.PlaceStampAsync(1, N(306, 396)))!;
        vm.MeasureScale = new MeasureScale(1, "in", 1, "in");
        vm.StartMeasure("Distance");
        await vm.MeasureClickAsync(1, N(72, 600));
        await vm.MeasureClickAsync(1, N(144, 600));
        vm.StartMeasure("Distance");
        var measurement = vm.AllAnnotations[1];
        var page = vm.Pages[0];
        vm.SelectAnnotation(stamp);
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var layer = new PdfViewer.Views.AnnotationEditLayer { Page = page, Width = page.UnrotatedDisplayWidth, Height = page.UnrotatedDisplayHeight };
                Assert.True(layer.IsHitTestVisible);
                var names = layer.Children.OfType<FrameworkElement>().Select(AutomationProperties.GetName).Where(n => n.Length > 0).ToList();
                Assert.Contains("Stamp annotation, selected", names);
                Assert.Equal(8, names.Count(n => n.StartsWith("Resize handle", StringComparison.Ordinal)));
                Assert.Contains("Rotate handle", names);
                var handle = layer.Children.OfType<System.Windows.Shapes.Rectangle>().First(r => AutomationProperties.GetName(r) == "Resize handle, top left");
                Assert.Equal(PdfViewer.Views.AnnotationEditLayer.HandleSize, handle.Width);

                // Zoomed in, the box grows and the handles keep their size on screen.
                double left = System.Windows.Controls.Canvas.GetLeft(handle);
                page.UpdateScale(page.DisplayScale * 2);
                handle = layer.Children.OfType<System.Windows.Shapes.Rectangle>().First(r => AutomationProperties.GetName(r) == "Resize handle, top left");
                Assert.Equal(PdfViewer.Views.AnnotationEditLayer.HandleSize, handle.Width);
                Assert.Equal(2 * (left + handle.Width / 2), System.Windows.Controls.Canvas.GetLeft(handle) + handle.Width / 2, 3);

                vm.SelectAnnotation(measurement);
                names = layer.Children.OfType<FrameworkElement>().Select(AutomationProperties.GetName).Where(n => n.Length > 0).ToList();
                Assert.Equal(new[] { "Measurement annotation, selected", "Point 1 handle", "Point 2 handle" }, names);

                vm.SelectAnnotation(null);
                Assert.False(layer.IsHitTestVisible);
                Assert.Empty(layer.Children);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null) throw error;
    }

    private static PdfRect Round(PdfRect r) => new(Math.Round(r.X, 3), Math.Round(r.Y, 3), Math.Round(r.Width, 3), Math.Round(r.Height, 3));
}
