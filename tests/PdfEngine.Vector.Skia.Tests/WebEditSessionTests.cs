using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// What the web viewer and its embed SDK need from the shared <see cref="ViewerEditSession"/>
/// (the core Android uses too): the open options (read-only, the file's own annotations, form
/// filling), shapes with a style, polygons and connected lines, a text box the reader sizes, colour,
/// style and moves as one undo step each, the host page's add, update and delete, the annotations an
/// XFDF import added, and the writes and saves a viewer follows. Every PDF is built here.
/// </summary>
public class WebEditSessionTests
{
    /// <summary>A Letter page with a note (q) that has a reply (a), and a blank Letter page.</summary>
    private static byte[] Commented()
    {
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        b.Add("<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (q) /Contents (Q) /T (Bob) /F 4 >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (a) /IRT 4 0 R /RT /R /Contents (A) /T (Bob) /F 28 >>");
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Annots [4 0 R 5 0 R] >>");
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static byte[] Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static ViewerPoint P(double x, double y) => new(x, y);

    [Fact]
    public async Task Options_ReadOnly_FileAnnotations_AndFormFilling()
    {
        await using (var doc = await ViewerDocument.OpenAsync(Commented()))
        {
            // The file's annotations are not managed: only the ones added are listed.
            var session = await ViewerEditSession.StartAsync(doc, new ViewerEditOptions { Author = "Ada", ReadFileAnnotations = false });
            Assert.Empty(session.Store.Current);
            Assert.Equal("Ada", session.Author);
            Assert.True(session.CanAnnotate);
            Assert.Null(session.AnnotateRestriction);
        }

        await using (var doc = await ViewerDocument.OpenAsync(Commented()))
        {
            // Opened read-only: no change, and the reason is the viewer's.
            var session = await ViewerEditSession.StartAsync(doc, new ViewerEditOptions { ReadOnlyReason = "Read-only here." });
            Assert.Equal(2, session.Store.Current.Count);
            Assert.True(session.ReadOnly);
            Assert.False(session.CanAnnotate || session.CanUndo);
            Assert.Equal("Read-only here.", session.AnnotateRestriction);
            var ex = Assert.Throws<ViewerAnnotationException>(() => session.AddNote(1, P(50, 50), "n"));
            Assert.Equal("Read-only here.", ex.Message);
            Assert.Throws<ViewerAnnotationException>(() => session.DeleteAnnotations(ViewerAnnotationFilter.All));
            Assert.Throws<ViewerAnnotationException>(() => session.ImportXfdf("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"/>"));
        }

        await using (var form = await ViewerDocument.OpenAsync(FormPdfFixture.Build()))
        {
            var noFill = await ViewerEditSession.StartAsync(form, new ViewerEditOptions { FillForms = false });
            Assert.False(noFill.CanFillForms);
            Assert.Empty(noFill.Widgets);
            Assert.True(noFill.CanAnnotate);
            var any = (await form.GetFormWidgetsAsync()).First();
            Assert.False(noFill.SetFieldValue(any, ["x"], out var why));
            Assert.NotNull(why);

            var readOnly = await ViewerEditSession.StartAsync(form, new ViewerEditOptions { ReadOnlyReason = "Read-only here." });
            Assert.Empty(readOnly.Widgets);
            Assert.False(readOnly.SetFieldValue(any, ["x"], out why));
            Assert.Equal("Read-only here.", why);
        }
    }

    [Fact]
    public async Task Shapes_TakeTheirStyle_PolygonsAndConnectedLines()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var style = new ViewerShapeStyle("#2563EB", ViewerAnnotation.Dashed, "Circle", "ClosedArrow");

        var rect = session.AddShape(ViewerAnnotationKind.Square, 1, P(100, 100), P(200, 160), "#E5484D", style)!;
        Assert.Equal(("#2563EB", "None", "None"), (rect.FillColor, rect.StartEnding, rect.EndEnding)); // a rectangle has no line endings
        Assert.True(ViewerAnnotation.SameDash(ViewerAnnotation.Dashed, rect.Dash));

        var line = session.AddLine(1, P(100, 300), P(300, 300), style)!;
        Assert.Equal(("Circle", "ClosedArrow", 2.0), (line.StartEnding, line.EndEnding, line.StrokeWidth));
        // The box holds the arrow's wings past the end point.
        Assert.True(line.Box.X2 >= 300 + 9 * 2);

        // A double-click gives its point twice: the repeat is dropped. Three points make a polygon.
        var polygon = session.AddPolyline(ViewerAnnotationKind.Polygon, 1, [P(50, 400), P(150, 400), P(100, 480), P(100.5, 480.5)], style)!;
        Assert.Equal(6, polygon.Vertices.Count);
        Assert.Equal(("#2563EB", "None", "None", 2.0), (polygon.FillColor, polygon.StartEnding, polygon.EndEnding, polygon.StrokeWidth));
        var polyline = session.AddPolyline(ViewerAnnotationKind.PolyLine, 1, [P(300, 400), P(400, 450), P(500, 400)], style, "#000000")!;
        Assert.Equal(("Circle", "ClosedArrow", "#000000"), (polyline.StartEnding, polyline.EndEnding, polyline.Color));

        Assert.Null(session.AddPolyline(ViewerAnnotationKind.Polygon, 1, [P(10, 10), P(60, 60), P(60.5, 60)]));
        Assert.Null(session.AddPolyline(ViewerAnnotationKind.PolyLine, 1, [P(10, 10), P(11, 10)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AddPolyline(ViewerAnnotationKind.Line, 1, [P(10, 10), P(60, 60)]));

        Assert.True((await session.ApplyAsync()).Written);
        using var back = await PdfVectorDocument.OpenAsync(doc.Bytes);
        var read = PdfAnnotationReader.Read(back, 1);
        Assert.Equal(4, read.Count);
        Assert.Contains(read, p => p.Name == polygon.Id && p.Subtype == "Polygon");
        Assert.Contains(read, p => p.Name == polyline.Id && p.Subtype == "PolyLine");
    }

    [Fact]
    public async Task ATextBoxTheReaderSized_KeepsItsSize_AndAClickGivesTheUsualBox()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var box = session.AddTextBox(1, P(300, 100), P(100, 200))!;
        Assert.Equal("", box.Contents);
        var shown = doc.Geometry[0].ToShown(box.Box);
        Assert.Equal((100.0, 100.0, 200.0, 100.0), (shown.X, shown.Y, shown.Width, shown.Height));

        Assert.True(session.SetContents(box.Id, "A short text"));
        Assert.Equal(box.Box, session.Store.Find(box.Id)!.Box); // its size is the reader's
        Assert.Throws<ViewerAnnotationException>(() => session.SetContents(box.Id, "  "));

        // A click (a drag too small for a box) gives the usual box, kept on the page.
        var click = session.AddTextBox(1, P(600, 780), P(601, 781))!;
        var c = doc.Geometry[0].ToShown(click.Box);
        Assert.Equal((ViewerEditSession.TextBoxWidth, ViewerEditSession.TextBoxHeight), (c.Width, c.Height));
        Assert.Equal((612 - c.Width, 792 - c.Height), (c.X, c.Y));
        // An empty text box the reader leaves is deleted.
        Assert.True(session.Delete(click.Id));
        Assert.Null(session.AddTextBox(1, P(double.NaN, 1), P(1, 1)));
    }

    [Fact]
    public async Task Colour_Style_AndMoves_AreOneUndoStepEach()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var line = session.AddLine(1, P(100, 100), P(200, 100), arrow: false)!;
        var stamp = session.AddStamp(1, P(300, 300), PdfStamps.Standard[0]);
        int version = session.Version;

        Assert.True(session.SetColor(line.Id, "#2563EB"));
        Assert.False(session.SetColor(line.Id, "#2563eb")); // the same colour
        Assert.False(session.SetColor(stamp.Id, "#2563EB")); // a stamp keeps its kind's colour

        Assert.True(session.SetShapeStyle(line.Id, new ViewerShapeStyle(End: "OpenArrow")));
        var arrowed = session.Store.Find(line.Id)!;
        Assert.Equal("OpenArrow", arrowed.EndEnding);
        Assert.True(arrowed.Box.X2 > line.Box.X2); // grown to hold the arrow
        Assert.False(session.SetShapeStyle(line.Id, ViewerShapeStyle.Of(arrowed)));
        Assert.False(session.SetShapeStyle(stamp.Id, new ViewerShapeStyle(Fill: "#000000")));

        Assert.True(session.Move(line.Id, 10, 20));
        var moved = session.Store.Find(line.Id)!;
        Assert.Equal(arrowed.Box.X1 + 10, moved.Box.X1, 6);
        // Shown points go down the page; user space goes up.
        Assert.Equal(arrowed.Box.Y1 - 20, moved.Box.Y1, 6);
        Assert.Equal(version + 3, session.Version);

        // A move the page edge stops entirely is no step: undo still goes back to before the move.
        var stuck = session.AddShape(ViewerAnnotationKind.Square, 1, P(0, 0), P(50, 50))!;
        Assert.False(session.Move(stuck.Id, -30, -30));
        Assert.False(session.CanRedo);
        Assert.True(session.Undo()); // the square
        Assert.True(session.Undo()); // the move
        Assert.Equal(arrowed.Box, session.Store.Find(line.Id)!.Box);
        Assert.True(session.Undo()); // the style
        Assert.True(session.Undo()); // the colour
        Assert.Equal(line.Color, session.Store.Find(line.Id)!.Color);
    }

    [Fact]
    public async Task TheHostPagesAnnotations_TakeDefaults_UpdatesKeepWhatIsLeftOut_AndDeletesTakeReplies()
    {
        await using var doc = await ViewerDocument.OpenAsync(Commented());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var given = new ViewerAnnotation
        {
            Id = "h1", Page = 2, Kind = ViewerAnnotationKind.Square, Box = new ViewerBox(100, 100, 200, 200), Color = "#FF0000",
            StrokeWidth = 1, Dash = ViewerAnnotation.Dotted,
        };
        var added = session.AddAnnotations([given]);
        Assert.Equal("Ada", added[0].Author);
        Assert.NotEqual(default, added[0].Created);
        Assert.False(session.IsOwn(added[0])); // the page made it, not the reader
        Assert.True(session.IsDirty);
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([given])); // the id is in use

        // No dash given: it stays. The author and creation date stay the annotation's.
        var updated = session.UpdateAnnotation(given with { Color = "#00FF00", Dash = null, Author = "" }, keepDash: true);
        Assert.Equal(("#00FF00", "Ada", added[0].Created), (updated.Color, updated.Author, updated.Created));
        Assert.True(ViewerAnnotation.SameDash(ViewerAnnotation.Dotted, updated.Dash));
        Assert.Null(session.UpdateAnnotation(updated with { Dash = null }).Dash);
        Assert.Throws<ViewerAnnotationException>(() => session.UpdateAnnotation(given with { Id = "nobody" }));

        // The file's note goes with its reply.
        var removed = session.DeleteAnnotations(new ViewerAnnotationFilter(new HashSet<string> { "q" }));
        Assert.Equal(["q", "a"], removed.Select(a => a.Id));
        Assert.Empty(session.DeleteAnnotations(new ViewerAnnotationFilter(new HashSet<string> { "q" })));
        Assert.Equal(["h1"], session.Store.Current.Select(a => a.Id));
        Assert.True(session.Undo());
        Assert.Equal(3, session.Store.Current.Count);
    }

    [Fact]
    public async Task AnXfdfImport_ReturnsTheAnnotationsItAdded()
    {
        await using var doc = await ViewerDocument.OpenAsync(Commented());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        // "q" is in use: the import's q gets a new id, and its reply follows it.
        var import = session.ImportXfdf("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"q\" title=\"Cy\"><contents>New</contents></text>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"r\" inreplyto=\"q\" replyType=\"reply\"><contents>Re</contents></text>" +
            "<square page=\"9\" rect=\"10,10,30,30\" name=\"far\"/></annots></xfdf>");
        Assert.Equal((2, 1), (import.Added, import.Skipped));
        Assert.Equal(2, import.Annotations.Count);
        var note = import.Annotations[0];
        Assert.NotEqual("q", note.Id);
        Assert.Equal(("Cy", note.Id), (note.Author, import.Annotations[1].InReplyTo));
        Assert.All(import.Annotations, a => Assert.Same(a, session.Store.Find(a.Id)));
        Assert.Empty(default(ViewerXfdfImport).Annotations);
    }

    [Fact]
    public async Task Writes_AreAnnounced_AndASaveIsAnsweredByItsId()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        int written = 0;
        session.Written += () => written++;

        Assert.True(await session.WriteAllAsync()); // nothing to write
        Assert.Equal(0, written);
        session.AddNote(1, P(50, 50), "One");
        Assert.True(await session.WriteAllAsync());
        Assert.Equal(1, written);
        Assert.True(session.IsCurrent);

        session.AddNote(1, P(80, 80), "Two");
        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        Assert.Equal(2, written); // the save wrote the second note first
        Assert.Equal(save.Id, session.PendingSave);
        Assert.Null(await session.BeginSaveAsync()); // one save at a time
        Assert.False(session.EndSave(save.Id + 1, saved: true));
        Assert.True(session.EndSave(save.Id, saved: true));
        Assert.False(session.EndSave(save.Id, saved: true)); // answered already
        Assert.False(session.IsSaving || session.IsDirty);
        Assert.Equal(0, session.PendingSave);
    }

