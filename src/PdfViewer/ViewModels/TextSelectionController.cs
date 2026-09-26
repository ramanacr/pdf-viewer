using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace PdfViewer.ViewModels;

/// <summary>What a drag or Shift+click extends by, set by how the selection began.</summary>
public enum SelectionUnit
{
    /// <summary>Single click and drag: character by character.</summary>
    Character,
    /// <summary>Double-click (and drag): whole words.</summary>
    Word,
    /// <summary>Ctrl+click (and drag): whole sentences.</summary>
    Sentence,
    /// <summary>Triple-click (and drag): whole paragraphs.</summary>
    Paragraph,
}

/// <summary>Keyboard selection moves (always with Shift: without it the arrows scroll the document).</summary>
public enum SelectionMove
{
    CharacterLeft, CharacterRight, WordLeft, WordRight, LineUp, LineDown, LineStart, LineEnd, PageStart, PageEnd,
}

/// <summary>
/// Text selection as a word processor does it, across pages: an anchor where the selection began
/// and an active end that follows the pointer or the keyboard. Double-click selects a word (only
/// the word), triple-click a paragraph, Ctrl+click a sentence; dragging after any of them extends
/// by that unit while the unit under the anchor stays selected; Shift+click and Shift+arrows move
/// the active end. UI-free: the window passes pages and normalized page points.
/// </summary>
public sealed class TextSelectionController
{
    private readonly Func<IReadOnlyList<PageViewModel>> _pages;
    private readonly Action _changed;

    private PageViewModel? _anchorPage;
    private int _anchorStart, _anchorEnd; // the unit under the anchor (equal carets for character mode)
    private PageViewModel? _activePage;
    private int _activeCaret;  // caret under the pointer / moved by the keyboard
    private int _activeGlyph;  // character under the pointer (unit modes)
    private double? _preferredU;

    public SelectionUnit Unit { get; private set; } = SelectionUnit.Character;
    public bool HasAnchor => _anchorPage != null;
    public PageViewModel? ActivePage => _activePage;
    public int ActiveCaret => _activeCaret;

    public TextSelectionController(Func<IReadOnlyList<PageViewModel>> pages, Action changed)
    {
        _pages = pages;
        _changed = changed;
    }

    /// <summary>A press on a page: places the anchor and selects the clicked unit.</summary>
    public void PointerDown(PageViewModel page, Point normPoint, int clickCount, bool shift, bool ctrl)
    {
        var layout = page.TextLayout;
        _preferredU = null;
        if (shift && _anchorPage != null && clickCount == 1)
        {
            MoveActive(page, normPoint);
            return;
        }

        _anchorPage = page;
        _activePage = page;
        int caret = layout.HitTest(normPoint);
        int glyph = layout.GlyphAt(normPoint);
        switch (clickCount)
        {
            case 2 when glyph >= 0:
                Unit = SelectionUnit.Word;
                (_anchorStart, _anchorEnd) = layout.WordAt(glyph);
                break;
            case >= 3 when glyph >= 0:
                Unit = SelectionUnit.Paragraph;
                (_anchorStart, _anchorEnd) = layout.ParagraphRange(layout.LineIndexOfCaret(glyph));
                break;
            case 1 when ctrl && glyph >= 0:
                Unit = SelectionUnit.Sentence;
                (_anchorStart, _anchorEnd) = layout.SentenceAt(glyph);
                break;
            default:
                Unit = SelectionUnit.Character;
                _anchorStart = _anchorEnd = caret;
                break;
        }
        _activeGlyph = Math.Max(0, glyph);
        _activeCaret = Unit == SelectionUnit.Character ? caret : _anchorEnd;
        Apply();
    }

    /// <summary>The pointer moved (button held) over <paramref name="page"/>.</summary>
    public void PointerMove(PageViewModel page, Point normPoint)
    {
        if (_anchorPage == null) return;
        MoveActive(page, normPoint);
    }

    private void MoveActive(PageViewModel page, Point normPoint)
    {
        var layout = page.TextLayout;
        _activePage = page;
        _activeCaret = layout.HitTest(normPoint);
        _activeGlyph = Math.Max(0, layout.GlyphAt(normPoint));
        Apply();
    }

