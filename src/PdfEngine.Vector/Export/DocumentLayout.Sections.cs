using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>Sections: page setup, columns, reading order, indents and the space between paragraphs.</summary>
internal static partial class DocumentLayout
{
    /// <summary>
    /// Left and right padding inside table cells, in points: none, so a cell's text has all the
    /// width the page gave it (its indent keeps it where it was).
    /// </summary>
    internal const double CellPad = 0;

    private sealed class Band
    {
        public List<DocItem> Items = new();
        /// <summary>Empty for a single column.</summary>
        public List<(double From, double To)> Gutters = new();
    }

    private static void Sections(List<DocPage> pages, DocDocument doc)
    {
        var margins = Margins(pages, doc);
        DocSection? current = null;
        for (int pi = 0; pi < pages.Count; pi++)
        {
            var page = pages[pi];
            var (ml, mt, mr, mb) = margins[pi];
            double left = ml, right = page.Width - mr;
            var bands = page.VerticalWriting ? new List<Band> { new() { Items = page.Items.ToList() } } : Bands(page.Items);
            if (bands.Count == 0) bands.Add(new Band());

            double cursor = mt;
            bool pageStart = true, pageNewSection = false;
            DocSection? firstSection = null;
            int firstIndex = -1;
            foreach (var band in bands)
            {
                var section = new DocSection
                {
                    Width = page.Width, Height = page.Height, MarginL = ml, MarginT = mt, MarginR = mr, MarginB = mb,
                    VerticalWriting = page.VerticalWriting,
                };
                var columns = ColumnBounds(band, left, right);
                if (columns.Count > 1)
                    for (int c = 0; c < columns.Count; c++)
                        section.Columns.Add((columns[c].R - columns[c].L, c + 1 < columns.Count ? columns[c + 1].L - columns[c].R : 0));
                else section.Columns.Add((right - left, 0));

                bool newSection = current == null || !current.SameSetup(section);
                if (newSection)
                {
                    section.Continuous = !pageStart;
                    doc.Sections.Add(section);
                    current = section;
                }
                var start = current!.Items.Count;
                if (page.VerticalWriting)
                {
                    foreach (var item in band.Items) LayoutItem(item, left, right);
                    current.Items.AddRange(band.Items);
                }
                else if (columns.Count == 1)
                {
                    var ordered = Order(band.Items, left, right);
                    cursor = Space(ordered, left, right, cursor);
                    current.Items.AddRange(ordered);
                }
                else
                {
                    double bandTop = cursor, bandEnd = cursor;
                    for (int c = 0; c < columns.Count; c++)
                    {
                        var col = columns[c];
                        var ordered = Order(band.Items.Where(i => ColumnOf(i, columns) == c).ToList(), col.L, col.R);
                        if (ordered.Count == 0) continue;
                        if (c > 0) ordered[0].ColumnBreakBefore = true;
                        bandEnd = Math.Max(bandEnd, Space(ordered, col.L, col.R, bandTop));
                        current.Items.AddRange(ordered);
                    }
                    cursor = bandEnd;
                }
                if (pageStart) pageNewSection = newSection;
                if (firstSection == null && current.Items.Count > start) (firstSection, firstIndex) = (current, start);
                pageStart = false;
            }

            // An empty page is an empty paragraph; pictures at fixed places hang on the page's first paragraph.
            if (firstSection == null)
            {
                current!.Items.Add(new DocParagraph { Box = new Box(left, mt, left, mt) });
                (firstSection, firstIndex) = (current, current.Items.Count - 1);
            }
            if (page.Floating.Count > 0 && firstSection.Items[firstIndex] is not DocParagraph { Image: null })
            {
                var holder = new DocParagraph { Box = new Box(left, mt, left, mt), ColumnBreakBefore = firstSection.Items[firstIndex].ColumnBreakBefore };
                firstSection.Items[firstIndex].ColumnBreakBefore = false;
                firstSection.Items.Insert(firstIndex, holder);
            }
            // A page that goes on in the same section starts with a page break; a new section starts a page itself.
            if (pi > 0 && !pageNewSection) firstSection.Items[firstIndex].PageBreakBefore = true;
            if (firstSection.Items[firstIndex] is DocParagraph anchor) anchor.Anchors.AddRange(page.Floating);
        }

        // Headers and footers: laid out across the first section's text width.
        if (doc.Sections.Count > 0)
        {
            var s = doc.Sections[0];
            double l = s.MarginL, r = s.Width - s.MarginR;
            var hs = pages.SelectMany(p => p.Header).ToList();
            var fs = pages.SelectMany(p => p.Footer).ToList();
            if (hs.Count > 0) foreach (var sec in doc.Sections) sec.HeaderDistance = Math.Max(0, hs.Min(p => p.Box.T) - 2);
            if (fs.Count > 0) foreach (var sec in doc.Sections) sec.FooterDistance = Math.Max(0, pages.Min(p => p.Height) - fs.Max(p => p.Box.B) - 2);
            Space(doc.Header, l, r, doc.Header.Count > 0 ? doc.Header[0].Box.T : 0, first: true);
            Space(doc.Footer, l, r, doc.Footer.Count > 0 ? doc.Footer[0].Box.T : 0, first: true);
            s.TitlePage = !doc.HeaderOnFirstPage || !doc.FooterOnFirstPage;
        }
    }

