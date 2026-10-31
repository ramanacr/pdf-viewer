using System.Text.Json;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Callout text boxes (free text with /IT /FreeTextCallout, /CL, /LE and /RD) in the shared editing
/// core of the web viewer, the embed SDK and the Android app: added as one undo step, written with
/// an appearance the engine draws (box, text, line and ending), read back from PDF and XFDF (as
/// Acrobat writes them), the box moved with the anchor kept, the anchor dragged with the knee laid
/// out again, invalid input refused, and a turned page. Every PDF is built here.
/// </summary>
public class CalloutTests
{
    private static byte[] Blank(string extra = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage("", mediaBox: "[0 0 612 792]", extra: extra);
        return b.Build();
    }

    /// <summary>A Letter page with a callout as Acrobat writes one: /Rect around the box and the line, /RD giving the box.</summary>
    private static byte[] WithAcrobatCallout()
    {
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        b.Add("<< /Type /Annot /Subtype /FreeText /IT /FreeTextCallout /Rect [90 490 300 600] /RD [110 40 0 0] "
              + "/CL [100 500 150 565 200 565] /LE /OpenArrow /DA (/Helv 12 Tf 1 0 0 rg) /BS << /W 1 >> /Contents (Look here) /NM (c1) /T (Bob) /F 4 >>");
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Annots [4 0 R] >>");
        return b.Build();
    }

    /// <summary>One page with the annotation dictionaries given (objects 4, 5, …).</summary>
    private static byte[] WithAnnots(string mediaBox, string pageExtra, params string[] annots)
    {
        var b = new VectorPdfBuilder();
        Assert.Equal(3, b.AddStream(string.Empty, string.Empty));
        foreach (var a in annots) b.Add(a);
        string refs = string.Join(" ", annots.Select((_, i) => $"{4 + i} 0 R"));
        b.AddPageObject($"<< /Type /Page /Parent {VectorPdfBuilder.PagesObject} 0 R /MediaBox {mediaBox} {pageExtra} /Contents 3 0 R /Annots [{refs}] >>");
        return b.Build();
    }

    private static ViewerPoint P(double x, double y) => new(x, y);

    private static void Near(double expected, double actual, double tolerance = 0.01) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");

    /// <summary>The darkest pixel (0 black, 255 white) within <paramref name="radius"/> pixels of (x, y).</summary>
    private static int Darkest(ViewerBitmap bitmap, double x, double y, int radius = 2)
    {
        int best = 255;
        for (int py = (int)y - radius; py <= (int)y + radius; py++)
        for (int px = (int)x - radius; px <= (int)x + radius; px++)
        {
            if (px < 0 || py < 0 || px >= bitmap.Width || py >= bitmap.Height) continue;
            int i = (py * bitmap.Width + px) * 4;
            var s = bitmap.Pixels.AsSpan();
            best = Math.Min(best, Math.Min(s[i], Math.Min(s[i + 1], s[i + 2])));
        }
        return best;
    }

    [Fact]
    public async Task AddCallout_IsOneUndoStep_AndIsWrittenWithItsAppearance()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[0];

        var c = session.AddCallout(1, P(100, 100), new ViewerRect(250, 200, 150, 50), "Look here");
        Assert.True(c.IsCallout);
        Assert.Equal(ViewerAnnotationKind.FreeText, c.Kind);
        Assert.Equal("Callout", ViewerEditSession.Label(c));
        Assert.Equal(ViewerCallout.DefaultEnding, c.StartEnding);
        Assert.Equal(ViewerEditSession.CalloutWidth, c.StrokeWidth);
        // The anchor first, a knee, then the middle of the box's left side (the side facing the anchor).
        Assert.Equal(6, c.Callout.Count);
        var anchor = g.ToShown(c.Callout[0], c.Callout[1]);
        var knee = g.ToShown(c.Callout[2], c.Callout[3]);
        var end = g.ToShown(c.Callout[4], c.Callout[5]);
        Near(100, anchor.X); Near(100, anchor.Y);
        Near(250, end.X); Near(225, end.Y);
        Near(250 - ViewerCallout.KneeLength, knee.X); Near(225, knee.Y);
        var box = g.ToShown(c.Box);
        Assert.Equal((250.0, 200.0, 150.0, 50.0), (box.X, box.Y, box.Width, box.Height));

        // One undo step, and redo gives it back.
        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Assert.Empty(session.Store.Current);
        Assert.False(session.CanUndo);
        Assert.True(session.Redo());
        Assert.Single(session.Store.Current);

