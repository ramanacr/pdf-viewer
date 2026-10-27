using System.Buffers;
using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The web viewer's lines, polygons, polylines, stamps and replies: their JSON, the store's
/// checks (vertex limits, finite numbers, line ending and stamp names, what a reply answers and
/// how deep a thread goes), the PDF they are written as and read back from, and XFDF.
/// </summary>
public class EmbedAnnotationTypesTests
{
    private const string Envelope = "\"protocol\":\"pdfviewer-embed\",\"version\":1";

    private static EmbedCall Call(string method, string args) =>
        Assert.IsType<EmbedCall>(EmbedProtocol.Parse("{" + Envelope + $",\"type\":\"call\",\"requestId\":9,\"document\":1,\"method\":\"{method}\",\"args\":{args}" + "}", EmbedMode.FullWindow));

    private static void Refused(string args) =>
        Assert.Equal(EmbedErrors.InvalidArgument, Assert.Throws<EmbedProtocolException>(() => Call("addAnnotations", "[" + args + "]")).Code);

    private static byte[] OnePage()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static string Shape(string id, string subtype, string extra) =>
        $"{{\"id\":\"{id}\",\"type\":\"Annotation\",\"bodyValue\":\"\",\"target\":{{\"source\":\"x\",\"selector\":{{\"node\":{{\"index\":0}},\"subtype\":\"{subtype}\"{extra}}}}},\"creator\":{{\"type\":\"Person\",\"name\":\"Ada\"}}}}";

    private static string Reply(string id, string parent, string text) =>
        $"{{\"id\":\"{id}\",\"type\":\"Annotation\",\"motivation\":\"replying\",\"bodyValue\":\"{text}\",\"target\":\"{parent}\",\"creator\":{{\"type\":\"Person\",\"name\":\"Bo\"}}}}";

    private static string NewTypes() => "[" + string.Join(",",
        Shape("l1", "line", ",\"vertices\":[100,100,300,200],\"lineEndings\":[\"None\",\"ClosedArrow\"],\"strokeWidth\":2,\"strokeColor\":\"#2563EB\",\"fillColor\":\"#2563EB\""),
        Shape("pg1", "polygon", ",\"vertices\":[100,400,250,420,180,560],\"fillColor\":\"#FFFF00\""),
        Shape("pl1", "polyline", ",\"vertices\":[300,400,350,500,420,430,500,520],\"lineEndings\":[\"Butt\",\"OpenArrow\"]"),
        Shape("st1", "stamp", ",\"stampName\":\"Confidential\",\"boundingBox\":[350,650,530,690]"),
        Shape("n1", "note", ",\"boundingBox\":[50,700,70,720]"),
        Reply("re1", "n1", "First answer"),
        Reply("re2", "re1", "Second answer")) + "]";

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public async Task NewTypes_AreWrittenAsRealAnnotations_AndReadBackTheSame()
    {
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        var added = store.Add(Call("addAnnotations", "[" + NewTypes() + "]").Annotations);
        Assert.Equal(7, added.Count);
        // A reply takes the page and box of what it answers.
        Assert.Equal(added[4].Box, added[5].Box);
        // A line's box is grown around its points to hold the arrow.
        Assert.True(added[0].Box.X2 > 300 && added[0].Box.Y2 > 200);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, []);

        using (var vector = await PdfVectorDocument.OpenAsync(doc.Bytes))
        {
            var read = PdfAnnotationReader.Read(vector, 1);
            Assert.Equal(new[] { "Line", "Polygon", "PolyLine", "Stamp", "Text", "Text", "Text" }, read.Select(a => a.Subtype));
            Assert.All(read, a => Assert.True(a.HasAppearance));
            Assert.Equal(read[4].ObjectNumber, read[5].InReplyTo);
            Assert.Equal(read[5].ObjectNumber, read[6].InReplyTo);
            Assert.Equal("Confidential", read[3].Icon);
        }

        await using var reopened = await ViewerDocument.OpenAsync(doc.Bytes);
        var back = await reopened.ReadAnnotationsAsync();
        Assert.Equal(new[] { "l1", "pg1", "pl1", "st1", "n1", "re1", "re2" }, back.Select(a => a.Id));
        Assert.Equal(new double[] { 100, 100, 300, 200 }, back[0].Vertices);
        Assert.Equal(("None", "ClosedArrow"), (back[0].StartEnding, back[0].EndEnding));
        Assert.Equal("#2563EB", back[0].Color);
        Assert.Equal(6, back[1].Vertices.Count);
        Assert.Equal("#FFFF00", back[1].FillColor);
        Assert.Equal(("Butt", "OpenArrow"), (back[2].StartEnding, back[2].EndEnding));
        Assert.Equal("Confidential", back[3].StampName);
        Assert.Equal(ViewerAnnotationKind.Reply, back[5].Kind);
        Assert.Equal("n1", back[5].InReplyTo);
        Assert.Equal("re1", back[6].InReplyTo);
        Assert.Equal("Second answer", back[6].Contents);
        Assert.Equal("Bo", back[6].Author);

