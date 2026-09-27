using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;

namespace PdfEngine.Vector.Editing;

/// <summary>How the lines of a paragraph are aligned.</summary>
public enum PdfTextAlignment { Left, Center, Right, Justified }

/// <summary>A line of text: glyphs along one baseline, in reading order.</summary>
internal sealed class TextLine
{
    public List<ContentGlyph> Glyphs = new();
    /// <summary>The line's direction (unit vector along the baseline) and up (perpendicular).</summary>
    public PdfPoint U, V;
    /// <summary>Where its baseline starts, and its extent along it (from the block's origin, in user units).</summary>
    public double Start, End, Baseline;
    /// <summary>Em height in user units.</summary>
    public double Size;
    public string Text = string.Empty;
    /// <summary>For every character of <see cref="Text"/>: its glyph (index into Glyphs), or -1 for a space between words.</summary>
    public List<int> CharGlyph = new();
    public bool HardBreakAfter;
}

/// <summary>A paragraph (or a single line) of text the page shows, as one editable unit.</summary>
internal sealed class TextBlock
{
    public int Id;
    public List<TextLine> Lines = new();
    public PdfPoint Origin, U, V;
    public PdfTextAlignment Alignment;
    public double Left, Right, Pitch, WordGap;
    public string Text = string.Empty;
    /// <summary>For every character of <see cref="Text"/>: (line, glyph in line or -1, is a line separator).</summary>
    public List<(int Line, int Glyph, bool Separator)> Chars = new();
    /// <summary>Start of each line's characters in <see cref="Text"/>.</summary>
    public List<int> LineStarts = new();
    public bool Editable = true;
    /// <summary>Copies of this paragraph drawn a little offset (a shadow or embossing): edited with it.</summary>
    public List<TextBlock> Twins = new();
    /// <summary>A twin of a paragraph drawn later (over it): not offered for editing on its own.</summary>
    public bool IsShadow;
    public PdfRect Bounds;
    public List<PdfRect> LineBounds = new();
}

/// <summary>Finds lines and paragraphs among a page's glyphs.</summary>
internal static class TextBlocks
{
    public static List<TextBlock> Build(IReadOnlyList<ContentGlyph> glyphs)
    {
        var lines = BuildLines(glyphs);
        var blocks = Group(lines);
        for (int i = 0; i < blocks.Count; i++) blocks[i].Id = i;
        // The same text drawn twice, a hair apart: a drop shadow. Both change together; the copy
        // drawn last (on top) is the one shown for editing.
        foreach (var a in blocks)
            foreach (var b in blocks)
            {
                if (ReferenceEquals(a, b) || a.Text != b.Text || a.Lines.Count != b.Lines.Count) continue;
                double size = a.Lines.Max(l => l.Size);
                var d = new PdfPoint(a.Origin.X - b.Origin.X, a.Origin.Y - b.Origin.Y);
                if (Length(d) > 0.2 * size || Dot(a.U, b.U) < 0.999) continue;
                a.Twins.Add(b);
                int lastA = a.Lines.SelectMany(l => l.Glyphs).Max(g => g.Op), lastB = b.Lines.SelectMany(l => l.Glyphs).Max(g => g.Op);
                if (lastA < lastB) a.IsShadow = true;
            }
        return blocks;
    }

