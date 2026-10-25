using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Stamp annotations (ISO 32000-2 12.5.6.12): designed standard and dynamic stamps, pictures and
/// PDF pages as custom stamps, turned stamps; each is written with /Name and a normal appearance,
/// and must survive saving and reading, moving, flattening, PDF/A conversion and printing.
/// </summary>
public class StampTests
{
    private const double Dpi = 144;

    private static byte[] Blank(string content = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage(content, mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static string Decode(PdfStream form)
    {
        using var z = new ZLibStream(new MemoryStream(form.GetRawBytes().ToArray()), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return Encoding.Latin1.GetString(o.ToArray());
    }

    private static PdfAnnotation Designed(string name, PdfRect rect, string? subtitle = null, double rotation = 0, double w = 0, double h = 0) => new()
    {
        PageNumber = 1, Subtype = "Stamp", Icon = name, Rect = rect, Contents = name.ToUpperInvariant(), Author = "Tester",
        Stamp = new PdfStampAppearance { Subtitle = subtitle, Rotation = rotation, Width = w, Height = h },
    };

    private static async Task<(PdfVectorDocument Doc, byte[] Bytes)> Write(byte[] original, params PdfAnnotation[] annotations)
    {
        using var doc = await PdfVectorDocument.OpenAsync(original);
        var bytes = PdfAnnotationEditor.Apply(doc, original, e => { foreach (var a in annotations) e.Add(a); });
        return (await PdfVectorDocument.OpenAsync(bytes), bytes);
    }

    private static (PdfDictionary Annot, PdfStream Form) First(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        var annot = (PdfDictionary)r.Resolve(annots[0])!;
        var form = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(annot["AP"])!)["N"])!;
        return (annot, form);
    }

    private static async Task<RenderedPage> Render(PdfVectorDocument doc, bool print = false)
    {
        var list = print ? await doc.GetPagePrintDisplayListAsync(1) : await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        return await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = Dpi });
    }

    private static (int R, int G, int B) At(RenderedPage page, double x, double y)
    {
        double s = Dpi / 72;
        int px = Math.Clamp((int)(x * s), 0, page.WidthPixels - 1), py = Math.Clamp((int)((400 - y) * s), 0, page.HeightPixels - 1);
        var span = page.Pixels.Span;
        int i = py * page.Stride + px * 4;
        return (span[i + 2], span[i + 1], span[i]);
    }

    // ------------------------------------------------------------------ designed stamps

    [Fact]
    public void TheCatalogue_HasTheAcrobatStamps_UnderTheirNames()
    {
        var names = PdfStamps.Standard.Select(s => s.Name).ToHashSet();
        foreach (string n in new[] { "Approved", "Draft", "Confidential", "Final", "ForComment", "NotApproved", "Received", "SBReviewed", "SBRevised", "SBVoid" })
            Assert.Contains(n, names);
        Assert.All(PdfStamps.Dynamic, s => Assert.True(s.Dynamic));
        Assert.Equal("By Jane Doe at 09:05, 3 Feb 2026", PdfStamps.DynamicLine(" Jane Doe ", new DateTime(2026, 2, 3, 9, 5, 0)));
    }

    [Fact]
    public async Task ADynamicStamp_IsWrittenWithNameAndAppearance_ShowingWhoAndWhen()
    {
        string line = PdfStamps.DynamicLine("Jane Doe", new DateTime(2026, 10, 25, 10, 42, 0));
        var (doc, _) = await Write(Blank(), Designed("Approved", new PdfRect(100, 200, 160, 56), line));
        using (doc)
        {
            var (annot, form) = First(doc);
            Assert.Equal("Stamp", annot.GetName("Subtype"));
            Assert.Equal("Approved", annot.GetName("Name"));
            Assert.Equal(4, annot.GetInteger("F"));                       // printed
            Assert.Equal("APPROVED", PdfTextStrings.Decode(doc.Resolver.Resolve(annot["Contents"])));
            Assert.Equal("Form", form.Dictionary.GetName("Subtype"));
            Assert.NotNull(doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(form.Dictionary["Resources"])!)["Font"]));
            string content = Decode(form);
            Assert.Equal(2, content.Split("BT").Length - 1);                // the label and the second line
            var read = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal("Approved", read.Icon);
            Assert.True(read.HasAppearance);
            Assert.Equal(0, read.AppearanceRotation);

            using var page = await Render(doc);
            var edge = At(page, 101, 228);
            Assert.True(edge.G > edge.R + 40 && edge.G > edge.B, $"the green band, got {edge}");
            var outside = At(page, 95, 228);
            Assert.True(outside.R > 245 && outside.G > 245 && outside.B > 245, $"nothing outside, got {outside}");
        }
    }

    [Fact]
    public async Task ATurnedStamp_HasARotationMatrix_AndFillsItsTurnedBox()
    {
        var bounds = PdfStamps.RotatedBounds(new PdfPoint(200, 200), 160, 40, 90);
        Assert.Equal(40, bounds.Width, 6);
        Assert.Equal(160, bounds.Height, 6);
        var (doc, _) = await Write(Blank(), Designed("NotApproved", bounds, rotation: 90, w: 160, h: 40));
        using (doc)
        {
            var (_, form) = First(doc);
            var m = PdfDocumentInfo.Numbers(doc.Resolver.Resolve(form.Dictionary["Matrix"]), doc.Resolver)!;
            Assert.Equal(new[] { 0.0, 1, -1, 0, 0, 0 }, m);
            Assert.Equal(90, PdfAnnotationReader.Read(doc, 1).Single().AppearanceRotation, 6);
            using var page = await Render(doc);
            // The red band now runs up the left and right edges of the 40 x 160 box.
            var left = At(page, 181, 200);
            Assert.True(left.R > 150 && left.G < 110, $"the red band on the left, got {left}");
            var below = At(page, 200, 116);
            Assert.True(below.R > 245 && below.G > 245, $"nothing under the turned stamp, got {below}");
        }
    }

    // ------------------------------------------------------------------ custom stamps

    private static PdfImageContent Picture()
    {
        int w = 8, h = 4;
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            rgb[i * 3] = 200; rgb[i * 3 + 1] = 20; rgb[i * 3 + 2] = 160; // purple
            alpha[i] = (byte)(i % w < w / 2 ? 255 : 0);                     // the right half is see-through
        }
        return new PdfImageContent(w, h, rgb, PdfImageEncoding.Rgb, alpha);
    }

    [Fact]
    public async Task APictureStamp_DrawsTheImage_WithItsTransparency()
    {
        var stamp = new PdfAnnotation
        {
            PageNumber = 1, Subtype = "Stamp", Icon = "Custom", Rect = new PdfRect(100, 100, 160, 80), Contents = "Logo",
            Stamp = new PdfStampAppearance { Image = Picture() },
        };
        var (doc, _) = await Write(Blank(), stamp);
        using (doc)
        {
            var (annot, form) = First(doc);
            Assert.Equal("Custom", annot.GetName("Name"));
            var xobjects = (PdfDictionary)doc.Resolver.Resolve(((PdfDictionary)doc.Resolver.Resolve(form.Dictionary["Resources"])!)["XObject"])!;
            var image = (PdfStream)doc.Resolver.Resolve(xobjects["Im0"])!;
            Assert.Equal("Image", image.Dictionary.GetName("Subtype"));
            Assert.NotNull(image.Dictionary["SMask"]);
            using var page = await Render(doc);
            var left = At(page, 130, 140);
            Assert.True(left.R > 150 && left.G < 80 && left.B > 110, $"the picture, got {left}");
            var right = At(page, 230, 140);
            Assert.True(right.R > 245 && right.G > 245, $"see-through, got {right}");
        }
    }

    [Fact]
    public async Task APdfPageStamp_DrawsThePage_FromItsOwnResources()
    {
        // The stamp's PDF: a 100 x 50 page, red on the left, blue on the right, through a form XObject.
        var src = new VectorPdfBuilder();
        int inner = src.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 50 50]", "0 0 1 rg 0 0 50 50 re f");
        src.AddPage("1 0 0 rg 0 0 50 50 re f q 1 0 0 1 50 0 cm /Fx Do Q", $"<< /XObject << /Fx {inner} 0 R >> >>", mediaBox: "[0 0 100 50]");
        var stamp = new PdfAnnotation
        {
            PageNumber = 1, Subtype = "Stamp", Icon = "Custom", Rect = new PdfRect(100, 100, 200, 100),
            Stamp = new PdfStampAppearance { SourcePdf = src.Build(), SourcePage = 1 },
        };
        var (doc, _) = await Write(Blank(), stamp);
        using (doc)
        {
            using var page = await Render(doc);
            var left = At(page, 150, 150);
            var right = At(page, 250, 150);
            Assert.True(left.R > 200 && left.B < 60, $"red half, got {left}");
            Assert.True(right.B > 200 && right.R < 60, $"blue half, got {right}");
        }
    }

    // ------------------------------------------------------------------ keeping the appearance

    [Fact]
    public async Task MovingAStamp_KeepsItsAppearance_AndOnlyChangesTheRectangle()
    {
        var (doc, bytes) = await Write(Blank(), Designed("Approved", new PdfRect(100, 200, 160, 56), "By A at 10:00, 1 Jan 2026"));
        using (doc)
        {
            int formNumber = Assert.IsType<PdfIndirectRef>(((PdfDictionary)doc.Resolver.Resolve(First(doc).Annot["AP"])!)["N"]).ObjectNumber;
            var a = PdfAnnotationReader.Read(doc, 1).Single();
            a.Rect = new PdfRect(20, 20, 160, 56);
            var moved = PdfAnnotationEditor.Apply(doc, bytes, e => e.Modify(a));
            using var again = await PdfVectorDocument.OpenAsync(moved);
            var (annot, _) = First(again);
            Assert.Equal(formNumber, Assert.IsType<PdfIndirectRef>(((PdfDictionary)again.Resolver.Resolve(annot["AP"])!)["N"]).ObjectNumber);
            Assert.Equal(new PdfRect(20, 20, 160, 56), PdfAnnotationReader.Read(again, 1).Single().Rect);
        }
    }

    // ------------------------------------------------------------------ flatten, PDF/A, print

    [Fact]
    public async Task AStamp_Flattens_IntoThePage()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Blank());
        var bytes = PdfAnnotationEditor.Apply(doc, Blank(), e => { e.Add(Designed("NotApproved", new PdfRect(100, 100, 160, 40))); e.FlattenAll(); });
        using var flat = await PdfVectorDocument.OpenAsync(bytes);
        Assert.Empty(PdfAnnotationReader.Read(flat, 1));
        using var page = await Render(flat);
        var edge = At(page, 101, 120);
        Assert.True(edge.R > 150 && edge.G < 110, $"the red band, now page content, got {edge}");
    }

    [Fact]
    public async Task AStamp_SurvivesPdfAConversion_AndIsCompliant()
    {
        var (doc, bytes) = await Write(Blank("0 0 0 rg 20 20 50 50 re f"),
            Designed("Approved", new PdfRect(100, 200, 160, 56), "By Jane Doe at 10:42, 25 Oct 2026"),
            new PdfAnnotation { PageNumber = 1, Subtype = "Stamp", Icon = "Custom", Rect = new PdfRect(100, 50, 80, 40), Stamp = new PdfStampAppearance { Image = Picture() } });
        using (doc)
        {
            var result = PdfAConverter.Convert(doc, bytes, new PdfAConversionOptions { Flavour = PdfAFlavour.A2b });
            Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues.Select(i => $"{i.RuleId}: {i.Message}")));
            using var converted = await PdfVectorDocument.OpenAsync(result.Bytes);
            var stamps = PdfAnnotationReader.Read(converted, 1).Where(a => a.Subtype == "Stamp").ToList();
            Assert.Equal(2, stamps.Count);
            Assert.All(stamps, s => Assert.True(s.HasAppearance));
        }
    }

    [Fact]
    public async Task AStamp_Prints()
    {
        var (doc, _) = await Write(Blank(), Designed("Final", new PdfRect(100, 100, 160, 40)));
        using (doc)
        {
            var print = await doc.GetPagePrintDisplayListAsync(1);
            Assert.Contains(print.Commands, c => c is DrawGlyphRun);
            using var page = await Render(doc, print: true);
            var edge = At(page, 101, 120);
            Assert.True(edge.G > edge.R + 40, $"the green band on paper, got {edge}");
        }
    }

    [Fact]
    public void ThePreview_IsAPageTheSizeOfTheStamp_WithTheStampOnIt()
    {
        var bytes = PdfStamps.Preview(Designed("Draft", new PdfRect(300, 300, 120, 40)));
        using var doc = PdfVectorDocument.Open(new Parsing.MemoryByteSource(bytes));
        var size = doc.PageTree.Pages[0].PageSize;
        Assert.Equal(120, size.Width, 3);
        Assert.Equal(40, size.Height, 3);
        var stamp = PdfAnnotationReader.Read(doc, 1).Single();
        Assert.Equal(new PdfRect(0, 0, 120, 40), stamp.Rect);
        Assert.True(stamp.HasAppearance);
    }
}
