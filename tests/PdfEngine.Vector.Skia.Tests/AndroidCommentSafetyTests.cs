using System.Buffers;
using System.Text;
using System.Text.Json;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The edges of comment threads, XFDF and kept edits: hostile XFDF, oversized snapshots, deleting a
/// comment with other people's replies, who owns a reply, a visible reply with /State, a rectangle
/// that is not finite, the embed SDK's status field, and kept edits after a save (no duplicates).
/// Every PDF is built here.
/// </summary>
public class AndroidCommentSafetyTests
{
    private const string Ns = "http://ns.adobe.com/xfdf/";

    /// <summary>A Letter page with a note (q) and Bob's reply to it (a).</summary>
    private static byte[] Commented(string replyEntries = "/F 28")
    {
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        b.Add("<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (q) /Contents (Q) /F 4 >>");
        b.Add($"<< /Type /Annot /Subtype /Text /Rect [100 600 120 620] /NM (a) /IRT 4 0 R /RT /R /Contents (A) /T (Bob) {replyEntries} >>");
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Annots [4 0 R 5 0 R] >>");
        return b.Build();
    }

    private static byte[] Blank()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    private static async Task<(ViewerDocument Doc, ViewerEditSession Session)> Start(byte[] pdf, string author)
    {
        var doc = await ViewerDocument.OpenAsync(pdf);
        return (doc, await ViewerEditSession.StartAsync(doc, author));
    }

    // ------------------------------------------------------------------ hostile XFDF

    [Fact]
    public async Task HostileXfdf_IsRefusedOrSkipped_AndNoActionIsWritten()
    {
        var (doc, session) = await Start(Blank(), "Ada");
        await using var _ = doc;

        // A DTD (and with it any external entity) is refused before anything is read.
        Assert.Throws<ViewerAnnotationException>(() => session.ImportXfdf(
            $"<?xml version=\"1.0\"?><!DOCTYPE xfdf [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><xfdf xmlns=\"{Ns}\"><annots>" +
            "<text page=\"0\" rect=\"10,10,30,30\"><contents>&x;</contents></text></annots></xfdf>"));
        Assert.Throws<ViewerAnnotationException>(() => session.ImportXfdf(
            $"<!DOCTYPE xfdf SYSTEM \"http://example.invalid/x.dtd\"><xfdf xmlns=\"{Ns}\"><annots/></xfdf>"));
        // Longer than the engine reads.
        Assert.Throws<ViewerAnnotationException>(() => session.ImportXfdf(new string(' ', PdfAnnotationXfdf.MaxCharacters + 1)));
        Assert.Empty(session.Store.Current);

        // Coordinates that are not finite are skipped; an action in the XFDF is not taken.
        var result = session.ImportXfdf($"<xfdf xmlns=\"{Ns}\"><annots>" +
            "<square page=\"0\" rect=\"NaN,10,30,30\"/><square page=\"0\" rect=\"10,10,Infinity,30\"/>" +
            "<ink page=\"0\" rect=\"10,10,30,30\"><inklist><gesture>1,1;NaN,2</gesture></inklist></ink>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"act\"><contents>Hi</contents>" +
            "<onactivation><action><launch><file>calc.exe</file></launch></action><action><javascript>app.alert(1)</javascript></action></onactivation></text>" +
            "</annots></xfdf>");
        Assert.Equal(1, result.Added);
        Assert.True(result.Skipped >= 2);
        Assert.Equal("Hi", session.Store.Find("act")!.Contents);
        Assert.True((await session.ApplyAsync()).Written);
        string written = Encoding.Latin1.GetString(doc.Bytes);
        Assert.DoesNotContain("/Launch", written);
        Assert.DoesNotContain("/JavaScript", written);
        Assert.DoesNotContain("calc.exe", written);
    }

