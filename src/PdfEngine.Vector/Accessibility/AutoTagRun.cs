using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Accessibility;

/// <summary>
/// One run of <see cref="PdfAutoTagger"/>: reads every page (operators, glyphs, lines and
/// paragraphs), finds the page furniture, the body text size, tables, lists, headings and the
/// reading order, decides what each operator is part of, then writes the tagged copy.
/// </summary>
internal sealed partial class AutoTagRun
{
    private readonly PdfVectorDocument _doc;
    private readonly PdfAutoTagOptions _options;
    private readonly CancellationToken _ct;
    private readonly PdfObjectResolver _r;
    private readonly List<PageModel> _pages = new();
    private double _bodySize = 10;
    private bool _bodyBold;

    public string Language { get; }
    public List<PdfAutoTagFigure> FiguresNeedingAlt { get; } = new();

    public AutoTagRun(PdfVectorDocument doc, PdfAutoTagOptions options, CancellationToken ct)
    {
        _doc = doc;
        _options = options;
        _ct = ct;
        _r = doc.Resolver;
        Language = options.Language ?? PdfAutoTagger.DocumentLanguage(doc) ?? "en";
    }

    // ------------------------------------------------------------------ the model

    /// <summary>A structure element to write.</summary>
    private sealed class TagNode
    {
        public TagNode(string type, int page) { Type = type; Page = page; }
        public string Type;
        public int Page;
        public TagNode? Parent;
        public List<TagNode> Children { get; } = new();
        /// <summary>Its marked content: MCIDs on <see cref="Page"/>, with where each reads.</summary>
        public List<(int Mcid, ReadKey Key)> Mcids { get; } = new();
        /// <summary>The annotation it holds by object reference.</summary>
        public int? Annot;
        public PdfDictionary? Attributes;
        public string? Alt;
        /// <summary>A heading's text (for the title only) and its size class.</summary>
        public string? HeadingText;
        public (double Size, bool Bold)? HeadingClass;
        /// <summary>Operators that draw its content.</summary>
        public int Used;
        public int Number;
        public PdfRect? Bounds;
        public ReadKey Key = ReadKey.Last;

        public TagNode Add(TagNode child)
        {
            child.Parent = this;
            Children.Add(child);
            return child;
        }
    }

    /// <summary>Where something reads on its page: by line, then along it, then by drawing order.</summary>
    private readonly record struct ReadKey(int Line, double Along, int Op) : IComparable<ReadKey>
    {
        public static readonly ReadKey Last = new(int.MaxValue, 0, int.MaxValue);
        public int CompareTo(ReadKey o) => Line != o.Line ? Line.CompareTo(o.Line) : Along != o.Along ? Along.CompareTo(o.Along) : Op.CompareTo(o.Op);
    }

    /// <summary>How content outside the structure is marked.</summary>
    private sealed record ArtifactTag(string Open, string Kind)
    {
        public static readonly ArtifactTag Plain = new("/Artifact BMC", "plain");
        public static readonly ArtifactTag Layout = new("/Artifact <</Type /Layout >>BDC", "layout");
        public static readonly ArtifactTag Background = new("/Artifact <</Type /Background >>BDC", "background");
        public static readonly ArtifactTag Header = new("/Artifact <</Type /Pagination /Subtype /Header >>BDC", "header");
        public static readonly ArtifactTag Footer = new("/Artifact <</Type /Pagination /Subtype /Footer >>BDC", "footer");
    }

    /// <summary>Text drawn with no line of its own (spaces, invisible marks): joins what is open around it.</summary>
    private sealed class InheritTag
    {
        public static readonly InheritTag Instance = new();
    }

