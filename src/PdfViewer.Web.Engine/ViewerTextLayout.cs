using System.Globalization;
using System.Text;
using PdfEngine.Vector.Text;

namespace PdfViewer.Web.Engine;

/// <summary>A point on a page as shown: PDF points from the top-left corner of the rotated page.</summary>
public readonly record struct ViewerPoint(double X, double Y);

/// <summary>
/// The text of one page for touch selection: the engine's characters in reading order with their
/// loose boxes (font ascent to descent), grouped into lines, with caret positions between them. It
/// follows the desktop viewer's selection rules (src/PdfViewer/Text/PageTextLayout.cs): the same
/// line building, hit testing, word boundaries, selection bands and copied text, in shown-page
/// points instead of WPF types, so that it runs on Android and in the browser. Sentences,
/// paragraphs and keyboard moves of the desktop class are not ported.
/// </summary>
public sealed class ViewerTextLayout
{
    /// <summary>One character: its text and box (shown-page points), and whether the extractor inferred it.</summary>
    public readonly record struct Glyph(string Text, ViewerRect Box, bool IsGenerated, bool IsHyphen)
    {
        public bool IsLineBreak => Text is "\r" or "\n" or "\r\n";
    }

    /// <summary>A line: glyphs [<see cref="Start"/>, <see cref="End"/>), its trailing break excluded; u runs along the text, v across it.</summary>
    public sealed record Line(int Index, int Start, int End, int NextStart, double Ux, double Uy, double UMin, double UMax, double VMin, double VMax)
    {
        public double Vx => -Uy;
        public double Vy => Ux;
    }

    public static readonly ViewerTextLayout Empty = new(Array.Empty<Glyph>());

    private readonly Glyph[] _glyphs;
    private readonly Line[] _lines;
    private readonly int[] _lineOf;

    public IReadOnlyList<Glyph> Glyphs => _glyphs;
    public IReadOnlyList<Line> Lines => _lines;
    public int Length => _glyphs.Length;
    public bool IsEmpty => _lines.Length == 0;