    [Fact]
    public async Task ImportedRepliesInTheReadersName_AreNotTheirOwn()
    {
        var (doc, session) = await Start(Blank(), "Ada");
        await using var _ = doc;
        session.ImportXfdf($"<xfdf xmlns=\"{Ns}\"><annots><text page=\"0\" rect=\"10,10,30,30\" name=\"q\" title=\"Ada\"/>" +
            "<text page=\"0\" rect=\"10,10,30,30\" name=\"r\" inreplyto=\"q\" title=\"Ada\"><contents>Not mine</contents></text></annots></xfdf>");
        Assert.Equal("Ada", session.Store.Find("r")!.Author);
        Assert.False(session.IsOwn(session.Store.Find("r")!));
        var mine = session.AddReply("q", "Mine")!;
        Assert.True(session.IsOwn(mine));
        // A viewer that kept the ids made on this device gives them back to the next session.
        var (again, next) = await Start(Blank(), "Ada");
        await using var __ = again;
        Assert.False(next.IsOwn(mine));
        next.MadeHere.Add(mine.Id);
        Assert.True(next.IsOwn(mine));
    }

    // ------------------------------------------------------------------ threads

    [Fact]
    public async Task DeletingAComment_NamesAndTakesOtherPeoplesReplies_AndUndoBringsThemBack()
    {
        var (doc, session) = await Start(Commented(), "Ada");
        await using var _ = doc;
        session.AddReply("a", "My answer to Bob");
        session.SetReviewState("q", "Accepted");
        Assert.Equal((2, 1), session.DeletedWith("q")); // Bob's reply and Ada's; the status is not counted
        Assert.Equal((1, 0), session.DeletedWith("a")); // Ada's own reply only
        Assert.True(session.Delete("q"));
        Assert.Empty(session.Store.Current);
        Assert.True(session.Undo());
        Assert.Equal(["q", "a"], session.Store.Current.Where(x => x.Author != "Ada").Select(x => x.Id));
        Assert.Equal("Accepted", session.ReviewStateOf("q"));
    }

    [Fact]
    public async Task AVisibleReplyWithAState_IsAnOrdinaryReply()
    {
        var (doc, session) = await Start(Commented("/F 28 /State (Accepted) /StateModel (Review)"), "Ada");
        await using var _ = doc;
        var a = session.Store.Find("a")!;
        Assert.False(a.IsStatus);
        Assert.Equal("", a.StateModel);
        Assert.Null(session.ReviewStateOf("q"));
        Assert.Equal(2, session.Thread("q").Count);
    }

    [Fact]
    public void ARectangleThatIsNotFinite_IsNotManaged()
    {
        PdfAnnotation Note(PdfRect r) => new() { PageNumber = 1, Subtype = "Text", Rect = r, Flags = 4 };
        Assert.NotNull(ViewerAnnotationPdf.FromPdf(Note(new PdfRect(10, 10, 20, 20)), "n"));
        Assert.Null(ViewerAnnotationPdf.FromPdf(Note(new PdfRect(10, 10, double.NaN, 20)), "n"));
        Assert.Null(ViewerAnnotationPdf.FromPdf(Note(new PdfRect(10, 10, 20, double.PositiveInfinity)), "n"));
        Assert.Null(ViewerAnnotationPdf.FromPdf(Note(new PdfRect(1e308, 10, double.MaxValue, 20)), "n"));
    }

    // ------------------------------------------------------------------ the embed SDK's status field