        var before = await doc.RenderPageAsync(1, 1, false, asOpened: true);
        Assert.True((await session.ApplyAsync()).Written);
        using (var back = await PdfVectorDocument.OpenAsync(doc.Bytes))
        {
            var p = Assert.Single(PdfAnnotationReader.Read(back, 1));
            Assert.Equal(("FreeText", "FreeTextCallout"), (p.Subtype, p.Intent));
            Assert.Equal(c.Callout, p.CalloutLine!.Select(v => Math.Round(v, 4)).ToArray(), new ToleranceComparer());
            Assert.Equal(new[] { "OpenArrow" }, p.LineEndings!);
            Assert.True(p.HasAppearance);
            // /Rect holds the line and its arrow; /Rect less /RD is the text box.
            var rd = p.RectDifferences!;
            Near(c.Box.X1, p.Rect.Left + rd[0], 0.001);
            Near(c.Box.Y1, p.Rect.Top + rd[1], 0.001);
            Near(c.Box.X2, p.Rect.Right - rd[2], 0.001);
            Near(c.Box.Y2, p.Rect.Bottom - rd[3], 0.001);
            Assert.True(p.Rect.Left < c.Callout[0] && p.Rect.Bottom > c.Callout[1]);
        }

        // The engine draws the box, the line and the arrow: the line between the anchor and the knee is dark.
        var after = await doc.RenderPageAsync(1, 1);
        Assert.Equal(255, Darkest(before, 166, 162));
        Assert.True(Darkest(after, (100 + knee.X) / 2, (100 + knee.Y) / 2) < 160);
        Assert.True(Darkest(after, 325, 200) < 160); // the box's top edge
        Assert.True(Darkest(after, 104, 104) < 200); // the arrow at the anchor