    // ------------------------------------------------------------------ review changes

    private static Task<ViewerEditSession> ReadOnlySession(ViewerDocument doc) =>
        ViewerEditSession.StartAsync(doc, new ViewerEditOptions { ReadOnlyReason = "Read-only here." });

    [Fact]
    public async Task Move_ClampsEachAxis_FollowsARotatedPage_AndRefusesWhatCannotMove()
    {
        await using var doc = await ViewerDocument.OpenAsync(Commented());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[1];
        // At the left edge: the move left is stopped, the move down is not.
        var square = session.AddShape(ViewerAnnotationKind.Square, 2, P(0, 100), P(50, 150))!;
        Assert.True(session.Move(square.Id, -30, 20));
        var moved = g.ToShown(session.Store.Find(square.Id)!.Box);
        var was = g.ToShown(square.Box);
        Assert.Equal((was.X, was.Y + 20), (moved.X, moved.Y));

        // A reply moves with its parent only; NaN and infinite moves are no moves.
        Assert.False(session.Move("a", 10, 10));
        Assert.False(session.Move(square.Id, double.NaN, 10));
        Assert.False(session.Move(square.Id, 10, double.PositiveInfinity));
        Assert.Throws<ViewerAnnotationException>(() => session.Move("nobody", 1, 1));

        Assert.True(session.Move("q", 5, 5));

        await using var ro = await ViewerDocument.OpenAsync(Commented());
        var readOnly = await ReadOnlySession(ro);
        Assert.Throws<ViewerAnnotationException>(() => readOnly.Move("q", 5, 5));
    }