        // The JSON: a shape's vertices and endings, a reply's motivation and target.
        using var json = JsonDocument.Parse(Json(EmbedAnnotationJson.List(back, "x.pdf")));
        var line = json.RootElement[0].GetProperty("target").GetProperty("selector");
        Assert.Equal(4, line.GetProperty("vertices").GetArrayLength());
        Assert.Equal("ClosedArrow", line.GetProperty("lineEndings")[1].GetString());
        Assert.Equal("Confidential", json.RootElement[3].GetProperty("target").GetProperty("selector").GetProperty("stampName").GetString());
        var reply = json.RootElement[5];
        Assert.Equal("replying", reply.GetProperty("motivation").GetString());
        Assert.Equal("n1", reply.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Replies_FollowWhatTheyAnswer_AndGoWithIt()
    {
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add(Call("addAnnotations", "[" + NewTypes() + "]").Annotations);
        Assert.Throws<ViewerAnnotationException>(() => store.Add(Call("addAnnotations", "[[" + Reply("x", "missing", "?") + "]]").Annotations));
        Assert.Throws<ViewerAnnotationException>(() => store.Update(store.Find("re1")! with { InReplyTo = "l1" }));

        // Moving the note moves nothing else; its replies keep its page and are removed with it.
        var moved = store.Move("n1", 20, -20, DateTimeOffset.UtcNow);
        Assert.Equal(new ViewerBox(50, 700, 70, 720), store.Find("re1")!.Box);
        var removed = store.Delete(new ViewerAnnotationFilter(new HashSet<string> { "n1" }));
        Assert.Equal(new[] { "n1", "re1", "re2" }, removed.Select(a => a.Id));
        Assert.Null(store.Find("re2"));
        Assert.True(store.Undo());
        Assert.NotNull(store.Find("re2"));
        Assert.Equal(moved.Box, store.Find("n1")!.Box);
    }

    [Fact]
    public async Task AThread_IsAtMostTheDepthLimit()
    {
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        var list = new List<string> { Shape("root", "note", ",\"boundingBox\":[50,700,70,720]") };
        for (int i = 1; i <= ViewerAnnotationStore.MaxReplyDepth; i++) list.Add(Reply($"r{i}", i == 1 ? "root" : $"r{i - 1}", "x"));
        store.Add(Call("addAnnotations", "[[" + string.Join(",", list) + "]]").Annotations);
        var deeper = Call("addAnnotations", "[[" + Reply("too-deep", $"r{ViewerAnnotationStore.MaxReplyDepth}", "x") + "]]").Annotations;
        Assert.Throws<ViewerAnnotationException>(() => store.Add(deeper));
    }

    [Fact]
    public void HostileShapes_AreRefused()
    {
        // NaN and infinities cannot be written in JSON; numbers past ±10⁶ and too many points are refused.
        Refused("[" + Shape("a", "line", ",\"vertices\":[1,2,3]") + "]");
        Refused("[" + Shape("a", "polygon", ",\"vertices\":[1,2,3,1e9,5,6]") + "]");
        string many = string.Join(",", Enumerable.Repeat("10", 2 * ViewerAnnotationStore.MaxVertices + 2));
        Refused("[" + Shape("a", "polyline", $",\"vertices\":[{many}]") + "]");
        Refused("[" + Shape("a", "line", ",\"vertices\":[1,2,3,4],\"lineEndings\":[\"Rocket\"]") + "]");
        Refused("[" + Shape("a", "stamp", ",\"stampName\":\"<script>\",\"boundingBox\":[1,1,50,20]") + "]");
        Refused("[" + Shape("a", "polygon", "") + "]");
        Refused("[{\"id\":\"r\",\"type\":\"Annotation\",\"motivation\":\"replying\",\"target\":{\"source\":\"x\"}}]");
    }

    [Fact]
    public async Task TheStore_RefusesPointsThatAreNotFinite_OrOffThePage()
    {
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        ViewerAnnotation Line(params double[] v) => new() { Id = "", Page = 1, Kind = ViewerAnnotationKind.Line, Box = new ViewerBox(0, 0, 600, 700), Vertices = v };
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Line(10, 10, double.NaN, 20)]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Line(10, 10, double.PositiveInfinity, 20)]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Line(10, 10, 5000, 20)]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Line(10, 10, 20, 20) with { StartEnding = "Rocket" }]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Line(10, 10, 20, 20) with { Kind = ViewerAnnotationKind.Polygon }]));
        Assert.Single(store.Add([Line(10, 10, 20, 20)]));
    }

    [Fact]
    public async Task AFilesReplies_AreManaged_ButNotLoopsOrGroups()
    {
        // 4 a note, 5 its reply, 6 and 7 replies to each other, 8 a group member of 4.
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        b.Add("<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /NM (q) /Contents (Q) >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 4 0 R /Contents (A) >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 7 0 R >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 6 0 R >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 4 0 R /RT /Group >>");
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Annots [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R] >>");
        await using var doc = await ViewerDocument.OpenAsync(b.Build());
        var read = await doc.ReadAnnotationsAsync();
        Assert.Equal(2, read.Count);
        Assert.Equal(ViewerAnnotationKind.Note, read[0].Kind);
        Assert.Equal("q", read[1].InReplyTo);
        Assert.Equal("A", read[1].Contents);
    }

    [Fact]
    public async Task Xfdf_KeepsTheNewTypes_AndTheirThreads()
    {
        await using var doc = await ViewerDocument.OpenAsync(OnePage());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        var added = store.Add(Call("addAnnotations", "[" + NewTypes() + "]").Annotations);
        string xfdf = PdfAnnotationXfdf.Export(added.Select(a => ViewerAnnotationPdf.ToPdf(a, null)));
        var imported = PdfAnnotationXfdf.Import(xfdf);
        var back = imported.Select(p => ViewerAnnotationPdf.FromPdf(p, "", p.InReplyToName)!).ToList();
        Assert.Equal(added.Select(a => a.Kind), back.Select(a => a.Kind));
        Assert.Equal(added.Select(a => a.Id), back.Select(a => a.Id));
        Assert.Equal(added[0].Vertices, back[0].Vertices);
        Assert.Equal(added[2].Vertices, back[2].Vertices);
        Assert.Equal((added[2].StartEnding, added[2].EndEnding), (back[2].StartEnding, back[2].EndEnding));
        Assert.Equal("Confidential", back[3].StampName);
        Assert.Equal("re1", back[6].InReplyTo);
    }
}
