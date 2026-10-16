using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.PdfA;

/// <summary>The flattened file and what was done.</summary>
internal sealed record PdfFlatteningResult(byte[] Bytes, PdfFlatteningReport Report);

/// <summary>
/// Removes transparency (ISO 19005-1 6.4) from a document so it can become PDF/A-1, changing as
/// little as possible, as Acrobat's flattener does:
/// <list type="bullet">
/// <item>Every object drawn with a soft mask, an alpha below 1, a blend mode other than Normal, a
/// soft-masked image or a transparent pattern (a "transparent object") is found with its bounds,
/// and the areas they cover are merged into regions (the whole page when they cover most of it).</item>
/// <item>Each region is drawn by the engine's renderer exactly as the page looks, from everything
/// up to the last transparent object, and the image replaces those objects there.</item>
/// <item>Everything else stays vector: what lies under the regions is drawn first (the image covers
/// it), and objects drawn after the last transparent object are drawn again on top of the image, so
/// text above transparency stays real, sharp text.</item>
/// <item>Text that was part of a transparent object stays in its operator, in place, as invisible
/// text (rendering mode 3), so it can still be searched and copied.</item>
/// <item>Transparency groups, soft masks, blend modes and alpha are removed from what remains, and
/// resources no longer drawn leave the page (and so the file).</item>
/// </list>
/// The page content is replayed three times by one walker (in the same order, so each drawing
/// operator has the same ordinal in each): to draw the regions, for the content under the images,
/// and for the content over them; operators that do not belong to a copy keep only their effect on
/// the graphics state (paths end with n, text keeps its advance), so the clipping and positions
/// every other operator relies on are unchanged.
/// </summary>
internal sealed class TransparencyFlattener
{
    private readonly PdfVectorDocument _doc;
    private readonly PdfObjectResolver _r;
    private readonly PdfFontResolver _fonts;
    private readonly PdfStreamDecoder _decoder;
    private int _next;
    private readonly Dictionary<PdfObject, bool> _patterns = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _groupForms = new();
    private int _hiddenText;

    private TransparencyFlattener(PdfVectorDocument doc)
    {
        _doc = doc;
        _r = doc.Resolver;
        _fonts = new PdfFontResolver(_r, PdfSecurityLimits.Default);
        _decoder = new PdfStreamDecoder(PdfSecurityLimits.Default, _r.Resolve);
        _next = PdfIncrementalWriter.NextObjectNumber(doc);
    }

    private int Allocate() => _next++;

    /// <summary>
    /// The document with its transparency flattened, or null when it has none on its pages.
    /// Throws <see cref="PdfAConversionRefusedException"/> when a page cannot be drawn.
    /// </summary>
    public static PdfFlatteningResult? Run(PdfVectorDocument doc, IPdfPageRasterizer rasterizer, PdfFlatteningOptions options)
    {
        ArgumentNullException.ThrowIfNull(rasterizer);
        return new TransparencyFlattener(doc).Execute(rasterizer, options ?? new PdfFlatteningOptions());
    }

    // ------------------------------------------------------------------ plan

    private sealed class Region
    {
        public PdfRect Bounds;
        public double Dpi;
        public int Width, Height;
        public bool Text;
        public string ImageName = string.Empty;
        public PdfRasterImage? Pixels;
    }

    private sealed class PagePlan
    {
        public int Index;
        public int ObjectNumber;
        public PdfPageNode Node = null!;
        public byte[] Content = Array.Empty<byte>();
        public PdfRect Crop;
        public bool PageGroup;
        public int Split = int.MaxValue;
        public int Last;
        public bool WholePage;
        public List<Region> Regions = new();
    }

    private PdfFlatteningResult? Execute(IPdfPageRasterizer rasterizer, PdfFlatteningOptions options)
    {
        var trailer = _doc.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (_r.Resolve(trailer["Root"]) is not PdfDictionary root) throw new InvalidOperationException("The document has no catalog.");
        var pageNumbers = PdfRedactor.PageObjectNumbers(root, _r);
        var plans = new List<PagePlan>();
        for (int i = 0; i < _doc.PageTree.Pages.Count && i < pageNumbers.Count; i++)
        {
            var node = _doc.PageTree.Pages[i];
            var content = PageContent(node);
            if (content == null) continue; // unreadable: left as it is (and reported by the validation)
            var crop = Intersect(node.CropBox, node.MediaBox) is { IsEmpty: false } c ? c : node.MediaBox;
            bool pageGroup = IsGroupForm(node.Dictionary);
            var analysis = new Walker(this, Pass.Analyze, int.MaxValue, crop);
            analysis.Run(content, node.Resources);
            if (analysis.Units.Count == 0 && !analysis.NeedsCleaning && !pageGroup) continue;
            var plan = new PagePlan { Index = i, ObjectNumber = pageNumbers[i], Node = node, Content = content, Crop = crop, PageGroup = pageGroup, Last = analysis.LastOrdinal };
            if (analysis.Units.Count > 0) PlanRegions(plan, analysis, options);
            plans.Add(plan);
        }
        if (plans.Count == 0) return null;

        RenderRegions(plans.Where(p => p.Regions.Count > 0).ToList(), rasterizer);

        // The final pages: under the images, the images, over the images.
        var overrides = new Dictionary<int, PdfObject>();
        var store = new FormStore(this, overrides);
        int groups = 0;
        foreach (var plan in plans)
        {
            var use = new ResourceUse(this, plan.Node.Resources);
            var sb = new StringBuilder();
            var under = new Walker(this, Pass.Under, plan.Split, plan.Crop, store);
            sb.Append("q\n").Append(under.Run(plan.Content, plan.Node.Resources, use)).Append("Q\n");
            foreach (var region in plan.Regions)
            {
                var image = ImageObject(region.Pixels!);
                int number = Allocate();
                overrides[number] = image;
                region.ImageName = use.AddXObject("FlIm", new PdfIndirectRef(number));
                region.Pixels = null;
                var b = region.Bounds;
                sb.Append("q ").Append(F(b.X)).Append(' ').Append(F(b.Y)).Append(' ').Append(F(b.Width)).Append(' ').Append(F(b.Height))
                  .Append(" re W n ").Append(F(b.Width)).Append(" 0 0 ").Append(F(b.Height)).Append(' ').Append(F(b.X)).Append(' ').Append(F(b.Y))
                  .Append(" cm /").Append(region.ImageName).Append(" Do Q\n");
            }
            if (plan.Split < plan.Last)
            {
                var over = new Walker(this, Pass.Over, plan.Split, plan.Crop, store);
                sb.Append("q\n").Append(over.Run(plan.Content, plan.Node.Resources, use)).Append("Q\n");
            }
            int contentNumber = Allocate();
            overrides[contentNumber] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") },
                ContentRedactor.Deflate(Encoding.Latin1.GetBytes(sb.ToString())));
            var page = new Dictionary<string, PdfObject>(((overrides.TryGetValue(plan.ObjectNumber, out var o) ? o : _r.Resolve(plan.ObjectNumber)) as PdfDictionary ?? plan.Node.Dictionary).Entries)
            {
                ["Contents"] = new PdfIndirectRef(contentNumber),
                ["Resources"] = use.Build(sanitize: true, prune: true),
            };
            if (page.Remove("Group")) groups++;
            if (plan.Regions.Count > 0) page.Remove("Thumb"); // a picture of the page with its transparency
            overrides[plan.ObjectNumber] = new PdfDictionary(page);
        }