    public ViewerTextLayout(IReadOnlyList<Glyph> glyphs)
    {
        var list = new List<Glyph>(glyphs.Count);
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (g.Text == "\r" && i + 1 < glyphs.Count && glyphs[i + 1].Text == "\n")
            {
                list.Add(g with { Text = "\r\n" });
                i++;
                continue;
            }
            if (g.Text.Length == 0 || g.Text == "\0") continue;
            list.Add(g);
        }
        _glyphs = list.ToArray();
        _lines = BuildLines(_glyphs, out _lineOf);
        FillGeneratedBoxes();
    }

    /// <summary>The layout of an extracted page: characters with their loose boxes on the shown page.</summary>
    public static ViewerTextLayout FromPage(PdfTextPage page)
    {
        var glyphs = new List<Glyph>(page.CharCount);
        int count = page.CharCount;
        for (int i = 0; i < count; i++)
        {
            char cp = page.GetUnicode(i);
            if (cp == 0) continue;
            int index = i;
            string text;
            if (char.IsHighSurrogate(cp) && i + 1 < count && char.IsLowSurrogate(page.GetUnicode(i + 1)))
            {
                text = new string(new[] { cp, page.GetUnicode(i + 1) });
                i++;
            }
            else if (char.IsSurrogate(cp))
            {
                continue;
            }
            else
            {
                text = cp.ToString();
            }
            var box = default(ViewerRect);
            if (text is not ("\r" or "\n") && page.HasTextObject(index))
            {
                var r = page.GetLooseCharBox(index);
                r.Normalize();
                if (r.IsEmpty)
                {
                    r = page.GetCharBox(index);
                    r.Normalize();
                }
                if (!r.IsEmpty) box = ViewerDocument.ToShown(page, r);
            }
            glyphs.Add(new Glyph(text, box, page.IsGenerated(index), page.IsHyphen(index)));
        }
        return new ViewerTextLayout(glyphs);
    }

    // ------------------------------------------------------------------ construction

    private static bool Visible(in Glyph g) => !g.IsLineBreak && g.Box.Width > 0 && g.Box.Height > 0;

    private static Line[] BuildLines(Glyph[] glyphs, out int[] lineOf)
    {
        var map = new int[glyphs.Length];
        var lines = new List<Line>();
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
            lines[^1] = prev with { NextStart = next };
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
            // A visible glyph that does not continue the previous one along the line starts a new one.
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

    private static bool StartsNewLine(in Glyph prev, in Glyph cur)
    {
        var a = prev.Box;
        var b = cur.Box;
        double h = Math.Max(Math.Min(a.Height, b.Height), 1e-6);
        double overlap = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (overlap < 0.3 * h) return true; // different rows
        return b.X < a.X - 0.5 * h;           // same row but moving back (a new column or a return)
    }

    private static Line? MakeLine(Glyph[] glyphs, int index, int start, int end, int next)
    {
        var boxes = new List<ViewerRect>();
        for (int i = start; i < end; i++)
            if (Visible(glyphs[i])) boxes.Add(glyphs[i].Box);
        if (boxes.Count == 0) return null;

        // Direction: from the first to the last glyph centre; a single glyph reads left to right.
        double ux = 1, uy = 0;
        var (c0x, c0y) = Center(boxes[0]);
        var (c1x, c1y) = Center(boxes[^1]);
        double dx = c1x - c0x, dy = c1y - c0y, length = Math.Sqrt(dx * dx + dy * dy);
        if (boxes.Count > 1 && length > 0.5 * Math.Min(boxes[0].Height, boxes[0].Width))
        {
            ux = dx / length;
            uy = dy / length;
            // Snap nearly axis-aligned lines: jitter in glyph positions should not tilt the band.
            if (Math.Abs(uy) < 0.05) (ux, uy) = (Math.Sign(ux) == 0 ? 1 : Math.Sign(ux), 0);
            else if (Math.Abs(ux) < 0.05) (ux, uy) = (0, Math.Sign(uy));
        }
        double vx = -uy, vy = ux;
        double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
        foreach (var r in boxes)
            foreach (var (px, py) in Corners(r))
            {
                double pu = px * ux + py * uy, pv = px * vx + py * vy;
                umin = Math.Min(umin, pu); umax = Math.Max(umax, pu);
                vmin = Math.Min(vmin, pv); vmax = Math.Max(vmax, pv);
            }
        return new Line(index, start, end, next, ux, uy, umin, umax, vmin, vmax);
    }

    private static (double X, double Y) Center(ViewerRect r) => (r.X + r.Width / 2, r.Y + r.Height / 2);

    private static (double X, double Y)[] Corners(ViewerRect r) =>
        new[] { (r.X, r.Y), (r.X + r.Width, r.Y), (r.X, r.Y + r.Height), (r.X + r.Width, r.Y + r.Height) };

    /// <summary>Generated spaces get the gap between their neighbours, at the line's height.</summary>
    private void FillGeneratedBoxes()
    {
        foreach (var line in _lines)
        {
            bool horizontal = line.Uy == 0;
            double top = double.MaxValue, bottom = double.MinValue;
            for (int i = line.Start; i < line.End; i++)
                if (Visible(_glyphs[i]))
                {
                    top = Math.Min(top, _glyphs[i].Box.Y);
                    bottom = Math.Max(bottom, _glyphs[i].Box.Y + _glyphs[i].Box.Height);
                }
            for (int i = line.Start; i < line.End; i++)
            {
                if (Visible(_glyphs[i])) continue;
                int p = i - 1;
                while (p >= line.Start && !Visible(_glyphs[p])) p--;
                int n = i + 1;
                while (n < line.End && !Visible(_glyphs[n])) n++;
                ViewerRect box;
                if (p >= line.Start && n < line.End)
                {
                    var a = _glyphs[p].Box;
                    var b = _glyphs[n].Box;
                    double x0 = a.X + a.Width, y0 = a.Y, x1 = b.X, y1 = b.Y + b.Height;
                    box = horizontal
                        ? new ViewerRect(Math.Min(x0, x1), top, Math.Max(1e-5, Math.Abs(x1 - x0)), bottom - top)
                        : new ViewerRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(1e-5, Math.Abs(x1 - x0)), Math.Max(1e-5, Math.Abs(y1 - y0)));
                }
                else if (p >= line.Start)
                {
                    var a = _glyphs[p].Box;
                    box = new ViewerRect(a.X + a.Width, a.Y, Math.Max(1e-4, a.Height * 0.25), a.Height);
                }
                else if (n < line.End)
                {
                    var b = _glyphs[n].Box;
                    box = new ViewerRect(Math.Max(0, b.X - b.Height * 0.25), b.Y, b.Height * 0.25, b.Height);
                }
                else
                {
                    continue;
                }
                _glyphs[i] = _glyphs[i] with { Box = box };
            }
        }
    }

    // ------------------------------------------------------------------ hit testing

    private static (double U, double V) Project(Line line, ViewerPoint p) =>
        (p.X * line.Ux + p.Y * line.Uy, p.X * line.Vx + p.Y * line.Vy);

    /// <summary>The line a point belongs to: the one whose band it is in, else the nearest one.</summary>
    public Line? LineAt(ViewerPoint p)
    {
        Line? best = null;
        double bestScore = double.MaxValue;
        foreach (var line in _lines)
        {
            var (pu, pv) = Project(line, p);
            double du = Math.Max(0, Math.Max(line.UMin - pu, pu - line.UMax));
            double dv = Math.Max(0, Math.Max(line.VMin - pv, pv - line.VMax));
            // Across-the-line distance weighs more, so a point between two lines of a column stays in that column.
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

    /// <summary>The distance from a point to the nearest line's band, in points (infinity when the page has no text).</summary>
    public double DistanceToText(ViewerPoint p)
    {
        double best = double.PositiveInfinity;
        foreach (var line in _lines)
        {
            var (pu, pv) = Project(line, p);
            double du = Math.Max(0, Math.Max(line.UMin - pu, pu - line.UMax));
            double dv = Math.Max(0, Math.Max(line.VMin - pv, pv - line.VMax));
            best = Math.Min(best, Math.Sqrt(du * du + dv * dv));
        }
        return best;
    }

    /// <summary>The caret nearest a point: between the two characters whose centres surround it.</summary>
    public int HitTest(ViewerPoint p)
    {
        var line = LineAt(p);
        return line == null ? 0 : CaretInLine(line, Project(line, p).U);
    }

    private int CaretInLine(Line line, double pu)
    {
        for (int i = line.Start; i < line.End; i++)
        {
            var (cx, cy) = Center(_glyphs[i].Box);
            if (pu < cx * line.Ux + cy * line.Uy) return i;
        }
        return line.End;
    }

    /// <summary>The character under a point (on its line, the nearest one), or -1.</summary>
    public int GlyphAt(ViewerPoint p)
    {
        var line = LineAt(p);
        if (line == null || line.End <= line.Start) return -1;
        var (pu, _) = Project(line, p);
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = line.Start; i < line.End; i++)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var (qx, qy) in Corners(_glyphs[i].Box))
            {
                double qu = qx * line.Ux + qy * line.Uy;
                lo = Math.Min(lo, qu); hi = Math.Max(hi, qu);
            }
            double dist = pu < lo ? lo - pu : pu > hi ? pu - hi : 0;
            if (dist < bestDist) { bestDist = dist; best = i; }
            if (dist == 0) break;
        }
        return best;
    }

    // ------------------------------------------------------------------ words

    private enum CharClass { Word, Space, Punctuation, Break, Ideograph }

    private static CharClass Classify(string text)
    {
        if (text is "\r" or "\n" or "\r\n") return CharClass.Break;
        int cp = char.ConvertToUtf32(text, 0);
        if (cp is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF or >= 0x3040 and <= 0x30FF)
            return CharClass.Ideograph; // no spaces between words: each is a word by itself
        switch (CharUnicodeInfo.GetUnicodeCategory(text, 0))
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
            case UnicodeCategory.ConnectorPunctuation:
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

    private CharClass ClassAt(int i) => Classify(_glyphs[i].Text);

    /// <summary>Joiners inside a word when letters or digits surround them: don't, 3.14, 1,000.</summary>
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
    /// The word containing character <paramref name="glyph"/>, without a trailing space. A run of
    /// punctuation is its own unit, an ideograph is a word by itself, and on a space the nearest
    /// word on the line is chosen; a hyphen that splits a word across lines joins its halves.
    /// </summary>
    public (int Start, int End) WordAt(int glyph)
    {
        if (Length == 0) return (0, 0);
        glyph = Math.Clamp(glyph, 0, Length - 1);
        var cls = ClassAt(glyph);
        if (cls is CharClass.Space or CharClass.Break)
        {
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
        if (cls == CharClass.Ideograph) return (glyph, glyph + 1);
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

    // ------------------------------------------------------------------ output

    /// <summary>
    /// One band per line for the characters [start, end): from the first to the last selected
    /// character at the full height of the line, so the spaces between words are covered.
    /// </summary>
    public IReadOnlyList<ViewerRect> SelectionBands(int start, int end)
    {
        var bands = new List<ViewerRect>();
        if (end <= start) return bands;
        foreach (var line in _lines)
        {
            int s = Math.Max(start, line.Start), e = Math.Min(end, line.End);
            if (e <= s) continue;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = s; i < e; i++)
                foreach (var (qx, qy) in Corners(_glyphs[i].Box))
                {
                    double qu = qx * line.Ux + qy * line.Uy;
                    lo = Math.Min(lo, qu); hi = Math.Max(hi, qu);
                }
            (double X, double Y) P(double u, double v) => (u * line.Ux + v * line.Vx, u * line.Uy + v * line.Vy);
            var corners = new[] { P(lo, line.VMin), P(hi, line.VMin), P(lo, line.VMax), P(hi, line.VMax) };
            double x0 = corners.Min(c => c.X), x1 = corners.Max(c => c.X), y0 = corners.Min(c => c.Y), y1 = corners.Max(c => c.Y);
            bands.Add(new ViewerRect(x0, y0, Math.Max(1e-5, x1 - x0), Math.Max(1e-5, y1 - y0)));
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
                if (i + 1 < end) sb.Append('\n');
                continue;
            }
            if (g.IsHyphen && i + 1 < Length && _glyphs[i + 1].IsLineBreak)
            {
                int n = i + 1;
                while (n < Length && _glyphs[n].IsLineBreak) n++;
                if (n < end && InWord(n))
                {
                    i = n - 1;
                    continue;
                }
            }
            sb.Append(g.Text);
        }
        while (sb.Length > 0 && sb[^1] is ' ' or '\n' or '\r') sb.Length--;
        return sb.ToString();
    }
}

