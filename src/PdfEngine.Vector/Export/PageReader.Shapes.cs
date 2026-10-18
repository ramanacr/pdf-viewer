using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Rendering;

namespace PdfEngine.Vector.Export;

/// <summary>
/// Drawings: rectangles and straight lines the page draws become Word shapes at their places;
/// groups of other drawings (charts, maps, curves, gradients and patterns) become a picture of
/// just those drawings, made by the vector renderer. Rules and fills that are already table
/// borders, underlines or shading are not drawn twice.
/// </summary>
internal sealed partial class PageReader
{
    private const int MaxShapesPerPage = 300;
    private const int MaxPicturesPerPage = 60;

    private sealed class Drawn
    {
        /// <summary>The command in the page's display list.</summary>
        public int Index;
        public Box Box;
        public string? Fill, Stroke;
        public double Width;
        public Box? Rect;
        public (PdfPoint A, PdfPoint B)? Segment;
        public bool Complex, Used;
    }

    private List<Drawn> _drawn = new();
    /// <summary>The drawing each rule and fill came from (an index into <see cref="_drawn"/>, or -1).</summary>
    private List<int> _ruleSource = new(), _fillSource = new();

    /// <summary>Records a painted path; returns its index in <see cref="_drawn"/>, or -1.</summary>
    private int Record(int index, PdfPath path, PdfMatrix ctm, string? fill, string? stroke, double width, List<List<PdfPoint>> polys, bool stroked)
    {
        if (PathBox(path, ctm) is not { } box) return -1;
        var d = new Drawn { Index = index, Box = stroked ? box.Inflate(width / 2) : box, Fill = fill, Stroke = stroke, Width = width };
        bool curved = path.Segments.Any(s => s is PdfCubicBezierTo);
        if (fill == "pattern" || curved || polys.Count != 1) d.Complex = true;
        else if (RectangleOf(polys[0]) is { } rect && (!stroked || polys[0].Count == 5 && polys[0][0] == polys[0][^1])) d.Rect = rect;
        else if (stroked && polys[0].Count == 2) d.Segment = (polys[0][0], polys[0][1]);
        else d.Complex = true;
        if (d.Complex && fill == "pattern") d.Fill = null;
        _drawn.Add(d);
        return _drawn.Count - 1;
    }

