using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Signatures;

/// <summary>How a visible signature is laid out in its box.</summary>
public enum PdfSignatureLayout
{
    /// <summary>The signature's details (who, when, why, where) filling the box.</summary>
    TextOnly,
    /// <summary>The handwritten or picture signature on the left, the details on the right.</summary>
    ImageAndText,
    /// <summary>The handwritten or picture signature alone, as large as the box allows.</summary>
    ImageOnly,
    /// <summary>The signer's name in large type on the left, the details on the right (no picture needed).</summary>
    NameAndText,
}

/// <summary>
/// What a visible signature shows. The picture (a scanned or drawn handwritten signature, a
/// seal) keeps its transparency through a soft mask; the text is set in an installed font
/// embedded as a subset (with a ToUnicode map, so it can be searched and copied) when a font
/// catalog is given or the text needs characters the standard Helvetica cannot show.
/// </summary>
public sealed record PdfSignatureAppearance
{
    public PdfSignatureLayout Layout { get; init; } = PdfSignatureLayout.TextOnly;
    /// <summary>The picture for <see cref="PdfSignatureLayout.ImageAndText"/> and <see cref="PdfSignatureLayout.ImageOnly"/>; without one, the name stands in for it.</summary>
    public PdfImageContent? Image { get; init; }
    public bool ShowName { get; init; } = true;
    public bool ShowDate { get; init; } = true;
    public bool ShowReason { get; init; } = true;
    public bool ShowLocation { get; init; } = true;
    /// <summary>"Digitally signed by", "Date:", "Reason:" and "Location:" before the values.</summary>
    public bool ShowLabels { get; init; } = true;
    /// <summary>Where the text font comes from; null uses the standard Helvetica (not embedded) unless the text needs more.</summary>
    public SystemFontCatalog? Fonts { get; init; }
    /// <summary>The family to set the text in when fonts are embedded.</summary>
    public string FontFamily { get; init; } = "Arial";
}

/// <summary>Builds the normal appearance stream of a signature widget (ISO 32000-2 12.7.5.5, 12.8.3.4).</summary>
internal static class PdfSignatureAppearanceBuilder
{
    private const double Leading = 1.15;

