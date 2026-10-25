using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfEngine.Geometry;
using PdfEngine.Vector.Services;

namespace PdfEngine.Vector.Annotations;

/// <summary>
/// Annotations as XFDF (ISO 19444-1, the <c>&lt;annots&gt;</c> element), in PDF user space: the
/// markup and comment types a reader makes (highlight, underline, strike-out, squiggly, note, free
/// text, ink, square, circle, line). Export writes <see cref="PdfAnnotation"/>s; import reads them
/// back, ready for <see cref="Services.PdfAnnotationEditor.Add"/>. Imported XML is untrusted: no DTD,
/// no external entities, a size limit, finite numbers only, and elements of other types skipped.
/// </summary>
public static class PdfAnnotationXfdf
{
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";

    /// <summary>Largest XFDF document read, in characters.</summary>
    public const int MaxCharacters = 8 * 1024 * 1024;

    /// <summary>Most annotations read from one document.</summary>
    public const int MaxAnnotations = 10000;

    /// <summary>Most points in one ink stroke, and strokes in one ink annotation.</summary>
    public const int MaxInkPoints = 10000, MaxInkStrokes = 100;

    private static readonly Dictionary<string, string> ElementForSubtype = new(StringComparer.Ordinal)
    {
        ["Highlight"] = "highlight", ["Underline"] = "underline", ["StrikeOut"] = "strikeout", ["Squiggly"] = "squiggly",
        ["Text"] = "text", ["FreeText"] = "freetext", ["Ink"] = "ink", ["Square"] = "square", ["Circle"] = "circle", ["Line"] = "line",
    };

    private static readonly Dictionary<string, string> SubtypeForElement =
        ElementForSubtype.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>True for the subtypes this class writes and reads.</summary>
    public static bool Supports(string subtype) => ElementForSubtype.ContainsKey(subtype);

    private static readonly (string Name, int Bit)[] FlagNames =
    [
        ("invisible", 1), ("hidden", 2), ("print", 4), ("nozoom", 8), ("norotate", 16), ("noview", 32), ("readonly", 64), ("locked", 128), ("togglenoview", 256),
    ];

