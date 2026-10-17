using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Images;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Export;

/// <summary>A straight line the page draws (a rule, a table border or an underline), on the page as shown.</summary>
internal readonly record struct Rule(bool Horizontal, double Pos, double From, double To, double Width, string Color)
{
    public double Length => To - From;
}

/// <summary>A filled rectangle (a shaded paragraph or cell).</summary>
internal readonly record struct Fill(Box Box, string Color);

/// <summary>
/// Reads one page for export: its text as paragraphs with styled runs, its tables (from tags,
/// ruling lines or aligned text), its images and links, in the coordinates of the page as shown.
/// </summary>
internal sealed partial class PageReader
{
    private const int MaxImagesPerPage = 300;
    private const int MaxPathSegments = 200_000;

    private readonly PdfVectorDocument _doc;
    private readonly StructureTree? _tags;
    private readonly IReadOnlyList<int>? _pageObjects;
    private readonly DocxPackage _package;
    private readonly List<string> _hyperlinks;
    private readonly Dictionary<string, int> _hyperlinkIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<object, (string Family, bool Bold, bool Italic)> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _imagesByKey = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();
    public Dictionary<string, (bool Serif, bool Fixed)> FontKinds { get; } = new(StringComparer.Ordinal);

    // Per page.
    private PdfMatrix _display;
    private double _width, _height;
    private List<Rule> _rules = new();
    private List<Fill> _fills = new();
    private List<(Box Box, int Link)> _links = new();
    private int _pageObject = -1;
    private readonly Dictionary<ContentGlyph, GlyphInfo> _info = new(ReferenceEqualityComparer.Instance);

    private sealed class GlyphInfo
    {
        public Box Box;
        public StructNode? Node;
        public int Link = -1;
        public bool Underline, Strike;
        public int VertAlign;
    }

    private readonly IPdfVectorRenderer? _renderer;
    private readonly double _graphicsDpi;

    public PageReader(PdfVectorDocument doc, StructureTree? tags, IReadOnlyList<int>? pageObjects, DocxPackage package, List<string> hyperlinks, DocxExportOptions? options = null)
    {
        _renderer = options?.Renderer;
        _graphicsDpi = Math.Clamp(options?.GraphicsDpi ?? 150, 72, 300);
        _doc = doc;
        _tags = tags;
        _pageObjects = pageObjects;
        _package = package;
        _hyperlinks = hyperlinks;
    }

