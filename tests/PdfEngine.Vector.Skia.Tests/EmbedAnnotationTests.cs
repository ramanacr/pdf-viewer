using System.Buffers;
using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Phase 2 of the web embed SDK: the annotation manager's JSON and its validation, the
/// annotations written by the engine and read back, XFDF, form filling and the SAVE_API messages.
/// </summary>
public class EmbedAnnotationTests
{
    private const string Envelope = "\"protocol\":\"pdfviewer-embed\",\"version\":1";

    private static EmbedCall Call(string method, string args) =>
        Assert.IsType<EmbedCall>(EmbedProtocol.Parse("{" + Envelope + $",\"type\":\"call\",\"requestId\":9,\"document\":1,\"method\":\"{method}\",\"args\":{args}" + "}", EmbedMode.FullWindow));

    private static EmbedProtocolException Refused(string method, string args)
    {
        var ex = Assert.Throws<EmbedProtocolException>(() => Call(method, args));
        Assert.Equal(EmbedErrors.InvalidArgument, ex.Code);
        Assert.Equal(9, ex.RequestId);
        return ex;
    }

    private static byte[] TwoPages()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static string Sel(string subtype, string extra, int page = 0) =>
        $"{{\"node\":{{\"index\":{page}}},\"subtype\":\"{subtype}\"{extra}}}";

    private static string Annot(string id, string selector, string body = "") =>
        $"{{\"id\":\"{id}\",\"type\":\"Annotation\",\"bodyValue\":\"{body}\",\"target\":{{\"source\":\"x\",\"selector\":{selector}}},\"creator\":{{\"type\":\"Person\",\"name\":\"Ada\"}},\"created\":\"2026-10-26T10:00:00Z\"}}";

    /// <summary>One of each kind, as a host page would add them.</summary>
    private static string EveryKind() => "[" + string.Join(",",
        Annot("h1", Sel("highlight", ",\"quadPoints\":[72,720,200,720,72,700,200,700],\"strokeColor\":\"#FFFF00\",\"opacity\":0.5"), "Look"),
        Annot("u1", Sel("underline", ",\"quadPoints\":[72,690,200,690,72,670,200,670]")),
        Annot("s1", Sel("strikeout", ",\"quadPoints\":[72,660,200,660,72,640,200,640]")),
        Annot("n1", Sel("note", ",\"boundingBox\":[300,700,320,720]"), "A note"),
        Annot("f1", Sel("freetext", ",\"boundingBox\":[72,500,272,540],\"strokeColor\":\"#0000FF\",\"fontSize\":14,\"strokeWidth\":0"), "Free text"),
        Annot("i1", Sel("ink", ",\"inkList\":[[100,400,150,450,200,400]],\"strokeWidth\":3,\"strokeColor\":\"#E5484D\""), ""),
        Annot("r1", Sel("square", ",\"boundingBox\":[300,300,400,360],\"fillColor\":\"#CCE5FF\"", page: 1)),
        Annot("c1", Sel("circle", ",\"boundingBox\":[420,300,520,360]", page: 1))) + "]";

    private static async Task<ViewerDocument> Open(byte[] bytes) => await ViewerDocument.OpenAsync(bytes);

    private static ViewerAnnotationStore Store(ViewerDocument doc, IReadOnlyList<ViewerAnnotation>? fromFile = null) =>
        new(doc.Geometry, fromFile ?? []);

    [Fact]
    public async Task Annotations_FromTheJson_AreSavedAsRealAnnotations_AndReadBackTheSame()
    {
        await using var doc = await Open(TwoPages());
        var store = Store(doc);
        var added = store.Add(Call("addAnnotations", "[" + EveryKind() + "]").Annotations);
        Assert.Equal(8, added.Count);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, []);
        Assert.True(doc.IsEdited);
        byte[] saved = doc.Bytes;
        // An incremental update: the file as opened is the start of the saved one.
        Assert.True(saved.AsSpan(0, doc.OriginalBytes.Length).SequenceEqual(doc.OriginalBytes));