/// <summary>
/// A touch selection on one page: a long press selects the word under the finger, and two
/// handles move its start and its end caret by caret. When a handle is dragged past the other,
/// they swap, so the selection never turns inside out.
/// </summary>
public sealed class ViewerTextSelection
{
    public ViewerTextSelection(int page, ViewerTextLayout layout)
    {
        Page = page;
        Layout = layout;
    }

    public int Page { get; }
    public ViewerTextLayout Layout { get; }
    public int Start { get; private set; }
    public int End { get; private set; }
    public bool IsEmpty => End <= Start;

    /// <summary>Selects the word under <paramref name="p"/>; false when the point is farther than <paramref name="slack"/> points from any text.</summary>
    public bool SelectWordAt(ViewerPoint p, double slack)
    {
        if (Layout.IsEmpty || Layout.DistanceToText(p) > slack) return false;
        int glyph = Layout.GlyphAt(p);
        if (glyph < 0) return false;
        (Start, End) = Layout.WordAt(glyph);
        return !IsEmpty;
    }

    /// <summary>Moves the start handle (<paramref name="startHandle"/>) or the end handle to the caret nearest <paramref name="p"/>; returns whether the handles swapped.</summary>
    public bool MoveHandle(bool startHandle, ViewerPoint p)
    {
        int caret = Layout.HitTest(p);
        if (startHandle)
        {
            if (caret < End) { Start = caret; return false; }
            // Past the end: the old end is the new start, and the dragged handle is now the end.
            (Start, End) = (End, caret);
        }
        else
        {
            if (caret > Start) { End = caret; return false; }
            (Start, End) = (caret, Start);
        }
        // At least one character stays selected.
        if (End <= Start)
        {
            if (End < Layout.Length) End = Start + 1;
            else Start = Math.Max(0, End - 1);
        }
        return true;
    }