    // ------------------------------------------------------------------ margins

    /// <summary>
    /// Page margins from the extent of the text (and tables and pictures in the flow), the same
    /// for a run of pages of one size so they share a section; the header and footer fit outside.
    /// </summary>
    private static List<(double L, double T, double R, double B)> Margins(List<DocPage> pages, DocDocument doc)
    {
        var result = new List<(double, double, double, double)>();
        int i = 0;
        while (i < pages.Count)
        {
            int j = i;
            while (j + 1 < pages.Count && Math.Abs(pages[j + 1].Width - pages[i].Width) < 1 && Math.Abs(pages[j + 1].Height - pages[i].Height) < 1) j++;
            double w = pages[i].Width, h = pages[i].Height;
            double ml = double.MaxValue, mt = double.MaxValue, mr = double.MaxValue, mb = double.MaxValue;
            double headerBottom = 0, footerTop = h;
            for (int k = i; k <= j; k++)
            {
                var boxes = pages[k].Items.Where(it => it is not DocParagraph { Image: null, Runs.Count: 0 }).Select(it => it.Box).ToList();
                if (boxes.Count > 0)
                {
                    ml = Math.Min(ml, boxes.Min(b => b.L));
                    mr = Math.Min(mr, w - boxes.Max(b => b.R));
                    mt = Math.Min(mt, boxes.Min(b => b.T));
                    mb = Math.Min(mb, h - boxes.Max(b => b.B));
                }
                if (pages[k].Header.Count > 0) headerBottom = Math.Max(headerBottom, pages[k].Header.Max(p => p.Box.B));
                if (pages[k].Footer.Count > 0) footerTop = Math.Min(footerTop, pages[k].Footer.Min(p => p.Box.T));
            }
            if (ml == double.MaxValue) ml = mr = mt = mb = Math.Min(72, Math.Min(w, h) / 8);
            ml = Math.Max(0, ml - 1);
            mr = Math.Max(0, mr - 1);
            mt = Math.Max(0, mt - 1);
            // Room at the foot, for Word's lines being a little taller than the page's: every page
            // starts with a page break, so what runs over goes there rather than onto a page of its own.
            mb = Math.Max(0, mb - 24);
            if (headerBottom > 0) mt = Math.Max(mt, headerBottom + 2);
            if (footerTop < h) mb = Math.Max(mb, h - footerTop + 2);
            if (w - ml - mr < 72) ml = mr = Math.Max(0, Math.Min(72, (w - 72) / 2));
            if (h - mt - mb < 72) mt = mb = Math.Max(0, Math.Min(72, (h - 72) / 2));
            for (int k = i; k <= j; k++) result.Add((ml, mt, mr, mb));
            i = j + 1;
        }
        return result;
    }

