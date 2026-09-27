using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Streams;
using static PdfEngine.Vector.Editing.TextBlocks;

namespace PdfEngine.Vector.Editing;

/// <summary>
/// Edits what a page shows: its text paragraph by paragraph and its images, the way a layout
/// program would. Edited text keeps each character's font, size and colour and reflows within
/// its paragraph's width and alignment; lines the edit does not reach keep their exact glyphs.
/// Characters the document's fonts do not have are drawn with a matching installed font,
/// embedded as a subset. Images are deleted, moved, resized, turned or replaced where they are
/// drawn, so what covers them and what clips them stays as it was. Written as an incremental update.
/// </summary>
public static class PdfContentEditor
{
    /// <summary>The page's paragraphs and images, with the ids <see cref="Apply"/> takes.</summary>
    public static PdfEditablePage Read(PdfVectorDocument document, int pageNumber, SystemFontCatalog? fonts = null)
    {
        var (walker, blocks) = Load(document, pageNumber);
        var texts = blocks.Where(b => !b.IsShadow).Select(b => Describe(b, fonts)).ToList();
        var images = walker.Images.Select(i => new PdfEditableImage(i.Index, i.Placement.Transform(new PdfRect(0, 0, 1, 1)), i.Placement, i.PixelWidth, i.PixelHeight)).ToList();
        return new PdfEditablePage(pageNumber, texts, images);
    }

    public static PdfContentEditResult Apply(PdfVectorDocument document, byte[] original, IReadOnlyList<PdfContentEdit> edits, PdfContentEditOptions? options = null)
    {
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (r.Resolve(trailer["Root"]) is not PdfDictionary root) throw new InvalidOperationException("The document has no catalog.");
        var pageObjects = PdfRedactor.PageObjectNumbers(root, r);
        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(document);
        int Allocate() => next++;
        var warnings = new List<string>();
        var embedded = new List<string>();
        var catalog = options?.Fonts ?? SystemFontCatalog.Installed;

        foreach (var page in edits.GroupBy(e => e.PageNumber).OrderBy(g => g.Key))
        {
            if (page.Key < 1 || page.Key > pageObjects.Count)
            {
                warnings.Add($"There is no page {page.Key}.");
                continue;
            }
            EditPage(document, page.Key, pageObjects[page.Key - 1], page.ToList(), catalog, Allocate, objects, warnings, embedded);
        }
        byte[] bytes = objects.Count == 0 ? original : PdfIncrementalWriter.Append(original, document, objects);
        return new PdfContentEditResult { Bytes = bytes, Warnings = warnings.Distinct().ToList(), EmbeddedFonts = embedded.Distinct().ToList() };
    }

    // ------------------------------------------------------------------ reading

    private static (PageContentWalker Walker, List<TextBlock> Blocks) Load(PdfVectorDocument document, int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > document.PageTree.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var node = document.PageTree.Pages[pageNumber - 1];
        var decoder = new PdfStreamDecoder(null, document.Resolver.Resolve);
        var content = new MemoryStream();
        try
        {
            foreach (var stream in node.Contents)
            {
                content.Write(decoder.DecodeStream(stream));
                content.WriteByte((byte)'\n');
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"Page {pageNumber}'s content cannot be read ({ex.Message}), so it cannot be edited.", ex);
        }
        var walker = new PageContentWalker(document, content.ToArray(), node.Resources);
        walker.Read();
        return (walker, TextBlocks.Build(walker.Glyphs));
    }

    private static PdfEditableText Describe(TextBlock b, SystemFontCatalog? fonts)
    {
        var first = b.Lines[0].Glyphs.FirstOrDefault(g => g.Text.Trim().Length > 0) ?? b.Lines[0].Glyphs[0];
        var font = first.Style.Font;
        string name = SystemFontCatalog.StripSubset(font?.PostScriptName ?? font?.BaseFont ?? first.Style.FontResource);
        bool bold = (font?.IsBold ?? false) || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = (font?.IsItalic ?? false) || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        string? family = fonts?.Match(name, bold, italic, font?.IsSerif ?? false, font?.IsFixedPitch ?? false)?.Family;
        return new PdfEditableText(b.Id, b.Text, b.Bounds, b.LineBounds, name, family, b.Lines[0].Size, bold, italic, first.Style.FillRgb,
            b.Alignment, b.Vertical ? Math.Atan2(b.V.Y, b.V.X) * 180 / Math.PI : Math.Atan2(b.U.Y, b.U.X) * 180 / Math.PI, b.Pitch, b.Vertical);
    }

