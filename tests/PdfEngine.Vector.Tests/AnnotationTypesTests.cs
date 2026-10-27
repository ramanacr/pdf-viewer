using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Lines with line endings, polygons, polylines, standard stamps and replies (ISO 32000-2
/// 12.5.6): each is created, saved, opened again and read back, with an appearance stream; XFDF
/// keeps them; and hostile values (huge vertex lists, numbers that are not finite, /IRT chains
/// that loop or go deep) stay bounded.
/// </summary>
public class AnnotationTypesTests
{
    private static byte[] Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage(string.Empty, mediaBox: "[0 0 400 400]");
        return b.Build();
    }

    private static async Task<(PdfVectorDocument Doc, byte[] Bytes)> Write(byte[] original, Action<PdfAnnotationEditor> edit)
    {
        using var doc = await PdfVectorDocument.OpenAsync(original);
        var bytes = PdfAnnotationEditor.Apply(doc, original, edit);
        return (await PdfVectorDocument.OpenAsync(bytes), bytes);
    }

    /// <summary>The decoded content of an annotation's normal appearance.</summary>
    private static string Appearance(PdfVectorDocument doc, int index, int page = 1)
    {
        var r = doc.Resolver;
        var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[page - 1].Dictionary["Annots"])!;
        var annot = (PdfDictionary)r.Resolve(annots[index])!;
        var form = (PdfStream)r.Resolve(((PdfDictionary)r.Resolve(annot["AP"])!)["N"])!;
        var raw = form.GetRawBytes().ToArray();
        if (r.Resolve(form.Dictionary["Filter"]) is not PdfName { Value: "FlateDecode" }) return Encoding.Latin1.GetString(raw);
        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return Encoding.Latin1.GetString(o.ToArray());
    }

    private static PdfAnnotation Line(string start, string end) => new()
    {
        PageNumber = 1, Subtype = "Line", Rect = new PdfRect(40, 40, 220, 120), Line = [50, 50, 250, 150],
        LineEndings = [start, end], Color = [0, 0, 1], InteriorColor = [1, 0, 0], BorderWidth = 2, Name = "line-1",
    };

    private static PdfAnnotation Poly(string subtype, params double[] xy) => new()
    {
        PageNumber = 1, Subtype = subtype, Rect = new PdfRect(20, 20, 300, 300), Color = [0, 0.5, 0], BorderWidth = 1.5,
        Vertices = Enumerable.Range(0, xy.Length / 2).Select(i => new PdfPoint(xy[2 * i], xy[2 * i + 1])).ToList(),
    };

    // ------------------------------------------------------------------ round trips

    [Fact]
    public async Task Line_WithLineEndings_RoundTrips_WithAnAppearance()
    {
        var (doc, _) = await Write(Blank(), e => e.Add(Line("Circle", "ClosedArrow")));
        using (doc)
        {
            var a = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal("Line", a.Subtype);
            Assert.Equal(new double[] { 50, 50, 250, 150 }, a.Line);
            Assert.Equal(new[] { "Circle", "ClosedArrow" }, a.LineEndings);
            Assert.Equal(new double[] { 1, 0, 0 }, a.InteriorColor);
            Assert.Equal(2, a.BorderWidth);
            Assert.True(a.HasAppearance);
            string content = Appearance(doc, 0);
            Assert.Contains("0 0 1 RG", content);
            // The closed arrow is a closed path, filled in the interior colour and stroked; the circle is four curves.
            Assert.Contains("1 0 0 rg", content);
            Assert.Contains("h\nB", content);
            Assert.True(content.Split('\n').Count(l => l.EndsWith(" c")) >= 4);
        }
    }

    [Fact]
    public async Task Polygon_AndPolyLine_RoundTrip_WithTheirVertices()
    {
        var polyline = Poly("PolyLine", 30, 30, 120, 200, 210, 60, 300, 280);
        polyline.LineEndings = ["None", "OpenArrow"];
        var polygon = Poly("Polygon", 40, 40, 200, 60, 150, 220);
        polygon.InteriorColor = [1, 1, 0];
        var (doc, _) = await Write(Blank(), e => { e.Add(polygon); e.Add(polyline); });
        using (doc)
        {
            var read = PdfAnnotationReader.Read(doc, 1);
            Assert.Equal(new[] { "Polygon", "PolyLine" }, read.Select(a => a.Subtype));
            Assert.Equal(polygon.Vertices, read[0].Vertices);
            Assert.Equal(polyline.Vertices, read[1].Vertices);
            Assert.Equal(new[] { "None", "OpenArrow" }, read[1].LineEndings);
            Assert.All(read, a => Assert.True(a.HasAppearance));
            Assert.Contains("1 1 0 rg", Appearance(doc, 0));
            // The polygon is closed (h) and filled and stroked; the polyline is open and stroked.
            Assert.Contains("h\nB", Appearance(doc, 0));
            string open = Appearance(doc, 1);
            Assert.DoesNotContain("h\nB", open);
            Assert.Contains("S", open);
        }
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Draft")]
    [InlineData("Confidential")]
    public async Task StandardStamp_RoundTrips_WithItsName_AndADrawnLabel(string name)
    {
        var stamp = new PdfAnnotation { PageNumber = 1, Subtype = "Stamp", Icon = name, Rect = new PdfRect(100, 100, 160, 40) };
        var (doc, _) = await Write(Blank(), e => e.Add(stamp));
        using (doc)
        {
            var a = PdfAnnotationReader.Read(doc, 1).Single();
            Assert.Equal("Stamp", a.Subtype);
            Assert.Equal(name, a.Icon);
            Assert.True(a.HasAppearance);
            // A rounded border (curves) and the label, in an embedded font.
            string content = Appearance(doc, 0);
            Assert.Contains(" c\n", content);
            Assert.Contains("BT", content);
        }
    }

    [Fact]
    public async Task Replies_FindTheirParentByName_AndFormAThread()
    {
        var parent = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(50, 300, 20, 20), Name = "p1", Contents = "Question" };
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = parent.Rect, Name = "r1", Contents = "Answer", InReplyToName = "p1" };
        var nested = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = parent.Rect, Name = "r2", Contents = "Thanks", InReplyToName = "r1" };
        int parentNumber = 0, replyNumber = 0;
        var (doc, bytes) = await Write(Blank(), e => { parentNumber = e.Add(parent); replyNumber = e.Add(reply); e.Add(nested); });
        using (doc)
        {
            var read = PdfAnnotationReader.Read(doc, 1);
            Assert.Equal(0, read[0].InReplyTo);
            Assert.Equal(parentNumber, read[1].InReplyTo);
            Assert.Equal(replyNumber, read[2].InReplyTo);
            Assert.All(read.Skip(1), a => Assert.True(PdfAnnotationThreads.IsReply(a)));
            // A reply's appearance draws nothing: no second note icon over the first.
            Assert.Equal(string.Empty, Appearance(doc, 1));
            Assert.NotEqual(string.Empty, Appearance(doc, 0));

            var threads = PdfAnnotationThreads.Threads(read);
            Assert.Equal(new[] { "Answer", "Thanks" }, threads[parentNumber].Select(a => a.Contents));

            // Deleting the question deletes the answers, the answer to the answer too.
            var after = PdfAnnotationEditor.Apply(doc, bytes, e => e.Delete(1, 0));
            using var deleted = await PdfVectorDocument.OpenAsync(after);
            Assert.Empty(PdfAnnotationReader.Read(deleted, 1));
        }
    }

    [Fact]
    public async Task AReplyWithAnUnknownName_IsWrittenWithoutIrt()
    {
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(10, 10, 20, 20), InReplyToName = "nobody" };
        var (doc, _) = await Write(Blank(), e => e.Add(reply));
        using (doc)
        {
            Assert.Equal(0, PdfAnnotationReader.Read(doc, 1).Single().InReplyTo);
            Assert.NotEqual(string.Empty, Appearance(doc, 0)); // a note, drawn as one
        }
    }

    [Fact]
    public async Task AFilesReply_ThatIsChanged_IsStillDrawnAsANote()
    {
        // A reader that treats every note alike (the desktop viewer) changes a reply from the file.
        var bytes = PageWith(
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /NM (q) >>",
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 4 0 R /C [1 0 0] >>");
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var reply = PdfAnnotationReader.Read(doc, 1)[1];
        reply.Contents = "Changed";
        using var changed = await PdfVectorDocument.OpenAsync(PdfAnnotationEditor.Apply(doc, bytes, e => e.Modify(reply)));
        Assert.Equal(4, PdfAnnotationReader.Read(changed, 1)[1].InReplyTo);
        Assert.Contains("1 0 0 rg", Appearance(changed, 1));
    }

    [Fact]
    public async Task DeletingAParent_WithoutItsReplies_MakesThemNotesOfTheirOwn()
    {
        var parent = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(50, 300, 20, 20), Name = "p1" };
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = parent.Rect, Name = "r1", InReplyToName = "p1", Contents = "Kept" };
        var (doc, bytes) = await Write(Blank(), e => { e.Add(parent); e.Add(reply); });
        using (doc)
        {
            Assert.Equal(string.Empty, Appearance(doc, 1));
            using var after = await PdfVectorDocument.OpenAsync(PdfAnnotationEditor.Apply(doc, bytes, e => e.Delete(1, 0, cascadeReplies: false)));
            var left = PdfAnnotationReader.Read(after, 1).Single();
            Assert.Equal(("Kept", 0), (left.Contents, left.InReplyTo));
            Assert.NotEqual(string.Empty, Appearance(after, 0));
        }
    }

    [Fact]
    public async Task AReplyToAParentWrittenDirectlyInAnnots_MakesTheParentAnObject()
    {
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 400 400] /Contents 3 0 R /Annots [<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /NM (direct) >>] >>");
        var original = b.Build();
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(10, 10, 20, 20), InReplyToName = "direct" };
        var (doc, _) = await Write(original, e => e.Add(reply));
        using (doc)
        {
            var read = PdfAnnotationReader.Read(doc, 1);
            Assert.True(read[0].ObjectNumber > 0);
            Assert.Equal(read[0].ObjectNumber, read[1].InReplyTo);
        }
    }

    [Fact]
    public void XfdfExport_NamesAParentWithoutNm_SoTheReplyKeepsItsThread()
    {
        var parent = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(5, 5, 20, 20), ObjectNumber = 7 };
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(5, 5, 20, 20), InReplyTo = 7 };
        var back = PdfAnnotationXfdf.Import(PdfAnnotationXfdf.Export([parent, reply]));
        Assert.Equal("obj-7", back[0].Name);
        Assert.Equal("obj-7", back[1].InReplyToName);
    }

    // ------------------------------------------------------------------ XFDF

    [Fact]
    public void Xfdf_KeepsLinesPolygonsStampsAndReplies()
    {
        var line = Line("OpenArrow", "Diamond");
        var polygon = Poly("Polygon", 40, 40, 200, 60, 150, 220);
        polygon.InteriorColor = [1, 1, 0];
        var polyline = Poly("PolyLine", 30, 30, 120, 200, 210, 60);
        polyline.LineEndings = ["Butt", "None"];
        var stamp = new PdfAnnotation { PageNumber = 1, Subtype = "Stamp", Icon = "Confidential", Rect = new PdfRect(10, 10, 150, 40), Name = "s1" };
        var parent = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(5, 5, 20, 20), Name = "p1", ObjectNumber = 12 };
        var reply = new PdfAnnotation { PageNumber = 1, Subtype = "Text", Rect = new PdfRect(5, 5, 20, 20), Name = "r1", InReplyTo = 12, Contents = "Yes" };

        string xfdf = PdfAnnotationXfdf.Export([line, polygon, polyline, stamp, parent, reply]);
        Assert.Contains("head=\"OpenArrow\"", xfdf);
        Assert.Contains("<vertices>40,40;200,60;150,220</vertices>", xfdf);
        Assert.Contains("inreplyto=\"p1\"", xfdf);

        var back = PdfAnnotationXfdf.Import(xfdf);
        Assert.Equal(new[] { "Line", "Polygon", "PolyLine", "Stamp", "Text", "Text" }, back.Select(a => a.Subtype));
        Assert.Equal(line.Line, back[0].Line);
        Assert.Equal(new[] { "OpenArrow", "Diamond" }, back[0].LineEndings);
        Assert.Equal(polygon.Vertices, back[1].Vertices);
        Assert.Equal(new double[] { 1, 1, 0 }, back[1].InteriorColor);
        Assert.Equal(new[] { "Butt", "None" }, back[2].LineEndings);
        Assert.Equal("Confidential", back[3].Icon);
        Assert.Null(back[4].InReplyToName);
        Assert.Equal("p1", back[5].InReplyToName);
        Assert.Null(back[5].ReplyType);
    }

    [Fact]
    public async Task XfdfReplies_AreLinkedWhenAdded()
    {
        var imported = PdfAnnotationXfdf.Import("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"q\"><contents>Q</contents></text>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"a\" inreplyto=\"q\"><contents>A</contents></text></annots></xfdf>");
        var (doc, _) = await Write(Blank(), e => { foreach (var a in imported) e.Add(a); });
        using (doc)
        {
            var read = PdfAnnotationReader.Read(doc, 1);
            Assert.Equal(read[0].ObjectNumber, read[1].InReplyTo);
        }
    }

    [Theory]
    [InlineData("<polygon page=\"0\" rect=\"0,0,9,9\"><vertices>1,1;NaN,2;3,3</vertices></polygon>")]
    [InlineData("<polygon page=\"0\" rect=\"0,0,9,9\"><vertices>1,1;Infinity,2;3,3</vertices></polygon>")]
    [InlineData("<polyline page=\"0\" rect=\"0,0,9,9\"><vertices>1,1</vertices></polyline>")]
    [InlineData("<polygon page=\"0\" rect=\"0,0,9,9\"></polygon>")]
    [InlineData("<line page=\"0\" rect=\"0,0,9,9\" start=\"NaN,1\" end=\"2,2\"/>")]
    public void Xfdf_SkipsShapesWithoutFinitePoints(string element)
    {
        var import = PdfAnnotationXfdf.ImportWithReport($"<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>{element}</annots></xfdf>");
        Assert.Empty(import.Annotations);
        Assert.Equal(1, import.Skipped);
    }

    [Fact]
    public void Xfdf_SkipsAPolygonOverTheVertexLimit_AndRefusesUnknownLineEndings()
    {
        string many = string.Join(";", Enumerable.Range(0, PdfAnnotationLimits.MaxVertices + 1).Select(i => $"{i % 400},{i % 300}"));
        var import = PdfAnnotationXfdf.ImportWithReport("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            $"<polygon page=\"0\" rect=\"0,0,400,400\"><vertices>{many}</vertices></polygon>" +
            "<line page=\"0\" rect=\"0,0,9,9\" start=\"1,1\" end=\"8,8\" head=\"Rocket\" tail=\"ClosedArrow\"/></annots></xfdf>");
        Assert.Equal(1, import.Skipped);
        Assert.Equal(new[] { "None", "ClosedArrow" }, import.Annotations.Single().LineEndings);
    }

    // ------------------------------------------------------------------ hostile files

    /// <summary>A page whose /Annots are the given annotation dictionaries, numbered from 4 (3 is the content).</summary>
    private static byte[] PageWith(params string[] annotations)
    {
        var b = new VectorPdfBuilder();
        // Objects: 3 content, 4.. annotations, then the page.
        int content = b.AddStream(string.Empty, string.Empty);
        Assert.Equal(3, content);
        var numbers = annotations.Select(a => b.Add(a)).ToList();
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 400 400] /Contents 3 0 R /Annots [{string.Join(" ", numbers.Select(n => $"{n} 0 R"))}] >>");
        return b.Build();
    }

    [Fact]
    public async Task AHugeVertexList_IsReadAndDrawnUpToTheLimit()
    {
        int count = PdfAnnotationLimits.MaxVertices * 3;
        var sb = new StringBuilder("<< /Type /Annot /Subtype /PolyLine /Rect [0 0 400 400] /C [0 0 0] /Vertices [");
        for (int i = 0; i < count; i++) sb.Append(i % 400).Append(' ').Append(i * 7 % 400).Append(' ');
        sb.Append("] >>");
        using var doc = await PdfVectorDocument.OpenAsync(PageWith(sb.ToString()));
        var a = PdfAnnotationReader.Read(doc, 1).Single();
        Assert.Equal(PdfAnnotationLimits.MaxVertices, a.Vertices.Count);

        var annots = (PdfArray)doc.Resolver.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        var form = PdfAnnotationAppearances.Generate((PdfDictionary)doc.Resolver.Resolve(annots[0])!, doc.Resolver, _ => 1000);
        Assert.NotNull(form);
        using var z = new ZLibStream(new MemoryStream(form!.GetRawBytes().ToArray()), CompressionMode.Decompress);
        using var reader = new StreamReader(z, Encoding.Latin1);
        int segments = reader.ReadToEnd().Split('\n').Count(l => l.EndsWith(" l"));
        Assert.Equal(PdfAnnotationLimits.MaxVertices - 1, segments);
    }

    [Fact]
    public async Task NumbersThatAreNotFinite_GiveNoShape()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Blank());
        var resolver = doc.Resolver;
        PdfArray A(params double[] v) => new(v.Select(x => (PdfObject)new PdfReal(x)).ToList());
        PdfDictionary Annot(string subtype, string key, PdfArray value) => new(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName(subtype), ["Rect"] = A(0, 0, 100, 100), [key] = value,
        });
        Assert.Null(PdfAnnotationAppearances.Generate(Annot("Line", "L", A(0, 0, double.NaN, 50)), resolver, _ => 1));
        Assert.Null(PdfAnnotationAppearances.Generate(Annot("Polygon", "Vertices", A(0, 0, 50, double.PositiveInfinity, 9, 9)), resolver, _ => 1));

        var line = PdfAnnotationReader.FromDictionary(Annot("Line", "L", A(0, 0, double.NaN, 50)), resolver);
        Assert.Null(line.Line);
        var polygon = PdfAnnotationReader.FromDictionary(Annot("Polygon", "Vertices", A(0, 0, 50, double.NaN, 9, 9)), resolver);
        Assert.Empty(polygon.Vertices);
    }

    [Fact]
    public async Task IrtChains_ThatLoop_OrGoTooDeep_BelongToNoThread()
    {
        // 4 and 5 answer each other; 6 is a note; 7.. is a chain of replies down from 6, longer than the limit.
        int depth = PdfAnnotationLimits.MaxReplyDepth + 10;
        var annots = new List<string>
        {
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /IRT 5 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /IRT 4 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] >>",
        };
        for (int i = 0; i < depth; i++) annots.Add($"<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /IRT {6 + i} 0 R >>");
        var bytes = PageWith(annots.ToArray());
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var read = PdfAnnotationReader.Read(doc, 1);
        var roots = PdfAnnotationThreads.Roots(read);
        Assert.False(roots.ContainsKey(4));
        Assert.False(roots.ContainsKey(5));
        Assert.Equal(PdfAnnotationLimits.MaxReplyDepth, roots.Count);
        Assert.All(roots.Values, root => Assert.Equal(6, root));
        Assert.True(roots.ContainsKey(6 + PdfAnnotationLimits.MaxReplyDepth));
        Assert.False(roots.ContainsKey(7 + PdfAnnotationLimits.MaxReplyDepth));

        // Deleting a reply of the loop ends, and takes the other one with it.
        var afterLoop = PdfAnnotationEditor.Apply(doc, bytes, e => e.Delete(1, 0));
        using (var loop = await PdfVectorDocument.OpenAsync(afterLoop))
            Assert.Equal(1 + depth, PdfAnnotationReader.Read(loop, 1).Count);

        // Deleting the note takes its whole chain, however long, without recursion.
        var afterChain = PdfAnnotationEditor.Apply(doc, bytes, e => e.Delete(1, 2));
        using var chain = await PdfVectorDocument.OpenAsync(afterChain);
        Assert.Equal(2, PdfAnnotationReader.Read(chain, 1).Count);
    }

    [Fact]
    public async Task ALongIrtChain_IsDeletedWithoutOverflowingTheStack()
    {
        var annots = new List<string> { "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] >>" };
        for (int i = 0; i < 3000; i++) annots.Add($"<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /IRT {4 + i} 0 R >>");
        var bytes = PageWith(annots.ToArray());
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var after = PdfAnnotationEditor.Apply(doc, bytes, e => e.Delete(1, 0));
        using var deleted = await PdfVectorDocument.OpenAsync(after);
        Assert.Empty(PdfAnnotationReader.Read(deleted, 1));
    }
}
