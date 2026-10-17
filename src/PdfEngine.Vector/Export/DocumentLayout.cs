using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>
/// Turns the pages' content into a Word document's structure: headers and footers from what
/// repeats, headings, lists, and sections with the columns, margins and spacing of the pages.
/// </summary>
internal static partial class DocumentLayout
{
    public static DocDocument Build(List<DocPage> pages, List<string> hyperlinks, string? title, Dictionary<string, (bool Serif, bool Fixed)>? fontKinds = null)
    {
        var doc = new DocDocument { Hyperlinks = hyperlinks, Title = title };
        if (fontKinds != null) doc.FontKinds = fontKinds;
        HeadersAndFooters(pages, doc);
        BodyStyle(pages, doc);
        foreach (var p in pages.SelectMany(pg => pg.Paragraphs)) p.List = Marker(p);
        Headings(pages, doc);
        Sections(pages, doc);
        GroupLists(doc);
        FitWidths(doc);
        return doc;
    }

    private static IEnumerable<DocParagraph> AllParagraphs(IEnumerable<DocPage> pages) =>
        pages.SelectMany(pg => pg.Items.SelectMany(i => i is DocTable t ? t.Cells.SelectMany(c => c.AllParagraphs) : i is DocParagraph p ? new[] { p } : Array.Empty<DocParagraph>()));

    // ------------------------------------------------------------------ headers and footers

    private static string Normalize(string text)
    {
        // A page number in roman numerals on its own (front matter).
        if (RomanFormat(text) is { } format) return "#" + format;
        var sb = new StringBuilder(text.Length);
        bool digits = false, space = false;
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsDigit(c)) { if (!digits) sb.Append('#'); digits = true; space = false; continue; }
            digits = false;
            if (char.IsWhiteSpace(c)) { if (!space) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>lowerRoman or upperRoman when the text is a roman numeral and nothing else.</summary>
    private static string? RomanFormat(string text)
    {
        string t = text.Trim();
        if (t.Length is 0 or > 8 || Roman(t) <= 0) return null;
        return t.All(char.IsLower) ? "lowerRoman" : t.All(char.IsUpper) ? "upperRoman" : null;
    }

    /// <summary>Paragraphs at one place on several pages, and what their numbers are.</summary>
    private sealed class Repeat
    {
        public bool Top;
        public List<(DocPage Page, DocParagraph Para)> Where = new();
        /// <summary>Number tokens that are the page number or the page count (-1: the whole text, a roman numeral).</summary>
        public List<(int Token, string Field)> Fields = new();
        public Dictionary<DocPage, int> Values = new(ReferenceEqualityComparer.Instance);
        /// <summary>The numbers that are neither, as each page has them (the same over runs of pages).</summary>
        public Dictionary<DocPage, string> Literal = new(ReferenceEqualityComparer.Instance);
        public string Format = "decimal";
    }

    /// <summary>
    /// Text at the same place near the top or bottom of many pages (the same words, numbers
    /// aside) is a header or footer: it is taken off the pages. Pages can have different ones
    /// (odd and even pages, front matter, parts of a report): each combination is written once,
    /// and the sections of the pages that have it use it. Page numbers become a PAGE field, and
    /// where the printed numbers start again (A-1, B-1, roman then arabic) a section does too.
    /// </summary>
    private static void HeadersAndFooters(List<DocPage> pages, DocDocument doc)
    {
        int n = pages.Count;
        if (n < 2) return;
        int need = n <= 4 ? Math.Max(2, (int)Math.Ceiling(0.5 * n)) : Math.Max(3, (int)Math.Ceiling(0.2 * n));
        var byKey = new Dictionary<string, List<(DocPage Page, DocParagraph Para)>>();
        foreach (var page in pages)
            foreach (var p in page.Paragraphs.Where(p => p.Image == null && !p.Rotated && !p.Vertical && p.Lines.Count <= 2 && p.Text.Trim().Length > 0))
            {
                string? zone = p.Box.T < 0.09 * page.Height && p.Box.B < 0.12 * page.Height ? "top"
                    : p.Box.B > 0.91 * page.Height && p.Box.T > 0.88 * page.Height ? "bottom" : null;
                if (zone == null) continue;
                string key = zone + "|" + Normalize(p.Text);
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<(DocPage, DocParagraph)>();
                list.Add((page, p));
            }
        var found = new List<Repeat>();
        foreach (var (key, all) in byKey)
        {
            double y = all.Select(o => o.Para.Box.T).OrderBy(v => v).ElementAt(all.Count / 2);
            var at = all.Where(o => Math.Abs(o.Para.Box.T - y) <= 8).GroupBy(o => o.Page).Select(g => g.First()).ToList();
            if (at.Count < need) continue;
            bool top = key.StartsWith("top", StringComparison.Ordinal);
            if (Analyze(at, top, n) is { } r) { found.Add(r); continue; }
            // Numbers that are neither the page number nor the same everywhere: only text that is the same repeats.
            foreach (var same in at.GroupBy(o => o.Para.Text.Trim(), StringComparer.Ordinal).Where(s => s.Count() >= need))
                if (Analyze(same.ToList(), top, n) is { } s) found.Add(s);
        }
        if (found.Count == 0) return;

        foreach (var r in found)
            foreach (var (page, para) in r.Where)
            {
                page.Items.Remove(para);
                (r.Top ? page.Header : page.Footer).Add(para);
                para.Repeated = true;
            }

        // Each page's combination of headers and of footers, written once each.
        var headers = new Dictionary<string, int>(StringComparer.Ordinal);
        var footers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            var mine = found.Select((r, i) => (R: r, Index: i)).Where(x => x.R.Where.Any(o => ReferenceEquals(o.Page, page))).ToList();
            page.HeaderVariant = VariantOf(page, mine.Where(x => x.R.Top).ToList(), headers, doc.Headers);
            page.FooterVariant = VariantOf(page, mine.Where(x => !x.R.Top).ToList(), footers, doc.Footers);
            foreach (var (r, _) in mine)
                if (r.Values.TryGetValue(page, out int value)) { page.PageValue = value; page.NumberFormat = r.Format; break; }
        }
    }

