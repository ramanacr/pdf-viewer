using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Turning a stamp with its rotation handle (the Android reader, PageView and MainActivity.Stamps.cs),
/// through the shared <see cref="ViewerEditSession.TurnStamp"/>: the turn snaps to 15 degrees within 4,
/// the stamp keeps its own size and centre, its box becomes the turned stamp's, one undo step, the
/// appearance is turned by its form's /Matrix when saved (new stamps and a file's), resizing keeps
/// the turn, and kept edits keep it.
/// </summary>
public class AndroidStampTurnTests
{
    private static byte[] Pages(int count, int rotate = 0)
    {
        var b = new VectorPdfBuilder();
        for (int i = 0; i < count; i++) b.AddPage("", mediaBox: "[0 0 612 792]", extra: rotate != 0 ? $"/Rotate {rotate}" : "");
        return b.Build();
    }

    private static async Task<(ViewerDocument Doc, ViewerEditSession Session)> Start(byte[] pdf)
    {
        var doc = await ViewerDocument.OpenAsync(pdf);
        return (doc, await ViewerEditSession.StartAsync(doc, author: "Reader"));
    }

    private static async Task<byte[]> Copy(ViewerEditSession session)
    {
        byte[]? copy = null;
        var result = await ViewerSaveBack.CopyAsync(session, bytes =>
        {
            copy = bytes.ToArray();
            return Task.FromResult<long?>(bytes.Length);
        });
        Assert.True(result.Saved);
        return copy!;
    }

    private static async Task<PdfAnnotation> ReadStamp(byte[] bytes, int page = 1)
    {
        using var vector = await PdfVectorDocument.OpenAsync(bytes);
        return Assert.Single(PdfAnnotationReader.Read(vector, page), p => p.Subtype == "Stamp");
    }

    private static (double X, double Y) Centre(ViewerBox b) => ((b.X1 + b.X2) / 2, (b.Y1 + b.Y2) / 2);

    [Theory]
    [InlineData(13, 15)]
    [InlineData(10, 10)]
    [InlineData(19.5, 19.5)]
    [InlineData(47, 45)]
    [InlineData(86.5, 90)]
    [InlineData(358, 0)]
    [InlineData(-2, 0)]
    [InlineData(-30, 330)]
    [InlineData(403, 45)]
    [InlineData(400, 40)]
    [InlineData(double.NaN, 0)]
    public void ATurn_SnapsToFifteenDegrees_WithinFour(double degrees, double snapped) =>
        Assert.Equal(snapped, ViewerEditSession.SnapStampTurn(degrees), 9);

    [Fact]
    public async Task ANewStamp_TurnsAboutItsCentre_AsOneUndoStep_AndIsSavedTurned()
    {
        var (doc, session) = await Start(Pages(1));
        await using var _ = doc;
        var a = session.AddStamp(1, new ViewerPoint(300, 400), PdfStamps.Standard[0]);
        var (w, h, d) = ViewerEditSession.StampFrame(a)!.Value;
        Assert.Equal((a.Box.Width, a.Box.Height, 0.0), (w, h, d));
        var centre = Centre(a.Box);

        Assert.True(session.TurnStamp(a.Id, 30));
        var t = session.Store.Find(a.Id)!;
        Assert.Equal((30.0, w, h), (t.StampRotation!.Value, t.StampWidth, t.StampHeight));
        var bounds = PdfStamps.RotatedBounds(new PdfEngine.Geometry.PdfPoint(centre.X, centre.Y), w, h, 30);
        Assert.Equal(bounds.Width, t.Box.Width, 6);
        Assert.Equal(bounds.Height, t.Box.Height, 6);
        Assert.Equal(centre.X, Centre(t.Box).X, 6);
        Assert.Equal(centre.Y, Centre(t.Box).Y, 6);
        // The outline drawn while it turns: the own rectangle's corners, turned.
        var corners = ViewerEditSession.StampCorners(t);
        Assert.Equal(4, corners.Count);
        Assert.Equal(w, Math.Sqrt(Math.Pow(corners[1].X - corners[0].X, 2) + Math.Pow(corners[1].Y - corners[0].Y, 2)), 6);
        Assert.Equal(30, Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X) * 180 / Math.PI, 6);

        // The same turn is no step; a full turn round is the same turn.
        Assert.False(session.TurnStamp(a.Id, 30));
        Assert.False(session.TurnStamp(a.Id, 390));
        Assert.False(session.TurnStamp(a.Id, double.NaN));

        // One undo step back to upright, redo turns it again.
        Assert.True(session.Undo());
        Assert.Null(session.Store.Find(a.Id)!.StampRotation);
        Assert.Equal(a.Box, session.Store.Find(a.Id)!.Box);
        Assert.True(session.Redo());
        Assert.Equal(30, session.Store.Find(a.Id)!.StampRotation);

