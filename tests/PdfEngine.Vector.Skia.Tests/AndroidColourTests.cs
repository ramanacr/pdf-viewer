using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The Android reader's colour of new annotations (issue #141) and the larger tap target of sticky
/// notes (issue #142), run on the desktop: the palette and the kept-colour preference
/// (<see cref="ViewerToolColors"/>), new annotations made with a colour (and without one, the
/// default) written into the file, and <see cref="ViewerEditSession.AnnotationAt"/> with a minimum
/// note size. The Android views themselves are not run here.
/// </summary>
public class AndroidColourTests
{
    /// <summary>A Letter page with one line of Helvetica text, and an empty Letter page.</summary>
    private static byte[] TextPage()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 24 Tf 72 700 Td (Annotate this line) Tj ET", $"<< /Font << /F1 {helv} 0 R >> >>", mediaBox: "[0 0 612 792]");
        b.AddPage("", mediaBox: "[0 0 612 792]");
        return b.Build();
    }

    // ------------------------------------------------------------------ palette and preference

    [Fact]
    public void Palette_IsTheWebViewersWithNames_AndHoldsTheDefaultHighlightAndNoteColour()
    {
        Assert.Equal(["#FFD400", "#22A06B", "#E5484D", "#2563EB", "#000000"], ViewerToolColors.Palette.Select(p => p.Value));
        Assert.Equal(["Yellow", "Green", "Red", "Blue", "Black"], ViewerToolColors.Palette.Select(p => p.Name));
        Assert.Equal(0, ViewerToolColors.IndexOf(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Highlight)));
        Assert.Equal(0, ViewerToolColors.IndexOf(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Note)));
        Assert.Equal(3, ViewerToolColors.IndexOf("#2563eb"));
        Assert.Equal(-1, ViewerToolColors.IndexOf("#1F9D55"));
        Assert.Equal(-1, ViewerToolColors.IndexOf(null));
        Assert.Equal("Blue", ViewerToolColors.NameOf("#2563eb"));
        // A colour not in the palette (the default underline colour) is named by its value.
        Assert.Equal("#1F9D55", ViewerToolColors.NameOf("#1F9D55"));
    }

    [Fact]
    public void PreferenceKeys_OnePerKind_AndNoneForStampsOrReplies()
    {
        var kinds = Enum.GetValues<ViewerAnnotationKind>().Where(ViewerToolColors.Choosable).ToList();
        Assert.Contains(ViewerAnnotationKind.Highlight, kinds);
        Assert.Contains(ViewerAnnotationKind.Ink, kinds);
        Assert.Contains(ViewerAnnotationKind.Note, kinds);
        Assert.DoesNotContain(ViewerAnnotationKind.Stamp, kinds);
        Assert.DoesNotContain(ViewerAnnotationKind.Reply, kinds);
        var keys = kinds.Select(ViewerToolColors.KeyOf).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal("colour.highlight", ViewerToolColors.KeyOf(ViewerAnnotationKind.Highlight));
        Assert.Equal("colour.strikeout", ViewerToolColors.KeyOf(ViewerAnnotationKind.StrikeOut));
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerToolColors.KeyOf(ViewerAnnotationKind.Stamp));
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerToolColors.KeyOf(ViewerAnnotationKind.Reply));
    }

    [Theory]
    [InlineData("#2563EB", "#2563EB")]
    [InlineData("#2563eb", "#2563EB")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2563EB", null)]
    [InlineData("#2563E", null)]
    [InlineData("#2563EBFF", null)]
    [InlineData("#GG63EB", null)]
    [InlineData("red", null)]
    [InlineData("#25 3EB", null)]
    public void Parse_AcceptsOnlyHexColours_AndColorOfFallsBackToTheDefault(string? stored, string? expected)
    {
        Assert.Equal(expected, ViewerToolColors.Parse(stored));
        // Nothing usable kept: the kind's default.
        Assert.Equal(expected ?? ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Ink), ViewerToolColors.ColorOf(ViewerAnnotationKind.Ink, stored));
    }

    // ------------------------------------------------------------------ new annotations with a colour

    [Fact]
    public async Task NewAnnotations_KeepTheChosenColour_ThroughTheFile_AndWithoutOneGetTheDefault()
    {
        byte[] pdf = TextPage();
        await using var doc = await ViewerDocument.OpenAsync(pdf);
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");

        var note = session.AddNote(2, new ViewerPoint(100, 100), "Blue note", "#2563EB");
        var plainNote = session.AddNote(2, new ViewerPoint(300, 100), "Default note");
        var ink = session.AddInk(2, [new ViewerPoint(100, 300), new ViewerPoint(150, 320), new ViewerPoint(200, 380)], "#22A06B");
        var plainInk = session.AddInk(2, [new ViewerPoint(300, 300), new ViewerPoint(350, 320), new ViewerPoint(400, 380)]);
        var rect = session.AddShape(ViewerAnnotationKind.Square, 2, new ViewerPoint(100, 500), new ViewerPoint(200, 600), "#000000");
        var selection = new ViewerTextSelection(1, await doc.GetTextLayoutAsync(1));
        selection.SelectAll();
        var strike = session.MarkText(ViewerAnnotationKind.StrikeOut, selection, "#2563EB");
        var underline = session.MarkText(ViewerAnnotationKind.Underline, selection);

        Assert.Equal("#2563EB", note.Color);
        Assert.Equal(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Note), plainNote.Color);
        Assert.Equal("#22A06B", ink!.Color);
        Assert.Equal(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Ink), plainInk!.Color);
        Assert.Equal("#000000", rect!.Color);
        Assert.Equal("#2563EB", strike!.Color);
        Assert.Equal(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Underline), underline!.Color);

        // Written into the file and read back from it.
        Assert.True((await session.ApplyAsync()).Written);
        await using var reopened = await ViewerDocument.OpenAsync(doc.Bytes);
        var again = await ViewerEditSession.StartAsync(reopened, author: "Reader");
        string ColourOf(string contents, ViewerAnnotationKind kind) =>
            again.Store.Current.Single(a => a.Kind == kind && (contents.Length == 0 || a.Contents == contents)).Color.ToUpperInvariant();
        Assert.Equal("#2563EB", ColourOf("Blue note", ViewerAnnotationKind.Note));
        Assert.Equal(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Note), ColourOf("Default note", ViewerAnnotationKind.Note));
        Assert.Equal("#000000", ColourOf("", ViewerAnnotationKind.Square));
        Assert.Equal("#2563EB", ColourOf("", ViewerAnnotationKind.StrikeOut));
        Assert.Contains(again.Store.Current, a => a.Kind == ViewerAnnotationKind.Ink && a.Color.Equals("#22A06B", StringComparison.OrdinalIgnoreCase));

        // The chosen annotation recoloured: one undo step.
        Assert.True(again.SetColor(again.Store.Current.Single(a => a.Contents == "Default note").Id, "#E5484D"));
        Assert.Equal("#E5484D", again.Store.Current.Single(a => a.Contents == "Default note").Color);
        Assert.True(again.Undo());
        Assert.Equal(ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Note), again.Store.Current.Single(a => a.Contents == "Default note").Color.ToUpperInvariant());
    }

    // ------------------------------------------------------------------ sticky-note tap target

    [Fact]
    public async Task NoteTapTarget_GrowsToTheMinimumSize_OnlyForNotes()
    {
        await using var doc = await ViewerDocument.OpenAsync(TextPage());
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        var note = session.AddNote(2, new ViewerPoint(100, 100), "Note");
        var shown = doc.Geometry[1].ToShown(note.Box);
        Assert.Equal(new ViewerRect(100, 100, ViewerEditSession.NoteSize, ViewerEditSession.NoteSize), shown);

        // 15 points right of the note's edge: found with a 48-point target (14 points each side, then the slack), not without.
        var beside = new ViewerPoint(shown.X + shown.Width + 15, shown.Y + 10);
        Assert.Null(session.AnnotationAt(2, beside, slack: 2));
        Assert.Equal(note.Id, session.AnnotationAt(2, beside, slack: 2, minNoteSize: 48)?.Id);
        // Below and above too; 20 points away is outside it.
        Assert.Equal(note.Id, session.AnnotationAt(2, new ViewerPoint(shown.X + 10, shown.Y - 15), slack: 2, minNoteSize: 48)?.Id);
        Assert.Null(session.AnnotationAt(2, new ViewerPoint(shown.X + shown.Width + 20, shown.Y + 10), slack: 2, minNoteSize: 48));
        // The note's box in the PDF is unchanged.
        Assert.Equal(shown, doc.Geometry[1].ToShown(session.Store.Find(note.Id)!.Box));

        // A rectangle (a shape) is not enlarged.
        var rect = session.AddShape(ViewerAnnotationKind.Square, 2, new ViewerPoint(300, 300), new ViewerPoint(310, 310))!;
        var r = doc.Geometry[1].ToShown(rect.Box);
        Assert.Null(session.AnnotationAt(2, new ViewerPoint(r.X + r.Width + 15, r.Y + r.Height / 2), slack: 2, minNoteSize: 48));
        Assert.Equal(rect.Id, session.AnnotationAt(2, new ViewerPoint(r.X + r.Width / 2, r.Y + r.Height / 2), slack: 2, minNoteSize: 48)?.Id);
    }

    [Fact]
    public async Task NoteTapTarget_TheSmallestStillWins_ANeighbourKeepsItsTaps_AndTheNearestNoteWins()
    {
        await using var doc = await ViewerDocument.OpenAsync(TextPage());
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        var g = doc.Geometry[1];

        // A note on a large rectangle: on the icon, and beside it within its larger target, the
        // smaller one (the note) wins; farther away the rectangle is chosen.
        var big = session.AddShape(ViewerAnnotationKind.Square, 2, new ViewerPoint(50, 50), new ViewerPoint(250, 250))!;
        var onBig = session.AddNote(2, new ViewerPoint(100, 100), "On the rectangle");
        Assert.Equal(onBig.Id, session.AnnotationAt(2, new ViewerPoint(110, 110), slack: 2, minNoteSize: 48)?.Id);
        Assert.Equal(onBig.Id, session.AnnotationAt(2, new ViewerPoint(133, 110), slack: 2, minNoteSize: 48)?.Id);
        Assert.Equal(big.Id, session.AnnotationAt(2, new ViewerPoint(133, 110), slack: 2)?.Id);
        Assert.Equal(big.Id, session.AnnotationAt(2, new ViewerPoint(160, 110), slack: 2, minNoteSize: 48)?.Id);

        // A neighbour smaller than the note, inside the note's larger target, keeps its taps.
        var neighbour = session.AddShape(ViewerAnnotationKind.Square, 2, new ViewerPoint(350, 104), new ViewerPoint(362, 116))!;
        var nearNeighbour = session.AddNote(2, new ViewerPoint(325, 100), "Near the neighbour");
        var n = g.ToShown(neighbour.Box);
        Assert.True(n.Width * n.Height < ViewerEditSession.NoteSize * ViewerEditSession.NoteSize);
        Assert.Equal(neighbour.Id, session.AnnotationAt(2, new ViewerPoint(n.X + 2, n.Y + n.Height / 2), slack: 2, minNoteSize: 48)?.Id);
        Assert.Equal(nearNeighbour.Id, session.AnnotationAt(2, new ViewerPoint(335, 110), slack: 2, minNoteSize: 48)?.Id);

        // Two notes side by side, 10 points apart: a tap on the first one's icon is its own, though
        // the second (topmost) one's larger target covers it; between them the nearer one wins.
        var first = session.AddNote(2, new ViewerPoint(400, 400), "First");
        var second = session.AddNote(2, new ViewerPoint(430, 400), "Second");
        Assert.Equal(first.Id, session.AnnotationAt(2, new ViewerPoint(415, 410), slack: 0, minNoteSize: 48)?.Id);
        Assert.Equal(first.Id, session.AnnotationAt(2, new ViewerPoint(422, 410), slack: 0, minNoteSize: 48)?.Id);
        Assert.Equal(second.Id, session.AnnotationAt(2, new ViewerPoint(428, 410), slack: 0, minNoteSize: 48)?.Id);
        // As near to both: the topmost (the one added last).
        Assert.Equal(second.Id, session.AnnotationAt(2, new ViewerPoint(425, 410), slack: 0, minNoteSize: 48)?.Id);
    }
}
