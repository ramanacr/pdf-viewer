using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;

namespace PdfViewer.Text;

/// <summary>
/// One character of a page's text in reading order. <see cref="Box"/> is normalized (0–1) to the
/// unrotated page, top-left origin, and uses the font's ascent and descent (not the glyph's ink),
/// so every character of a line has the same height — selection bands are then as even as a word
/// processor's. Generated characters (spaces and line breaks the extractor inferred) may have an
/// empty box; the layout gives them one from their neighbours.
/// </summary>
public readonly record struct TextGlyph(string Text, Rect Box, bool IsGenerated = false, bool IsHyphen = false)
{
    public bool IsLineBreak => Text is "\r" or "\n" or "\r\n";
}

/// <summary>A line of text: glyphs [<see cref="Start"/>, <see cref="End"/>), its trailing break excluded.</summary>
public sealed class TextLine
{
    public int Index { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    /// <summary>Index after the line's break glyphs (the next line's start).</summary>
    public int NextStart { get; init; }
    public Rect Bounds { get; init; }

    // Oriented frame: u runs along the text, v across it (both unit vectors, page space).
    internal Vector U { get; init; }
    internal Vector V { get; init; }
    internal double UMin { get; init; }
    internal double UMax { get; init; }
    internal double VMin { get; init; }
    internal double VMax { get; init; }
    public double Thickness => VMax - VMin;
}

/// <summary>
/// The text of one page as a word processor sees it: characters in reading order grouped into
/// lines, with caret positions between characters. A caret is an index 0..<see cref="Length"/>;
/// the range [a, b) selects the characters between two carets. Everything here is pure geometry
/// and Unicode rules — no UI — so selection behaviour is fully unit-testable.
/// </summary>
public sealed class PageTextLayout
{
    public static readonly PageTextLayout Empty = new(Array.Empty<TextGlyph>());

    private readonly TextGlyph[] _glyphs;
    private readonly TextLine[] _lines;
    private readonly int[] _lineOf; // glyph index → line index (break glyphs belong to the line they end)
    private readonly int[] _paragraphStartLine; // per line: first line of its paragraph

    public IReadOnlyList<TextGlyph> Glyphs => _glyphs;
    public IReadOnlyList<TextLine> Lines => _lines;
    public int Length => _glyphs.Length;
    public bool IsEmpty => _lines.Length == 0;

    public PageTextLayout(IReadOnlyList<TextGlyph> glyphs)
    {
        var list = new List<TextGlyph>(glyphs.Count);
        // Normalise: a "\r" "\n" pair is one break; surrogate halves were joined by the extractor.
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (g.Text == "\r" && i + 1 < glyphs.Count && glyphs[i + 1].Text == "\n")
            {
                list.Add(g with { Text = "\r\n" });
                i++;
                continue;
            }
            if (g.Text.Length == 0 || g.Text == "\0")
                continue;
            list.Add(g);
        }
        _glyphs = list.ToArray();
        _lines = BuildLines(_glyphs, out _lineOf);
        FillGeneratedBoxes();
        _paragraphStartLine = FindParagraphs();
    }

    // ------------------------------------------------------------------ construction

