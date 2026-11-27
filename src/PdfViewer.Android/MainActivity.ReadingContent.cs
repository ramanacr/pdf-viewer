using System.Text;
using System.Text.RegularExpressions;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;
using PdfViewer.Web.Engine;

namespace PdfViewer.Android;

/// <summary>
/// What read mode shows of a page: blocks in reading order with their role. From the document's
/// tags when it has them (the desktop's way, src/PdfViewer/Services/NativePageText.cs: the page's
/// structure tree walked in order, each block element collecting the text of the marked content it
/// owns, or its /ActualText; figures carry /Alt), otherwise inferred from the text layout as the
/// desktop does (src/PdfViewer/Text/PageAccessibleContent.cs): lines grouped into paragraphs by
/// their spacing, headings by their larger text, list items by their marker.
/// </summary>
public sealed partial class MainActivity
{
    private enum ReadRole { Heading, Paragraph, ListItem, Figure, Caption, Quote, Code, TableHeader, TableCell, Note, Unreadable }

    /// <summary>A block of read mode; <see cref="Level"/> is the heading level (1 to 6). A figure's text is its alternate text.</summary>
    private sealed record ReadBlock(ReadRole Role, string Text, int Level = 0);

    private static partial class ReadContent
    {
        /// <summary>The page's blocks: from its tags, or from its text layout when it has none.</summary>
        public static List<ReadBlock> Page(PdfVectorDocument doc, int page, CancellationToken token)
        {
            var text = PdfTextExtractor.LoadPage(doc, page, token);
            var tagged = Tagged(doc, page, text);
            return tagged is { Count: > 0 } ? tagged : FromLayout(text);
        }

        // ------------------------------------------------------------------ tagged pages

        private static (ReadRole Role, int Level)? RoleFor(string type) => type switch
        {
            "H1" or "Title" => (ReadRole.Heading, 1),
            "H" or "H2" => (ReadRole.Heading, 2),
            "H3" => (ReadRole.Heading, 3),
            "H4" => (ReadRole.Heading, 4),
            "H5" => (ReadRole.Heading, 5),
            "H6" => (ReadRole.Heading, 6),
            "P" => (ReadRole.Paragraph, 0),
            "LI" or "TOCI" => (ReadRole.ListItem, 0),
            "Figure" or "Formula" => (ReadRole.Figure, 0),
            "Caption" => (ReadRole.Caption, 0),
            "BlockQuote" or "Quote" => (ReadRole.Quote, 0),
            "Code" => (ReadRole.Code, 0),
            "TH" => (ReadRole.TableHeader, 0),
            "TD" => (ReadRole.TableCell, 0),
            "Note" or "FENote" => (ReadRole.Note, 0),
            _ => null,
        };

