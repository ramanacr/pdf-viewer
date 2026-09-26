using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PdfViewer.Models;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Selection gestures as a word processor has them: click and drag by character, double-click a
/// word (only the word) and drag by words, triple-click a paragraph, Ctrl+click a sentence,
/// Shift+click and Shift+arrows to extend, and drags across pages.
/// </summary>
public class TextSelectionControllerTests
{
    // Words are laid out one character per 0.01 of the page width; lines 0.03 apart.
    private const double W = 0.01, H = 0.02, X0 = 0.1, Y0 = 0.1, Pitch = 0.03;

    private static PageViewModel Page(int number, params string[] lines)
    {
        var page = new PageViewModel(number, 612, 792);
        int index = 0;
        for (int li = 0; li < lines.Length; li++)
        {
            double x = X0;
            foreach (var word in lines[li].Split(' '))
            {
                page.TextSegments.Add(new PageTextSegment
                {
                    Text = word, SegmentIndex = index++, X = x, Y = Y0 + li * Pitch, Width = word.Length * W, Height = H,
                });
                x += (word.Length + 1) * W;
            }
        }
        page.IsTextExtracted = true;
        return page;
    }

    private static Point At(int line, double column) => new(X0 + column * W, Y0 + line * Pitch + H / 2);

    private sealed class Harness
    {
        public readonly List<PageViewModel> Pages;
        public readonly TextSelectionController Selection;
        public int Changes;
        public Harness(params PageViewModel[] pages)
        {
            Pages = pages.ToList();
            Selection = new TextSelectionController(() => Pages, () => Changes++);
        }
        public string Text => string.Join("|", Pages.Where(p => p.HasTextSelection).Select(p => p.GetSelectedText()));
    }

    [Fact]
    public void ClickThenDrag_SelectsCharacterByCharacter()
    {
        var h = new Harness(Page(1, "Hello world"));
        var p = h.Pages[0];
        h.Selection.PointerDown(p, At(0, 1.2), 1, shift: false, ctrl: false);
        Assert.False(p.HasTextSelection); // a click places the caret, it selects nothing
        h.Selection.PointerMove(p, At(0, 3.8));
        Assert.Equal("ell", h.Text);
        h.Selection.PointerMove(p, At(0, 0.1)); // back past the anchor
        Assert.Equal("H", h.Text);
    }

    [Fact]
    public void DoubleClick_SelectsOnlyTheWord_AndDraggingExtendsByWholeWords()
    {
        var h = new Harness(Page(1, "one two three four"));
        var p = h.Pages[0];
        h.Selection.PointerDown(p, At(0, 5), 2, false, false); // on "two"
        Assert.Equal("two", h.Text);
        h.Selection.PointerMove(p, At(0, 9)); // into "three"
        Assert.Equal("two three", h.Text);
        h.Selection.PointerMove(p, At(0, 1)); // back into "one"
        Assert.Equal("one two", h.Text); // the anchor word stays selected
    }

    [Fact]
    public void TripleClick_SelectsTheParagraph()
    {
        var h = new Harness(Page(1, "First line of", "the paragraph."));
        h.Selection.PointerDown(h.Pages[0], At(1, 2), 3, false, false);
        Assert.Equal("First line of" + System.Environment.NewLine + "the paragraph.", h.Text);
    }

    [Fact]
    public void CtrlClick_SelectsTheSentence()
    {
        var h = new Harness(Page(1, "One here. Two there. Three"));
        h.Selection.PointerDown(h.Pages[0], At(0, 12), 1, false, ctrl: true);
        Assert.Equal("Two there.", h.Text);
    }

    [Fact]
    public void ShiftClick_ExtendsFromTheAnchor()
    {
        var h = new Harness(Page(1, "alpha beta gamma"));
        var p = h.Pages[0];
        h.Selection.PointerDown(p, At(0, 0), 1, false, false);
        h.Selection.PointerDown(p, At(0, 10), 1, shift: true, ctrl: false);
        Assert.Equal("alpha beta", h.Text);
        h.Selection.PointerDown(p, At(0, 16), 1, shift: true, ctrl: false);
        Assert.Equal("alpha beta gamma", h.Text);
    }

