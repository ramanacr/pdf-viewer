using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Text selection across pages on Android (PageView), through the shared <see cref="ViewerTextSpan"/>:
/// the end handle dragged onto later pages (and the start onto earlier ones), pages in between whole,
/// the text in reading order with each page's part joined by a line break, handles that swap, the
/// layouts a handle needs before it reaches a page, the page cap, and markup as one undo step
/// (<see cref="ViewerEditSession.MarkText(ViewerAnnotationKind, IReadOnlyList{ViewerTextSelection}, string?)"/>).
/// </summary>
public class AndroidTextSpanTests
{
    /// <summary>A page's layout with one line per text, 10 points per character, lines 20 points apart from y = 100.</summary>
    private static ViewerTextLayout Page(params string[] lines)
    {
        var glyphs = new List<ViewerTextLayout.Glyph>();
        for (int l = 0; l < lines.Length; l++)
        {
            for (int i = 0; i < lines[l].Length; i++)
                glyphs.Add(new ViewerTextLayout.Glyph(lines[l][i].ToString(), new ViewerRect(100 + 10 * i, 100 + 20 * l, 10, 12), false, false));
            if (l + 1 < lines.Length) glyphs.Add(new ViewerTextLayout.Glyph("\n", default, true, false));
        }
        return new ViewerTextLayout(glyphs);
    }

    private static readonly ViewerTextLayout One = Page("alpha beta", "gamma");
    private static readonly ViewerTextLayout Two = Page("delta", "epsilon");
    private static readonly ViewerTextLayout Three = Page("zeta eta");

    /// <summary>The point just right of a line's last character, or on a character (index) of a line.</summary>
    private static ViewerPoint At(int line, double character) => new(100 + 10 * character, 106 + 20 * line);

    [Fact]
    public void TheEndHandle_DraggedOntoLaterPages_SelectsAcrossThem_InReadingOrder()
    {
        var span = new ViewerTextSpan(1, One);
        Assert.True(span.SelectWordAt(1, At(0, 7), slack: 4));
        Assert.Equal("beta", span.Text);

        // Page 3 needs pages 2 and 3 first: nothing moves until they are given.
        Assert.Equal([2, 3], span.MissingLayouts(3));
        Assert.False(span.MoveHandle(false, 3, At(0, 4.2)));
        Assert.Equal("beta", span.Text);
        span.AddLayout(2, Two);
        span.AddLayout(3, Three);
        Assert.Empty(span.MissingLayouts(3));

        Assert.False(span.MoveHandle(false, 3, At(0, 4.2)));
        Assert.Equal((1, 3), (span.FirstPage, span.LastPage));
        Assert.Equal("beta\ngamma\ndelta\nepsilon\nzeta", span.Text);
        Assert.Equal([1, 2, 3], span.Parts.Select(p => p.Page));
        Assert.Equal("delta\nepsilon", span.Parts[1].Text);

        // The handles: bottom-left of the first band on page 1, bottom-right of the last on page 3.
        var (start, end) = span.HandlePositions()!.Value;
        Assert.Equal((1, new ViewerPoint(160, 112)), start);
        Assert.Equal(3, end.Page);
        Assert.Equal(140, end.Point.X, 6);

        // Back onto page 2: page 3 drops out.
        span.MoveHandle(false, 2, At(1, 3.2));
        Assert.Equal("beta\ngamma\ndelta\neps", span.Text);
        Assert.Equal(2, span.LastPage);
    }