    // ------------------------------------------------------------------ editing a page

    private static void EditPage(PdfVectorDocument document, int pageNumber, int pageObject, List<PdfContentEdit> edits, SystemFontCatalog catalog,
        Func<int> allocate, Dictionary<int, PdfObject> objects, List<string> warnings, List<string> embedded)
    {
        var r = document.Resolver;
        var node = document.PageTree.Pages[pageNumber - 1];
        var (walker, blocks) = Load(document, pageNumber);
        var session = new PageSession(node.Resources, r, catalog, allocate, objects, warnings);
        session.Reserve(walker.FormResourceNames);
        var removed = new HashSet<(int, int)>();
        var output = new List<OutGlyph>();
        var inPlace = new Dictionary<int, List<OutGlyph>>();
        var imageChanges = new Dictionary<int, ImageChange>();
        var additions = new StringBuilder();

        // ---- paragraphs (a shadowed paragraph's shadow gets the same edit)
        edits = edits.SelectMany(e => e switch
        {
            PdfReplaceText rt when rt.TextId >= 0 && rt.TextId < blocks.Count => blocks[rt.TextId].Twins.Select(t => (PdfContentEdit)(rt with { TextId = t.Id })).Prepend(e),
            PdfTransformContent { Target: PdfEditTarget.Text } tc when tc.Id >= 0 && tc.Id < blocks.Count => blocks[tc.Id].Twins.Select(t => (PdfContentEdit)(tc with { Id = t.Id })).Prepend(e),
            PdfDeleteContent { Target: PdfEditTarget.Text } dc when dc.Id >= 0 && dc.Id < blocks.Count => blocks[dc.Id].Twins.Select(t => (PdfContentEdit)(dc with { Id = t.Id })).Prepend(e),
            _ => new[] { e },
        }).ToList();
        foreach (var group in edits.Where(e => e is PdfReplaceText || (e is PdfTransformContent t && t.Target == PdfEditTarget.Text) || (e is PdfDeleteContent d && d.Target == PdfEditTarget.Text))
                     .GroupBy(e => e switch { PdfReplaceText rt => rt.TextId, PdfTransformContent tc => tc.Id, PdfDeleteContent dc => dc.Id, _ => -1 }))
        {
            if (group.Key < 0 || group.Key >= blocks.Count)
            {
                warnings.Add($"Page {pageNumber} has no paragraph {group.Key}.");
                continue;
            }
            var block = blocks[group.Key];
            var all = block.Lines.SelectMany(l => l.Glyphs).ToList();
            string? newText = group.OfType<PdfReplaceText>().LastOrDefault()?.NewText;
            if (group.OfType<PdfDeleteContent>().Any() || (newText != null && newText.Trim().Length == 0))
            {
                foreach (var g in all) removed.Add((g.Op, g.Index));
                continue;
            }
            var gone = new HashSet<ContentGlyph>();
            var produced = new List<OutGlyph>();
            if (newText != null)
            {
                var layout = new BlockLayout(block, session).Layout(newText);
                gone = layout.Gone;
                produced = layout.Output;
            }
            var transforms = group.OfType<PdfTransformContent>().ToList();
            if (transforms.Count > 0)
            {
                var t = transforms.Aggregate(PdfMatrix.Identity, (m, e) => m * e.Transform);
                // Everything is drawn again, moved: the lines the edit left alone too.
                foreach (var g in all.Where(g => !gone.Contains(g)))
                {
                    gone.Add(g);
                    produced.Add(OutGlyph.From(g));
                }
                foreach (var o in produced) o.Matrix *= t;
            }
            foreach (var g in gone) removed.Add((g.Op, g.Index));
            // Each glyph drawn where the text it replaces was drawn: under and over what that was, clipped as it was.
            int last = (gone.Count > 0 ? gone : (IEnumerable<ContentGlyph>)all).Max(g => g.Op);
            foreach (var o in produced)
            {
                int after = o.After >= 0 ? o.After : last;
                if (!inPlace.TryGetValue(after, out var list)) inPlace[after] = list = new List<OutGlyph>();
                list.Add(o);
            }
        }

        // ---- images
        foreach (var group in edits.Where(e => e is PdfReplaceImage || (e is PdfTransformContent t && t.Target == PdfEditTarget.Image) || (e is PdfDeleteContent d && d.Target == PdfEditTarget.Image))
                     .GroupBy(e => e switch { PdfReplaceImage ri => ri.ImageId, PdfTransformContent tc => tc.Id, PdfDeleteContent dc => dc.Id, _ => -1 }))
        {
            if (group.Key < 0 || group.Key >= walker.Images.Count)
            {
                warnings.Add($"Page {pageNumber} has no image {group.Key}.");
                continue;
            }
            if (group.OfType<PdfDeleteContent>().Any())
            {
                imageChanges[group.Key] = new ImageDeleted();
                continue;
            }
            var image = walker.Images[group.Key];
            var placement = image.Placement;
            if (!placement.TryInvert(out var inverse))
            {
                warnings.Add($"Image {group.Key} on page {pageNumber} has no area, so it was left as it is.");
                continue;
            }
            var x = group.OfType<PdfTransformContent>().Aggregate(PdfMatrix.Identity, (m, e) => m * e.Transform);
            string? name = null;
            var fit = PdfMatrix.Identity;
            if (group.OfType<PdfReplaceImage>().LastOrDefault() is { } replace)
            {
                name = session.AddImage(replace.Image);
                double oldW = Math.Sqrt(placement.A * placement.A + placement.B * placement.B), oldH = Math.Sqrt(placement.C * placement.C + placement.D * placement.D);
                double oldRatio = oldW / Math.Max(oldH, 1e-9), newRatio = replace.Image.Width / (double)Math.Max(replace.Image.Height, 1);
                double sx = newRatio > oldRatio ? 1 : newRatio / oldRatio, sy = newRatio > oldRatio ? oldRatio / newRatio : 1;
                fit = new PdfMatrix(sx, 0, 0, sy, (1 - sx) / 2, (1 - sy) / 2);
            }
            // The new CTM is fit × placement × X; the cm put before the image is that times the old CTM's inverse.
            var cm = fit * placement * x * inverse;
            imageChanges[group.Key] = new ImageRedrawn(Mat(cm), name);
        }

        // ---- new text and images, drawn over the page
        foreach (var add in edits.OfType<PdfAddText>())
            output.AddRange(session.AddText(add));
        foreach (var add in edits.OfType<PdfAddImage>())
        {
            string name = session.AddImage(add.Image);
            var b = add.Bounds;
            var m = new PdfMatrix(b.Width, 0, 0, b.Height, b.X, b.Y);
            if (Math.Abs(add.AngleDegrees) > 1e-9)
            {
                double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
                m = m * PdfMatrix.CreateTranslation(-cx, -cy) * PdfMatrix.CreateRotation(add.AngleDegrees * Math.PI / 180) * PdfMatrix.CreateTranslation(cx, cy);
            }
            additions.Append("q ").Append(Mat(m)).Append(" cm /").Append(PdfObjectWriter.EscapeName(name)).Append(" Do Q\n");
        }

        if (removed.Count == 0 && output.Count == 0 && inPlace.Count == 0 && imageChanges.Count == 0 && additions.Length == 0) return;

        var insertions = inPlace.ToDictionary(e => e.Key, e =>
        {
            var content = new StringBuilder();
            GlyphEmitter.Emit(content, e.Value, inPlace: true);
            return content.ToString();
        });
        // Fonts are written before the rewrite, so copies of edited form XObjects can name them too.
        var forms = new FormCopies { Allocate = allocate, Objects = objects, Fonts = session.WriteFonts(embedded), XObjects = session.NewXObjects };
        byte[] rewritten = walker.Rewrite(removed, imageChanges, insertions, forms);
        session.AddXObjects(forms.PageXObjects);
        if (walker.AlternateTextsRemoved > 0)
            warnings.Add("Replacement text (/ActualText) of edited words was removed, so it no longer contradicts what the page shows.");
        var sb = new StringBuilder("q\n").Append(Encoding.Latin1.GetString(rewritten)).Append("Q\n");
        GlyphEmitter.Emit(sb, output);
        sb.Append(additions);

        int contentObj = allocate();
        objects[contentObj] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") },
            ContentRedactor.Deflate(Encoding.Latin1.GetBytes(sb.ToString())));
        var pageDict = (PdfDictionary)r.Resolve(pageObject)!;
        objects[pageObject] = new PdfDictionary(new Dictionary<string, PdfObject>(pageDict.Entries)
        {
            ["Contents"] = new PdfIndirectRef(contentObj),
            ["Resources"] = session.Finish(embedded),
        });
    }

    internal static string Mat(PdfMatrix m) =>
        $"{PageContentWalker.F(m.A)} {PageContentWalker.F(m.B)} {PageContentWalker.F(m.C)} {PageContentWalker.F(m.D)} {PageContentWalker.F(m.E)} {PageContentWalker.F(m.F)}";
}