    public async Task<DocPage> ReadAsync(int pageNumber, CancellationToken ct)
    {
        var node = _doc.PageTree.Pages[pageNumber - 1];
        _display = DisplayMatrix(node.CropBox, node.RotationDegrees);
        _width = node.PageSize.Width;
        _height = node.PageSize.Height;
        _info.Clear();
        _pageObject = _pageObjects != null && pageNumber - 1 < _pageObjects.Count ? _pageObjects[pageNumber - 1] : -1;
        var page = new DocPage { Number = pageNumber, Width = _width, Height = _height };

        // Text.
        var glyphs = ReadGlyphs(node, pageNumber);
        var visible = glyphs.Where(g => g.Style.RenderMode != 3 && g.Style.FontResource.Length > 0).ToList();
        // Invisible text over a scan (recognized text) or over lettering drawn as shapes is the
        // page's text too: it is exported, except where visible text already says it.
        var hidden = glyphs.Where(g => g.Style.RenderMode == 3 && g.Style.FontResource.Length > 0 && g.Text.Trim().Length > 0).ToList();
        bool recognized = hidden.Count > visible.Count;
        if (hidden.Count > 0)
        {
            var shownBoxes = visible.Select(g => _display.Transform(TextBlocks.GlyphBox(g))).Select(ToBox).ToList();
            foreach (var g in hidden)
            {
                var b = ToBox(_display.Transform(TextBlocks.GlyphBox(g)));
                if (!shownBoxes.Any(s => s.Contains(b.Cx, b.Cy))) visible.Add(Shown(g));
            }
        }

        // Graphics: rules, fills and images.
        var images = new List<(PdfImageRef Ref, PdfMatrix M)>();
        _rules = new List<Rule>();
        _fills = new List<Fill>();
        _ruleSource = new List<int>();
        _fillSource = new List<int>();
        _drawn = new List<Drawn>();
        IPdfDisplayList? displayList = null;
        try
        {
            displayList = await _doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
            ReadGraphics(displayList.Commands, images);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Warnings.Add($"Page {pageNumber}: its drawings could not be read ({ex.GetType().Name}).");
        }
        ct.ThrowIfCancellationRequested();
        ReadLinks(node);

        foreach (var g in visible)
        {
            var info = new GlyphInfo { Box = ToBox(_display.Transform(TextBlocks.GlyphBox(g))) };
            if (_tags != null && g.Mcid >= 0 && _pageObject >= 0) info.Node = _tags.Find(_pageObject, g.Mcid);
            var c = new PdfPoint(info.Box.Cx, info.Box.Cy);
            for (int i = 0; i < _links.Count; i++)
                if (_links[i].Box.Contains(c.X, c.Y)) { info.Link = _links[i].Link; break; }
            _info[g] = info;
        }
        var underlines = MarkUnderlines(visible);

        // Tables take their glyphs out of the flow.
        var tables = new List<DocTable>();
        var inTable = new HashSet<ContentGlyph>(ReferenceEqualityComparer.Instance);
        if (_tags != null) tables.AddRange(TaggedTables(visible, inTable));
        tables.AddRange(RuledTables(visible, inTable, underlines, tables.Select(t => t.Box).ToList()));
        var flow = visible.Where(g => !inTable.Contains(g)).ToList();
        var paragraphs = BuildFlow(flow);
        MergeStrays(paragraphs);
        // A tagged page says where its tables are; elsewhere text aligned in columns can be one.
        if (!visible.Any(g => _info[g].Node != null)) tables.AddRange(GridTables(paragraphs));
        foreach (var p in paragraphs) p.Shading = ShadingOf(p.Box);
        // Turned text goes in a text box turned the same way, where it is: in the flow it would
        // be a line across the page that pushes the text after it down.
        foreach (var p in paragraphs.Where(p => p.Rotated && p.Image == null).ToList())
        {
            paragraphs.Remove(p);
            page.TextBoxes.Add(TurnedBox(p));
        }

        page.Items.AddRange(paragraphs);
        page.Items.AddRange(tables);
        PlaceImages(page, images, recognized, ct);
        if (displayList != null) await PlaceDrawingsAsync(page, displayList, tables, underlines, ct).ConfigureAwait(false);
        page.VerticalWriting = paragraphs.Sum(p => p.Vertical ? p.Text.Length : 0) > paragraphs.Sum(p => p.Vertical ? 0 : p.Text.Length);
        return page;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Default user space to the page as shown: from the crop box's top-left corner, y down, turned by /Rotate.</summary>
    internal static PdfMatrix DisplayMatrix(PdfRect crop, int rotation)
    {
        double cx = crop.X, cy = crop.Y, w = crop.Width, h = crop.Height;
        return (((rotation % 360) + 360) % 360) switch
        {
            90 => new PdfMatrix(0, 1, 1, 0, -cy, -cx),
            180 => new PdfMatrix(-1, 0, 0, 1, cx + w, -cy),
            270 => new PdfMatrix(0, -1, -1, 0, cy + h, cx + w),
            _ => new PdfMatrix(1, 0, 0, -1, -cx, cy + h),
        };
    }

    private static Box ToBox(PdfRect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

    private PdfPoint Show(PdfPoint userPoint) => _display.Transform(userPoint);

    /// <summary>A direction on the page as shown.</summary>
    private PdfPoint Direction(PdfPoint v) => new(_display.A * v.X + _display.C * v.Y, _display.B * v.X + _display.D * v.Y);

    // ------------------------------------------------------------------ reading

    private List<ContentGlyph> ReadGlyphs(PdfPageNode node, int pageNumber)
    {
        var decoder = new PdfStreamDecoder(null, _doc.Resolver.Resolve);
        var content = new MemoryStream();
        try
        {
            foreach (var stream in node.Contents)
            {
                content.Write(decoder.DecodeStream(stream));
                content.WriteByte((byte)'\n');
            }
            var walker = new PageContentWalker(_doc, content.ToArray(), node.Resources);
            walker.Read();
            TightenAdvances(walker.Glyphs);
            return walker.Glyphs;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Warnings.Add($"Page {pageNumber}: its text could not be read ({ex.GetType().Name}).");
            return new List<ContentGlyph>();
        }
    }

    /// <summary>
    /// Character spacing that a TJ array takes back glyph by glyph (a table row drawn with a large
    /// Tc and negative adjustments) is not space the glyph fills: its advance is cut to where the
    /// next glyph of the same operator starts, but never below its own width.
    /// </summary>
    private static void TightenAdvances(List<ContentGlyph> glyphs)
    {
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (g.Style.Vertical) continue;
            var m = g.Matrix;
            double lenSq = m.A * m.A + m.B * m.B;
            if (lenSq < 1e-12) continue;
            double natural = g.Width0 / 1000.0 * g.Style.FontSize * g.Style.Scaling / 100.0;
            // Spacing smaller than a third of an em is letter spacing the text really has.
            if (g.Advance <= natural + 0.3 * Math.Abs(g.Style.FontSize)) continue;
            bool kept = false;
            if (i + 1 < glyphs.Count && glyphs[i + 1].Op == g.Op)
            {
                var n = glyphs[i + 1];
                double d = ((n.Origin.X - g.Origin.X) * m.A + (n.Origin.Y - g.Origin.Y) * m.B) / lenSq;
                kept = Math.Abs(d - g.Advance) < 0.01 * Math.Abs(g.Style.FontSize) + 1e-6;
            }
            if (!kept) g.Advance = natural;
        }
    }

    private static ContentGlyph Shown(ContentGlyph g) => new()
    {
        Op = g.Op, Index = g.Index, Text = g.Text, Code = g.Code, Style = g.Style with { RenderMode = 0 }, Matrix = g.Matrix,
        Width0 = g.Width0, Advance = g.Advance, IsWordSpace = g.IsWordSpace, Context = g.Context, Mcid = g.Mcid,
    };

    private void ReadGraphics(IReadOnlyList<PdfDrawCommand> commands, List<(PdfImageRef, PdfMatrix)> images)
    {
        var ctm = PdfMatrix.Identity;
        var stack = new Stack<PdfMatrix>();
        int segments = 0;
        double pageArea = _width * _height;
        for (int index = 0; index < commands.Count; index++)
        {
            var cmd = commands[index];
            switch (cmd)
            {
                case SaveState: stack.Push(ctm); break;
                case RestoreState: if (stack.Count > 0) ctm = stack.Pop(); break;
                case ConcatTransform c: ctm = c.Matrix * ctm; break;
                case DrawImage di when images.Count < MaxImagesPerPage: images.Add((di.Image, di.Transform * ctm)); break;
                case StrokePath sp when segments < MaxPathSegments:
                {
                    if (Invisible(sp.Paint)) break;
                    double scale = Math.Sqrt(Math.Abs(ctm.Determinant));
                    double width = Math.Max(sp.Stroke.Width * scale, 0.25);
                    string color = Hex(sp.Paint.Color);
                    var polys = Polylines(sp.Path, ctm, closeAlways: false);
                    int source = Record(index, sp.Path, ctm, null, color, width, polys, stroked: true);
                    foreach (var poly in polys)
                        for (int i = 1; i < poly.Count; i++)
                        {
                            segments++;
                            AddRule(poly[i - 1], poly[i], width, color, source);
                        }
                    break;
                }
                case FillPath fp when segments < MaxPathSegments:
                {
                    if (Invisible(fp.Paint))
                    {
                        // A pattern fill: drawn only as a picture.
                        if (fp.Paint.PatternName != null && fp.Paint.Alpha >= 0.1) Record(index, fp.Path, ctm, "pattern", null, 0, new List<List<PdfPoint>>(), stroked: false);
                        break;
                    }
                    string color = Hex(fp.Paint.Color);
                    var polys = Polylines(fp.Path, ctm, closeAlways: true);
                    int source = Record(index, fp.Path, ctm, color, null, 0, polys, stroked: false);
                    foreach (var poly in polys)
                    {
                        segments += poly.Count;
                        if (RectangleOf(poly) is not { } box) continue;
                        if (box.H <= 2.5 && box.W > 2) { _rules.Add(new Rule(true, box.Cy, box.L, box.R, Math.Max(box.H, 0.25), color)); _ruleSource.Add(source); }
                        else if (box.W <= 2.5 && box.H > 2) { _rules.Add(new Rule(false, box.Cx, box.T, box.B, Math.Max(box.W, 0.25), color)); _ruleSource.Add(source); }
                        else if (box.W * box.H < 0.8 * pageArea && color != "FFFFFF") { _fills.Add(new Fill(box, color)); _fillSource.Add(source); }
                    }
                    break;
                }
                case DrawShading or DrawTilingPattern when cmd.Bounds is { } bounds:
                {
                    // A gradient or a pattern: drawn only as a picture.
                    var b = ToBox(_display.Transform(bounds));
                    _drawn.Add(new Drawn { Index = index, Box = b, Complex = true });
                    break;
                }
            }
        }
    }

    private static bool Invisible(PdfPaint paint) => paint.PatternName != null || paint.Alpha < 0.1 || paint.Color.A < 0.1f;

    internal static string Hex(PdfColor c) =>
        $"{(int)Math.Round(Math.Clamp(c.R, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(c.G, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(c.B, 0, 1) * 255):X2}";

    internal static string Hex(double[] rgb) => rgb.Length >= 3
        ? $"{(int)Math.Round(Math.Clamp(rgb[0], 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(rgb[1], 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(rgb[2], 0, 1) * 255):X2}"
        : "000000";

    private void AddRule(PdfPoint a, PdfPoint b, double width, string color, int source)
    {
        if (color == "FFFFFF") return;
        double dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        if (dy < 0.8 && dx > 2) { _rules.Add(new Rule(true, (a.Y + b.Y) / 2, Math.Min(a.X, b.X), Math.Max(a.X, b.X), width, color)); _ruleSource.Add(source); }
        else if (dx < 0.8 && dy > 2) { _rules.Add(new Rule(false, (a.X + b.X) / 2, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), width, color)); _ruleSource.Add(source); }
    }

