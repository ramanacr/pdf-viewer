using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The Android reader's comment threads, XFDF and kept edits, through the shared
/// <see cref="ViewerEditSession"/>: replies and review statuses written as the desktop viewer writes
/// them (/IRT, /RT /R, /F 28 and the Comment icon; a status a hidden reply, /F 30, with /State and
/// /StateModel /Review), own replies edited and deleted with undo, an XFDF export imported into
/// another document, and the edits kept while Android ends the app (a snapshot as JSON) given back
/// to the same bytes. Every PDF is built here.
/// </summary>
public class AndroidCommentTests
{
    /// <summary>A Letter page with a note (q) that has a reply and a rejected status, and a square (sq) with a comment.</summary>
    private static byte[] Commented()
    {
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        b.Add("<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (q) /Contents (Q) /F 4 >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (a) /IRT 4 0 R /RT /R /Contents (A) /T (Bob) /F 28 >>");
        b.Add("<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (st) /IRT 4 0 R /RT /R /State (Rejected) /StateModel (Review) /T (Bob) /F 30 /M (D:20260101100000Z) >>");
        b.Add("<< /Type /Annot /Subtype /Square /Rect [200 200 300 260] /NM (sq) /C [1 0 0] /Contents (Square text) /F 4 >>");
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Annots [4 0 R 5 0 R 6 0 R 7 0 R] >>");
        return b.Build();
    }

    /// <summary>Two blank Letter pages.</summary>
    private static byte[] Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static async Task<(ViewerDocument Doc, ViewerEditSession Session)> Start(byte[] pdf, string author)
    {
        var doc = await ViewerDocument.OpenAsync(pdf);
        return (doc, await ViewerEditSession.StartAsync(doc, author));
    }

    [Fact]
    public async Task AFilesStatus_IsTheCommentsStatus_NotAReplyLine()
    {
        var (doc, session) = await Start(Commented(), "Ada");
        await using var _ = doc;
        var status = session.Store.Find("st");
        Assert.NotNull(status);
        Assert.True(status.IsStatus);
        Assert.Equal(("Rejected", "Review"), (status.State, status.StateModel));
        Assert.Equal("Rejected", session.ReviewStateOf("q"));

        var thread = session.Thread("q");
        Assert.Equal(["q", "a"], thread.Select(i => i.Annotation.Id));
        Assert.Equal("Rejected", thread[0].Status);
        Assert.DoesNotContain(session.List(), i => i.Annotation.IsStatus);
        Assert.Empty(session.Thread("st"));
        // The reader may not change Bob's reply.
        Assert.False(session.IsOwn(thread[1].Annotation));
    }

