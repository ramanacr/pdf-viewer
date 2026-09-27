using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Editing;

/// <summary>A form XObject a page draws as a mark, and its box in the space the page places it in.</summary>
internal sealed record MarkForm(int Number, PdfRect Box);

/// <summary>
/// The form XObjects of one update's marks, one per design: the same watermark on every page is
/// stored once, and a header's text once per different text. Text is shaped and set in installed
/// fonts, embedded once for the whole update as subsets. The form each page draws carries
/// Acrobat's PieceInfo (ADBE_CompoundType, with /Private naming the tool and /DocSettings holding
/// the settings) so this editor and Acrobat both know the mark for what it is.
/// </summary>
internal sealed class PageMarkForms
{
    /// <summary>Keys of the DocSettings dictionary this editor writes.</summary>
    public const string KindKey = "PdfViewer_Kind", SettingsKey = "PdfViewer_Settings";

    private readonly PageSession _text;
    private readonly Func<int> _allocate;
    private readonly Dictionary<int, PdfObject> _objects;
    private readonly Dictionary<string, MarkForm> _cache = new(StringComparer.Ordinal);
    private readonly List<Pending> _pending = new();
    private readonly int _settings;
    private readonly PdfMarkKind _kind;
    private readonly string _settingsJson;
    private readonly PdfString _modified;

    private sealed class Pending
    {
        public int Number;
        public string Content = string.Empty;
        public PdfRect BBox;
        public PdfMatrix? Matrix;
        public Dictionary<string, PdfObject> Resources = new();
        public bool Group, Outer, Text;
    }

    public PageMarkForms(PdfObjectResolver resolver, SystemFontCatalog catalog, Func<int> allocate, Dictionary<int, PdfObject> objects, List<string> warnings,
        PdfMarkKind kind, string settingsJson, DateTime now)
    {
        _text = new PageSession(new PdfDictionary(new Dictionary<string, PdfObject>()), resolver, catalog, allocate, objects, warnings);
        _allocate = allocate;
        _objects = objects;
        _kind = kind;
        _settingsJson = settingsJson;
        _settings = allocate();
        _modified = new PdfString(Encoding.ASCII.GetBytes(PdfDate(now)));
    }

    /// <summary>Acrobat's name for the tool (the /Private entry of ADBE_CompoundType).</summary>
    public static string PrivateName(PdfMarkKind kind) => kind is PdfMarkKind.HeaderFooter or PdfMarkKind.Bates ? "Headers & Footers" : "Watermark";

    // ------------------------------------------------------------------ designs

    /// <summary>Lines of text, each aligned in the block as <paramref name="align"/> says; the first baseline at y = 0. Null when nothing shows.</summary>
    public MarkForm? Text(string text, PdfTextFormat format, PdfMarkHorizontal align, double opacity)
    {
        string key = $"T|{format}|{align}|{F(opacity)}|{text}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        double size = format.Size > 0 ? format.Size : 12, lead = 1.2 * size;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var laid = new List<(List<OutGlyph> Glyphs, double Width)>();
        for (int li = 0; li < lines.Length; li++)
        {
            var glyphs = _text.AddText(new PdfAddText(1, new PdfPoint(0, -li * lead), lines[li], format with { AngleDegrees = 0 }));
            laid.Add((glyphs, glyphs.Count == 0 ? 0 : glyphs.Max(g => g.Matrix.E + g.Advance)));
        }
        if (laid.All(l => l.Glyphs.Count == 0)) return null;
        double width = laid.Max(l => l.Width);
        var all = new List<OutGlyph>();
        foreach (var (glyphs, w) in laid)
        {
            double dx = align switch { PdfMarkHorizontal.Center => (width - w) / 2, PdfMarkHorizontal.Right => width - w, _ => 0 };
            foreach (var g in glyphs) g.Matrix *= PdfMatrix.CreateTranslation(dx, 0);
            all.AddRange(glyphs);
        }
        var sb = new StringBuilder();
        GlyphEmitter.Emit(sb, all);
        // Ascent and descent as the text editor places a line (0.8 em above the baseline).
        var box = new PdfRect(0, -(lines.Length - 1) * lead - 0.2 * size, width, (lines.Length - 1) * lead + size);
        return _cache[key] = Design(sb.ToString(), box, null, new Dictionary<string, PdfObject>(), opacity, text: true);
    }