    [Fact]
    public void EmbedJson_KeepsAStatus_BothWays()
    {
        var status = new ViewerAnnotation
        {
            Id = "s1", Page = 1, Kind = ViewerAnnotationKind.Reply, Box = default, InReplyTo = "q",
            State = "Accepted", StateModel = "Review", Contents = "Accepted set by Bo", Author = "Bo",
        };
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(w, status, "doc");
        using var json = JsonDocument.Parse(buffer.WrittenMemory);
        Assert.Equal("Accepted", json.RootElement.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("Review", json.RootElement.GetProperty("status").GetProperty("stateModel").GetString());
        var back = EmbedAnnotationJson.Parse(json.RootElement, 1, "Host", DateTimeOffset.UtcNow, requireId: true);
        Assert.True(back.IsStatus);
        Assert.Equal(("Accepted", "Review", "q"), (back.State, back.StateModel, back.InReplyTo));

        // An ordinary reply has no status field; a status name past 64 characters is refused, not cut.
        buffer.Clear();
        using (var w = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(w, status with { State = "", StateModel = "" }, "doc");
        using (var plain = JsonDocument.Parse(buffer.WrittenMemory)) Assert.False(plain.RootElement.TryGetProperty("status", out _));
        using var bad = JsonDocument.Parse("{\"id\":\"s2\",\"motivation\":\"replying\",\"target\":\"q\",\"status\":{\"state\":\"" + new string('x', 65) + "\",\"stateModel\":\"Review\"}}");
        Assert.Throws<EmbedProtocolException>(() => EmbedAnnotationJson.Parse(bad.RootElement, 1, "Host", DateTimeOffset.UtcNow, requireId: true));
    }

    [Fact]
    public async Task AStatus_StaysAStatus_WhenUpdated()
    {
        var (doc, session) = await Start(Commented(), "Ada");
        await using var _ = doc;
        var status = session.SetReviewState("q", "Rejected")!;
        Assert.Throws<ViewerAnnotationException>(() => session.Store.Update(status with { State = "", StateModel = "" }));
        Assert.Equal("Accepted", session.Store.Update(status with { State = "Accepted" }).State);
    }

    // ------------------------------------------------------------------ kept edits

    [Fact]
    public void OversizedKeptEdits_AreNotWritten_NorRead()
    {
        Assert.Throws<FormatException>(() => ViewerEditSnapshot.FromJson(new string(' ', ViewerEditSnapshot.MaxBytes + 1)));
        var big = new ViewerAnnotation
        {
            Id = "n", Page = 1, Kind = ViewerAnnotationKind.Note, Box = new ViewerBox(0, 0, 20, 20),
            Contents = new string('é', ViewerEditSnapshot.MaxBytes / 2 + 1), // two UTF-8 bytes each
        };
        var snapshot = new ViewerEditSnapshot { Location = "x", OriginalLength = 1, OriginalHash = "00", Annotations = [new ViewerSnapshotItem(null, big)] };
        Assert.Null(snapshot.ToUtf8());
        Assert.NotNull((snapshot with { Annotations = [] }).ToUtf8());
    }

    /// <summary>
    /// Save, add another note, Android ends the app, the file is opened again from the saved state
    /// and saved: each note is in the file once. The kept edits fit the bytes as first opened, which
    /// are the start of the saved file.
    /// </summary>
    [Fact]
    public async Task SaveThenMoreEdits_KeptAndGivenBack_AreSavedWithoutDuplicates()
    {
        byte[] pdf = Blank();
        var (doc, session) = await Start(pdf, "Ada");
        await using var _ = doc;
        var first = session.AddNote(1, new ViewerPoint(50, 50), "First");
        var save = await session.BeginSaveAsync();
        Assert.NotNull(save);
        session.EndSave(save, saved: true);
        byte[] onDisk = save.Bytes;
        var second = session.AddNote(1, new ViewerPoint(150, 150), "Second");
        // The hash and the length are both of the bytes as opened (OriginalBytes), not of the saved file.
        var kept = ViewerEditSnapshot.FromJson(session.Snapshot("content://x", ViewerEditSnapshot.Hash(doc.OriginalBytes)).ToJson());
        Assert.Equal(pdf.LongLength, kept.OriginalLength);

        // The new process reads the saved file: the kept edits fit its start.
        byte[] original = kept.OriginalOf(onDisk)!;
        Assert.Equal(pdf, original);
        var (doc2, restored) = await Start(original, "Ada");
        await using var __ = doc2;
        Assert.True(restored.Restore(kept));
        var again = await restored.BeginSaveAsync();
        Assert.NotNull(again);
        using var back = await PdfVectorDocument.OpenAsync(again.Bytes);
        var names = PdfAnnotationReader.Read(back, 1).Select(p => p.Name).ToList();
        Assert.Equal(1, names.Count(n => n == first.Id));
        Assert.Equal(1, names.Count(n => n == second.Id));
        Assert.Equal(2, names.Count);

        // Given to the saved file itself (it has the first note already), the edits still write each note once.
        var (doc3, onSaved) = await Start(onDisk, "Ada");
        await using var ___ = doc3;
        Assert.True(onSaved.Restore(kept));
        Assert.Equal(1, onSaved.Store.Current.Count(a => a.Id == first.Id));
        var third = await onSaved.BeginSaveAsync();
        using var back3 = await PdfVectorDocument.OpenAsync(third!.Bytes);
        var names3 = PdfAnnotationReader.Read(back3, 1).Select(p => p.Name).ToList();
        Assert.Equal(1, names3.Count(n => n == first.Id));
        Assert.Equal(1, names3.Count(n => n == second.Id));
    }
}
