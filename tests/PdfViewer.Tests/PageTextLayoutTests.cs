using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PdfViewer.Text;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Character-level page text: caret hit testing, word/sentence/paragraph units, keyboard motion,
/// selection bands and copied text — the rules a word processor's selection follows.
/// </summary>
public class PageTextLayoutTests
{
    private const double W = 0.01, H = 0.02, Pitch = 0.03, X0 = 0.1, Y0 = 0.1;

    /// <summary>One glyph per character, 0.01 wide; lines 0.03 apart; generated breaks between lines.</summary>
    internal static PageTextLayout Layout(params string[] lines) => Layout(lines.Select((l, i) => (l, X0, Y0 + i * Pitch)).ToArray());

    internal static PageTextLayout Layout(params (string Text, double X, double Y)[] lines)
    {
        var glyphs = new List<TextGlyph>();
        for (int li = 0; li < lines.Length; li++)
        {
            var (text, x, y) = lines[li];
            if (li > 0) { glyphs.Add(new TextGlyph("\r", Rect.Empty, true)); glyphs.Add(new TextGlyph("\n", Rect.Empty, true)); }
            for (int k = 0; k < text.Length; k++)
            {
                bool hyphen = text[k] == '-' && k == text.Length - 1;
                glyphs.Add(new TextGlyph(text[k].ToString(), new Rect(x + k * W, y, W, H), IsHyphen: hyphen));
            }
        }
        return new PageTextLayout(glyphs);
    }

    private static Point At(int line, double column) => new(X0 + column * W, Y0 + line * Pitch + H / 2);

    private static string Sel(PageTextLayout l, (int Start, int End) r) => l.GetText(r.Start, r.End);

    [Fact]
    public void Lines_AreFoundFromBreaks_AndBreaksEndTheirLine()
    {
        var l = Layout("Hello world", "Second line");
        Assert.Equal(2, l.Lines.Count);
        Assert.Equal("Hello world", l.GetText(l.Lines[0].Start, l.Lines[0].End));
        Assert.Equal(l.Lines[1].Start, l.Lines[0].NextStart);
        Assert.Equal(0, l.LineIndexOfCaret(l.Lines[0].End)); // the break belongs to the first line
    }

    [Fact]
    public void Lines_WithoutBreaks_SplitWhereTheTextChangesRow()
    {
        var glyphs = new List<TextGlyph>();
        foreach (var (t, y) in new[] { ("abc", 0.1), ("def", 0.2) })
            for (int k = 0; k < t.Length; k++)
                glyphs.Add(new TextGlyph(t[k].ToString(), new Rect(0.1 + k * W, y, W, H)));
        var l = new PageTextLayout(glyphs);
        Assert.Equal(2, l.Lines.Count);
    }

    [Fact]
    public void HitTest_PlacesTheCaretBetweenTheCharactersAroundThePoint()
    {
        var l = Layout("Hello world");
        Assert.Equal(0, l.HitTest(At(0, 0.2)));   // left half of 'H'
        Assert.Equal(1, l.HitTest(At(0, 0.7)));   // right half of 'H'
        Assert.Equal(6, l.HitTest(At(0, 6.4)));   // before 'w'
        Assert.Equal(11, l.HitTest(At(0, 40)));   // past the line end: end of line
        Assert.Equal(0, l.HitTest(new Point(0.0, Y0 + H / 2))); // in the left margin: start of line
    }

    [Fact]
    public void HitTest_AboveBelowAndBetweenLines_UsesTheNearestLine()
    {
        var l = Layout("first", "second");
        Assert.Equal(2, l.HitTest(new Point(X0 + 2 * W, 0.0)));                 // above: first line
        Assert.Equal(l.Lines[1].Start + 3, l.HitTest(new Point(X0 + 3 * W, 0.9))); // below: last line
        // In the gap, nearer the second line.
        Assert.Equal(l.Lines[1].Start + 1, l.HitTest(new Point(X0 + 1 * W, Y0 + Pitch - 0.001)));
    }

    [Fact]
    public void HitTest_InAColumnGap_StaysInThatColumn()
    {
        // Two columns: left at x 0.1, right at x 0.6; rows interleave in reading order.
        var l = Layout(("left one", 0.1, 0.1), ("left two", 0.1, 0.13), ("right one", 0.6, 0.1), ("right two", 0.6, 0.13));
        int caret = l.HitTest(new Point(0.12, 0.1 + H + 0.007)); // in the gap, nearer "left two"
        Assert.Equal(1, l.LineIndexOfCaret(caret)); // "left two", not "right one"
    }

    [Theory]
    [InlineData("Hello, world", 8, "world")]
    [InlineData("Hello, world", 5, ",")]
    [InlineData("Hello, world", 2, "Hello")]
    [InlineData("It don't matter", 5, "don't")]
    [InlineData("pi is 3.14 today", 7, "3.14")]
    [InlineData("a well-known fact", 8, "known")]
    [InlineData("snake_case name", 3, "snake_case")]
    [InlineData("end.", 3, ".")]
    public void DoubleClick_SelectsOnlyTheWord(string text, int glyph, string expected)
    {
        var l = Layout(text);
        Assert.Equal(expected, Sel(l, l.WordAt(glyph)));
    }