    // ------------------------------------------------------------------ columns and reading order

    /// <summary>
    /// Bands of the page from top to bottom: those set in columns (with the gutters between
    /// them) and those across the page (titles, wide tables and figures between column text).
    /// </summary>
    private static List<Band> Bands(List<DocItem> items)
    {
        var bands = new List<Band>();
        if (items.Count == 0) return bands;
        var gutters = Gutters(items);
        if (gutters.Count == 0)
        {
            bands.Add(new Band { Items = items.ToList() });
            return bands;
        }
        bool Spans(DocItem i) => gutters.Any(g => i.Box.L < g.From - 1 && i.Box.R > g.To + 1);
        Band? band = null;
        bool spanning = false;
        foreach (var item in items.OrderBy(i => i.Box.T))
        {
            bool s = Spans(item);
            if (band == null || s != spanning)
            {
                band = new Band { Gutters = s ? new() : gutters.ToList() };
                bands.Add(band);
                spanning = s;
            }
            band.Items.Add(item);
        }
        // Column bands with too little in them (or text in one column only) read as single columns.
        foreach (var b in bands.Where(b => b.Gutters.Count > 0))
        {
            var cols = ColumnBounds(b, double.NegativeInfinity, double.PositiveInfinity);
            int lines = b.Items.Sum(i => i is DocParagraph p ? Math.Max(1, p.Lines.Count) : 3);
            int used = Enumerable.Range(0, cols.Count).Count(c => b.Items.Any(i => ColumnOf(i, cols) == c));
            if (lines < 3 || used < 2) b.Gutters.Clear();
        }
        var merged = new List<Band>();
        foreach (var b in bands)
        {
            if (merged.Count > 0 && merged[^1].Gutters.Count == 0 && b.Gutters.Count == 0) merged[^1].Items.AddRange(b.Items);
            else merged.Add(b);
        }
        return merged;
    }

    /// <summary>
    /// Gutters: vertical strips, well inside the text, that almost no line crosses, with a good
    /// share of the text on each side. Two at most (three columns).
    /// </summary>
    private static List<(double From, double To)> Gutters(List<DocItem> items) =>
        Gutters(items.SelectMany(i => i is DocParagraph { Image: null } p && p.Lines.Count > 0 ? p.Lines.Select(l => l.Box) : new[] { i.Box }).ToList(), 4);