    /// <summary>A path's subpaths as points on the page as shown (none for subpaths with curves).</summary>
    private List<List<PdfPoint>> Polylines(PdfPath path, PdfMatrix ctm, bool closeAlways)
    {
        var result = new List<List<PdfPoint>>();
        List<PdfPoint>? current = null;
        bool curved = false;
        void End(bool close)
        {
            if (current != null && current.Count >= 2 && !curved)
            {
                if ((close || closeAlways) && (current[0] != current[^1])) current.Add(current[0]);
                result.Add(current);
            }
            current = null;
            curved = false;
        }
        foreach (var seg in path.Segments)
        {
            switch (seg)
            {
                case PdfMoveTo m:
                    End(false);
                    current = new List<PdfPoint> { Show(ctm.Transform(m.Point)) };
                    break;
                case PdfLineTo l:
                    current ??= new List<PdfPoint>();
                    current.Add(Show(ctm.Transform(l.Point)));
                    break;
                case PdfCubicBezierTo c:
                    curved = true;
                    current ??= new List<PdfPoint>();
                    current.Add(Show(ctm.Transform(c.EndPoint)));
                    break;
                case PdfCloseSubpath:
                    var start = current != null && current.Count > 0 ? current[0] : default;
                    End(true);
                    current = new List<PdfPoint> { start };
                    break;
            }
        }
        End(false);
        return result;
    }