    /// <summary>Shift+arrow and friends: move the active end, extending or shrinking the selection.</summary>
    public bool Extend(SelectionMove move)
    {
        if (_anchorPage == null || _activePage == null) return false;
        if (Unit != SelectionUnit.Character)
        {
            // From here on the selection changes by characters, from the unit's far edge: the
            // anchor becomes the edge the active end is not at, as in a word processor.
            bool forward = Compare(_activePage, _activeCaret, _anchorPage, _anchorStart) >= 0;
            _anchorStart = _anchorEnd = forward ? _anchorStart : _anchorEnd;
            if (forward && _activePage == _anchorPage) _activeCaret = Math.Max(_activeCaret, SelectionEndOn(_activePage));
            Unit = SelectionUnit.Character;
        }
        var page = _activePage;
        var layout = page.TextLayout;
        int caret = _activeCaret;
        if (move is not (SelectionMove.LineUp or SelectionMove.LineDown)) _preferredU = null;

        int next = caret;
        PageViewModel? nextPage = page;
        switch (move)
        {
            case SelectionMove.CharacterLeft:
                if (caret > 0) next = caret - 1;
                else if (Neighbour(page, -1) is { } prev) { nextPage = prev; next = prev.TextLayout.Length; }
                break;
            case SelectionMove.CharacterRight:
                if (caret < layout.Length) next = caret + 1;
                else if (Neighbour(page, +1) is { } nxt) { nextPage = nxt; next = 0; }
                break;
            case SelectionMove.WordLeft:
                if (caret > 0) next = layout.PreviousWordStart(caret);
                else if (Neighbour(page, -1) is { } prev) { nextPage = prev; next = prev.TextLayout.PreviousWordStart(prev.TextLayout.Length); }
                break;
            case SelectionMove.WordRight:
                if (caret < layout.Length) next = layout.NextWordStart(caret);
                else if (Neighbour(page, +1) is { } nxt) { nextPage = nxt; next = nxt.TextLayout.NextWordStart(0); }
                break;
            case SelectionMove.LineUp:
            case SelectionMove.LineDown:
            {
                int delta = move == SelectionMove.LineUp ? -1 : 1;
                int li = layout.LineIndexOfCaret(caret);
                int target = li + delta;
                if (target < 0 && Neighbour(page, -1) is { } prev && !prev.TextLayout.IsEmpty)
                {
                    nextPage = prev;
                    next = prev.TextLayout.Lines[^1].Start;
                }
                else if (target >= layout.Lines.Count && Neighbour(page, +1) is { } nxt && !nxt.TextLayout.IsEmpty)
                {
                    nextPage = nxt;
                    next = nxt.TextLayout.Lines[0].End;
                }
                else
                {
                    next = layout.MoveLine(caret, delta, ref _preferredU);
                }
                break;
            }
            case SelectionMove.LineStart: next = layout.LineStart(caret); break;
            case SelectionMove.LineEnd: next = layout.LineEnd(caret); break;
            case SelectionMove.PageStart: next = 0; break;
            case SelectionMove.PageEnd: next = layout.Length; break;
        }

        _activePage = nextPage;
        _activeCaret = next;
        Apply();
        return true;
    }

    private PageViewModel? Neighbour(PageViewModel page, int delta)
    {
        var pages = _pages();
        int i = page.PageNumber - 1 + delta;
        return i >= 0 && i < pages.Count ? pages[i] : null;
    }

    /// <summary>Selects all text of a page (Ctrl+A).</summary>
    public void SelectAll(PageViewModel page)
    {
        Unit = SelectionUnit.Character;
        _anchorPage = page;
        _anchorStart = _anchorEnd = 0;
        _activePage = page;
        _activeCaret = page.TextLayout.Length;
        Apply();
    }

    public void Clear()
    {
        _anchorPage = _activePage = null;
        Unit = SelectionUnit.Character;
        foreach (var p in _pages()) if (p.HasTextSelection || p.SelectedSegments.Count > 0) p.ClearTextSelection();
        _changed();
    }

    private static int Compare(PageViewModel pa, int a, PageViewModel pb, int b) =>
        pa.PageNumber != pb.PageNumber ? pa.PageNumber.CompareTo(pb.PageNumber) : a.CompareTo(b);

    private static int SelectionEndOn(PageViewModel page) => page.HasTextSelection ? page.SelectionEnd : 0;

    /// <summary>
    /// Writes the selection to every page: from the anchor unit to the unit at the active end,
    /// whichever comes first in the document; the anchor unit always stays selected.
    /// </summary>
    private void Apply()
    {
        if (_anchorPage == null || _activePage == null) return;
        var ap = _anchorPage;
        var act = _activePage;
        var (xs, xe) = Unit == SelectionUnit.Character ? (_activeCaret, _activeCaret) : UnitAt(act, _activeGlyph);

        (PageViewModel Page, int Caret) start, end;
        if (Compare(act, xs, ap, _anchorStart) >= 0)
        {
            start = (ap, _anchorStart);
            end = (act, act == ap ? Math.Max(xe, _anchorEnd) : xe);
        }
        else
        {
            start = (act, xs);
            end = (ap, _anchorEnd);
        }

        foreach (var page in _pages())
        {
            int n = page.PageNumber;
            if (n < start.Page.PageNumber || n > end.Page.PageNumber)
            {
                if (page.HasTextSelection || page.SelectedSegments.Count > 0) page.ClearTextSelection();
                continue;
            }
            int s = n == start.Page.PageNumber ? start.Caret : 0;
            int e = n == end.Page.PageNumber ? end.Caret : page.TextLayout.Length;
            page.SetTextSelection(s, e);
        }
        _changed();
    }

    private (int Start, int End) UnitAt(PageViewModel page, int glyph)
    {
        var layout = page.TextLayout;
        if (layout.Length == 0) return (0, 0);
        glyph = Math.Clamp(glyph, 0, layout.Length - 1);
        return Unit switch
        {
            SelectionUnit.Word => layout.WordAt(glyph),
            SelectionUnit.Sentence => layout.SentenceAt(glyph),
            SelectionUnit.Paragraph => layout.ParagraphRange(layout.LineIndexOfCaret(glyph)),
            _ => (glyph, glyph),
        };
    }
}