    [Fact]
    public void DoubleClick_OnASpace_SelectsTheNearestWord_NotTheSpace()
    {
        var l = Layout("one   two");
        Assert.Equal("one", Sel(l, l.WordAt(3)));
        Assert.Equal("two", Sel(l, l.WordAt(5)));
    }

    [Fact]
    public void Ideographs_AreWordsOfTheirOwn()
    {
        var l = Layout("漢字かな");
        Assert.Equal("字", Sel(l, l.WordAt(1)));
    }

    [Fact]
    public void AHyphenatedWordAcrossLines_IsOneWord_AndCopiesJoined()
    {
        var l = Layout("an exam-", "ple here");
        int p = l.Lines[1].Start; // 'p'
        Assert.Equal("example", Sel(l, l.WordAt(p)));
        Assert.Equal("an example here", l.GetText(0, l.Length));
    }

    [Fact]
    public void Text_AcrossLines_UsesLineBreaks_AndDropsTrailingSpaces()
    {
        var l = Layout("Hello world  ", "next");
        Assert.Equal("world" + Environment.NewLine + "ne", l.GetText(6, l.Lines[1].Start + 2));
    }

    [Fact]
    public void Bands_ArePerLine_FullLineHeight_AndCoverTheSpacesBetweenWords()
    {
        var l = Layout("Hello world", "Second line");
        var bands = l.SelectionBands(6, l.Lines[1].Start + 6);
        Assert.Equal(2, bands.Count);
        Assert.Equal(X0 + 6 * W, bands[0].Rect.Left, 6);
        Assert.Equal(X0 + 11 * W, bands[0].Rect.Right, 6);
        Assert.Equal(H, bands[0].Rect.Height, 6);
        Assert.Equal(X0, bands[1].Rect.Left, 6);
        Assert.Equal(X0 + 6 * W, bands[1].Rect.Right, 6); // "Second" — its trailing space excluded
        // A selection across a space is one band, not two word boxes.
        Assert.Single(l.SelectionBands(0, 11));
    }

    [Fact]
    public void Paragraphs_BreakOnALargerGap_AndAfterAShortSentenceEnd()
    {
        var l = Layout(("A long line of text that", X0, 0.10), ("runs to the margin here.", X0, 0.13),
            ("New paragraph after a gap", X0, 0.19), ("that continues here and", X0, 0.22), ("ends.", X0, 0.25),
            ("Third paragraph starts", X0, 0.28));
        Assert.Equal("A long line of text that" + Environment.NewLine + "runs to the margin here.", Sel(l, l.ParagraphRange(1)));
        Assert.StartsWith("New paragraph", Sel(l, l.ParagraphRange(3)));
        Assert.EndsWith("ends.", Sel(l, l.ParagraphRange(3)));
        Assert.Equal("Third paragraph starts", Sel(l, l.ParagraphRange(5)));
    }

    [Fact]
    public void Sentences_EndAtTerminalPunctuation_NotInsideNumbers()
    {
        var l = Layout("First one. Pi is 3.14 here! Last");
        Assert.Equal("First one.", Sel(l, l.SentenceAt(2)));
        Assert.Equal("Pi is 3.14 here!", Sel(l, l.SentenceAt(13)));
        Assert.Equal("Last", Sel(l, l.SentenceAt(30)));
    }

    [Fact]
    public void CtrlArrows_MoveByWords()
    {
        var l = Layout("one two, three");
        Assert.Equal(4, l.NextWordStart(0));
        Assert.Equal(7, l.NextWordStart(4)); // the comma is a unit of its own
        Assert.Equal(9, l.NextWordStart(7));
        Assert.Equal(4, l.PreviousWordStart(7));
        Assert.Equal(0, l.PreviousWordStart(4));
    }

    [Fact]
    public void ShiftUpDown_KeepsTheColumn()
    {
        var l = Layout("abcdefghij", "klmnopqrst", "uv");
        double? preferred = null;
        int down = l.MoveLine(5, +1, ref preferred);
        Assert.Equal(l.Lines[1].Start + 5, down);
        int down2 = l.MoveLine(down, +1, ref preferred);
        Assert.Equal(l.Lines[2].End, down2); // short line: its end
        int back = l.MoveLine(down2, -1, ref preferred);
        Assert.Equal(l.Lines[1].Start + 5, back); // the preferred column is remembered
    }

    [Fact]
    public void IsOverText_OnlyNearLines()
    {
        var l = Layout("text");
        Assert.True(l.IsOverText(At(0, 2), 0.005));
        Assert.False(l.IsOverText(new Point(0.8, 0.8), 0.005));
    }
}