    /// <summary>The box a closed four-sided, axis-aligned polygon fills, or null.</summary>
    private static Box? RectangleOf(List<PdfPoint> poly)
    {
        if (poly.Count is < 4 or > 5) return null;
        double l = poly.Min(p => p.X), r = poly.Max(p => p.X), t = poly.Min(p => p.Y), b = poly.Max(p => p.Y);
        foreach (var p in poly)
            if ((Math.Abs(p.X - l) > 0.5 && Math.Abs(p.X - r) > 0.5) || (Math.Abs(p.Y - t) > 0.5 && Math.Abs(p.Y - b) > 0.5)) return null;
        return new Box(l, t, r, b);
    }

    private void ReadLinks(PdfPageNode node)
    {
        _links = new List<(Box, int)>();
        var r = _doc.Resolver;
        if (r.Resolve(node.Dictionary["Annots"]) is not PdfArray annots) return;
        foreach (var item in annots.Take(2000))
        {
            if (r.Resolve(item) is not PdfDictionary a || a.GetName("Subtype") != "Link") continue;
            if (r.Resolve(a["A"]) is not PdfDictionary action || action.GetName("S") != "URI") continue;
            if (r.Resolve(action["URI"]) is not PdfString us) continue;
            string uri = us.AsDecodedString().Trim();
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto" or "ftp")) continue;
            if (r.Resolve(a["Rect"]) is not PdfArray rect || rect.Count < 4) continue;
            var n = rect.Items.Select(i => r.Resolve(i)?.TryGetNumber(out double v) == true ? v : double.NaN).ToArray();
            if (n.Any(double.IsNaN)) continue;
            var box = ToBox(_display.Transform(new PdfRect(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Abs(n[2] - n[0]), Math.Abs(n[3] - n[1]))));
            if (!_hyperlinkIndex.TryGetValue(parsed.AbsoluteUri, out int index))
            {
                index = _hyperlinks.Count;
                _hyperlinks.Add(parsed.AbsoluteUri);
                _hyperlinkIndex[parsed.AbsoluteUri] = index;
            }
            _links.Add((box.Inflate(1), index));
        }
    }

    /// <summary>Marks glyphs that a thin rule runs under (underlined) or through (struck out); returns those rules.</summary>
    private HashSet<int> MarkUnderlines(List<ContentGlyph> glyphs)
    {
        var used = new HashSet<int>();
        var horizontal = _rules.Select((r, i) => (r, i)).Where(x => x.r.Horizontal && x.r.Width < 3).OrderBy(x => x.r.Pos).ToList();
        if (horizontal.Count == 0) return used;
        var positions = horizontal.Select(x => x.r.Pos).ToArray();
        foreach (var g in glyphs)
        {
            if (g.Text.Trim().Length == 0) continue;
            var (u, _) = TextBlocks.FrameOf(g);
            var du = Direction(u);
            if (du.X < 0.99 || g.Style.Vertical) continue;
            var info = _info[g];
            double size = TextBlocks.EmHeight(g), baseline = Show(g.Origin).Y;
            double x0 = info.Box.L, x1 = Math.Max(info.Box.R, x0 + 0.1);
            int start = LowerBound(positions, baseline - 0.45 * size);
            for (int k = start; k < horizontal.Count && positions[k] <= baseline + 0.3 * size; k++)
            {
                var (rule, index) = horizontal[k];
                if (rule.Width > 0.15 * size + 0.3) continue;
                double covered = Math.Min(rule.To, x1) - Math.Max(rule.From, x0);
                if (covered < 0.7 * (x1 - x0)) continue;
                double d = rule.Pos - baseline;
                if (d >= -0.02 * size) { info.Underline = true; used.Add(index); }
                else if (d <= -0.15 * size && d >= -0.45 * size) { info.Strike = true; used.Add(index); }
            }
        }
        return used;
    }