/// <summary>A glyph to draw: its code in a font of the page, its style and where it goes.</summary>
internal sealed class OutGlyph
{
    public PdfMatrix Matrix;
    public byte[] Code = Array.Empty<byte>();
    public GlyphStyle Style = null!;
    /// <summary>Advance in text space.</summary>
    public double Advance;
    public int Line;
    /// <summary>The text-showing operator it is drawn right after (-1: after the paragraph's last).</summary>
    public int After = -1;
    /// <summary>The text it shows (for kerning it against its neighbours).</summary>
    public string Text = string.Empty;

    public static OutGlyph From(ContentGlyph g) => new() { Matrix = g.Matrix, Code = g.Code, Style = g.Style, Advance = g.Advance, Line = -1 - g.Op, After = g.Op, Text = g.Text };
}

/// <summary>Draws glyphs at exact positions: runs of one style in one TJ each, placed by adjustments.</summary>
internal static class GlyphEmitter
{
    /// <param name="inPlace">Drawn where the original text was: the graphics state there holds, only the colours are set.</param>
    public static void Emit(StringBuilder sb, IReadOnlyList<OutGlyph> glyphs, bool inPlace = false)
    {
        int i = 0;
        while (i < glyphs.Count)
        {
            var first = glyphs[i];
            int j = i + 1;
            while (j < glyphs.Count && SameRun(first, glyphs[j], glyphs[j - 1])) j++;
            EmitRun(sb, glyphs, i, j, inPlace);
            i = j;
        }
    }