    [Fact]
    public async Task RepliesAndStatuses_AreWrittenAsTheDesktopViewerWritesThem()
    {
        var (doc, session) = await Start(Blank(), "Ada");
        await using var _ = doc;
        var note = session.AddNote(1, new ViewerPoint(100, 100), "Please check");
        var reply = session.AddReply(note.Id, "  Looks good  ")!;
        Assert.Equal(("Looks good", "Ada", note.Page), (reply.Contents, reply.Author, reply.Page));
        Assert.Null(session.AddReply(note.Id, "   "));
        var nested = session.AddReply(reply.Id, "Thanks")!;
        var status = session.SetReviewState(note.Id, "Accepted")!;
        Assert.Equal("Accepted set by Ada", status.Contents);
        Assert.Null(session.SetReviewState(note.Id, "accepted")); // the status it has already
        Assert.Throws<ViewerAnnotationException>(() => session.SetReviewState(note.Id, "Maybe"));
        Assert.Throws<ViewerAnnotationException>(() => session.SetReviewState(reply.Id, "Accepted"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddReply(status.Id, "no"));
        Assert.Equal("Accepted", session.ReviewStateOf(note.Id));
        Assert.Equal([0, 1, 2], session.Thread(note.Id).Select(i => i.Level));
        Assert.Equal("Accepted", session.List().Single(i => i.Level == 0).Status);

        Assert.True((await session.ApplyAsync()).Written);
        using (var back = await PdfVectorDocument.OpenAsync(doc.Bytes))
        {
            var read = PdfAnnotationReader.Read(back, 1);
            var pNote = read.Single(p => p.Name == note.Id);
            var pReply = read.Single(p => p.Name == reply.Id);
            Assert.Equal((pNote.ObjectNumber, "R", 28, "Comment"), (pReply.InReplyTo, pReply.ReplyType, pReply.Flags, pReply.Icon));
            Assert.Equal(pReply.ObjectNumber, read.Single(p => p.Name == nested.Id).InReplyTo);
            var pStatus = read.Single(p => p.Name == status.Id);
            Assert.Equal((pNote.ObjectNumber, "R", 30, "Accepted", "Review"), (pStatus.InReplyTo, pStatus.ReplyType, pStatus.Flags, pStatus.State, pStatus.StateModel));
        }

        // Another reader opens the saved bytes: the thread and the status are there; "None" clears the status.
        var (again, other) = await Start(doc.Bytes, "Bob");
        await using var __ = again;
        Assert.Equal("Accepted", other.ReviewStateOf(note.Id));
        Assert.Equal(3, other.Thread(note.Id).Count);
        Assert.False(other.IsOwn(other.Store.Find(reply.Id)!));
        Assert.NotNull(other.SetReviewState(note.Id, "None"));
        Assert.Null(other.ReviewStateOf(note.Id));
        Assert.True(other.Undo());
        Assert.Equal("Accepted", other.ReviewStateOf(note.Id));
    }

    [Fact]
    public async Task OwnReplies_AreEditedAndDeleted_AndUndoBringsThemBack()
    {
        var (doc, session) = await Start(Blank(), "Ada");
        await using var _ = doc;
        var note = session.AddNote(1, new ViewerPoint(50, 50), "Note");
        var reply = session.AddReply(note.Id, "one")!;
        Assert.True(session.IsOwn(reply));
        Assert.True(session.SetContents(reply.Id, "two"));
        Assert.True(session.Delete(reply.Id));
        Assert.Null(session.Store.Find(reply.Id));
        Assert.True(session.Undo());
        Assert.Equal("two", session.Store.Find(reply.Id)!.Contents);
        Assert.True(session.Undo());
        Assert.Equal("one", session.Store.Find(reply.Id)!.Contents);

        // Deleting the comment takes its replies and statuses with it.
        session.SetReviewState(note.Id, "Rejected");
        Assert.True(session.Delete(note.Id));
        Assert.Empty(session.Store.Current);

        // Without a name nothing is the reader's own.
        session.Author = "";
        Assert.False(session.IsOwn(reply with { Author = "" }));
    }

    [Fact]
    public async Task XfdfExport_ImportsIntoAnotherDocument_WithThreadsAndStatus_AsOneUndoStep()
    {
        var (doc, session) = await Start(Commented(), "Ada");
        await using var _ = doc;
        session.AddReply("a", "Agreed");
        session.SetReviewState("q", "Completed");
        string xfdf = session.ExportXfdf("commented.pdf");
        Assert.Contains("replyType=\"reply\"", xfdf);
        Assert.Contains("state=\"Completed\"", xfdf);
        Assert.Contains("statemodel=\"Review\"", xfdf);

        var (blank, target) = await Start(Blank(), "Cy");
        await using var __ = blank;
        var result = target.ImportXfdf(xfdf);
        Assert.Equal((6, 0), (result.Added, result.Skipped)); // q, a, st, sq, the new reply and the new status
        Assert.Equal("Completed", target.ReviewStateOf("q"));
        Assert.Equal(["q", "a", "Agreed"], target.Thread("q").Select(i => i.Level == 2 ? i.Annotation.Contents : i.Annotation.Id));
        Assert.True(target.IsDirty);
        Assert.True((await target.ApplyAsync()).Written);
        using (var back = await PdfVectorDocument.OpenAsync(blank.Bytes))
        {
            var read = PdfAnnotationReader.Read(back, 1);
            Assert.Equal(2, read.Count(p => p.StateModel == "Review" && (p.Flags & 2) != 0));
        }
        Assert.True(target.Undo());
        Assert.Empty(target.Store.Current);

        // Annotations off the document's pages are left out (a reply with its parent); text that is not XFDF is refused.
        var off = target.ImportXfdf("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<text page=\"6\" rect=\"10,10,30,30\" name=\"far\"/><text page=\"6\" rect=\"10,10,30,30\" name=\"r\" inreplyto=\"far\"/>" +
            "<square page=\"1\" rect=\"10,10,30,30\" name=\"ok\"/></annots></xfdf>");
        Assert.Equal((1, 2), (off.Added, off.Skipped));
        Assert.Equal(2, target.Store.Find("ok")!.Page);
        Assert.Throws<ViewerAnnotationException>(() => target.ImportXfdf("not xml"));
    }

    [Fact]
    public async Task KeptEdits_GoThroughJson_AndComeBackToTheSameBytes()
    {
        byte[] pdf = Commented();
        var (doc, session) = await Start(pdf, "Ada");
        await using var _ = doc;
        Assert.True(session.SetContents("q", "Q, changed"));
        var note = session.AddNote(1, new ViewerPoint(300, 300), "New note");
        session.AddReply("sq", "About the square");
        session.SetReviewState("sq", "Accepted");
        string hash = ViewerEditSnapshot.Hash(pdf);
        var snapshot = session.Snapshot("content://provider/doc.pdf", hash);
        // A file's annotation that did not change is kept by its id only: its text is not stored.
        Assert.Contains(snapshot.Annotations, i => i.KeptId == "sq");
        string json = snapshot.ToJson();
        Assert.DoesNotContain("Square text", json);
        Assert.Contains("Q, changed", json);

        var read = ViewerEditSnapshot.FromJson(json);
        Assert.Equal(("content://provider/doc.pdf", pdf.LongLength, hash), (read.Location, read.OriginalLength, read.OriginalHash));

        // A new process opens the same bytes and gets the edits back, pending.
        var (doc2, restored) = await Start(read.OriginalOf(pdf)!, "Ada");
        await using var __ = doc2;
        Assert.False(restored.IsDirty);
        Assert.True(restored.Restore(read));
        Assert.True(restored.IsDirty);
        Assert.Equal(json, restored.Snapshot("content://provider/doc.pdf", hash).ToJson());
        Assert.Same(restored.Store.FromFile.Single(a => a.Id == "sq"), restored.Store.Find("sq"));
        Assert.Equal("Accepted", restored.ReviewStateOf("sq"));
        Assert.Equal("New note", restored.Store.Find(note.Id)!.Contents);

        // Undo goes back to the file as opened; the edits are written and saved as an incremental update.
        var (doc3, fresh) = await Start(pdf, "Ada");
        await using var ___ = doc3;
        Assert.True(fresh.Restore(read));
        Assert.True(fresh.Undo());
        Assert.Equal(fresh.Store.FromFile, fresh.Store.Current);
        Assert.True(fresh.Redo());
        Assert.True((await fresh.ApplyAsync()).Written);
        var save = await fresh.BeginSaveAsync();
        Assert.NotNull(save);
        Assert.True(save.Bytes.AsSpan(0, pdf.Length).SequenceEqual(pdf));
        using var back = await PdfVectorDocument.OpenAsync(save.Bytes);
        Assert.Equal("Q, changed", PdfAnnotationReader.Read(back, 1).Single(p => p.Name == "q").Contents);
    }

    [Fact]
    public void KeptEdits_FitOnlyTheBytesTheyWereMadeOn_OrThoseBytesWithAnUpdateAfterThem()
    {
        byte[] pdf = Blank();
        var kept = new ViewerEditSnapshot { Location = "content://x", OriginalLength = pdf.Length, OriginalHash = ViewerEditSnapshot.Hash(pdf) };
        Assert.Same(pdf, kept.OriginalOf(pdf));
        byte[] saved = [.. pdf, .. "\n% an incremental update\n"u8.ToArray()];
        Assert.Equal(pdf, kept.OriginalOf(saved));
        byte[] changed = (byte[])pdf.Clone();
        changed[20] ^= 1;
        Assert.Null(kept.OriginalOf(changed));
        Assert.Null(kept.OriginalOf(pdf[..^1]));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"location\":\"x\",\"length\":1,\"sha256\":\"00\",\"annotations\":[]}")]
    [InlineData("{\"version\":1,\"location\":\"x\",\"length\":1,\"sha256\":\"00\",\"annotations\":[{\"id\":\"a\",\"kind\":\"Nothing\"}]}")]
    [InlineData("{\"version\":1,\"location\":\"x\",\"length\":1,\"sha256\":\"00\",\"annotations\":[5]}")]
    public void DamagedKeptEdits_AreRefused(string json) => Assert.Throws<FormatException>(() => ViewerEditSnapshot.FromJson(json));