    public static PdfStream Build(PdfSignatureRequest request, double width, double height, int rotation, Parsing.PdfObjectResolver resolver,
        Func<int> allocate, IDictionary<int, PdfObject> objects)
    {
        var a = request.Appearance ?? new PdfSignatureAppearance();
        bool quarter = rotation is 90 or 270;
        double w = quarter ? height : width, h = quarter ? width : height;

        var layout = a.Layout;
        var image = layout is PdfSignatureLayout.ImageAndText or PdfSignatureLayout.ImageOnly ? a.Image : null;
        if (image == null && layout == PdfSignatureLayout.ImageAndText) layout = PdfSignatureLayout.NameAndText;
        bool nameOnly = image == null && layout == PdfSignatureLayout.ImageOnly;

        string details = Details(request, a, withName: layout is PdfSignatureLayout.TextOnly or PdfSignatureLayout.ImageAndText);
        string name = request.SignerName;
        var face = TextFace.Choose(a, details + name, resolver);

        double pad = Math.Min(4, Math.Min(w, h) * 0.08);
        double innerW = Math.Max(1, w - 2 * pad), innerH = Math.Max(1, h - 2 * pad);
        var s = new StringBuilder();
        var xobjects = new Dictionary<string, PdfObject>();
        string? imageName = null;
        if (image != null)
        {
            imageName = "SigImg";
            xobjects[imageName] = new PdfIndirectRef(PdfImageXObject.Write(image, allocate, objects, interpolate: true));
        }

        switch (layout)
        {
            case PdfSignatureLayout.TextOnly:
                TextBlock(s, face, details, pad, pad, innerW, innerH, maxSize: 12, center: false);
                break;
            case PdfSignatureLayout.ImageOnly when !nameOnly:
                Image(s, imageName!, image!, pad, pad, innerW, innerH);
                break;
            case PdfSignatureLayout.ImageOnly:
                TextBlock(s, face, name, pad, pad, innerW, innerH, maxSize: 72, center: true, middle: true);
                break;
            default:
            {
                // A picture (or the name) on the left, the details on the right.
                double aspect = image != null ? (double)image.Width / image.Height : 2.5;
                double left = details.Length == 0 ? innerW : Math.Clamp(innerH * aspect, innerW * 0.3, innerW * 0.5);
                if (image != null) Image(s, imageName!, image, pad, pad, left, innerH);
                else TextBlock(s, face, name, pad, pad, left, innerH, maxSize: 72, center: true, middle: true);
                if (details.Length > 0)
                {
                    double x = pad + left + pad;
                    TextBlock(s, face, details, x, pad, Math.Max(1, w - pad - x), innerH, maxSize: 12, center: false, middle: true);
                }
                break;
            }
        }

        var resources = new Dictionary<string, PdfObject>();
        var fonts = face.Resources(allocate, objects);
        if (fonts.Count > 0) resources["Font"] = new PdfDictionary(fonts);
        if (xobjects.Count > 0) resources["XObject"] = new PdfDictionary(xobjects);
        var entries = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Form"),
            ["BBox"] = new PdfArray(new PdfObject[] { Num(0), Num(0), Num(w), Num(h) }),
            ["Resources"] = new PdfDictionary(resources),
        };
        if (rotation != 0)
            entries["Matrix"] = rotation switch
            {
                90 => new PdfArray(new PdfObject[] { Num(0), Num(1), Num(-1), Num(0), Num(h), Num(0) }),
                180 => new PdfArray(new PdfObject[] { Num(-1), Num(0), Num(0), Num(-1), Num(w), Num(h) }),
                _ => new PdfArray(new PdfObject[] { Num(0), Num(-1), Num(1), Num(0), Num(0), Num(w) }),
            };
        return PdfObjectWriter.NewStream(entries, Encoding.Latin1.GetBytes(s.ToString()));
    }

    /// <summary>The text lines the appearance shows, as the options ask.</summary>
    internal static string Details(PdfSignatureRequest request, PdfSignatureAppearance a, bool withName)
    {
        var lines = new List<string>();
        if (withName && a.ShowName && !string.IsNullOrWhiteSpace(request.SignerName))
            lines.Add(a.ShowLabels ? "Digitally signed by " + request.SignerName : request.SignerName);
        if (a.ShowDate)
        {
            string date = request.SigningTime.ToString("yyyy.MM.dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            lines.Add(a.ShowLabels ? "Date: " + date : date);
        }
        if (a.ShowReason && !string.IsNullOrWhiteSpace(request.Reason)) lines.Add(a.ShowLabels ? "Reason: " + request.Reason : request.Reason!);
        if (a.ShowLocation && !string.IsNullOrWhiteSpace(request.Location)) lines.Add(a.ShowLabels ? "Location: " + request.Location : request.Location!);
        return string.Join("\n", lines);
    }

    /// <summary>The picture scaled to fit the area, keeping its proportions, centred.</summary>
    private static void Image(StringBuilder s, string name, PdfImageContent image, double x, double y, double width, double height)
    {
        double scale = Math.Min(width / image.Width, height / image.Height);
        double dw = image.Width * scale, dh = image.Height * scale;
        double ox = x + (width - dw) / 2, oy = y + (height - dh) / 2;
        s.Append("q ").Append(F(dw)).Append(" 0 0 ").Append(F(dh)).Append(' ').Append(F(ox)).Append(' ').Append(F(oy))
         .Append(" cm /").Append(name).Append(" Do Q\n");
    }

    /// <summary>
    /// Text wrapped to the area at the largest size (up to <paramref name="maxSize"/>) at which it
    /// fits, left-aligned or centred, from the top or centred vertically.
    /// </summary>
    private static void TextBlock(StringBuilder s, TextFace face, string text, double x, double y, double width, double height,
        double maxSize, bool center, bool middle = false)
    {
        if (text.Length == 0) return;
        double size = Math.Min(maxSize, Math.Max(2, height / Leading));
        List<string> lines;
        while (true)
        {
            lines = Wrap(text, t => face.Width(t, size), width);
            bool fits = lines.Count * size * Leading <= height && lines.All(l => face.Width(l, size) <= width + 0.01);
            if (fits || size <= 2) break;
            size = Math.Max(2, size - Math.Max(0.5, size * 0.04));
        }
        double block = lines.Count * size * Leading;
        double top = middle ? y + height - Math.Max(0, (height - block) / 2) : y + height;
        double baseline = top - size * 0.9;
        s.Append("q BT 0 g\n");
        for (int i = 0; i < lines.Count; i++)
        {
            double lx = center ? x + Math.Max(0, (width - face.Width(lines[i], size)) / 2) : x;
            double ly = baseline - i * size * Leading;
            s.Append("1 0 0 1 ").Append(F(lx)).Append(' ').Append(F(ly)).Append(" Tm ");
            face.Show(s, lines[i], size);
            s.Append('\n');
        }
        s.Append("ET Q\n");
    }

    /// <summary>Greedy word wrap; a word longer than the line is broken between characters.</summary>
    internal static List<string> Wrap(string text, Func<string, double> measure, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (measure(candidate) <= width || line.Length == 0 && measure(word) <= width)
                {
                    line.Clear().Append(candidate);
                    continue;
                }
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                var piece = new StringBuilder();
                var e = StringInfo.GetTextElementEnumerator(word);
                while (e.MoveNext())
                {
                    string element = (string)e.Current;
                    if (piece.Length > 0 && measure(piece + element) > width) { lines.Add(piece.ToString()); piece.Clear(); }
                    piece.Append(element);
                }
                line.Append(piece);
            }
            lines.Add(line.ToString());
        }
        return lines;
    }

    /// <summary>The font the text is set in: the standard Helvetica, or installed fonts embedded as subsets.</summary>
    private abstract class TextFace
    {
        public abstract double Width(string text, double size);
        public abstract void Show(StringBuilder s, string text, double size);
        public abstract Dictionary<string, PdfObject> Resources(Func<int> allocate, IDictionary<int, PdfObject> objects);

        public static TextFace Choose(PdfSignatureAppearance a, string text, Parsing.PdfObjectResolver resolver)
        {
            var standard = new StandardFace(resolver);
            var catalog = a.Fonts ?? (standard.CanShow(text) ? null : SystemFontCatalog.Installed);
            if (catalog != null && EmbeddedFace.TryCreate(catalog, a.FontFamily) is { } embedded) return embedded;
            return standard;
        }
    }

    private sealed class StandardFace : TextFace
    {
        private readonly PdfDictionary _font = new(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName("Helvetica"),
            ["Encoding"] = new PdfName("WinAnsiEncoding"),
        });
        private readonly PdfFormFiller.FontMetrics _metrics;

        public StandardFace(Parsing.PdfObjectResolver resolver) => _metrics = PdfFormFiller.Metrics(_font, resolver);

        public bool CanShow(string text) => text.All(c => c == '?' || c == '\n' || _metrics.Encode(c) != (byte)'?');
        public override double Width(string text, double size) => _metrics.Width(text, size);
        private bool _used;
        public override void Show(StringBuilder s, string text, double size)
        {
            _used = true;
            s.Append("/Helv ").Append(F(size)).Append(" Tf (").Append(PdfFormFiller.Escape(text, _metrics)).Append(") Tj");
        }
        public override Dictionary<string, PdfObject> Resources(Func<int> allocate, IDictionary<int, PdfObject> objects) =>
            _used ? new() { ["Helv"] = _font } : new();
    }

    /// <summary>
    /// An installed font and its fallbacks, each embedded as a subset of the glyphs used: each
    /// character goes in the first of them that has it.
    /// </summary>
    private sealed class EmbeddedFace : TextFace
    {
        private readonly List<EmbeddedFontBuilder> _chain;
        private readonly IEnumerator<EmbeddedFontBuilder> _more;

        private EmbeddedFace(EmbeddedFontBuilder primary, IEnumerable<EmbeddedFontBuilder> fallbacks)
        {
            _chain = new List<EmbeddedFontBuilder> { primary };
            _more = fallbacks.GetEnumerator();
        }

        public static EmbeddedFace? TryCreate(SystemFontCatalog catalog, string family)
        {
            var face = catalog.FindFamily(family, false, false) ?? catalog.Match(family, false, false, false, false);
            if (face == null || catalog.Load(face) is not { } file) return null;
            int n = 1;
            var primary = new EmbeddedFontBuilder(file, "SigF" + n++);
            IEnumerable<EmbeddedFontBuilder> Fallbacks()
            {
                foreach (var f in catalog.Fallbacks(false, false))
                    if (f.Path != face.Path || f.Index != face.Index)
                        if (catalog.Load(f) is { } ff) yield return new EmbeddedFontBuilder(ff, "SigF" + n++);
            }
            return new EmbeddedFace(primary, Fallbacks());
        }

        /// <summary>The font with a glyph for the character, and that glyph; null when none has it.</summary>
        private (EmbeddedFontBuilder Font, int Gid)? Find(string element)
        {
            int cp = char.ConvertToUtf32(element, 0);
            foreach (var b in _chain)
                if (b.Font.GlyphFor(cp) is > 0 and var gid) return (b, gid);
            while (_more.MoveNext())
            {
                _chain.Add(_more.Current);
                if (_more.Current.Font.GlyphFor(cp) is > 0 and var gid) return (_more.Current, gid);
            }
            return null;
        }

        private IEnumerable<(EmbeddedFontBuilder Font, int Gid, string Element)> Glyphs(string text)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                string element = (string)e.Current;
                if (char.IsSurrogate(element, 0) && element.Length < 2) continue;
                if (Find(element) is { } g) yield return (g.Font, g.Gid, element);
            }
        }

        public override double Width(string text, double size) => Glyphs(text).Sum(g => g.Font.Font.Advance(g.Gid)) * size / 1000.0;

        public override void Show(StringBuilder s, string text, double size)
        {
            EmbeddedFontBuilder? current = null;
            var run = new StringBuilder();
            void Flush()
            {
                if (current == null || run.Length == 0) return;
                s.Append('/').Append(current.ResourceName).Append(' ').Append(F(size)).Append(" Tf <").Append(run).Append("> Tj ");
                run.Clear();
            }
            foreach (var (font, gid, element) in Glyphs(text).ToList())
            {
                if (!ReferenceEquals(font, current)) { Flush(); current = font; }
                run.Append(Convert.ToHexString(font.Encode(gid, element)));
            }
            Flush();
        }

        public override Dictionary<string, PdfObject> Resources(Func<int> allocate, IDictionary<int, PdfObject> objects)
        {
            var result = new Dictionary<string, PdfObject>();
            foreach (var b in _chain.Where(b => b.IsUsed))
                result[b.ResourceName] = new PdfIndirectRef(b.Write(allocate, objects));
            return result;
        }
    }

    private static PdfObject Num(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e12 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(v);

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));
}
