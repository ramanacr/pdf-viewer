using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PdfEngine.Geometry;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Accessibility;

/// <summary>Lists, headings, paragraphs, tables, annotations and what each operator is part of.</summary>
internal sealed partial class AutoTagRun
{
    // ------------------------------------------------------------------ list markers

    /// <summary>A list item's label at the start of a line: a bullet, or a number or letter with a full stop or parenthesis.</summary>
    private static readonly Regex Marker = new(
        @"^(?<m>[•◦▪▫‣⁃∙·○●■□►▶➢➤✓✔❖◆◇]|[\-–—*](?=\s)|\(?(?<n>\d{1,3})[.)](?=\s|$)|\(?(?<a>[a-z])[.)](?=\s|$)|\(?(?<r>[ivx]{2,5})[.)](?=\s|$))",
        RegexOptions.CultureInvariant);

    private readonly record struct Label(string Family, int? Number, int Length);

    private static Label? LabelOf(string text)
    {
        var m = Marker.Match(text);
        if (!m.Success) return null;
        string mark = m.Groups["m"].Value;
        if (m.Groups["n"].Success)
            return new Label("number" + mark[^1] + (mark.StartsWith('(') ? "(" : ""), int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), mark.Length);
        if (m.Groups["a"].Success) return new Label("letter" + mark[^1] + (mark.StartsWith('(') ? "(" : ""), m.Groups["a"].Value[0] - 'a' + 1, mark.Length);
        if (m.Groups["r"].Success) return new Label("roman" + mark[^1], null, mark.Length);
        return new Label("bullet" + mark, null, mark.Length);
    }

    /// <summary>Lines that are only a bullet or number, with the line beside them on the right (their item's text).</summary>
    private Dictionary<TextLine, (TextLine, Match)> AttachMarkers(PageModel page, List<TextLine> lines)
    {
        var result = new Dictionary<TextLine, (TextLine, Match)>(ReferenceEqualityComparer.Instance);
        var boxes = lines.Where(l => Horizontal(page, l)).Select(l => (Line: l, D: LineDisplay(page, l))).ToList();
        foreach (var (line, d) in boxes)
        {
            string text = line.Text.Trim();
            var m = Marker.Match(text);
            if (!m.Success || m.Length != text.Length) continue;
            double size = Math.Max(line.Size, 1);
            TextLine? best = null;
            double bestGap = double.MaxValue;
            foreach (var (other, od) in boxes)
            {
                if (ReferenceEquals(other, line) || result.ContainsKey(other)) continue;
                if (Math.Abs((od.Y + od.Height / 2) - (d.Y + d.Height / 2)) > 0.45 * Math.Max(size, other.Size)) continue;
                double gap = od.X - (d.X + d.Width);
                if (gap < -1 || gap > 4 * size || gap >= bestGap) continue;
                var om = Marker.Match(other.Text.Trim());
                if (om.Success && om.Length == other.Text.Trim().Length) continue;
                best = other;
                bestGap = gap;
            }
            if (best != null) result[best] = (line, m);
        }
        return result;
    }

    // ------------------------------------------------------------------ headings

    /// <summary>The size class of a paragraph that reads as a heading (larger than the body text, or bold where the body is not), or null.</summary>
    private (double Size, bool Bold)? HeadingClass(List<TextLine> lines)
    {
        if (lines.Count > 3) return null;
        string text = string.Join(" ", lines.Select(l => l.Text)).Trim();
        if (text.Length == 0 || text.Length > 200 || !text.Any(char.IsLetter)) return null;
        double size = lines.Max(l => l.Size);
        bool bold = lines.All(LineBold);
        double ratio = size / _bodySize;
        bool heading = ratio >= 1.2
            || (ratio >= 1.08 && bold && !_bodyBold)
            || (bold && !_bodyBold && lines.Count == 1 && text.Length <= 80 && ratio >= 0.95 && !text.EndsWith('.') && !text.EndsWith(',') && !text.EndsWith(';'));
        return heading ? (Math.Round(size * 2) / 2, bold) : null;
    }

    // ------------------------------------------------------------------ placing the items

    private sealed class ListSegment
    {
        public List<TextLine> Lines = new();
        public TextLine? MarkerLine;
        public Label Label;
    }

    /// <summary>Splits a paragraph into list items when it starts with a label (every line with a label starts another), or null.</summary>
    private static List<ListSegment>? Segments(Item item, Dictionary<TextLine, (TextLine Line, Match M)> markerOf)
    {
        Label? LabelFor(TextLine line) =>
            markerOf.TryGetValue(line, out var m) ? LabelOf(m.Line.Text.Trim()) is { } l ? l with { Length = 0 } : null : LabelOf(line.Text);
        if (LabelFor(item.Lines[0]) == null) return null;
        var segments = new List<ListSegment>();
        foreach (var line in item.Lines)
        {
            if (LabelFor(line) is { } label)
            {
                var previous = segments.Count > 0 ? segments[^1].Label : (Label?)null;
                if (previous is { } p && (p.Family != label.Family || (p.Number is int pn && label.Number != pn + 1))) return null;
                segments.Add(new ListSegment { Label = label, MarkerLine = markerOf.TryGetValue(line, out var ml) ? ml.Line : null });
            }
            segments[^1].Lines.Add(line);
        }
        return segments;
    }

    /// <summary>Turns the ordered items into elements: tables and figures as they are, paragraphs as headings, paragraphs or lists.</summary>
    private void Place(PageModel page, List<Item> ordered, Dictionary<TextLine, (TextLine Line, Match M)> markerOf)
    {
        int order = 0;
        var pending = new List<(Item Item, List<ListSegment> Segments)>();

        void Paragraph(Item item)
        {
            var heading = HeadingClass(item.Lines);
            var node = new TagNode(heading != null ? "H" : "P", page.Index) { HeadingClass = heading };
            if (heading != null) node.HeadingText = string.Join(" ", item.Lines.Select(l => l.Text)).Trim();
            foreach (var line in item.Lines)
            {
                page.LineOwner[line] = node;
                page.LineOrder[line] = order++;
            }
            page.Top.Add(node);
        }

        void Flush()
        {
            if (pending.Count == 0) return;
            if (pending.Sum(p => p.Segments.Count) < 2)
            {
                foreach (var (item, _) in pending) Paragraph(item);
                pending.Clear();
                return;
            }
            var list = new TagNode("L", page.Index);
            foreach (var (_, segments) in pending)
                foreach (var segment in segments)
                {
                    var li = list.Add(new TagNode("LI", page.Index));
                    TagNode? lbl = null;
                    if (segment.MarkerLine != null)
                    {
                        lbl = li.Add(new TagNode("Lbl", page.Index));
                        page.LineOwner[segment.MarkerLine] = lbl;
                        page.LineOrder[segment.MarkerLine] = order++;
                    }
                    else if (segment.Label.Length > 0)
                    {
                        // The label's glyphs: an operator that shows only them is the label.
                        lbl = li.Add(new TagNode("Lbl", page.Index));
                        var first = segment.Lines[0];
                        for (int c = 0; c < segment.Label.Length && c < first.CharGlyph.Count; c++)
                            if (first.CharGlyph[c] >= 0) page.MarkerGlyphs[first.Glyphs[first.CharGlyph[c]]] = lbl;
                    }
                    var body = li.Add(new TagNode("LBody", page.Index));
                    foreach (var line in segment.Lines)
                    {
                        page.LineOwner[line] = body;
                        page.LineOrder[line] = order++;
                    }
                    page.ListItems++;
                }
            page.Lists++;
            page.Top.Add(list);
            pending.Clear();
        }

        foreach (var item in ordered)
        {
            if (item.Node != null)
            {
                Flush();
                page.Top.Add(item.Node);
                if (item.Cells != null)
                {
                    foreach (var (cell, lines) in item.Cells)
                        foreach (var line in lines)
                        {
                            page.LineOwner[line] = cell;
                            page.LineOrder[line] = order++;
                        }
                }
                else page.FigureOrder[item.Node] = order++;
                continue;
            }
            var segments = _options.DetectLists && HeadingClass(item.Lines) == null ? Segments(item, markerOf) : null;
            if (segments == null)
            {
                Flush();
                Paragraph(item);
                continue;
            }
            if (pending.Count > 0)
            {
                var last = pending[^1].Segments[^1].Label;
                var next = segments[0].Label;
                if (last.Family != next.Family || (last.Number is int n && next.Number != n + 1)) Flush();
            }
            pending.Add((item, segments));
        }
        Flush();
    }

    // ------------------------------------------------------------------ tables

    private IEnumerable<Item> FindTables(PageModel page, List<TextLine> available, HashSet<TextLine> exclude, List<(int Op, TagNode Node)> figures)
    {
        var free = available.Where(l => !exclude.Contains(l)).ToList();
        var tables = RuledTables(page, free, figures);
        var taken = new HashSet<TextLine>(tables.SelectMany(t => t.Lines), ReferenceEqualityComparer.Instance);
        tables.AddRange(AlignedTables(page, free.Where(l => !taken.Contains(l)).ToList()));
        return tables;
    }

    private static List<double> Cluster(IEnumerable<double> values, double tolerance)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var result = new List<double>();
        int start = 0;
        for (int i = 1; i <= sorted.Count; i++)
            if (i == sorted.Count || sorted[i] - sorted[i - 1] > tolerance)
            {
                result.Add(sorted.Skip(start).Take(i - start).Average());
                start = i;
            }
        return result;
    }

    private static double Covered(IEnumerable<(double From, double To)> spans, double from, double to)
    {
        double covered = 0, reach = from;
        foreach (var (a, b) in spans.Select(s => (Math.Max(s.From, from), Math.Min(s.To, to))).Where(s => s.Item2 > s.Item1).OrderBy(s => s.Item1))
        {
            if (b <= reach) continue;
            covered += b - Math.Max(a, reach);
            reach = b;
        }
        return covered;
    }

    /// <summary>
    /// Grids drawn with rules: horizontal and vertical edges that meet, every row and column line
    /// running the grid's whole width or height (no merged cells), each line of text inside one cell.
    /// </summary>
    private List<Item> RuledTables(PageModel page, List<TextLine> free, List<(int Op, TagNode Node)> figures)
    {
        var tables = new List<Item>();
        var hs = new List<(double Y, double X0, double X1)>();
        var vs = new List<(double X, double Y0, double Y1)>();
        foreach (var op in page.Ops)
        {
            if (op.Kind != ContentOpKind.PathPaint || op.Edges == null) continue;
            foreach (var (a, b) in op.Edges)
            {
                var p = Display(page, a.X, a.Y);
                var q = Display(page, b.X, b.Y);
                double dx = Math.Abs(p.X - q.X), dy = Math.Abs(p.Y - q.Y);
                if (dy <= 1 && dx >= 6) hs.Add(((p.Y + q.Y) / 2, Math.Min(p.X, q.X), Math.Max(p.X, q.X)));
                else if (dx <= 1 && dy >= 6) vs.Add(((p.X + q.X) / 2, Math.Min(p.Y, q.Y), Math.Max(p.Y, q.Y)));
            }
        }
        if (hs.Count < 3 || vs.Count < 3 || (long)hs.Count * vs.Count > 4_000_000) return tables;
        var parent = Enumerable.Range(0, hs.Count + vs.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < hs.Count; i++)
            for (int j = 0; j < vs.Count; j++)
            {
                var h = hs[i];
                var v = vs[j];
                if (v.X >= h.X0 - 2 && v.X <= h.X1 + 2 && h.Y >= v.Y0 - 2 && h.Y <= v.Y1 + 2) parent[Find(i)] = Find(hs.Count + j);
            }
        var used = new HashSet<TextLine>(ReferenceEqualityComparer.Instance);
        foreach (var component in Enumerable.Range(0, hs.Count + vs.Count).GroupBy(Find))
        {
            var ch = component.Where(k => k < hs.Count).Select(k => hs[k]).ToList();
            var cv = component.Where(k => k >= hs.Count).Select(k => vs[k - hs.Count]).ToList();
            if (ch.Count < 3 || cv.Count < 3) continue;
            var ys = Cluster(ch.Select(h => h.Y), 2.5);
            var xs = Cluster(cv.Select(v => v.X), 2.5);
            if (ys.Count < 3 || xs.Count < 3) continue;
            double x0 = xs[0], x1 = xs[^1], y0 = ys[0], y1 = ys[^1];
            if (x1 - x0 < 20 || y1 - y0 < 10) continue;
            int rows = ys.Count - 1, cols = xs.Count - 1;
            if (rows * cols > 5000) continue;
            bool regular = ys.All(y => Covered(ch.Where(h => Math.Abs(h.Y - y) <= 2.5).Select(h => (h.X0, h.X1)), x0, x1) >= 0.9 * (x1 - x0))
                           && xs.All(x => Covered(cv.Where(v => Math.Abs(v.X - x) <= 2.5).Select(v => (v.Y0, v.Y1)), y0, y1) >= 0.9 * (y1 - y0));
            if (!regular) continue;
            if (Enumerable.Range(0, rows).Any(r => ys[r + 1] - ys[r] < 4) || Enumerable.Range(0, cols).Any(c => xs[c + 1] - xs[c] < 4)) continue;
            var box = new PdfRect(x0, y0, x1 - x0, y1 - y0);
            if (figures.Any(f => Display(page, f.Node.Bounds ?? default) is var fd && fd.X + fd.Width / 2 > x0 && fd.X + fd.Width / 2 < x1 && fd.Y + fd.Height / 2 > y0 && fd.Y + fd.Height / 2 < y1)) continue;

            var cells = new List<TextLine>[rows, cols];
            var cellOf = new Dictionary<TextLine, int>(ReferenceEqualityComparer.Instance);
            bool fits = true;
            foreach (var line in free)
            {
                if (used.Contains(line)) continue;
                var d = LineDisplay(page, line);
                double cx = d.X + d.Width / 2, cy = d.Y + d.Height / 2;
                if (cx <= x0 || cx >= x1 || cy <= y0 || cy >= y1) continue;
                int r = 0, c = 0;
                while (r < rows - 1 && cy >= ys[r + 1]) r++;
                while (c < cols - 1 && cx >= xs[c + 1]) c++;
                if (d.X < xs[c] - 2 || d.X + d.Width > xs[c + 1] + 2 || d.Y < ys[r] - 2 || d.Y + d.Height > ys[r + 1] + 2) { fits = false; break; }
                (cells[r, c] ??= new List<TextLine>()).Add(line);
                cellOf[line] = r * cols + c;
            }
            int filled = 0;
            foreach (var cell in cells) if (cell != null) filled++;
            if (!fits || filled < 2 || filled < 0.3 * rows * cols || !OpsInCells(page, cellOf)) continue;
            tables.Add(TableItem(page, rows, cols, (r, c) => cells[r, c] ?? new List<TextLine>(), box));
            used.UnionWith(cellOf.Keys);
        }
        return tables;
    }

    /// <summary>
    /// Tables without rules: three or more rows of single-line cells on shared baselines, the same
    /// number in each row, each column's cells aligned left, right or centre. Columns of prose
    /// (wide cells, lines that wrap) are not tables.
    /// </summary>
    private List<Item> AlignedTables(PageModel page, List<TextLine> free)
    {
        var tables = new List<Item>();
        var lines = free.Where(l => Horizontal(page, l)).Select(l => (Line: l, D: LineDisplay(page, l))).OrderBy(x => x.D.Y + x.D.Height).ToList();
        if (lines.Count < 6) return tables;
        var rows = new List<List<(TextLine Line, PdfRect D)>>();
        foreach (var x in lines)
        {
            double baseline = x.D.Y + x.D.Height;
            if (rows.Count > 0 && Math.Abs(rows[^1][0].D.Y + rows[^1][0].D.Height - baseline) <= 0.35 * Math.Max(x.Line.Size, rows[^1][0].Line.Size))
                rows[^1].Add(x);
            else rows.Add(new List<(TextLine, PdfRect)> { x });
        }
        foreach (var row in rows) row.Sort((a, b) => a.D.X.CompareTo(b.D.X));
        bool Separated(List<(TextLine Line, PdfRect D)> row)
        {
            for (int i = 1; i < row.Count; i++)
                if (row[i].D.X - (row[i - 1].D.X + row[i - 1].D.Width) < 1.0 * Math.Max(row[i].Line.Size, row[i - 1].Line.Size)) return false;
            return true;
        }

        int start = 0;
        while (start < rows.Count)
        {
            int k = rows[start].Count;
            int end = start + 1;
            if (k >= 2 && Separated(rows[start]))
            {
                while (end < rows.Count && rows[end].Count == k && Separated(rows[end]))
                {
                    double size = Math.Max(rows[end][0].Line.Size, 1);
                    double step = (rows[end][0].D.Y + rows[end][0].D.Height) - (rows[end - 1][0].D.Y + rows[end - 1][0].D.Height);
                    if (step < 0.8 * size || step > 3 * size) break;
                    end++;
                }
                if (end - start >= 3 && AlignedTable(page, rows.GetRange(start, end - start)) is { } table)
                {
                    tables.Add(table);
                    start = end;
                    continue;
                }
            }
            start++;
        }
        return tables;
    }

    private Item? AlignedTable(PageModel page, List<List<(TextLine Line, PdfRect D)>> rows)
    {
        int k = rows[0].Count;
        double size = rows.SelectMany(r => r).Select(c => c.Line.Size).OrderBy(s => s).ElementAt(rows.Count * k / 2);
        for (int c = 0; c < k; c++)
        {
            var col = rows.Select(r => r[c].D).ToList();
            double Spread(Func<PdfRect, double> f) => col.Max(f) - col.Min(f);
            if (Spread(d => d.X) > size && Spread(d => d.X + d.Width) > size && Spread(d => d.X + d.Width / 2) > size) return null;
            if (col.Any(d => d.Width > 0.35 * page.Width)) return null;
            if (c < k - 1 && col.Max(d => d.X + d.Width) >= rows.Min(r => r[c + 1].D.X)) return null;
        }
        // Columns of prose have long lines; table cells are short.
        var lengths = rows.SelectMany(r => r).Select(c => c.Line.Text.Length).OrderBy(n => n).ToList();
        if (lengths[lengths.Count / 2] > 30 || lengths[^1] > 80) return null;
        // A sentence running on from one row's cell into the next row's is prose.
        for (int r = 1; r < rows.Count; r++)
            for (int c = 0; c < k; c++)
            {
                string above = rows[r - 1][c].Line.Text.TrimEnd(), below = rows[r][c].Line.Text.TrimStart();
                if (above.Length > 0 && below.Length > 0 && char.IsLower(below[0]) && !".:;!?".Contains(above[^1])
                    && ReferenceEquals(page.LineBlock[rows[r - 1][c].Line], page.LineBlock[rows[r][c].Line])) return null;
            }
        var cellOf = new Dictionary<TextLine, int>(ReferenceEqualityComparer.Instance);
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < k; c++) cellOf[rows[r][c].Line] = r * k + c;
        if (!OpsInCells(page, cellOf)) return null;
        var box = rows.SelectMany(r => r).Select(c => c.D).Aggregate(Union);
        return TableItem(page, rows.Count, k, (r, c) => new List<TextLine> { rows[r][c].Line }, box);
    }

    /// <summary>Every operator that shows a table line's glyphs shows glyphs of that cell only (marked content cannot split an operator).</summary>
    private static bool OpsInCells(PageModel page, Dictionary<TextLine, int> cellOf)
    {
        var checkedOps = new HashSet<int>();
        foreach (var (line, cell) in cellOf)
            foreach (var g in line.Glyphs)
            {
                if (!page.OpAt.TryGetValue(g.PageOp, out int op) || !checkedOps.Add(op)) continue;
                foreach (var other in page.OpGlyphs[op])
                {
                    if (!Meaningful(other) || !page.GlyphLine.TryGetValue(other, out var otherLine)) continue;
                    if (!cellOf.TryGetValue(otherLine, out int otherCell) || otherCell != cell) return false;
                }
            }
        return true;
    }

    /// <summary>The table's elements; the first row is a header row when it is bold and the rest is not.</summary>
    private Item TableItem(PageModel page, int rows, int cols, Func<int, int, List<TextLine>> cell, PdfRect box)
    {
        bool header = rows >= 2
            && Enumerable.Range(0, cols).All(c => cell(0, c) is var l && l.Count > 0 && l.All(LineBold))
            && Enumerable.Range(1, rows - 1).Any(r => Enumerable.Range(0, cols).Any(c => cell(r, c).Any(l => !LineBold(l))));
        var table = new TagNode("Table", page.Index);
        var item = new Item { D = box, Node = table, Cells = new() };
        for (int r = 0; r < rows; r++)
        {
            var tr = table.Add(new TagNode("TR", page.Index));
            for (int c = 0; c < cols; c++)
            {
                var td = tr.Add(new TagNode(header && r == 0 ? "TH" : "TD", page.Index));
                if (header && r == 0)
                    td.Attributes = new PdfDictionary(new Dictionary<string, PdfObject> { ["O"] = new PdfName("Table"), ["Scope"] = new PdfName("Column") });
                var lines = cell(r, c).OrderBy(l => LineDisplay(page, l).Y).ThenBy(l => LineDisplay(page, l).X).ToList();
                item.Cells.Add((td, lines));
                item.Lines.AddRange(lines);
            }
        }
        return item;
    }

    // ------------------------------------------------------------------ what each operator is part of

    private void AssignOwners(PageModel page)
    {
        var spanOwner = new Dictionary<int, object>();
        for (int k = 0; k < page.Ops.Count; k++)
        {
            var op = page.Ops[k];
            if (op.InArtifact) continue;
            object? owner = op.Kind switch
            {
                ContentOpKind.Text => FromGlyphs(page, k) ?? InheritTag.Instance,
                ContentOpKind.XObject when op.XObjectSubtype == "Form" =>
                    FromGlyphs(page, k) ?? (page.Figures.TryGetValue(k, out var f) ? f : page.OpArtifacts.TryGetValue(k, out var a) ? a : ArtifactTag.Plain),
                ContentOpKind.XObject or ContentOpKind.InlineImage =>
                    page.Figures.TryGetValue(k, out var fi) ? fi : page.OpArtifacts.TryGetValue(k, out var ai) ? ai : ArtifactTag.Plain,
                ContentOpKind.PathPaint or ContentOpKind.Shading => ArtifactTag.Plain,
                _ => null,
            };
            if (owner == null) continue;
            // Glyphs read together as one /ActualText must stay in one sequence of ours.
            if (op.Kind == ContentOpKind.Text && page.SpanOf[k] >= 0)
            {
                if (spanOwner.TryGetValue(page.SpanOf[k], out var first)) owner = first;
                else if (owner is not InheritTag) spanOwner[page.SpanOf[k]] = owner;
            }
            page.Owner[k] = owner;
            if (owner is TagNode node) node.Used++;
            page.OpKey[k] = KeyOf(page, k, owner);
        }
    }

    private static ReadKey KeyOf(PageModel page, int k, object owner)
    {
        if (page.OpGlyphs.TryGetValue(k, out var glyphs))
            foreach (var g in glyphs)
                if (Meaningful(g) && page.GlyphLine.TryGetValue(g, out var line) && page.LineOrder.TryGetValue(line, out int order))
                    return new ReadKey(order, TextBlocks.Dot(new PdfPoint(g.Origin.X - line.Glyphs[0].Origin.X, g.Origin.Y - line.Glyphs[0].Origin.Y), line.U), k);
        if (owner is TagNode node && page.FigureOrder.TryGetValue(node, out int figure)) return new ReadKey(figure, 0, k);
        return new ReadKey(int.MaxValue - 1, 0, k);
    }

    /// <summary>What the operator's text belongs to: its first readable glyph's line's element (or furniture, or a list label).</summary>
    private static object? FromGlyphs(PageModel page, int k)
    {
        if (!page.OpGlyphs.TryGetValue(k, out var glyphs)) return null;
        foreach (var g in glyphs)
        {
            if (!Meaningful(g) || !page.GlyphLine.TryGetValue(g, out var line)) continue;
            if (page.ArtifactBlocks.TryGetValue(page.LineBlock[line], out var artifact)) return artifact;
            if (page.MarkerGlyphs.TryGetValue(g, out var lbl)
                && glyphs.Where(x => Meaningful(x) && page.GlyphLine.ContainsKey(x)).All(x => page.MarkerGlyphs.TryGetValue(x, out var l2) && ReferenceEquals(l2, lbl)))
                return lbl;
            if (page.LineOwner.TryGetValue(line, out var node)) return node;
        }
        return null;
    }

    // ------------------------------------------------------------------ annotations

    /// <summary>
    /// Links become Link elements holding their annotation (and the text they cover, where the
    /// operators drawing it show nothing else); form fields become Form elements and other
    /// annotations Annot elements. Links without a description get the text they cover, or their URI.
    /// </summary>
    private void TagAnnotations(PageModel page)
    {
        if (_r.Resolve(page.Node.Dictionary["Annots"]) is not PdfArray annots) return;
        for (int i = 0; i < annots.Count; i++)
        {
            if (_r.Resolve(annots[i]) is not PdfDictionary dict) continue;
            int number = annots[i] is PdfIndirectRef ir ? ir.ObjectNumber : -1;
            string subtype = dict.GetName("Subtype") ?? string.Empty;
            if (subtype is "Popup" or "PrinterMark" or "TrapNet")
            {
                page.Annots.Add((i, number, dict, null, null));
                continue;
            }
            TagNode node;
            string? contents = null;
            if (subtype == "Link")
            {
                node = new TagNode("Link", page.Index);
                var rect = RectOf(dict);
                var inside = new List<int>();
                if (rect is { } box)
                    foreach (var (k, glyphs) in page.OpGlyphs)
                    {
                        if (!page.Owner.ContainsKey(k)) continue;
                        var shown = glyphs.Where(Meaningful).ToList();
                        if (shown.Count == 0) continue;
                        if (shown.All(g => Centre(g) is var c && c.X >= box.X - 1 && c.X <= box.X + box.Width + 1 && c.Y >= box.Y - 1 && c.Y <= box.Y + box.Height + 1)) inside.Add(k);
                    }
                var owners = inside.Select(k => page.Owner[k]).Distinct().ToList();
                if (owners.Count == 1 && owners[0] is TagNode leaf && leaf.Type != "Link")
                {
                    leaf.Add(node);
                    foreach (int k in inside)
                    {
                        page.Owner[k] = node;
                        leaf.Used--;
                        node.Used++;
                    }
                }
                else if (owners.FirstOrDefault(o => o is TagNode t && t.Type != "Link") is TagNode near) near.Add(node);
                else page.Top.Add(node);
                if (string.IsNullOrWhiteSpace(Text(_r.Resolve(dict["Contents"]))))
                    contents = rect is { } r ? CoveredText(page, r) : null;
                if (string.IsNullOrWhiteSpace(contents) && _r.Resolve(dict["A"]) is PdfDictionary action && action.GetName("S") == "URI" && Text(_r.Resolve(action["URI"])) is { Length: > 0 } uri)
                    contents = uri;
                if (string.IsNullOrWhiteSpace(contents)) contents = null;
            }
            else
            {
                node = new TagNode(subtype == "Widget" ? "Form" : "Annot", page.Index);
                page.Top.Add(node);
            }
            node.Annot = number;
            node.Used++;
            page.Annots.Add((i, number, dict, node, contents));
        }
    }

    private static string? Text(PdfObject? o) => o is PdfString s ? s.AsDecodedString() : null;

    private PdfRect? RectOf(PdfDictionary annot)
    {
        if (_r.Resolve(annot["Rect"]) is not PdfArray a || a.Count < 4) return null;
        double[] v = a.Items.Take(4).Select(x => _r.Resolve(x) is { } o && o.TryGetNumber(out double d) ? d : 0).ToArray();
        return new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1]));
    }

    private static PdfPoint Centre(ContentGlyph g)
    {
        var b = TextBlocks.GlyphBox(g);
        return new PdfPoint(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    /// <summary>The text of the lines inside <paramref name="rect"/>, in reading order (a link's description).</summary>
    private static string? CoveredText(PageModel page, PdfRect rect)
    {
        var sb = new StringBuilder();
        foreach (var line in page.LineOrder.OrderBy(l => l.Value).Select(l => l.Key))
        {
            bool any = false;
            for (int c = 0; c < line.Text.Length; c++)
            {
                int gi = line.CharGlyph[c];
                bool inside = gi < 0 ? any : rect.Contains(Centre(line.Glyphs[gi]));
                if (!inside) continue;
                if (!any && sb.Length > 0) sb.Append(' ');
                any = true;
                sb.Append(line.Text[c]);
            }
        }
        string text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text.Length > 300 ? text[..300] : text;
    }
}
