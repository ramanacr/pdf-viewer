using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Geometry;
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
/// Tools → Measure and Comment → Stamp in the viewer: the measuring flow with its live info panel,
/// snapping and page viewports, the scale remembered per document, stamps (standard, dynamic with
/// the user's name, custom from a picture), each undone and redone, saved, and read back.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class MeasureAndStampViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MeasureStamp_" + Guid.NewGuid().ToString("N"));

    public MeasureAndStampViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir); // stamps.json, the stamp library and scales go here
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A 612 x 792 page with a horizontal line from (72, 720) to (272, 720) and a rectangle, and optionally a viewport.</summary>
    private string Drawing(string name, string extra = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 w 72 720 m 272 720 l S 100 100 144 72 re S", mediaBox: "[0 0 612 792]", extra: extra);
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

    // ------------------------------------------------------------------ measuring

    [Fact]
    public async Task Distance_TwoClicks_AddAMeasurement_ShownLive_AndUndoable()
    {
        using var vm = await Open(Drawing("distance.pdf"));
        vm.StartMeasure("Distance");
        Assert.True(vm.IsMeasuring);
        Assert.Equal(PdfMeasurementKind.Distance, vm.ActiveMeasureTool);
        vm.MeasureScale = new MeasureScale(1, "in", 10, "ft");

        Assert.False(await vm.MeasureClickAsync(1, N(100, 400)));
        vm.MeasureMove(1, N(136, 448)); // 36 by 48 points: 60 pt, 10/72 ft each
        Assert.Equal("Distance: 8.33 ft", vm.Measurement.Total);
        Assert.Equal("1: 8.33 ft", vm.Measurement.Segments);
        Assert.Equal("Angle: 53.1°", vm.Measurement.Angle);
        Assert.StartsWith("Scale 1 in = 10 ft", vm.Measurement.Scale);
        Assert.Contains("X: ", vm.Measurement.Cursor);

        Assert.True(await vm.MeasureClickAsync(1, N(172, 400))); // 72 pt = 1 in = 10 ft
        var m = Assert.Single(vm.AllAnnotations);
        Assert.Equal(AnnotationType.Measurement, m.Type);
        Assert.Equal(PdfMeasurementKind.Distance, m.MeasureKind);
        Assert.Equal("10.00 ft", m.Contents);
        Assert.Equal(2, m.Vertices.Count);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal("_Undo Add Measurement", vm.UndoHeader);
        Assert.True(vm.IsMeasuring); // the tool stays on for the next one

        await vm.UndoAsync();
        Assert.Empty(vm.AllAnnotations);
        Assert.Empty(vm.Pages[0].AnnotationsOnPage);
        await vm.RedoAsync();
        Assert.Same(m, Assert.Single(vm.Pages[0].AnnotationsOnPage));
    }

    [Fact]
    public async Task PerimeterAndArea_FinishOnRequest_WithSegmentsTotalAndArea()
    {
        using var vm = await Open(Drawing("area.pdf"));
        vm.MeasureScale = new MeasureScale(1, "in", 1, "in");
        vm.StartMeasure("Area");
        foreach (var p in new[] { N(100, 100), N(244, 100), N(244, 172) }) await vm.MeasureClickAsync(1, p);
        vm.MeasureMove(1, N(244, 172));
        Assert.Equal("Area: 1.00 sq in", vm.Measurement.Area);   // a right triangle, legs 2 in and 1 in
        Assert.Contains("(closing)", vm.Measurement.Segments);
        Assert.True(vm.FinishMeasurement());
        Assert.Equal("1.00 sq in", Assert.Single(vm.AllAnnotations).Contents);

        vm.StartMeasure("Perimeter");
        Assert.Equal(PdfMeasurementKind.Perimeter, vm.ActiveMeasureTool);
        await vm.MeasureClickAsync(1, N(100, 300));
        Assert.False(vm.FinishMeasurement()); // one point is not enough
        await vm.MeasureClickAsync(1, N(172, 300));
        await vm.MeasureClickAsync(1, N(172, 444));
        vm.MeasureMove(1, N(172, 444));
        Assert.Equal("1: 1.00 in\n2: 2.00 in", vm.Measurement.Segments);
        Assert.Equal("Perimeter: 3.00 in", vm.Measurement.Total);
        vm.UndoMeasurePoint();
        vm.MeasureMove(1, N(172, 300));
        Assert.Equal("Perimeter: 1.00 in", vm.Measurement.Total);
        await vm.MeasureClickAsync(1, N(172, 444));
        Assert.True(vm.FinishMeasurement());
        Assert.Equal(2, vm.AllAnnotations.Count);
        Assert.Equal("3.00 in", vm.AllAnnotations[1].Contents);
    }

    [Fact]
    public async Task Snapping_FindsTheLinesEndAndMiddle_OnThePage()
    {
        using var vm = await Open(Drawing("snap.pdf"));
        vm.StartMeasure("Distance");
        Assert.True(await vm.PrepareMeasurePageAsync(1));
        var (end, kind) = vm.SnapMeasurePoint(1, N(74, 722), 6);
        Assert.Equal(PdfSnapKind.Endpoint, kind);
        Assert.Equal(N(72, 720).X, end.X, 9);
        Assert.Equal(N(72, 720).Y, end.Y, 9);
        Assert.Equal(PdfSnapKind.Midpoint, vm.SnapMeasurePoint(1, N(171, 721), 6).Kind);
        Assert.Equal(PdfSnapKind.OnPath, vm.SnapMeasurePoint(1, N(230, 723), 6).Kind);
        Assert.Equal(PdfSnapKind.None, vm.SnapMeasurePoint(1, N(230, 723), 6, snap: false).Kind);
        vm.SnapToContent = false;
        Assert.Equal(PdfSnapKind.None, vm.SnapMeasurePoint(1, N(74, 722), 6).Kind);
    }

    [Fact]
    public async Task APageViewport_GivesTheScale_WhereItCovers()
    {
        string vp = "/VP [<< /Type /Viewport /BBox [0 0 306 792] /Measure << /Type /Measure /Subtype /RL /R (1 in = 2 m) " +
                    "/X [<< /Type /NumberFormat /U (m) /C 0.0277777778 /D 100 >>] /D [<< /U (m) /C 1 /D 100 >>] /A [<< /U (sq m) /C 1 /D 100 >>] >> >>]";
        using var vm = await Open(Drawing("viewport.pdf", vp));
        vm.MeasureScale = new MeasureScale(1, "in", 1, "ft");
        vm.StartMeasure("Distance");
        await vm.MeasureClickAsync(1, N(50, 300));
        await vm.MeasureClickAsync(1, N(122, 300));                // in the viewport: 1 in = 2 m
        Assert.Equal("2.00 m", vm.AllAnnotations[^1].Contents);
        await vm.MeasureClickAsync(1, N(400, 300));
        await vm.MeasureClickAsync(1, N(472, 300));                // outside it: the document's scale
        Assert.Equal("1.00 ft", vm.AllAnnotations[^1].Contents);
    }

    [Fact]
    public async Task TheScale_IsRememberedForTheDocument()
    {
        string path = Drawing("scale.pdf");
        using (var vm = await Open(path))
        {
            vm.ShowMeasureScaleFunc = _ => new MeasureScale(1, "cm", 5, "m", 1);
            vm.SetMeasureScale();
            Assert.Equal(new MeasureScale(1, "cm", 5, "m", 1), vm.MeasureScale);
        }
        Assert.Equal(new MeasureScale(1, "cm", 5, "m", 1), MeasureScaleStore.Load(path));
        string json = File.ReadAllText(Path.Combine(_dir, "measure-scales.json"));
        Assert.DoesNotContain("scale.pdf", json); // the document is not named in the settings
        using var again = await Open(path);
        Assert.Equal(new MeasureScale(1, "cm", 5, "m", 1), again.MeasureScale);
        using var other = await Open(Drawing("other.pdf"));
        Assert.Equal(MeasureScale.Default, other.MeasureScale);
    }

    [Fact]
    public async Task Measurements_SurviveSaveAndReopen_WithTheirMeasure()
    {
        string path = Drawing("saved-measure.pdf");
        using (var vm = await Open(path))
        {
            vm.MeasureScale = new MeasureScale(1, "in", 10, "ft");
            vm.StartMeasure("Distance");
            await vm.MeasureClickAsync(1, N(100, 400));
            await vm.MeasureClickAsync(1, N(172, 400));
            vm.StartMeasure("Area");
            foreach (var p in new[] { N(300, 300), N(372, 300), N(372, 444), N(300, 444) }) await vm.MeasureClickAsync(1, p);
            Assert.True(vm.FinishMeasurement());
            await vm.SaveAsync();
            Assert.False(vm.HasUnsavedChanges);
        }

        using (var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path)))
        {
            var read = PdfAnnotationReader.Read(doc, 1);
            Assert.Equal(new[] { "Line", "Polygon" }, read.Select(a => a.Subtype));
            Assert.Equal(new[] { "LineDimension", "PolygonDimension" }, read.Select(a => a.Intent));
            Assert.Equal("200.00 sq ft", read[1].Contents);
            Assert.All(read, a => Assert.Equal("1 in = 10 ft", a.Measure!.Ratio));
        }

        using var reopened = await Open(path);
        Assert.Equal(2, reopened.AllAnnotations.Count);
        var distance = reopened.AllAnnotations[0];
        Assert.Equal(AnnotationType.Measurement, distance.Type);
        Assert.Equal("10.00 ft", distance.Contents);
        Assert.True(distance.HasOwnAppearance);
        Assert.Equal(N(100, 400).X, distance.Vertices[0].X, 6);
        Assert.Equal(PdfMeasurementKind.Area, reopened.AllAnnotations[1].MeasureKind);
        Assert.True(PdfMeasure.FromScale(1, "in", 10, "ft").SameAs(reopened.AllAnnotations[1].Measure));
        Assert.False(reopened.HasUnsavedChanges);

        // Deleting one and saving removes it from the file; the other is kept as it was.
        reopened.DeleteAnnotation(distance);
        await reopened.SaveAsync();
        using var after = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        Assert.Equal("Polygon", Assert.Single(PdfAnnotationReader.Read(after, 1)).Subtype);
    }

    // ------------------------------------------------------------------ stamps

    [Fact]
    public async Task AStandardStamp_IsPlacedByAClick_DrawnByTheEngine_AndUndoable()
    {
        using var vm = await Open(Drawing("stamp.pdf"));
        vm.ChooseStamp("S:Approved");
        Assert.True(vm.IsPlacingStamp);
        Assert.Null(vm.ActiveMeasureTool);
        var stamp = await vm.PlaceStampAsync(1, N(306, 396));
        Assert.NotNull(stamp);
        Assert.False(vm.IsPlacingStamp); // one placement
        Assert.Equal(AnnotationType.Stamp, stamp!.Type);
        Assert.Equal("Approved", stamp.StampName);
        Assert.Equal("APPROVED", stamp.Contents);
        Assert.NotNull(stamp.StampPreview);
        var (w, h) = PdfStamps.DefaultSize("APPROVED", false);
        Assert.Equal(w / 612, stamp.Width, 6);
        Assert.Equal(h / 792, stamp.Height, 6);
        Assert.Equal(306.0 / 612, stamp.X + stamp.Width / 2, 6); // centred on the click

        await vm.UndoAsync();
        Assert.Empty(vm.AllAnnotations);
        await vm.RedoAsync();
        Assert.Same(stamp, Assert.Single(vm.AllAnnotations));
    }

    [Fact]
    public async Task ADynamicStamp_AsksForTheNameOnce_AndShowsWhoAndWhen()
    {
        using var vm = await Open(Drawing("dynamic.pdf"));
        int asked = 0;
        vm.RequestStampIdentityFunc = _ => { asked++; return "Jane Doe"; };
        vm.ChooseStamp("D:Approved");
        var first = await vm.PlaceStampAsync(1, N(200, 600));
        vm.ChooseStamp("D:Received");
        var second = await vm.PlaceStampAsync(1, N(200, 400), new Rect(N(100, 450), N(300, 350))); // a dragged box
        Assert.Equal(1, asked);
        Assert.Equal("Jane Doe", StampLibrary.Identity);
        Assert.Contains("\"Identity\": \"Jane Doe\"", File.ReadAllText(Path.Combine(_dir, "stamps.json")));
        Assert.StartsWith("By Jane Doe at ", first!.StampSubtitle);
        Assert.Equal(200, second!.StampSize.Width, 6);
        Assert.Equal(100, second.StampSize.Height, 6);
    }

    [Fact]
    public async Task ATurnedStamp_CoversItsTurnedBox()
    {
        using var vm = await Open(Drawing("turned.pdf"));
        vm.SetStampRotation("90");
        Assert.Equal(90, vm.StampRotation);
        vm.ChooseStamp("S:SBVoid");
        var stamp = await vm.PlaceStampAsync(1, N(306, 396));
        var (w, h) = PdfStamps.DefaultSize("VOID", false);
        Assert.Equal(90, stamp!.StampRotation);
        Assert.Equal(h / 612, stamp.Width, 6);   // turned a quarter: its height runs across
        Assert.Equal(w / 792, stamp.Height, 6);
    }

    private string Png(string name)
    {
        var pixels = new byte[40 * 20 * 4];
        for (int i = 0; i < 40 * 20; i++) { pixels[i * 4] = 200; pixels[i * 4 + 1] = 40; pixels[i * 4 + 2] = 20; pixels[i * 4 + 3] = (byte)(i % 40 < 20 ? 255 : 0); }
        var source = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 40 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    [Fact]
    public async Task Stamps_StandardDynamicAndCustom_SurviveSaveAndReopen()
    {
        string path = Drawing("saved-stamps.pdf");
        var custom = StampLibrary.AddImage(Png("logo.png"), "Logo");
        Assert.True(File.Exists(Path.Combine(StampLibrary.Folder, custom.FileName)));
        using (var vm = await Open(path))
        {
            Assert.Contains(vm.CustomStamps, s => s.Id == custom.Id);
            vm.RequestStampIdentityFunc = _ => "Jane Doe";
            vm.ChooseStamp("S:Confidential");
            await vm.PlaceStampAsync(1, N(200, 700));
            vm.ChooseStamp("D:SBReviewed");
            await vm.PlaceStampAsync(1, N(200, 500));
            vm.ChooseStamp("C:" + custom.Id);
            var logo = await vm.PlaceStampAsync(1, N(400, 300));
            Assert.Equal(2.0, logo!.StampSize.Width / logo.StampSize.Height, 6); // the picture's shape
            await vm.SaveAsync();
        }

        using (var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path)))
        {
            var read = PdfAnnotationReader.Read(doc, 1);
            Assert.Equal(new[] { "Confidential", "SBReviewed", "Custom" }, read.Select(a => a.Icon));
            Assert.All(read, a => Assert.True(a.HasAppearance));
            var r = doc.Resolver;
            var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
            var logoAnnot = (PdfDictionary)r.Resolve(annots[2])!;
            var form = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(logoAnnot["AP"])!)["N"])!;
            var xobjects = (PdfDictionary)r.Resolve(((PdfDictionary)r.Resolve(form.Dictionary["Resources"])!)["XObject"])!;
            Assert.Equal("Image", ((PdfStream)r.Resolve(xobjects["Im0"])!).Dictionary.GetName("Subtype"));
        }

        using var reopened = await Open(path);
        Assert.Equal(3, reopened.AllAnnotations.Count);
        Assert.All(reopened.AllAnnotations, a => Assert.Equal(AnnotationType.Stamp, a.Type));
        Assert.All(reopened.AllAnnotations, a => Assert.True(a.HasOwnAppearance));
        Assert.Equal("SBReviewed", reopened.AllAnnotations[1].StampName);

        // Reopened and saved untouched, the stamps are kept exactly (their appearances too).
        reopened.AddAnnotation(new AnnotationModel { PageNumber = 1, Type = AnnotationType.Highlight, X = 0.1, Y = 0.1, Width = 0.2, Height = 0.02 });
        await reopened.SaveAsync();
        using var after = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        Assert.Equal(new[] { "Stamp", "Stamp", "Stamp", "Highlight" }, PdfAnnotationReader.Read(after, 1).Select(a => a.Subtype));
    }

    [Fact]
    public async Task ACustomStampFromAPdfPage_IsKeptInTheLibrary_AndPlaced()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 0 1 rg 0 0 300 100 re f", mediaBox: "[0 0 300 100]");
        b.AddPage("1 0 0 rg 0 0 100 100 re f", mediaBox: "[0 0 100 100]");
        string source = Path.Combine(_dir, "source.pdf");
        File.WriteAllBytes(source, b.Build());
        var custom = StampLibrary.AddPdfPage(source, 1, "Seal");
        Assert.True(custom.IsPdf);
        Assert.Equal(3.0, custom.Width / custom.Height, 6);
        using (var kept = PdfVectorDocument.Open(new PdfEngine.Vector.Parsing.PdfMemoryByteSource(StampLibrary.Load(custom))))
            Assert.Equal(1, kept.PageCount);

        string path = Drawing("pdf-stamp.pdf");
        using var vm = await Open(path);
        vm.ChooseStamp("C:" + custom.Id);
        var stamp = await vm.PlaceStampAsync(1, N(300, 300));
        Assert.NotNull(stamp!.StampPreview);
        await vm.SaveAsync();
        using var doc = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(path));
        Assert.Equal("Custom", Assert.Single(PdfAnnotationReader.Read(doc, 1)).Icon);

        StampLibrary.Remove(custom);
        Assert.DoesNotContain(StampLibrary.CustomStamps, s => s.Id == custom.Id);
    }

    [Fact]
    public async Task Tools_ExcludeEachOther()
    {
        using var vm = await Open(Drawing("tools.pdf"));
        vm.StartMeasure("Area");
        vm.ToggleAnnotationTool("Highlight");
        Assert.Null(vm.ActiveMeasureTool);
        vm.ChooseStamp("S:Draft");
        Assert.Null(vm.ActiveAnnotationTool);
        vm.StartMeasure("Distance");
        Assert.Null(vm.ActiveStamp);
        vm.StartMeasure("Distance");
        Assert.Null(vm.ActiveMeasureTool); // the same tool again turns it off
        Assert.Equal(string.Empty, vm.Measurement.Tool);
    }

    [Fact]
    public async Task CancellingTheCustomStampName_MakesNoStamp()
    {
        string png = Path.Combine(_dir, "logo.png");
        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        using (var fs = File.Create(png)) { var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); enc.Save(fs); }
        using var vm = await Open(Drawing("cancel.pdf"));
        vm.RefreshCustomStamps();
        int before = vm.CustomStamps.Count;
        vm.PickImageFileFunc = () => png;
        vm.RequestStampNameFunc = _ => null; // the user clicks Cancel

        vm.CreateStampFromImage();

        Assert.Equal(before, vm.CustomStamps.Count);
    }
}