    /// <summary>
    /// A layout from word boxes alone (no character positions): characters share their word's
    /// box evenly, words are separated by a space and rows by a line break.
    /// </summary>
    public static PageTextLayout FromWords(IEnumerable<(string Text, Rect Box)> words)
    {
        var glyphs = new List<TextGlyph>();
        Rect? prev = null;
        foreach (var (text, box) in words)
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (prev is { } p)
            {
                bool newRow = p.Top + p.Height * 0.6 < box.Top || box.Left < p.Left - p.Height;
                glyphs.Add(new TextGlyph(newRow ? "\n" : " ", Rect.Empty, IsGenerated: true));
            }
            var elements = StringInfo.GetTextElementEnumerator(text);
            var parts = new List<string>();
            while (elements.MoveNext()) parts.Add(elements.GetTextElement());
            double cw = box.Width / Math.Max(1, parts.Count);
            for (int k = 0; k < parts.Count; k++)
                glyphs.Add(new TextGlyph(parts[k], new Rect(box.X + k * cw, box.Y, cw, box.Height)));
            prev = box;
        }
        return new PageTextLayout(glyphs);
    }

    private static bool Visible(in TextGlyph g) => !g.IsLineBreak && g.Box.Width > 0 && g.Box.Height > 0;

    private static TextLine[] BuildLines(TextGlyph[] glyphs, out int[] lineOf)
    {
        var map = new int[glyphs.Length];
        var lines = new List<TextLine>();
        int n = glyphs.Length;
        int start = 0, lastVisible = -1;

        void Emit(int s, int end, int next)
        {
            var line = MakeLine(glyphs, lines.Count, s, end, next);
            if (line != null)
            {
                for (int k = s; k < next; k++) map[k] = lines.Count;
                lines.Add(line);
                return;
            }
            // Only spaces and breaks: they end the previous line.
            if (lines.Count == 0) return;
            var prev = lines[^1];
            for (int k = s; k < next; k++) map[k] = prev.Index;
            lines[^1] = new TextLine
            {
                Index = prev.Index, Start = prev.Start, End = prev.End, NextStart = next, Bounds = prev.Bounds,
                U = prev.U, V = prev.V, UMin = prev.UMin, UMax = prev.UMax, VMin = prev.VMin, VMax = prev.VMax,
            };
        }

        int i = 0;
        while (i < n)
        {
            if (glyphs[i].IsLineBreak)
            {
                int next = i;
                while (next < n && glyphs[next].IsLineBreak) next++;
                Emit(start, i, next);
                start = next;
                i = next;
                lastVisible = -1;
                continue;
            }
            // Extractors without explicit breaks still change line: a visible glyph that does
            // not continue the previous one along the line starts a new one.
            if (Visible(glyphs[i]))
            {
                if (lastVisible >= 0 && StartsNewLine(glyphs[lastVisible], glyphs[i]))
                {
                    Emit(start, i, i);
                    start = i;
                }
                lastVisible = i;
            }
            i++;
        }
        if (start < n) Emit(start, n, n);
        lineOf = map;
        return lines.ToArray();
    }

    private static bool StartsNewLine(in TextGlyph prev, in TextGlyph cur)
    {
        var a = prev.Box;
        var b = cur.Box;
        double h = Math.Max(Math.Min(a.Height, b.Height), 1e-6);
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (overlap < 0.3 * h)
            return true; // different rows
        // Same row but moving back (a new column's line at the same height, or a return).
        return b.Left < a.Left - 0.5 * h;
    }

    private static TextLine? MakeLine(TextGlyph[] glyphs, int index, int start, int end, int next)
    {
        var pts = new List<Rect>();
        for (int i = start; i < end; i++)
            if (Visible(glyphs[i])) pts.Add(glyphs[i].Box);
        if (pts.Count == 0)
            return null;

        // Direction: from the first to the last glyph centre; a single glyph reads left to right
        // unless it is clearly taller than wide (vertical writing).
        Vector u;
        var c0 = Center(pts[0]);
        var c1 = Center(pts[^1]);
        var d = c1 - c0;
        if (d.Length > 0.5 * Math.Min(pts[0].Height, pts[0].Width) && pts.Count > 1)
        {
            d.Normalize();
            u = d;
            // Snap nearly axis-aligned lines: jitter in glyph positions should not tilt the band.
            if (Math.Abs(u.Y) < 0.05) u = new Vector(Math.Sign(u.X) == 0 ? 1 : Math.Sign(u.X), 0);
            else if (Math.Abs(u.X) < 0.05) u = new Vector(0, Math.Sign(u.Y));
        }
        else
        {
            u = new Vector(1, 0);
        }
        var v = new Vector(-u.Y, u.X);
        double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in pts)
        {
            foreach (var p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
            {
                double pu = p.X * u.X + p.Y * u.Y, pv = p.X * v.X + p.Y * v.Y;
                umin = Math.Min(umin, pu); umax = Math.Max(umax, pu);
                vmin = Math.Min(vmin, pv); vmax = Math.Max(vmax, pv);
            }
            left = Math.Min(left, r.Left); top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right); bottom = Math.Max(bottom, r.Bottom);
        }
        return new TextLine
        {
            Index = index,
            Start = start,
            End = end,
            NextStart = next,
            Bounds = new Rect(left, top, right - left, bottom - top),
            U = u, V = v, UMin = umin, UMax = umax, VMin = vmin, VMax = vmax,
        };
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Generated spaces get the gap between their neighbours, at the line's height.</summary>
    private void FillGeneratedBoxes()
    {
        foreach (var line in _lines)
        {
            for (int i = line.Start; i < line.End; i++)
            {
                if (Visible(_glyphs[i]))
                    continue;
                int p = i - 1;
                while (p >= line.Start && !Visible(_glyphs[p])) p--;
                int n = i + 1;
                while (n < line.End && !Visible(_glyphs[n])) n++;
                Rect box;
                if (p >= line.Start && n < line.End)
                    box = Union(new Rect(_glyphs[p].Box.TopRight, _glyphs[n].Box.BottomLeft), line);
                else if (p >= line.Start)
                    box = new Rect(_glyphs[p].Box.Right, _glyphs[p].Box.Top, Math.Max(1e-4, _glyphs[p].Box.Height * 0.25), _glyphs[p].Box.Height);
                else if (n < line.End)
                    box = new Rect(Math.Max(0, _glyphs[n].Box.Left - _glyphs[n].Box.Height * 0.25), _glyphs[n].Box.Top, _glyphs[n].Box.Height * 0.25, _glyphs[n].Box.Height);
                else
                    continue;
                _glyphs[i] = _glyphs[i] with { Box = box };
            }
        }

        static Rect Union(Rect gap, TextLine line)
        {
            // Horizontal lines: the gap across the full line height; otherwise the plain gap.
            if (line.U.Y == 0)
                return new Rect(Math.Min(gap.Left, gap.Right), line.Bounds.Top, Math.Max(1e-5, Math.Abs(gap.Width)), line.Bounds.Height);
            return gap;
        }
    }

    /// <summary>
    /// Paragraphs: a new one starts where the gap to the previous line is clearly larger than the
    /// block's line pitch, where the line changes column or size, or after a short line that ends
    /// a sentence (the last line of a paragraph rarely reaches the right margin).
    /// </summary>
    private int[] FindParagraphs()
    {
        var starts = new int[_lines.Length];
        for (int i = 0; i < _lines.Length; i++)
        {
            if (i == 0) { starts[i] = 0; continue; }
            var prev = _lines[i - 1];
            var cur = _lines[i];
            bool newParagraph;
            double h = Math.Max(prev.Thickness, 1e-6);
            if (prev.U != cur.U)
            {
                newParagraph = true;
            }
            else
            {
                double pitch = cur.VMin - prev.VMin; // along the reading-order perpendicular
                double gap = cur.VMin - prev.VMax;
                bool sizeChange = Math.Abs(cur.Thickness - prev.Thickness) > 0.25 * h;
                bool columnChange = pitch < 0 || Math.Abs(cur.UMin - prev.UMin) > 6 * h && cur.UMin > prev.UMax;
                double typicalGap = TypicalGap(i);
                bool largeGap = gap > Math.Max(0.8 * h, typicalGap + 0.5 * h);
                string prevText = LineText(prev).TrimEnd();
                bool endsSentence = prevText.Length > 0 && ".!?:;。！？".Contains(prevText[^1]);
                double blockRight = Math.Max(prev.UMax, cur.UMax);
                bool shortLine = prev.UMax < blockRight - 4 * h;
                newParagraph = largeGap || sizeChange || columnChange || (endsSentence && shortLine);
            }
            starts[i] = newParagraph ? i : starts[i - 1];
        }
        return starts;
    }

    private double TypicalGap(int line)
    {
        var gaps = new List<double>();
        for (int k = Math.Max(1, line - 4); k <= Math.Min(_lines.Length - 1, line + 4); k++)
        {
            var a = _lines[k - 1];
            var b = _lines[k];
            if (a.U == b.U && b.VMin > a.VMin) gaps.Add(b.VMin - a.VMax);
        }
        if (gaps.Count == 0) return 0;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    private string LineText(TextLine line)
    {
        var sb = new StringBuilder();
        for (int i = line.Start; i < line.End; i++) sb.Append(_glyphs[i].Text);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ hit testing

    public int LineIndexOfCaret(int caret)
    {
        if (_lines.Length == 0) return -1;
        if (caret >= Length) return _lines.Length - 1;
        caret = Math.Max(0, caret);
        return _lineOf[caret];
    }

    private static (double U, double V) Project(TextLine line, Point p) =>
        (p.X * line.U.X + p.Y * line.U.Y, p.X * line.V.X + p.Y * line.V.Y);

    /// <summary>The line a point belongs to: the one whose band it is in, else the nearest one.</summary>
    public TextLine? LineAt(Point p)
    {
        TextLine? best = null;
        double bestScore = double.MaxValue;
        foreach (var line in _lines)
        {
            var (pu, pv) = Project(line, p);
            double du = Math.Max(0, Math.Max(line.UMin - pu, pu - line.UMax));
            double dv = Math.Max(0, Math.Max(line.VMin - pv, pv - line.VMax));
            // Across-the-line distance weighs more: a point in the gap between two lines of a
            // column belongs to that column, not to a line beside it in the next column.
            double score = du + 3 * dv;
            if (score < bestScore - 1e-12 || (Math.Abs(score - bestScore) <= 1e-12 && best != null &&
                Math.Abs(pv - (line.VMin + line.VMax) / 2) < Math.Abs(Project(best, p).V - (best.VMin + best.VMax) / 2)))
            {
                bestScore = score;
                best = line;
            }
        }
        return best;
    }

    /// <summary>The caret nearest a point: between the two characters whose centres surround it.</summary>
    public int HitTest(Point p)
    {
        var line = LineAt(p);
        if (line == null) return 0;
        return CaretInLine(line, Project(line, p).U);
    }

    private int CaretInLine(TextLine line, double pu)
    {
        for (int i = line.Start; i < line.End; i++)
        {
            var c = Center(_glyphs[i].Box);
            double cu = c.X * line.U.X + c.Y * line.U.Y;
            if (pu < cu) return i;
        }
        return line.End;
    }

    /// <summary>The character under a point (on its line, the nearest one), or -1.</summary>
    public int GlyphAt(Point p)
    {
        var line = LineAt(p);
        if (line == null || line.End <= line.Start) return -1;
        var (pu, _) = Project(line, p);
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = line.Start; i < line.End; i++)
        {
            var b = _glyphs[i].Box;
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var q in new[] { b.TopLeft, b.BottomRight, b.TopRight, b.BottomLeft })
            {
                double qu = q.X * line.U.X + q.Y * line.U.Y;
                lo = Math.Min(lo, qu); hi = Math.Max(hi, qu);
            }
            double dist = pu < lo ? lo - pu : pu > hi ? pu - hi : 0;
            if (dist < bestDist) { bestDist = dist; best = i; }
            if (dist == 0) break;
        }
        return best;
    }

    /// <summary>True when the point is over text (for the I-beam cursor): inside a line's band, near its extent.</summary>
    public bool IsOverText(Point p, double slack)
    {
        foreach (var line in _lines)
        {
            var (pu, pv) = Project(line, p);
            if (pu >= line.UMin - slack && pu <= line.UMax + slack && pv >= line.VMin - slack * 0.5 && pv <= line.VMax + slack * 0.5)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ units: word, sentence, line, paragraph

    internal enum CharClass { Word, Space, Punctuation, Break, Ideograph }

    internal static CharClass Classify(string text)
    {
        if (text is "\r" or "\n" or "\r\n") return CharClass.Break;
        int cp = char.ConvertToUtf32(text, 0);
        if (IsIdeograph(cp)) return CharClass.Ideograph;
        var cat = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        switch (cat)
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
            case UnicodeCategory.ConnectorPunctuation: // snake_case is one word
                return CharClass.Word;
            case UnicodeCategory.SpaceSeparator:
            case UnicodeCategory.LineSeparator:
            case UnicodeCategory.ParagraphSeparator:
            case UnicodeCategory.Control:
            case UnicodeCategory.Format:
                return CharClass.Space;
            default:
                return char.IsWhiteSpace(text, 0) ? CharClass.Space : CharClass.Punctuation;
        }
    }

    private static bool IsIdeograph(int cp) =>
        cp is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF
            or >= 0x3040 and <= 0x30FF; // Hiragana and Katakana: no spaces between words either

    private CharClass ClassAt(int i) => Classify(_glyphs[i].Text);

    /// <summary>Joiners that stay inside a word when letters or digits surround them: don't, 3.14, 1,000, l'été.</summary>
    private bool JoinsWord(int i)
    {
        if (i <= 0 || i >= Length - 1) return false;
        string t = _glyphs[i].Text;
        bool apostrophe = t is "'" or "’" or "ʼ";
        bool numeric = t is "." or "," && IsDigit(i - 1) && IsDigit(i + 1);
        return (apostrophe && ClassAt(i - 1) == CharClass.Word && ClassAt(i + 1) == CharClass.Word) || numeric;
    }

    private bool IsDigit(int i) => i >= 0 && i < Length && char.IsDigit(_glyphs[i].Text, 0);

    private bool InWord(int i) => i >= 0 && i < Length && (ClassAt(i) == CharClass.Word || JoinsWord(i));

    /// <summary>
    /// The word containing character <paramref name="glyph"/> — only the word: no trailing space.
    /// A run of punctuation is its own unit; an ideograph is a word by itself; on a space the
    /// nearest word on the line is chosen.
    /// </summary>
    public (int Start, int End) WordAt(int glyph)
    {
        if (Length == 0) return (0, 0);
        glyph = Math.Clamp(glyph, 0, Length - 1);
        var cls = ClassAt(glyph);
        if (cls is CharClass.Space or CharClass.Break)
        {
            // Nearest non-space on this line (left first on ties).
            var line = _lines[_lineOf[glyph]];
            int l = glyph - 1, r = glyph + 1;
            while (l >= line.Start || r < line.End)
            {
                if (l >= line.Start && ClassAt(l) is not (CharClass.Space or CharClass.Break)) return WordAt(l);
                if (r < line.End && ClassAt(r) is not (CharClass.Space or CharClass.Break)) return WordAt(r);
                l--; r++;
            }
            return (glyph, glyph + 1);
        }
        if (cls == CharClass.Ideograph)
            return (glyph, glyph + 1);
        if (cls == CharClass.Punctuation && !JoinsWord(glyph))
        {
            int ps = glyph, pe = glyph + 1;
            while (ps > 0 && ClassAt(ps - 1) == CharClass.Punctuation && !JoinsWord(ps - 1)) ps--;
            while (pe < Length && ClassAt(pe) == CharClass.Punctuation && !JoinsWord(pe)) pe++;
            return (ps, pe);
        }
        int s = glyph, e = glyph + 1;
        while (s > 0 && InWord(s - 1)) s--;
        while (e < Length && InWord(e)) e++;
        // A hyphen that splits a word across lines joins its halves (the soft hyphen case).
        if (e < Length && _glyphs[e].IsHyphen && e + 1 < Length)
        {
            int n = e + 1;
            while (n < Length && _glyphs[n].IsLineBreak) n++;
            if (n > e + 1 && InWord(n))
            {
                e = n;
                while (e < Length && InWord(e)) e++;
            }
        }
        if (s > 1 && _glyphs[s - 1].IsLineBreak)
        {
            int b = s - 1;
            while (b > 0 && _glyphs[b].IsLineBreak) b--;
            if (_glyphs[b].IsHyphen && b > 0 && InWord(b - 1))
            {
                s = b;
                while (s > 0 && InWord(s - 1)) s--;
            }
        }
        return (s, e);
    }

    /// <summary>Start of the word at or before a caret (Ctrl+Left).</summary>
    public int PreviousWordStart(int caret)
    {
        int i = Math.Min(caret, Length) - 1;
        while (i >= 0 && ClassAt(i) is CharClass.Space or CharClass.Break) i--;
        if (i < 0) return 0;
        return WordAt(i).Start;
    }

    /// <summary>Start of the next word after a caret (Ctrl+Right), as in a word processor.</summary>
    public int NextWordStart(int caret)
    {
        if (caret >= Length) return Length;
        int i = Math.Max(0, caret);
        if (ClassAt(i) is not (CharClass.Space or CharClass.Break))
            i = WordAt(i).End;
        while (i < Length && ClassAt(i) is CharClass.Space or CharClass.Break) i++;
        return i;
    }

    public (int Start, int End) LineRange(int lineIndex)
    {
        var line = _lines[Math.Clamp(lineIndex, 0, _lines.Length - 1)];
        return (line.Start, line.End);
    }

    /// <summary>The paragraph of a line: its lines' characters, the final break excluded.</summary>
    public (int Start, int End) ParagraphRange(int lineIndex)
    {
        if (_lines.Length == 0) return (0, 0);
        lineIndex = Math.Clamp(lineIndex, 0, _lines.Length - 1);
        int first = _paragraphStartLine[lineIndex];
        int last = lineIndex;
        while (last + 1 < _lines.Length && _paragraphStartLine[last + 1] == first) last++;
        return (_lines[first].Start, _lines[last].End);
    }

    /// <summary>The sentence containing a character (Ctrl+click in a word processor), within its paragraph.</summary>
    public (int Start, int End) SentenceAt(int glyph)
    {
        if (Length == 0) return (0, 0);
        glyph = Math.Clamp(glyph, 0, Length - 1);
        var (ps, pe) = ParagraphRange(_lineOf[glyph]);
        int s = glyph;
        while (s > ps && !EndsSentence(s - 1)) s--;
        while (s < pe && ClassAt(s) is CharClass.Space or CharClass.Break) s++;
        int e = glyph;
        while (e < pe && !EndsSentence(e)) e++;
        if (e < pe) e++;
        while (e < pe && _glyphs[e].Text is "\"" or "'" or ")" or "”" or "’") e++;
        return (s, Math.Max(e, s));
    }

    private bool EndsSentence(int i)
    {
        string t = _glyphs[i].Text;
        if (t is "。" or "！" or "？") return true;
        if (t is not ("." or "!" or "?")) return false;
        // "3.14", "e.g." inside a word, and an ellipsis in progress are not sentence ends.
        int n = i + 1;
        if (n >= Length) return true;
        var next = ClassAt(n);
        return next is CharClass.Space or CharClass.Break;
    }

    // ------------------------------------------------------------------ keyboard navigation

    /// <summary>Caret one line up or down, keeping the horizontal position (<paramref name="preferredU"/>).</summary>
    public int MoveLine(int caret, int delta, ref double? preferredU)
    {
        if (_lines.Length == 0) return 0;
        int li = LineIndexOfCaret(caret);
        var line = _lines[li];
        preferredU ??= CaretU(caret);
        int target = li + delta;
        if (target < 0) return 0;
        if (target >= _lines.Length) return Length;
        var t = _lines[target];
        // Same direction lines share u; project the preferred u onto the target line.
        return CaretInLine(t, t.U == line.U ? preferredU.Value : (t.UMin + t.UMax) / 2);
    }

    private double CaretU(int caret)
    {
        int li = LineIndexOfCaret(caret);
        var line = _lines[li];
        if (caret < line.End && caret >= line.Start)
        {
            var b = _glyphs[caret].Box;
            return Math.Min(b.Left * line.U.X + b.Top * line.U.Y, b.Right * line.U.X + b.Bottom * line.U.Y);
        }
        return line.UMax;
    }

    public int LineStart(int caret) => _lines.Length == 0 ? 0 : _lines[LineIndexOfCaret(caret)].Start;
    public int LineEnd(int caret) => _lines.Length == 0 ? 0 : _lines[LineIndexOfCaret(caret)].End;

    // ------------------------------------------------------------------ output

    /// <summary>
    /// One band per line for the characters [start, end): from the first to the last selected
    /// character, at the full height of the line, so the spaces between words are covered and
    /// the selection reads as continuous text, like a word processor's.
    /// </summary>
    public IReadOnlyList<(Rect Rect, int Line, int Start, int End)> SelectionBands(int start, int end)
    {
        var bands = new List<(Rect, int, int, int)>();
        if (end <= start || _lines.Length == 0) return bands;
        foreach (var line in _lines)
        {
            int s = Math.Max(start, line.Start), e = Math.Min(end, line.End);
            if (e <= s) continue;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = s; i < e; i++)
            {
                var b = _glyphs[i].Box;
                foreach (var q in new[] { b.TopLeft, b.BottomRight })
                {
                    double qu = q.X * line.U.X + q.Y * line.U.Y;
                    lo = Math.Min(lo, qu); hi = Math.Max(hi, qu);
                }
            }
            // The band's corners in page space from (u, v) back to (x, y).
            Point P(double u, double v) => new(u * line.U.X + v * line.V.X, u * line.U.Y + v * line.V.Y);
            var corners = new[] { P(lo, line.VMin), P(hi, line.VMin), P(lo, line.VMax), P(hi, line.VMax) };
            double x0 = corners.Min(c => c.X), x1 = corners.Max(c => c.X), y0 = corners.Min(c => c.Y), y1 = corners.Max(c => c.Y);
            bands.Add((new Rect(x0, y0, Math.Max(1e-5, x1 - x0), Math.Max(1e-5, y1 - y0)), line.Index, s, e));
        }
        return bands;
    }

    /// <summary>
    /// The text of [start, end) as it reads: lines joined by line breaks, a hyphen that breaks a
    /// word across lines removed and the halves joined, trailing spaces of each line dropped.
    /// </summary>
    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, 0, Length);
        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            var g = _glyphs[i];
            if (g.IsLineBreak)
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                if (i + 1 < end) sb.Append(Environment.NewLine);
                continue;
            }
            if (g.IsHyphen && i + 1 < Length && _glyphs[i + 1].IsLineBreak)
            {
                // Soft hyphen at a line end: "exam-\nple" copies as "example".
                int n = i + 1;
                while (n < Length && _glyphs[n].IsLineBreak) n++;
                if (n < end && InWord(n))
                {
                    i = n - 1;
                    continue;
                }
            }
            sb.Append(g.Text == " " ? " " : g.Text);
        }
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\n' || sb[^1] == '\r')) sb.Length--;
        return sb.ToString();
    }

    /// <summary>Words as (text, box) for the consumers that work word by word (search, export).</summary>
    public IEnumerable<(string Text, Rect Box, int Start, int End)> Words()
    {
        int i = 0;
        while (i < Length)
        {
            var cls = ClassAt(i);
            if (cls is CharClass.Space or CharClass.Break) { i++; continue; }
            // A space-delimited token, as the previous word-level extraction produced.
            int s = i;
            while (i < Length && ClassAt(i) is not (CharClass.Space or CharClass.Break)
                   && (i == s || _lineOf[i] == _lineOf[s])) i++;
            var r = _glyphs[s].Box;
            var sb = new StringBuilder();
            for (int k = s; k < i; k++) { r.Union(_glyphs[k].Box); sb.Append(_glyphs[k].Text); }
            yield return (sb.ToString(), r, s, i);
        }
    }
}