        // Read back as the viewer reads a file: the same callout.
        await using var reopened = await ViewerDocument.OpenAsync(doc.Bytes.ToArray());
        var read = Assert.Single(await reopened.ReadAnnotationsAsync());
        Assert.True(read.IsCallout);
        Near(c.Box.X1, read.Box.X1, 0.001); Near(c.Box.Y2, read.Box.Y2, 0.001);
        Assert.Equal(c.Callout, read.Callout, new ToleranceComparer());
        Assert.Equal("OpenArrow", read.StartEnding);
        Assert.Equal("Look here", read.Contents);
    }

    [Fact]
    public async Task ACalloutFromTheFile_IsRead_AndMovingItsBoxKeepsTheAnchor()
    {
        await using var doc = await ViewerDocument.OpenAsync(WithAcrobatCallout());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var c = Assert.Single(session.Store.Current);
        Assert.True(c.IsCallout);
        Assert.Equal("c1", c.Id);
        Assert.Equal(new ViewerBox(200, 530, 300, 600), c.Box);
        Assert.Equal(new[] { 100.0, 500, 150, 565, 200, 565 }, c.Callout);
        Assert.Equal(("OpenArrow", "#FF0000"), (c.StartEnding, c.Color));
        Assert.Equal("Callout", Assert.Single(session.List()).Label);

        // A click on the text box finds it; a click on the anchor (outside the box) does not.
        var g = doc.Geometry[0];
        var inBox = g.ToShown(250, 560);
        Assert.Equal("c1", session.AnnotationAt(1, P(inBox.X, inBox.Y), 1)?.Id);

        // Moving the box 40 points right (shown) keeps the anchor; the line meets the box's left side again.
        Assert.True(session.Move("c1", 40, 0));
        var moved = session.Store.Find("c1")!;
        Assert.Equal(new ViewerBox(240, 530, 340, 600), moved.Box);
        Assert.Equal((100.0, 500.0), (moved.Callout[0], moved.Callout[1]));
        Near(240, moved.Callout[^2]);
        Near(565, moved.Callout[^1]);
        Assert.True(session.Undo());
        Assert.Equal(c, session.Store.Find("c1"));
        Assert.True(session.Redo());

        Assert.True((await session.ApplyAsync()).Written);
        using var back = await PdfVectorDocument.OpenAsync(doc.Bytes);
        var p = Assert.Single(PdfAnnotationReader.Read(back, 1));
        Assert.Equal((100.0, 500.0), (p.CalloutLine![0], p.CalloutLine[1]));
        Assert.Equal(("FreeTextCallout", "Look here"), (p.Intent, p.Contents));
        Assert.Equal(240, p.Rect.Left + p.RectDifferences![0], 3);
        Assert.Equal(340, p.Rect.Right - p.RectDifferences[2], 3);
    }

    [Fact]
    public async Task DraggingTheAnchor_IsOneUndoStep_AndLaysTheKneeOutAgain()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[0];
        var c = session.AddCallout(1, P(100, 100), new ViewerRect(250, 200, 150, 50), "Text");

        // Now the anchor is right of the box: the line leaves the box's right side.
        Assert.True(session.MoveCalloutAnchor(c.Id, P(560, 300)));
        var dragged = session.Store.Find(c.Id)!;
        Assert.Equal(c.Box, dragged.Box);
        var anchor = g.ToShown(dragged.Callout[0], dragged.Callout[1]);
        var knee = g.ToShown(dragged.Callout[2], dragged.Callout[3]);
        var end = g.ToShown(dragged.Callout[4], dragged.Callout[5]);
        Near(560, anchor.X); Near(300, anchor.Y);
        Near(400, end.X); Near(225, end.Y);
        Near(400 + ViewerCallout.KneeLength, knee.X); Near(225, knee.Y);

        // Below the box and straight under its middle: no knee, the line goes up to the bottom side.
        Assert.True(session.MoveCalloutAnchor(c.Id, P(325, 500)));
        var below = session.Store.Find(c.Id)!;
        Assert.Equal(4, below.Callout.Count);
        var e2 = g.ToShown(below.Callout[2], below.Callout[3]);
        Near(325, e2.X); Near(250, e2.Y);

        // Each drag is one step; the same point is no step.
        Assert.False(session.MoveCalloutAnchor(c.Id, P(325, 500)));
        Assert.True(session.Undo());
        Assert.Equal(dragged, session.Store.Find(c.Id));
        Assert.True(session.Undo());
        Assert.Equal(c, session.Store.Find(c.Id));

        // Text editing follows the text box rules: an empty text is refused; a callout the reader
        // placed with a click grows to its text, and its line meets the box again.
        Assert.Throws<ViewerAnnotationException>(() => session.SetContents(c.Id, "   "));
        var click = session.AddCallout(1, P(50, 600), new ViewerRect(300, 500, 0, 0), "One");
        Assert.True(session.SetContents(click.Id, "One\nTwo\nThree\nFour"));
        var grown = session.Store.Find(click.Id)!;
        Assert.True(grown.Box.Height > click.Box.Height);
        var gEnd = g.ToShown(grown.Callout[^2], grown.Callout[^1]);
        var gBox = g.ToShown(grown.Box);
        Near(gBox.X, gEnd.X); Near(gBox.Y + gBox.Height / 2, gEnd.Y);
    }

    [Fact]
    public async Task InvalidInput_IsRefused()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var box = new ViewerRect(250, 200, 150, 50);
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(1, P(double.NaN, 10), box, "x"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(1, P(10, double.PositiveInfinity), box, "x"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(1, P(10, 10), new ViewerRect(double.NaN, 1, 10, 10), "x"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(1, P(10, 10), new ViewerRect(1, 1, double.PositiveInfinity, 10), "x"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(1, P(-5, 10), box, "x"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(1, P(10, 800), box, "x"));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCallout(2, P(10, 10), box, "x"));
        Assert.Empty(session.Store.Current);
        Assert.False(session.CanUndo);

        var c = session.AddCallout(1, P(10, 10), box, "x");
        Assert.Throws<ViewerAnnotationException>(() => session.MoveCalloutAnchor(c.Id, P(double.NaN, 5)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveCalloutAnchor(c.Id, P(700, 5)));
        Assert.Throws<ViewerAnnotationException>(() => session.MoveCalloutAnchor("nothing", P(5, 5)));
        var note = session.AddNote(1, P(400, 400), "n");
        Assert.Throws<ViewerAnnotationException>(() => session.MoveCalloutAnchor(note.Id, P(5, 5)));

        // The store refuses a callout point off the page, a line of the wrong length, a callout on
        // another kind, and a callout line without width (it would not be drawn).
        var good = c with { Id = "" };
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([good with { Callout = [-100, 10, 250, 225] }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([good with { Callout = [10, 10, 250] }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([good with { Kind = ViewerAnnotationKind.Square }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([good with { StrokeWidth = 0 }]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([good with { StartEnding = "Arrowish" }]));

        // The SDK's JSON: callout is 2, 4 or 6 numbers, calloutEnding a line ending name.
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1,2,3]"));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1,2,3,4],\"calloutEnding\":\"Spear\""));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":\"1,2\""));
    }

    private static ViewerAnnotation Parse(string calloutJson)
    {
        string json = "{\"type\":\"Annotation\",\"motivation\":\"commenting\",\"bodyValue\":\"Hi\",\"target\":{\"selector\":{\"node\":{\"index\":0},"
                      + "\"subtype\":\"freetext\",\"boundingBox\":[300,500,450,550]," + calloutJson + "}}}";
        using var d = JsonDocument.Parse(json);
        return EmbedAnnotationJson.Parse(d.RootElement, 1, "Ada", DateTimeOffset.UtcNow, requireId: false);
    }

    [Fact]
    public async Task TheEmbedJson_AddsGetsUpdatesAndDeletesCallouts()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");

        // Only the anchor: the viewer lays out the line to the box, with an open arrow.
        var given = Parse("\"callout\":[100,700]");
        var added = Assert.Single(session.AddAnnotations([given]));
        Assert.True(added.IsCallout);
        Assert.Equal(6, added.Callout.Count);
        Assert.Equal((100.0, 700.0), (added.Callout[0], added.Callout[1]));
        Assert.Equal("OpenArrow", added.StartEnding);

        // getAnnotations gives callout and calloutEnding; given back, it is the same callout.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(w, added, "file");
        using var written = JsonDocument.Parse(buffer.WrittenMemory);
        var selector = written.RootElement.GetProperty("target").GetProperty("selector");
        Assert.Equal("freetext", selector.GetProperty("subtype").GetString());
        Assert.Equal(6, selector.GetProperty("callout").GetArrayLength());
        Assert.Equal("OpenArrow", selector.GetProperty("calloutEnding").GetString());
        var again = EmbedAnnotationJson.Parse(written.RootElement, 1, "Ada", DateTimeOffset.UtcNow, requireId: true);
        Assert.Equal(added.Callout, again.Callout, new ToleranceComparer());

        // An update that moves the box and gives the line back as it was keeps the anchor.
        var update = again with { Box = again.Box.Offset(0, -100), Contents = "Moved" };
        var updated = session.UpdateAnnotation(update);
        Assert.Equal((100.0, 700.0), (updated.Callout[0], updated.Callout[1]));
        // The anchor is now farther above the box than beside it: the line meets the top side's middle.
        Near(updated.Box.X1 + updated.Box.Width / 2, updated.Callout[^2]);
        Near(updated.Box.Y2, updated.Callout[^1]);
        // Another ending, given in full.
        updated = session.UpdateAnnotation(updated with { StartEnding = "Circle" });
        Assert.Equal("Circle", updated.StartEnding);

        Assert.True((await session.ApplyAsync()).Written);
        using (var back = await PdfVectorDocument.OpenAsync(doc.Bytes))
            Assert.Equal(new[] { "Circle" }, Assert.Single(PdfAnnotationReader.Read(back, 1)).LineEndings!);

        Assert.Single(session.DeleteAnnotations(new ViewerAnnotationFilter(new HashSet<string> { added.Id })));
        Assert.Empty(session.Store.Current);
    }

    [Fact]
    public async Task Xfdf_RoundTrip_AndAcrobatsCallout()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var c = session.AddCallout(1, P(100, 100), new ViewerRect(250, 200, 150, 50), "Look here");
        string xfdf = session.ExportXfdf("x.pdf");
        Assert.Contains("callout=\"", xfdf);
        Assert.Contains("head=\"OpenArrow\"", xfdf);
        Assert.Contains("intent=\"FreeTextCallout\"", xfdf);
        Assert.Contains("fringe=\"", xfdf);

        await using var other = await ViewerDocument.OpenAsync(Blank());
        var into = await ViewerEditSession.StartAsync(other, "Ada");
        var import = into.ImportXfdf(xfdf);
        Assert.Equal((1, 0), (import.Added, import.Skipped));
        var read = into.Store.Current[0];
        Assert.True(read.IsCallout);
        Assert.Equal(c.Callout, read.Callout, new ToleranceComparer());
        Near(c.Box.X1, read.Box.X1, 0.001); Near(c.Box.Y1, read.Box.Y1, 0.001);
        Near(c.Box.X2, read.Box.X2, 0.001); Near(c.Box.Y2, read.Box.Y2, 0.001);
        Assert.Equal("OpenArrow", read.StartEnding);

        // A callout as Acrobat exports it (rect around box and line; fringe is /RD).
        const string acrobat = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve"><annots>
            <freetext page="0" rect="90,490,300,600" callout="100,500,150,565,200,565" fringe="110,40,0,0" head="OpenArrow"
                      intent="FreeTextCallout" width="1" name="acro-1" title="Bob" color="#FF0000">
              <contents>From Acrobat</contents><defaultappearance>/Helv 12 Tf 0 0 1 rg</defaultappearance>
            </freetext>
            <freetext page="0" rect="10,10,100,40" callout="1,2,3" name="bad-line"><contents>No line</contents></freetext>
            </annots></xfdf>
            """;
        var parsed = PdfAnnotationXfdf.Import(acrobat);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(new[] { 100.0, 500, 150, 565, 200, 565 }, parsed[0].CalloutLine!);
        Assert.Equal(new[] { "OpenArrow" }, parsed[0].LineEndings!);
        Assert.Equal("FreeTextCallout", parsed[0].Intent);
        Assert.Equal(new[] { 110.0, 40, 0, 0 }, parsed[0].RectDifferences!);
        Assert.Null(parsed[1].CalloutLine); // a line of three numbers is left out, not the text box

        await using var third = await ViewerDocument.OpenAsync(Blank());
        var s3 = await ViewerEditSession.StartAsync(third, "Ada");
        Assert.Equal(2, s3.ImportXfdf(acrobat).Added);
        var fromAcrobat = s3.Store.Find("acro-1")!;
        Assert.True(fromAcrobat.IsCallout);
        Assert.Equal(new ViewerBox(200, 530, 300, 600), fromAcrobat.Box);
        Assert.False(s3.Store.Find("bad-line")!.IsCallout);

        // Written into the PDF and read back, it is still Acrobat's callout.
        Assert.True((await s3.ApplyAsync()).Written);
        using var back = await PdfVectorDocument.OpenAsync(third.Bytes);
        var p = PdfAnnotationReader.Read(back, 1).Single(a => a.Name == "acro-1");
        Assert.Equal(new[] { 100.0, 500, 150, 565, 200, 565 }, p.CalloutLine!);
        Assert.True(p.HasAppearance);
    }

    [Fact]
    public async Task OnATurnedPage_TheLineAndTheBoxAreWhereTheReaderPutThem()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank("/Rotate 90"));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var g = doc.Geometry[0];
        Assert.Equal((792.0, 612.0), (g.ShownWidth, g.ShownHeight));

        var c = session.AddCallout(1, P(60, 60), new ViewerRect(300, 200, 200, 60), "Turned");
        var shownBox = g.ToShown(c.Box);
        Assert.Equal((300.0, 200.0, 200.0, 60.0), (shownBox.X, shownBox.Y, shownBox.Width, shownBox.Height));
        // The knee leaves the box sideways as shown (to the left, the anchor's side).
        var knee = g.ToShown(c.Callout[2], c.Callout[3]);
        var end = g.ToShown(c.Callout[4], c.Callout[5]);
        Near(300, end.X); Near(230, end.Y);
        Near(300 - ViewerCallout.KneeLength, knee.X); Near(230, knee.Y);

        Assert.True((await session.ApplyAsync()).Written);
        var bitmap = await doc.RenderPageAsync(1, 1);
        Assert.Equal((792, 612), (bitmap.Width, bitmap.Height));
        // The line, the box's top and bottom edges, and the arrow are drawn where they are shown.
        Assert.True(Darkest(bitmap, (60 + knee.X) / 2, (60 + knee.Y) / 2) < 160);
        Assert.True(Darkest(bitmap, 400, 200) < 160);
        Assert.True(Darkest(bitmap, 400, 260) < 160);
        Assert.True(Darkest(bitmap, 64, 64) < 200);
        // Nothing is drawn where the box would be if /RD were taken in the turned form's axes.
        Assert.Equal(255, Darkest(bitmap, 400, 150, 1));

        // Moved down on the shown page, the anchor stays and the knee still leaves sideways.
        Assert.True(session.Move(c.Id, 0, 100));
        var moved = session.Store.Find(c.Id)!;
        Assert.Equal((c.Callout[0], c.Callout[1]), (moved.Callout[0], moved.Callout[1]));
        var mEnd = g.ToShown(moved.Callout[^2], moved.Callout[^1]);
        Near(300, mEnd.X); Near(330, mEnd.Y);

        await using var reopened = await ViewerDocument.OpenAsync(doc.Bytes.ToArray());
        var read = Assert.Single(await reopened.ReadAnnotationsAsync());
        Near(c.Box.X1, read.Box.X1, 0.001); Near(c.Box.Y2, read.Box.Y2, 0.001);
    }

    [Fact]
    public async Task KeptEdits_KeepTheCallout()
    {
        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var c = session.AddCallout(1, P(100, 100), new ViewerRect(250, 200, 150, 50), "Kept");
        var snapshot = session.Snapshot("content://x", "hash");
        var back = ViewerEditSnapshot.FromJson(snapshot.ToJson());
        var item = Assert.Single(back.Annotations);
        Assert.Equal(c.Callout, item.Annotation!.Callout);
        Assert.Equal("OpenArrow", item.Annotation.StartEnding);
    }

    private const string Letter = "[0 0 612 792]";

    /// <summary>A callout in Rect [90 490 300 600] whose line comes from below (the box is at the top) or above (the box is at the bottom).</summary>
    private static string FileCallout(bool lineAbove, string name = "c", string extra = "/BS << /W 1 >>") => lineAbove
        ? $"<< /Type /Annot /Subtype /FreeText /IT /FreeTextCallout /Rect [90 490 300 600] /RD [110 0 0 40] /CL [100 590 150 525 200 525] /LE /OpenArrow /DA (/Helv 12 Tf 0 0 0 rg) {extra} /Contents (Box) /NM ({name}) /F 4 >>"
        : $"<< /Type /Annot /Subtype /FreeText /IT /FreeTextCallout /Rect [90 490 300 600] /RD [110 40 0 0] /CL [100 500 150 565 200 565] /LE /OpenArrow /DA (/Helv 12 Tf 0 0 0 rg) {extra} /Contents (Box) /NM ({name}) /F 4 >>";

    /// <summary>
    /// /RD is left, bottom, right, top insets: the order of /Rect's numbers, as Acrobat writes it (see
    /// the next test) and PDFBox and MuPDF read it. With the line above the box and below it, the box
    /// is read where Acrobat put it, and drawn there; read as left, top, right, bottom it would be elsewhere.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RdIsLeftBottomRightTop_WithTheLineAboveOrBelowTheBox(bool lineAbove)
    {
        await using var doc = await ViewerDocument.OpenAsync(WithAnnots(Letter, "", FileCallout(lineAbove)));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var c = Assert.Single(session.Store.Current);
        var box = lineAbove ? new ViewerBox(200, 490, 300, 560) : new ViewerBox(200, 530, 300, 600);
        Assert.Equal(box, c.Box);
        // The file's line ends on the middle of the box's left side: only this reading puts it there.
        Assert.Equal(box.Y1 + box.Height / 2, c.Callout[^1]);

        Assert.True(session.SetColor(c.Id, "#E5484D"));
        Assert.True((await session.ApplyAsync()).Written);
        using (var back = await PdfVectorDocument.OpenAsync(doc.Bytes))
        {
            var p = Assert.Single(PdfAnnotationReader.Read(back, 1));
            var rd = p.RectDifferences!;
            Assert.Equal(box, new ViewerBox(p.Rect.Left + rd[0], p.Rect.Top + rd[1], p.Rect.Right - rd[2], p.Rect.Bottom - rd[3]));
        }
        var bitmap = await doc.RenderPageAsync(1, 1);
        Assert.True(Darkest(bitmap, 250, 792 - box.Y2) < 160); // the box's top edge
        Assert.True(Darkest(bitmap, 250, 792 - box.Y1) < 160); // and its bottom edge
        // Where the box's edge would be read the other way round: nothing.
        double wrongEdge = lineAbove ? 600 : 490;
        Assert.Equal(255, Darkest(bitmap, 250, 792 - wrongEdge, 1));
    }

    /// <summary>
    /// Numbers from an XFDF document Acrobat wrote (quoted in Apache PDFBox issue PDFBOX-3164): the
    /// line starts at rect's top-left corner, and its end is the middle of the text box's left side
    /// only when fringe (/RD) is left, bottom, right, top.
    /// </summary>
    [Fact]
    public async Task AcrobatsOwnXfdf_PutsTheTextBoxWhereItsLineEnds()
    {
        const string xfdf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/" xml:space="preserve"><annots>
            <freetext page="0" rect="356.7395,1269.4126,704.3984,1471.7811" callout="356.7395,1471.7811,479.2464,1372.3664,494.2464,1327.1395"
                      fringe="137.5069,-0.0,0.0,86.9147" head="OpenArrow" intent="FreeTextCallout" name="acrobat" width="1"><contents>Note</contents></freetext>
            </annots></xfdf>
            """;
        await using var doc = await ViewerDocument.OpenAsync(WithAnnots("[0 0 1224 1584]", "", "<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] /F 2 >>"));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        Assert.Equal(1, session.ImportXfdf(xfdf).Added);
        var a = session.Store.Find("acrobat")!;
        Assert.True(a.IsCallout);
        Near(494.2464, a.Box.X1, 0.001);
        Near(704.3984, a.Box.X2, 0.001);
        Near(1269.4126, a.Box.Y1, 0.001);
        Near(1471.7811 - 86.9147, a.Box.Y2, 0.001);
        Near(a.Box.Y1 + a.Box.Height / 2, a.Callout[^1], 0.001);
        Near(a.Box.X1, a.Callout[^2], 0.001);
    }

    /// <summary>A square's uneven /RD is drawn left, bottom, right, top; it and a plain text box's /RD survive an edit.</summary>
    [Fact]
    public async Task ASquaresUnevenRd_IsDrawnInPlace_AndRdSurvivesAnEdit()
    {
        await using var doc = await ViewerDocument.OpenAsync(WithAnnots(Letter, "",
            "<< /Type /Annot /Subtype /Square /Rect [100 100 300 300] /RD [10 20 30 60] /C [1 0 0] /BS << /W 2 >> /NM (sq) /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Rect [350 100 550 200] /RD [5 6 7 8] /DA (/Helv 12 Tf 0 0 0 rg) /Contents (Plain) /NM (ft) /F 4 >>"));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        Assert.False(session.Store.Find("ft")!.IsCallout);
        Assert.Equal(new ViewerBox(350, 100, 550, 200), session.Store.Find("ft")!.Box); // a plain text box's box is its /Rect
        Assert.True(session.SetColor("sq", "#2563EB"));
        Assert.True(session.SetColor("ft", "#2563EB"));
        Assert.True((await session.ApplyAsync()).Written);
        using (var back = await PdfVectorDocument.OpenAsync(doc.Bytes))
        {
            var read = PdfAnnotationReader.Read(back, 1);
            Assert.Equal(new[] { 10.0, 20, 30, 60 }, read.Single(p => p.Name == "sq").RectDifferences!);
            Assert.Equal(new[] { 5.0, 6, 7, 8 }, read.Single(p => p.Name == "ft").RectDifferences!);
        }
        // The square is x 110..270, y 120..240 (shown y 552..672).
        var bitmap = await doc.RenderPageAsync(1, 1);
        Assert.True(Darkest(bitmap, 110, 612) < 200);
        Assert.True(Darkest(bitmap, 270, 612) < 200);
        Assert.True(Darkest(bitmap, 190, 552) < 200);
        Assert.True(Darkest(bitmap, 190, 672) < 200);
        // Read left, top, right, bottom, it would be y 160..280 (shown 512..632).
        Assert.Equal(255, Darkest(bitmap, 190, 512, 1));
        Assert.Equal(255, Darkest(bitmap, 190, 632, 1));
    }

    /// <summary>
    /// /RD is in default user space: on a page turned 90, 180 or 270 degrees, and for free text with
    /// its own /Rotate (assumed the same: /RD is not turned with the text), the box is drawn where the
    /// page's axes put it.
    /// </summary>
    [Theory]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(90, true)]
    public async Task OnATurnedPageOrWithItsOwnRotate_RdIsInPageAxes(int rotate, bool own)
    {
        string annot = own ? FileCallout(false, extra: $"/BS << /W 1 >> /Rotate {rotate}") : FileCallout(false);
        await using var doc = await ViewerDocument.OpenAsync(WithAnnots(Letter, own ? "" : $"/Rotate {rotate}", annot));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        var c = Assert.Single(session.Store.Current);
        Assert.Equal(new ViewerBox(200, 530, 300, 600), c.Box);
        Assert.True(session.SetColor(c.Id, "#E5484D"));
        Assert.True((await session.ApplyAsync()).Written);
        var bitmap = await doc.RenderPageAsync(1, 1);
        var r = doc.Geometry[0].ToShown(c.Box);
        double mx = r.X + r.Width / 2, my = r.Y + r.Height / 2;
        Assert.True(Darkest(bitmap, r.X, my) < 160, "left edge");
        Assert.True(Darkest(bitmap, r.X + r.Width, my) < 160, "right edge");
        Assert.True(Darkest(bitmap, mx, r.Y) < 160, "top edge");
        Assert.True(Darkest(bitmap, mx, r.Y + r.Height) < 160, "bottom edge");
    }

    [Fact]
    public async Task AFileCalloutWithALineNotDrawnOrPointingOffThePage_StaysEditable()
    {
        await using var doc = await ViewerDocument.OpenAsync(WithAnnots(Letter, "",
            FileCallout(false, "thin", "/BS << /W 0 >>"),
            "<< /Type /Annot /Subtype /FreeText /IT /FreeTextCallout /Rect [-60 490 300 600] /RD [260 40 0 0] /CL [-50 500 150 565 200 565] /LE /OpenArrow "
            + "/DA (/Helv 12 Tf 0 0 0 rg) /BS << /W 1 >> /Contents (Off) /NM (off) /F 4 >>"));
        var session = await ViewerEditSession.StartAsync(doc, "Ada");

        // A line 0 wide (not drawn) is kept through a move and a text edit; moving its anchor draws it.
        Assert.Equal(0.0, session.Store.Find("thin")!.StrokeWidth);
        Assert.True(session.Move("thin", 10, 0));
        Assert.True(session.SetContents("thin", "New text"));
        Assert.Equal(0.0, session.Store.Find("thin")!.StrokeWidth);
        Assert.True(session.MoveCalloutAnchor("thin", P(50, 300)));
        Assert.Equal(ViewerEditSession.CalloutWidth, session.Store.Find("thin")!.StrokeWidth);

        // An anchor off the page stays where it is through a move and a text edit.
        var off = session.Store.Find("off")!;
        Assert.Equal(new ViewerBox(200, 530, 300, 600), off.Box);
        Assert.True(session.Move("off", 10, 0));
        Assert.True(session.SetContents("off", "Still off"));
        var moved = session.Store.Find("off")!;
        Assert.Equal((-50.0, 500.0), (moved.Callout[0], moved.Callout[1]));
        // A new anchor off the page is refused (a host page's too); one on the page is taken.
        Assert.Throws<ViewerAnnotationException>(() => session.UpdateAnnotation(moved with { Callout = [-40, 500, 210, 565] }));
        Assert.Throws<ViewerAnnotationException>(() => session.UpdateAnnotation(moved with { StrokeWidth = 0 }));
        Assert.True(session.MoveCalloutAnchor("off", P(20, 300)));
        Assert.True((await session.ApplyAsync()).Written);
    }

    [Fact]
    public void XfdfCallouts_AreReadSafely_AndRectHoldsTheLine()
    {
        static PdfAnnotation One(string attributes) => Assert.Single(PdfAnnotationXfdf.Import(
            $"<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots><freetext page=\"0\" rect=\"200,200,300,250\" {attributes}><contents>t</contents></freetext></annots></xfdf>"));
        Assert.Null(One("callout=\"NaN,1,2,3\"").CalloutLine);
        Assert.Null(One("callout=\"1e8,1,2,3\"").CalloutLine);
        Assert.Null(One("callout=\"1,2,3,4,5,6,7,8\"").CalloutLine);
        Assert.Null(One("callout=\"210,240,250,240\" fringe=\"-1,0,0,0\"").RectDifferences);
        Assert.Null(One("callout=\"210,240,250,240\" fringe=\"1,2,3\"").RectDifferences);
        Assert.Null(One("callout=\"210,240,250,240\" fringe=\"60,0,60,0\"").RectDifferences); // wider than rect
        Assert.Null(One("fringe=\"Infinity,0,0,0\"").RectDifferences);
        Assert.Null(One("callout=\"210,240,250,240\" head=\"Spear\"").LineEndings);
        Assert.Null(One("intent=\"Free Text!\"").Intent);
        var inside = One("callout=\"210,240,250,240\" fringe=\"0,0,0,0\"");
        Assert.Equal((200.0, 200.0, 300.0, 250.0), (inside.Rect.Left, inside.Rect.Top, inside.Rect.Right, inside.Rect.Bottom));

        // Another writer's rect that does not hold the line grows to hold it, and fringe grows with it:
        // the text box stays where rect less fringe put it.
        var grown = One("callout=\"100,100,150,150,200,225\" fringe=\"0,0,10,0\"");
        Assert.Equal((100.0, 100.0, 300.0, 250.0), (grown.Rect.Left, grown.Rect.Top, grown.Rect.Right, grown.Rect.Bottom));
        Assert.Equal(new[] { 100.0, 100, 10, 0 }, grown.RectDifferences!);
        var box = ViewerAnnotationPdf.FromPdf(grown, "g")!.Box;
        Assert.Equal(new ViewerBox(200, 200, 290, 250), box);
    }

    [Fact]
    public async Task TheEmbedJson_RefusesBadCallouts_AndLeavesTextBoxesWithoutOneAlone()
    {
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1,2,3,4,5,6,7,8]"));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1,2,3,4,5]"));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[2000000,2]"));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1e309,2]"));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1,2],\"calloutEnding\":\"" + new string('A', 33) + "\""));
        Assert.Throws<EmbedProtocolException>(() => Parse("\"callout\":[1,2],\"calloutEnding\":7"));
        // calloutEnding without callout is not a callout's.
        Assert.Equal("None", Parse("\"calloutEnding\":\"Circle\"").StartEnding);

        await using var doc = await ViewerDocument.OpenAsync(Blank());
        var session = await ViewerEditSession.StartAsync(doc, "Ada");
        // An anchor off the page: the numbers are read, the store refuses it.
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([Parse("\"callout\":[-10,5]")]));
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([Parse("\"callout\":[100,900]")]));

        // A text box as a page wrote it before callouts: unchanged, and written back without callout.
        var plain = Parse("\"fontSize\":14");
        Assert.False(plain.IsCallout);
        Assert.Empty(plain.Callout);
        var added = Assert.Single(session.AddAnnotations([plain]));
        Assert.Equal(new ViewerBox(300, 500, 450, 550), added.Box);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(w, added, "file");
        using var written = JsonDocument.Parse(buffer.WrittenMemory);
        var selector = written.RootElement.GetProperty("target").GetProperty("selector");
        Assert.False(selector.TryGetProperty("callout", out _));
        Assert.False(selector.TryGetProperty("calloutEnding", out _));
        Assert.Equal(14, selector.GetProperty("fontSize").GetDouble());
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= 0.001;
        public int GetHashCode(double obj) => 0;
    }
}