    private static bool SameRun(OutGlyph first, OutGlyph g, OutGlyph prev)
    {
        var a = first.Style;
        var b = g.Style;
        if (g.Line != first.Line || a.FontResource != b.FontResource || a.FontSize != b.FontSize || a.CharSpacing != b.CharSpacing || a.WordSpacing != b.WordSpacing
            || a.Scaling != b.Scaling || a.Rise != b.Rise || a.RenderMode != b.RenderMode || a.StateOps != b.StateOps || a.ColorOps != b.ColorOps) return false;
        var m = first.Matrix;
        var n = g.Matrix;
        if (Math.Abs(m.A - n.A) > 1e-6 || Math.Abs(m.B - n.B) > 1e-6 || Math.Abs(m.C - n.C) > 1e-6 || Math.Abs(m.D - n.D) > 1e-6) return false;
        // On the run's baseline (a column's centre line, in vertical writing).
        var (ax, ay) = a.Vertical ? (m.C, m.D) : (m.A, m.B);
        double lenSq = ax * ax + ay * ay;
        if (lenSq < 1e-18) return false;
        double dx = n.E - m.E, dy = n.F - m.F;
        double across = (dx * -ay + dy * ax) / Math.Sqrt(lenSq);
        return Math.Abs(across) < 1e-3;
    }