    private sealed class PageModel
    {
        public int Index;
        public PdfPageNode Node = null!;
        public byte[] Content = Array.Empty<byte>();
        public List<ContentOp> Ops = new();
        public Dictionary<long, int> OpAt = new();
        /// <summary>The glyphs and images each page-level operator draws (forms' too).</summary>
        public Dictionary<int, List<ContentGlyph>> OpGlyphs = new();
        public Dictionary<int, List<ContentImage>> OpImages = new();
        /// <summary>For each operator inside an existing /ActualText sequence: that sequence's BDC.</summary>
        public int[] SpanOf = Array.Empty<int>();
        public List<TextBlock> Blocks = new();
        public Dictionary<ContentGlyph, TextLine> GlyphLine = new(ReferenceEqualityComparer.Instance);
        public Dictionary<TextLine, TextBlock> LineBlock = new(ReferenceEqualityComparer.Instance);
        public Dictionary<TextLine, PdfRect> LineBounds = new(ReferenceEqualityComparer.Instance);
        public Dictionary<TextBlock, ArtifactTag> ArtifactBlocks = new(ReferenceEqualityComparer.Instance);
        public double Width, Height;
        public bool MultiColumn;

        // ---- decided by BuildStructure
        public List<TagNode> Top = new();
        public Dictionary<TextLine, TagNode> LineOwner = new(ReferenceEqualityComparer.Instance);
        public Dictionary<TextLine, int> LineOrder = new(ReferenceEqualityComparer.Instance);
        public Dictionary<ContentGlyph, TagNode> MarkerGlyphs = new(ReferenceEqualityComparer.Instance);
        public Dictionary<int, TagNode> OpOverride = new();
        public Dictionary<int, TagNode> Figures = new();
        public Dictionary<int, ArtifactTag> OpArtifacts = new();
        /// <summary>What each operator is part of: a TagNode, an ArtifactTag or InheritTag; none for content the document already marks as an artifact.</summary>
        public Dictionary<int, object> Owner = new();
        /// <summary>Where each operator's content reads (for ordering an element's marked content).</summary>
        public Dictionary<int, ReadKey> OpKey = new();
        public Dictionary<TagNode, int> FigureOrder = new(ReferenceEqualityComparer.Instance);
        /// <summary>Annotations: (index in /Annots, object number, its element or null when not tagged).</summary>
        public List<(int Index, int Number, PdfDictionary Dict, TagNode? Node, string? Contents)> Annots = new();
        public Dictionary<int, PdfDictionary> DirectAnnots = new();
        public int Tables, TablesWithHeaders, Lists, ListItems, HeadersAndFooters;

        // ---- decided by the emission
        public List<TagNode?> ParentArray = new();
        public byte[]? Tagged;
        public int ArtifactSequences;
    }

    // ------------------------------------------------------------------ reading the pages

    /// <summary>Reads every page; returns the 1-based number of a page whose content cannot be read, or null.</summary>
    public int? Analyze()
    {
        var decoder = new PdfStreamDecoder(null, _r.Resolve);
        for (int i = 0; i < _doc.PageCount; i++)
        {
            _ct.ThrowIfCancellationRequested();
            var node = _doc.PageTree.Pages[i];
            var page = new PageModel { Index = i, Node = node };
            try
            {
                var content = new MemoryStream();
                foreach (var stream in node.Contents)
                {
                    content.Write(decoder.DecodeStream(stream));
                    content.WriteByte((byte)'\n');
                }
                page.Content = content.ToArray();
                page.Ops = AutoTagContent.Scan(page.Content, node.Resources, _r);
                var walker = new PageContentWalker(_doc, page.Content, node.Resources);
                walker.Read();
                Read(page, walker);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return i + 1;
            }
            _pages.Add(page);
        }
        FindFurniture();
        FindBodyText();
        foreach (var page in _pages)
        {
            _ct.ThrowIfCancellationRequested();
            BuildStructure(page);
        }
        return null;
    }

