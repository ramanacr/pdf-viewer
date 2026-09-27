using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Generated annotation appearances: each is put in a page as the annotation's /AP /N, then drawn
/// by both the vector renderer and PDFium, and the pixels are checked where the border, fill,
/// stroke, icon or text must be (and must not be). Free text and stamps are read back as text by
/// PDFium from their embedded font.
/// </summary>
public class AnnotationAppearanceTests
{
    private const double Dpi = 144;
    private const double Scale = Dpi / 72;

    // ------------------------------------------------------------------ building

    private static PdfName N(string name) => new(name);
    private static PdfArray A(params double[] v) => new(v.Select(x => (PdfObject)new PdfReal(x)).ToList());
    private static PdfString S(string s) => PdfObjectWriter.TextString(s);

    private static PdfDictionary Annot(string subtype, double[] rect, params (string Key, PdfObject Value)[] entries)
    {
        var d = new Dictionary<string, PdfObject> { ["Type"] = N("Annot"), ["Subtype"] = N(subtype), ["Rect"] = A(rect), ["F"] = new PdfInteger(4) }; // Print: PDFium renders as for printing, and PDF/A requires it
        foreach (var (k, v) in entries) d[k] = v;
        return new PdfDictionary(d);
    }

    private static PdfDictionary D(params (string Key, PdfObject Value)[] entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Value));

    private static byte[] Serialize(PdfObject obj)
    {
        using var ms = new MemoryStream();
        PdfObjectWriter.Write(ms, obj);
        return ms.ToArray();
    }

    private static async Task<PdfStream?> Generate(PdfDictionary annot, Func<PdfObject, int>? newObject = null)
    {
        var host = new VectorPdfBuilder();
        host.AddPage(string.Empty);
        using var doc = await PdfVectorDocument.OpenAsync(host.Build());
        return PdfAnnotationAppearances.Generate(annot, doc.Resolver, newObject ?? (_ => 1000));
    }

    /// <summary>A 200 x 200 page with <paramref name="content"/> under the annotation, whose /AP /N is the generated form.</summary>
    private static async Task<(byte[] Pdf, PdfStream Form)> Page(PdfDictionary annot, string content = "")
    {
        var b = new VectorPdfBuilder();
        int NewObject(PdfObject o) => b.Add(Serialize(o));
        var form = await Generate(annot, NewObject);
        Assert.NotNull(form);
        int ap = NewObject(form!);
        var entries = new Dictionary<string, PdfObject>(annot.Entries) { ["AP"] = D(("N", new PdfIndirectRef(ap))) };
        int number = NewObject(new PdfDictionary(entries));
        b.AddPage(content, extra: $"/Annots [{number} 0 R]");
        return (b.Build(), form!);
    }

    /// <summary>
    /// PDFium's text layer covers page content only, not annotation appearances: to read the
    /// generated text back, the page itself draws the form where the annotation would.
    /// </summary>
    private static async Task<string> FormText(PdfDictionary annot)
    {
        var b = new VectorPdfBuilder();
        int NewObject(PdfObject o) => b.Add(Serialize(o));
        var form = await Generate(annot, NewObject);
        Assert.NotNull(form);
        int ap = NewObject(form!);
        var rect = Assert.IsType<PdfArray>(annot["Rect"]).Select(o => o.TryGetNumber(out double v) ? v : 0).ToArray();
        string x = PdfObjectWriter.FormatReal(Math.Min(rect[0], rect[2])), y = PdfObjectWriter.FormatReal(Math.Min(rect[1], rect[3]));
        b.AddPage($"q 1 0 0 1 {x} {y} cm /Ap Do Q", $"<< /XObject << /Ap {ap} 0 R >> >>");
        return Squash(await PdfiumText(b.Build()));
    }

    private static string Content(PdfStream form)
    {
        using var z = new ZLibStream(new MemoryStream(form.GetRawBytes().ToArray()), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return Encoding.Latin1.GetString(o.ToArray());
    }

    // ------------------------------------------------------------------ rendering

    private sealed class Rendered : IDisposable
    {
        public required string Name { get; init; }
        public required RenderedPage Page { get; init; }
        public void Dispose() => Page.Dispose();

        /// <summary>The colour at a point in page space.</summary>
        public (int R, int G, int B) At(double x, double y)
        {
            int px = Math.Clamp((int)(x * Scale), 0, Page.WidthPixels - 1);
            int py = Math.Clamp((int)((200 - y) * Scale), 0, Page.HeightPixels - 1);
            var span = Page.Pixels.Span;
            int i = py * Page.Stride + px * 4;
            return (span[i + 2], span[i + 1], span[i]);
        }
    }

    private static async Task<Rendered[]> RenderBoth(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var vector = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = Dpi });
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        var reference = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = Dpi });
        return new[] { new Rendered { Name = "vector", Page = vector }, new Rendered { Name = "pdfium", Page = reference } };
    }

    private static async Task Check(byte[] pdf, Action<Rendered> check)
    {
        var pages = await RenderBoth(pdf);
        try
        {
            foreach (var p in pages) check(p);
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    private static void Near(Rendered r, double x, double y, (int R, int G, int B) expected, int tolerance = 40)
    {
        var c = r.At(x, y);
        bool ok = Math.Abs(c.R - expected.R) <= tolerance && Math.Abs(c.G - expected.G) <= tolerance && Math.Abs(c.B - expected.B) <= tolerance;
        Assert.True(ok, $"{r.Name}: at ({x}, {y}) expected {expected}, got {c}");
    }

    private static readonly (int, int, int) White = (255, 255, 255);
    private static readonly (int, int, int) Red = (255, 0, 0);
    private static readonly (int, int, int) Blue = (0, 0, 255);
    private static readonly (int, int, int) Black = (0, 0, 0);

    private static async Task<string> PdfiumText(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, 1);
    }

    private static string Squash(string s) => string.Join(' ', s.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------------ the form

    [Fact]
    public async Task Appearance_IsAFlateForm_WhoseBBoxIsTheRectAtTheOrigin()
    {
        var form = await Generate(Annot("Square", new[] { 150.0, 130, 50, 50 }, ("C", A(1, 0, 0))));
        Assert.NotNull(form);
        var d = form!.Dictionary;
        Assert.Equal("XObject", d.GetName("Type"));
        Assert.Equal("Form", d.GetName("Subtype"));
        Assert.Equal("FlateDecode", d.GetName("Filter"));
        Assert.IsType<PdfDictionary>(d["Resources"]);
        Assert.Null(d["Matrix"]);
        var bbox = Assert.IsType<PdfArray>(d["BBox"]).Select(o => o.TryGetNumber(out double v) ? v : double.NaN).ToArray();
        Assert.Equal(new[] { 0.0, 0, 100, 80 }, bbox);
        Assert.Contains("re", Content(form));
    }

    [Theory]
    [InlineData("Popup")]
    [InlineData("Link")]
    [InlineData("Widget")]
    [InlineData("Sound")]
    public async Task UnsupportedTypes_GetNoAppearance(string subtype) =>
        Assert.Null(await Generate(Annot(subtype, new[] { 10.0, 10, 50, 50 })));

    [Fact]
    public async Task WithoutARect_ThereIsNoAppearance()
    {
        var annot = new PdfDictionary(new Dictionary<string, PdfObject> { ["Subtype"] = N("Square") });
        Assert.Null(await Generate(annot));
    }

    // ------------------------------------------------------------------ shapes

    [Fact]
    public async Task Square_BorderSitsInsideTheRect_AndTheInteriorIsFilled()
    {
        var (pdf, _) = await Page(Annot("Square", new[] { 50.0, 50, 150, 130 },
            ("C", A(1, 0, 0)), ("IC", A(0, 0, 1)), ("BS", D(("W", new PdfReal(6))))));
        await Check(pdf, r =>
        {
            Near(r, 53, 90, Red);     // the border band, inside the rectangle
            Near(r, 147, 90, Red);
            Near(r, 100, 127, Red);
            Near(r, 100, 90, Blue);   // interior colour
            Near(r, 45, 90, White);   // outside
        });
    }

    [Fact]
    public async Task Square_WithoutInteriorColour_IsHollow()
    {
        var (pdf, _) = await Page(Annot("Square", new[] { 50.0, 50, 150, 130 }, ("C", A(1, 0, 0)), ("Border", A(0, 0, 3))));
        await Check(pdf, r =>
        {
            Near(r, 51.5, 90, Red);
            Near(r, 100, 90, White);
        });
    }

    [Fact]
    public async Task Circle_IsAnEllipseInscribedInTheRect()
    {
        var (pdf, form) = await Page(Annot("Circle", new[] { 40.0, 40, 160, 120 }, ("C", A(0, 0.5, 0)), ("BS", D(("W", new PdfReal(4))))));
        Assert.Contains(" c\n", Content(form));
        await Check(pdf, r =>
        {
            Near(r, 42, 80, (0, 128, 0));     // left of the ellipse
            Near(r, 100, 118, (0, 128, 0));   // top
            Near(r, 44, 44, White);           // the rectangle's corner is outside the ellipse
            Near(r, 100, 80, White);
        });
    }

    [Fact]
    public async Task Colours_AreWrittenInTheSpaceOfTheirArray()
    {
        string gray = Content((await Generate(Annot("Square", new[] { 0.0, 0, 50, 50 }, ("C", A(0.5)))))!);
        Assert.Contains("0.5 G", gray);
        string cmyk = Content((await Generate(Annot("Square", new[] { 0.0, 0, 50, 50 }, ("C", A(0, 1, 1, 0)), ("IC", A(0.1, 0, 0, 0)))))!);
        Assert.Contains("0 1 1 0 K", cmyk);
        Assert.Contains("0.1 0 0 0 k", cmyk);
        // An empty colour array is transparent: the border is not drawn at all.
        string none = Content((await Generate(Annot("Square", new[] { 0.0, 0, 50, 50 }, ("C", new PdfArray(Array.Empty<PdfObject>())))))!);
        Assert.DoesNotContain(" re", none);
    }

    [Fact]
    public async Task Opacity_BlendsTheWholeAnnotation()
    {
        var (pdf, form) = await Page(Annot("Square", new[] { 50.0, 50, 150, 150 },
            ("C", A(1, 0, 0)), ("IC", A(1, 0, 0)), ("CA", new PdfReal(0.5))));
        var gs = Assert.IsType<PdfDictionary>(Assert.IsType<PdfDictionary>(form.Dictionary["Resources"])["ExtGState"]);
        var state = Assert.IsType<PdfDictionary>(gs.Entries.Values.Single());
        Assert.Equal(0.5, state.GetNumber("CA"));
        Assert.Equal(0.5, state.GetNumber("ca"));
        await Check(pdf, r => Near(r, 100, 100, (255, 128, 128), 25));
    }

    [Fact]
    public async Task DashedBorder_HasGaps()
    {
        var (pdf, form) = await Page(Annot("Square", new[] { 50.0, 50, 150, 150 }, ("C", A(1, 0, 0)),
            ("BS", D(("W", new PdfReal(2)), ("S", N("D")), ("D", A(6, 6))))));
        Assert.Contains("[6 6] 0 d", Content(form));
        await Check(pdf, r =>
        {
            int red = 0, white = 0;
            for (double x = 55; x < 145; x += 1)
            {
                var c = r.At(x, 149);
                if (c.R > 200 && c.G < 80) red++;
                if (c.R > 230 && c.G > 230) white++;
            }
            Assert.True(red > 20 && white > 20, $"{r.Name}: {red} red, {white} white along the dashed top edge");
        });
    }

    [Fact]
    public async Task CloudyBorder_IsDrawnInArcs_WithinTheRect()
    {
        var (pdf, form) = await Page(Annot("Square", new[] { 30.0, 30, 170, 170 }, ("C", A(1, 0, 0)), ("IC", A(0, 0, 1)),
            ("BE", D(("S", N("C")), ("I", new PdfReal(1))))));
        string content = Content(form);
        Assert.True(content.Split(" c\n").Length > 40, "a cloud is many arcs");
        await Check(pdf, r =>
        {
            Near(r, 100, 100, Blue);
            Near(r, 25, 100, White);
            int red = 0;
            for (double y = 40; y < 160; y += 0.5)
            for (double x = 30; x < 42; x += 0.5)
            {
                var c = r.At(x, y);
                if (c.R > 180 && c.G < 90 && c.B < 90) red++;
            }
            Assert.True(red > 30, $"{r.Name}: the cloud's left edge has {red} red samples");
        });
    }

    [Fact]
    public async Task Line_WithAClosedArrow_AtTheEnd()
    {
        var (pdf, _) = await Page(Annot("Line", new[] { 20.0, 80, 180, 120 }, ("L", A(30, 100, 170, 100)),
            ("LE", new PdfArray(new PdfObject[] { N("None"), N("ClosedArrow") })),
            ("C", A(1, 0, 0)), ("IC", A(1, 0, 0)), ("BS", D(("W", new PdfReal(2))))));
        await Check(pdf, r =>
        {
            Near(r, 100, 100, Red);
            Near(r, 163, 102.5, Red);   // inside the arrow head, off the line
            Near(r, 163, 97.5, Red);
            Near(r, 35, 103, White);   // no ending at the start
            Near(r, 100, 106, White);
        });
    }

    [Fact]
    public async Task Line_LeaderLines_MoveTheLineToTheLeftOfItsDirection()
    {
        var (pdf, _) = await Page(Annot("Line", new[] { 20.0, 80, 180, 130 }, ("L", A(30, 90, 170, 90)),
            ("LL", new PdfReal(20)), ("C", A(0, 0, 1)), ("BS", D(("W", new PdfReal(2))))));
        await Check(pdf, r =>
        {
            Near(r, 100, 110, Blue);    // the line, 20 above its endpoints
            Near(r, 100, 90, White);
            Near(r, 30, 100, Blue);     // a leader line
        });
    }

    [Theory]
    [InlineData("Square")]
    [InlineData("Circle")]
    [InlineData("Diamond")]
    [InlineData("OpenArrow")]
    [InlineData("ROpenArrow")]
    [InlineData("RClosedArrow")]
    [InlineData("Butt")]
    [InlineData("Slash")]
    public async Task LineEndings_AreDrawnAroundTheEndpoint(string ending)
    {
        var (pdf, _) = await Page(Annot("Line", new[] { 20.0, 60, 180, 140 }, ("L", A(40, 100, 160, 100)),
            ("LE", new PdfArray(new PdfObject[] { N(ending), N(ending) })),
            ("C", A(0, 0, 0)), ("BS", D(("W", new PdfReal(2))))));
        await Check(pdf, r =>
        {
            int dark = 0;
            for (double y = 101.5; y < 112; y += 0.5)
            for (double x = 145; x < 176; x += 0.5)
                if (r.At(x, y).R < 100) dark++;
            Assert.True(dark > 4, $"{r.Name}: {ending} has {dark} dark samples off the line");
        });
    }

    [Fact]
    public async Task Polygon_IsClosedAndFilled_PolyLineIsOpen()
    {
        var (pdf, _) = await Page(Annot("Polygon", new[] { 20.0, 20, 180, 180 }, ("Vertices", A(30, 30, 170, 30, 100, 170)),
            ("C", A(0, 0, 0)), ("IC", A(0, 0, 1))));
        await Check(pdf, r =>
        {
            Near(r, 100, 80, Blue);
            Near(r, 100, 30, Black, 90);
            Near(r, 40, 150, White);
        });

        var (open, _) = await Page(Annot("PolyLine", new[] { 20.0, 20, 180, 180 }, ("Vertices", A(30, 30, 170, 30, 100, 170)),
            ("C", A(1, 0, 0)), ("IC", A(0, 0, 1)), ("BS", D(("W", new PdfReal(2))))));
        await Check(open, r =>
        {
            Near(r, 100, 30, Red);
            Near(r, 100, 80, White);   // not filled, not closed
            Near(r, 65, 100, White);   // no closing edge from (100,170) back to (30,30)
        });
    }

    [Fact]
    public async Task Ink_StrokesPassThroughEveryPoint()
    {
        var points = new List<double>();
        for (int i = 0; i <= 12; i++) { points.Add(30 + i * 11); points.Add(100 + 40 * Math.Sin(i * 0.6)); }
        var (pdf, form) = await Page(Annot("Ink", new[] { 20.0, 50, 180, 150 },
            ("InkList", new PdfArray(new PdfObject[] { A(points.ToArray()) })), ("C", A(0, 0, 1)), ("BS", D(("W", new PdfReal(3))))));
        Assert.Contains("1 J 1 j", Content(form));
        await Check(pdf, r =>
        {
            for (int i = 0; i + 1 < points.Count; i += 2)
                Near(r, points[i], points[i + 1], Blue, 60);
            Near(r, 100, 60, White);
        });
    }

    // ------------------------------------------------------------------ text markup

    [Fact]
    public async Task Highlight_MultipliesOverThePage()
    {
        // A black block under part of the highlight: under Multiply it stays black, white turns yellow.
        var (pdf, form) = await Page(Annot("Highlight", new[] { 30.0, 85, 170, 125 },
            ("QuadPoints", A(40, 120, 160, 120, 40, 90, 160, 90)), ("C", A(1, 1, 0))), "0 g 60 95 40 20 re f");
        var gs = Assert.IsType<PdfDictionary>(Assert.IsType<PdfDictionary>(form.Dictionary["Resources"])["ExtGState"]);
        Assert.Equal("Multiply", Assert.IsType<PdfDictionary>(gs.Entries.Values.Single()).GetName("BM"));
        await Check(pdf, r =>
        {
            Near(r, 130, 105, (255, 255, 0));
            Near(r, 80, 105, Black, 50);
            Near(r, 130, 80, White);
        });
    }

    [Fact]
    public async Task StrikeOut_CrossesTheMiddle_Underline_TheBottom()
    {
        var quad = A(40, 110, 160, 110, 40, 90, 160, 90);
        var (strike, _) = await Page(Annot("StrikeOut", new[] { 30.0, 85, 170, 115 }, ("QuadPoints", quad), ("C", A(1, 0, 0))));
        await Check(strike, r =>
        {
            Near(r, 100, 98.4, Red, 70);
            Near(r, 100, 106, White);
            Near(r, 100, 91.5, White);
        });
        var (under, _) = await Page(Annot("Underline", new[] { 30.0, 85, 170, 115 }, ("QuadPoints", quad), ("C", A(0, 0, 1))));
        await Check(under, r =>
        {
            Near(r, 100, 91.4, Blue, 70);
            Near(r, 100, 100, White);
        });
    }

    [Fact]
    public async Task Underline_FollowsARotatedQuad()
    {
        // Text running up the page: its top is on the left, so the underline is on the right.
        var quad = A(80, 40, 80, 160, 100, 40, 100, 160);
        var (pdf, _) = await Page(Annot("Underline", new[] { 70.0, 30, 110, 170 }, ("QuadPoints", quad), ("C", A(1, 0, 0))));
        await Check(pdf, r =>
        {
            Near(r, 98.6, 100, Red, 70);
            Near(r, 81.4, 100, White);
        });
    }

    [Fact]
    public async Task Squiggly_WavesAlongTheBottom()
    {
        var (pdf, _) = await Page(Annot("Squiggly", new[] { 30.0, 85, 170, 115 },
            ("QuadPoints", A(40, 110, 160, 110, 40, 90, 160, 90)), ("C", A(1, 0, 0))));
        await Check(pdf, r =>
        {
            int low = 0, high = 0;
            for (double x = 45; x < 155; x += 0.5)
            {
                if (r.At(x, 90.8).R > 200 && r.At(x, 90.8).G < 120) low++;
                if (r.At(x, 92.6).R > 200 && r.At(x, 92.6).G < 120) high++;
            }
            Assert.True(low > 10 && high > 10, $"{r.Name}: wave samples low {low}, high {high}");
            Near(r, 100, 100, White);
        });
    }

    // ------------------------------------------------------------------ icons

    [Fact]
    public async Task Note_IsA20PointIcon_AtTheTopLeft_InItsColour()
    {
        var (pdf, _) = await Page(Annot("Text", new[] { 50.0, 100, 90, 140 }, ("C", A(1, 1, 0))));
        await Check(pdf, r =>
        {
            Near(r, 58, 133.5, (255, 255, 0));   // the sheet, between its ruled lines
            Assert.True(r.At(57, 135).G < 200, $"{r.Name}: a ruled line crosses the sheet");
            Near(r, 80, 110, White);            // the rest of the rectangle stays empty
        });
    }

    [Fact]
    public async Task Comment_IsTheFallbackIcon()
    {
        var (pdf, form) = await Page(Annot("Text", new[] { 50.0, 100, 70, 120 }, ("Name", N("SomethingElse")), ("C", A(0, 0, 1))));
        Assert.Contains("4.5 2 l", Content(form)); // the speech bubble's tail
        await Check(pdf, r => Near(r, 60, 117.5, Blue, 60));
    }

    [Theory]
    [InlineData("Help")]
    [InlineData("Insert")]
    [InlineData("Key")]
    [InlineData("NewParagraph")]
    [InlineData("Paragraph")]
    [InlineData("Check")]
    [InlineData("Cross")]
    [InlineData("Circle")]
    [InlineData("Star")]
    public async Task OtherNoteIcons_AreDrawnInTheIconSquare(string name)
    {
        var (pdf, _) = await Page(Annot("Text", new[] { 50.0, 100, 70, 120 }, ("Name", N(name)), ("C", A(1, 0, 0))));
        await Check(pdf, r =>
        {
            int red = 0;
            for (double y = 100.5; y < 120; y += 0.5)
            for (double x = 50.5; x < 70; x += 0.5)
            {
                var c = r.At(x, y);
                if (c.R > 200 && c.G < 80) red++;
            }
            Assert.True(red > 20, $"{r.Name}: {name} icon has {red} red samples");
        });
    }

    [Theory]
    [InlineData("PushPin", 10, 16)]
    [InlineData("Graph", 10.5, 10)]
    [InlineData("Tag", 13, 10)]
    public async Task FileAttachment_DrawsItsIcon(string name, double ix, double iy)
    {
        var (pdf, _) = await Page(Annot("FileAttachment", new[] { 50.0, 100, 70, 120 }, ("Name", N(name)), ("C", A(0, 0, 1))));
        await Check(pdf, r => Near(r, 50 + ix, 100 + iy, Blue, 60));
    }

    [Fact]
    public async Task Paperclip_IsStrokedInItsColour()
    {
        var (pdf, _) = await Page(Annot("FileAttachment", new[] { 50.0, 100, 70, 120 }, ("Name", N("Paperclip")), ("C", A(0, 0, 1))));
        await Check(pdf, r => Near(r, 63, 10 + 100, Blue, 80));
    }

    [Fact]
    public async Task Caret_IsFilledInItsColour()
    {
        var (pdf, _) = await Page(Annot("Caret", new[] { 90.0, 90, 110, 110 }, ("C", A(0, 0, 1))));
        await Check(pdf, r =>
        {
            Near(r, 100, 92, Blue, 60);
            Near(r, 92, 105, White);
        });
    }

    // ------------------------------------------------------------------ text

    [Fact]
    public async Task FreeText_WrapsItsContents_InAnEmbeddedFont_ThatPdfiumCanRead()
    {
        const string text = "The quick brown fox jumps over the lazy dog again and again";
        var annot = Annot("FreeText", new[] { 20.0, 60, 180, 140 }, ("Contents", S(text)),
            ("DA", S("/Helv 12 Tf 0 0 1 rg")), ("BS", D(("W", new PdfReal(1)))));
        var (pdf, form) = await Page(annot);
        var fonts = Assert.IsType<PdfDictionary>(Assert.IsType<PdfDictionary>(form.Dictionary["Resources"])["Font"]);
        Assert.Single(fonts.Entries);
        Assert.Contains("FontFile2", Encoding.Latin1.GetString(pdf));

        string extracted = await FormText(annot);
        foreach (var word in text.Split(' ')) Assert.Contains(word, extracted);

        // The text wrapped: its glyphs sit on more than one baseline, all inside the box.
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var list = await doc.GetPageDisplayListAsync(1);
            var origins = list.Commands.OfType<DrawGlyphRun>()
                .SelectMany(c => c.Run.Glyphs.Select(g => c.Run.TextToPage.Transform(g.OffsetX, g.OffsetY))).ToList();
            Assert.NotEmpty(origins);
            Assert.True(origins.Select(p => Math.Round(p.Y)).Distinct().Count() >= 2, "wrapped onto several lines");
            Assert.All(origins, p => Assert.InRange(p.X, 20, 180));
            Assert.All(origins, p => Assert.InRange(p.Y, 60, 140));
        }

        await Check(pdf, r =>
        {
            Near(r, 20.5, 100, Blue, 90);    // the border takes the text colour when /DA has no stroke colour
            int blue = 0;
            for (double y = 62; y < 138; y += 0.5)
            for (double x = 22; x < 178; x += 0.5)
            {
                var c = r.At(x, y);
                if (c.B > 150 && c.R < 110 && c.G < 110) blue++;
            }
            Assert.True(blue > 50, $"{r.Name}: {blue} blue text samples");
        });
    }

    [Fact]
    public async Task FreeText_BackgroundAndRightAlignment()
    {
        var annot = Annot("FreeText", new[] { 20.0, 60, 180, 140 }, ("Contents", S("Right")),
            ("DA", S("/Helv 14 Tf 0 g")), ("C", A(1, 1, 0)), ("Q", new PdfInteger(2)), ("Border", A(0, 0, 0)));
        var (pdf, _) = await Page(annot);
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var list = await doc.GetPageDisplayListAsync(1);
            var xs = list.Commands.OfType<DrawGlyphRun>().SelectMany(c => c.Run.Glyphs.Select(g => c.Run.TextToPage.Transform(g.OffsetX, g.OffsetY).X)).ToList();
            Assert.InRange(xs.Min(), 120, 178);
        }
        Assert.Contains("Right", await FormText(annot));
        await Check(pdf, r => Near(r, 40, 80, (255, 255, 0)));
    }

    [Fact]
    public async Task FreeText_Callout_DrawsItsLineAndEnding()
    {
        var annot = Annot("FreeText", new[] { 20.0, 20, 180, 180 }, ("Contents", S("Look")),
            ("DA", S("/Helv 10 Tf 1 0 0 rg")), ("IT", N("FreeTextCallout")), ("CL", A(30, 30, 60, 60, 90, 60)),
            ("LE", N("OpenArrow")), ("RD", A(70, 20, 10, 100)));
        var (pdf, _) = await Page(annot);
        await Check(pdf, r =>
        {
            Near(r, 45, 45, Red, 80);      // along the callout's first leg
            Near(r, 100, 160, White);      // above the box
        });
        Assert.Contains("Look", await FormText(annot));
    }

    [Fact]
    public async Task Stamp_ShowsItsNameInARoundedBorder()
    {
        var annot = Annot("Stamp", new[] { 30.0, 60, 170, 110 }, ("Name", N("NotApproved")));
        var (pdf, _) = await Page(annot);
        Assert.Contains("NOT APPROVED", await FormText(annot));
        await Check(pdf, r =>
        {
            var edge = r.At(31.5, 85);
            Assert.True(edge.R > 150 && edge.G < 90, $"{r.Name}: the red border, got {edge}");
            Near(r, 25, 85, White);
        });
    }

    [Fact]
    public async Task Stamp_ColourComesFromC_WhenGiven()
    {
        var annot = Annot("Stamp", new[] { 30.0, 60, 170, 110 }, ("Name", N("Approved")), ("C", A(0, 0, 1)));
        var (_, form) = await Page(annot);
        Assert.Contains("0 0 1 RG", Content(form));
        Assert.Contains("APPROVED", await FormText(annot));
    }

    [Fact]
    public async Task LineCaption_IsTextAlongTheLine()
    {
        var annot = Annot("Line", new[] { 20.0, 80, 180, 120 }, ("L", A(30, 100, 170, 100)),
            ("Cap", PdfBoolean.True), ("Contents", S("12 mm")), ("C", A(0, 0, 0)));
        var (pdf, _) = await Page(annot);
        Assert.Contains("12 mm", await FormText(annot));
        await Check(pdf, r => Near(r, 50, 100, Black, 90));
    }
}