        using (var vector = await PdfVectorDocument.OpenAsync(saved))
        {
            var page1 = PdfAnnotationReader.Read(vector, 1);
            Assert.Equal(new[] { "Highlight", "Underline", "StrikeOut", "Text", "FreeText", "Ink" }, page1.Select(a => a.Subtype));
            Assert.All(page1, a => Assert.True(a.HasAppearance));
            Assert.Equal(new[] { "Square", "Circle" }, PdfAnnotationReader.Read(vector, 2).Select(a => a.Subtype));
        }

        await using var reopened = await Open(saved);
        var back = await reopened.ReadAnnotationsAsync();
        Assert.Equal(new[] { "h1", "u1", "s1", "n1", "f1", "i1", "r1", "c1" }, back.Select(a => a.Id));
        foreach (var (a, b) in added.Zip(back))
        {
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Page, b.Page);
            Assert.Equal(a.Contents, b.Contents);
            Assert.Equal("Ada", b.Author);
            Assert.Equal(a.Color, b.Color);
            Assert.Equal(a.Box.X1, b.Box.X1, 2);
            Assert.Equal(a.Box.Y2, b.Box.Y2, 2);
            Assert.Equal(a.QuadPoints.Count, b.QuadPoints.Count);
            Assert.Equal(a.Created, b.Created);
        }
        Assert.Equal(0.5, back[0].Opacity, 3);
        Assert.Equal(14, back[4].FontSize);
        Assert.Equal(new double[] { 100, 400, 150, 450, 200, 400 }, back[5].InkList.Single());
        Assert.Equal("#CCE5FF", back[6].FillColor);

        // The JSON the host page gets back has the shape it sent.
        string json = Json(EmbedAnnotationJson.List(back.Take(1), "report.pdf"));
        using var parsed = JsonDocument.Parse(json);
        var first = parsed.RootElement[0];
        Assert.Equal("h1", first.GetProperty("id").GetString());
        Assert.Equal("highlighting", first.GetProperty("motivation").GetString());
        Assert.Equal(0, first.GetProperty("target").GetProperty("selector").GetProperty("node").GetProperty("index").GetInt32());
        Assert.Equal(8, first.GetProperty("target").GetProperty("selector").GetProperty("quadPoints").GetArrayLength());
        Assert.Equal("2026-10-26T10:00:00Z", first.GetProperty("created").GetString());
    }

    [Fact]
    public async Task TheFilesAnnotations_CanBeMovedChangedAndDeleted_AndUndone()
    {
        byte[] withAnnotations;
        await using (var doc = await Open(TwoPages()))
        {
            var store = Store(doc);
            store.Add(Call("addAnnotations", "[" + EveryKind() + "]").Annotations);
            await doc.ApplyEditsAsync(store.FromFile, store.Current, []);
            withAnnotations = doc.Bytes;
        }

        await using var second = await Open(withAnnotations);
        var fromFile = await second.ReadAnnotationsAsync();
        var s = Store(second, fromFile);
        Assert.False(s.IsChanged);
        s.Delete(new ViewerAnnotationFilter(new HashSet<string> { "u1", "c1" }));
        var moved = s.Move("n1", 10, -20, DateTimeOffset.UtcNow);
        s.Update(s.Find("f1")! with { Contents = "Changed" });
        Assert.True(s.IsChanged);
        await second.ApplyEditsAsync(s.FromFile, s.Current, []);

        await using var third = await Open(second.Bytes);
        var now = await third.ReadAnnotationsAsync();
        Assert.Equal(new[] { "h1", "s1", "n1", "f1", "i1", "r1" }, now.Select(a => a.Id));
        Assert.Equal(moved.Box.X1, now.Single(a => a.Id == "n1").Box.X1, 2);
        Assert.Equal(310, moved.Box.X1, 2);
        Assert.Equal("Changed", now.Single(a => a.Id == "f1").Contents);

        // Undo, step by step, back to the file.
        Assert.True(s.Undo());
        Assert.True(s.Undo());
        Assert.True(s.Undo());
        Assert.False(s.CanUndo);
        Assert.False(s.IsChanged);
        await second.ApplyEditsAsync(s.FromFile, s.Current, []);
        Assert.False(second.IsEdited);
        Assert.True(s.Redo());
        Assert.Equal(6, s.Current.Count);
    }

    [Fact]
    public async Task TheStore_RefusesAnnotationsOffThePage()
    {
        await using var doc = await Open(TwoPages());
        var store = Store(doc);
        ViewerAnnotation Note(int page, double x) => new() { Id = "", Page = page, Kind = ViewerAnnotationKind.Note, Box = new ViewerBox(x, 10, x + 20, 30) };
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Note(3, 10)]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Note(1, 700)]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Note(1, -50)]));
        var ok = store.Add([Note(1, 10)]).Single();
        Assert.NotEmpty(ok.Id);
        Assert.Throws<ViewerAnnotationException>(() => store.Add([Note(1, 10) with { Id = ok.Id }]));
        Assert.Throws<ViewerAnnotationException>(() => store.Update(ok with { Kind = ViewerAnnotationKind.Square }));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([new ViewerAnnotation
        {
            Id = "q", Page = 1, Kind = ViewerAnnotationKind.Highlight, Box = new ViewerBox(0, 0, 10, 10), QuadPoints = [1, 2, 3, 4, 5, 6, 7, 9000],
        }]));
        // Moving keeps the annotation on its page.
        var moved = store.Move(ok.Id, -500, 5000, DateTimeOffset.UtcNow);
        Assert.Equal(0, moved.Box.X1, 6);
        Assert.Equal(792, moved.Box.Y2, 6);
    }

    [Theory]
    [InlineData("addAnnotations", "[[{}]]")]
    [InlineData("addAnnotations", "[[]]")]
    [InlineData("addAnnotations", "[[\"x\"]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"caret\",\"boundingBox\":[0,0,1,1]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":-1},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1e9]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,\"1\"]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1],\"strokeColor\":\"red\"}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1],\"opacity\":2}}}]]")]
    [InlineData("addAnnotations", "[[{\"bodyValue\":5,\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1]}}}]]")]
    [InlineData("addAnnotations", "[[{\"created\":\"yesterday\",\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"highlight\",\"quadPoints\":[1,2,3]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"ink\",\"inkList\":[[1]]}}}]]")]
    [InlineData("addAnnotations", "[[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"ink\",\"inkList\":[]}}}]]")]
    [InlineData("addAnnotations", "[{}]")]
    [InlineData("updateAnnotation", "[{\"target\":{\"selector\":{\"node\":{\"index\":0},\"subtype\":\"note\",\"boundingBox\":[0,0,1,1]}}}]")]
    [InlineData("getAnnotations", "[{\"annotationIds\":\"a\"}]")]
    [InlineData("getAnnotations", "[{\"annotationIds\":[\"\"]}]")]
    [InlineData("getAnnotations", "[{\"pageRange\":{\"startPage\":0}}]")]
    [InlineData("getAnnotations", "[{\"pageRange\":{\"startPage\":3,\"endPage\":2}}]")]
    [InlineData("getAnnotations", "[7]")]
    [InlineData("deleteAnnotations", "[]")]
    [InlineData("selectAnnotation", "[]")]
    [InlineData("selectAnnotation", "[\"\"]")]
    [InlineData("setAnnotationConfig", "[{\"showToolbar\":\"yes\"}]")]
    [InlineData("setAnnotationConfig", "[{\"authorName\":7}]")]
    [InlineData("setAnnotationConfig", "[null]")]
    [InlineData("importXfdf", "[5]")]
    [InlineData("importXfdf", "[\"\"]")]
    [InlineData("setSaveOptions", "[{\"autoSaveFrequency\":-1}]")]
    [InlineData("setSaveOptions", "[{\"enableFocusPolling\":1}]")]
    [InlineData("setSaveOptions", "[]")]
    [InlineData("saveResult", "[1,\"OK\"]")]
    [InlineData("saveResult", "[0,\"SUCCESS\"]")]
    [InlineData("saveResult", "[1,\"SUCCESS\",5]")]
    public void MalformedAnnotationCalls_AreRefused(string method, string args) => Refused(method, args);

    [Fact]
    public void Counts_AreCapped()
    {
        string note = Annot("", Sel("note", ",\"boundingBox\":[0,0,10,10]"));
        Assert.Equal(ViewerAnnotationStore.MaxPerCall, Call("addAnnotations", "[[" + string.Join(",", Enumerable.Repeat(note, ViewerAnnotationStore.MaxPerCall)) + "]]").Annotations.Count);
        Refused("addAnnotations", "[[" + string.Join(",", Enumerable.Repeat(note, ViewerAnnotationStore.MaxPerCall + 1)) + "]]");

        string Ink(int points) => Annot("i", Sel("ink", ",\"inkList\":[[" + string.Join(",", Enumerable.Repeat("1,2", points)) + "]]"));
        Assert.Single(Call("addAnnotations", "[[" + Ink(ViewerAnnotationStore.MaxInkPointsPerStroke) + "]]").Annotations);
        Refused("addAnnotations", "[[" + Ink(ViewerAnnotationStore.MaxInkPointsPerStroke + 1) + "]]");

        string longText = new('x', ViewerAnnotationStore.MaxContentsLength + 1);
        Refused("addAnnotations", "[[" + Annot("t", Sel("note", ",\"boundingBox\":[0,0,10,10]"), longText) + "]]");
        Refused("addAnnotations", "[[" + Annot(new string('i', ViewerAnnotationStore.MaxIdLength + 1), Sel("note", ",\"boundingBox\":[0,0,10,10]")) + "]]");
    }

    [Fact]
    public void WellFormedCalls_CarryTheirArguments()
    {
        var get = Call("getAnnotations", "[{\"annotationIds\":[\"a\",\"b\"],\"pageRange\":{\"startPage\":1,\"endPage\":2}}]");
        Assert.Equal(EmbedMethod.GetAnnotations, get.Method);
        Assert.True(get.Filter!.Ids!.SetEquals(["a", "b"]));
        Assert.Equal((1, 2), (get.Filter.StartPage, get.Filter.EndPage));
        Assert.Same(ViewerAnnotationFilter.All, Call("getAnnotations", "[]").Filter);
        Assert.Same(ViewerAnnotationFilter.All, Call("deleteAnnotations", "[{}]").Filter);
        Assert.Equal("a", Call("selectAnnotation", "[\"a\"]").AnnotationId);
        Assert.Equal(new EmbedAnnotationConfig(false, true, null, true, "Ada"),
            Call("setAnnotationConfig", "[{\"showToolbar\":false,\"showCommentsPanel\":true,\"printWithAnnotations\":true,\"authorName\":\"Ada\",\"other\":1}]").AnnotationConfig);
        Assert.Equal(new EmbedSaveOptions(true, 30, true, false),
            Call("setSaveOptions", "[{\"autoSaveFrequency\":30,\"enableFocusPolling\":true,\"showSaveButton\":false}]").SaveOptions);
        var result = Call("saveResult", "[4,\"SUCCESS\",\"renamed.pdf\"]");
        Assert.Equal((4, true, "renamed.pdf"), (result.SaveId, result.Saved, result.SavedFileName));
        Assert.False(Call("saveResult", "[4,\"FAIL\"]").Saved);
        Assert.Equal(EmbedMethod.ExportXfdf, Call("exportXfdf", "[]").Method);
        Assert.Equal("<xfdf/>", Call("importXfdf", "[\"<xfdf/>\"]").Xfdf);

        var update = Call("updateAnnotation", "[" + Annot("n1", Sel("note", ",\"boundingBox\":[10,10,30,30]"), "x") + "]");
        Assert.Equal("n1", update.Annotations.Single().Id);
    }

    [Fact]
    public void TheOpenMessage_CarriesTheSaveOptionsAndTheAnnotationConfig()
    {
        var open = Assert.IsType<EmbedOpen>(EmbedProtocol.Parse("{" + Envelope +
            ",\"type\":\"open\",\"requestId\":2,\"config\":{\"enableAnnotationAPIs\":true,\"includePDFAnnotations\":true,\"downloadWithAnnotations\":true}," +
            "\"save\":{\"enabled\":true,\"autoSaveFrequency\":5}}", EmbedMode.FullWindow));
        Assert.Equal(new EmbedSaveOptions(true, 5, false, true), open.Save);
        Assert.True(open.Config.EnableAnnotationApis);
        Assert.True(open.Config.IncludePdfAnnotations);
        Assert.True(open.Config.DownloadWithAnnotations);
        Assert.False(open.Config.PrintWithAnnotations);
        Assert.True(open.Config.EnableFormFilling);
        Assert.True(open.Config.HasAnnotationTools);

        var sized = Assert.IsType<EmbedOpen>(EmbedProtocol.Parse("{" + Envelope + ",\"type\":\"open\",\"requestId\":2}", EmbedMode.SizedContainer));
        Assert.False(sized.Config.EnableAnnotationApis);
        Assert.False(sized.Config.EnableFormFilling);
        Assert.False(sized.Config.HasAnnotationTools);
        Assert.Equal(EmbedSaveOptions.None, sized.Save);

        var ex = Assert.Throws<EmbedProtocolException>(() => EmbedProtocol.Parse("{" + Envelope + ",\"type\":\"open\",\"requestId\":2,\"save\":{\"autoSaveFrequency\":\"1\"}}", EmbedMode.FullWindow));
        Assert.Equal(2, ex.RequestId);
        Assert.Throws<EmbedProtocolException>(() => EmbedProtocol.Parse("{" + Envelope + ",\"type\":\"open\",\"requestId\":2,\"config\":{\"enableFormFilling\":\"no\"}}", EmbedMode.FullWindow));
    }

    [Fact]
    public void TheSaveRequest_NamesTheFile()
    {
        using var doc = JsonDocument.Parse(EmbedProtocol.SaveRequest(3, "a.pdf", null, "button"));
        var r = doc.RootElement;
        Assert.Equal("save", r.GetProperty("type").GetString());
        Assert.Equal(3, r.GetProperty("saveId").GetInt32());
        Assert.Equal("a.pdf", r.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("fileId").ValueKind);
    }

    [Fact]
    public async Task Xfdf_RoundTripsTheAnnotations()
    {
        await using var doc = await Open(TwoPages());
        var store = Store(doc);
        var added = store.Add(Call("addAnnotations", "[" + EveryKind() + "]").Annotations);
        string xfdf = PdfAnnotationXfdf.Export(added.Select(a => ViewerAnnotationPdf.ToPdf(a, null)), "report.pdf");
        Assert.Contains("<highlight", xfdf);
        Assert.Contains("<gesture>", xfdf);

        var imported = PdfAnnotationXfdf.Import(xfdf);
        Assert.Equal(8, imported.Count);
        var back = imported.Select(p => ViewerAnnotationPdf.FromPdf(p, "x")!).ToList();
        Assert.Equal(added.Select(a => a.Id), back.Select(a => a.Id));
        foreach (var (a, b) in added.Zip(back))
        {
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Page, b.Page);
            Assert.Equal(a.Contents, b.Contents);
            Assert.Equal(a.Color, b.Color);
            Assert.Equal(a.Author, b.Author);
            Assert.Equal(a.Box.X2, b.Box.X2, 3);
            Assert.Equal(a.QuadPoints, b.QuadPoints);
            Assert.Equal(a.InkList.SelectMany(s => s), b.InkList.SelectMany(s => s));
            Assert.Equal(a.Created, b.Created);
        }
        // Imported annotations write as real ones too.
        var second = Store(doc);
        second.Add(back.Select(a => a with { Id = "" }).ToList());
        await doc.ApplyEditsAsync(second.FromFile, second.Current, []);
        using var vector = await PdfVectorDocument.OpenAsync(doc.Bytes);
        Assert.Equal(6, PdfAnnotationReader.Read(vector, 1).Count);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<html/>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><xfdf><annots/></xfdf>")]
    public void Xfdf_RefusesWhatIsNotXfdf(string xml) => Assert.Throws<FormatException>(() => PdfAnnotationXfdf.Import(xml));

    [Fact]
    public void Xfdf_SkipsUnknownAndBrokenElements()
    {
        var list = PdfAnnotationXfdf.Import("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<caret page=\"0\" rect=\"0,0,10,10\"/><text page=\"x\" rect=\"0,0,1,1\"/><text page=\"0\" rect=\"NaN,0,1,1\"/>" +
            "<square page=\"1\" rect=\"10,20,30,40\" color=\"#FF0000\" width=\"2\"><contents>Box</contents></square></annots></xfdf>");
        var a = Assert.Single(list);
        Assert.Equal(("Square", 2, "Box", 2.0), (a.Subtype, a.PageNumber, a.Contents, a.BorderWidth));
    }

    [Fact]
    public async Task FormValues_AreWritten_AndKeptBySaving()
    {
        await using var doc = await Open(FormPdfFixture.Build());
        var widgets = await doc.GetFormWidgetsAsync();
        Assert.Contains(widgets, w => w.Name == "person.name" && w.Kind == ViewerFormKind.Text);
        var agree = widgets.Single(w => w.Name == "agree");
        Assert.False(agree.IsOn);
        Assert.Equal(2, widgets.Count(w => w.Name == "colour"));
        var blue = widgets.Single(w => w.Name == "colour" && w.OnState == "Blue");

        var values = new List<(string, IReadOnlyList<string>)>
        {
            ("person.name", ["Ada Lovelace"]),
            ("agree", [agree.ToggleValue]),
            ("colour", [blue.ToggleValue]),
            ("city", ["ROM"]),
        };
        await doc.ApplyEditsAsync([], [], values);
        var after = await doc.GetFormWidgetsAsync();
        Assert.Equal("Ada Lovelace", after.First(w => w.Name == "person.name").Value);
        Assert.True(after.Single(w => w.Name == "agree").IsOn);
        Assert.True(after.Single(w => w.Name == "colour" && w.OnState == "Blue").IsOn);

        using var vector = await PdfVectorDocument.OpenAsync(doc.Bytes);
        var form = PdfAcroForm.Read(vector)!;
        Assert.Equal("Ada Lovelace", form["person.name"]!.Value);
        Assert.True(form["agree"]!.IsChecked);
        Assert.Equal("Blue", form["colour"]!.Value);
        Assert.Equal("ROM", form["city"]!.Value);
    }

    [Fact]
    public async Task FormCalculations_RunWithoutJavaScript_AndBadValuesAreRefused()
    {
        await using var doc = await Open(ScriptedFormFixture.Build());
        await doc.ApplyEditsAsync([], [], [("qty", ["3"]), ("price", ["2.5"])]);
        using (var vector = await PdfVectorDocument.OpenAsync(doc.Bytes))
            Assert.Equal("7.5", PdfAcroForm.Read(vector)!["total"]!.Value);
        byte[] before = doc.Bytes;
        // The price's range rule (0 to 1000) refuses this value; nothing changes.
        await Assert.ThrowsAsync<PdfFieldValidationException>(() => doc.ApplyEditsAsync([], [], [("price", ["5000"])]));
        Assert.Same(before, doc.Bytes);
    }

    [Fact]
    public async Task AnnotationsAndFormValues_GoIntoOneSavedFile()
    {
        await using var doc = await Open(FormPdfFixture.Build());
        var store = Store(doc);
        store.Add([new ViewerAnnotation { Id = "n", Page = 1, Kind = ViewerAnnotationKind.Note, Box = new ViewerBox(10, 10, 30, 30), Contents = "Checked" }]);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, [("person.name", ["Grace"])]);
        await using var back = await Open(doc.Bytes);
        Assert.Equal("Checked", (await back.ReadAnnotationsAsync()).Single().Contents);
        Assert.Equal("Grace", (await back.GetFormWidgetsAsync()).First(w => w.Name == "person.name").Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PageGeometry_MapsShownPointsToUserSpaceAndBack(int rotation)
    {
        var g = new ViewerPageGeometry(new PdfEngine.Geometry.PdfRect(0, 0, 612, 792), new PdfEngine.Geometry.PdfRect(10, 20, 500, 700), rotation);
        var (x, y) = g.ToUser(30, 40);
        var (sx, sy) = g.ToShown(x, y);
        Assert.Equal(30, sx, 6);
        Assert.Equal(40, sy, 6);
        var (ox, oy) = g.ToUser(0, 0);
        // The shown top-left corner: the crop box's top-left, turned clockwise with the page.
        Assert.Equal(rotation is 0 or 90 ? 10 : 510, ox, 6);
        Assert.Equal(rotation is 0 or 270 ? 720 : 20, oy, 6);
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