    /// <summary>The bounds of a path on the page as shown (control points included), or null when it is empty.</summary>
    private Box? PathBox(PdfPath path, PdfMatrix ctm)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        void Add(PdfPoint p)
        {
            var s = Show(ctm.Transform(p));
            l = Math.Min(l, s.X); r = Math.Max(r, s.X); t = Math.Min(t, s.Y); b = Math.Max(b, s.Y);
        }
        foreach (var seg in path.Segments)
            switch (seg)
            {
                case PdfMoveTo m: Add(m.Point); break;
                case PdfLineTo ln: Add(ln.Point); break;
                case PdfCubicBezierTo c: Add(c.Control1); Add(c.Control2); Add(c.EndPoint); break;
            }
        return l <= r && t <= b ? new Box(l, t, r, b) : null;
    }

    private async Task PlaceDrawingsAsync(DocPage page, IPdfDisplayList list, List<DocTable> tables, HashSet<int> underlines, CancellationToken ct)
    {
        if (_drawn.Count == 0) return;
        foreach (int i in underlines)
            if (i < _ruleSource.Count && _ruleSource[i] >= 0) _drawn[_ruleSource[i]].Used = true;
        var pageBox = new Box(0, 0, _width, _height);
        foreach (var d in _drawn)
        {
            if (d.Used) continue;
            // White on the white page, or nothing to see.
            if (d.Stroke == null && d.Fill == "FFFFFF" || d.Box.W < 0.3 && d.Box.H < 0.3 || d.Box.Overlap(pageBox) <= 0) d.Used = true;
            // Table borders and shading are the table's.
            else if (!d.Complex && tables.Any(t => t.Box.Inflate(3).Contains(d.Box.L, d.Box.T) && t.Box.Inflate(3).Contains(d.Box.R, d.Box.B))) d.Used = true;
        }
        var simple = _drawn.Where(d => !d.Used && !d.Complex).ToList();
        // A page of hundreds of little shapes (a map drawn in boxes) is better as a picture.
        if (simple.Count > MaxShapesPerPage) foreach (var d in simple) d.Complex = true;
        foreach (var d in _drawn.Where(d => !d.Used && !d.Complex))
        {
            if (d.Rect is { } r)
                page.Shapes.Add(new DocShape { Kind = ShapeKind.Rectangle, Box = r, Fill = d.Fill, Stroke = d.Stroke, StrokeWidth = d.Width, Behind = true, Order = d.Index });
            else if (d.Segment is var (a, b))
            {
                var box = new Box(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                // A line shape runs from its box's top-left corner to the bottom-right; mirrored for the other diagonal.
                bool down = (b.X - a.X) * (b.Y - a.Y) >= 0;
                page.Shapes.Add(new DocShape { Kind = ShapeKind.Line, Box = box, FlipH = !down, Stroke = d.Stroke, StrokeWidth = d.Width, Behind = true, Order = d.Index });
            }
        }
        await PicturesAsync(page, list, ct).ConfigureAwait(false);
        page.Shapes.Sort((x, y) => x.Order.CompareTo(y.Order));
    }

    /// <summary>The other drawings, in groups of those that touch, each as a picture of just those drawings.</summary>
    private async Task PicturesAsync(DocPage page, IPdfDisplayList list, CancellationToken ct)
    {
        var complex = _drawn.Where(d => !d.Used && d.Complex).ToList();
        if (complex.Count == 0 || !_package.WantsImages) return;
        if (_renderer == null)
        {
            Warnings.Add("Drawings other than rectangles and lines need a renderer to come over as pictures.");
            return;
        }
        // Groups of drawings that touch or nearly do.
        var parent = Enumerable.Range(0, complex.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var order = Enumerable.Range(0, complex.Count).OrderBy(i => complex[i].Box.L).ToList();
        for (int a = 0; a < order.Count; a++)
        {
            var ba = complex[order[a]].Box.Inflate(3);
            for (int b = a + 1; b < order.Count && complex[order[b]].Box.L <= ba.R; b++)
                if (ba.Overlap(complex[order[b]].Box.Inflate(3)) > 0) parent[Find(order[a])] = Find(order[b]);
        }
        var groups = Enumerable.Range(0, complex.Count).GroupBy(Find)
            .Select(g => (Members: g.Select(i => complex[i]).ToList(), Box: g.Select(i => complex[i].Box).Aggregate((x, y) => x.Union(y))))
            .Where(g => g.Box.W >= 2 && g.Box.H >= 2)
            .OrderByDescending(g => g.Box.W * g.Box.H).Take(MaxPicturesPerPage).ToList();
        if (groups.Count == 0) return;

        // One rendering of only those drawings (no text, no images): each group is cut out of it.
        var keep = new HashSet<int>(groups.SelectMany(g => g.Members).Select(d => d.Index));
        var commands = new List<PdfDrawCommand>(list.Commands.Count);
        for (int i = 0; i < list.Commands.Count; i++)
        {
            var c = list.Commands[i];
            if (c is SaveState or RestoreState or ConcatTransform or PushClip or PushStrokeClip or PushTextClip or PopClip
                or BeginTransparencyGroup or EndTransparencyGroup or BeginCompositingGroup or EndCompositingGroup || keep.Contains(i))
                commands.Add(c);
        }
        var drawings = new PdfDisplayList(list.PageNumber, list.PageSize, list.MediaBox, list.CropBox, list.RotationDegrees, commands, list.Features);
        RenderedPage rendered;
        try
        {
            rendered = await _renderer.RenderDisplayListAsync(drawings, new RenderRequest { PageNumber = list.PageNumber, Dpi = _graphicsDpi, RenderAnnotations = false, RenderForms = false }, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Warnings.Add($"Page {list.PageNumber}: its drawings could not be drawn as pictures ({ex.GetType().Name}).");
            return;
        }
        using (rendered)
        {
            double scaleX = rendered.WidthPixels / Math.Max(1, _width), scaleY = rendered.HeightPixels / Math.Max(1, _height);
            var pixels = rendered.Pixels.Span;
            foreach (var (members, box) in groups)
            {
                ct.ThrowIfCancellationRequested();
                int x0 = Math.Clamp((int)Math.Floor((box.L - 1) * scaleX), 0, rendered.WidthPixels), x1 = Math.Clamp((int)Math.Ceiling((box.R + 1) * scaleX), 0, rendered.WidthPixels);
                int y0 = Math.Clamp((int)Math.Floor((box.T - 1) * scaleY), 0, rendered.HeightPixels), y1 = Math.Clamp((int)Math.Ceiling((box.B + 1) * scaleY), 0, rendered.HeightPixels);
                int w = x1 - x0, h = y1 - y0;
                if (w < 2 || h < 2) continue;
                var crop = new byte[w * h * 4];
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    var row = pixels.Slice((y0 + y) * rendered.Stride + x0 * 4, w * 4);
                    var to = crop.AsSpan(y * w * 4, w * 4);
                    row.CopyTo(to);
                    for (int k = 0; k < to.Length; k += 4)
                    {
                        // The renderer paints the page white: white is left see-through, so the picture does not hide what is behind it.
                        if (to[k] >= 250 && to[k + 1] >= 250 && to[k + 2] >= 250) to[k + 3] = 0;
                        else { to[k + 3] = 255; any = true; }
                    }
                }
                if (!any) continue;
                string rel = _package.AddImage(PngEncoder.Encode(crop, w, h), "png");
                var shown = new Box(x0 / scaleX, y0 / scaleY, x1 / scaleX, y1 / scaleY);
                page.Shapes.Add(new DocShape { Kind = ShapeKind.Picture, Box = shown, RelId = rel, Behind = true, Order = members.Min(m => m.Index) });
            }
        }
    }
}