    /// <summary>Gutters among boxes (lines, or single glyphs) at least <paramref name="minWidth"/> wide.</summary>
    internal static List<(double From, double To)> Gutters(List<Box> all, double minWidth)
    {
        var none = new List<(double, double)>();
        var boxes = all.Where(b => b.W > 0 && b.H > 0).ToList();
        if (boxes.Count < 6) return none;
        double left = boxes.Min(b => b.L), right = boxes.Max(b => b.R), width = right - left;
        if (width < 150 || width > 5000) return none;
        int n = (int)Math.Ceiling(width) + 1;
        var cover = new double[n];
        double total = 0;
        foreach (var b in boxes)
        {
            total += b.H;
            for (int x = Math.Max(0, (int)Math.Floor(b.L - left)); x < Math.Min(n, (int)Math.Ceiling(b.R - left)); x++) cover[x] += b.H;
        }
        // Low against how much a column's strip is covered (lines cover a strip whole, glyphs only where they are).
        double full = cover.OrderBy(c => c).ElementAt((int)(0.9 * (n - 1)));
        double low = 0.2 * full, slack = 0.02 * full;
        var runs = new List<(int A, int B, double Worst)>();
        for (int x = 0; x < n;)
        {
            if (cover[x] > low) { x++; continue; }
            int a = x;
            double worst = 0;
            while (x < n && cover[x] <= low) worst = Math.Max(worst, cover[x++]);
            double mid = (a + x) / 2.0;
            if (x - a >= minWidth && mid > 0.15 * width && mid < 0.85 * width) runs.Add((a, x, worst));
        }
        var gutters = new List<(double From, double To)>();
        foreach (var run in runs.OrderBy(r => r.Worst).ThenByDescending(r => r.B - r.A))
        {
            // Tightened to where it is emptiest.
            double least = double.MaxValue;
            for (int x = run.A; x < run.B; x++) least = Math.Min(least, cover[x]);
            int a = run.A, b = run.B;
            while (a < b && cover[a] > least + slack) a++;
            while (b > a && cover[b - 1] > least + slack) b--;
            if (b - a < Math.Min(6, minWidth)) continue;
            var g = (From: left + a, To: left + b);
            if (gutters.Any(o => Math.Abs((o.From + o.To) / 2 - (g.From + g.To) / 2) < 0.2 * width)) continue;
            var trial = gutters.Append(g).OrderBy(o => o.From).ToList();
            // Every column holds a good share of the text.
            var edges = new List<double> { double.NegativeInfinity };
            foreach (var t in trial) { edges.Add(t.From); edges.Add(t.To); }
            edges.Add(double.PositiveInfinity);
            bool ok = true;
            for (int c = 0; c < trial.Count + 1; c++)
            {
                double lo = edges[2 * c], hi = edges[2 * c + 1];
                double mass = boxes.Where(bx => bx.L >= lo - 1 && bx.R <= hi + 1).Sum(bx => bx.H);
                if (mass < (trial.Count == 1 ? 0.2 : 0.15) * total) ok = false;
            }
            if (!ok) continue;
            gutters = trial;
            if (gutters.Count == 2) break;
        }
        return gutters;
    }

    private static List<(double L, double R)> ColumnBounds(Band band, double left, double right)
    {
        var result = new List<(double, double)>();
        double l = left;
        foreach (var g in band.Gutters.OrderBy(g => g.From))
        {
            result.Add((l, g.From));
            l = g.To;
        }
        result.Add((l, right));
        // Columns too narrow to hold text in Word are not columns.
        if (result.Count > 1 && !double.IsInfinity(left) && result.Any(c => c.Item2 - c.Item1 < 40)) return new List<(double, double)> { (left, right) };
        return result;
    }

    private static int ColumnOf(DocItem item, List<(double L, double R)> columns)
    {
        double x = item.Box.Cx;
        for (int c = 0; c < columns.Count - 1; c++)
            if (x < (columns[c].R + columns[c + 1].L) / 2) return c;
        return columns.Count - 1;
    }