    private static int VariantOf(DocPage page, List<(Repeat R, int Index)> repeats, Dictionary<string, int> known, List<DocHeaderFooter> variants)
    {
        if (repeats.Count == 0) return -1;
        string key = string.Join(",", repeats.Select(x => x.Index + (x.R.Literal.TryGetValue(page, out var literal) ? ":" + literal : string.Empty)));
        if (known.TryGetValue(key, out int index)) return index;
        // This page's copies stand for every page with the same ones; their numbers become fields.
        var paragraphs = new List<DocParagraph>();
        foreach (var (r, _) in repeats)
        {
            var para = r.Where.First(o => ReferenceEquals(o.Page, page)).Para;
            var matches = Regex.Matches(para.Text, @"\d+");
            foreach (var (t, field) in r.Fields.OrderByDescending(f => f.Token))
            {
                if (t < 0)
                {
                    string text = para.Text;
                    int start = text.Length - text.TrimStart().Length;
                    ReplaceWithField(para, start, text.Trim().Length, field);
                }
                else if (t < matches.Count) ReplaceWithField(para, matches[t].Index, matches[t].Length, field);
            }
            paragraphs.Add(para);
        }
        index = variants.Count;
        variants.Add(new DocHeaderFooter { Paragraphs = Rows(paragraphs, page.Width), Page = page });
        known[key] = index;
        return index;
    }

    /// <summary>What the numbers of a repeated text are: the page number, the page count or the same everywhere; null when they are none of these.</summary>
    private static Repeat? Analyze(List<(DocPage Page, DocParagraph Para)> where, bool top, int pageCount)
    {
        var r = new Repeat { Top = top, Where = where };
        if (RomanFormat(where[0].Para.Text) is { } format)
        {
            var romans = where.Select(o => (long)Roman(o.Para.Text.Trim())).ToList();
            if (PageNumbers(where, romans, r)) { r.Fields.Add((-1, "PAGE")); r.Format = format; return r; }
            return where.Select(o => o.Para.Text.Trim()).Distinct(StringComparer.Ordinal).Count() == 1 ? r : null;
        }
        var values = where.Select(o => Regex.Matches(o.Para.Text, @"\d+").Select(m => m.Value).ToList()).ToList();
        int tokens = values.Min(v => v.Count);
        if (values.Any(v => v.Count != tokens)) return null;
        bool page = false;
        var other = new List<int>();
        for (int t = 0; t < tokens; t++)
        {
            var nums = values.Select(v => long.TryParse(v[t], NumberStyles.None, CultureInfo.InvariantCulture, out long x) && x < 100000 ? x : -1).ToList();
            bool same = values.Select(v => v[t]).Distinct(StringComparer.Ordinal).Count() == 1;
            if (!nums.Any(x => x < 0) && !page && where.Count >= 2 && !same && PageNumbers(where, nums, r))
            {
                r.Fields.Add((t, "PAGE"));
                page = true;
            }
            else if (same)
            {
                if (nums[0] == pageCount && pageCount > 1) r.Fields.Add((t, "NUMPAGES"));
            }
            else other.Add(t);
        }
        if (other.Count == 0) return r;
        // A number that stays the same over runs of pages beside the page number (the chapter in
        // "2-14"): each run gets a header or footer of its own, with its number as it is.
        if (!page) return null;
        var byPage = where.Select((o, i) => (o.Page, Key: string.Join("-", other.Select(t => values[i][t])))).OrderBy(x => x.Page.Number).ToList();
        int changes = byPage.Zip(byPage.Skip(1)).Count(p => p.First.Key != p.Second.Key);
        if (changes > Math.Max(4, byPage.Count / 3)) return null;
        foreach (var (p, k) in byPage) r.Literal[p] = k;
        return r;
    }