    private static void EmitRun(StringBuilder sb, IReadOnlyList<OutGlyph> glyphs, int from, int to, bool inPlace)
    {
        var first = glyphs[from];
        var st = first.Style;
        var m = first.Matrix;
        // Positions along the writing direction: text space x, or y down a column (where TJ moves the pen by fs / 1000 per unit, without Tz).
        var (ax, ay) = st.Vertical ? (m.C, m.D) : (m.A, m.B);
        double lenSq = ax * ax + ay * ay;
        double scale = st.Vertical ? st.FontSize : st.FontSize * st.Scaling / 100.0;
        string state = !inPlace ? st.StateOps : st.ColorOps.Length > 0 ? st.ColorOps : "0 g 0 G ";
        sb.Append("q ").Append(state).Append("BT /").Append(PdfObjectWriter.EscapeName(st.FontResource)).Append(' ').Append(PageContentWalker.F(st.FontSize)).Append(" Tf ")
          .Append(PageContentWalker.F(st.CharSpacing)).Append(" Tc ").Append(PageContentWalker.F(st.WordSpacing)).Append(" Tw ")
          .Append(PageContentWalker.F(st.Scaling)).Append(" Tz ").Append(PageContentWalker.F(st.Rise)).Append(" Ts ")
          .Append(st.RenderMode.ToString(CultureInfo.InvariantCulture)).Append(" Tr ")
          .Append(PdfContentEditor.Mat(m)).Append(" Tm [");
        double expected = 0;
        bool open = false;
        for (int i = from; i < to; i++)
        {
            var g = glyphs[i];
            double x = ((g.Matrix.E - m.E) * ax + (g.Matrix.F - m.F) * ay) / lenSq;
            if (i > from && Math.Abs(scale) > 1e-12)
            {
                double adjust = -(x - expected) * 1000 / scale;
                if (Math.Abs(adjust) > 0.01)
                {
                    if (open) { sb.Append('>'); open = false; }
                    sb.Append(' ').Append(PageContentWalker.F(Math.Round(adjust, 3))).Append(' ');
                }
            }
            if (!open) { sb.Append('<'); open = true; }
            sb.Append(Convert.ToHexString(g.Code));
            expected = x + g.Advance;
        }
        if (open) sb.Append('>');
        sb.Append("] TJ ET Q\n");
    }
}