    internal static double Length(PdfPoint p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
    internal static double Dot(PdfPoint a, PdfPoint b) => a.X * b.X + a.Y * b.Y;
    private static PdfPoint Sub(PdfPoint a, PdfPoint b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Unit baseline direction of a glyph, and the unit up direction.</summary>
    internal static (PdfPoint U, PdfPoint V) Frame(PdfMatrix m)
    {
        double len = Math.Sqrt(m.A * m.A + m.B * m.B);
        var u = len > 1e-12 ? new PdfPoint(m.A / len, m.B / len) : new PdfPoint(1, 0);
        var v = m.Determinant >= 0 ? new PdfPoint(-u.Y, u.X) : new PdfPoint(u.Y, -u.X);
        return (u, v);
    }

    /// <summary>A glyph's advance, and its em height, in user units.</summary>
    internal static double UserAdvance(ContentGlyph g) => g.Advance * Math.Sqrt(g.Matrix.A * g.Matrix.A + g.Matrix.B * g.Matrix.B);
    internal static double EmHeight(ContentGlyph g) => Math.Abs(g.Style.FontSize) * Math.Sqrt(g.Matrix.C * g.Matrix.C + g.Matrix.D * g.Matrix.D);

    internal static PdfRect GlyphBox(ContentGlyph g)
    {
        var font = g.Style.Font;
        double fs = g.Style.FontSize, ascent = font != null ? Math.Max(font.Ascent, 0.7) : 0.9, descent = font != null ? Math.Min(font.Descent, -0.15) : -0.25;
        double w = Math.Max(g.Width0 / 1000.0 * fs * g.Style.Scaling / 100.0, 0.01);
        return g.Matrix.Transform(new PdfRect(0, g.Style.Rise + descent * fs, w, (ascent - descent) * fs));
    }

    private static List<TextLine> BuildLines(IReadOnlyList<ContentGlyph> glyphs)
    {
        var lines = new List<TextLine>();
        TextLine? current = null;
        PdfPoint origin = default;
        foreach (var g in glyphs)
        {
            // Invisible text (a scan's recognized layer) and vertical writing are not edited here.
            if (g.Style.RenderMode == 3 || g.Style.Vertical || g.Style.FontResource.Length == 0) continue;
            double size = EmHeight(g);
            if (size < 0.5) continue;
            var (u, v) = Frame(g.Matrix);
            if (current != null)
            {
                var d = Sub(g.Origin, origin);
                double s = Dot(d, current.U), t = Dot(d, current.V);
                bool sameDirection = Dot(u, current.U) > 0.999;
                double tol = Math.Max(size, current.Size);
                if (sameDirection && g.Context == current.Glyphs[0].Context && Math.Abs(t - current.Baseline) < 0.25 * tol && s >= current.End - 0.5 * tol && s <= current.End + 1.5 * tol)
                {
                    current.Glyphs.Add(g);
                    current.End = Math.Max(current.End, s + UserAdvance(g));
                    current.Size = Math.Max(current.Size, size);
                    continue;
                }
            }
            origin = g.Origin;
            current = new TextLine { U = u, V = v, Start = 0, End = UserAdvance(g), Baseline = 0, Size = size };
            current.Glyphs.Add(g);
            lines.Add(current);
        }

        // Every line measured in its own frame, starting at its first glyph.
        foreach (var line in lines) Measure(line);

        // Pieces of one line shown separately (a word at a time, or out of order) become one line.
        // Candidates are near each other in (direction, baseline) order, so only neighbours are compared.
        bool merged = true;
        while (merged)
        {
            merged = false;
            var order = lines.OrderBy(Angle).ThenBy(l => Dot(l.Glyphs[0].Origin, l.V)).ToList();
            for (int i = 0; i < order.Count; i++)
                for (int j = i + 1; j < order.Count; j++)
                {
                    var a = order[i];
                    var b = order[j];
                    if (Angle(a) != Angle(b)) break;
                    double tol = Math.Max(a.Size, b.Size);
                    if (Dot(b.Glyphs[0].Origin, a.V) - Dot(a.Glyphs[0].Origin, a.V) > 0.25 * tol) break;
                    if (Dot(a.U, b.U) < 0.999 || Math.Min(a.Size, b.Size) < 0.7 * tol || a.Glyphs[0].Context != b.Glyphs[0].Context) continue;
                    double bStart = Dot(Sub(b.Glyphs[0].Origin, a.Glyphs[0].Origin), a.U), bEnd = bStart + (b.End - b.Start);
                    double gapAfter = bStart - a.End, gapBefore = -bEnd;
                    bool adjacent = (gapAfter >= -0.3 * tol && gapAfter <= 1.0 * tol) || (gapBefore >= -0.3 * tol && gapBefore <= 1.0 * tol);
                    if (!adjacent) continue;
                    a.Glyphs.AddRange(b.Glyphs);
                    lines.Remove(b);
                    order.RemoveAt(j--);
                    Measure(a);
                    merged = true;
                }
        }
        return lines;
    }

    private static double Angle(TextLine l) => Math.Round(Math.Atan2(l.U.Y, l.U.X), 2);

    /// <summary>Orders a line's glyphs along the baseline and works out its extent and text.</summary>
    private static void Measure(TextLine line)
    {
        var o = line.Glyphs[0].Origin;
        double S(ContentGlyph g) => Dot(Sub(g.Origin, o), line.U);
        var ordered = line.Glyphs.Select((g, i) => (g, i)).OrderBy(x => S(x.g)).ThenBy(x => x.i).Select(x => x.g).ToList();
        line.Glyphs = ordered;
        var first = ordered[0].Origin;
        double shift = Dot(Sub(first, o), line.U);
        line.Start = 0;
        line.End = ordered.Max(g => S(g) + UserAdvance(g)) - shift;
        line.Size = ordered.Max(EmHeight);

        var text = new StringBuilder();
        line.CharGlyph.Clear();
        double prevEnd = double.NaN;
        for (int i = 0; i < ordered.Count; i++)
        {
            var g = ordered[i];
            double s = S(g) - shift;
            bool prevSpace = text.Length > 0 && text[^1] == ' ';
            bool isSpace = g.Text == " " || g.Text.Trim().Length == 0 && g.Text.Length > 0;
            if (!double.IsNaN(prevEnd) && !prevSpace && !isSpace && s - prevEnd > 0.2 * EmHeight(g))
            {
                text.Append(' ');
                line.CharGlyph.Add(-1);
            }
            string t = isSpace ? " " : g.Text;
            if (isSpace && prevSpace) t = string.Empty; // runs of spaces read as one
            foreach (char ch in t)
            {
                text.Append(ch);
                line.CharGlyph.Add(i);
            }
            prevEnd = Math.Max(double.IsNaN(prevEnd) ? s : prevEnd, s + UserAdvance(g));
        }
        // No spaces at the ends: they are not part of the line's words.
        while (text.Length > 0 && text[^1] == ' ') { text.Length--; line.CharGlyph.RemoveAt(line.CharGlyph.Count - 1); }
        while (text.Length > 0 && text[0] == ' ') { text.Remove(0, 1); line.CharGlyph.RemoveAt(0); }
        line.Text = text.ToString();
    }

    private static List<TextBlock> Group(List<TextLine> lines)
    {
        var blocks = new List<TextBlock>();
        var open = new List<(TextBlock Block, TextLine Last)>();
        // Top to bottom in each line's own orientation (the reading order of paragraphs).
        var ordered = lines.Where(l => l.Text.Length > 0)
            .OrderBy(l => Math.Round(Math.Atan2(l.U.Y, l.U.X), 2))
            .ThenByDescending(l => Dot(l.Glyphs[0].Origin, l.V))
            .ThenBy(l => Dot(l.Glyphs[0].Origin, l.U))
            .ToList();
        foreach (var line in ordered)
        {
            (TextBlock Block, TextLine Last)? best = null;
            double bestDelta = double.MaxValue;
            foreach (var candidate in open)
            {
                var (block, last) = candidate;
                if (Dot(block.U, line.U) < 0.999 || block.Lines[0].Glyphs[0].Context != line.Glyphs[0].Context) continue;
                double ratio = line.Size / last.Size;
                if (ratio < 0.8 || ratio > 1.25) continue;
                var lo = line.Glyphs[0].Origin;
                double delta = Dot(Sub(last.Glyphs[0].Origin, lo), block.V);
                double size = Math.Max(line.Size, last.Size);
                if (delta < 0.8 * size || delta > 1.8 * size) continue;
                if (block.Lines.Count >= 2 && Math.Abs(delta - block.Pitch) > 0.2 * size) continue;
                // Along the baseline, relative to the block's origin.
                double start = Dot(Sub(lo, block.Origin), block.U), end = start + (line.End - line.Start);
                double lastStart = Dot(Sub(last.Glyphs[0].Origin, block.Origin), block.U), lastEnd = lastStart + (last.End - last.Start);
                if (end < lastStart || start > lastEnd) continue;
                bool left = Math.Abs(start - block.Left) < (block.Lines.Count == 1 ? 3 : 1.5) * size;
                bool right = Math.Abs(end - lastEnd) < 1.0 * size;
                bool centre = Math.Abs((start + end) / 2 - (lastStart + lastEnd) / 2) < 1.0 * size;
                if (!left && !right && !centre) continue;
                if (delta < bestDelta) { bestDelta = delta; best = candidate; }
            }
            if (best is { } chosen)
            {
                var block = chosen.Block;
                if (block.Lines.Count == 1) block.Pitch = bestDelta;
                block.Lines.Add(line);
                open.Remove(chosen);
                open.Add((block, line));
                double start = Dot(Sub(line.Glyphs[0].Origin, block.Origin), block.U);
                block.Left = Math.Min(block.Left, start);
                block.Right = Math.Max(block.Right, start + line.End - line.Start);
            }
            else
            {
                var block = new TextBlock { Origin = line.Glyphs[0].Origin, U = line.U, V = line.V, Left = 0, Right = line.End - line.Start };
                block.Lines.Add(line);
                blocks.Add(block);
                open.Add((block, line));
            }
        }
        foreach (var block in blocks) Finish(block);
        return blocks;
    }

    /// <summary>Works out a block's alignment, where its lines break for good, its text and its bounds.</summary>
    private static void Finish(TextBlock block)
    {
        double StartOf(TextLine l) => Dot(Sub(l.Glyphs[0].Origin, block.Origin), block.U);
        double EndOf(TextLine l) => StartOf(l) + l.End - l.Start;
        var lines = block.Lines;
        double size = lines.Max(l => l.Size);
        if (lines.Count == 1) block.Pitch = 1.2 * size;

        if (lines.Count >= 2)
        {
            bool lefts = lines.Skip(1).All(l => Math.Abs(StartOf(l) - block.Left) < 0.5 * size);
            bool rights = lines.Take(lines.Count - 1).All(l => Math.Abs(EndOf(l) - block.Right) < 0.5 * size);
            double centre = (block.Left + block.Right) / 2;
            bool centres = lines.All(l => Math.Abs((StartOf(l) + EndOf(l)) / 2 - centre) < 0.5 * size);
            bool allRights = lines.All(l => Math.Abs(EndOf(l) - block.Right) < 0.5 * size);
            block.Alignment = lefts && rights && lines.Count >= 3 ? PdfTextAlignment.Justified
                : lefts ? PdfTextAlignment.Left
                : allRights ? PdfTextAlignment.Right
                : centres ? PdfTextAlignment.Center
                : PdfTextAlignment.Left;
        }

        // A line breaks for good when the next line's first word would have fitted on it.
        double width = block.Right - block.Left;
        var gaps = new List<double>();
        foreach (var line in lines)
            for (int i = 1; i < line.Glyphs.Count; i++)
            {
                double gap = Dot(Sub(line.Glyphs[i].Origin, line.Glyphs[i - 1].Origin), line.U) - UserAdvance(line.Glyphs[i - 1]);
                if (gap > 0.15 * line.Size && gap < 1.0 * line.Size) gaps.Add(gap);
            }
        block.WordGap = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : 0.25 * size;
        for (int i = 0; i < lines.Count - 1; i++)
        {
            var next = lines[i + 1];
            int firstWordEnd = next.Text.IndexOf(' ');
            string word = firstWordEnd < 0 ? next.Text : next.Text[..firstWordEnd];
            double wordWidth = WidthOf(next, word.Length);
            bool fromLeft = block.Alignment is PdfTextAlignment.Left or PdfTextAlignment.Justified;
            double used = fromLeft ? EndOf(lines[i]) - block.Left : lines[i].End - lines[i].Start;
            lines[i].HardBreakAfter = used + block.WordGap + wordWidth < width - 0.05 * size;
        }
        // The widest line always looks full, so its own break says nothing: in a block whose other
        // lines mostly end for good (an address, a list), it ends for good too.
        if (lines.Count >= 3)
        {
            int widest = Enumerable.Range(0, lines.Count - 1).OrderByDescending(i => lines[i].End - lines[i].Start).First();
            var others = Enumerable.Range(0, lines.Count - 1).Where(i => i != widest).ToList();
            if (!lines[widest].HardBreakAfter && others.Count(i => lines[i].HardBreakAfter) > others.Count(i => !lines[i].HardBreakAfter))
                lines[widest].HardBreakAfter = true;
        }

        var text = new StringBuilder();
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            block.LineStarts.Add(text.Length);
            for (int c = 0; c < line.Text.Length; c++)
            {
                text.Append(line.Text[c]);
                block.Chars.Add((li, line.CharGlyph[c], false));
            }
            if (li < lines.Count - 1)
            {
                if (line.HardBreakAfter) { text.Append('\n'); block.Chars.Add((li, -1, true)); }
                else if (!line.Text.EndsWith('-')) { text.Append(' '); block.Chars.Add((li, -1, true)); }
            }
        }
        block.Text = text.ToString();

        var boxes = new List<PdfRect>();
        foreach (var line in lines)
        {
            var lb = line.Glyphs.Select(GlyphBox).Aggregate(Union);
            block.LineBounds.Add(lb);
            boxes.Add(lb);
        }
        block.Bounds = boxes.Aggregate(Union);
    }

    /// <summary>The width of a line's first <paramref name="chars"/> characters.</summary>
    private static double WidthOf(TextLine line, int chars)
    {
        if (chars <= 0 || line.CharGlyph.Count == 0) return 0;
        int last = line.CharGlyph.Take(chars).Where(g => g >= 0).DefaultIfEmpty(0).Max();
        var o = line.Glyphs[0].Origin;
        var g = line.Glyphs[last];
        return Dot(Sub(g.Origin, o), line.U) + UserAdvance(g);
    }

    internal static PdfRect Union(PdfRect a, PdfRect b)
    {
        if (a.Width <= 0 && a.Height <= 0) return b;
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        double x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }
}