    /// <summary>
    /// A mouse drag: the characters between the caret nearest <paramref name="anchor"/> (where the
    /// button went down) and the caret nearest <paramref name="focus"/>, in either order. Empty
    /// until the pointer passes a character's centre.
    /// </summary>
    public void SelectBetween(ViewerPoint anchor, ViewerPoint focus)
    {
        if (Layout.IsEmpty)
        {
            Start = End = 0;
            return;
        }
        int a = Layout.HitTest(anchor), b = Layout.HitTest(focus);
        (Start, End) = a <= b ? (a, b) : (b, a);
    }

    /// <summary>Selects nothing.</summary>
    public void Clear() => Start = End = 0;

    /// <summary>Selects the whole page.</summary>
    public void SelectAll()
    {
        Start = 0;
        End = Layout.Length;
    }

    public IReadOnlyList<ViewerRect> Bands => Layout.SelectionBands(Start, End);

    public string Text => Layout.GetText(Start, End);

    /// <summary>The start handle sits at the bottom-left of the first band, the end handle at the bottom-right of the last (points).</summary>
    public (ViewerPoint Start, ViewerPoint End)? HandlePositions()
    {
        var bands = Bands;
        if (bands.Count == 0) return null;
        var first = bands[0];
        var last = bands[^1];
        return (new ViewerPoint(first.X, first.Y + first.Height), new ViewerPoint(last.X + last.Width, last.Y + last.Height));
    }
}