/// <summary>What a page's edits add: fonts for new characters and new images, and the resources that name them.</summary>
internal sealed class PageSession
{
    private readonly PdfDictionary _resources;
    private readonly Parsing.PdfObjectResolver _r;
    private readonly SystemFontCatalog _catalog;
    private readonly Func<int> _allocate;
    private readonly Dictionary<int, PdfObject> _objects;
    private readonly List<string> _warnings;
    private readonly Dictionary<PdfFont, FontEncoder> _encoders = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string, int), EmbeddedFontBuilder?> _builders = new();
    private readonly Dictionary<string, PdfObject> _newXObjects = new();
    private readonly HashSet<string> _fontNames, _xobjectNames;
    private Dictionary<string, PdfObject>? _writtenFonts;

    public PageSession(PdfDictionary resources, Parsing.PdfObjectResolver r, SystemFontCatalog catalog, Func<int> allocate, Dictionary<int, PdfObject> objects, List<string> warnings)
    {
        _resources = resources;
        _r = r;
        _catalog = catalog;
        _allocate = allocate;
        _objects = objects;
        _warnings = warnings;
        _fontNames = new HashSet<string>((r.Resolve(resources["Font"]) as PdfDictionary)?.Entries.Keys ?? Enumerable.Empty<string>());
        _xobjectNames = new HashSet<string>((r.Resolve(resources["XObject"]) as PdfDictionary)?.Entries.Keys ?? Enumerable.Empty<string>());
    }

    public List<string> Warnings => _warnings;

    /// <summary>Names new fonts and images must not take (those the page's form XObjects use).</summary>
    public void Reserve(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            _fontNames.Add(name);
            _xobjectNames.Add(name);
        }
    }

    public FontEncoder? Encoder(PdfFont? font)
    {
        if (font == null) return null;
        if (!_encoders.TryGetValue(font, out var encoder)) _encoders[font] = encoder = new FontEncoder(font);
        return encoder;
    }

    private EmbeddedFontBuilder? Builder(SystemFontFace face)
    {
        if (_builders.TryGetValue((face.Path, face.Index), out var existing)) return existing;
        EmbeddedFontBuilder? builder = null;
        if (_catalog.Load(face) is { } file)
        {
            string name = Unique("FEd", _fontNames);
            builder = new EmbeddedFontBuilder(file, name);
            _byResource[name] = builder;
        }
        _builders[(face.Path, face.Index)] = builder;
        return builder;
    }

    /// <summary>Installed fonts for characters a style's own font lacks: the one that matches it first.</summary>
    public IEnumerable<EmbeddedFontBuilder> Chain(PdfFont? font, bool bold, bool italic)
    {
        string? name = font != null ? SystemFontCatalog.StripSubset(font.PostScriptName ?? font.BaseFont) : null;
        bold |= font?.IsBold ?? false;
        italic |= font?.IsItalic ?? false;
        if (_catalog.Match(name, bold, italic, font?.IsSerif ?? false, font?.IsFixedPitch ?? false) is { } match && Builder(match) is { } b) yield return b;
        foreach (var face in _catalog.Fallbacks(bold, italic))
            if (Builder(face) is { } fb) yield return fb;
    }

    private static readonly string[] VerticalFeatures = { "vert", "vrt2" };
    private readonly Dictionary<string, EmbeddedFontBuilder> _byResource = new(StringComparer.Ordinal);
    private readonly Dictionary<PdfFont, TrueTypeFontFile?> _designs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Pair kerning (1/1000 em) between two glyphs shown one after the other in one font and size.
    /// A font the edits embedded kerns by its own glyphs. A document font kerns the way its installed
    /// original does (by character), when that original is installed under the same name; otherwise 0.
    /// </summary>
    public double Kerning(GlyphStyle left, byte[] leftCode, string leftText, GlyphStyle right, byte[] rightCode, string rightText)
    {
        if (left.FontResource != right.FontResource || left.FontSize != right.FontSize || left.Vertical) return 0;
        if (_byResource.TryGetValue(left.FontResource, out var builder))
            return leftCode.Length == 2 && rightCode.Length == 2
                ? builder.Font.Kerning(leftCode[0] << 8 | leftCode[1], rightCode[0] << 8 | rightCode[1]) : 0;
        if (left.Font == null || !ReferenceEquals(left.Font, right.Font) || leftText.Length == 0 || rightText.Length == 0) return 0;
        if (!_designs.TryGetValue(left.Font, out var design))
            _designs[left.Font] = design = InstalledOriginal(left.Font);
        if (design == null) return 0;
        int a = design.GlyphFor(char.ConvertToUtf32(leftText, 0)), b = design.GlyphFor(char.ConvertToUtf32(rightText, 0));
        return design.Kerning(a, b);
    }

    // The installed font a document font was made from: the same PostScript name, or the same
    // family and style (TimesNewRoman,Bold is Times New Roman Bold). A stand-in is not used: its
    // kerning belongs to other letter shapes.
    private TrueTypeFontFile? InstalledOriginal(PdfFont font)
    {
        string name = SystemFontCatalog.StripSubset(font.PostScriptName ?? font.BaseFont);
        if (name.Length == 0) return null;
        string key = DesignKey(name);
        foreach (var face in _catalog.Faces)
            if (DesignKey(face.PostScriptName) == key || DesignKey(face.Family + face.Subfamily) == key)
                return _catalog.Load(face);
        return null;
    }

    private static string DesignKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        string key = sb.ToString();
        foreach (string suffix in new[] { "psmt", "mt", "ps", "regular" })
            if (key.EndsWith(suffix, StringComparison.Ordinal) && key.Length > suffix.Length + 2) { key = key[..^suffix.Length]; break; }
        return key;
    }

    /// <summary>A character in the first installed font that has it, in the style given (with that font instead).</summary>
    /// <param name="vertical">For a column: the font's vertical forms (brackets and punctuation turned for vertical writing).</param>
    public (byte[] Code, double Width0, GlyphStyle Style)? EncodeWithInstalled(string element, GlyphStyle style, bool bold, bool italic, bool vertical = false)
    {
        int cp = char.ConvertToUtf32(element, 0);
        foreach (var builder in Chain(style.Font, bold, italic))
        {
            int gid = builder.Font.GlyphFor(cp);
            if (gid <= 0) continue;
            if (vertical && builder.Font.Substitution is { } gsub)
            {
                var run = new List<ShapingGlyph> { new() { Gid = gid, Text = element } };
                gsub.Apply(run, "hani", VerticalFeatures);
                if (run.Count == 1 && run[0].Gid > 0) gid = run[0].Gid;
            }
            return (builder.Encode(gid, element), builder.Font.Advance(gid), style with { FontResource = builder.ResourceName, Font = null, WordSpacing = 0 });
        }
        return null;
    }

    public List<OutGlyph> AddText(PdfAddText add)
    {
        var f = add.Format;
        var face = _catalog.FindFamily(f.FontFamily, f.Bold, f.Italic) ?? _catalog.Match(f.FontFamily, f.Bold, f.Italic, false, false);
        var primary = face != null ? Builder(face) : null;
        double size = f.Size > 0 ? f.Size : 12;
        var rgb = new[] { Math.Clamp(f.Red, 0, 1), Math.Clamp(f.Green, 0, 1), Math.Clamp(f.Blue, 0, 1) };
        string color = $"{PageContentWalker.F(rgb[0])} {PageContentWalker.F(rgb[1])} {PageContentWalker.F(rgb[2])} rg ";
        double angle = f.AngleDegrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        var result = new List<OutGlyph>();
        var lines = add.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li].Normalize(NormalizationForm.FormC).Replace('\t', ' ');
            double s = 0, t = -li * 1.2 * size;
            (TextShaper.Shaped Glyph, EmbeddedFontBuilder Font)? previous = null;
            // Shaped in the order it is read (joining, ligatures), then drawn left to right.
            foreach (var (g, builder) in TextShaper.ShapeLine(line, word => FontFor(word, primary, f.Bold, f.Italic), b => b.Font))
            {
                if (g.Gid <= 0)
                {
                    previous = null;
                    if (g.Text.Trim().Length == 0) { if (!IsFormat(g.Text)) s += 0.25 * size; continue; }
                    // No font has the whole word: this character from the first that has it.
                    var plain = new GlyphStyle(builder.ResourceName, null, size, 0, 0, 100, 0, 0, color, rgb);
                    if (EncodeWithInstalled(g.Text, plain, f.Bold, f.Italic) is not { } alone)
                    {
                        _warnings.Add($"No installed font has the character \"{g.Text}\"; it was left out.");
                        continue;
                    }
                    var at = new PdfPoint(add.Baseline.X + s * cos - t * sin, add.Baseline.Y + s * sin + t * cos);
                    result.Add(new OutGlyph { Matrix = new PdfMatrix(cos, sin, -sin, cos, at.X, at.Y), Code = alone.Code, Style = alone.Style, Advance = alone.Width0 / 1000.0 * size, Line = int.MinValue + li, Text = g.Text });
                    s += alone.Width0 / 1000.0 * size;
                    continue;
                }
                // Pair kerning in left-to-right text (right-to-left pairs are kerned in reading order, which is not read here).
                if (previous is { } p && ReferenceEquals(p.Font, builder) && (p.Glyph.Level & 1) == 0 && (g.Level & 1) == 0)
                    s += builder.Font.Kerning(p.Glyph.Gid, g.Gid) / 1000.0 * size;
                // A mark on its anchor: moved from the pen, and not moving it.
                double gx = s + g.Dx / 1000.0 * size, gy = t + g.Dy / 1000.0 * size;
                var origin = new PdfPoint(add.Baseline.X + gx * cos - gy * sin, add.Baseline.Y + gx * sin + gy * cos);
                double advance = (g.Advance ?? builder.Font.Advance(g.Gid)) / 1000.0 * size;
                var style = new GlyphStyle(builder.ResourceName, null, size, 0, 0, 100, 0, 0, color, rgb);
                result.Add(new OutGlyph
                {
                    Matrix = new PdfMatrix(cos, sin, -sin, cos, origin.X, origin.Y), Code = builder.Encode(g.Gid, g.Text), Style = style,
                    Advance = advance, Line = int.MinValue + li, Text = g.Text,
                });
                previous = (g, builder);
                s += advance;
            }
        }
        return result;
    }

    // The font a word of new text is written in: the chosen one when it has every character, else
    // the first installed font that does (one design per word), else the chosen one for what it has.
    private EmbeddedFontBuilder? FontFor(string word, EmbeddedFontBuilder? primary, bool bold, bool italic)
    {
        bool Has(EmbeddedFontBuilder b)
        {
            for (int i = 0; i < word.Length;)
            {
                int cp = char.ConvertToUtf32(word, i);
                string c = char.ConvertFromUtf32(cp);
                if (c != " " && !IsFormat(c) && b.Font.GlyphFor(cp) <= 0) return false;
                i += c.Length;
            }
            return true;
        }
        if (primary != null && Has(primary)) return primary;
        foreach (var b in Chain(null, bold, italic))
            if (Has(b)) return b;
        return primary ?? Chain(null, bold, italic).FirstOrDefault();
    }

    private static bool IsFormat(string c) => c.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(c, 0) == UnicodeCategory.Format;

    public string AddImage(PdfImageContent image)
    {
        int number = PdfImageXObject.Write(image, _allocate, _objects);
        string name = Unique("ImEd", _xobjectNames);
        _newXObjects[name] = new PdfIndirectRef(number);
        return name;
    }

    /// <summary>The images the edits added, by resource name.</summary>
    public IReadOnlyDictionary<string, PdfObject> NewXObjects => _newXObjects;

    /// <summary>Adds XObjects (copies of edited forms) to the page's resources.</summary>
    public void AddXObjects(IReadOnlyDictionary<string, PdfObject> xobjects)
    {
        foreach (var (k, v) in xobjects)
        {
            _xobjectNames.Add(k);
            _newXObjects[k] = v;
        }
    }

    /// <summary>Writes the fonts that were used (once; nothing may be encoded with them afterwards) and returns them by resource name.</summary>
    public IReadOnlyDictionary<string, PdfObject> WriteFonts(List<string> embedded)
    {
        if (_writtenFonts != null) return _writtenFonts;
        _writtenFonts = new Dictionary<string, PdfObject>();
        foreach (var builder in _builders.Values)
        {
            if (builder is not { IsUsed: true }) continue;
            _writtenFonts[builder.ResourceName] = new PdfIndirectRef(builder.Write(_allocate, _objects));
            embedded.Add(builder.Font.FamilyName + (builder.Font.SubfamilyName is "Regular" ? string.Empty : " " + builder.Font.SubfamilyName));
        }
        return _writtenFonts;
    }

    /// <summary>Writes the fonts that were used, and returns the page's resources with everything new in them.</summary>
    public PdfDictionary Finish(List<string> embedded)
    {
        var res = new Dictionary<string, PdfObject>(_resources.Entries);
        var fonts = new Dictionary<string, PdfObject>((_r.Resolve(_resources["Font"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
        foreach (var (k, v) in WriteFonts(embedded)) fonts[k] = v;
        if (fonts.Count > 0) res["Font"] = new PdfDictionary(fonts);
        if (_newXObjects.Count > 0)
        {
            var xo = new Dictionary<string, PdfObject>((_r.Resolve(_resources["XObject"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
            foreach (var (k, v) in _newXObjects) xo[k] = v;
            res["XObject"] = new PdfDictionary(xo);
        }
        return new PdfDictionary(res);
    }

    private static string Unique(string prefix, HashSet<string> taken)
    {
        for (int i = 1; ; i++)
        {
            string name = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (taken.Add(name)) return name;
        }
    }
}
