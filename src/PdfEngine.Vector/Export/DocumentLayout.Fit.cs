using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>
/// Lines that keep to the page: where Word's font sets a line wider than the room the page gave
/// it (a stand-in font, or a font wider than the PDF's glyph widths), the paragraph's characters
/// are set closer (character spacing), or narrower (horizontal scale) when that is not enough, so
/// Word breaks the paragraph into no more lines than the page has. Tables whose text needs more
/// height than their rows had give the room back from the space after them.
/// </summary>
internal static partial class DocumentLayout
{
    /// <summary>Character spacing goes down to this share of the font size; beyond it the text is scaled.</summary>
    private const double MaxCondense = 0.1;

    private static void FitWidths(DocDocument doc)
    {
        foreach (var p in doc.Sections.SelectMany(s => s.Items).SelectMany(ParagraphsOf).ToList())
        {
            Fit(p, doc.FontKinds);
            // Turned text in its text box: as long as on the page.
            foreach (var box in p.TextBoxes)
                foreach (var q in box.Paragraphs) Fit(q, doc.FontKinds);
        }
        foreach (var v in doc.Headers.Concat(doc.Footers))
            foreach (var p in v.Paragraphs) Fit(p, doc.FontKinds);
    }

    private static IEnumerable<DocParagraph> ParagraphsOf(DocItem item) => item switch
    {
        DocParagraph p => new[] { p },
        DocTable t => t.Cells.SelectMany(c => c.AllParagraphs),
        _ => Array.Empty<DocParagraph>(),
    };

    /// <summary>Condenses the paragraph's runs so each of the page's lines fits the width Word gives it.</summary>
    internal static void Fit(DocParagraph p, Dictionary<string, (bool Serif, bool Fixed)> kinds)
    {
        if (p.Image != null || p.Vertical || p.Tabs.Count > 0 || p.ColumnWidth <= 0 || p.Lines.Count == 0) return;
        string text = p.Text;
        if (p.CharLine.Count != text.Length || p.Runs.Any(r => r.Field != null)) return;
        int lines = p.CharLine.Count == 0 ? 0 : p.CharLine.Max() + 1;
        if (lines == 0) return;
        var width = new double[lines];
        var count = new int[lines];
        var trailing = new double[lines];
        var trailingCount = new int[lines];
        int k = 0;
        foreach (var r in p.Runs)
        {
            foreach (char c in r.Text)
            {
                int li = p.CharLine[k++];
                if (WordMetrics.Width(r, c.ToString(), kinds) is not double w) return;
                w += r.Spacing;
                // Spaces at a line's end do not count where Word breaks it.
                if (c == ' ') { trailing[li] += w; trailingCount[li]++; continue; }
                width[li] += trailing[li] + w;
                count[li] += trailingCount[li] + 1;
                trailing[li] = 0;
                trailingCount[li] = 0;
            }
        }
        double size = p.Size, worst = 0, scale = 1;
        for (int li = 0; li < lines; li++)
        {
            if (count[li] == 0) continue;
            double start = li == 0 && p.List == null ? p.Indent + p.FirstLine : p.Indent;
            double room = p.ColumnWidth - start - p.RightIndent;
            if (room <= size) { worst = double.MaxValue / 4; scale = 0.5; continue; }
            double target = room * 0.99 - 0.3;
            if (width[li] <= target) continue;
            worst = Math.Max(worst, (width[li] - target) / count[li]);
            scale = Math.Min(scale, target / width[li]);
        }
        if (worst <= 0) return;
        // Lines that start at very different places (text set in columns inside a cell, a line
        // indented far under the first): the room the indents leave is too little for some of
        // them. They keep the page's line breaks and start from the leftmost line.
        if (worst > MaxCondense * size && p.Lines.Count >= 2 && !p.HardLines && p.List == null && p.Align is PdfTextAlignment.Left or PdfTextAlignment.Justified)
        {
            double rest = p.Lines.Skip(1).Min(l => l.Box.L), left = rest - p.Indent, min = p.Lines.Min(l => l.Box.L);
            p.Indent = Math.Max(0, min - left);
            p.FirstLine = p.Lines[0].Box.L - Math.Max(min, left);
            p.RightIndent = Math.Min(p.RightIndent, 0);
            if (p.Align == PdfTextAlignment.Justified) p.Align = PdfTextAlignment.Left;
            p.HardLines = true;
            Fit(p, kinds);
            return;
        }
        if (worst <= MaxCondense * size)
        {
            // In twentieths of a point, rounded up so the line does fit.
            double spacing = Math.Ceiling(worst * 20) / 20;
            foreach (var r in p.Runs) r.Spacing -= spacing;
        }
        else
        {
            int percent = (int)Math.Clamp(Math.Floor(scale * 100), 50, 99);
            foreach (var r in p.Runs) r.Scale = Math.Max(1, r.Scale * percent / 100);
        }
    }

    /// <summary>How much taller Word makes a table than the page's: rows whose cells' text needs more than the row had.</summary>
    internal static double ExtraHeight(DocItem item)
    {
        if (item is not DocTable t) return 0;
        double extra = 0;
        for (int r = 0; r + 1 < t.Rows.Length; r++)
        {
            double row = t.Rows[r + 1] - t.Rows[r];
            double need = t.Cells.Where(c => c.Row == r && c.RowSpan == 1).Select(CellHeight).DefaultIfEmpty(0).Max();
            extra += Math.Max(0, need - row);
        }
        return extra;
    }

    private static double CellHeight(DocCell c)
    {
        if (c.Items != null) return c.Items.Sum(i => i is DocTable t ? t.Rows[^1] - t.Rows[0] + ExtraHeight(t) + i.SpaceBefore : ItemHeight(i));
        // Word makes room for the cell's top and bottom borders too.
        return c.Paragraphs.Sum(ItemHeight) + (c.Top?.Width ?? 0) + (c.Bottom?.Width ?? 0);
    }

    private static double ItemHeight(DocItem i) => i is DocParagraph p
        ? p.SpaceBefore + (p.Image != null ? p.Box.H : Math.Max(1, p.Lines.Count) * p.LineHeight)
        : i.SpaceBefore + i.Box.H;

    /// <summary>
    /// Cells whose text, laid out with the page's line heights, is taller than their row (rows set
    /// tighter than the font's line height): the space above goes first, then the line height,
    /// down to four fifths of the font size (as tight as the page sets them).
    /// </summary>
    private static void TightenCells(DocTable t)
    {
        foreach (var c in t.Cells.Where(c => c.RowSpan == 1 && c.Items == null && c.Paragraphs.Count > 0))
        {
            double over = CellHeight(c) - c.Box.H;
            if (over <= 0.05) continue;
            foreach (var p in c.Paragraphs)
            {
                double take = Math.Min(over, p.SpaceBefore);
                p.SpaceBefore -= take;
                over -= take;
            }
            if (over <= 0.05) continue;
            double lineSpace = c.Paragraphs.Sum(p => Math.Max(1, p.Lines.Count) * p.LineHeight);
            if (lineSpace <= 0) continue;
            double factor = Math.Max(0, (lineSpace - over) / lineSpace);
            foreach (var p in c.Paragraphs.Where(p => p.Image == null)) p.LineHeight = Math.Max(0.8 * p.Size, p.LineHeight * factor);
        }
    }
}
