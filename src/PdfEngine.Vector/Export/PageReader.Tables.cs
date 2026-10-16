using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>Tables: from tags, from ruling lines, and from text aligned in columns.</summary>
internal sealed partial class PageReader
{
    private sealed record CellSpec(int Row, int Col, int RowSpan, int ColSpan, List<ContentGlyph> Glyphs, bool Header);

    private List<Rule> _tableRules = new();

    // ------------------------------------------------------------------ tagged

    private IEnumerable<DocTable> TaggedTables(List<ContentGlyph> glyphs, HashSet<ContentGlyph> inTable)
    {
        var byTable = new Dictionary<StructNode, Dictionary<StructNode, List<ContentGlyph>>>(ReferenceEqualityComparer.Instance);
        foreach (var g in glyphs)
        {
            var cell = _info[g].Node?.Ancestor(n => n.Type is "TD" or "TH");
            var table = cell?.Ancestor(n => n.Type == "Table");
            if (cell == null || table == null) continue;
            if (!byTable.TryGetValue(table, out var cells)) byTable[table] = cells = new Dictionary<StructNode, List<ContentGlyph>>(ReferenceEqualityComparer.Instance);
            if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<ContentGlyph>();
            list.Add(g);
        }
        foreach (var (table, cellGlyphs) in byTable)
        {
            // Rows in order, directly under the table or in its head, body and foot.
            var rows = new List<StructNode>();
            void Collect(StructNode n, int depth)
            {
                foreach (var c in n.Children)
                    if (c.Type == "TR") rows.Add(c);
                    else if (depth < 3 && c.Type is "THead" or "TBody" or "TFoot" or "NonStruct" or "Div" or "Part" or "Sect") Collect(c, depth + 1);
            }
            Collect(table, 0);
            if (rows.Count == 0) continue;
            var occupied = new HashSet<(int, int)>();
            var specs = new List<CellSpec>();
            int columns = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                int c = 0;
                foreach (var cell in rows[r].Children.Where(n => n.Type is "TD" or "TH"))
                {
                    while (occupied.Contains((r, c))) c++;
                    int rs = Math.Min(cell.RowSpan, rows.Count - r), cs = cell.ColSpan;
                    for (int i = 0; i < rs; i++)
                        for (int j = 0; j < cs; j++) occupied.Add((r + i, c + j));
                    specs.Add(new CellSpec(r, c, rs, cs, cellGlyphs.TryGetValue(cell, out var list) ? list : new List<ContentGlyph>(), cell.Type == "TH"));
                    c += cs;
                    columns = Math.Max(columns, c);
                }
            }
            if (columns == 0 || specs.Count(s => s.Glyphs.Count > 0) < 2 || columns > 64) continue;
            var cols = Boundaries(columns, specs.Where(s => s.Glyphs.Count > 0).Select(s => (s.Col, s.ColSpan, Extent(s.Glyphs, true))), vertical: false);
            var rowsAt = Boundaries(rows.Count, specs.Where(s => s.Glyphs.Count > 0).Select(s => (s.Row, s.RowSpan, Extent(s.Glyphs, false))), vertical: true);
            if (cols == null || rowsAt == null) continue;
            var t = MakeTable(cols, rowsAt, specs);
            t.HeaderRow = specs.Where(s => s.Row == 0).All(s => s.Header);
            foreach (var s in specs) foreach (var g in s.Glyphs) inTable.Add(g);
            yield return t;
        }
    }

    private (double From, double To) Extent(List<ContentGlyph> glyphs, bool horizontal)
    {
        var boxes = glyphs.Select(g => _info[g].Box);
        return horizontal ? (boxes.Min(b => b.L), boxes.Max(b => b.R)) : (boxes.Min(b => b.T), boxes.Max(b => b.B));
    }

    /// <summary>
    /// Grid lines between tracks (columns or rows) from where the text of the cells in them is,
    /// snapped to ruling lines nearby; null when the text does not sort into the tracks.
    /// </summary>
    private double[]? Boundaries(int count, IEnumerable<(int Index, int Span, (double From, double To) Extent)> cells, bool vertical)
    {
        var from = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
        var to = Enumerable.Repeat(double.NegativeInfinity, count).ToArray();
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var (index, span, (f, t)) in cells)
        {
            if (index >= count) continue;
            from[index] = Math.Min(from[index], f);
            int last = Math.Min(count - 1, index + span - 1);
            to[last] = Math.Max(to[last], t);
            min = Math.Min(min, f);
            max = Math.Max(max, t);
        }
        if (double.IsInfinity(min)) return null;
        var b = new double[count + 1];
        b[0] = min - 3;
        b[count] = max + 3;
        for (int i = 1; i < count; i++)
        {
            bool hasTo = !double.IsInfinity(to[i - 1]), hasFrom = !double.IsInfinity(from[i]);
            b[i] = hasTo && hasFrom ? (to[i - 1] + from[i]) / 2 : hasFrom ? from[i] - 2 : hasTo ? to[i - 1] + 2 : double.NaN;
        }
        // Tracks without text of their own share the space around them.
        for (int i = 1; i < count; i++)
            if (double.IsNaN(b[i]))
            {
                int j = i;
                while (j < count && double.IsNaN(b[j])) j++;
                for (int k = i; k < j; k++) b[k] = b[i - 1] + (b[j] - b[i - 1]) * (k - i + 1) / (j - i + 1);
            }
        for (int i = 1; i <= count; i++)
            if (b[i] <= b[i - 1] + 2)
            {
                for (int k = 1; k < count; k++) b[k] = b[0] + (b[count] - b[0]) * k / count;
                break;
            }
        // A ruling line close by is where the grid line really is.
        for (int i = 0; i <= count; i++)
        {
            double best = 6;
            foreach (var r in _rules)
                if (r.Horizontal == vertical && Math.Abs(r.Pos - b[i]) < best) { best = Math.Abs(r.Pos - b[i]); b[i] = r.Pos; }
        }
        for (int i = 1; i <= count; i++) if (b[i] <= b[i - 1]) return null;
        return b;
    }

    // ------------------------------------------------------------------ ruled

    private IEnumerable<DocTable> RuledTables(List<ContentGlyph> glyphs, HashSet<ContentGlyph> inTable, HashSet<int> underlines, List<Box> taken)
    {
        _tableRules = _rules.Where((r, i) => !underlines.Contains(i)).ToList();
        var h = Merge(_tableRules.Where(r => r.Horizontal));
        var v = Merge(_tableRules.Where(r => !r.Horizontal));
        if (h.Count < 2 || v.Count < 2 || (long)h.Count * v.Count > 4_000_000) yield break;

        // Rules that cross or touch belong to one grid.
        var parent = Enumerable.Range(0, h.Count + v.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < h.Count; i++)
            for (int j = 0; j < v.Count; j++)
                if (v[j].Pos >= h[i].From - 2 && v[j].Pos <= h[i].To + 2 && h[i].Pos >= v[j].From - 2 && h[i].Pos <= v[j].To + 2)
                    parent[Find(i)] = Find(h.Count + j);
        var groups = Enumerable.Range(0, h.Count + v.Count).GroupBy(Find);
        var free = glyphs.Where(g => !inTable.Contains(g)).ToList();
        foreach (var group in groups)
        {
            var hs = group.Where(i => i < h.Count).Select(i => h[i]).ToList();
            var vs = group.Where(i => i >= h.Count).Select(i => v[i - h.Count]).ToList();
            if (hs.Count < 2 || vs.Count < 2) continue;
            var box = new Box(Math.Min(hs.Min(r => r.From), vs.Min(r => r.Pos)), Math.Min(vs.Min(r => r.From), hs.Min(r => r.Pos)),
                Math.Max(hs.Max(r => r.To), vs.Max(r => r.Pos)), Math.Max(vs.Max(r => r.To), hs.Max(r => r.Pos)));
            if (taken.Any(t => t.Overlap(box) > 0.2 * Math.Min(t.W * t.H, box.W * box.H))) continue;
            var ruledCols = Tracks(vs.Select(r => r.Pos).Append(box.L).Append(box.R));
            var ruledRows = Tracks(hs.Select(r => r.Pos).Append(box.T).Append(box.B));
            var inside = free.Where(g => !inTable.Contains(g)).ToList();
            var (cols, drawnCol) = SplitCols(ruledCols, box, inside);
            var (rows, drawn) = SplitRows(ruledRows, cols, box, inside);
            int nr = rows.Length - 1, nc = cols.Length - 1;
            if (nr < 1 || nc < 1 || nr * nc < 2 || nr * nc > 5000 || nc > 63) continue;

            // Glyphs by grid square.
            var squares = new Dictionary<(int, int), List<ContentGlyph>>();
            foreach (var g in free)
            {
                var b = _info[g].Box;
                if (inTable.Contains(g) || !box.Contains(b.Cx, b.Cy)) continue;
                int c = Track(cols, b.Cx), r = Track(rows, b.Cy);
                if (c < 0 || r < 0) continue;
                if (!squares.TryGetValue((r, c), out var list)) squares[(r, c)] = list = new List<ContentGlyph>();
                list.Add(g);
            }
            if (squares.Count < 2 && nr * nc > 1) continue;
            // A frame around the page, or a chart's grid: mostly empty.
            if (squares.Count < 0.25 * nr * nc && nr * nc > 8) continue;
            if (box.W * box.H > 0.9 * _width * _height && nr * nc <= 2) continue;

            // Squares join where no line separates them; a join that is not a rectangle stays split by rows.
            var cellOf = new int[nr, nc];
            var up = Enumerable.Range(0, nr * nc).ToArray();
            int FindCell(int x) { while (up[x] != x) x = up[x] = up[up[x]]; return x; }
            // Text on both sides of a missing line, none of it across where the line would be: two cells still.
            bool Apart(int r0, int c0, int r1, int c1, bool vertical, double at)
            {
                if (!squares.TryGetValue((r0, c0), out var a) || !squares.TryGetValue((r1, c1), out var b)) return false;
                return !a.Concat(b).Any(g =>
                {
                    var bx = _info[g].Box;
                    return vertical ? bx.L < at - 0.5 && bx.R > at + 0.5 : bx.T < at - 0.5 && bx.B > at + 0.5;
                });
            }
            // Across a column line found from the text, only text that runs over it joins two squares.
            bool Across(int r, int c, double at) =>
                new[] { (r, c), (r, c + 1) }.Any(k => squares.TryGetValue(k, out var list) && list.Any(g => _info[g].Box.L < at - 0.5 && _info[g].Box.R > at + 0.5));
            for (int r = 0; r < nr; r++)
                for (int c = 0; c < nc; c++)
                {
                    if (c + 1 < nc && EdgeV(cols[c + 1], rows[r], rows[r + 1], vs) == null
                        && (drawnCol[c + 1] ? !Apart(r, c, r, c + 1, true, cols[c + 1]) : Across(r, c, cols[c + 1])))
                        up[FindCell(r * nc + c)] = FindCell(r * nc + c + 1);
                    if (r + 1 < nr && drawn[r + 1] && EdgeH(rows[r + 1], cols[c], cols[c + 1], hs) == null && !Apart(r, c, r + 1, c, false, rows[r + 1]))
                        up[FindCell(r * nc + c)] = FindCell((r + 1) * nc + c);
                }
            var specs = new List<CellSpec>();
            foreach (var region in Enumerable.Range(0, nr * nc).GroupBy(FindCell))
            {
                int r0 = region.Min(x => x / nc), r1 = region.Max(x => x / nc), c0 = region.Min(x => x % nc), c1 = region.Max(x => x % nc);
                if ((r1 - r0 + 1) * (c1 - c0 + 1) == region.Count())
                    specs.Add(new CellSpec(r0, c0, r1 - r0 + 1, c1 - c0 + 1, Gather(squares, r0, r1, c0, c1), false));
                else
                    foreach (var row in region.GroupBy(x => x / nc))
                        foreach (var run in Runs(row.Select(x => x % nc).OrderBy(x => x).ToList()))
                            specs.Add(new CellSpec(row.Key, run.From, 1, run.To - run.From + 1, Gather(squares, row.Key, row.Key, run.From, run.To), false));
            }
            // Squares covered twice (a non-rectangular join split by rows overlapping a rectangle) cannot be a Word table.
            var covered = new HashSet<(int, int)>();
            bool clash = false;
            foreach (var s in specs)
                for (int i = 0; i < s.RowSpan; i++)
                    for (int j = 0; j < s.ColSpan; j++) clash |= !covered.Add((s.Row + i, s.Col + j));
            if (clash) continue;
            var table = MakeTable(cols, rows, specs);
            foreach (var s in specs) foreach (var g in s.Glyphs) inTable.Add(g);
            taken.Add(table.Box);
            yield return table;
        }
    }

    /// <summary>
    /// Rows the ruling lines leave tall, holding several lines of text that line up across the
    /// columns (a body without row rules), are split between those lines. Returns the row
    /// boundaries and which of them are drawn (true) rather than found from the text.
    /// </summary>
    private (double[] Rows, bool[] Drawn) SplitRows(double[] rows, double[] cols, Box box, List<ContentGlyph> glyphs)
    {
        var result = new List<(double Y, bool Drawn)>();
        var byRow = glyphs.Where(g => g.Text.Trim().Length > 0).Select(g => (G: g, B: _info[g].Box)).Where(x => box.Contains(x.B.Cx, x.B.Cy))
            .GroupBy(x => Track(rows, x.B.Cy)).ToDictionary(g => g.Key, g => g.ToList());
        for (int r = 0; r + 1 < rows.Length; r++)
        {
            result.Add((rows[r], true));
            if (!byRow.TryGetValue(r, out var inRow)) continue;
            if (inRow.Count < 6) continue;
            // Lines of text by baseline.
            var clusters = new List<(double Base, double Top, double Bottom, HashSet<int> Cols)>();
            foreach (var x in inRow.Select(x => (x.B, Base: Show(x.G.Origin).Y)).OrderBy(x => x.Base))
            {
                int col = Track(cols, x.B.Cx);
                if (clusters.Count > 0 && x.Base - clusters[^1].Base <= Math.Max(1.5, 0.2 * x.B.H))
                {
                    var c = clusters[^1];
                    c.Cols.Add(col);
                    clusters[^1] = (c.Base, Math.Min(c.Top, x.B.T), Math.Max(c.Bottom, x.B.B), c.Cols);
                }
                else clusters.Add((x.Base, x.B.T, x.B.B, new HashSet<int> { col }));
            }
            if (clusters.Count < 3 || clusters.Count(c => c.Cols.Count >= 2) * 2 < clusters.Count) continue;
            for (int k = 1; k < clusters.Count; k++)
            {
                double y = (clusters[k - 1].Bottom + clusters[k].Top) / 2;
                if (clusters[k].Top > clusters[k - 1].Bottom && y > rows[r] + 1 && y < rows[r + 1] - 1) result.Add((y, false));
            }
        }
        result.Add((rows[^1], true));
        return (result.Select(x => x.Y).ToArray(), result.Select(x => x.Drawn).ToArray());
    }

    /// <summary>
    /// Columns the ruling lines leave wide, holding text in columns of its own (years across a
    /// table with only its outer lines drawn), are split where most rows of text leave a gap.
    /// Returns the column boundaries and which of them are drawn.
    /// </summary>
    private (double[] Cols, bool[] Drawn) SplitCols(double[] cols, Box box, List<ContentGlyph> glyphs)
    {
        var result = new List<(double X, bool Drawn)>();
        var inBox = glyphs.Where(g => g.Text.Trim().Length > 0).Select(g => (B: _info[g].Box, Base: Show(g.Origin).Y)).Where(x => box.Contains(x.B.Cx, x.B.Cy)).ToList();
        for (int c = 0; c + 1 < cols.Length; c++)
        {
            result.Add((cols[c], true));
            var inCol = inBox.Where(x => x.B.Cx >= cols[c] && x.B.Cx < cols[c + 1]).OrderBy(x => x.Base).ToList();
            if (inCol.Count < 6) continue;
            // Rows of text by baseline, and the stretch each covers.
            var rows = new List<List<Box>>();
            double last = double.NegativeInfinity;
            foreach (var x in inCol)
            {
                if (x.Base - last > 1.5) rows.Add(new List<Box>());
                rows[^1].Add(x.B);
                last = rows[^1].Count == 1 ? x.Base : last;
            }
            if (rows.Count < 3) continue;
            double l = cols[c], width = cols[c + 1] - cols[c];
            int n = (int)Math.Ceiling(width) + 1;
            if (n > 5000) continue;
            var count = new int[n];
            foreach (var row in rows)
            {
                var hit = new bool[n];
                foreach (var b in row)
                    for (int k = Math.Max(0, (int)Math.Floor(b.L - l)); k < Math.Min(n, (int)Math.Ceiling(b.R - l)); k++) hit[k] = true;
                for (int k = 0; k < n; k++) if (hit[k]) count[k]++;
            }
            double size = inCol.Select(x => x.B.H).OrderBy(h => h).ElementAt(inCol.Count / 2);
            int limit = (int)(0.15 * rows.Count);
            for (int k = 0; k < n;)
            {
                if (count[k] > limit) { k++; continue; }
                int a = k;
                while (k < n && count[k] <= limit) k++;
                // A gap between text on its left and text on its right, in the middle of the column.
                bool inner = a > 0 && k < n && a > 1 && k < n - 1;
                if (inner && k - a >= Math.Max(4, 0.5 * size)) result.Add((l + (a + k) / 2.0, false));
            }
        }
        result.Add((cols[^1], true));
        return (result.Select(x => x.X).ToArray(), result.Select(x => x.Drawn).ToArray());
    }

    private static List<ContentGlyph> Gather(Dictionary<(int, int), List<ContentGlyph>> squares, int r0, int r1, int c0, int c1)
    {
        var list = new List<ContentGlyph>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (squares.TryGetValue((r, c), out var g)) list.AddRange(g);
        // In content order, as the paragraphs are found from it.
        return list.OrderBy(g => g.Op).ThenBy(g => g.Index).ToList();
    }

    private static IEnumerable<(int From, int To)> Runs(List<int> sorted)
    {
        int start = sorted[0], prev = sorted[0];
        foreach (int x in sorted.Skip(1))
        {
            if (x != prev + 1) { yield return (start, prev); start = x; }
            prev = x;
        }
        yield return (start, prev);
    }

    /// <summary>Collinear pieces of a line, touching or overlapping, as one.</summary>
    private static List<Rule> Merge(IEnumerable<Rule> rules)
    {
        var result = new List<Rule>();
        var sorted = rules.OrderBy(r => r.Pos).ToList();
        int i = 0;
        while (i < sorted.Count)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].Pos - sorted[i].Pos <= 1) j++;
            Rule? current = null;
            foreach (var r in sorted.Skip(i).Take(j - i + 1).OrderBy(r => r.From))
            {
                if (current is { } c && r.From <= c.To + 1.5)
                    current = c with { To = Math.Max(c.To, r.To), Width = Math.Max(c.Width, r.Width) };
                else
                {
                    if (current is { } done) result.Add(done);
                    current = r;
                }
            }
            if (current is { } last) result.Add(last);
            i = j + 1;
        }
        return result;
    }

    /// <summary>Grid line positions: positions closer than 4 points (a double line) count once.</summary>
    private static double[] Tracks(IEnumerable<double> positions)
    {
        var sorted = positions.OrderBy(p => p).ToList();
        var result = new List<double>();
        foreach (double p in sorted)
        {
            if (result.Count > 0 && p - result[^1] < 4)
            {
                if (p == sorted[^1]) result[^1] = p;
                continue;
            }
            result.Add(p);
        }
        return result.ToArray();
    }

    private static int Track(double[] bounds, double v)
    {
        for (int i = 0; i + 1 < bounds.Length; i++)
            if (v >= bounds[i] && v < bounds[i + 1]) return i;
        return -1;
    }

    private DocBorder? EdgeH(double y, double x0, double x1, List<Rule>? among = null)
    {
        foreach (var r in among ?? _tableRules)
            if (r.Horizontal && Math.Abs(r.Pos - y) <= 3 && Math.Min(r.To, x1) - Math.Max(r.From, x0) >= 0.6 * (x1 - x0))
                return new DocBorder(r.Width, r.Color);
        return null;
    }

    private DocBorder? EdgeV(double x, double y0, double y1, List<Rule>? among = null)
    {
        foreach (var r in among ?? _tableRules)
            if (!r.Horizontal && Math.Abs(r.Pos - x) <= 3 && Math.Min(r.To, y1) - Math.Max(r.From, y0) >= 0.6 * (y1 - y0))
                return new DocBorder(r.Width, r.Color);
        return null;
    }

    /// <summary>A Word table of the grid: each cell's text as paragraphs, its borders, shading and vertical alignment.</summary>
    private DocTable MakeTable(double[] cols, double[] rows, List<CellSpec> specs)
    {
        var table = new DocTable { Cols = cols, Rows = rows, Box = new Box(cols[0], rows[0], cols[^1], rows[^1]) };
        foreach (var s in specs.OrderBy(s => s.Row).ThenBy(s => s.Col))
        {
            var box = new Box(cols[s.Col], rows[s.Row], cols[s.Col + s.ColSpan], rows[s.Row + s.RowSpan]);
            var cell = new DocCell
            {
                Row = s.Row, Col = s.Col, RowSpan = s.RowSpan, ColSpan = s.ColSpan, Box = box, Header = s.Header,
                Paragraphs = BuildParagraphs(s.Glyphs).OrderBy(p => p.Box.T).ThenBy(p => p.Box.L).ToList(),
                Top = EdgeH(box.T, box.L, box.R), Bottom = EdgeH(box.B, box.L, box.R),
                Left = EdgeV(box.L, box.T, box.B), Right = EdgeV(box.R, box.T, box.B),
                Shading = ShadingOf(box.Inflate(-1)),
            };
            if (cell.Paragraphs.Count > 0)
            {
                double above = cell.Paragraphs.Min(p => p.Box.T) - box.T, below = box.B - cell.Paragraphs.Max(p => p.Box.B);
                cell.VAlign = Math.Abs(above - below) < 2.5 && above > 3 ? "center" : below < above - 4 ? "bottom" : "top";
            }
            table.Cells.Add(cell);
        }
        return table;
    }

    // ------------------------------------------------------------------ aligned text

    /// <summary>
    /// Text in aligned columns without ruling lines: at least three rows of short cells, with
    /// gaps that no row's text crosses. The paragraphs it takes become its cells.
    /// </summary>
    private IEnumerable<DocTable> GridTables(List<DocParagraph> paragraphs)
    {
        var flowing = paragraphs.Where(p => !p.Rotated && !p.Vertical && p.Lines.Count > 0).ToList();
        if (flowing.Count < 3) yield break;
        double left = flowing.Min(p => p.Box.L), right = flowing.Max(p => p.Box.R), width = Math.Max(right - left, 1);
        // A column of short entries one under the other (figures drawn column by column) reads as
        // one paragraph; as cells, its lines are separate.
        var pieces = new Dictionary<DocParagraph, DocParagraph>(ReferenceEqualityComparer.Instance);
        var candidates = new List<DocParagraph>();
        foreach (var p in flowing)
        {
            // Not narrow columns of prose: their lines are longer, or set justified.
            if (p.Lines.Count >= 2 && p.Box.W <= 0.35 * width && p.Text.Length <= 30 * p.Lines.Count
                && !(p.Align == PdfEngine.Vector.Editing.PdfTextAlignment.Justified && p.Text.Length > 12 * p.Lines.Count)
                && p.CharLine.Count == p.Text.Length)
                foreach (var piece in SplitByLine(p)) { pieces[piece] = p; candidates.Add(piece); }
            else candidates.Add(p);
        }
        candidates = candidates.OrderBy(p => p.FirstBaseline).ThenBy(p => p.Box.L).ToList();
        if (candidates.Count < 6) yield break;
        bool Cell(DocParagraph p) => p.Lines.Count == 1 ? p.Box.W <= 0.6 * width : p.Box.W <= 0.3 * width && p.Lines.Count <= 4;

        var rows = new List<List<DocParagraph>>();
        foreach (var p in candidates)
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].FirstBaseline - p.FirstBaseline) <= 0.3 * Math.Max(p.Size, rows[^1][0].Size)) rows[^1].Add(p);
            else rows.Add(new List<DocParagraph> { p });
        }
        int i = 0;
        while (i < rows.Count)
        {
            // The longest run of rows of cells, each close under the one before.
            int j = i;
            if (rows[i].All(Cell))
                while (j + 1 < rows.Count && rows[j + 1].All(Cell)
                       && rows[j + 1].Min(p => p.Box.T) - rows[j].Max(p => p.Box.B) <= 1.6 * rows[j].Max(p => p.Size)) j++;
            var run = rows.Skip(i).Take(j - i + 1).ToList();
            i = j + 1;
            int multi = run.Count(r => r.Count >= 2);
            if (run.Count < 3 || multi < 3 || multi < 0.6 * run.Count) continue;
            var members = run.SelectMany(r => r).ToList();
            if (members.Average(p => p.Text.Length) > 40) continue;
            double l = members.Min(p => p.Box.L), r = members.Max(p => p.Box.R);
            double size = members.Select(p => p.Size).OrderBy(s => s).ElementAt(members.Count / 2);
            // Gaps most rows leave open (a title across the table may cross them).
            var gaps = RowGaps(run.Select(row => row.Select(p => (p.Box.L, p.Box.R)).ToList()).ToList(), l, r, Math.Max(6, 0.8 * size));
            if (gaps.Count == 0 || gaps.Count > 20) continue;
            var cols = new List<double> { l - 2 };
            cols.AddRange(gaps.Select(g => (g.From + g.To) / 2));
            cols.Add(r + 2);
            int nc = cols.Count - 1;
            var filled = new int[nc];
            foreach (var row in run)
                foreach (int c in row.Select(p => Track(cols.ToArray(), p.Box.Cx)).Where(c => c >= 0).Distinct()) filled[c]++;
            if (filled.Count(f => f >= 0.5 * run.Count) < 2) continue;

            var rowsAt = new List<double> { run[0].Min(p => p.Box.T) - 1 };
            for (int k = 1; k < run.Count; k++) rowsAt.Add((run[k - 1].Max(p => p.Box.B) + run[k].Min(p => p.Box.T)) / 2);
            rowsAt.Add(run[^1].Max(p => p.Box.B) + 1);
            var table = new DocTable { Cols = cols.ToArray(), Rows = rowsAt.ToArray() };
            table.Box = new Box(table.Cols[0], table.Rows[0], table.Cols[^1], table.Rows[^1]);
            for (int ri = 0; ri < run.Count; ri++)
            {
                // A paragraph across gaps spans those columns.
                var spans = run[ri].Select(p => (P: p, From: Math.Max(0, Track(table.Cols, p.Box.L + 1)), To: Track(table.Cols, p.Box.R - 1) is int t && t >= 0 ? t : nc - 1)).ToList();
                for (int c = 0; c < nc;)
                {
                    var here = spans.Where(s => s.From <= c && s.To >= c).ToList();
                    int to = here.Count > 0 ? Math.Max(c, here.Max(s => s.To)) : c;
                    var box = new Box(table.Cols[c], table.Rows[ri], table.Cols[to + 1], table.Rows[ri + 1]);
                    table.Cells.Add(new DocCell
                    {
                        Row = ri, Col = c, ColSpan = to - c + 1, Box = box,
                        Paragraphs = here.Select(s => s.P).Distinct().OrderBy(p => p.Box.T).ThenBy(p => p.Box.L).ToList(),
                        Top = EdgeH(box.T, box.L, box.R), Bottom = EdgeH(box.B, box.L, box.R),
                        Shading = ShadingOf(box.Inflate(-1)),
                    });
                    spans.RemoveAll(s => s.From <= to);
                    c = to + 1;
                }
            }
            foreach (var p in members)
            {
                if (!pieces.TryGetValue(p, out var whole)) { paragraphs.Remove(p); continue; }
                // A paragraph split into lines: the lines the table did not take stay in the flow, one by one.
                if (!paragraphs.Remove(whole)) continue;
                foreach (var rest in pieces.Where(kv => ReferenceEquals(kv.Value, whole) && !members.Contains(kv.Key)).Select(kv => kv.Key))
                    paragraphs.Add(rest);
            }
            yield return table;
        }
    }

    /// <summary>Each line of a paragraph as a paragraph of its own.</summary>
    private static IEnumerable<DocParagraph> SplitByLine(DocParagraph p)
    {
        for (int li = 0; li < p.Lines.Count; li++)
        {
            var piece = new DocParagraph { Align = p.Align, Rtl = p.Rtl, Pitch = p.Pitch, Shading = p.Shading, Box = p.Lines[li].Box, TagHeading = p.TagHeading };
            piece.Lines.Add(p.Lines[li]);
            int pos = 0;
            foreach (var r in p.Runs)
            {
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < r.Text.Length; k++)
                    if (p.CharLine[pos + k] == li)
                    {
                        sb.Append(r.Text[k]);
                        piece.CharX.Add(p.CharX[pos + k]);
                        piece.CharLine.Add(0);
                    }
                pos += r.Text.Length;
                if (sb.Length > 0) piece.Runs.Add(r.Clone(sb.ToString()));
            }
            // The space that joined it to the next line is not its own.
            while (piece.Runs.Count > 0 && piece.Runs[^1].Text.EndsWith(' '))
            {
                var r = piece.Runs[^1];
                r.Text = r.Text[..^1];
                piece.CharX.RemoveAt(piece.CharX.Count - 1);
                piece.CharLine.RemoveAt(piece.CharLine.Count - 1);
                if (r.Text.Length == 0) piece.Runs.RemoveAt(piece.Runs.Count - 1);
            }
            if (piece.Runs.Count > 0) yield return piece;
        }
    }

    /// <summary>Stretches of [from, to] that at most a sixth of the rows cover, at least <paramref name="min"/> wide, with text on both sides.</summary>
    private static List<(double From, double To)> RowGaps(List<List<(double L, double R)>> rows, double from, double to, double min)
    {
        int n = (int)Math.Ceiling(to - from) + 1;
        var gaps = new List<(double, double)>();
        if (n <= 2 || n > 10000) return gaps;
        var count = new int[n];
        foreach (var row in rows)
        {
            var hit = new bool[n];
            foreach (var (l, r) in row)
                for (int k = Math.Max(0, (int)Math.Floor(l - from)); k < Math.Min(n, (int)Math.Ceiling(r - from)); k++) hit[k] = true;
            for (int k = 0; k < n; k++) if (hit[k]) count[k]++;
        }
        int limit = rows.Count / 6;
        for (int k = 0; k < n;)
        {
            if (count[k] > limit) { k++; continue; }
            int a = k;
            while (k < n && count[k] <= limit) k++;
            if (a > 0 && k < n && k - a >= min) gaps.Add((from + a, from + k));
        }
        return gaps;
    }

    /// <summary>Stretches of [from, to] no extent covers, at least <paramref name="min"/> wide.</summary>
    internal static List<(double From, double To)> Gaps(IEnumerable<(double L, double R)> extents, double from, double to, double min)
    {
        var sorted = extents.OrderBy(e => e.L).ToList();
        var gaps = new List<(double, double)>();
        double reach = from;
        foreach (var (l, r) in sorted)
        {
            if (l - reach >= min && reach > from) gaps.Add((reach, l));
            reach = Math.Max(reach, r);
        }
        return gaps;
    }
}