    private static int LowerBound(double[] values, double v)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (values[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private string? ShadingOf(Box box)
    {
        int best = -1;
        for (int i = 0; i < _fills.Count; i++)
        {
            var f = _fills[i];
            if (f.Box.L <= box.L + 1 && f.Box.R >= box.R - 1 && f.Box.T <= box.T + 1 && f.Box.B >= box.B - 1
                && (best < 0 || f.Box.W * f.Box.H < _fills[best].Box.W * _fills[best].Box.H))
                best = i;
        }
        if (best < 0) return null;
        // The fill is the paragraph's or cell's shading now, not a shape of its own.
        if (_fillSource[best] >= 0) _drawn[_fillSource[best]].Used = true;
        return _fills[best].Color;
    }

    // ------------------------------------------------------------------ paragraphs

    /// <summary>
    /// The page's paragraphs, found column by column where the page has columns: a narrow
    /// gutter is less than a line's word gap, so lines across it would otherwise join. Rows of
    /// text that cross a gutter (a title over the columns) stay whole.
    /// </summary>
    private List<DocParagraph> BuildFlow(List<ContentGlyph> flow)
    {
        // Upright text only: turned text (a title up the page's edge) crosses no column.
        var gutters = DocumentLayout.Gutters(flow.Where(g => g.Text.Trim().Length > 0 && !g.Style.Vertical && Direction(TextBlocks.FrameOf(g).Item1).X > 0.98)
            .Select(g => _info[g].Box).ToList(), 3);
        if (gutters.Count == 0) return BuildParagraphs(flow);
        bool Crosses(Box b) => gutters.Any(gt => b.R > gt.From + 0.5 && b.L < gt.To - 0.5);
        // Rows by baseline.
        var zone = new Dictionary<ContentGlyph, int>(ReferenceEqualityComparer.Instance);
        var ordered = flow.Select(g => (G: g, Y: Show(g.Origin).Y)).OrderBy(x => x.Y).ToList();
        int i = 0;
        while (i < ordered.Count)
        {
            int j = i;
            while (j + 1 < ordered.Count && ordered[j + 1].Y - ordered[i].Y <= 1.5) j++;
            var row = ordered.Skip(i).Take(j - i + 1).Select(x => x.G).ToList();
            bool across = row.Any(g => g.Text.Trim().Length > 0 && Crosses(_info[g].Box));
            foreach (var g in row)
                zone[g] = across ? -1 : gutters.Count(gt => _info[g].Box.Cx > (gt.From + gt.To) / 2);
            i = j + 1;
        }
        var result = new List<DocParagraph>();
        foreach (var part in flow.GroupBy(g => zone[g]).OrderBy(z => z.Key))
            result.AddRange(BuildParagraphs(part.ToList()));
        return result;
    }

    /// <summary>
    /// A mark or two of small text off a line's baseline (®, a footnote number) that the line
    /// finder left on its own goes back into the line it sits on, raised or lowered there; on
    /// its own it would take a line's height in Word.
    /// </summary>
    private static void MergeStrays(List<DocParagraph> paragraphs)
    {
        // Marks one under the other (a column of footnote letters) were found as one paragraph: each goes on its own.
        foreach (var p in paragraphs.Where(p => p.Lines.Count >= 2 && p.Image == null && !p.Rotated && !p.Vertical && p.CharLine.Count == p.Text.Length).ToList())
        {
            var pieces = SplitByLine(p).ToList();
            if (pieces.Count != p.Lines.Count || pieces.Any(x => x.Text.Trim().Length > 3)) continue;
            int at = paragraphs.IndexOf(p);
            paragraphs.RemoveAt(at);
            paragraphs.InsertRange(at, pieces);
        }
        foreach (var s in paragraphs.Where(p => p.Lines.Count == 1 && p.Image == null && !p.Rotated && !p.Vertical && p.Text.Trim().Length is > 0 and <= 3).ToList())
        {
            double size = s.Size;
            foreach (var t in paragraphs)
            {
                if (ReferenceEquals(t, s) || t.Rotated || t.Vertical || t.Image != null) continue;
                int li = t.Lines.FindIndex(l => (l.Size > 1.2 * size || l.Size > 1.05 * size && s.Box.Cy < l.Baseline - 0.4 * l.Size)
                                                && s.Box.Cy >= l.Box.T - 0.3 * l.Size && s.Box.Cy <= l.Box.B + 0.1 * l.Size
                                                && s.Box.L >= l.Box.L - 1 && s.Box.L <= l.Box.R + 1.5 * l.Size);
                if (li < 0) continue;
                var line = t.Lines[li];
                int vert = s.Box.Cy < line.Baseline - 0.35 * line.Size ? 1 : -1;
                // Before the first character of that line that starts to its right.
                int at = -1, lastOnLine = -1;
                for (int k = 0; k < t.CharLine.Count; k++)
                {
                    if (t.CharLine[k] != li) continue;
                    lastOnLine = k;
                    if (at < 0 && !double.IsNaN(t.CharX[k]) && t.CharX[k] > s.Box.L) at = k;
                }
                if (at < 0) at = lastOnLine + 1;
                if (at <= 0) continue;
                var runs = s.Runs.Select(r => { var c = r.Clone(r.Text.Trim()); c.VertAlign = vert; c.Size = Math.Max(c.Size, Math.Round(line.Size * 2) / 2); return c; })
                    .Where(r => r.Text.Length > 0).ToList();
                InsertRuns(t, at, runs, s.Box.L, li);
                paragraphs.Remove(s);
                break;
            }
        }
    }

    private static void InsertRuns(DocParagraph p, int at, List<DocRun> runs, double x, int line)
    {
        int pos = 0;
        for (int i = 0; i <= p.Runs.Count; i++)
        {
            int len = i < p.Runs.Count ? p.Runs[i].Text.Length : 0;
            if (i == p.Runs.Count || at <= pos + len)
            {
                if (i < p.Runs.Count && at > pos && at < pos + len)
                {
                    var r = p.Runs[i];
                    var tail = r.Clone(r.Text[(at - pos)..]);
                    r.Text = r.Text[..(at - pos)];
                    p.Runs.Insert(i + 1, tail);
                    i++;
                }
                else if (i < p.Runs.Count && at == pos + len) i++;
                p.Runs.InsertRange(i, runs);
                int n = runs.Sum(r => r.Text.Length);
                p.CharX.InsertRange(Math.Min(at, p.CharX.Count), Enumerable.Repeat(x, n));
                p.CharLine.InsertRange(Math.Min(at, p.CharLine.Count), Enumerable.Repeat(line, n));
                return;
            }
            pos += len;
        }
    }

    /// <summary>Paragraphs of the given glyphs, in reading order, with runs of one look each.</summary>
    private List<DocParagraph> BuildParagraphs(List<ContentGlyph> glyphs)
    {
        var result = new List<DocParagraph>();
        if (glyphs.Count == 0) return result;
        List<TextBlock> blocks;
        try { blocks = TextBlocks.Build(glyphs); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            Warnings.Add($"Some text could not be grouped into paragraphs ({ex.GetType().Name}).");
            return result;
        }
        foreach (var block in blocks)
        {
            if (block.IsShadow || block.Lines.Count == 0) continue;
            MarkScripts(block);
            // Paragraph breaks: where a line ends for good, and where tagged elements change.
            var elements = block.Lines.Select(ElementOf).ToList();
            var cuts = new List<int> { 0 };
            for (int k = 0; k < block.Text.Length; k++)
                if (block.Text[k] == '\n') cuts.Add(k + 1);
            for (int li = 1; li < block.Lines.Count; li++)
                if (elements[li] != null && elements[li - 1] != null && !ReferenceEquals(elements[li], elements[li - 1]))
                    cuts.Add(block.LineStarts[li]);
            // A line that starts with a bullet or a number starts a list item.
            for (int li = 1; li < block.Lines.Count; li++)
                if (DocumentLayout.StartsListItem(block.Lines[li].Text)) cuts.Add(block.LineStarts[li]);
            cuts = cuts.Distinct().OrderBy(c => c).ToList();
            for (int s = 0; s < cuts.Count; s++)
            {
                int from = cuts[s], to = s + 1 < cuts.Count ? cuts[s + 1] : block.Text.Length;
                if (Paragraph(block, from, to) is { } p) result.Add(p);
            }
        }
        return result;
    }

    /// <summary>The block-level structure element (paragraph, heading, list item, cell) most of a line belongs to.</summary>
    private StructNode? ElementOf(TextLine line)
    {
        if (_tags == null) return null;
        return line.Glyphs.Select(g => _info.TryGetValue(g, out var i) ? i.Node?.Ancestor(n => n.Type is "P" or "LI" or "TD" or "TH" or "Caption" or "TOCI" or "BlockQuote" or "Note" || n.HeadingLevel > 0) : null)
            .Where(n => n != null).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
    }

    /// <summary>Superscript and subscript glyphs: smaller, and off the line's baseline.</summary>
    private void MarkScripts(TextBlock block)
    {
        foreach (var line in block.Lines)
        {
            var main = line.Glyphs.OrderByDescending(TextBlocks.EmHeight).First();
            double baseT = TextBlocks.Dot(main.Origin, line.V) + main.Style.Rise;
            foreach (var g in line.Glyphs)
            {
                if (!_info.TryGetValue(g, out var info)) continue;
                double ratio = TextBlocks.EmHeight(g) / Math.Max(line.Size, 0.1);
                double t = TextBlocks.Dot(g.Origin, line.V) + g.Style.Rise - baseT;
                if (ratio < 0.85 && t > 0.12 * line.Size) info.VertAlign = 1;
                else if (ratio < 0.85 && t < -0.08 * line.Size) info.VertAlign = -1;
            }
        }
    }

    private DocParagraph? Paragraph(TextBlock block, int from, int to)
    {
        var p = new DocParagraph { Align = block.Alignment, AlignKnown = block.Lines.Count >= 2, Vertical = block.Vertical };
        var lines = new SortedSet<int>();
        DocRun? run = null;
        ContentGlyph? last = null;
        string text = block.Text;
        // The space that joined its last line to the next paragraph's first is not its own.
        while (to > from && block.Chars[to - 1].Separator) to--;
        for (int k = from; k < to; k++)
        {
            char c = text[k];
            if (c == '\n') continue;
            // Characters XML cannot hold are left out (a lone surrogate, a control code).
            bool valid = c == '\t' || c >= 0x20 && c < 0xD800 || c >= 0xE000 && c <= 0xFFFD
                || char.IsHighSurrogate(c) && k + 1 < to && char.IsLowSurrogate(text[k + 1])
                || char.IsLowSurrogate(c) && k > from && char.IsHighSurrogate(text[k - 1]);
            if (!valid) continue;
            var (li, gi, _) = block.Chars[k];
            ContentGlyph? g = gi >= 0 ? block.Lines[li].Glyphs[gi] : null;
            if (g != null) { lines.Add(li); last = g; }
            var look = Look(g ?? last ?? NextGlyph(block, k, to), c, run);
            if (look == null) continue;
            p.CharX.Add(g != null && _info.TryGetValue(g, out var gInfo) ? gInfo.Box.L : double.NaN);
            p.CharLine.Add(Math.Max(0, lines.Count - 1));
            if (run != null && run.SameLook(look)) run.Text += c;
            else
            {
                look.Text = c.ToString();
                p.Runs.Add(look);
                run = look;
            }
        }
        if (lines.Count == 0 || p.Runs.All(r => r.Text.Trim().Length == 0)) return null;
        double lineSize = Math.Round(lines.Max(li => block.Lines[li].Size) * 2) / 2;
        // Word draws superscript and subscript smaller itself: they take the line's size.
        foreach (var r in p.Runs)
            if (r.VertAlign != 0) r.Size = Math.Max(r.Size, lineSize);

        var du = Direction(block.Vertical ? block.V : block.U);
        p.Rotated = !block.Vertical && du.X < 0.98;
        foreach (int li in lines)
        {
            var line = block.Lines[li];
            var box = ToBox(_display.Transform(block.LineBounds[li]));
            var main = line.Glyphs.OrderByDescending(TextBlocks.EmHeight).First();
            double baseline = p.Rotated || p.Vertical ? box.B : Show(main.Origin).Y;
            p.Lines.Add(new DocLine { Box = box, Baseline = baseline, Size = line.Size });
        }
        p.Box = p.Lines.Select(l => l.Box).Aggregate((a, b) => a.Union(b));
        p.Pitch = block.Lines.Count >= 2 ? block.Pitch : 1.2 * p.Size;
        if (p.Rotated)
        {
            // How far the text runs along its own direction: from the first glyph's start to the last one's end.
            double len = Math.Sqrt(du.X * du.X + du.Y * du.Y);
            var dn = len > 1e-9 ? new PdfPoint(du.X / len, du.Y / len) : new PdfPoint(1, 0);
            p.Angle = Math.Atan2(dn.Y, dn.X) * 180 / Math.PI;
            double first = double.MaxValue, end = double.MinValue;
            foreach (int li in lines)
                foreach (var g in block.Lines[li].Glyphs)
                {
                    var m = g.Matrix;
                    var a = Show(g.Origin);
                    var b = Show(new PdfPoint(g.Origin.X + g.Advance * m.A, g.Origin.Y + g.Advance * m.B));
                    double sa = a.X * dn.X + a.Y * dn.Y, sb = b.X * dn.X + b.Y * dn.Y;
                    first = Math.Min(first, Math.Min(sa, sb));
                    end = Math.Max(end, Math.Max(sa, sb));
                }
            p.Along = end > first ? end - first : Math.Max(p.Box.W, p.Box.H);
        }
        string shown = p.Text;
        p.Rtl = shown.Count(TextKinds.IsRtl) > shown.Count(ch => char.IsLetter(ch) && !TextKinds.IsRtl(ch));

        // What the tags say it is.
        if (_tags != null)
        {
            var nodes = lines.SelectMany(li => block.Lines[li].Glyphs).Select(g => _info.TryGetValue(g, out var i) ? i.Node : null).Where(n => n != null).ToList();
            if (nodes.Count > 0)
            {
                var heading = nodes.Select(n => n!.Ancestor(a => a.HeadingLevel > 0)).GroupBy(n => n?.HeadingLevel ?? 0).OrderByDescending(g => g.Count()).First();
                if (heading.Key > 0 && heading.Count() * 2 > nodes.Count) p.TagHeading = heading.Key;
                var item = nodes.Select(n => n!.Ancestor(a => a.Type == "LI")).FirstOrDefault(n => n != null);
                if (item != null)
                {
                    p.TagListItem = true;
                    int depth = 0;
                    StructNode? list = null;
                    for (var a = item.Parent; a != null; a = a.Parent)
                        if (a.Type == "L") { depth++; list ??= a; }
                    p.TagListDepth = Math.Max(1, depth);
                    p.TagListNumbering = list?.ListNumbering;
                }
            }
        }
        return p;
    }

    /// <summary>A text box for turned text: as long as the text (with room for a wider stand-in font), its lines high, centred where the text is.</summary>
    internal static DocTextBox TurnedBox(DocParagraph p)
    {
        double size = p.Size, pitch = p.Lines.Count >= 2 ? p.Pitch : 1.2 * size;
        double w = p.Along * 1.15 + size, h = Math.Max(1, p.Lines.Count) * pitch + 0.3 * size;
        double rad = p.Angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        // The text starts at the box's start: the extra length is past its end.
        double shift = (w - p.Along) / 2;
        double cx = p.Box.Cx + cos * shift, cy = p.Box.Cy + sin * shift;
        double bw = Math.Abs(w * cos) + Math.Abs(h * sin), bh = Math.Abs(w * sin) + Math.Abs(h * cos);
        int rotation = (int)Math.Round(p.Angle);
        rotation = ((rotation % 360) + 360) % 360;
        return new DocTextBox
        {
            Box = new Box(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2), Width = w, Height = h, Rotation = rotation,
            Paragraphs = { p },
        };
    }

    private static ContentGlyph? NextGlyph(TextBlock block, int k, int to)
    {
        for (int j = k + 1; j < to; j++)
        {
            var (li, gi, _) = block.Chars[j];
            if (gi >= 0) return block.Lines[li].Glyphs[gi];
        }
        return null;
    }

    /// <summary>The look of one character: its glyph's font, size and colour, and what the page draws over and under it.</summary>
    private DocRun? Look(ContentGlyph? g, char c, DocRun? previous)
    {
        if (g == null) return previous?.Clone(string.Empty);
        var st = g.Style;
        object key = (object?)st.Font ?? st.FontResource;
        if (!_fonts.TryGetValue(key, out var font))
        {
            string ps = st.Font?.PostScriptName ?? st.Font?.BaseFont ?? st.FontResource;
            _fonts[key] = font = FontNames.Resolve(ps, st.Font?.IsBold ?? false, st.Font?.IsItalic ?? false);
            FontKinds.TryAdd(font.Family, (st.Font?.IsSerif == true || FontNames.LooksSerif(ps), st.Font?.IsFixedPitch == true));
        }
        _info.TryGetValue(g, out var info);
        bool neutral = !char.IsLetter(c);
        var run = new DocRun
        {
            Font = font.Family,
            Bold = font.Bold || st.RenderMode == 2,
            Italic = font.Italic,
            Size = Math.Round(TextBlocks.EmHeight(g) * 2) / 2,
            Color = Hex(st.FillRgb),
            Scale = Math.Abs(st.Scaling - 100) > 3 ? (int)Math.Clamp(Math.Round(st.Scaling), 1, 600) : 100,
            Underline = info?.Underline ?? false,
            Strike = info?.Strike ?? false,
            VertAlign = info?.VertAlign ?? 0,
            Link = info?.Link ?? -1,
            Rtl = neutral ? previous?.Rtl ?? false : TextKinds.IsRtl(c),
            EastAsian = TextKinds.IsEastAsian(c) || (neutral && !char.IsDigit(c) && (previous?.EastAsian ?? false)),
        };
        if (run.Size < 1) run.Size = 1;
        return run;
    }
}
