using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using static PdfEngine.Vector.Editing.TextBlocks;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Lays out a paragraph's new text. Every character keeps the look of the old character it
/// stands for (new characters that of the one before them); unchanged glyphs are drawn with
/// their own codes. Lines break within the paragraph's width and keep its alignment (a single
/// line grows along its baseline instead). Lines before the first change are left alone, and
/// once the text is the same again from the start of an old line, the rest keeps its exact
/// glyphs, only moved if the paragraph grew or shrank.
/// </summary>
internal sealed class BlockLayout
{
    private readonly TextBlock _b;
    private readonly PageSession _session;

    public BlockLayout(TextBlock block, PageSession session)
    {
        _b = block;
        _session = session;
    }

    private sealed class Item
    {
        public OutGlyph? Glyph;
        public ContentGlyph Template = null!;
        public double Width; // user units along the baseline
        public double UserScale = 1; // text space to user space along the baseline
        public bool Space, BreakAfter;
        public int End; // index in the new text after this item
        public int OldLine = -1, OldIndex = -1; // an old glyph drawn again: where it was
        /// <summary>A shaped word (right-to-left or cursive): its glyphs in drawing order, each at its offset along the baseline.</summary>
        public List<(OutGlyph Glyph, double Offset)>? Cluster;
        public int Level; // bidirectional embedding level (odd: right to left)
    }