        var copy = await Copy(session);
        var p = await ReadStamp(copy);
        Assert.Equal(30, p.AppearanceRotation, 3);
        Assert.Equal(w, p.AppearanceSize!.Value.Width, 2);
        Assert.Equal(h, p.AppearanceSize!.Value.Height, 2);
        Assert.Equal(PdfStamps.Standard[0].Name, p.Icon);
    }

    [Fact]
    public async Task AStampOnATurnedPage_TurnsFromItsUprightTurn()
    {
        var (doc, session) = await Start(Pages(1, rotate: 90));
        await using var _ = doc;
        var a = session.AddStamp(1, new ViewerPoint(400, 300), PdfStamps.Standard[2]);
        Assert.Equal(90, a.StampTurn);
        var (w, h, d) = ViewerEditSession.StampFrame(a)!.Value;
        // Upright on the page as shown: its own width runs along the box's height.
        Assert.Equal((a.Box.Height, a.Box.Width, 90.0), (w, h, d));
        Assert.True(session.TurnStamp(a.Id, 105));
        var copy = await Copy(session);
        var p = await ReadStamp(copy);
        Assert.Equal(105, p.AppearanceRotation, 3);
        Assert.Equal(w, p.AppearanceSize!.Value.Width, 2);
    }

    [Fact]
    public async Task AFilesStamp_IsTurnedFurther_KeepingItsAppearance()
    {
        var (doc, session) = await Start(Pages(1));
        var a = session.AddStamp(1, new ViewerPoint(300, 400), PdfStamps.Standard[1]);
        Assert.True(session.TurnStamp(a.Id, 45));
        var saved = await Copy(session);
        await doc.DisposeAsync();

        var (doc2, again) = await Start(saved);
        await using var _ = doc2;
        var read = Assert.Single(again.Store.Current);
        Assert.True(read.FromFile);
        Assert.Equal(45, read.StampRotation!.Value, 3);
        var (w, h, _) = ViewerEditSession.StampFrame(read)!.Value;
        Assert.Equal(a.Box.Width, w, 2);
        Assert.Equal(a.Box.Height, h, 2);
        Assert.True(again.TurnStamp(read.Id, 90));
        var copy = await Copy(again);
        var p = await ReadStamp(copy);
        Assert.Equal(90, p.AppearanceRotation, 2);
        Assert.Equal(w, p.AppearanceSize!.Value.Width, 1);
        Assert.Equal(h, p.AppearanceSize!.Value.Height, 1);
    }

    [Fact]
    public async Task ATurnNearTheEdge_StaysOnThePage_AndOnlyStampsTurn()
    {
        var (doc, session) = await Start(Pages(1));
        await using var _ = doc;
        var a = session.AddStamp(1, new ViewerPoint(5, 5), PdfStamps.Standard[0]);
        Assert.True(session.TurnStamp(a.Id, 60));
        var t = session.Store.Find(a.Id)!;
        var m = doc.Geometry[0].MediaBox;
        Assert.True(t.Box.X1 >= m.Left - 1e-6 && t.Box.Y2 <= m.Bottom + 1e-6);
        Assert.Equal(PdfStamps.RotatedBounds(default, t.StampWidth, t.StampHeight, 60).Width, t.Box.Width, 6);

        var note = session.AddNote(1, new ViewerPoint(300, 300), "Note");
        Assert.Throws<ViewerAnnotationException>(() => session.TurnStamp(note.Id, 30));
        Assert.Null(ViewerEditSession.StampFrame(note));
        Assert.Empty(ViewerEditSession.StampCorners(note));
    }

    [Fact]
    public async Task ResizingATurnedStamp_KeepsItsTurn_AndScalesItsOwnSize()
    {
        var (doc, session) = await Start(Pages(1));
        await using var _ = doc;
        var a = session.AddStamp(1, new ViewerPoint(300, 400), PdfStamps.Standard[0]);
        Assert.True(session.TurnStamp(a.Id, 30));
        var t = session.Store.Find(a.Id)!;
        var bigger = new ViewerBox(t.Box.X1, t.Box.Y1, t.Box.X1 + t.Box.Width * 2, t.Box.Y1 + t.Box.Height * 2);
        // A host page's box without the turn keeps it too.
        var r = session.UpdateAnnotation(t with { Box = bigger, StampRotation = null, StampWidth = 0, StampHeight = 0 });
        Assert.Equal(30, r.StampRotation);
        Assert.Equal(t.StampWidth * 2, r.StampWidth, 6);
        Assert.Equal(t.StampHeight * 2, r.StampHeight, 6);
        Assert.Equal(PdfStamps.RotatedBounds(default, r.StampWidth, r.StampHeight, 30).Width, r.Box.Width, 6);
        var p = await ReadStamp(await Copy(session));
        Assert.Equal(30, p.AppearanceRotation, 3);
        Assert.Equal(r.StampWidth, p.AppearanceSize!.Value.Width, 2);
    }

    [Fact]
    public async Task KeptEdits_KeepTheTurn()
    {
        byte[] pdf = Pages(1);
        var (doc, session) = await Start(pdf);
        await using var _ = doc;
        var a = session.AddStamp(1, new ViewerPoint(300, 400), PdfStamps.Standard[0]);
        Assert.True(session.TurnStamp(a.Id, 75));
        string hash = ViewerEditSnapshot.Hash(pdf);
        var json = session.Snapshot("content://x", hash).ToJson();
        var read = ViewerEditSnapshot.FromJson(json);

        var (doc2, restored) = await Start(read.OriginalOf(pdf)!);
        await using var __ = doc2;
        Assert.True(restored.Restore(read));
        var back = restored.Store.Find(a.Id)!;
        Assert.Equal((75.0, session.Store.Find(a.Id)!.StampWidth), (back.StampRotation!.Value, back.StampWidth));
        Assert.Equal(json, restored.Snapshot("content://x", hash).ToJson());
    }
}