        private static List<ReadBlock>? Tagged(PdfVectorDocument doc, int page, PdfTextPage text)
        {
            var roots = PdfStructureTree.ForPage(doc, page);
            if (roots == null || roots.Count == 0) return null;
            // Marked-content ID: its text and where it starts in content order.
            var byMcid = new Dictionary<int, (StringBuilder Text, int First)>();
            int lastMcid = -1;
            for (int i = 0; i < text.CharCount; i++)
            {
                char cp = text.GetUnicode(i);
                if (cp == 0) continue;
                int mcid = text.HasTextObject(i) ? text.GetMarkedContentId(i) : lastMcid;
                if (mcid < 0) continue; // artifacts and untagged content
                lastMcid = mcid;
                if (!byMcid.TryGetValue(mcid, out var entry))
                    byMcid[mcid] = entry = (new StringBuilder(), i);
                entry.Text.Append(cp is '\r' or '\n' ? ' ' : cp);
            }
            var blocks = new List<ReadBlock>();
            foreach (var root in roots) Walk(root, byMcid, blocks, 0);
            return blocks;
        }

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static void Walk(PdfStructElement? element, Dictionary<int, (StringBuilder Text, int First)> byMcid, List<ReadBlock> blocks, int depth)
        {
            if (element == null || depth > 64) return;
            if (RoleFor(element.Type) is { } role)
            {
                string? actual = Clean(element.ActualText);
                if (role.Role == ReadRole.Figure)
                {
                    blocks.Add(new ReadBlock(ReadRole.Figure, Clean(element.Alt) ?? actual ?? ""));
                    return;
                }
                string content = Reflow(actual ?? CollectText(element, byMcid, depth));
                if (content.Length > 0) blocks.Add(new ReadBlock(role.Role, content, role.Level));
                return;
            }
            // A grouping (Document, Sect, Div, L, Table, TR ...): its own marked content reads as a paragraph, then its children in order.
            string own = Reflow(string.Concat(OwnMcids(element).Where(byMcid.ContainsKey).OrderBy(m => byMcid[m].First).Select(m => byMcid[m].Text.ToString())));
            if (own.Length > 0) blocks.Add(new ReadBlock(ReadRole.Paragraph, own));
            foreach (var child in element.Children) Walk(child, byMcid, blocks, depth + 1);
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

        // ------------------------------------------------------------------ untagged pages

        /// <summary>Blocks inferred from the page's lines: a paragraph ends at a wider gap, a change of size or direction, or a jump back up (a new column).</summary>
        private static List<ReadBlock> FromLayout(PdfTextPage page)
        {
            var layout = ViewerTextLayout.FromPage(page);
            var lines = layout.Lines;
            var blocks = new List<ReadBlock>();
            if (lines.Count == 0) return blocks;

            var paragraphs = new List<(int First, int Last)>();
            int first = 0;
            for (int i = 1; i < lines.Count; i++)
            {
                var a = lines[i - 1];
                var b = lines[i];
                double ha = a.VMax - a.VMin, hb = b.VMax - b.VMin, h = Math.Max(ha, hb);
                bool turned = Math.Abs(a.Ux - b.Ux) > 0.1 || Math.Abs(a.Uy - b.Uy) > 0.1;
                double gap = b.VMin - a.VMax;
                bool resized = h > 0 && Math.Min(ha, hb) / h < 0.8;
                if (turned || resized || gap > 0.6 * h || gap < -0.5 * h)
                {
                    paragraphs.Add((first, i - 1));
                    first = i;
                }
            }
            paragraphs.Add((first, lines.Count - 1));

            double Height(int f, int l)
            {
                double sum = 0;
                for (int i = f; i <= l; i++) sum += lines[i].VMax - lines[i].VMin;
                return sum / (l - f + 1);
            }

            // Body text size: the line height most of the page's characters are set in.
            double body = lines.GroupBy(l => Math.Round(l.VMax - l.VMin, 0))
                .OrderByDescending(g => g.Sum(l => l.End - l.Start)).First().Key;
            if (body <= 0) body = 1;
            var headingSizes = paragraphs.Select(p => Height(p.First, p.Last)).Where(t => t >= body * 1.25)
                .Select(t => Math.Round(t / body, 1)).Distinct().OrderByDescending(t => t).ToList();

            foreach (var (f, l) in paragraphs)
            {
                string text = Reflow(layout.GetText(lines[f].Start, lines[l].End));
                if (text.Length == 0) continue;
                double t = Height(f, l);
                if (t >= body * 1.25 && l - f < 3 && text.Length <= 200)
                    blocks.Add(new ReadBlock(ReadRole.Heading, text, Math.Clamp(headingSizes.IndexOf(Math.Round(t / body, 1)) + 1, 1, 6)));
                else if (StartsWithListMarker(text))
                    blocks.Add(new ReadBlock(ReadRole.ListItem, text));
                else
                    blocks.Add(new ReadBlock(ReadRole.Paragraph, text));
            }
            return blocks;
        }

        /// <summary>Lines joined into flowing text: line breaks and runs of white space become single spaces.</summary>
        public static string Reflow(string text) => WhiteSpace().Replace(text, " ").Trim();

        /// <summary>A bullet ("•", "–", "*") or a number or letter with "." or ")" and a space.</summary>
        public static bool StartsWithListMarker(string text) => ListMarker().IsMatch(text);

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhiteSpace();

        [GeneratedRegex(@"^([•◦▪▫‣●○■□\-–—*·]|\(?(\d{1,3}|[a-zA-Z]|[ivxlcIVXLC]{1,6})[.)])\s")]
        private static partial Regex ListMarker();
    }
}