    public (HashSet<ContentGlyph> Gone, List<OutGlyph> Output) Layout(string newText)
    {
        var gone = new HashSet<ContentGlyph>();
        var output = new List<OutGlyph>();
        string oldText = _b.Text;
        string text = newText.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Normalize(NormalizationForm.FormC);
        if (text == oldText) return (gone, output);

        var (o2n, n2o) = CharDiff.Map(oldText, text);
        var changed = new bool[oldText.Length + 1];
        for (int k = 0; k < oldText.Length; k++) if (o2n[k] < 0) changed[k] = true;
        int nextOld = oldText.Length;
        for (int j = text.Length - 1; j >= 0; j--)
        {
            if (n2o[j] >= 0) nextOld = n2o[j];
            else changed[nextOld] = true;
        }
        int firstChanged = Array.IndexOf(changed, true), lastChanged = Array.LastIndexOf(changed, true);
        if (firstChanged < 0) return (gone, output);
        int n = _b.Lines.Count;
        int LineOf(int k)
        {
            int line = 0;
            for (int i = 0; i < n; i++) if (_b.LineStarts[i] <= k) line = i;
            return line;
        }
        int f = LineOf(firstChanged);

        // The look of each new character.
        var template = new ContentGlyph?[text.Length];
        for (int j = 0; j < text.Length; j++)
            template[j] = n2o[j] >= 0 ? TemplateOfOld(n2o[j]) : j > 0 ? template[j - 1] : null;
        var firstTemplate = template.FirstOrDefault(t => t != null) ?? _b.Lines[f].Glyphs[0];
        for (int j = 0; j < text.Length && template[j] == null; j++) template[j] = firstTemplate;

        double size = _b.Lines.Max(l => l.Size);
        double pitch = n >= 2 ? _b.Pitch : 1.2 * size;
        double Base(int i) => i < n ? Dot(Sub(_b.Lines[i].Glyphs[0].Origin, _b.Origin), _b.V) : Base(n - 1) - pitch * (i - n + 1);
        double StartOf(int i) => Dot(Sub(_b.Lines[i].Glyphs[0].Origin, _b.Origin), _b.U);
        bool pointText = n == 1;

        int p = _b.LineStarts[f], li = f, sync = -1;
        while (p < text.Length && sync < 0)
        {
            int segEnd = text.IndexOf('\n', p);
            if (segEnd < 0) segEnd = text.Length;
            var items = Items(text, p, segEnd, n2o, template!);
            int ii = 0;
            bool firstOfSegment = true;
            while (true)
            {
                int pos = ii < items.Count ? (ii == 0 ? p : items[ii - 1].End) : segEnd;
                // The rest is the old text from the start of an old line: those lines keep their glyphs.
                if (li > f && pos < text.Length && n2o[pos] >= 0 && n2o[pos] > lastChanged)
                {
                    int j = _b.LineStarts.IndexOf(n2o[pos]);
                    if (j > 0) { sync = j; break; }
                }
                if (ii >= items.Count && !(firstOfSegment && items.Count == 0)) break;

                double start = _b.Alignment is PdfTextAlignment.Left or PdfTextAlignment.Justified
                    ? (li == f || (firstOfSegment && li < n) ? StartOf(Math.Min(li, n - 1)) : _b.Left)
                    : _b.Left;
                double available = pointText ? double.PositiveInfinity
                    : _b.Alignment is PdfTextAlignment.Left or PdfTextAlignment.Justified ? _b.Right - start : _b.Right - _b.Left;

                // Greedy: as many words as fit, breaking after a space or a hyphen. A word longer
                // than the line is never cut: it overflows, as it would in a word processor.
                int end = ii, lastBreak = -1;
                double width = 0;
                while (end < items.Count)
                {
                    var item = items[end];
                    if (!item.Space && width + item.Width > available + 0.01 && end > ii && lastBreak > ii)
                    {
                        end = lastBreak;
                        break;
                    }
                    width += item.Width;
                    end++;
                    if (item.Space || item.BreakAfter) lastBreak = end;
                }
                int take = end;
                while (take > ii && items[take - 1].Space) take--;
                var line = items.GetRange(ii, take - ii);
                double used = line.Sum(x => x.Width);
                bool lastOfSegment = end >= items.Count;
                if (_b.Alignment == PdfTextAlignment.Justified && !lastOfSegment && !pointText)
                {
                    var spaces = line.Where((x, i) => x.Space && i > 0).ToList();
                    double extra = available - used;
                    if (spaces.Count > 0 && extra > 0 && extra / spaces.Count < 3 * size)
                    {
                        foreach (var s in spaces) s.Width += extra / spaces.Count;
                        used = available;
                    }
                }
                double s0 = _b.Alignment switch
                {
                    PdfTextAlignment.Center => (pointText ? (StartOf(0) + StartOf(0) + _b.Lines[0].End - _b.Lines[0].Start) / 2 : (_b.Left + _b.Right) / 2) - used / 2,
                    PdfTextAlignment.Right => (pointText ? StartOf(0) + _b.Lines[0].End - _b.Lines[0].Start : _b.Right) - used,
                    _ => start,
                };
                double t = Base(li), x0 = s0;
                // Right-to-left words typed into left-to-right text are drawn in reading order's reverse.
                if (line.Any(x => x.Level > 0)) line = Bidi.Reorder(line, x => x.Level);
                foreach (var item in line)
                {
                    var tm = item.Template.Matrix;
                    int after = li < n ? _b.Lines[li].Glyphs.Max(g => g.Op) : -1;
                    void Place(OutGlyph glyph, double at)
                    {
                        var o = new PdfPoint(_b.Origin.X + at * _b.U.X + t * _b.V.X, _b.Origin.Y + at * _b.U.Y + t * _b.V.Y);
                        glyph.Matrix = new PdfMatrix(tm.A, tm.B, tm.C, tm.D, o.X, o.Y);
                        glyph.Line = li;
                        glyph.After = after;
                        output.Add(glyph);
                    }
                    if (item.Glyph != null) Place(item.Glyph, x0);
                    if (item.Cluster != null) foreach (var (glyph, offset) in item.Cluster) Place(glyph, x0 + offset);
                    x0 += item.Width;
                }
                li++;
                ii = end;
                firstOfSegment = false;
                if (ii >= items.Count) break;
            }
            if (sync >= 0) break;
            p = segEnd + 1; // past the line break
        }

        int stop = sync >= 0 ? sync : n;
        for (int i = f; i < stop; i++) gone.UnionWith(_b.Lines[i].Glyphs);
        if (sync >= 0)
        {
            double shift = Base(li) - Base(sync);
            if (li != sync || Math.Abs(shift) > 1e-6)
            {
                for (int i = sync; i < n; i++)
                    foreach (var g in _b.Lines[i].Glyphs)
                    {
                        gone.Add(g);
                        var o = OutGlyph.From(g);
                        o.Matrix = new PdfMatrix(o.Matrix.A, o.Matrix.B, o.Matrix.C, o.Matrix.D, o.Matrix.E + shift * _b.V.X, o.Matrix.F + shift * _b.V.Y);
                        output.Add(o);
                    }
            }
        }
        return (gone, output);
    }