    /// <summary>A picture, one point per pixel.</summary>
    public MarkForm Image(PdfImageContent image, double opacity)
    {
        string key = $"I|{F(opacity)}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        int number = PdfImageXObject.Write(image, _allocate, _objects, interpolate: true);
        var box = new PdfRect(0, 0, image.Width, image.Height);
        string content = $"q {F(image.Width)} 0 0 {F(image.Height)} 0 0 cm /Im0 Do Q\n";
        var resources = new Dictionary<string, PdfObject> { ["XObject"] = Dict(("Im0", new PdfIndirectRef(number))) };
        return _cache[key] = Design(content, box, null, resources, opacity);
    }

    /// <summary>A page of another PDF, upright as that page is seen; its box is the page's crop box.</summary>
    public MarkForm Page(PdfVectorDocument source, int pageNumber, double opacity)
    {
        string key = $"P|{F(opacity)}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (source.IsEncrypted) throw new NotSupportedException("The PDF to draw is encrypted; use an unencrypted copy.");
        if (pageNumber < 1 || pageNumber > source.PageTree.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageNumber), $"The PDF to draw has no page {pageNumber}.");
        var node = source.PageTree.Pages[pageNumber - 1];
        var decoder = new PdfStreamDecoder(null, source.Resolver.Resolve);
        var content = new MemoryStream();
        foreach (var stream in node.Contents)
        {
            content.Write(decoder.DecodeStream(stream));
            content.WriteByte((byte)'\n');
        }
        var (w, h, toUser) = PdfPageMarks.Display(node);
        toUser.TryInvert(out var toDisplay);
        var copies = new Dictionary<int, int>();
        var resources = ((PdfDictionary)Copy(node.Resources, source.Resolver, copies, 0)).Entries.ToDictionary(e => e.Key, e => e.Value);
        return _cache[key] = Design(Encoding.Latin1.GetString(content.ToArray()), node.CropBox, toDisplay, resources, opacity, fullBox: new PdfRect(0, 0, w, h));
    }

    /// <summary>A rectangle of colour the size of the page as it is seen.</summary>
    public MarkForm Color(double[] rgb, double width, double height, double opacity)
    {
        string key = $"C|{F(width)}|{F(height)}|{string.Join(",", rgb.Select(F))}|{F(opacity)}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var c = rgb.Length >= 3 ? rgb : new double[] { 1, 1, 1 };
        string content = $"{F(Math.Clamp(c[0], 0, 1))} {F(Math.Clamp(c[1], 0, 1))} {F(Math.Clamp(c[2], 0, 1))} rg 0 0 {F(width)} {F(height)} re f\n";
        return _cache[key] = Design(content, new PdfRect(0, 0, width, height), null, new Dictionary<string, PdfObject>(), opacity);
    }

    // A design's form, and when it is translucent a form around it that sets the opacity: the
    // design is a transparency group, so it fades as a whole (overlaps do not show through).
    private MarkForm Design(string content, PdfRect bbox, PdfMatrix? matrix, Dictionary<string, PdfObject> resources, double opacity, bool text = false, PdfRect? fullBox = null)
    {
        var box = fullBox ?? bbox;
        var design = new Pending { Number = _allocate(), Content = content, BBox = bbox, Matrix = matrix, Resources = resources, Text = text };
        _pending.Add(design);
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity >= 1)
        {
            design.Outer = true;
            return new MarkForm(design.Number, box);
        }
        design.Group = true;
        var outer = new Pending
        {
            Number = _allocate(), Content = "/GS0 gs /Fm0 Do\n", BBox = box, Outer = true,
            Resources = new Dictionary<string, PdfObject>
            {
                ["ExtGState"] = Dict(("GS0", Dict(("Type", new PdfName("ExtGState")), ("CA", new PdfReal(opacity)), ("ca", new PdfReal(opacity))))),
                ["XObject"] = Dict(("Fm0", new PdfIndirectRef(design.Number))),
            },
        };
        _pending.Add(outer);
        return new MarkForm(outer.Number, box);
    }

    /// <summary>Writes the fonts, the forms and the settings.</summary>
    public void Finish(List<string> embedded)
    {
        var fonts = _text.WriteFonts(embedded);
        var pieceInfo = Dict(("ADBE_CompoundType", Dict(
            ("DocSettings", new PdfIndirectRef(_settings)), ("LastModified", _modified), ("Private", new PdfName(PrivateName(_kind))))));
        foreach (var p in _pending)
        {
            var resources = new Dictionary<string, PdfObject>(p.Resources);
            if (p.Text)
            {
                var used = fonts.Where(f => PdfPageMarks.UsesName(p.Content, f.Key)).ToList();
                if (used.Count > 0) resources["Font"] = new PdfDictionary(used.ToDictionary(f => f.Key, f => f.Value));
            }
            var dict = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["FormType"] = new PdfInteger(1),
                ["BBox"] = new PdfArray(new PdfObject[] { new PdfReal(p.BBox.X), new PdfReal(p.BBox.Y), new PdfReal(p.BBox.Right), new PdfReal(p.BBox.Bottom) }),
                ["Resources"] = new PdfDictionary(resources),
                ["Filter"] = new PdfName("FlateDecode"),
            };
            if (p.Matrix is { } m && !m.IsIdentity)
                dict["Matrix"] = new PdfArray(new PdfObject[] { new PdfReal(m.A), new PdfReal(m.B), new PdfReal(m.C), new PdfReal(m.D), new PdfReal(m.E), new PdfReal(m.F) });
            if (p.Group) dict["Group"] = Dict(("Type", new PdfName("Group")), ("S", new PdfName("Transparency")));
            if (p.Outer)
            {
                dict["PieceInfo"] = pieceInfo;
                dict["LastModified"] = _modified;
            }
            _objects[p.Number] = PdfObjectWriter.NewStream(dict, ContentRedactor.Deflate(Encoding.Latin1.GetBytes(p.Content)));
        }
        _objects[_settings] = Dict(("Type", new PdfName("PdfViewer_MarkSettings")), (KindKey, new PdfName(_kind.ToString())),
            (SettingsKey, PdfObjectWriter.TextString(_settingsJson)), ("LastModified", _modified));
    }

    // ------------------------------------------------------------------ copying another document's objects

    // The object and everything it refers to, renumbered into this update (each once, cycles kept).
    private PdfObject Copy(PdfObject? obj, PdfObjectResolver r, Dictionary<int, int> copies, int depth)
    {
        if (depth > 256) return PdfNull.Instance;
        switch (obj)
        {
            case null: return PdfNull.Instance;
            case PdfIndirectRef ir:
            {
                if (copies.TryGetValue(ir.ObjectNumber, out int done)) return new PdfIndirectRef(done);
                int n = _allocate();
                copies[ir.ObjectNumber] = n;
                _objects[n] = Copy(r.Resolve(ir), r, copies, depth + 1);
                return new PdfIndirectRef(n);
            }
            case PdfStream s:
            {
                var dict = (PdfDictionary)Copy(s.Dictionary, r, copies, depth + 1);
                byte[] raw = s.GetRawBytes().ToArray();
                return new PdfStream(dict, 0, raw.Length, null, raw);
            }
            case PdfDictionary d:
                return new PdfDictionary(d.Entries.Where(e => e.Key != "Parent").ToDictionary(e => e.Key, e => Copy(e.Value, r, copies, depth + 1)));
            case PdfArray a:
                return new PdfArray(a.Items.Select(i => Copy(i, r, copies, depth + 1)).ToList());
            default:
                return obj;
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries) => new(entries.ToDictionary(e => e.Key, e => e.Value));

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));

    internal static string PdfDate(DateTime t)
    {
        var offset = t.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(t);
        string zone = offset == TimeSpan.Zero ? "Z" : (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hh", CultureInfo.InvariantCulture) + "'" + offset.ToString("mm", CultureInfo.InvariantCulture) + "'";
        return "D:" + t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }
}