    /// <summary>
    /// Numbers that go up with the pages: the page's number plus an offset that is the same over
    /// runs of pages (a few runs at most, each of two pages or more: parts numbered on their own).
    /// </summary>
    private static bool PageNumbers(List<(DocPage Page, DocParagraph Para)> where, List<long> nums, Repeat r)
    {
        if (nums.Any(x => x <= 0)) return false;
        var order = where.Select((o, i) => (o.Page, Value: nums[i])).OrderBy(x => x.Page.Number).ToList();
        int runs = 0, length = 0;
        long diff = long.MinValue;
        for (int i = 0; i < order.Count; i++)
        {
            long d = order[i].Value - order[i].Page.Number;
            if (d != diff)
            {
                if (runs > 0 && length < 2) return false;
                runs++;
                length = 0;
                diff = d;
            }
            length++;
        }
        if (length < 2 && runs > 1 || runs > Math.Max(4, order.Count / 3)) return false;
        foreach (var (p, v) in order) r.Values[p] = (int)v;
        return true;
    }

    private static void ReplaceWithField(DocParagraph p, int start, int length, string field)
    {
        int pos = 0;
        for (int i = 0; i < p.Runs.Count; i++)
        {
            var r = p.Runs[i];
            int end = pos + r.Text.Length;
            if (start >= pos && start + length <= end)
            {
                int at = start - pos;
                var parts = new List<DocRun>();
                if (at > 0) parts.Add(r.Clone(r.Text[..at]));
                var f = r.Clone(r.Text.Substring(at, length));
                f.Field = field;
                parts.Add(f);
                if (at + length < r.Text.Length) parts.Add(r.Clone(r.Text[(at + length)..]));
                p.Runs.RemoveAt(i);
                p.Runs.InsertRange(i, parts);
                return;
            }
            pos = end;
        }
    }

    /// <summary>Header or footer paragraphs on one baseline become one paragraph, the later ones placed by tab stops.</summary>
    private static List<DocParagraph> Rows(List<DocParagraph> paragraphs, double width)
    {
        var result = new List<DocParagraph>();
        foreach (var row in paragraphs.OrderBy(p => p.FirstBaseline).GroupBy(p => Math.Round(p.FirstBaseline / 3)))
        {
            var parts = row.OrderBy(p => p.Box.L).ToList();
            var first = parts[0];
            foreach (var next in parts.Skip(1)) AppendWithTab(first, next, width);
            result.Add(first);
        }
        return result;
    }

    /// <summary>Puts <paramref name="next"/> after <paramref name="first"/> on its line, at a tab stop where it is on the page.</summary>
    internal static void AppendWithTab(DocParagraph first, DocParagraph next, double columnRight, double columnLeft = 0)
    {
        double centre = (columnLeft + columnRight) / 2;
        var kind = Math.Abs(next.Box.Cx - centre) < 0.06 * (columnRight - columnLeft) ? TabKind.Center
            : next.Box.R > columnRight - 0.12 * (columnRight - columnLeft) && next.Box.L > centre ? TabKind.Right : TabKind.Left;
        double x = kind switch { TabKind.Center => next.Box.Cx, TabKind.Right => next.Box.R, _ => next.Box.L };
        var tab = (first.Runs.LastOrDefault() ?? next.Runs[0]).Clone("\t");
        tab.Field = null;
        tab.Link = -1;
        first.Runs.Add(tab);
        first.CharX.Add(double.NaN);
        first.CharLine.Add(0);
        first.Runs.AddRange(next.Runs);
        first.CharX.AddRange(next.CharX);
        first.CharLine.AddRange(next.CharLine.Select(_ => 0));
        first.Tabs.Add((x, kind));
        first.Box = first.Box.Union(next.Box);
        if (first.Lines.Count > 0 && next.Lines.Count > 0) first.Lines[0].Box = first.Lines[0].Box.Union(next.Lines[0].Box);
        first.Align = PdfTextAlignment.Left;
        first.AlignKnown = true;
    }