    [Fact]
    public async Task Move_OnATurnedPage_GoesTheWayTheReaderDragged()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: "/Rotate 90");
        await using var doc = await ViewerDocument.OpenAsync(b.Build());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[0];
        Assert.Equal(90, g.Rotation);
        var square = session.AddShape(ViewerAnnotationKind.Square, 1, P(200, 200), P(260, 240))!;
        var before = g.ToShown(square.Box);
        Assert.True(session.Move(square.Id, 15, -10));
        var after = g.ToShown(session.Store.Find(square.Id)!.Box);
        Assert.Equal(before.X + 15, after.X, 6);
        Assert.Equal(before.Y - 10, after.Y, 6);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
    }

    [Fact]
    public async Task AFailedWrite_IsReported_AndNeitherWriteAllNorASaveClaimsTheBytes()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var reports = new List<ViewerApplyResult>();
        session.Reported += reports.Add;
        session.BeforeWrite = _ => throw new IOException("disk");
        session.AddNote(1, P(50, 50), "One");

        Assert.False(await session.WriteAllAsync());
        Assert.Single(reports);
        Assert.NotNull(reports[0].Failed);
        Assert.DoesNotContain("disk", reports[0].Failed); // type names only, never the exception's text
        Assert.Null(await session.BeginSaveAsync());
        Assert.False(session.IsSaving);
        Assert.True(session.IsDirty);

        // The next change is written once writing works again.
        session.BeforeWrite = null;
        session.AddNote(1, P(80, 80), "Two");
        Assert.True(await session.WriteAllAsync());
    }

    [Fact]
    public async Task ARunningWrite_IsWaitedFor_HoweverLongItTakes()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        // Longer than the 4 s the waits once shared with the write attempts.
        session.BeforeWrite = ct => Task.Delay(TimeSpan.FromSeconds(4.5), ct);
        session.AddNote(1, P(50, 50), "Slow");
        var running = session.ApplyAsync();
        Assert.Equal(default, await session.ApplyAsync()); // one write at a time
        Assert.True(await session.WriteAllAsync());
        Assert.True((await running).Written);

        session.AddNote(1, P(80, 80), "Slow too");
        running = session.ApplyAsync();
        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        await running;
        Assert.True(session.EndSave(save, saved: true));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task ASaveId_IsUniqueAcrossSessions_SoALateAnswerForAnotherDocumentIsRefused()
    {
        await using var first = await ViewerDocument.OpenAsync(Blank());
        var a = await ViewerEditSession.StartAsync(first, "Ada");
        a.AddNote(1, P(50, 50), "A");
        var saveA = (await a.BeginSaveAsync())!;

        await using var second = await ViewerDocument.OpenAsync(Blank());
        var b = await ViewerEditSession.StartAsync(second, "Ada");
        b.AddNote(1, P(50, 50), "B");
        var saveB = (await b.BeginSaveAsync())!;
        Assert.NotEqual(saveA.Id, saveB.Id);

        // The host page answers the first document's save after the second one opened.
        Assert.False(b.EndSave(saveA.Id, saved: true));
        Assert.True(b.IsSaving && b.IsDirty);
        Assert.True(b.EndSave(saveB.Id, saved: true));
        Assert.False(b.IsDirty);
    }

    private const string Ns = "http://ns.adobe.com/xfdf/";

    [Fact]
    public async Task XfdfImport_SkipsInvalidAnnotationsWithTheirReplies_AndRefusesTooMany()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");

        // A square outside the media box is skipped, and its reply with it.
        var outside = session.ImportXfdf($"<xfdf xmlns=\"{Ns}\"><annots>" +
            "<square page=\"0\" rect=\"1000,1000,1100,1100\" name=\"far\"/>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"r\" inreplyto=\"far\" replyType=\"reply\"/>" +
            "<square page=\"0\" rect=\"10,10,30,30\" name=\"ok\"/></annots></xfdf>");
        Assert.Equal((1, 2), (outside.Added, outside.Skipped));
        Assert.Equal(["ok"], session.Store.Current.Select(a => a.Id));

        // A thread deeper than the limit: the replies past it are skipped, the rest are added.
        int extra = 2;
        var chain = new System.Text.StringBuilder($"<xfdf xmlns=\"{Ns}\"><annots><text page=\"0\" rect=\"10,10,30,30\" name=\"t0\"/>");
        for (int i = 1; i <= ViewerAnnotationStore.MaxReplyDepth + extra; i++)
            chain.Append($"<text page=\"0\" rect=\"10,10,30,30\" name=\"t{i}\" inreplyto=\"t{i - 1}\" replyType=\"reply\"/>");
        chain.Append("</annots></xfdf>");
        var deep = session.ImportXfdf(chain.ToString());
        Assert.Equal((1 + ViewerAnnotationStore.MaxReplyDepth, extra), (deep.Added, deep.Skipped));
        Assert.Null(session.Store.Find($"t{ViewerAnnotationStore.MaxReplyDepth + 1}"));

        // More than the store takes in one call: the whole import is refused, nothing added.
        int count = session.Store.Current.Count;
        var many = new System.Text.StringBuilder($"<xfdf xmlns=\"{Ns}\"><annots>");
        for (int i = 0; i <= ViewerAnnotationStore.MaxPerCall; i++) many.Append($"<square page=\"0\" rect=\"10,10,30,30\" name=\"m{i}\"/>");
        many.Append("</annots></xfdf>");
        var ex = Assert.Throws<ViewerAnnotationException>(() => session.ImportXfdf(many.ToString()));
        Assert.Contains($"at most {ViewerAnnotationStore.MaxPerCall}", ex.Message);
        Assert.Equal(count, session.Store.Current.Count);
    }

    [Fact]
    public async Task TheHostPagesValues_AreChecked_AndAnUpdateKeepsTheAuthorAsItWas()
    {
        await using var doc = await ViewerDocument.OpenAsync(Commented());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        ViewerAnnotation Square(string id) => new()
        {
            Id = id, Page = 1, Kind = ViewerAnnotationKind.Square, Box = new ViewerBox(100, 100, 200, 200), Color = "#FF0000",
        };
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([Square("nan") with { Box = new ViewerBox(double.NaN, 100, 200, 200) }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([Square("inf") with { Box = new ViewerBox(100, 100, double.PositiveInfinity, 200) }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([Square("wide") with { StrokeWidth = 1000 }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([Square("long") with { Contents = new string('x', ViewerAnnotationStore.MaxContentsLength + 1) }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations(Enumerable.Range(0, ViewerAnnotationStore.MaxPerCall + 1).Select(i => Square($"s{i}")).ToList()));
        Assert.Equal(2, session.Store.Current.Count);
        Assert.False(session.IsDirty);

        // An update without an author keeps the annotation's own (Bob's, not the reader's).
        var q = session.Store.Find("q")!;
        Assert.Equal("Bob", session.UpdateAnnotation(q with { Author = "", Contents = "Edited" }).Author);
        Assert.Throws<ViewerAnnotationException>(() => session.UpdateAnnotation(q with { Box = new ViewerBox(100, 600, double.NaN, 620) }));
        Assert.Equal("Edited", session.Store.Find("q")!.Contents);

        // An annotation without an author stays without one: the reader's name is not written in.
        var anonymous = session.AddAnnotations([Square("anon")])[0];
        Assert.Equal("Ada", anonymous.Author); // a new one takes the reader's name
        var noName = session.UpdateAnnotation(anonymous with { Author = "" });
        Assert.Equal("Ada", noName.Author);
        session.Author = "";
        var blank = session.AddAnnotations([Square("blank")])[0];
        session.Author = "Cy";
        Assert.Equal("", session.UpdateAnnotation(blank with { Contents = "x" }).Author);
    }

    [Fact]
    public async Task AReadOnlySession_RefusesColourStyleAndTextBoxes()
    {
        await using var doc = await ViewerDocument.OpenAsync(Commented());
        var session = await ReadOnlySession(doc);
        Assert.Throws<ViewerAnnotationException>(() => session.SetColor("q", "#000000"));
        Assert.Throws<ViewerAnnotationException>(() => session.SetShapeStyle("q", ViewerShapeStyle.Plain));
        Assert.Throws<ViewerAnnotationException>(() => session.AddTextBox(1, P(10, 10), P(200, 100)));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([]));
        Assert.Throws<ViewerAnnotationException>(() => session.UpdateAnnotation(session.Store.Find("q")!));
        Assert.False(session.IsDirty);
    }
}