    private static PdfPoint Sub(PdfPoint a, PdfPoint b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>The glyph whose look an old character has (a space: the glyph before it on its line).</summary>
    private ContentGlyph TemplateOfOld(int k)
    {
        var (line, glyph, _) = _b.Chars[k];
        if (glyph >= 0) return _b.Lines[line].Glyphs[glyph];
        for (int i = k - 1; i >= 0 && _b.Chars[i].Line == line; i--)
            if (_b.Chars[i].Glyph >= 0) return _b.Lines[line].Glyphs[_b.Chars[i].Glyph];
        for (int i = k + 1; i < _b.Chars.Count && _b.Chars[i].Line == line; i++)
            if (_b.Chars[i].Glyph >= 0) return _b.Lines[line].Glyphs[_b.Chars[i].Glyph];
        return _b.Lines[line].Glyphs[0];
    }

    /// <summary>The glyphs (and word gaps) for text[from..to).</summary>
    private List<Item> Items(string text, int from, int to, int[] n2o, ContentGlyph[] template)
    {
        var items = new List<Item>();
        // A word the document's font cannot write in full is written entirely in the installed
        // font: one design per word, never letters of two fonts side by side.
        var installedWords = new bool[to - from];
        for (int ws = from; ws < to;)
        {
            if (text[ws] == ' ') { ws++; continue; }
            int we = ws;
            while (we < to && text[we] != ' ') we++;
            bool whole = true;
            for (int k = ws; k < we && whole;)
            {
                int len = StringInfo.GetNextTextElementLength(text, k);
                if (_session.Encoder(template[k].Style.Font) is not { } enc || !enc.TryEncode(text.Substring(k, len), out _, out _)) whole = false;
                k += len;
            }
            // A cursive word that changed is shaped again as a whole, in an installed font: the
            // document's glyphs for its letters are in the forms their old neighbours needed.
            if (whole && text[ws..we].Any(c => ArabicJoining.Joins(c)))
                for (int k = ws; k < we && whole; k++)
                    if (n2o[k] < 0 || (k > ws && n2o[k] != n2o[k - 1] + 1)) whole = false;
            if (!whole) for (int k = ws; k < we; k++) installedWords[k - from] = true;
            ws = we;
        }
        // The text is in reading order (a right-to-left paragraph's too): each line is drawn in the
        // bidirectional algorithm's order, the paragraph's direction being that of the old text.
        var levels = Bidi.Levels(text[from..to], Bidi.ParagraphLevel(_b.Text));
        bool reorder = true;
        int j = from;
        while (j < to)
        {
            bool installedWord = installedWords[j - from];
            if (installedWord && reorder && text[j] != ' ' && ShapedWord(text, j, to, template[j], levels[j - from]) is { } shaped)
            {
                items.Add(shaped);
                j = shaped.End;
                continue;
            }
            // An old glyph, all of whose characters are still there in order: drawn as it was.
            if (!installedWord && n2o[j] >= 0 && Reusable(n2o[j], j, to, n2o) is { } reuse)
            {
                var m = reuse.Glyph.Matrix;
                items.Add(new Item
                {
                    Glyph = OutGlyph.From(reuse.Glyph), Template = reuse.Glyph, Width = UserAdvance(reuse.Glyph), UserScale = Math.Sqrt(m.A * m.A + m.B * m.B),
                    Space = reuse.Glyph.Text.Trim().Length == 0, BreakAfter = reuse.Glyph.Text.EndsWith('-'), End = j + reuse.Length,
                    OldLine = _b.Chars[n2o[j]].Line, OldIndex = _b.Chars[n2o[j]].Glyph, Level = levels[j - from],
                });
                j += reuse.Length;
                continue;
            }
            var tmpl = template[j];
            var style = tmpl.Style;
            double userScale = Math.Sqrt(tmpl.Matrix.A * tmpl.Matrix.A + tmpl.Matrix.B * tmpl.Matrix.B);
            int length = text[j] == ' ' ? 1 : StringInfo.GetNextTextElementLength(text, j);
            string element = text.Substring(j, length);
            // Right to left, a bracket is drawn (and reads) as its mirror image.
            if ((levels[j - from] & 1) != 0 && length == 1 && Bidi.Mirror(element[0]) is int mirrored) element = ((char)mirrored).ToString();
            bool space = element == " ";
            bool bold = style.Font?.IsBold ?? false, italic = style.Font?.IsItalic ?? false;
            var item = new Item { Template = tmpl, Space = space, BreakAfter = element is "-" or "‐" or "–", End = j + length, UserScale = userScale };
            var wordInstalled = installedWord && !space ? _session.EncodeWithInstalled(element, style, bold, italic) : null;
            if (wordInstalled is { } inWord)
            {
                double advance = (inWord.Width0 / 1000.0 * style.FontSize + style.CharSpacing) * style.Scaling / 100.0;
                item.Glyph = new OutGlyph { Code = inWord.Code, Style = inWord.Style, Advance = advance, Text = element };
                item.Width = advance * userScale;
            }
            else if (_session.Encoder(style.Font) is { } encoder && encoder.TryEncode(element, out var code, out double w0))
            {
                bool wordSpace = code.Length == 1 && code[0] == 32;
                double advance = (w0 / 1000.0 * style.FontSize + style.CharSpacing + (wordSpace ? style.WordSpacing : 0)) * style.Scaling / 100.0;
                item.Glyph = new OutGlyph { Code = code, Style = style, Advance = advance, Text = element };
                item.Width = advance * userScale;
            }
            else if (space)
            {
                item.Width = _b.WordGap; // the font has no space: the gap alone separates the words
            }
            else if (_session.EncodeWithInstalled(element, style, bold, italic) is { } installed)
            {
                double advance = (installed.Width0 / 1000.0 * style.FontSize + style.CharSpacing) * style.Scaling / 100.0;
                item.Glyph = new OutGlyph { Code = installed.Code, Style = installed.Style, Advance = advance, Text = element };
                item.Width = advance * userScale;
            }
            else
            {
                _session.Warnings.Add($"No font has the character \"{element}\"; it was left out.");
                j += length;
                continue;
            }
            item.Level = levels[j - from];
            items.Add(item);
            j += length;
        }
        Kern(items);
        return items;
    }

    private static bool IsRightToLeft(char c) => c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';

    /// <summary>
    /// A word in right-to-left or cursive script, shaped in the first installed font that has all of
    /// it (joining forms, ligatures, drawing order), as one item; null for other words.
    /// </summary>
    private Item? ShapedWord(string text, int start, int to, ContentGlyph template, int level)
    {
        int end = start;
        while (end < to && text[end] != ' ') end++;
        string word = text[start..end];
        if (!word.Any(IsRightToLeft)) return null;
        var style = template.Style;
        bool bold = style.Font?.IsBold ?? false, italic = style.Font?.IsItalic ?? false;
        var builder = _session.Chain(style.Font, bold, italic).FirstOrDefault(b => word.All(c => char.IsSurrogate(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format || b.Font.GlyphFor(c) > 0));
        if (builder == null) return null;
        double userScale = Math.Sqrt(template.Matrix.A * template.Matrix.A + template.Matrix.B * template.Matrix.B);
        var shapedStyle = style with { FontResource = builder.ResourceName, Font = null, WordSpacing = 0 };
        var cluster = new List<(OutGlyph, double)>();
        double x = 0;
        foreach (var (g, _) in TextShaper.ShapeLine(word, _ => builder, b => b.Font))
        {
            if (g.Gid <= 0) continue;
            double advance = (builder.Font.Advance(g.Gid) / 1000.0 * style.FontSize + style.CharSpacing) * style.Scaling / 100.0;
            cluster.Add((new OutGlyph { Code = builder.Encode(g.Gid, g.Text), Style = shapedStyle, Advance = advance, Text = g.Text }, x));
            x += advance * userScale;
        }
        if (cluster.Count == 0) return null;
        return new Item { Template = template, Cluster = cluster, Width = x, UserScale = userScale, End = end, Level = Math.Max(level, 1) };
    }

    // Glyphs that were side by side stay as far apart as they were (the document's own kerning and
    // tracking); a new glyph next to another is kerned the way its font kerns that pair.
    private void Kern(List<Item> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            var a = items[i - 1];
            var b = items[i];
            if (a.Glyph == null || b.Glyph == null || a.Space || b.Space || a.Level > 0 || b.Level > 0) continue;
            if (a.OldLine >= 0 && b.OldLine == a.OldLine && b.OldIndex == a.OldIndex + 1)
            {
                var line = _b.Lines[a.OldLine].Glyphs;
                double d = Dot(Sub(line[b.OldIndex].Origin, line[a.OldIndex].Origin), _b.U);
                if (d > 0 && Math.Abs(d - a.Width) < 0.3 * EmHeight(line[a.OldIndex])) a.Width = d;
                continue;
            }
            var st = a.Glyph.Style;
            double k = _session.Kerning(st, a.Glyph.Code, a.Glyph.Text, b.Glyph.Style, b.Glyph.Code, b.Glyph.Text);
            if (k != 0) a.Width += k / 1000.0 * st.FontSize * st.Scaling / 100.0 * a.UserScale;
        }
    }

    /// <summary>The old glyph that old character <paramref name="k"/> starts, when all its characters map, in order, from new position <paramref name="j"/>.</summary>
    private (ContentGlyph Glyph, int Length)? Reusable(int k, int j, int to, int[] n2o)
    {
        var (line, glyph, sep) = _b.Chars[k];
        if (sep || glyph < 0) return null;
        if (k > 0 && _b.Chars[k - 1].Line == line && _b.Chars[k - 1].Glyph == glyph) return null; // not the glyph's first character
        int length = 0;
        while (k + length < _b.Chars.Count && _b.Chars[k + length].Line == line && _b.Chars[k + length].Glyph == glyph && !_b.Chars[k + length].Separator) length++;
        if (j + length > to) return null;
        for (int i = 0; i < length; i++)
            if (n2o[j + i] != k + i) return null;
        return (_b.Lines[line].Glyphs[glyph], length);
    }
}