        var flattened = plans.Where(p => p.Regions.Count > 0).ToList();
        var report = new PdfFlatteningReport
        {
            Pages = flattened.Select(p => new PdfFlattenedPage(p.Index + 1,
                p.Regions.Select(r => new PdfFlattenedRegion(r.Bounds, r.Dpi, r.Width, r.Height, r.Text)).ToList(), p.WholePage,
                _renderers.TryGetValue(p.Index, out var who) ? who : string.Empty)).ToList(),
            PagesCleanedOnly = plans.Count - flattened.Count,
            GroupsRemoved = groups + _groupForms.Count,
            InvisibleTextOperators = _hiddenText,
        };
        return new PdfFlatteningResult(PdfAConverter.WriteDecrypted(_doc, overrides), report);
    }

    /// <summary>The page's content streams as one (concatenated as ISO 32000 7.8.2 says), or null when it cannot be read.</summary>
    private byte[]? PageContent(PdfPageNode node)
    {
        try
        {
            using var ms = new MemoryStream();
            foreach (var stream in node.Contents)
            {
                ms.Write(_decoder.DecodeStream(stream));
                ms.WriteByte((byte)'\n');
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// Merges the transparent objects' areas into regions on the pixel grid of their resolution,
    /// and decides where the content splits into what is under the images and what is over them.
    /// </summary>
    private void PlanRegions(PagePlan plan, Walker analysis, PdfFlatteningOptions options)
    {
        var crop = plan.Crop;
        const double Pad = 1.0; // anti-aliasing at the objects' edges
        var rects = analysis.Units.Select(u => Grow(u.Bounds, Pad)).ToList();
        plan.Split = analysis.Units.Max(u => u.Ordinal);

        // What the engine itself finds transparent must be covered too. An area that reaches past an
        // object the walker found is the same object bounded differently: the region grows. One
        // apart from all of them is an object the walker missed: its area is added, and everything
        // is drawn under the images, so the image stacks it as the page did.
        var found = rects.ToList();
        foreach (var bounds in DisplayListTransparency(plan.Index + 1))
        {
            var b = Intersect(bounds, crop);
            if (b.IsEmpty || rects.Any(r => Contains(r, b, 0.5))) continue;
            if (!found.Any(r => r.IntersectsWith(b))) plan.Split = int.MaxValue;
            rects.Add(Grow(b, Pad));
        }

        var merged = Merge(rects.Select(r => Intersect(r, Grow(crop, Pad))).Where(r => !r.IsEmpty).ToList());
        double area = merged.Sum(r => r.Width * r.Height);
        if (merged.Count > 64 || area >= options.WholePageCoverage * crop.Width * crop.Height)
        {
            merged = new List<PdfRect> { crop };
            plan.WholePage = true;
        }

        foreach (var rect in merged)
        {
            // Text drawn (and so rasterized) in the region, under or in a transparent object, wants more pixels.
            bool text = analysis.Units.Any(u => u.Text && u.Bounds.IntersectsWith(rect))
                        || analysis.Texts.Any(t => t.Ordinal <= plan.Split && t.Bounds.IntersectsWith(rect));
            double dpi = Math.Clamp(text ? Math.Max(options.TextDpi, options.Dpi) : options.Dpi, 36, 2400);
            var region = Snap(rect, crop, dpi, plan.WholePage);
            long pixels = (long)region.Width * region.Height;
            if (pixels > options.MaxPixelsPerRegion && options.MaxPixelsPerRegion > 0)
                region = Snap(rect, crop, dpi * Math.Sqrt(options.MaxPixelsPerRegion / (double)pixels), plan.WholePage);
            region.Text = text;
            plan.Regions.Add(region);
        }
    }

    /// <summary>
    /// The region on the pixel grid of <paramref name="dpi"/> anchored at the crop box's corner, so
    /// its image maps pixel for pixel; it may reach past the crop box by less than a pixel.
    /// </summary>
    private static Region Snap(PdfRect rect, PdfRect crop, double dpi, bool whole)
    {
        double s = dpi / 72.0;
        if (whole)
        {
            // The whole page: exactly the crop box, at the nearest whole number of pixels.
            int w = Math.Max(1, (int)Math.Round(crop.Width * s)), h = Math.Max(1, (int)Math.Round(crop.Height * s));
            return new Region { Bounds = crop, Dpi = dpi, Width = w, Height = h };
        }
        var r = Intersect(rect, crop);
        if (r.IsEmpty) r = rect;
        int px0 = (int)Math.Floor((r.X - crop.X) * s + 1e-6), px1 = (int)Math.Ceiling((r.Right - crop.X) * s - 1e-6);
        int py0 = (int)Math.Floor((r.Y - crop.Y) * s + 1e-6), py1 = (int)Math.Ceiling((r.Bottom - crop.Y) * s - 1e-6);
        px1 = Math.Max(px1, px0 + 1);
        py1 = Math.Max(py1, py0 + 1);
        return new Region
        {
            Bounds = new PdfRect(crop.X + px0 / s, crop.Y + py0 / s, (px1 - px0) / s, (py1 - py0) / s),
            Dpi = dpi,
            Width = px1 - px0,
            Height = py1 - py0,
        };
    }

    /// <summary>Overlapping or touching rectangles become their common bounding box, until none overlap.</summary>
    private static List<PdfRect> Merge(List<PdfRect> rects)
    {
        if (rects.Count > 4000) return new List<PdfRect> { rects.Aggregate(Union) };
        var list = new List<PdfRect>(rects);
        bool mergedAny = true;
        while (mergedAny)
        {
            mergedAny = false;
            for (int i = 0; i < list.Count && !mergedAny; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (!Grow(list[i], 1).IntersectsWith(list[j])) continue;
                    list[i] = Union(list[i], list[j]);
                    list.RemoveAt(j);
                    mergedAny = true;
                    break;
                }
        }
        return list;
    }

    /// <summary>
    /// Areas the engine's own interpreter composites (groups with alpha, blend modes or soft masks,
    /// paint with alpha): the display list's conservative page-space bounds.
    /// </summary>
    private List<PdfRect> DisplayListTransparency(int pageNumber)
    {
        var found = new List<PdfRect>();
        IPdfDisplayList list;
        try { list = _doc.GetPageDisplayListAsync(pageNumber).AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return found; }
        int depth = 0;
        foreach (var command in list.Commands)
        {
            switch (command)
            {
                case BeginCompositingGroup g:
                    if (depth++ == 0 && g.Bounds is { } gb && (g.Alpha < 1 || g.Blend != PdfBlendMode.Normal || g.SoftMask != null)) found.Add(gb);
                    break;
                case BeginTransparencyGroup t:
                    if (depth++ == 0 && t.Bounds is { } tb && (t.Alpha < 1 || t.BlendMode is { } bm && bm != "Normal")) found.Add(tb);
                    break;
                case EndCompositingGroup or EndTransparencyGroup:
                    depth = Math.Max(0, depth - 1);
                    break;
                case FillPath f when depth == 0 && f.Paint.Alpha < 1 && f.Bounds is { } fb: found.Add(fb); break;
                case StrokePath s when depth == 0 && s.Paint.Alpha < 1 && s.Bounds is { } sb: found.Add(sb); break;
                case DrawGlyphRun g when depth == 0 && !g.Run.IsInvisible && (g.Paint.Alpha < 1 || g.Run.StrokePaint is { Alpha: < 1 }) && g.Bounds is { } gl: found.Add(gl); break;
                case DrawImage im when depth == 0 && im.Image.Opacity < 1 && im.Bounds is { } ib: found.Add(ib); break;
            }
        }
        return found;
    }

    // ------------------------------------------------------------------ rendering

    private readonly Dictionary<int, string> _renderers = new();

    /// <summary>
    /// Draws every region: a temporary document whose pages are the regions (each a copy of its page
    /// cropped to the region, without annotations, with the content up to the split), drawn by the
    /// rasterizer at the region's pixel size.
    /// </summary>
    private void RenderRegions(List<PagePlan> plans, IPdfPageRasterizer rasterizer)
    {
        if (plans.Count == 0) return;
        var overrides = new Dictionary<int, PdfObject>();
        var store = new FormStore(this, overrides);
        int pagesNode = Allocate();
        var kids = new List<PdfObject>();
        var order = new List<(PagePlan Plan, Region Region)>();
        foreach (var plan in plans)
        {
            PdfDictionary resources = plan.Node.Resources;
            byte[] content = plan.Content;
            if (plan.Split < plan.Last)
            {
                var use = new ResourceUse(this, plan.Node.Resources);
                var render = new Walker(this, Pass.Render, plan.Split, plan.Crop, store);
                content = Encoding.Latin1.GetBytes(render.Run(plan.Content, plan.Node.Resources, use));
                resources = use.Build(sanitize: false, prune: false);
            }
            int contentNumber = Allocate();
            overrides[contentNumber] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, ContentRedactor.Deflate(content));
            foreach (var region in plan.Regions)
            {
                var box = RectArray(region.Bounds);
                var page = new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("Page"),
                    ["Parent"] = new PdfIndirectRef(pagesNode),
                    ["MediaBox"] = box,
                    ["CropBox"] = box,
                    ["Resources"] = resources,
                    ["Contents"] = new PdfIndirectRef(contentNumber),
                };
                if (plan.Node.Dictionary["Group"] is { } group) page["Group"] = group;
                int pageNumber = Allocate();
                overrides[pageNumber] = new PdfDictionary(page);
                kids.Add(new PdfIndirectRef(pageNumber));
                order.Add((plan, region));
            }
        }
        overrides[pagesNode] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Pages"), ["Kids"] = new PdfArray(kids), ["Count"] = new PdfInteger(kids.Count),
        });
        var trailer = _doc.XrefTable.Trailer!;
        int rootNumber = ((PdfIndirectRef)trailer["Root"]!).ObjectNumber;
        var catalog = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("Catalog"), ["Pages"] = new PdfIndirectRef(pagesNode) };
        if (_r.Resolve(rootNumber) is PdfDictionary root && root["OCProperties"] is { } oc) catalog["OCProperties"] = oc;
        overrides[rootNumber] = new PdfDictionary(catalog);

        byte[] bytes = PdfAConverter.WriteDecrypted(_doc, overrides);
        using var temp = PdfVectorDocument.Open(new MemoryByteSource(bytes), _doc.FilePath);
        for (int i = 0; i < order.Count; i++)
        {
            var (plan, region) = order[i];
            PdfRasterImage? pixels;
            try { pixels = rasterizer.Render(temp, bytes, i + 1, region.Width, region.Height); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { pixels = null; }
            if (pixels == null || pixels.Width != region.Width || pixels.Height != region.Height || pixels.Bgra.Length < region.Width * region.Height * 4)
                throw new PdfAConversionRefusedException(
                    $"PDF/A-1b does not allow transparency, and page {plan.Index + 1} could not be drawn to flatten it. Convert to PDF/A-2b instead.");
            region.Pixels = pixels;
            _renderers[plan.Index] = _renderers.TryGetValue(plan.Index, out var before) && before != pixels.Renderer ? before + ", " + pixels.Renderer : pixels.Renderer;
        }
    }

    /// <summary>
    /// The pixels as an image XObject: 8-bit DeviceRGB (DeviceGray when every pixel is grey),
    /// Flate-compressed with PNG predictors (lossless, as text and line art need).
    /// </summary>
    private static PdfStream ImageObject(PdfRasterImage pixels)
    {
        int w = pixels.Width, h = pixels.Height;
        var bgra = pixels.Bgra;
        bool gray = true;
        for (int i = 0; i < w * h && gray; i++)
            gray = bgra[i * 4] == bgra[i * 4 + 1] && bgra[i * 4 + 1] == bgra[i * 4 + 2];
        int n = gray ? 1 : 3;
        var samples = new byte[(long)w * h * n];
        for (int i = 0, p = 0; i < w * h; i++)
        {
            if (gray) samples[p++] = bgra[i * 4];
            else { samples[p++] = bgra[i * 4 + 2]; samples[p++] = bgra[i * 4 + 1]; samples[p++] = bgra[i * 4]; }
        }
        return PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Width"] = new PdfInteger(w),
            ["Height"] = new PdfInteger(h),
            ["ColorSpace"] = new PdfName(gray ? "DeviceGray" : "DeviceRGB"),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = new PdfName("FlateDecode"),
            ["DecodeParms"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Predictor"] = new PdfInteger(15), ["Colors"] = new PdfInteger(n), ["BitsPerComponent"] = new PdfInteger(8), ["Columns"] = new PdfInteger(w),
            }),
        }, ContentRedactor.Deflate(PngPredict(samples, w, h, n)));
    }

    /// <summary>PNG row filters (ISO 32000 7.4.4.4), each row with the filter that leaves the smallest sum, as PNG encoders choose.</summary>
    internal static byte[] PngPredict(byte[] samples, int width, int height, int n)
    {
        int row = width * n;
        var output = new byte[(long)(row + 1) * height];
        var candidate = new byte[row];
        var best = new byte[row];
        for (int y = 0; y < height; y++)
        {
            int at = y * row, prior = at - row;
            long bestSum = long.MaxValue;
            int bestType = 0;
            for (int type = 0; type <= 4; type++)
            {
                long sum = 0;
                for (int i = 0; i < row; i++)
                {
                    int x = samples[at + i];
                    int a = i >= n ? samples[at + i - n] : 0;
                    int b = y > 0 ? samples[prior + i] : 0;
                    int c = i >= n && y > 0 ? samples[prior + i - n] : 0;
                    int predicted = type switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => 0,
                    };
                    byte v = (byte)(x - predicted);
                    candidate[i] = v;
                    sum += v < 128 ? v : 256 - v;
                }
                if (sum < bestSum) { bestSum = sum; bestType = type; Array.Copy(candidate, best, row); }
            }
            long o = (long)y * (row + 1);
            output[o] = (byte)bestType;
            Array.Copy(best, 0, output, o + 1, row);
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ------------------------------------------------------------------ transparency in resources



    private bool IsGroupForm(PdfDictionary d) => _r.Resolve(d["Group"]) is PdfDictionary g && _r.Resolve(g["S"]) is PdfName { Value: "Transparency" };

    private static bool NormalBlend(string name) => name is "Normal" or "Compatible";

    private bool BlendIsNormal(PdfObject? bm) => _r.Resolve(bm) switch
    {
        PdfName n => NormalBlend(n.Value),
        PdfArray a => a.Count == 0 || _r.Resolve(a[0]) is not PdfName first || NormalBlend(first.Value),
        _ => true,
    };

    /// <summary>An ExtGState PDF/A-1 does not allow: a soft mask, alpha other than 1, or a blend mode other than Normal.</summary>
    private bool HasTransparencyKeys(PdfDictionary gs) =>
        (_r.Resolve(gs["SMask"]) is { } sm && sm is not PdfName { Value: "None" } && sm is not PdfNull)
        || (_r.Resolve(gs["CA"]) is { } ca && ca.TryGetNumber(out double a) && a != 1.0)
        || (_r.Resolve(gs["ca"]) is { } cb && cb.TryGetNumber(out double b) && b != 1.0)
        || !BlendIsNormal(gs["BM"]);

    /// <summary>An ExtGState without what PDF/A-1 does not allow (the transparency is flattened).</summary>
    private PdfObject Sanitized(PdfObject value)
    {
        if (_r.Resolve(value) is not PdfDictionary gs || !HasTransparencyKeys(gs)) return value;
        var e = new Dictionary<string, PdfObject>(gs.Entries);
        e.Remove("SMask"); e.Remove("CA"); e.Remove("ca"); e.Remove("BM"); e.Remove("AIS");
        return new PdfDictionary(e);
    }

    /// <summary>A pattern whose cell or shading brings transparency of its own (conservatively: its resources do).</summary>
    private bool PatternIsTransparent(PdfObject? entry)
    {
        var pattern = _r.Resolve(entry);
        if (pattern == null) return false;
        if (_patterns.TryGetValue(pattern, out bool known)) return known;
        bool transparent = false;
        var dict = pattern is PdfStream ps ? ps.Dictionary : pattern as PdfDictionary;
        if (dict != null)
        {
            if (_r.Resolve(dict["ExtGState"]) is PdfDictionary own && HasTransparencyKeys(own)) transparent = true;
            if (_r.Resolve(dict["Resources"]) is PdfDictionary res)
            {
                if (_r.Resolve(res["ExtGState"]) is PdfDictionary states)
                    transparent |= states.Entries.Values.Any(v => _r.Resolve(v) is PdfDictionary s && HasTransparencyKeys(s));
                if (_r.Resolve(res["XObject"]) is PdfDictionary xobjects)
                    transparent |= xobjects.Entries.Values.Any(v => _r.Resolve(v) is PdfStream x
                        && (x.Dictionary.GetName("Subtype") == "Image" ? ImageHasSoftMask(x.Dictionary) : IsGroupForm(x.Dictionary)));
                if (_r.Resolve(res["Pattern"]) is PdfDictionary nested && _patterns.Count < 256)
                {
                    _patterns[pattern] = false; // a pattern that names itself
                    transparent |= nested.Entries.Values.Any(PatternIsTransparent);
                }
            }
        }
        _patterns[pattern] = transparent;
        return transparent;
    }

    private bool ImageHasSoftMask(PdfDictionary image) =>
        _r.Resolve(image["SMask"]) is PdfStream || (_r.Resolve(image["SMaskInData"]) is { } sid && sid.TryGetNumber(out double v) && v != 0);

    // ------------------------------------------------------------------ resources of a rewritten content

    /// <summary>What a rewritten content stream draws from its resources, and the objects it adds.</summary>
    private sealed class ResourceUse
    {
        private readonly TransparencyFlattener _f;
        public readonly PdfDictionary Source;
        private readonly Dictionary<string, HashSet<string>> _used = new();
        public readonly Dictionary<string, PdfObject> Added = new();
        private int _names;

        public ResourceUse(TransparencyFlattener f, PdfDictionary source)
        {
            _f = f;
            Source = source;
        }

        public void Use(string category, string name)
        {
            if (!_used.TryGetValue(category, out var set)) _used[category] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(name);
        }

        public void UseAll(ResourceUse other)
        {
            foreach (var (category, names) in other._used)
                foreach (var name in names) Use(category, name);
        }

        public string AddXObject(string prefix, PdfObject value)
        {
            var existing = _f._r.Resolve(Source["XObject"]) as PdfDictionary;
            while (true)
            {
                string name = prefix + (++_names).ToString(CultureInfo.InvariantCulture);
                if (Added.ContainsKey(name) || existing?.ContainsKey(name) == true) continue;
                Added[name] = value;
                return name;
            }
        }

        /// <summary>
        /// The resources: the source's, with what is no longer drawn taken out (images, forms,
        /// graphics states, patterns and shadings) when pruning, ExtGStates without their
        /// transparency when sanitizing, and the added XObjects.
        /// </summary>
        public PdfDictionary Build(bool sanitize, bool prune)
        {
            var f = _f;
            var e = new Dictionary<string, PdfObject>(Source.Entries);
            foreach (string category in new[] { "XObject", "ExtGState", "Pattern", "Shading" })
            {
                var map = f._r.Resolve(Source[category]) as PdfDictionary;
                var names = _used.GetValueOrDefault(category);
                var m = new Dictionary<string, PdfObject>();
                if (map != null)
                    foreach (var (name, value) in map.Entries)
                    {
                        if (prune && names?.Contains(name) != true) continue;
                        m[name] = sanitize && category == "ExtGState" ? f.Sanitized(value) : value;
                    }
                if (category == "XObject") foreach (var (name, value) in Added) m[name] = value;
                if (m.Count > 0) e[category] = new PdfDictionary(m);
                else if (map != null || e.ContainsKey(category)) e.Remove(category);
            }
            return new PdfDictionary(e);
        }

        public bool NeedsSanitizing()
        {
            var f = _f;
            if (_used.GetValueOrDefault("ExtGState") is not { } names || f._r.Resolve(Source["ExtGState"]) is not PdfDictionary map) return false;
            return names.Any(n => f._r.Resolve(map[n]) is PdfDictionary gs && f.HasTransparencyKeys(gs));
        }
    }

    /// <summary>Copies of form XObjects written for a rewrite, one per distinct copy.</summary>
    private sealed class FormStore
    {
        private readonly TransparencyFlattener _f;
        private readonly Dictionary<int, PdfObject> _objects;
        private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

        public FormStore(TransparencyFlattener f, Dictionary<int, PdfObject> objects)
        {
            _f = f;
            _objects = objects;
        }

        public PdfIndirectRef Copy(PdfStream form, int? source, string content, PdfDictionary resources, bool keepGroup)
        {
            var entries = new Dictionary<string, PdfObject>(form.Dictionary.Entries);
            foreach (var drop in new[] { "Length", "Filter", "DecodeParms", "DL", "F", "FFilter", "FDecodeParms" }) entries.Remove(drop);
            if (!keepGroup && entries.Remove("Group") && source is int number) _f._groupForms.Add(number);
            entries["Resources"] = resources;
            entries["Filter"] = new PdfName("FlateDecode");
            byte[] data = Encoding.Latin1.GetBytes(content);
            string key = $"{source}|{keepGroup}|{Convert.ToHexString(SHA256.HashData(data))}|{PageContentWalker.Serialize(resources)}";
            if (_seen.TryGetValue(key, out int existing)) return new PdfIndirectRef(existing);
            int n = _f.Allocate();
            _objects[n] = PdfObjectWriter.NewStream(entries, ContentRedactor.Deflate(data));
            _seen[key] = n;
            return new PdfIndirectRef(n);
        }
    }

    // ------------------------------------------------------------------ the content walker

    private enum Pass { Analyze, Render, Under, Over }

    /// <summary>What a drawing operator becomes in a pass: drawn, kept only as invisible text, or skipped (its state effect kept).</summary>
    private enum Act { Paint, Hide, Skip }

    private sealed class GState
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public PdfRect? Clip;
        public double FillAlpha = 1, StrokeAlpha = 1;
        public bool Blend, SoftMask, FillPattern, StrokePattern;
        public string? FontName;
        public PdfFont? Font;
        public double FontSize = 1, Tc, Tw, Th = 100, TL, Rise;
        public int Tr;
        public double LineWidth = 1, MiterLimit = 10;
        public int LineJoin;
        public GState Clone() => (GState)MemberwiseClone();
    }

    private readonly record struct WalkResult(string Output, bool Changed, bool Painted);

    /// <summary>
    /// One pass over a page's content (and the forms it draws). Every drawing operator gets the next
    /// ordinal, in drawing order, except inside a transparent form (one drawing, one ordinal).
    /// </summary>
    private sealed class Walker
    {
        private readonly TransparencyFlattener _f;
        private readonly Pass _pass;
        private readonly int _split;
        private readonly PdfRect _page;
        private readonly FormStore? _store;
        private int _ordinal, _depth;
        private readonly HashSet<int> _active = new();

        /// <summary>Transparent objects: ordinal, page-space bounds, and whether they show text.</summary>
        public readonly List<(int Ordinal, PdfRect Bounds, bool Text)> Units = new();
        /// <summary>Visible, opaque text.</summary>
        public readonly List<(int Ordinal, PdfRect Bounds)> Texts = new();
        /// <summary>Something PDF/A-1 does not allow is used without drawing anything transparent (an unused alpha, an opaque group).</summary>
        public bool NeedsCleaning;
        public int LastOrdinal => _ordinal;

        public Walker(TransparencyFlattener f, Pass pass, int split, PdfRect page, FormStore? store = null)
        {
            _f = f;
            _pass = pass;
            _split = split;
            _page = page;
            _store = store;
        }

        private bool Final => _pass is Pass.Under or Pass.Over;

        public string Run(byte[] content, PdfDictionary resources, ResourceUse? use = null) =>
            Walk(content, resources, new GState(), hidden: false, use ?? new ResourceUse(_f, resources)).Output;

        private Act Decide(bool hidden, bool transparent, PdfRect? bounds, bool text, out int ordinal)
        {
            ordinal = 0;
            if (hidden) return Act.Hide;
            ordinal = ++_ordinal;
            if (_pass == Pass.Analyze)
            {
                if (bounds is { IsEmpty: false } b)
                {
                    if (transparent) Units.Add((ordinal, b, text));
                    else if (text) Texts.Add((ordinal, b));
                }
                return Act.Paint;
            }
            return _pass switch
            {
                Pass.Render => ordinal > _split ? Act.Skip : Act.Paint,
                Pass.Under => ordinal > _split ? Act.Skip : transparent ? Act.Hide : Act.Paint,
                _ => ordinal > _split ? Act.Paint : Act.Skip,
            };
        }

        private WalkResult Walk(byte[] content, PdfDictionary resources, GState gs, bool hidden, ResourceUse use)
        {
            var output = new List<string>();
            bool changed = false, painted = false;
            var stack = new Stack<GState>();
            PdfMatrix tm = PdfMatrix.Identity, tlm = PdfMatrix.Identity;
            var operands = new List<PdfObject>(8);
            var operandText = new StringBuilder();
            var pathRaw = new StringBuilder();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double cx = 0, cy = 0, sx = 0, sy = 0;
            string? pendingClip = null;

            void Emit(string s) { if (_pass != Pass.Analyze && s.Length > 0) output.Add(s); }
            void Point(double x, double y)
            {
                var p = gs.Ctm.Transform(x, y);
                if (double.IsFinite(p.X) && double.IsFinite(p.Y))
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
            }
            PdfRect? PathBounds() => minX <= maxX ? new PdfRect(minX, minY, maxX - minX, maxY - minY) : null;
            void EndPath()
            {
                if (pendingClip != null && PathBounds() is { } clip) gs.Clip = gs.Clip is { } c ? Intersect(c, clip) : clip;
                pathRaw.Clear();
                pendingClip = null;
                minX = minY = double.MaxValue;
                maxX = maxY = double.MinValue;
            }

            using var src = new MemoryByteSource(content);
            var lexer = new PdfLexer(src, PdfSecurityLimits.Default);
            var parser = new PdfParser(PdfSecurityLimits.Default);
            while (true)
            {
                long tokenPos = src.Position;
                var token = lexer.NextToken();
                if (token.Type == PdfTokenType.EndOfFile) break;
                if (token.Type != PdfTokenType.Keyword || token.TextValue is "true" or "false" or "null")
                {
                    src.Position = tokenPos;
                    var obj = token.Type == PdfTokenType.Keyword
                        ? (token.TextValue == "null" ? PdfNull.Instance : token.TextValue == "true" ? PdfBoolean.True : PdfBoolean.False)
                        : parser.ParseObject(lexer);
                    if (token.Type == PdfTokenType.Keyword) lexer.NextToken();
                    if (obj != null)
                    {
                        operands.Add(obj);
                        operandText.Append(PageContentWalker.Serialize(obj)).Append(' ');
                    }
                    else if (src.Position == tokenPos) src.Position = tokenPos + 1;
                    continue;
                }

                string op = token.TextValue;
                string verbatim = operandText + op;
                switch (op)
                {
                    case "q":
                        stack.Push(gs.Clone());
                        Emit(verbatim);
                        break;
                    case "Q":
                        // A Q without its q would pop the state the copy is wrapped in.
                        if (stack.Count > 0) { gs = stack.Pop(); Emit(verbatim); }
                        else changed = true;
                        break;
                    case "cm" when Numbers(operands, 6) is { } m:
                        gs.Ctm = new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]) * gs.Ctm;
                        Emit(verbatim);
                        break;
                    case "w" when Numbers(operands, 1) is { } lw: gs.LineWidth = lw[0]; Emit(verbatim); break;
                    case "j" when Numbers(operands, 1) is { } lj: gs.LineJoin = (int)lj[0]; Emit(verbatim); break;
                    case "M" when Numbers(operands, 1) is { } ml: gs.MiterLimit = ml[0]; Emit(verbatim); break;
                    case "gs" when operands.Count >= 1 && operands[^1] is PdfName gsName:
                        ApplyExtGState(gs, resources, gsName.Value);
                        use.Use("ExtGState", gsName.Value);
                        Emit(verbatim);
                        break;

                    // ---------------------------------------------------------- colour (patterns may bring transparency)
                    case "scn" or "SCN":
                    {
                        bool fill = op == "scn";
                        if (operands.Count >= 1 && operands[^1] is PdfName pattern)
                        {
                            var entry = (_f._r.Resolve(resources["Pattern"]) as PdfDictionary)?[pattern.Value];
                            bool transparent = _f.PatternIsTransparent(entry);
                            if (fill) gs.FillPattern = transparent; else gs.StrokePattern = transparent;
                            if (transparent && _pass == Pass.Analyze) NeedsCleaning = true;
                            // Whatever paints with it is flattened: in the final copies the colour is not set at all.
                            if (transparent && Final) { changed = true; break; }
                            use.Use("Pattern", pattern.Value);
                        }
                        else if (fill) gs.FillPattern = false;
                        else gs.StrokePattern = false;
                        Emit(verbatim);
                        break;
                    }
                    case "g" or "rg" or "k" or "cs" or "sc":
                        gs.FillPattern = false;
                        Emit(verbatim);
                        break;
                    case "G" or "RG" or "K" or "CS" or "SC":
                        gs.StrokePattern = false;
                        Emit(verbatim);
                        break;

                    // ---------------------------------------------------------- text
                    case "BT":
                        tm = tlm = PdfMatrix.Identity;
                        Emit(verbatim);
                        break;
                    case "Tf" when operands.Count >= 2 && operands[0] is PdfName fn && operands[1].TryGetNumber(out double fsize):
                        gs.FontName = fn.Value;
                        gs.FontSize = fsize;
                        gs.Font = null;
                        Emit(verbatim);
                        break;
                    case "Tc" when Numbers(operands, 1) is { } tc: gs.Tc = tc[0]; Emit(verbatim); break;
                    case "Tw" when Numbers(operands, 1) is { } tw: gs.Tw = tw[0]; Emit(verbatim); break;
                    case "Tz" when Numbers(operands, 1) is { } tz: gs.Th = tz[0]; Emit(verbatim); break;
                    case "TL" when Numbers(operands, 1) is { } tl: gs.TL = tl[0]; Emit(verbatim); break;
                    case "Ts" when Numbers(operands, 1) is { } ts: gs.Rise = ts[0]; Emit(verbatim); break;
                    case "Tr" when Numbers(operands, 1) is { } tr: gs.Tr = (int)tr[0]; Emit(verbatim); break;
                    case "Td" when Numbers(operands, 2) is { } td:
                        tlm = PdfMatrix.CreateTranslation(td[0], td[1]) * tlm; tm = tlm;
                        Emit(verbatim);
                        break;
                    case "TD" when Numbers(operands, 2) is { } tdd:
                        gs.TL = -tdd[1];
                        tlm = PdfMatrix.CreateTranslation(tdd[0], tdd[1]) * tlm; tm = tlm;
                        Emit(verbatim);
                        break;
                    case "Tm" when Numbers(operands, 6) is { } tmv:
                        tm = tlm = new PdfMatrix(tmv[0], tmv[1], tmv[2], tmv[3], tmv[4], tmv[5]);
                        Emit(verbatim);
                        break;
                    case "T*":
                        tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                        Emit(verbatim);
                        break;
                    case "Tj" when operands.Count >= 1 && operands[^1] is PdfString s:
                        Text(new PdfObject[] { s }, string.Empty, verbatim);
                        break;
                    case "TJ" when operands.Count >= 1 && operands[^1] is PdfArray arr:
                        Text(arr.Items, string.Empty, verbatim);
                        break;
                    case "'" when operands.Count >= 1 && operands[^1] is PdfString s1:
                        tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                        Text(new PdfObject[] { s1 }, "T* ", verbatim);
                        break;
                    case "\"" when operands.Count >= 3 && operands[2] is PdfString s2 && operands[0].TryGetNumber(out double aw) && operands[1].TryGetNumber(out double ac):
                        gs.Tw = aw; gs.Tc = ac;
                        tlm = PdfMatrix.CreateTranslation(0, -gs.TL) * tlm; tm = tlm;
                        Text(new PdfObject[] { s2 }, $"{F(aw)} Tw {F(ac)} Tc T* ", verbatim);
                        break;

                    // ---------------------------------------------------------- paths
                    case "m" when Numbers(operands, 2) is { } mv:
                        cx = sx = mv[0]; cy = sy = mv[1];
                        Point(cx, cy);
                        pathRaw.Append(verbatim).Append('\n');
                        break;
                    case "l" when Numbers(operands, 2) is { } lv:
                        cx = lv[0]; cy = lv[1];
                        Point(cx, cy);
                        pathRaw.Append(verbatim).Append('\n');
                        break;
                    case "c" when Numbers(operands, 6) is { } c6:
                        Point(c6[0], c6[1]); Point(c6[2], c6[3]); Point(c6[4], c6[5]);
                        cx = c6[4]; cy = c6[5];
                        pathRaw.Append(verbatim).Append('\n');
                        break;
                    case "v" or "y" when Numbers(operands, 4) is { } c4:
                        Point(c4[0], c4[1]); Point(c4[2], c4[3]);
                        cx = c4[2]; cy = c4[3];
                        pathRaw.Append(verbatim).Append('\n');
                        break;
                    case "h":
                        cx = sx; cy = sy;
                        pathRaw.Append(verbatim).Append('\n');
                        break;
                    case "re" when Numbers(operands, 4) is { } rv:
                        Point(rv[0], rv[1]); Point(rv[0] + rv[2], rv[1]); Point(rv[0] + rv[2], rv[1] + rv[3]); Point(rv[0], rv[1] + rv[3]);
                        cx = sx = rv[0]; cy = sy = rv[1];
                        pathRaw.Append(verbatim).Append('\n');
                        break;
                    case "W" or "W*":
                        pendingClip = op;
                        break;
                    case "n":
                        Emit(pathRaw + (pendingClip != null ? pendingClip + " " : string.Empty) + op);
                        EndPath();
                        break;
                    case "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*":
                    {
                        bool fills = op is not ("S" or "s"), strokes = op is "S" or "s" or "B" or "B*" or "b" or "b*";
                        bool transparent = gs.Blend || gs.SoftMask || (fills && (gs.FillAlpha < 1 || gs.FillPattern)) || (strokes && (gs.StrokeAlpha < 1 || gs.StrokePattern));
                        var bounds = PathBounds() is { } pb ? Clipped(gs, strokes ? Grow(pb, StrokeGrowth(gs)) : pb) : (PdfRect?)null;
                        var act = Decide(hidden, transparent, bounds, text: false, out _);
                        string clip = pendingClip != null ? pendingClip + " " : string.Empty;
                        if (act == Act.Paint) { Emit(pathRaw + clip + op); painted = true; }
                        else
                        {
                            changed = true;
                            if (pendingClip != null) Emit(pathRaw + clip + "n");
                        }
                        EndPath();
                        break;
                    }

                    // ---------------------------------------------------------- images, forms, shadings
                    case "Do" when operands.Count >= 1 && operands[^1] is PdfName xname:
                    {
                        var entry = (_f._r.Resolve(resources["XObject"]) as PdfDictionary)?[xname.Value];
                        var xobject = _f._r.Resolve(entry) as PdfStream;
                        string? subtype = xobject?.Dictionary.GetName("Subtype");
                        if (subtype == "Image")
                        {
                            var d = xobject!.Dictionary;
                            bool stencil = _f._r.Resolve(d["ImageMask"]) is PdfBoolean { Value: true };
                            bool transparent = gs.Blend || gs.SoftMask || gs.FillAlpha < 1 || _f.ImageHasSoftMask(d) || (stencil && gs.FillPattern);
                            var act = Decide(hidden, transparent, Clipped(gs, gs.Ctm.Transform(new PdfRect(0, 0, 1, 1))), text: false, out _);
                            if (act == Act.Paint) { Emit(verbatim); use.Use("XObject", xname.Value); painted = true; }
                            else changed = true;
                        }
                        else if (subtype == "Form")
                        {
                            var r = Form(xobject!, entry as PdfIndirectRef, xname.Value, verbatim, gs, resources, hidden, use, Emit);
                            changed |= r.Changed;
                            painted |= r.Painted;
                        }
                        else
                        {
                            Emit(verbatim);
                            use.Use("XObject", xname.Value);
                        }
                        break;
                    }
                    case "sh" when operands.Count >= 1 && operands[^1] is PdfName shName:
                    {
                        bool transparent = gs.Blend || gs.SoftMask || gs.FillAlpha < 1;
                        var act = Decide(hidden, transparent, Clipped(gs, _page), text: false, out _);
                        if (act == Act.Paint) { Emit(verbatim); use.Use("Shading", shName.Value); painted = true; }
                        else changed = true;
                        break;
                    }
                    case "BI":
                    {
                        var (raw, _, _) = PageContentWalker.InlineImage(src, lexer, parser);
                        if (raw == null) break;
                        bool transparent = gs.Blend || gs.SoftMask || gs.FillAlpha < 1 || gs.FillPattern;
                        var act = Decide(hidden, transparent, Clipped(gs, gs.Ctm.Transform(new PdfRect(0, 0, 1, 1))), text: false, out _);
                        if (act == Act.Paint) { Emit(raw); painted = true; }
                        else changed = true;
                        break;
                    }

                    default:
                        Emit(verbatim);
                        break;
                }
                operands.Clear();
                operandText.Clear();
            }
            for (int i = 0; i < stack.Count; i++) Emit("Q");
            return new WalkResult(string.Join("\n", output) + "\n", changed, painted);

            // A text-showing operator: drawn, kept as invisible text, or reduced to its advance.
            void Text(IReadOnlyList<PdfObject> items, string prefix, string verbatim)
            {
                var font = gs.Font;
                if (font == null && gs.FontName != null)
                {
                    try { font = gs.Font = _f._fonts.ResolveFont(gs.FontName, resources); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { font = null; }
                }
                var (bounds, advanceOnly) = Glyphs(gs, font, ref tm, items, prefix);
                bool fills = gs.Tr is 0 or 2 or 4 or 6, strokes = gs.Tr is 1 or 2 or 5 or 6;
                bool visible = fills || strokes;
                bool transparent = visible && (gs.Blend || gs.SoftMask || (fills && (gs.FillAlpha < 1 || gs.FillPattern)) || (strokes && (gs.StrokeAlpha < 1 || gs.StrokePattern)));
                var b = bounds is { } gb ? Clipped(gs, strokes ? Grow(gb, StrokeGrowth(gs)) : gb) : (PdfRect?)null;
                var act = Decide(hidden, transparent, visible ? b : null, text: visible, out _);
                string invisible = $"{(gs.Tr >= 4 ? 7 : 3)} Tr {verbatim} {gs.Tr} Tr";
                switch (act)
                {
                    case Act.Paint:
                        Emit(verbatim);
                        painted = true;
                        break;
                    case Act.Hide:
                        changed = true;
                        if (gs.Tr is 3 or 7) { Emit(verbatim); painted = true; }
                        else if (font?.IsType3 == true && gs.Tr < 4) Emit(advanceOnly ?? invisible); // Type 3 glyphs ignore the rendering mode
                        else { Emit(invisible); painted = true; if (_pass == Pass.Under) _f._hiddenText++; }
                        break;
                    default:
                        changed = true;
                        // Clipping text keeps its clip; other text keeps only its advance (it is in the other copy).
                        if (gs.Tr == 7) Emit(verbatim);
                        else if (gs.Tr >= 4) Emit($"7 Tr {verbatim} {gs.Tr} Tr");
                        else Emit(advanceOnly ?? invisible);
                        break;
                }


            }
        }



        private PdfRect? Clipped(GState gs, PdfRect bounds)
        {
            var b = gs.Clip is { } c ? Intersect(bounds, c) : bounds;
            return b.IsEmpty && (b.Width < 0 || b.Height < 0) ? null : b;
        }

        /// <summary>How far a stroke reaches beyond its path, in page space (joins, caps and hairlines included).</summary>
        private static double StrokeGrowth(GState gs)
        {
            var m = gs.Ctm;
            double scale = Math.Sqrt(m.A * m.A + m.B * m.B + m.C * m.C + m.D * m.D);
            double half = Math.Max(gs.LineWidth, 0) / 2 * scale;
            double factor = gs.LineJoin == 0 ? Math.Clamp(gs.MiterLimit, 1.5, 10) : 1.5;
            return Math.Max(half * factor, 1);
        }

        /// <summary>
        /// The glyphs' page-space bounds (each glyph's advance by the font's height, generously), the
        /// text matrix advanced past them, and the operator reduced to its advance (null when that
        /// cannot be written).
        /// </summary>
        private (PdfRect? Bounds, string? AdvanceOnly) Glyphs(GState gs, PdfFont? font, ref PdfMatrix tm, IReadOnlyList<PdfObject> items, string prefix)
        {
            double fs = gs.FontSize, th = gs.Th / 100.0;
            bool vertical = font?.IsVertical ?? false;
            // The vertical band and side bearings, in em.
            double lo = Math.Min(font?.Descent ?? -0.3, -0.25), hi = Math.Max(font?.Ascent ?? 1.0, 0.95), left = -0.1, right = 0.1;
            if (font?.FontBBox is { } fb && fb.Width > 0 && fb.Height > 0)
            {
                var em = font.IsType3 ? font.FontMatrix.Transform(fb) : new PdfRect(fb.X / 1000, fb.Y / 1000, fb.Width / 1000, fb.Height / 1000);
                lo = Math.Min(lo, Math.Max(em.Y, -2));
                hi = Math.Max(hi, Math.Min(em.Bottom, 3));
                left = Math.Min(left, Math.Max(em.X, -2));
                right = Math.Max(right, Math.Min(em.Right, 3));
            }
            var textToPage = tm * gs.Ctm;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Box(double x0, double y0, double x1, double y1)
            {
                foreach (var (px, py) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) })
                {
                    var p = textToPage.Transform(px, py);
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
            }
            double x = 0, y = 0;
            foreach (var item in items)
            {
                if (item is PdfString s)
                {
                    var bytes = s.RawBytes.Span;
                    int pos = 0;
                    while (pos < bytes.Length)
                    {
                        int code, cid, consumed;
                        if (font != null) { consumed = font.ReadCode(bytes, pos, out code, out cid); if (consumed <= 0) consumed = 1; }
                        else { code = cid = bytes[pos]; consumed = 1; }
                        pos += consumed;
                        double w0 = (font?.GetGlyphWidth(font.IsComposite ? cid : code) ?? 500) / 1000.0;
                        double tw = (font?.IsWordSpaceCode(code, consumed) ?? code == 32) ? gs.Tw : 0;
                        if (vertical)
                        {
                            var vm = font!.GetVerticalMetrics(cid);
                            double ox = -vm.VX / 1000.0 * fs * th, oy = y - vm.VY / 1000.0 * fs;
                            double advY = vm.W1y / 1000.0 * fs + gs.Tc + tw;
                            Box(ox + left * fs * th, oy + gs.Rise + Math.Min(advY, 0) - 0.1 * fs, ox + (Math.Max(w0, 1) + right) * fs * th, oy + gs.Rise + Math.Max(advY, 0) + 0.1 * fs);
                            y += advY;
                        }
                        else
                        {
                            double adv = (w0 * fs + gs.Tc + tw) * th;
                            Box(x + left * fs * th, gs.Rise + lo * fs, x + (Math.Max(w0, 0) + right) * fs * th, gs.Rise + hi * fs);
                            x += adv;
                        }
                    }
                }
                else if (item.TryGetNumber(out double adj) && double.IsFinite(adj))
                {
                    if (vertical) y -= adj / 1000.0 * fs; else x -= adj / 1000.0 * fs * th;
                }
            }
            tm = PdfMatrix.CreateTranslation(x, y) * tm;
            double scale = vertical ? fs : fs * th;
            string? advanceOnly = font == null || Math.Abs(scale) < 1e-9 ? null
                : $"{prefix}[{F(-(vertical ? y : x) * 1000.0 / scale)}] TJ";
            var bounds = minX <= maxX ? new PdfRect(minX, minY, maxX - minX, maxY - minY) : (PdfRect?)null;
            return (bounds, advanceOnly);
        }

        private void ApplyExtGState(GState gs, PdfDictionary resources, string name)
        {
            if (_f._r.Resolve((_f._r.Resolve(resources["ExtGState"]) as PdfDictionary)?[name]) is not PdfDictionary d) return;
            var r = _f._r;
            if (r.Resolve(d["CA"]) is { } ca && ca.TryGetNumber(out double a)) gs.StrokeAlpha = Math.Clamp(a, 0, 1);
            if (r.Resolve(d["ca"]) is { } cb && cb.TryGetNumber(out double b)) gs.FillAlpha = Math.Clamp(b, 0, 1);
            if (d.ContainsKey("BM")) gs.Blend = !_f.BlendIsNormal(d["BM"]);
            if (d.ContainsKey("SMask")) gs.SoftMask = r.Resolve(d["SMask"]) is { } sm && sm is not PdfName { Value: "None" } && sm is not PdfNull;
            if (r.Resolve(d["LW"]) is { } lw && lw.TryGetNumber(out double w)) gs.LineWidth = w;
            if (r.Resolve(d["LJ"]) is { } lj && lj.TryGetNumber(out double j)) gs.LineJoin = (int)j;
            if (r.Resolve(d["ML"]) is { } ml && ml.TryGetNumber(out double mlv)) gs.MiterLimit = mlv;
            if (_pass == Pass.Analyze && _f.HasTransparencyKeys(d)) NeedsCleaning = true;
        }

        /// <summary>
        /// A form XObject drawn here. A transparency group drawn with alpha, a blend mode or a soft
        /// mask is one transparent object; any other form is walked, its operators counted with the
        /// page's, and copied when its drawing changes in this pass.
        /// </summary>
        private WalkResult Form(PdfStream form, PdfIndirectRef? reference, string name, string verbatim, GState gs, PdfDictionary resources,
            bool hidden, ResourceUse use, Action<string> emit)
        {
            var dict = form.Dictionary;
            var r = _f._r;
            bool group = _f.IsGroupForm(dict);
            if (group && _pass == Pass.Analyze) NeedsCleaning = true;
            var matrix = r.Resolve(dict["Matrix"]) is PdfArray { Count: 6 } ma && ma.Items.All(i => r.Resolve(i)?.TryGetNumber(out _) == true)
                ? new PdfMatrix(N(r.Resolve(ma[0])), N(r.Resolve(ma[1])), N(r.Resolve(ma[2])), N(r.Resolve(ma[3])), N(r.Resolve(ma[4])), N(r.Resolve(ma[5])))
                : PdfMatrix.Identity;
            var formCtm = matrix * gs.Ctm;
            PdfRect? bbox = r.Resolve(dict["BBox"]) is PdfArray { Count: 4 } ba && ba.Items.All(i => r.Resolve(i)?.TryGetNumber(out _) == true)
                ? RectOf(N(r.Resolve(ba[0])), N(r.Resolve(ba[1])), N(r.Resolve(ba[2])), N(r.Resolve(ba[3])))
                : null;
            var pageBounds = Clipped(gs, bbox is { } bb ? formCtm.Transform(bb) : _page);

            byte[]? content = null;
            bool recursive = reference != null && _active.Contains(reference.ObjectNumber);
            if (!recursive && _depth < 12)
            {
                try { content = _f._decoder.DecodeStream(form); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { content = null; }
            }
            var own = r.Resolve(dict["Resources"]) as PdfDictionary;
            var formResources = own ?? resources;
            var inner = gs.Clone();
            inner.Ctm = formCtm;
            if (bbox is { } clipBox) inner.Clip = pageBounds ?? formCtm.Transform(clipBox);

            // Unreadable (or drawing itself): one opaque drawing, kept as it is.
            if (content == null)
            {
                var act0 = Decide(hidden, false, pageBounds, text: false, out _);
                if (act0 == Act.Paint) { emit(verbatim); use.Use("XObject", name); return new WalkResult(string.Empty, false, true); }
                return new WalkResult(string.Empty, true, false);
            }

            bool unit = !hidden && group && (gs.FillAlpha < 1 || gs.Blend || gs.SoftMask);
            if (group)
            {
                // Elements of a transparency group start without the alpha, blend mode and soft mask of the Do (ISO 32000 11.6.6).
                inner.FillAlpha = inner.StrokeAlpha = 1;
                inner.Blend = inner.SoftMask = false;
            }

            if (unit)
            {
                bool text = _pass == Pass.Analyze && HasText(content);
                var act = Decide(false, true, pageBounds, text, out _);
                if (act == Act.Paint) { emit(verbatim); use.Use("XObject", name); return new WalkResult(string.Empty, false, true); }
                if (act == Act.Skip) return new WalkResult(string.Empty, true, false);
                // Under the images: only its text, invisible, in place.
                return Copy(form, reference, inner, content, formResources, own, hidden: true, emit, use);
            }

            if (_pass == Pass.Analyze)
            {
                Enter(reference);
                try { Walk(content, formResources, inner, hidden, new ResourceUse(_f, formResources)); }
                finally { Leave(reference); }
                return new WalkResult(string.Empty, false, true);
            }
            return Copy(form, reference, inner, content, formResources, own, hidden, emit, use, verbatim, name, group);
        }

        private WalkResult Copy(PdfStream form, PdfIndirectRef? reference, GState inner, byte[] content, PdfDictionary formResources, PdfDictionary? own,
            bool hidden, Action<string> emit, ResourceUse use, string? verbatim = null, string? name = null, bool group = false)
        {
            var innerUse = new ResourceUse(_f, formResources);
            WalkResult result;
            Enter(reference);
            try { result = Walk(content, formResources, inner, hidden, innerUse); }
            finally { Leave(reference); }
            if (!result.Painted) return new WalkResult(string.Empty, true, false); // nothing of it shows in this pass
            bool needsCopy = result.Changed || hidden || verbatim == null || (Final && (group || innerUse.NeedsSanitizing()));
            if (!needsCopy)
            {
                emit(verbatim!);
                use.Use("XObject", name!);
                if (own == null) use.UseAll(innerUse); // it draws from the resources it is drawn with
                return new WalkResult(string.Empty, false, true);
            }
            var copy = _store!.Copy(form, reference?.ObjectNumber, result.Output, innerUse.Build(sanitize: Final, prune: Final), keepGroup: !Final);
            emit("/" + use.AddXObject("FlFm", copy) + " Do");
            return new WalkResult(string.Empty, true, true);
        }

        private void Enter(PdfIndirectRef? reference)
        {
            _depth++;
            if (reference != null) _active.Add(reference.ObjectNumber);
        }

        private void Leave(PdfIndirectRef? reference)
        {
            _depth--;
            if (reference != null) _active.Remove(reference.ObjectNumber);
        }

        /// <summary>Whether a content stream shows text (or draws forms, which may).</summary>
        private static bool HasText(byte[] content)
        {
            var s = Encoding.Latin1.GetString(content);
            return s.Contains("Tj", StringComparison.Ordinal) || s.Contains("TJ", StringComparison.Ordinal) || s.Contains(" Do", StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static double N(PdfObject? o) => o != null && o.TryGetNumber(out double v) ? v : 0;

    private static double[]? Numbers(List<PdfObject> operands, int n)
    {
        if (operands.Count < n) return null;
        var v = new double[n];
        for (int i = 0; i < n; i++)
            if (!operands[operands.Count - n + i].TryGetNumber(out v[i]) || !double.IsFinite(v[i])) return null;
        return v;
    }

    private static PdfRect RectOf(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));

    private static PdfRect Intersect(PdfRect a, PdfRect b)
    {
        double x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        return x1 > x0 && y1 > y0 ? new PdfRect(x0, y0, x1 - x0, y1 - y0) : new PdfRect(x0, y0, 0, 0);
    }

    private static PdfRect Union(PdfRect a, PdfRect b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), x1 = Math.Max(a.Right, b.Right), y1 = Math.Max(a.Bottom, b.Bottom);
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static PdfRect Grow(PdfRect r, double by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static bool Contains(PdfRect outer, PdfRect inner, double tolerance) =>
        inner.X >= outer.X - tolerance && inner.Y >= outer.Y - tolerance && inner.Right <= outer.Right + tolerance && inner.Bottom <= outer.Bottom + tolerance;

    private static PdfArray RectArray(PdfRect r) => new(new PdfObject[] { new PdfReal(r.X), new PdfReal(r.Y), new PdfReal(r.Right), new PdfReal(r.Bottom) });

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 6));
}