    /// <summary>Puts <paramref name="next"/> after <paramref name="first"/> on its line, a space between (they overlap on the page).</summary>
    internal static void AppendWithSpace(DocParagraph first, DocParagraph next)
    {
        var space = (first.Runs.LastOrDefault() ?? next.Runs[0]).Clone(" ");
        space.Field = null;
        space.Link = -1;
        first.Runs.Add(space);
        first.CharX.Add(double.NaN);
        first.CharLine.Add(0);
        first.Runs.AddRange(next.Runs);
        first.CharX.AddRange(next.CharX);
        first.CharLine.AddRange(next.CharLine.Select(_ => 0));
        foreach (var t in next.Tabs) first.Tabs.Add(t);
        first.Box = first.Box.Union(next.Box);
        if (first.Lines.Count > 0 && next.Lines.Count > 0) first.Lines[0].Box = first.Lines[0].Box.Union(next.Lines[0].Box);
    }

    /// <summary>Puts a one-line <paramref name="mark"/> at the start of <paramref name="p"/>'s first line, a space after it.</summary>
    internal static void Prepend(DocParagraph p, DocParagraph mark)
    {
        var runs = mark.Runs.Select(r => r.Clone(r.Text)).ToList();
        var space = runs[^1].Clone(" ");
        space.Field = null;
        space.Link = -1;
        space.VertAlign = 0;
        runs.Add(space);
        p.Runs.InsertRange(0, runs);
        p.CharX.InsertRange(0, mark.CharX.Append(double.NaN));
        p.CharLine.InsertRange(0, Enumerable.Repeat(0, mark.CharX.Count + 1));
        p.Box = p.Box.Union(mark.Box);
        p.Lines[0].Box = p.Lines[0].Box.Union(mark.Box);
    }

    // ------------------------------------------------------------------ style

    private static void BodyStyle(List<DocPage> pages, DocDocument doc)
    {
        var sizes = new Dictionary<double, int>();
        var fonts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in AllParagraphs(pages).SelectMany(p => p.Runs))
        {
            int n = r.Text.Count(c => !char.IsWhiteSpace(c));
            sizes[r.Size] = sizes.GetValueOrDefault(r.Size) + n;
            fonts[r.Font] = fonts.GetValueOrDefault(r.Font) + n;
        }
        if (sizes.Count > 0) doc.BodySize = sizes.OrderByDescending(kv => kv.Value).First().Key;
        if (fonts.Count > 0) doc.BodyFont = fonts.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>
    /// Headings: from the tags when the document has them; otherwise short lines clearly larger
    /// than the body text and bold (or very large), levels by size.
    /// </summary>
    private static void Headings(List<DocPage> pages, DocDocument doc)
    {
        var flow = pages.SelectMany(p => p.Paragraphs).Where(p => p.Image == null).ToList();
        if (flow.Any(p => p.TagHeading > 0))
        {
            foreach (var p in flow) p.Heading = Math.Min(p.TagHeading, 3);
            return;
        }
        var candidates = new List<(DocParagraph P, double Size)>();
        foreach (var p in flow)
        {
            string text = p.Text.Trim();
            if (p.Lines.Count > 3 || text.Length < 2 || text.Length > 200 || !text.Any(char.IsLetter) || p.Rotated || p.Vertical) continue;
            if (p.List is { Format: "bullet" }) continue;
            if (text.EndsWith('.') && text.Length > 60) continue;
            var main = p.Runs.GroupBy(r => (r.Size, r.Bold)).OrderByDescending(g => g.Sum(r => r.Text.Length)).First().Key;
            if ((main.Bold && main.Size >= 1.15 * doc.BodySize) || main.Size >= 1.6 * doc.BodySize) candidates.Add((p, main.Size));
        }
        var levels = candidates.Select(c => Math.Round(c.Size)).Distinct().OrderByDescending(s => s).ToList();
        foreach (var (p, size) in candidates)
        {
            p.Heading = Math.Min(levels.IndexOf(Math.Round(size)) + 1, 3);
            p.List = null;
        }
    }