    private void Read(PageModel page, PageContentWalker walker)
    {
        for (int k = 0; k < page.Ops.Count; k++) page.OpAt[page.Ops[k].Keyword] = k;
        page.SpanOf = new int[page.Ops.Count];
        var open = new List<int>();
        for (int k = 0; k < page.Ops.Count; k++)
        {
            var op = page.Ops[k];
            int span = -1;
            for (int s = open.Count - 1; s >= 0; s--)
                if (open[s] >= 0) { span = open[s]; break; }
            page.SpanOf[k] = span;
            if (op.Kind == ContentOpKind.BeginMarked) open.Add(op.Properties?.ContainsKey("ActualText") == true ? k : -1);
            else if (op.Kind == ContentOpKind.EndMarked && open.Count > 0) open.RemoveAt(open.Count - 1);
        }

        var layout = new List<ContentGlyph>();
        var visible = new Dictionary<GlyphStyle, GlyphStyle>(ReferenceEqualityComparer.Instance);
        foreach (var g in walker.Glyphs)
        {
            if (!page.OpAt.TryGetValue(g.PageOp, out int op)) continue;
            if (!page.OpGlyphs.TryGetValue(op, out var list)) page.OpGlyphs[op] = list = new List<ContentGlyph>();
            list.Add(g);
            if (page.Ops[op].InArtifact) continue;
            // Invisible text (a scan's recognized layer) is the page's text all the same.
            if (g.Style.RenderMode is 3 or 7)
            {
                if (!visible.TryGetValue(g.Style, out var style)) visible[g.Style] = style = g.Style with { RenderMode = g.Style.RenderMode == 3 ? 0 : 4 };
                g.Style = style;
            }
            layout.Add(g);
        }
        foreach (var image in walker.Images)
            if (page.OpAt.TryGetValue(image.PageOp, out int op))
            {
                if (!page.OpImages.TryGetValue(op, out var list)) page.OpImages[op] = list = new List<ContentImage>();
                list.Add(image);
            }

        page.Blocks = TextBlocks.Build(layout);
        foreach (var block in page.Blocks)
        {
            for (int l = 0; l < block.Lines.Count; l++)
            {
                var line = block.Lines[l];
                page.LineBlock[line] = block;
                page.LineBounds[line] = l < block.LineBounds.Count ? block.LineBounds[l] : block.Bounds;
                foreach (var g in line.Glyphs)
                {
                    page.GlyphLine[g] = line;
                    if (g.Joined != null) foreach (var j in g.Joined) page.GlyphLine.TryAdd(j, line);
                }
            }
            // A drop shadow repeats its paragraph: only the copy on top is read.
            if (block.IsShadow) page.ArtifactBlocks[block] = ArtifactTag.Plain;
        }
        var crop = page.Node.CropBox;
        bool turned = page.Node.RotationDegrees is 90 or 270;
        page.Width = turned ? crop.Height : crop.Width;
        page.Height = turned ? crop.Width : crop.Height;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>A point of user space on the page as displayed (after /Rotate), from its top-left corner, y down.</summary>
    private static PdfPoint Display(PageModel page, double x, double y)
    {
        var c = page.Node.CropBox;
        return page.Node.RotationDegrees switch
        {
            90 => new PdfPoint(y - c.Y, x - c.X),
            180 => new PdfPoint(c.X + c.Width - x, y - c.Y),
            270 => new PdfPoint(c.Y + c.Height - y, c.X + c.Width - x),
            _ => new PdfPoint(x - c.X, c.Y + c.Height - y),
        };
    }

    /// <summary>A user-space rectangle on the page as displayed (x right, y down from the top-left).</summary>
    private static PdfRect Display(PageModel page, PdfRect r)
    {
        var a = Display(page, r.X, r.Y);
        var b = Display(page, r.X + r.Width, r.Y + r.Height);
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        return new PdfRect(x0, y0, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static PdfRect Union(PdfRect a, PdfRect b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        return new PdfRect(x0, y0, Math.Max(a.X + a.Width, b.X + b.Width) - x0, Math.Max(a.Y + a.Height, b.Y + b.Height) - y0);
    }

    private PdfRect LineDisplay(PageModel page, TextLine line) => Display(page, page.LineBounds[line]);

    /// <summary>The line runs left to right on the page as displayed.</summary>
    private static bool Horizontal(PageModel page, TextLine line)
    {
        var a = Display(page, 0, 0);
        var b = Display(page, line.U.X, line.U.Y);
        return b.X - a.X > 0.99 && !line.Vertical;
    }

    private static bool Meaningful(ContentGlyph g) => g.Text.Trim().Length > 0;

    private static bool IsBold(ContentGlyph g)
    {
        var font = g.Style.Font;
        if (font?.IsBold == true || g.Style.RenderMode == 2) return true;
        string name = font?.PostScriptName ?? font?.BaseFont ?? string.Empty;
        return name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Demi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LineBold(TextLine line)
    {
        int bold = 0, all = 0;
        foreach (var g in line.Glyphs)
        {
            if (!Meaningful(g)) continue;
            all++;
            if (IsBold(g)) bold++;
        }
        return all > 0 && bold >= 0.7 * all;
    }

    // ------------------------------------------------------------------ page furniture

    private static readonly Regex Digits = new(@"\d+", RegexOptions.CultureInvariant);
    private static readonly Regex PageNumber = new(@"^[\s\-–—\[\(]*((page|p\.|seite|pagina|página|page no\.?)\s*)?(#|[ivxlcdm]{1,6})(\s*(of|/|von|de|sur)\s*#)?[\s\-–—\]\)]*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Headers, footers and page numbers: text in the top or bottom eighth of the page that
    /// repeats on other pages at the same height (numbers aside), or that is a page number.
    /// </summary>
    private void FindFurniture()
    {
        var seen = new Dictionary<(bool Top, string Text), List<(PageModel Page, TextBlock Block, double Y)>>();
        foreach (var page in _pages)
            foreach (var block in page.Blocks)
            {
                if (page.ArtifactBlocks.ContainsKey(block) || block.Lines.Count > 3 || block.Text.Length > 200) continue;
                var d = Display(page, block.Bounds);
                bool top = d.Y + d.Height <= 0.125 * page.Height, bottom = d.Y >= 0.875 * page.Height;
                if (!top && !bottom) continue;
                string key = Regex.Replace(Digits.Replace(block.Text, "#"), @"\s+", " ").Trim();
                if (key.Length == 0) continue;
                if (PageNumber.IsMatch(key))
                {
                    page.ArtifactBlocks[block] = top ? ArtifactTag.Header : ArtifactTag.Footer;
                    page.HeadersAndFooters++;
                    continue;
                }
                if (!seen.TryGetValue((top, key), out var list)) seen[(top, key)] = list = new();
                list.Add((page, block, d.Y));
            }
        int needed = _pages.Count <= 4 ? 2 : 3;
        foreach (var ((top, _), list) in seen)
        {
            if (list.Count < needed) continue;
            var ys = list.Select(e => e.Y).OrderBy(y => y).ToList();
            double median = ys[ys.Count / 2];
            var near = list.Where(e => Math.Abs(e.Y - median) <= 10).ToList();
            if (near.Select(e => e.Page.Index).Distinct().Count() < needed) continue;
            foreach (var (page, block, _) in near)
                if (page.ArtifactBlocks.TryAdd(block, top ? ArtifactTag.Header : ArtifactTag.Footer)) page.HeadersAndFooters++;
        }
    }

    /// <summary>The body text size and weight: the size most characters are set in.</summary>
    private void FindBodyText()
    {
        var sizes = new List<(double Size, int Count, bool Bold)>();
        foreach (var page in _pages)
            foreach (var block in page.Blocks)
                if (!page.ArtifactBlocks.ContainsKey(block))
                    foreach (var line in block.Lines)
                        sizes.Add((Math.Round(line.Size * 2) / 2, line.Text.Length, LineBold(line)));
        if (sizes.Count == 0) return;
        var bySize = sizes.GroupBy(s => s.Size).Select(g => (Size: g.Key, Count: g.Sum(x => x.Count))).OrderByDescending(g => g.Count).ThenBy(g => g.Size).ToList();
        _bodySize = Math.Max(bySize[0].Size, 1);
        int total = sizes.Where(s => s.Size == _bodySize).Sum(s => s.Count);
        _bodyBold = sizes.Where(s => s.Size == _bodySize && s.Bold).Sum(s => s.Count) > 0.5 * total;
    }

    // ------------------------------------------------------------------ structure of a page

    /// <summary>Something read as a unit when ordering the page: a table, a figure or (what is left of) a paragraph.</summary>
    private sealed class Item
    {
        public PdfRect D;
        public TagNode? Node;
        public TextBlock? Block;
        public List<TextLine> Lines = new();
        /// <summary>For a table: its cells in reading order, each with its lines.</summary>
        public List<(TagNode Cell, List<TextLine> Lines)>? Cells;
    }

    private void BuildStructure(PageModel page)
    {
        var available = new List<TextLine>();
        foreach (var block in page.Blocks)
            if (!page.ArtifactBlocks.ContainsKey(block)) available.AddRange(block.Lines);
        var claimed = new HashSet<TextLine>(ReferenceEqualityComparer.Instance);

        // Bullets and numbers set apart from their text (as lines of their own) belong to the line beside them.
        var markerOf = _options.DetectLists ? AttachMarkers(page, available) : new Dictionary<TextLine, (TextLine, Match)>(ReferenceEqualityComparer.Instance);
        var markerLines = new HashSet<TextLine>(markerOf.Values.Select(v => v.Item1), ReferenceEqualityComparer.Instance);
        claimed.UnionWith(markerLines);

        var items = new List<Item>();
        var figureOps = FindFigures(page);
        if (_options.DetectTables)
        {
            var exclude = new HashSet<TextLine>(markerLines, ReferenceEqualityComparer.Instance);
            exclude.UnionWith(markerOf.Keys);
            foreach (var table in FindTables(page, available, exclude, figureOps))
            {
                foreach (var line in table.Lines) claimed.Add(line);
                items.Add(table);
                page.Tables++;
                if (table.Node!.Children.Count > 0 && table.Node.Children[0].Children.Any(c => c.Type == "TH")) page.TablesWithHeaders++;
            }
        }
        foreach (var block in page.Blocks)
        {
            if (page.ArtifactBlocks.ContainsKey(block)) continue;
            var lines = block.Lines.Where(l => !claimed.Contains(l)).ToList();
            if (lines.Count == 0) continue;
            var d = lines.Select(l => LineDisplay(page, l)).Aggregate(Union);
            items.Add(new Item { D = d, Block = block, Lines = lines });
        }
        foreach (var (op, node) in figureOps)
            items.Add(new Item { D = Display(page, node.Bounds ?? default), Node = node });

        bool multi = false;
        double gutter = Math.Max(6, 0.8 * _bodySize);
        var ordered = Cut(items, gutter, page, 0, ref multi);
        page.MultiColumn = multi;
        Place(page, ordered, markerOf);
        AssignOwners(page);
        TagAnnotations(page);
    }

    // ------------------------------------------------------------------ figures

    /// <summary>Images (and forms that draw only images) as figures; full-page images behind the page and specks as artifacts.</summary>
    private List<(int Op, TagNode Node)> FindFigures(PageModel page)
    {
        var figures = new List<(int, TagNode)>();
        double pageArea = Math.Max(page.Width * page.Height, 1);
        for (int k = 0; k < page.Ops.Count; k++)
        {
            var op = page.Ops[k];
            if (op.InArtifact) continue;
            bool image = op.Kind == ContentOpKind.InlineImage || (op.Kind == ContentOpKind.XObject && op.XObjectSubtype == "Image");
            bool form = op.Kind == ContentOpKind.XObject && op.XObjectSubtype == "Form";
            if (form && page.OpGlyphs.TryGetValue(k, out var glyphs) && glyphs.Any(Meaningful)) continue; // read as text
            if (form && !page.OpImages.ContainsKey(k)) continue; // vector drawing: an artifact
            if (!image && !form) continue;
            var bounds = op.Bounds ?? (page.OpImages.TryGetValue(k, out var imgs) ? imgs.Select(i => i.Placement.Transform(new PdfRect(0, 0, 1, 1))).Aggregate(Union) : default);
            var d = Display(page, bounds);
            if (d.Width * d.Height >= 0.85 * pageArea) { page.OpArtifacts[k] = ArtifactTag.Background; continue; }
            if (d.Width < 3 || d.Height < 3) { page.OpArtifacts[k] = ArtifactTag.Plain; continue; }
            var node = new TagNode("Figure", page.Index) { Bounds = bounds, Alt = _options.FigureAltPlaceholder };
            node.Attributes = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["O"] = new PdfName("Layout"),
                ["BBox"] = new PdfArray(new PdfObject[] { Real(bounds.X), Real(bounds.Y), Real(bounds.X + bounds.Width), Real(bounds.Y + bounds.Height) }),
            });
            page.Figures[k] = node;
            figures.Add((k, node));
        }
        return figures;
    }

    private static PdfObject Real(double v) => new PdfReal(Math.Round(v, 3));

    // ------------------------------------------------------------------ reading order

    /// <summary>
    /// Orders the page's items by cutting it along its white space (recursive XY cut): first into
    /// columns where a gap runs the whole height of the region, else into bands where one runs its
    /// whole width; what cannot be cut reads top to bottom, then left to right.
    /// </summary>
    private List<Item> Cut(List<Item> items, double gutter, PageModel page, int depth, ref bool multi)
    {
        if (items.Count <= 1 || depth > 64) return items;
        var byX = items.OrderBy(i => i.D.X).ToList();
        double maxRight = byX[0].D.X + byX[0].D.Width, bestGap = -1;
        int bestAt = -1;
        for (int k = 1; k < byX.Count; k++)
        {
            double gap = byX[k].D.X - maxRight;
            if (gap >= gutter && gap > bestGap) { bestGap = gap; bestAt = k; }
            maxRight = Math.Max(maxRight, byX[k].D.X + byX[k].D.Width);
        }
        if (bestAt > 0)
        {
            var left = byX.Take(bestAt).ToList();
            var right = byX.Skip(bestAt).ToList();
            double Tall(List<Item> side) => side.Where(i => i.Block != null).Select(i => i.D.Y + i.D.Height).DefaultIfEmpty(0).Max()
                                            - side.Where(i => i.Block != null).Select(i => i.D.Y).DefaultIfEmpty(0).Min();
            if (Tall(left) > 0.2 * page.Height && Tall(right) > 0.2 * page.Height) multi = true;
            var result = Cut(left, gutter, page, depth + 1, ref multi);
            result.AddRange(Cut(right, gutter, page, depth + 1, ref multi));
            return result;
        }
        var byY = items.OrderBy(i => i.D.Y).ThenBy(i => i.D.X).ToList();
        var bands = new List<List<Item>> { new() { byY[0] } };
        double bottom = byY[0].D.Y + byY[0].D.Height;
        for (int k = 1; k < byY.Count; k++)
        {
            if (byY[k].D.Y >= bottom - 1) bands.Add(new List<Item>());
            bands[^1].Add(byY[k]);
            bottom = Math.Max(bottom, byY[k].D.Y + byY[k].D.Height);
        }
        if (bands.Count == 1) return byY;
        var ordered = new List<Item>();
        foreach (var band in bands) ordered.AddRange(Cut(band, gutter, page, depth + 1, ref multi));
        return ordered;
    }
}