    /// <summary>Top to bottom, left to right for things side by side; single lines on one baseline become one line with tabs.</summary>
    private static List<DocItem> Order(List<DocItem> items, double left, double right)
    {
        var list = items.OrderBy(i => i.Box.T).ToList();
        for (int pass = 0; pass < 4; pass++)
        {
            bool swapped = false;
            for (int k = 0; k + 1 < list.Count; k++)
            {
                var a = list[k];
                var b = list[k + 1];
                if (b.Box.L < a.Box.L && a.Box.OverlapY(b.Box) > 0.5 * Math.Min(a.Box.H, b.Box.H) && a.Box.OverlapX(b.Box) <= 0)
                {
                    list[k] = b;
                    list[k + 1] = a;
                    swapped = true;
                }
            }
            if (!swapped) break;
        }
        // Blocks of several lines side by side (a letterhead's address beside its date): one row of
        // a table without borders, so they stay side by side rather than one under the other.
        for (int k = 0; k < list.Count; k++)
        {
            if (list[k] is not DocParagraph first || !Flowing(first) || first.List != null) continue;
            var group = new List<DocParagraph> { first };
            var box = first.Box;
            for (int j = k + 1; j < list.Count && list[j].Box.T < box.B; j++)
                if (list[j] is DocParagraph b && Flowing(b) && b.List == null && b.Box.OverlapY(box) > 0.3 * Math.Min(b.Box.H, box.H) && group.All(g => g.Box.OverlapX(b.Box) <= 0))
                {
                    group.Add(b);
                    box = box.Union(b.Box);
                }
            if (group.Count < 2 || group.All(g => g.Lines.Count == 1)) continue;
            group = group.OrderBy(g => g.Box.L).ToList();
            var cols = new List<double> { group[0].Box.L - 1 };
            for (int c = 1; c < group.Count; c++) cols.Add((group[c - 1].Box.R + group[c].Box.L) / 2);
            cols.Add(group[^1].Box.R + 1);
            var table = new DocTable { Cols = cols.ToArray(), Rows = new[] { box.T - 1, box.B + 1 } };
            table.Box = new Box(cols[0], box.T - 1, cols[^1], box.B + 1);
            for (int c = 0; c < group.Count; c++)
                table.Cells.Add(new DocCell { Row = 0, Col = c, Box = new Box(cols[c], box.T - 1, cols[c + 1], box.B + 1), Paragraphs = { group[c] } });
            foreach (var g in group) list.Remove(g);
            list.Insert(k, table);
        }
        for (int k = 0; k < list.Count; k++)
        {
            if (list[k] is not DocParagraph a || !OneLine(a)) continue;
            while (k + 1 < list.Count && list[k + 1] is DocParagraph b && OneLine(b)
                   && Math.Abs(b.FirstBaseline - a.FirstBaseline) < 0.3 * Math.Max(a.Size, b.Size) && b.Box.L > a.Box.R - 1)
            {
                AppendWithTab(a, b, right, left);
                list.RemoveAt(k + 1);
            }
        }
        return list;
    }

    private static bool OneLine(DocParagraph p) => p.Image == null && p.Lines.Count == 1 && !p.Rotated && !p.Vertical && p.List == null && p.Heading == 0 && !p.Rtl;

    // ------------------------------------------------------------------ indents and spacing

    /// <summary>Lays out items one after another from <paramref name="cursor"/> down; returns where the last ends.</summary>
    private static double Space(List<DocItem> items, double left, double right, double cursor) => Space(items.Cast<DocItem>().ToList(), left, right, cursor, false);

    private static double Space<T>(List<T> items, double left, double right, double cursor, bool first) where T : DocItem
    {
        foreach (var item in items)
        {
            LayoutItem(item, left, right);
            double top = Top(item);
            item.SpaceBefore = first ? 0 : Math.Clamp(top - cursor, 0, 1500);
            first = false;
            cursor = Math.Max(Bottom(item), top);
        }
        return cursor;
    }

    private static bool Flowing(DocParagraph p) => p.Image == null && !p.Rotated && !p.Vertical && p.Lines.Count > 0;

    /// <summary>Where Word's first line box starts (exact line height, the text low in it).</summary>
    internal static double Top(DocItem item) => item is DocParagraph p && Flowing(p) ? p.FirstBaseline - 0.8 * p.LineHeight : item.Box.T;

    internal static double Bottom(DocItem item) => item is DocParagraph p && Flowing(p) ? p.LastBaseline + 0.2 * p.LineHeight : item.Box.B;

    private static void LayoutItem(DocItem item, double left, double right)
    {
        switch (item)
        {
            case DocParagraph p: LayoutParagraph(p, left, right); break;
            case DocTable t:
                t.Indent = t.Box.L - left;
                foreach (var cell in t.Cells)
                {
                    Space(cell.Paragraphs, cell.Box.L + CellPad, cell.Box.R - CellPad, cell.Box.T + 1, false);
                    if (cell.VAlign != "top" && cell.Paragraphs.Count > 0) cell.Paragraphs[0].SpaceBefore = 0;
                }
                break;
        }
    }