    // ------------------------------------------------------------------ lists

    private const string Bullets = "•◦▪▫■□●○◆◇►▶‣⁃∙·✓✔➢➤→❖";
    private const string WeakBullets = "-–—*";
    private static readonly Regex Numbered = new(@"^(\()?(\d{1,3}|[A-Za-z]|[ivxlcdm]{2,7}|[IVXLCDM]{2,7})([.)])[ \t]+(?=\S)", RegexOptions.CultureInvariant);

    /// <summary>Whether a line of text begins the way a list item does (a bullet or a number, then a space).</summary>
    internal static bool StartsListItem(string line)
    {
        string t = line.TrimStart();
        if (t.Length < 3) return false;
        if (Bullets.Contains(t[0]) || WeakBullets.Contains(t[0])) return t[1] == ' ' || t[1] == '\t';
        return Numbered.IsMatch(t);
    }

    /// <summary>A leading bullet or number that makes the paragraph look like a list item.</summary>
    private static ListMark? Marker(DocParagraph p)
    {
        if (p.Image != null || p.Rtl || p.Vertical || p.Rotated || p.Runs.Count == 0) return null;
        string t = p.Text;
        int i = 0;
        while (i < t.Length && t[i] == ' ') i++;
        if (i >= t.Length - 1) return null;
        char c = t[i];
        if (Bullets.Contains(c) || WeakBullets.Contains(c))
        {
            int j = i + 1;
            if (t[j] != ' ' && t[j] != '\t') return null;
            while (j < t.Length && (t[j] == ' ' || t[j] == '\t')) j++;
            if (j >= t.Length) return null;
            return new ListMark { Format = "bullet", Text = c.ToString(), Weak = WeakBullets.Contains(c), Length = j, MarkerX = X(p, i), TextX = X(p, j), Look = RunAt(p, i), Tagged = p.TagListItem };
        }
        var m = Numbered.Match(t[i..]);
        if (!m.Success) return null;
        bool open = m.Groups[1].Success;
        string close = m.Groups[3].Value;
        if (open && close != ")") return null;
        string token = m.Groups[2].Value;
        var mark = new ListMark
        {
            Text = (open ? "(" : string.Empty) + "%1" + close, Length = i + m.Length, MarkerX = X(p, i), TextX = X(p, i + m.Length),
            Look = RunAt(p, i), Tagged = p.TagListItem, Token = token,
        };
        if (char.IsDigit(token[0]))
        {
            mark.Format = "decimal";
            mark.Value = int.Parse(token, CultureInfo.InvariantCulture);
        }
        else if (token.Length > 1)
        {
            mark.Format = char.IsUpper(token[0]) ? "upperRoman" : "lowerRoman";
            mark.Value = Roman(token);
            if (mark.Value <= 0) return null;
        }
        else
        {
            mark.Format = char.IsUpper(token[0]) ? "upperLetter" : "lowerLetter";
            mark.Value = char.ToLowerInvariant(token[0]) - 'a' + 1;
        }
        return mark;
    }

    private static double X(DocParagraph p, int index)
    {
        for (int k = index; k < p.CharX.Count; k++)
            if (!double.IsNaN(p.CharX[k])) return p.CharX[k];
        return p.Box.L;
    }

    private static DocRun RunAt(DocParagraph p, int index)
    {
        int pos = 0;
        foreach (var r in p.Runs)
        {
            if (index < pos + r.Text.Length) return r;
            pos += r.Text.Length;
        }
        return p.Runs[^1];
    }