    /// <summary>The annotations as an XFDF document; those of other subtypes are left out. <paramref name="href"/> names the PDF (the &lt;f&gt; element).</summary>
    public static string Export(IEnumerable<PdfAnnotation> annotations, string? href = null)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        var annots = new XElement(Ns + "annots");
        foreach (var a in annotations)
        {
            if (!ElementForSubtype.TryGetValue(a.Subtype, out var name)) continue;
            var e = new XElement(Ns + name,
                new XAttribute("page", (a.PageNumber - 1).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("rect", Join(a.Rect.Left, a.Rect.Top, a.Rect.Right, a.Rect.Bottom)));
            if (Hex(a.Color) is { } color) e.Add(new XAttribute("color", color));
            if (a.InteriorColor != null && Hex(a.InteriorColor) is { } interior) e.Add(new XAttribute("interior-color", interior));
            if (a.Opacity < 1) e.Add(new XAttribute("opacity", Number(a.Opacity)));
            if (!string.IsNullOrEmpty(a.Author)) e.Add(new XAttribute("title", a.Author));
            if (!string.IsNullOrEmpty(a.Subject)) e.Add(new XAttribute("subject", a.Subject));
            if (!string.IsNullOrEmpty(a.Name)) e.Add(new XAttribute("name", a.Name));
            if (!string.IsNullOrEmpty(a.ModifiedDate)) e.Add(new XAttribute("date", a.ModifiedDate));
            if (!string.IsNullOrEmpty(a.CreationDate)) e.Add(new XAttribute("creationdate", a.CreationDate));
            string flags = string.Join(",", FlagNames.Where(f => (a.Flags & f.Bit) != 0).Select(f => f.Name));
            if (flags.Length > 0) e.Add(new XAttribute("flags", flags));
            if (a.BorderWidth is double w) e.Add(new XAttribute("width", Number(w)));
            if (a.BorderDash is { Length: > 0 } dash) e.Add(new XAttribute("style", "dash"), new XAttribute("dashes", Join(dash)));
            if (a.QuadPoints.Count > 0) e.Add(new XAttribute("coords", Join(a.QuadPoints.SelectMany(q => q).ToArray())));
            if (a.Subtype == "Text" && !string.IsNullOrEmpty(a.Icon)) e.Add(new XAttribute("icon", a.Icon));
            if (a.Subtype == "Line" && a.Line is { Length: 4 } l)
                e.Add(new XAttribute("start", Join(l[0], l[1])), new XAttribute("end", Join(l[2], l[3])));
            if (a.Subtype == "FreeText" && a.Justification is 1 or 2) e.Add(new XAttribute("justification", a.Justification == 1 ? "centered" : "right"));
            if (!string.IsNullOrEmpty(a.Contents)) e.Add(new XElement(Ns + "contents", a.Contents));
            if (a.Subtype == "FreeText" && !string.IsNullOrEmpty(a.DefaultAppearance)) e.Add(new XElement(Ns + "defaultappearance", a.DefaultAppearance));
            if (a.Subtype == "Ink" && a.InkList.Count > 0)
                e.Add(new XElement(Ns + "inklist", a.InkList.Select(s => new XElement(Ns + "gesture", string.Join(";", s.Select(p => Join(p.X, p.Y)))))));
            if (a.PopupRect is { } popup)
                e.Add(new XElement(Ns + "popup", new XAttribute("rect", Join(popup.Left, popup.Top, popup.Right, popup.Bottom)), new XAttribute("open", a.PopupOpen ? "yes" : "no")));
            annots.Add(e);
        }
        var root = new XElement(Ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"), annots);
        if (!string.IsNullOrEmpty(href)) root.AddFirst(new XElement(Ns + "f", new XAttribute("href", href)));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root.ToString(SaveOptions.None);
    }

    /// <summary>
    /// Reads the annotations of an XFDF document. Elements of other types, and annotations whose
    /// page or rectangle cannot be read, are skipped. Throws <see cref="FormatException"/> when the
    /// text is not XFDF (not XML, a DTD, too long, another root element) or has too many annotations.
    /// </summary>
    public static List<PdfAnnotation> Import(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.Length > MaxCharacters) throw new FormatException($"The XFDF document is longer than {MaxCharacters / (1024 * 1024)} MB.");
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The text is not a well-formed XFDF document.", ex);
        }
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "xfdf") throw new FormatException("The document is not XFDF (its root element is not <xfdf>).");
        var result = new List<PdfAnnotation>();
        foreach (var annots in root.Elements().Where(e => e.Name.LocalName == "annots"))
        {
            foreach (var e in annots.Elements())
            {
                if (!SubtypeForElement.TryGetValue(e.Name.LocalName, out var subtype)) continue;
                if (result.Count >= MaxAnnotations) throw new FormatException($"The XFDF document has more than {MaxAnnotations} annotations.");
                if (Read(e, subtype) is { } a) result.Add(a);
            }
        }
        return result;
    }

    private static PdfAnnotation? Read(XElement e, string subtype)
    {
        if (!int.TryParse((string?)e.Attribute("page"), NumberStyles.None, CultureInfo.InvariantCulture, out int page) || page < 0 || page > 1_000_000) return null;
        if (Numbers((string?)e.Attribute("rect")) is not { Length: 4 } r) return null;
        var a = new PdfAnnotation
        {
            PageNumber = page + 1,
            Subtype = subtype,
            Rect = new PdfRect(Math.Min(r[0], r[2]), Math.Min(r[1], r[3]), Math.Abs(r[2] - r[0]), Math.Abs(r[3] - r[1])),
            Color = Colour((string?)e.Attribute("color")),
            InteriorColor = Colour((string?)e.Attribute("interior-color")),
            Opacity = double.TryParse((string?)e.Attribute("opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out double o) && double.IsFinite(o) ? Math.Clamp(o, 0, 1) : 1,
            Author = Limit((string?)e.Attribute("title")),
            Subject = Limit((string?)e.Attribute("subject")),
            Name = Limit((string?)e.Attribute("name")),
            ModifiedDate = Limit((string?)e.Attribute("date")),
            CreationDate = Limit((string?)e.Attribute("creationdate")),
            Contents = Limit(e.Elements().FirstOrDefault(c => c.Name.LocalName == "contents")?.Value, 100_000),
        };
        if ((string?)e.Attribute("flags") is { } flags)
        {
            int f = 0;
            foreach (var part in flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                f |= FlagNames.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).Bit;
            a.Flags = f;
        }
        if (double.TryParse((string?)e.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && double.IsFinite(w) && w >= 0 && w <= 1000)
            a.BorderWidth = w;
        if ((string?)e.Attribute("style") == "dash") a.BorderDash = Numbers((string?)e.Attribute("dashes")) is { Length: > 0 and <= 16 } dashes ? dashes : new[] { 3.0 };
        if (Numbers((string?)e.Attribute("coords")) is { Length: >= 8 } coords)
            for (int k = 0; k + 7 < coords.Length && a.QuadPoints.Count < MaxInkPoints; k += 8) a.QuadPoints.Add(coords[k..(k + 8)]);
        if (subtype == "Text") a.Icon = Limit((string?)e.Attribute("icon"), 64) is { Length: > 0 } icon && icon.All(char.IsAsciiLetterOrDigit) ? icon : "Note";
        if (subtype == "Line" && Numbers((string?)e.Attribute("start")) is { Length: 2 } s && Numbers((string?)e.Attribute("end")) is { Length: 2 } t)
            a.Line = new[] { s[0], s[1], t[0], t[1] };
        if (subtype == "FreeText")
        {
            a.DefaultAppearance = Limit(e.Elements().FirstOrDefault(c => c.Name.LocalName == "defaultappearance")?.Value ?? (string?)e.Attribute("defaultappearance"), 1000);
            a.Justification = (string?)e.Attribute("justification") switch { "centered" => 1, "right" => 2, _ => 0 };
        }
        if (subtype == "Ink")
        {
            foreach (var gesture in e.Elements().Where(c => c.Name.LocalName == "inklist").SelectMany(c => c.Elements()).Where(c => c.Name.LocalName == "gesture"))
            {
                if (a.InkList.Count >= MaxInkStrokes) break;
                var points = new List<PdfPoint>();
                foreach (var pair in gesture.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (points.Count >= MaxInkPoints) break;
                    if (Numbers(pair) is { Length: 2 } p) points.Add(new PdfPoint(p[0], p[1]));
                }
                if (points.Count > 0) a.InkList.Add(points);
            }
            if (a.InkList.Count == 0) return null;
        }
        if (subtype is "Highlight" or "Underline" or "StrikeOut" or "Squiggly" && a.QuadPoints.Count == 0)
        {
            // Without coords the rectangle is the one quadrilateral (top edge first, as Acrobat writes them).
            var q = a.Rect;
            a.QuadPoints.Add(new[] { q.Left, q.Bottom, q.Right, q.Bottom, q.Left, q.Top, q.Right, q.Top });
        }
        if (e.Elements().FirstOrDefault(c => c.Name.LocalName == "popup") is { } popup && Numbers((string?)popup.Attribute("rect")) is { Length: 4 } pr)
        {
            a.PopupRect = new PdfRect(Math.Min(pr[0], pr[2]), Math.Min(pr[1], pr[3]), Math.Abs(pr[2] - pr[0]), Math.Abs(pr[3] - pr[1]));
            a.PopupOpen = (string?)popup.Attribute("open") == "yes";
        }
        return a;
    }

    private static string? Limit(string? s, int max = 1024) => s == null ? null : s.Length <= max ? s : s[..max];

    /// <summary>Comma- or space-separated numbers, all finite and within ±10⁷; null when any is not.</summary>
    private static double[]? Numbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 8 * MaxInkPoints) return null;
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || Math.Abs(v) > 1e7) return null;
            result[i] = v;
        }
        return result;
    }

    private static double[]? Colour(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return null;
        return new[] { (rgb >> 16 & 255) / 255.0, (rgb >> 8 & 255) / 255.0, (rgb & 255) / 255.0 };
    }

    private static string? Hex(double[]? c)
    {
        if (PdfAnnotation.ToRgb(c) is not (double r, double g, double b)) return null;
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{B(r):X2}{B(g):X2}{B(b):X2}");
    }

    private static string Number(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Join(params double[] values) => string.Join(",", values.Select(Number));
}
