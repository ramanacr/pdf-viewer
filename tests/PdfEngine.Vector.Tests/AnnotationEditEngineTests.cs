using System;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// What a viewer's annotation edit layer needs from the engine: a stamp that keeps its own
/// appearance turned through its form's /Matrix, the upright size of a turned appearance, a
/// preview of a kept appearance, and annotations left out of the screen display list while the
/// viewer draws them itself.
/// </summary>
public class AnnotationEditEngineTests
{
    private const double Dpi = 144;

    private static byte[] Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(string.Empty, mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static PdfAnnotation Designed(string name, PdfRect rect, double rotation = 0, double w = 0, double h = 0) => new()
    {
        PageNumber = 1, Subtype = "Stamp", Icon = name, Rect = rect, Contents = name.ToUpperInvariant(),
        Stamp = new PdfStampAppearance { Rotation = rotation, Width = w, Height = h },
    };

    private static async Task<(PdfVectorDocument Doc, byte[] Bytes)> Write(params PdfAnnotation[] annotations)
    {
        var original = Blank();
        using var doc = await PdfVectorDocument.OpenAsync(original);
        var bytes = PdfAnnotationEditor.Apply(doc, original, e => { foreach (var a in annotations) e.Add(a); });
        return (await PdfVectorDocument.OpenAsync(bytes), bytes);
    }

    private static PdfObject NormalRef(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        return ((PdfDictionary)r.Resolve(((PdfDictionary)r.Resolve(annots[0])!)["AP"])!)["N"]!;
    }

    private static async Task<RenderedPage> Render(IPdfDisplayList list)
    {
        using var renderer = new Direct2DVectorRenderer();
        return await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = Dpi });
    }

    private static (int R, int G, int B) At(RenderedPage page, double x, double y, double pageHeight = 400)
    {
        double s = Dpi / 72;
        int px = Math.Clamp((int)(x * s), 0, page.WidthPixels - 1), py = Math.Clamp((int)((pageHeight - y) * s), 0, page.HeightPixels - 1);
        var span = page.Pixels.Span;
        int i = py * page.Stride + px * 4;
        return (span[i + 2], span[i + 1], span[i]);
    }

    [Fact]
    public async Task TurningAKeptStamp_AddsTheTurnToItsMatrix_InANewForm()
    {
        var (doc, bytes) = await Write(Designed("NotApproved", new PdfRect(120, 180, 160, 40)));
        using (doc)
        {
            var before = (PdfIndirectRef)NormalRef(doc);
            var a = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal(0, a.AppearanceRotation);
            a.Rect = PdfStamps.RotatedBounds(new PdfPoint(200, 200), 160, 40, 90);
            a.AppearanceTurn = 90;
            var turned = PdfAnnotationEditor.Apply(doc, bytes, e => e.Modify(a));
            using var again = await PdfVectorDocument.OpenAsync(turned);
            var after = (PdfIndirectRef)NormalRef(again);
            Assert.NotEqual(before.ObjectNumber, after.ObjectNumber);
            var form = (PdfStream)again.Resolver.Resolve(after)!;
            Assert.Equal(new[] { 0.0, 1, -1, 0, 0, 0 }, PdfDocumentInfo.Numbers(again.Resolver.Resolve(form.Dictionary["Matrix"]), again.Resolver));
            var read = PdfAnnotationReader.Read(again, 1).Single();
            Assert.Equal(90, read.AppearanceRotation, 6);
            Assert.Equal(160, read.AppearanceSize!.Value.Width, 3);
            Assert.Equal(40, read.AppearanceSize!.Value.Height, 3);

            using var page = await Render(await again.GetPageDisplayListAsync(1));
            var left = At(page, 181, 200);
            Assert.True(left.R > 150 && left.G < 110, $"the red band up the left edge, got {left}");
            var below = At(page, 200, 116);
            Assert.True(below.R > 245 && below.G > 245, $"nothing under the turned stamp, got {below}");
        }
    }

    [Fact]
    public async Task ATurnedAppearance_ReadsBackItsUprightSize()
    {
        var rect = PdfStamps.RotatedBounds(new PdfPoint(200, 200), 160, 40, 30);
        var (doc, _) = await Write(Designed("Draft", rect, rotation: 30, w: 160, h: 40));
        using (doc)
        {
            var read = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal(30, read.AppearanceRotation, 4);
            Assert.Equal(160, read.AppearanceSize!.Value.Width, 2);
            Assert.Equal(40, read.AppearanceSize!.Value.Height, 2);
        }
    }

    [Fact]
    public async Task HiddenAnnotations_AreLeftOffTheScreen_ButStillPrint()
    {
        var (doc, _) = await Write(Designed("NotApproved", new PdfRect(100, 100, 160, 40)));
        using (doc)
        {
            int number = ((PdfIndirectRef)((PdfArray)doc.Resolver.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!)[0]).ObjectNumber;
            using (var shown = await Render(await doc.GetPageDisplayListAsync(1)))
                Assert.True(At(shown, 101, 120).G < 110, "the stamp is drawn");

            Assert.Equal(new[] { 1 }, doc.SetHiddenAnnotations(new[] { number }));
            Assert.Empty(doc.SetHiddenAnnotations(new[] { number })); // no change, nothing to draw again
            using (var hidden = await Render(await doc.GetPageDisplayListAsync(1)))
            {
                var edge = At(hidden, 101, 120);
                Assert.True(edge.R > 245 && edge.G > 245 && edge.B > 245, $"left off the screen, got {edge}");
            }
            using (var printed = await Render(await doc.GetPagePrintDisplayListAsync(1)))
                Assert.True(At(printed, 101, 120).G < 110, "still printed");

            Assert.Equal(new[] { 1 }, doc.SetHiddenAnnotations(Array.Empty<int>()));
            using var back = await Render(await doc.GetPageDisplayListAsync(1));
            Assert.True(At(back, 101, 120).G < 110, "drawn again");
        }
    }

    [Fact]
    public async Task ThePreviewOfAKeptAppearance_IsTheTurnedBox_WithTheStampOnIt()
    {
        var (doc, _) = await Write(Designed("NotApproved", new PdfRect(100, 100, 160, 40)));
        using (doc)
        {
            Assert.Null(PdfStamps.PreviewOfAppearance(doc, 1, 5));
            var bytes = PdfStamps.PreviewOfAppearance(doc, 1, 0, turn: 90)!;
            using var preview = PdfVectorDocument.Open(new Parsing.PdfMemoryByteSource(bytes));
            var size = preview.PageTree.Pages[0].PageSize;
            Assert.Equal(40, size.Width, 2);
            Assert.Equal(160, size.Height, 2);
            using var page = await Render(await preview.GetPageDisplayListAsync(1));
            var left = At(page, 1.5, 80, 160);
            Assert.True(left.R > 150 && left.G < 110, $"the red band up the left edge, got {left}");

            var black = PdfStamps.PreviewOfAppearance(doc, 1, 0, black: true)!;
            using var onBlack = PdfVectorDocument.Open(new Parsing.PdfMemoryByteSource(black));
            using var dark = await Render(await onBlack.GetPageDisplayListAsync(1));
            var corner = At(dark, 0.2, 39.8, 40);
            Assert.True(corner.R < 10 && corner.G < 10 && corner.B < 10, $"a black page, got {corner}");
        }
    }
}
