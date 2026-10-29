using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The web viewer's shape style: fill (/IC), dash (/BS /S /D) and line endings (/LE) of
/// rectangles, ellipses, lines, polygons and polylines. New shapes are written with them and an
/// appearance; a file's shapes keep what was not changed; measurements keep their style; the
/// embed API's dashPattern; undo.
/// </summary>
public class WebShapeStyleTests
{
    private const string Envelope = "\"protocol\":\"pdfviewer-embed\",\"version\":1";

    /// <summary>A 612 x 792 page with the given annotation dictionaries (made here, not taken from anywhere).</summary>
    private static byte[] WithAnnotations(params string[] annotations)
    {
        var b = new VectorPdfBuilder();
        var numbers = annotations.Select(a => b.Add(a)).ToList();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: annotations.Length == 0 ? "" : "/Annots [" + string.Join(" ", numbers.Select(n => $"{n} 0 R")) + "]");
        return b.Build();
    }

    /// <summary>Page 1's annotation dictionaries as written, with the content of each normal appearance.</summary>
    private static async Task<(PdfAnnotation Annotation, PdfDictionary Dictionary, string Appearance)[]> ReadSaved(byte[] bytes)
    {
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var r = doc.Resolver;
        var annots = (PdfArray)r.Resolve(doc.PageTree.Pages[0].Dictionary["Annots"])!;
        return PdfAnnotationReader.Read(doc, 1).Select((a, i) =>
        {
            var d = (PdfDictionary)r.Resolve(annots[i])!;
            string content = "";
            if (r.Resolve(d["AP"]) is PdfDictionary ap && r.Resolve(ap["N"]) is PdfStream form)
            {
                var raw = form.GetRawBytes().ToArray();
                if (form.Dictionary["Filter"] != null)
                {
                    using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                    using var o = new MemoryStream();
                    z.CopyTo(o);
                    raw = o.ToArray();
                }
                content = Encoding.Latin1.GetString(raw);
            }
            return (a, d, content);
        }).ToArray();
    }

    private static ViewerAnnotation New(ViewerAnnotationKind kind, ViewerBox box, double[]? vertices = null) => new()
    {
        Id = ViewerAnnotationStore.NewId(), Page = 1, Kind = kind, Box = box, Vertices = vertices ?? [],
        Color = "#E5484D", StrokeWidth = 2, Author = "Ada", Created = DateTimeOffset.UtcNow, Modified = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task NewShapes_AreWrittenWithFillDashAndEndings_AndReadBack()
    {
        await using var doc = await ViewerDocument.OpenAsync(WithAnnotations());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        store.Add(
        [
            New(ViewerAnnotationKind.Square, new ViewerBox(50, 50, 150, 120)) with { FillColor = "#2563EB", Dash = ViewerAnnotation.Dashed },
            New(ViewerAnnotationKind.Circle, new ViewerBox(200, 50, 300, 120)) with { Dash = ViewerAnnotation.Dotted },
            New(ViewerAnnotationKind.Line, new ViewerBox(40, 190, 360, 310), [50, 200, 350, 300]) with
                { FillColor = "#FFD400", StartEnding = "Circle", EndEnding = "RClosedArrow", Dash = ViewerAnnotation.Dashed },
            New(ViewerAnnotationKind.Polygon, new ViewerBox(40, 390, 360, 560), [50, 400, 300, 420, 200, 550]) with { FillColor = "#22A06B" },
        ]);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, []);

        var saved = await ReadSaved(doc.Bytes);
        Assert.Equal(["Square", "Circle", "Line", "Polygon"], saved.Select(s => s.Annotation.Subtype));
        Assert.All(saved, s => Assert.True(s.Annotation.HasAppearance));
        Assert.Equal("D", saved[0].Annotation.BorderStyle);
        Assert.Equal([4.0, 3.0], saved[0].Annotation.BorderDash!);
        Assert.NotNull(saved[0].Annotation.InteriorColor);
        Assert.Contains("[4 3] 0 d", saved[0].Appearance);
        Assert.Contains("[1 2] 0 d", saved[1].Appearance);
        Assert.Null(saved[1].Annotation.InteriorColor);
        Assert.Equal(["Circle", "RClosedArrow"], saved[2].Annotation.LineEndings!);
        Assert.Contains("[] 0 d", saved[2].Appearance); // the endings are drawn solid
        Assert.Equal("S", saved[3].Annotation.BorderStyle);
        Assert.Null(saved[3].Annotation.BorderDash);

        // Read back as the viewer's model.
        await using var again = await ViewerDocument.OpenAsync(doc.Bytes);
        var read = await again.ReadAnnotationsAsync();
        Assert.Equal("#2563EB", read[0].FillColor);
        Assert.Equal([4.0, 3.0], read[0].Dash!);
        Assert.Equal([1.0, 2.0], read[1].Dash!);
        Assert.Equal(("Circle", "RClosedArrow"), (read[2].StartEnding, read[2].EndEnding));
        Assert.Equal("#FFD400", read[2].FillColor);
        Assert.Null(read[3].Dash);
        Assert.All(read, a => Assert.True(a.HasShapeStyle));
    }

    [Fact]
    public async Task FileShapes_KeepTheirOwnStyle_UnlessThatValueChanges()
    {
        byte[] file = WithAnnotations(
            // A beveled square with a CMYK fill; a dashed line without /C; a polyline dashed through /Border; a square without /BS.
            "<< /Type /Annot /Subtype /Square /Rect [50 50 150 150] /C [1 0 0] /IC [0 0 1 0] /BS << /W 3 /S /B >> >>",
            "<< /Type /Annot /Subtype /Line /Rect [40 190 360 310] /L [50 200 350 300] /LE [/None /Diamond] /BS << /W 1 /S /D /D [2] >> >>",
            "<< /Type /Annot /Subtype /PolyLine /Rect [40 390 360 560] /Vertices [50 400 300 420 200 550] /Border [0 0 2 [5 1]] >>",
            "<< /Type /Annot /Subtype /Square /Rect [400 50 500 150] /C [0 0 1] >>");
        await using var doc = await ViewerDocument.OpenAsync(file);
        var fromFile = await doc.ReadAnnotationsAsync();
        Assert.Equal([5.0, 1.0], fromFile[2].Dash!);
        Assert.Null(fromFile[0].Dash); // beveled is not dashed
        var store = new ViewerAnnotationStore(doc.Geometry, fromFile);

        // The square gets a dash: its fill (CMYK) and /C stay.
        store.Update(fromFile[0] with { Dash = ViewerAnnotation.Dashed });
        // The line gets a fill: its endings, dash and missing /C... stay.
        store.Update(fromFile[1] with { FillColor = "#22A06B" });
        // The polyline and the plain square only move.
        store.Move(fromFile[2].Id, 10, 0, DateTimeOffset.UtcNow);
        store.Move(fromFile[3].Id, 10, 0, DateTimeOffset.UtcNow);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, []);

        var saved = await ReadSaved(doc.Bytes);
        Assert.Equal(4, ((PdfArray)saved[0].Dictionary["IC"]!).Count); // still CMYK
        Assert.Equal("D", saved[0].Annotation.BorderStyle);
        Assert.Equal([4.0, 3.0], saved[0].Annotation.BorderDash!);
        Assert.Equal(["None", "Diamond"], saved[1].Annotation.LineEndings!);
        Assert.Equal([2.0], saved[1].Annotation.BorderDash!);
        Assert.NotNull(saved[1].Annotation.InteriorColor);
        Assert.Contains("[5 1] 0 d", saved[2].Appearance);
        Assert.Null(saved[2].Dictionary["IC"]);
        Assert.Null(saved[2].Dictionary["LE"]);
        // No /IC, /BS or /LE defaults are added to a shape that had none.
        Assert.Null(saved[3].Dictionary["IC"]);
        Assert.Null(saved[3].Dictionary["BS"]);
    }

    [Fact]
    public async Task Measurements_KeepTheirStyle()
    {
        byte[] file = WithAnnotations(
            "<< /Type /Annot /Subtype /Line /Rect [40 190 360 310] /L [50 200 350 300] /LE [/Butt /Butt] /IT /LineDimension "
            + "/Measure << /Type /Measure /R (1 in = 1 in) /X [<< /U (in) /C 0.0138889 >>] /D [<< /U (in) /C 1 >>] /A [<< /U (sq in) /C 1 >>] >> >>");
        await using var doc = await ViewerDocument.OpenAsync(file);
        var fromFile = await doc.ReadAnnotationsAsync();
        var m = Assert.Single(fromFile);
        Assert.True(m.IsMeasurement);
        Assert.False(m.HasShapeStyle);
        var store = new ViewerAnnotationStore(doc.Geometry, fromFile);
        Assert.Throws<ViewerAnnotationException>(() => store.Update(m with { FillColor = "#000000" }));
        Assert.Throws<ViewerAnnotationException>(() => store.Update(m with { Dash = ViewerAnnotation.Dashed }));
        Assert.Throws<ViewerAnnotationException>(() => store.Update(m with { EndEnding = "OpenArrow" }));
        // A move keeps it a measurement, with its /Measure and endings.
        var moved = store.Move(m.Id, 5, 0, DateTimeOffset.UtcNow);
        Assert.True(moved.IsMeasurement);
        await doc.ApplyEditsAsync(store.FromFile, store.Current, []);
        var saved = Assert.Single(await ReadSaved(doc.Bytes));
        Assert.NotNull(saved.Annotation.Measure);
        Assert.Equal(["Butt", "Butt"], saved.Annotation.LineEndings!);
        Assert.Null(saved.Dictionary["IC"]);
    }

    [Fact]
    public async Task StyleChanges_AreUndoable_AndEndingsGrowTheBox()
    {
        await using var doc = await ViewerDocument.OpenAsync(WithAnnotations());
        var store = new ViewerAnnotationStore(doc.Geometry, []);
        var line = New(ViewerAnnotationKind.Line, ViewerBox.Around([100, 100, 300, 100], ViewerAnnotationStore.ShapeMargin(2, false)), [100, 100, 300, 100]);
        store.Add([line]);
        var before = store.Find(line.Id)!;
        var arrow = before with { StartEnding = "ROpenArrow", EndEnding = "OpenArrow" };
        arrow = arrow with { Box = ViewerAnnotationStore.GrownForEndings(arrow) };
        store.Update(arrow with { Dash = ViewerAnnotation.Dotted });
        var after = store.Find(line.Id)!;
        // A reverse arrow reaches past its point by nine widths at 30 degrees.
        Assert.True(after.Box.X1 <= 100 - 9 * 2 * Math.Cos(Math.PI / 6));
        Assert.True(after.Box.Y2 > before.Box.Y2);
        Assert.True(store.Undo());
        Assert.Same(before, store.Find(line.Id));
        Assert.True(store.Redo());
        Assert.Equal([1.0, 2.0], store.Find(line.Id)!.Dash!);
    }

    [Fact]
    public void Store_RefusesBadDashes()
    {
        var store = new ViewerAnnotationStore([new ViewerPageGeometry(new(0, 0, 612, 792), new(0, 0, 612, 792), 0)], []);
        var square = New(ViewerAnnotationKind.Square, new ViewerBox(50, 50, 150, 150));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([square with { Dash = [0, 0] }]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([square with { Dash = [-1, 2] }]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([square with { Dash = Enumerable.Repeat(1.0, 17).ToArray() }]));
        Assert.Throws<ViewerAnnotationException>(() => store.Add([square with { Kind = ViewerAnnotationKind.Ink, InkList = [[60, 60, 70, 70]], Dash = [2] }]));
        Assert.Single(store.Add([square with { Dash = [6, 2, 1, 2] }]));
    }

    [Fact]
    public void EmbedJson_DashPattern_RoundTrips()
    {
        string json = "{" + Envelope + ",\"type\":\"call\",\"requestId\":9,\"document\":1,\"method\":\"addAnnotations\",\"args\":[[" +
            "{\"id\":\"s1\",\"type\":\"Annotation\",\"bodyValue\":\"\",\"target\":{\"source\":\"x\",\"selector\":{\"node\":{\"index\":0},\"subtype\":\"square\",\"boundingBox\":[10,10,90,90],\"dashPattern\":[4,3]}}}," +
            "{\"id\":\"s2\",\"type\":\"Annotation\",\"bodyValue\":\"\",\"target\":{\"source\":\"x\",\"selector\":{\"node\":{\"index\":0},\"subtype\":\"circle\",\"boundingBox\":[10,10,90,90],\"dashPattern\":[]}}}]]}";
        var call = Assert.IsType<EmbedCall>(EmbedProtocol.Parse(json, EmbedMode.FullWindow));
        Assert.Equal([4.0, 3.0], call.Annotations[0].Dash!);
        Assert.Null(call.Annotations[1].Dash);

        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(w, call.Annotations[0], "x");
        using var parsed = JsonDocument.Parse(buffer.WrittenMemory);
        var selector = parsed.RootElement.GetProperty("target").GetProperty("selector");
        Assert.Equal([4.0, 3.0], selector.GetProperty("dashPattern").EnumerateArray().Select(e => e.GetDouble()));

        buffer.Clear();
        using (var w = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(w, call.Annotations[1], "x");
        using var solid = JsonDocument.Parse(buffer.WrittenMemory);
        Assert.False(solid.RootElement.GetProperty("target").GetProperty("selector").TryGetProperty("dashPattern", out _));
    }
}