    [Fact]
    public void ShiftArrows_MoveTheActiveEnd()
    {
        var h = new Harness(Page(1, "alpha beta gamma", "second line"));
        var p = h.Pages[0];
        h.Selection.PointerDown(p, At(0, 0), 1, false, false);
        h.Selection.PointerMove(p, At(0, 3));
        Assert.Equal("alp", h.Text);
        h.Selection.Extend(SelectionMove.CharacterRight);
        Assert.Equal("alph", h.Text);
        h.Selection.Extend(SelectionMove.WordRight);
        Assert.Equal("alpha", h.Text); // up to the next word's start: the space is inside, trimmed on copy
        h.Selection.Extend(SelectionMove.LineEnd);
        Assert.Equal("alpha beta gamma", h.Text);
        h.Selection.Extend(SelectionMove.LineDown);
        Assert.Equal("alpha beta gamma" + System.Environment.NewLine + "second line", h.Text);
        h.Selection.Extend(SelectionMove.PageStart);
        Assert.False(p.HasTextSelection); // back to the anchor
    }

    [Fact]
    public void ShiftLeft_AfterADoubleClick_ShrinksTheWordFromItsEnd()
    {
        var h = new Harness(Page(1, "one word here"));
        h.Selection.PointerDown(h.Pages[0], At(0, 5), 2, false, false);
        Assert.Equal("word", h.Text);
        h.Selection.Extend(SelectionMove.CharacterLeft);
        Assert.Equal("wor", h.Text);
        h.Selection.Extend(SelectionMove.CharacterRight);
        h.Selection.Extend(SelectionMove.CharacterRight);
        Assert.Equal("word", h.Text.TrimEnd()); // the space follows; copy trims it
    }

    [Fact]
    public void DraggingAcrossPages_SelectsTheTailTheMiddleAndTheHead()
    {
        var h = new Harness(Page(1, "end of page one"), Page(2, "all of page two"), Page(3, "start of three"));
        h.Selection.PointerDown(h.Pages[0], At(0, 7), 1, false, false);
        h.Selection.PointerMove(h.Pages[2], At(0, 5));
        Assert.Equal("page one|all of page two|start", h.Text);
        // Back onto the first page: the other pages clear.
        h.Selection.PointerMove(h.Pages[0], At(0, 11));
        Assert.Equal("page", h.Text);
        Assert.False(h.Pages[1].HasTextSelection);
        Assert.False(h.Pages[2].HasTextSelection);
    }

    [Fact]
    public void ShiftRight_AtThePageEnd_ContinuesOnTheNextPage()
    {
        var h = new Harness(Page(1, "ab"), Page(2, "cd"));
        h.Selection.PointerDown(h.Pages[0], At(0, 1), 1, false, false);
        h.Selection.Extend(SelectionMove.CharacterRight);
        h.Selection.Extend(SelectionMove.CharacterRight);
        h.Selection.Extend(SelectionMove.CharacterRight);
        Assert.Equal("b|c", h.Text);
    }

    [Fact]
    public void BeyondTheText_MapsToTheNearestLine()
    {
        var h = new Harness(Page(1, "top line", "bottom line"));
        var p = h.Pages[0];
        h.Selection.PointerDown(p, new Point(0.0, 0.0), 1, false, false); // above and left: start
        h.Selection.PointerMove(p, new Point(0.9, 0.9));                  // below and right: end
        Assert.Equal("top line" + System.Environment.NewLine + "bottom line", h.Text);
    }

    [Fact]
    public void Clear_RemovesEverySelection()
    {
        var h = new Harness(Page(1, "one"), Page(2, "two"));
        h.Selection.PointerDown(h.Pages[0], At(0, 0), 1, false, false);
        h.Selection.PointerMove(h.Pages[1], At(0, 3));
        h.Selection.Clear();
        Assert.All(h.Pages, p => Assert.False(p.HasTextSelection));
        Assert.All(h.Pages, p => Assert.Empty(p.SelectedSegments));
        Assert.False(h.Selection.HasAnchor);
    }
}