    [Fact]
    public async Task KeptEdits_ThatFailTheStoresChecks_ChangeNothing()
    {
        byte[] pdf = Blank();
        var (doc, session) = await Start(pdf, "Ada");
        await using var _ = doc;
        var orphan = new ViewerAnnotation
        {
            Id = "r", Page = 1, Kind = ViewerAnnotationKind.Reply, Box = new ViewerBox(10, 10, 30, 30), InReplyTo = "nobody", Contents = "lost",
        };
        var snapshot = new ViewerEditSnapshot
        {
            Location = "content://x", OriginalLength = pdf.Length, OriginalHash = ViewerEditSnapshot.Hash(pdf),
            Annotations = [new ViewerSnapshotItem(null, orphan)],
        };
        var read = ViewerEditSnapshot.FromJson(snapshot.ToJson());
        Assert.Throws<ViewerAnnotationException>(() => session.Restore(read));
        Assert.Empty(session.Store.Current);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task KeptFormValues_ComeBack_AndAreWritten()
    {
        byte[] pdf = FormPdfFixture.Build();
        var (doc, session) = await Start(pdf, "");
        await using var _ = doc;
        Assert.True(session.SetFieldValue(session.Widgets.First(w => w.Name == "person.name"), ["Ada Lovelace"], out string? none));
        Assert.Null(none);
        string json = session.Snapshot("content://x", ViewerEditSnapshot.Hash(pdf)).ToJson();

        var (doc2, restored) = await Start(pdf, "");
        await using var __ = doc2;
        Assert.True(restored.Restore(ViewerEditSnapshot.FromJson(json)));
        Assert.Equal(["Ada Lovelace"], restored.ValueOf("person.name"));
        Assert.True((await restored.ApplyAsync()).Written);
        Assert.Equal("Ada Lovelace", restored.Widgets.First(w => w.Name == "person.name").Value);
    }
}