    [Fact]
    public void TheStartHandle_DraggedPastTheEnd_OntoAnotherPage_Swaps()
    {
        var span = new ViewerTextSpan(2, Two);
        Assert.True(span.SelectWordAt(2, At(0, 2), slack: 4));
        Assert.Equal("delta", span.Text);
        span.AddLayout(1, One);
        span.AddLayout(3, Three);

        // The start handle onto page 1: the selection runs back to it.
        Assert.False(span.MoveHandle(true, 1, At(1, 0)));
        Assert.Equal("gamma\ndelta", span.Text);

        // The start handle dragged onto page 3, past the end: the handles swap.
        Assert.True(span.MoveHandle(true, 3, At(0, 4.2)));
        Assert.Equal(new ViewerCaret(2, 5), span.Start);
        Assert.Equal(new ViewerCaret(3, 4), span.End);
        Assert.Equal("epsilon\nzeta", span.Text);
    }

    [Fact]
    public void All_SelectsEveryPageTheSelectionIsOn()
    {
        var span = new ViewerTextSpan(1, One);
        span.AddLayout(2, Two);
        span.SelectWordAt(1, At(1, 1), slack: 4);
        span.MoveHandle(false, 2, At(0, 1.2));
        span.SelectPages();
        Assert.Equal("alpha beta\ngamma\ndelta\nepsilon", span.Text);
    }

    [Fact]
    public void APageWithoutText_InBetween_AddsNothing_AndTheSpanIsCapped()
    {
        var span = new ViewerTextSpan(1, One);
        span.SelectWordAt(1, At(1, 1), slack: 4);
        span.AddLayout(2, ViewerTextLayout.Empty);
        span.AddLayout(3, Three);
        span.MoveHandle(false, 3, At(0, 3.8));
        Assert.Equal("gamma\nzeta", span.Text);
        Assert.Equal([1, 3], span.Parts.Select(p => p.Page));

        Assert.True(span.CanReach(ViewerTextSpan.MaxPages));
        Assert.False(span.CanReach(ViewerTextSpan.MaxPages + 1));
        Assert.Empty(span.MissingLayouts(ViewerTextSpan.MaxPages + 1));
        Assert.False(span.MoveHandle(false, ViewerTextSpan.MaxPages + 1, At(0, 1)));
    }

    [Fact]
    public void AnEmptySpan_HasNoTextNorHandles()
    {
        var span = new ViewerTextSpan(1, One);
        Assert.True(span.IsEmpty);
        Assert.Equal("", span.Text);
        Assert.Null(span.HandlePositions());
        Assert.False(span.SelectWordAt(1, new ViewerPoint(500, 700), slack: 4));
        Assert.False(span.SelectWordAt(4, At(0, 1), slack: 4));
    }

    [Fact]
    public async Task MarkingASpan_AddsOneAnnotationPerPage_AsOneUndoStep()
    {
        var b = new VectorPdfBuilder();
        for (int i = 0; i < 3; i++) b.AddPage("", mediaBox: "[0 0 612 792]");
        await using var doc = await ViewerDocument.OpenAsync(b.Build());
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        var span = new ViewerTextSpan(1, One);
        span.AddLayout(2, Two);
        span.AddLayout(3, Three);
        span.SelectWordAt(1, At(1, 1), slack: 4);
        span.MoveHandle(false, 3, At(0, 2.2));

        var marks = session.MarkText(ViewerAnnotationKind.Highlight, span.Parts, "#FFD400");
        Assert.Equal([1, 2, 3], marks.Select(m => m.Page));
        Assert.All(marks, m => Assert.Equal(ViewerAnnotationKind.Highlight, m.Kind));
        // Page 2 has two lines: two quadrilaterals.
        Assert.Equal(16, marks[1].QuadPoints.Count);
        Assert.All(marks, m => Assert.True(session.IsOwn(m)));
        Assert.Equal(3, session.Store.Current.Count);

        Assert.True(session.Undo());
        Assert.Empty(session.Store.Current);
        Assert.True(session.Redo());
        Assert.Equal(3, session.Store.Current.Count);

        Assert.Empty(session.MarkText(ViewerAnnotationKind.Underline, Array.Empty<ViewerTextSelection>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.MarkText(ViewerAnnotationKind.Ink, span.Parts));
    }
}