    internal static int Roman(string s)
    {
        int total = 0, prev = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            int v = char.ToLowerInvariant(s[i]) switch { 'i' => 1, 'v' => 5, 'x' => 10, 'l' => 50, 'c' => 100, 'd' => 500, 'm' => 1000, _ => 0 };
            if (v == 0) return 0;
            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }
        return total;
    }

    private sealed class OpenList
    {
        public List<(DocParagraph P, ListMark M)> Items = new();
        public List<double> LevelX = new();
        public Dictionary<int, (string Format, string Text, int Last)> Expect = new();
    }

    /// <summary>
    /// Consecutive list items become one list (a Word numbering instance); levels by how far
    /// their markers are indented. Paragraphs indented under an item continue it.
    /// </summary>
    private static void GroupLists(DocDocument doc)
    {
        var flow = doc.Sections.SelectMany(s => s.Items).OfType<DocParagraph>().ToList();
        OpenList? open = null;
        foreach (var p in flow)
        {
            var m = p.List;
            if (m == null)
            {
                if (open != null && (p.IsEmpty && p.Image == null || p.Heading == 0 && p.Image == null && p.Box.L >= open.Items[^1].M.TextX - 3)) continue;
                Finish(open, doc);
                open = null;
                continue;
            }
            if (open == null || !Continues(open, m)) { Finish(open, doc); open = new OpenList(); Continues(open, m); }
            open.Items.Add((p, m));
        }
        Finish(open, doc);
    }

    /// <summary>Whether the marker continues the list (and at which level); records it when it does.</summary>
    private static bool Continues(OpenList list, ListMark m)
    {
        int level = list.LevelX.FindIndex(x => Math.Abs(x - m.MarkerX) <= 6);
        if (level < 0)
        {
            if (list.LevelX.Count > 0 && m.MarkerX < list.LevelX[0]) return false;
            if (list.LevelX.Count >= 9) return false;
            list.LevelX.Add(m.MarkerX);
            list.LevelX.Sort();
            level = list.LevelX.IndexOf(m.MarkerX);
            // Levels below the new one move down one.
            var moved = list.Expect.Where(kv => kv.Key >= level).OrderByDescending(kv => kv.Key).ToList();
            foreach (var kv in moved) { list.Expect.Remove(kv.Key); list.Expect[kv.Key + 1] = kv.Value; }
            foreach (var (_, mm) in list.Items) if (mm.Level >= level) mm.Level++;
        }
        else if (list.Expect.TryGetValue(level, out var e))
        {
            if (e.Text != m.Text || (e.Format == "bullet") != (m.Format == "bullet") || (e.Format == "decimal") != (m.Format == "decimal")) return false;
            if (m.Format is not ("bullet" or "decimal"))
            {
                if (e.Format.StartsWith("upper", StringComparison.Ordinal) != m.Format.StartsWith("upper", StringComparison.Ordinal)) return false;
                bool wasRoman = e.Format.EndsWith("Roman", StringComparison.Ordinal), isRoman = m.Format.EndsWith("Roman", StringComparison.Ordinal);
                if (!wasRoman && isRoman)
                {
                    // A list that began with the letter i is roman when ii follows it.
                    if (e.Last != 9 || m.Value != 2) return false;
                    foreach (var (_, earlier) in list.Items.Where(x => x.M.Level == level)) { earlier.Format = m.Format; earlier.Value = 1; }
                    e = (m.Format, e.Text, 1);
                }
                else if (wasRoman && !isRoman)
                {
                    // A single letter that is a roman numeral (v, x) continues a roman list.
                    int value = Roman(m.Token);
                    if (value != e.Last + 1) return false;
                    m.Format = e.Format;
                    m.Value = value;
                }
            }
            if (m.Format != "bullet" && m.Value != e.Last + 1) return false;
        }
        m.Level = level;
        list.Expect[level] = (m.Format, m.Text, m.Value);
        // Deeper levels start again after an item above them.
        foreach (int deeper in list.Expect.Keys.Where(k => k > level).ToList()) list.Expect.Remove(deeper);
        return true;
    }

    private static void Finish(OpenList? list, DocDocument doc)
    {
        if (list == null || list.Items.Count == 0) return;
        // One item alone is a list only when its marker is a real bullet or the tags say so.
        if (list.Items.Count == 1 && !list.Items[0].M.Tagged && (list.Items[0].M.Weak || list.Items[0].M.Format != "bullet"))
        {
            list.Items[0].P.List = null;
            return;
        }
        var def = new ListDef { NumId = doc.Lists.Count + 1 };
        doc.Lists.Add(def);
        foreach (var (p, m) in list.Items)
        {
            m.NumId = def.NumId;
            def.Levels.TryAdd(m.Level, m);
            // Word draws the marker: it leaves the text.
            int remove = Math.Min(m.Length, p.CharX.Count);
            RemovePrefix(p, remove);
        }
    }

    private static void RemovePrefix(DocParagraph p, int count)
    {
        int left = count;
        while (left > 0 && p.Runs.Count > 0)
        {
            var r = p.Runs[0];
            if (r.Text.Length <= left) { left -= r.Text.Length; p.Runs.RemoveAt(0); }
            else { r.Text = r.Text[left..]; left = 0; }
        }
        p.CharX.RemoveRange(0, Math.Min(count, p.CharX.Count));
        p.CharLine.RemoveRange(0, Math.Min(count, p.CharLine.Count));
    }
}
