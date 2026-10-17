using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// The viewer's text features (search, word segments, word-processor selection, tagged reading
/// order) from the vector engine's text page, with exactly the conventions the PDFium document
/// service uses: boxes normalized to the unrotated crop box (top-left origin), the same padding,
/// word grouping and ordering. No PDFium call is made.
/// </summary>
internal static class NativePageText
{
    private static (double Width, double Height, double OriginX, double OriginY) Frame(PdfTextPage page)
    {
        var box = page.PageBox;
        double w = box.Width > 0 ? box.Width : 612, h = box.Height > 0 ? box.Height : 792;
        return (w, h, box.X, box.Y);
    }

    /// <summary>Every hit of <paramref name="query"/> in the document, one highlight box per hit.</summary>
    public static List<SearchMatch> Search(PdfVectorDocument doc, string query, bool matchCase, CancellationToken ct)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        int matchIndex = 1;
        for (int p = 1; p <= doc.PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = PdfTextExtractor.LoadPage(doc, p, ct);
            var (pageWidth, pageHeight, originX, originY) = Frame(page);
            foreach (var (start, count) in page.Find(query, matchCase))
            {
                ct.ThrowIfCancellationRequested();
                if (count <= 0) continue;
                double minLeft = double.MaxValue, maxTop = double.MinValue, maxRight = double.MinValue, minBottom = double.MaxValue;
                for (int i = start; i < start + count && i < page.CharCount; i++)
                {
                    var b = page.GetCharBox(i);
                    double l = Math.Min(b.Left, b.Right), r = Math.Max(b.Left, b.Right), t = Math.Max(b.Top, b.Bottom), bt = Math.Min(b.Top, b.Bottom);
                    if (r > l && t > bt)
                    {
                        minLeft = Math.Min(minLeft, l); maxTop = Math.Max(maxTop, t);
                        maxRight = Math.Max(maxRight, r); minBottom = Math.Min(minBottom, bt);
                    }
                }
                if (minLeft == double.MaxValue || maxTop == double.MinValue) continue;

                // Optical padding: shift the top up by 1.5 pt and grow the height by 2.5 pt so the
                // highlight covers ascenders and descenders without dipping into the line below.
                const double padY = 1.5, padH = 2.5, padX = 1.0;
                double normX = Math.Max(0, (minLeft - originX - padX) / pageWidth);
                double normY = Math.Max(0, 1.0 - ((maxTop - originY + padY) / pageHeight));
                double normW = Math.Max(0.005, ((maxRight - minLeft) + (padX * 2)) / pageWidth);
                double normH = Math.Max(0.005, ((maxTop - minBottom) + padH) / pageHeight);
                string matchText = page.GetText(start, count);
                if (matchText.Length == 0) matchText = query;
                results.Add(new SearchMatch
                {
                    MatchIndex = matchIndex++, PageNumber = p, Text = matchText, ContextSnippet = matchText,
                    X = normX, Y = normY, Width = normW, Height = normH,
                });
            }
        }
        return results;
    }

    /// <summary>The page's words with their boxes, in reading order (top to bottom, left to right).</summary>
    public static List<PageTextSegment> Segments(PdfVectorDocument doc, int pageNumber, CancellationToken ct = default)
    {
        var list = new List<PageTextSegment>();
        if (pageNumber < 1 || pageNumber > doc.PageCount) return list;
        var page = PdfTextExtractor.LoadPage(doc, pageNumber, ct);
        var (pageWidth, pageHeight, originX, originY) = Frame(page);
        var word = new StringBuilder();
        double left = double.MaxValue, top = double.MinValue, right = double.MinValue, bottom = double.MaxValue;
        bool inWord = false;

        void Flush()
        {
            if (inWord && word.Length > 0 && left != double.MaxValue)
                Add(list, pageNumber, word.ToString(), left - originX, top - originY, right - originX, bottom - originY, pageWidth, pageHeight);
            word.Clear();
            left = double.MaxValue; top = double.MinValue; right = double.MinValue; bottom = double.MaxValue;
            inWord = false;
        }

        for (int i = 0; i < page.CharCount; i++)
        {
            char ch = page.GetUnicode(i);
            if (char.IsWhiteSpace(ch) || ch == 0 || ch == '\r' || ch == '\n')
            {
                Flush();
                continue;
            }
            var b = page.GetCharBox(i);
            word.Append(ch);
            left = Math.Min(left, b.Left); top = Math.Max(top, b.Top);
            right = Math.Max(right, b.Right); bottom = Math.Min(bottom, b.Bottom);
            inWord = true;
        }
        Flush();

        list.Sort((a, b) =>
        {
            double yDiff = a.Y - b.Y;
            double threshold = Math.Min(a.Height, b.Height) * 0.5;
            if (threshold <= 0) threshold = 0.008;
            return Math.Abs(yDiff) > threshold ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
        });
        for (int i = 0; i < list.Count; i++) list[i].SegmentIndex = i;
        return list;
    }

    private static void Add(List<PageTextSegment> list, int pageNum, string text, double left, double top, double right, double bottom, double pageWidth, double pageHeight)
    {
        const double padY = 1.5, padH = 2.5, padX = 0.5;
        list.Add(new PageTextSegment
        {
            PageNumber = pageNum,
            Text = text,
            X = Math.Max(0, (left - padX) / pageWidth),
            Y = Math.Max(0, 1.0 - ((top + padY) / pageHeight)),
            Width = Math.Max(0.001, ((right - left) + (padX * 2)) / pageWidth),
            Height = Math.Max(0.001, ((top - bottom) + padH) / pageHeight),
        });
    }

    /// <summary>
    /// The page's characters in reading order with their loose boxes (font ascent to descent, so
    /// a line's characters share one height), the inferred spaces and line breaks, and line-end
    /// hyphens: the input of word-processor selection.
    /// </summary>
    public static PdfViewer.Text.PageTextLayout Layout(PdfVectorDocument doc, int pageNumber, CancellationToken ct = default)
    {
        if (pageNumber < 1 || pageNumber > doc.PageCount) return PdfViewer.Text.PageTextLayout.Empty;
        var page = PdfTextExtractor.LoadPage(doc, pageNumber, ct);
        var (pageWidth, pageHeight, originX, originY) = Frame(page);
        var glyphs = new List<PdfViewer.Text.TextGlyph>();
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
                continue; // unpaired surrogate
            }
            else
            {
                text = cp.ToString();
            }

            var box = Rect.Empty;
            if (text is not ("\r" or "\n"))
            {
                var r = page.GetLooseCharBox(index);
                double l = r.Left, rt = r.Right, b = r.Bottom, t = r.Top;
                if (rt > l && t > b)
                    box = new Rect((l - originX) / pageWidth, 1.0 - (t - originY) / pageHeight, (rt - l) / pageWidth, (t - b) / pageHeight);
                else
                {
                    var c = page.GetCharBox(index);
                    if (c.Right > c.Left && c.Top > c.Bottom)
                        box = new Rect((c.Left - originX) / pageWidth, 1.0 - (c.Top - originY) / pageHeight, (c.Right - c.Left) / pageWidth, (c.Top - c.Bottom) / pageHeight);
                }
            }
            glyphs.Add(new PdfViewer.Text.TextGlyph(text, box, page.IsGenerated(index), page.IsHyphen(index)));
        }
        return new PdfViewer.Text.PageTextLayout(glyphs);
    }

    /// <summary>
    /// Tagged content (ISO 32000-2 14.8): the page's structure tree walked in order; each block
    /// element collects the text of the marked content it owns, in content order, or its
    /// /ActualText; figures carry /Alt. Null for untagged pages (the caller then uses the layout).
    /// </summary>
    public static PdfViewer.Text.PageAccessibleContent? Tagged(PdfVectorDocument doc, int pageNumber, CancellationToken ct = default)
    {
        if (pageNumber < 1 || pageNumber > doc.PageCount) return null;
        var roots = PdfStructureTree.ForPage(doc, pageNumber);
        if (roots == null || roots.Count == 0) return null;

        // Marked-content ID -> its text and where it starts in content order.
        var byMcid = new Dictionary<int, (StringBuilder Text, int First)>();
        var page = PdfTextExtractor.LoadPage(doc, pageNumber, ct);
        int lastMcid = -1;
        for (int i = 0; i < page.CharCount; i++)
        {
            char cp = page.GetUnicode(i);
            if (cp == 0) continue;
            int mcid = page.HasTextObject(i) ? page.GetMarkedContentId(i) : lastMcid;
            if (mcid < 0) continue; // artifacts and untagged content
            lastMcid = mcid;
            if (!byMcid.TryGetValue(mcid, out var entry))
                byMcid[mcid] = entry = (new StringBuilder(), i);
            entry.Text.Append(cp is '\r' or '\n' ? ' ' : cp);
        }

        var blocks = new List<PdfViewer.Text.ContentBlock>();
        foreach (var root in roots)
            Walk(root, byMcid, blocks, depth: 0);
        return new PdfViewer.Text.PageAccessibleContent(pageNumber, fromTags: true, blocks);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void Walk(PdfStructElement? element, Dictionary<int, (StringBuilder Text, int First)> byMcid, List<PdfViewer.Text.ContentBlock> blocks, int depth)
    {
        if (element == null || depth > 64) return;
        if (PdfViewer.Text.PageAccessibleContent.RoleForStructureType(element.Type) is { } role)
        {
            string? lang = Clean(element.Lang);
            string? actual = Clean(element.ActualText);
            if (role == PdfViewer.Text.ContentRole.Figure)
            {
                blocks.Add(new PdfViewer.Text.ContentBlock(role, actual ?? string.Empty, Clean(element.Alt), lang));
                return;
            }
            string text = PdfViewer.Text.PageAccessibleContent.Reflow(actual ?? CollectText(element, byMcid, depth));
            if (text.Length > 0)
                blocks.Add(new PdfViewer.Text.ContentBlock(role, text, null, lang));
            return;
        }
        // A grouping (Document, Sect, Div, L, Table, TR, Span at the top ...): its own marked
        // content reads as a paragraph, its children in order.
        string own = string.Concat(OwnMcids(element).Where(byMcid.ContainsKey).OrderBy(m => byMcid[m].First).Select(m => byMcid[m].Text.ToString()));
        own = PdfViewer.Text.PageAccessibleContent.Reflow(own);
        if (own.Length > 0)
            blocks.Add(new PdfViewer.Text.ContentBlock(PdfViewer.Text.ContentRole.Paragraph, own));
        foreach (var child in element.Children)
            Walk(child, byMcid, blocks, depth + 1);
    }

    /// <summary>The text of every marked content in an element's subtree, in content order (inline /ActualText honoured).</summary>
    private static string CollectText(PdfStructElement element, Dictionary<int, (StringBuilder Text, int First)> byMcid, int depth)
    {
        var parts = new List<(int First, string Text)>();
        void Visit(PdfStructElement? e, int d)
        {
            if (e == null || d > 64) return;
            if (d > depth && Clean(e.ActualText) is { } actual)
            {
                int first = SubtreeMcids(e, d).Where(byMcid.ContainsKey).Select(m => byMcid[m].First).DefaultIfEmpty(int.MaxValue).Min();
                parts.Add((first, actual));
                return;
            }
            foreach (int m in OwnMcids(e))
                if (byMcid.TryGetValue(m, out var t)) parts.Add((t.First, t.Text.ToString()));
            foreach (var child in e.Children) Visit(child, d + 1);
        }
        Visit(element, depth);
        return string.Concat(parts.OrderBy(p => p.First).Select(p => p.Text));
    }

    private static IEnumerable<int> OwnMcids(PdfStructElement element) => element.MarkedContentIds.Where(m => m >= 0);

    private static IEnumerable<int> SubtreeMcids(PdfStructElement? element, int depth)
    {
        if (element == null || depth > 64) yield break;
        foreach (int m in OwnMcids(element)) yield return m;
        foreach (var child in element.Children)
            foreach (int m in SubtreeMcids(child, depth + 1))
                yield return m;
    }
}