    /// <summary>Alignment, indents and line height of a paragraph in a column from <paramref name="left"/> to <paramref name="right"/>.</summary>
    internal static void LayoutParagraph(DocParagraph p, double left, double right)
    {
        double width = Math.Max(right - left, 1);
        if (p.Image != null)
        {
            double gapL = p.Box.L - left, gapR = right - p.Box.R;
            p.Align = Math.Abs(gapL - gapR) < Math.Max(4, 0.04 * width) && gapL > 5 ? PdfTextAlignment.Center
                : gapR < 3 && gapL > 20 ? PdfTextAlignment.Right : PdfTextAlignment.Left;
            p.Indent = p.Align == PdfTextAlignment.Left ? gapL : 0;
            return;
        }
        double size = p.Size;
        p.LineHeight = Math.Clamp(p.Lines.Count >= 2 ? p.Pitch : Math.Max(p.Pitch, 1.15 * size), 1.0 * size, 3.0 * size);
        if (p.Lines.Count == 0) { p.LineHeight = Math.Max(p.LineHeight, 1); return; }
        if (p.Rotated || p.Vertical)
        {
            p.Indent = Math.Max(0, p.Box.L - left);
            p.Align = PdfTextAlignment.Left;
            return;
        }
        if (!p.AlignKnown) p.Align = GuessAlign(p, left, right);
        var first = p.Lines[0].Box;
        var rest = p.Lines.Skip(1).Select(l => l.Box).ToList();
        var wrapped = p.Lines.Take(p.Lines.Count - 1).Select(l => l.Box).ToList();
        p.Indent = p.RightIndent = p.FirstLine = 0;
        bool startRight = p.Rtl;
        switch (p.Align)
        {
            case PdfTextAlignment.Center:
            {
                double offset = p.Box.Cx - (left + right) / 2;
                p.Indent = Math.Max(0, 2 * offset);
                p.RightIndent = Math.Max(0, -2 * offset);
                break;
            }
            case PdfTextAlignment.Right when !startRight:
                p.RightIndent = right - p.Box.R;
                break;
            case PdfTextAlignment.Left when startRight:
                p.Indent = p.Box.L - left;
                break;
            default:
                if (startRight)
                {
                    // Lines start at the right: the first line's indent is on that side.
                    double restR = rest.Count > 0 ? rest.Max(b => b.R) : first.R;
                    p.RightIndent = right - restR;
                    p.FirstLine = restR - first.R;
                    if (wrapped.Count > 0) p.Indent = Math.Max(0, wrapped.Min(b => b.L) - left - 1);
                }
                else
                {
                    double restL = rest.Count > 0 ? rest.Min(b => b.L) : first.L;
                    p.Indent = restL - left;
                    p.FirstLine = first.L - restL;
                    // Wrapped lines end where the column ends, or earlier where the page's text did. Word's
                    // stand-in fonts can be a little wider: lines that filled the column get a few points
                    // more, so they do not wrap a word earlier and run the page over.
                    if (wrapped.Count > 0)
                    {
                        double gap = right - wrapped.Max(b => b.R);
                        p.RightIndent = gap > size ? gap - 0.5 * size : -Math.Min(0.015 * width, 6);
                    }
                }
                break;
        }
        if (p.List is { } m)
        {
            p.Indent = m.TextX - left;
            p.FirstLine = m.MarkerX - m.TextX;
        }
        p.Tabs = p.Tabs.Select(t => (t.X - left, t.Kind)).Where(t => t.Item1 > 0).ToList();
    }

    private static PdfTextAlignment GuessAlign(DocParagraph p, double left, double right)
    {
        double width = right - left, gapL = p.Box.L - left, gapR = right - p.Box.R;
        if (p.Tabs.Count > 0) return PdfTextAlignment.Left;
        if (Math.Abs(gapL - gapR) < Math.Max(4, 0.04 * width) && gapL > 0.08 * width) return PdfTextAlignment.Center;
        if (gapR < Math.Max(3, 0.01 * width) && gapL > 0.25 * width) return PdfTextAlignment.Right;
        if (p.Rtl && gapR < gapL) return PdfTextAlignment.Right;
        return PdfTextAlignment.Left;
    }
}
