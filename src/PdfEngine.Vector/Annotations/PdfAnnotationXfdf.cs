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

/// <summary>The annotations an XFDF document gave, and how many of a supported type were skipped.</summary>
public sealed record PdfXfdfImport(List<PdfAnnotation> Annotations, int Skipped);

/// <summary>
/// Annotations as XFDF (ISO 19444-1, the <c>&lt;annots&gt;</c> element), in PDF user space: the
/// markup and comment types a reader makes (highlight, underline, strike-out, squiggly, note, free
/// text with its callout line, ink, square, circle, line, polygon, polyline, stamp) and replies (inreplyto, the parent's
/// name; a status reply's state and statemodel). Export writes <see cref="PdfAnnotation"/>s; import reads them
/// back, ready for <see cref="Services.PdfAnnotationEditor.Add"/>. Imported XML is untrusted: no DTD,
/// no external entities, a size limit, an element limit, finite numbers only, and elements of other
/// types skipped.
/// </summary>
public static class PdfAnnotationXfdf
{
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";

    /// <summary>Largest XFDF document read, in characters.</summary>
    public const int MaxCharacters = 8 * 1024 * 1024;

    /// <summary>Most annotations read from one document.</summary>
    public const int MaxAnnotations = 10000;

    /// <summary>Most points in one ink stroke, and strokes in one ink annotation (<see cref="PdfAnnotationLimits"/>).</summary>
    public const int MaxInkPoints = PdfAnnotationLimits.MaxInkPointsPerStroke, MaxInkStrokes = PdfAnnotationLimits.MaxInkStrokes;

    /// <summary>Most elements read from one document (a skipped element counts once, with its content).</summary>
    public const int MaxElements = 100_000;

    /// <summary>Most attributes of one element.</summary>
    public const int MaxAttributes = 64;

    /// <summary>Most numbers in a dash pattern.</summary>
    public const int MaxDashes = 16;

    /// <summary>Longest status name read (a reply's state and statemodel: "Accepted", "Review").</summary>
    public const int MaxStateLength = 64;

    private static readonly Dictionary<string, string> ElementForSubtype = new(StringComparer.Ordinal)
    {
        ["Highlight"] = "highlight", ["Underline"] = "underline", ["StrikeOut"] = "strikeout", ["Squiggly"] = "squiggly",
        ["Text"] = "text", ["FreeText"] = "freetext", ["Ink"] = "ink", ["Square"] = "square", ["Circle"] = "circle", ["Line"] = "line",
        ["Polygon"] = "polygon", ["PolyLine"] = "polyline", ["Stamp"] = "stamp",
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
        var list = annotations.ToList();
        // inreplyto names the parent: its /NM, found by the object number /IRT gives. A parent
        // without /NM is named after its object number here, so the reply keeps its thread.
        var replied = list.Where(a => a.InReplyTo > 0).Select(a => a.InReplyTo).ToHashSet();
        var names = new Dictionary<int, string>();
        foreach (var a in list)
        {
            if (a.ObjectNumber <= 0) continue;
            if (!string.IsNullOrEmpty(a.Name)) names.TryAdd(a.ObjectNumber, a.Name);
            else if (replied.Contains(a.ObjectNumber)) names.TryAdd(a.ObjectNumber, "obj-" + a.ObjectNumber.ToString(CultureInfo.InvariantCulture));
        }
        var annots = new XElement(Ns + "annots");
        foreach (var a in list)
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
            if ((string.IsNullOrEmpty(a.Name) && a.ObjectNumber > 0 ? names.GetValueOrDefault(a.ObjectNumber) : a.Name) is { Length: > 0 } nm)
                e.Add(new XAttribute("name", nm));
            if (!string.IsNullOrEmpty(a.ModifiedDate)) e.Add(new XAttribute("date", a.ModifiedDate));
            if (!string.IsNullOrEmpty(a.CreationDate)) e.Add(new XAttribute("creationdate", a.CreationDate));
            string flags = string.Join(",", FlagNames.Where(f => (a.Flags & f.Bit) != 0).Select(f => f.Name));
            if (flags.Length > 0) e.Add(new XAttribute("flags", flags));
            if (a.BorderWidth is double w) e.Add(new XAttribute("width", Number(w)));
            if (a.BorderDash is { Length: > 0 } dash && a.BorderStyle is null or "D") e.Add(new XAttribute("style", "dash"), new XAttribute("dashes", Join(dash)));
            if (a.QuadPoints.Count > 0) e.Add(new XAttribute("coords", Join(a.QuadPoints.SelectMany(q => q).ToArray())));
            if (a.Subtype is "Text" or "Stamp" && !string.IsNullOrEmpty(a.Icon)) e.Add(new XAttribute("icon", a.Icon));
            if (a.Subtype == "Line" && a.Line is { Length: 4 } l)
                e.Add(new XAttribute("start", Join(l[0], l[1])), new XAttribute("end", Join(l[2], l[3])));
            if (a.Subtype is "Line" or "PolyLine" && a.LineEndings is { Length: 2 } ends)
            {
                if (ends[0] != "None") e.Add(new XAttribute("head", ends[0]));
                if (ends[1] != "None") e.Add(new XAttribute("tail", ends[1]));
            }
            string? parentName = a.InReplyTo > 0 && names.TryGetValue(a.InReplyTo, out var n) ? n : a.InReplyToName;
            if (!string.IsNullOrEmpty(parentName))
            {
                e.Add(new XAttribute("inreplyto", parentName));
                // XFDF (ISO 19444-1) replyType: "reply" (/RT /R, or none) or "group"; a reader takes a missing one as a reply.
                e.Add(new XAttribute("replyType", a.ReplyType == "Group" ? "group" : "reply"));
                // A review or marked status (a reply with /State and /StateModel, as Acrobat writes them).
                if (!string.IsNullOrEmpty(a.StateModel) && !string.IsNullOrEmpty(a.State))
                    e.Add(new XAttribute("state", a.State), new XAttribute("statemodel", a.StateModel));
            }
            if (a.Subtype == "FreeText" && a.Justification is 1 or 2) e.Add(new XAttribute("justification", a.Justification == 1 ? "centered" : "right"));
            if (a.Subtype == "FreeText")
            {
                // A callout as Acrobat writes it: its line (callout), the ending at the pointed-at
                // spot (head), its intent, and the text box inside rect (fringe, /RD).
                if (a.CalloutLine is { Length: 4 or 6 } cl && cl.All(double.IsFinite)) e.Add(new XAttribute("callout", Join(cl)));
                if (a.LineEndings is { Length: 1 } fe && fe[0] != "None" && PdfAnnotationLimits.LineEndings.Contains(fe[0])) e.Add(new XAttribute("head", fe[0]));
                if (IsIntentName(a.Intent)) e.Add(new XAttribute("intent", a.Intent!));
                if (a.RectDifferences is { Length: 4 } rd && rd.All(v => double.IsFinite(v) && v >= 0)) e.Add(new XAttribute("fringe", Join(rd)));
            }
            if (!string.IsNullOrEmpty(a.Contents)) e.Add(new XElement(Ns + "contents", a.Contents));
            if (a.Subtype == "FreeText" && !string.IsNullOrEmpty(a.DefaultAppearance)) e.Add(new XElement(Ns + "defaultappearance", a.DefaultAppearance));
            if (a.Subtype == "Ink" && a.InkList.Count > 0)
                e.Add(new XElement(Ns + "inklist", a.InkList.Select(s => new XElement(Ns + "gesture", string.Join(";", s.Select(p => Join(p.X, p.Y)))))));
            if (a.Subtype is "Polygon" or "PolyLine" && a.Vertices.Count > 0)
                e.Add(new XElement(Ns + "vertices", string.Join(";", a.Vertices.Select(p => Join(p.X, p.Y)))));
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
    public static List<PdfAnnotation> Import(string xml) => ImportWithReport(xml).Annotations;

    /// <summary>
    /// <see cref="Import"/>, with the number of annotations of a supported type that were skipped:
    /// unreadable (no page or rectangle, an ink annotation without points) or larger than
    /// <see cref="PdfAnnotationLimits"/> allows (more than <see cref="PdfAnnotationLimits.MaxQuads"/>
    /// quadrilaterals). Long texts are cut to the limits instead (author, subject and name to
    /// <see cref="PdfAnnotationLimits.MaxTextLength"/>, contents to <see cref="PdfAnnotationLimits.MaxContentsLength"/>),
    /// so one long field does not refuse the whole import. The XML is read as a stream: no DTD, no
    /// resolver, at most <see cref="MaxElements"/> elements and <see cref="MaxAttributes"/>
    /// attributes per element; unknown elements are skipped without being built.
    /// </summary>
    public static PdfXfdfImport ImportWithReport(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.Length > MaxCharacters) throw new FormatException($"The XFDF document is longer than {MaxCharacters / (1024 * 1024)} MB.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
        };
        var result = new List<PdfAnnotation>();
        int skipped = 0;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var counter = new ElementCounter();
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "xfdf")
                throw new FormatException("The document is not XFDF (its root element is not <xfdf>).");
            counter.Count(reader);
            Children(reader, counter, r =>
            {
                if (r.LocalName != "annots")
                {
                    r.Skip();
                    return;
                }
                Children(r, counter, e =>
                {
                    if (!SubtypeForElement.TryGetValue(e.LocalName, out var subtype))
                    {
                        e.Skip();
                        return;
                    }
                    if (result.Count >= MaxAnnotations) throw new FormatException($"The XFDF document has more than {MaxAnnotations} annotations.");
                    var element = ReadElement(e, counter, subtype);
                    if (element.TooLarge || Read(element, subtype) is not { } a) skipped++;
                    else result.Add(a);
                });
            });
            // Whatever follows the root element must still be well-formed.
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            throw new FormatException("The text is not a well-formed XFDF document.", ex);
        }
        return new PdfXfdfImport(result, skipped);
    }

    /// <summary>Counts the elements read and refuses an element with too many attributes.</summary>
    private sealed class ElementCounter
    {
        private int _elements;

        public void Count(XmlReader r)
        {
            if (++_elements > MaxElements) throw new FormatException($"The XFDF document has more than {MaxElements} elements.");
            if (r.AttributeCount > MaxAttributes) throw new FormatException($"An element of the XFDF document has more than {MaxAttributes} attributes.");
        }
    }

    /// <summary>
    /// With the reader on a start element: calls <paramref name="element"/> for each child element
    /// (which must read past it, with <see cref="XmlReader.Skip"/> at least) and <paramref name="text"/>
    /// for each piece of text, then leaves the reader after the element's end.
    /// </summary>
    private static void Children(XmlReader r, ElementCounter counter, Action<XmlReader> element, Action<string>? text = null)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return;
        }
        int depth = r.Depth;
        r.Read();
        while (!r.EOF)
        {
            switch (r.NodeType)
            {
                case XmlNodeType.EndElement when r.Depth == depth:
                    r.Read();
                    return;
                case XmlNodeType.Element:
                    counter.Count(r);
                    element(r);
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    text?.Invoke(r.Value);
                    r.Read();
                    break;
                default:
                    r.Read();
                    break;
            }
        }
    }

    /// <summary>The text of the element (child elements skipped), cut to <paramref name="max"/> characters; the reader goes past it.</summary>
    private static string Text(XmlReader r, ElementCounter counter, int max)
    {
        var sb = new StringBuilder();
        Children(r, counter, e => e.Skip(), t =>
        {
            if (sb.Length < max) sb.Append(t, 0, Math.Min(t.Length, max - sb.Length));
        });
        return sb.ToString();
    }

    /// <summary>What one annotation element says: its attributes and the children this reader knows.</summary>
    private sealed class XfdfElement
    {
        public readonly Dictionary<string, string> Attributes = new(StringComparer.Ordinal);
        public string? Contents, DefaultAppearance, Vertices;
        public readonly List<string> Gestures = new();
        public Dictionary<string, string>? Popup;
        public bool TooLarge;

        public string? this[string name] => Attributes.TryGetValue(name, out var v) ? v : null;
    }

    private static Dictionary<string, string> Attributes(XmlReader r)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.MoveToFirstAttribute())
        {
            do
            {
                if (r.NamespaceURI.Length == 0) attributes[r.LocalName] = r.Value;
            }
            while (r.MoveToNextAttribute());
            r.MoveToElement();
        }
        return attributes;
    }

    private static XfdfElement ReadElement(XmlReader r, ElementCounter counter, string subtype)
    {
        var e = new XfdfElement();
        foreach (var (k, v) in Attributes(r)) e.Attributes[k] = v;
        // More quadrilaterals than the limit: the annotation is skipped, not cut (a cut highlight would mark the wrong text).
        if (e["coords"] is { } coords && CountNumbers(coords) > 8 * PdfAnnotationLimits.MaxQuads) e.TooLarge = true;
        Children(r, counter, c =>
        {
            switch (c.LocalName)
            {
                case "contents" when e.Contents == null:
                    e.Contents = Text(c, counter, PdfAnnotationLimits.MaxContentsLength);
                    break;
                case "defaultappearance" when e.DefaultAppearance == null && subtype == "FreeText":
                    e.DefaultAppearance = Text(c, counter, 1000);
                    break;
                case "inklist" when subtype == "Ink":
                    Children(c, counter, g =>
                    {
                        if (g.LocalName == "gesture" && e.Gestures.Count < MaxInkStrokes) e.Gestures.Add(Text(g, counter, MaxCharacters));
                        else g.Skip();
                    });
                    break;
                case "vertices" when e.Vertices == null && subtype is "Polygon" or "PolyLine":
                    e.Vertices = Text(c, counter, MaxCharacters);
                    // More points than the limit: skipped, not cut (a cut polygon has another shape).
                    if (CountNumbers(e.Vertices.Replace(';', ',')) > 2 * PdfAnnotationLimits.MaxVertices) e.TooLarge = true;
                    break;
                case "popup" when e.Popup == null:
                    e.Popup = Attributes(c);
                    c.Skip();
                    break;
                default:
                    c.Skip();
                    break;
            }
        });
        return e;
    }

    private static int CountNumbers(string text) =>
        text.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static PdfAnnotation? Read(XfdfElement e, string subtype)
    {
        if (!int.TryParse(e["page"], NumberStyles.None, CultureInfo.InvariantCulture, out int page) || page < 0 || page > 1_000_000) return null;
        if (Numbers(e["rect"]) is not { Length: 4 } r) return null;
        var a = new PdfAnnotation
        {
            PageNumber = page + 1,
            Subtype = subtype,
            Rect = new PdfRect(Math.Min(r[0], r[2]), Math.Min(r[1], r[3]), Math.Abs(r[2] - r[0]), Math.Abs(r[3] - r[1])),
            Color = Colour(e["color"]),
            InteriorColor = Colour(e["interior-color"]),
            Opacity = double.TryParse(e["opacity"], NumberStyles.Float, CultureInfo.InvariantCulture, out double o) && double.IsFinite(o) ? Math.Clamp(o, 0, 1) : 1,
            Author = Limit(e["title"], PdfAnnotationLimits.MaxTextLength),
            Subject = Limit(e["subject"], PdfAnnotationLimits.MaxTextLength),
            Name = Limit(e["name"], PdfAnnotationLimits.MaxTextLength),
            ModifiedDate = Limit(e["date"], 64),
            CreationDate = Limit(e["creationdate"], 64),
            Contents = e.Contents,
        };
        if (e["flags"] is { } flags)
        {
            int f = 0;
            foreach (var part in flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                f |= FlagNames.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).Bit;
            a.Flags = f;
        }
        if (double.TryParse(e["width"], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && double.IsFinite(w) && w >= 0 && w <= 1000)
            a.BorderWidth = w;
        if (e["style"] == "dash") a.BorderDash = Dashes(e["dashes"]);
        if (Numbers(e["coords"]) is { Length: >= 8 } coords)
            for (int k = 0; k + 7 < coords.Length && a.QuadPoints.Count < PdfAnnotationLimits.MaxQuads; k += 8) a.QuadPoints.Add(coords[k..(k + 8)]);
        if (subtype == "Text") a.Icon = Limit(e["icon"], 64) is { Length: > 0 } icon && icon.All(char.IsAsciiLetterOrDigit) ? icon : "Note";
        if (subtype == "Stamp") a.Icon = Limit(e["icon"], 64) is { Length: > 0 } stamp && stamp.All(char.IsAsciiLetterOrDigit) ? stamp : "Draft";
        if (subtype == "Line")
        {
            if (Numbers(e["start"]) is not { Length: 2 } s || Numbers(e["end"]) is not { Length: 2 } t) return null;
            a.Line = new[] { s[0], s[1], t[0], t[1] };
        }
        if (subtype is "Line" or "PolyLine" && (e["head"] != null || e["tail"] != null))
            a.LineEndings = new[] { Ending(e["head"]), Ending(e["tail"]) };
        if (subtype is "Polygon" or "PolyLine")
        {
            if (Numbers(e.Vertices?.Replace(';', ',')) is not { Length: >= 4 } v || v.Length % 2 != 0) return null;
            for (int k = 0; k + 1 < v.Length; k += 2) a.Vertices.Add(new PdfPoint(v[k], v[k + 1]));
        }
        if (Limit(e["inreplyto"], PdfAnnotationLimits.MaxTextLength) is { Length: > 0 } parent && parent != a.Name)
        {
            a.InReplyToName = parent;
            a.ReplyType = string.Equals(e["replyType"], "group", StringComparison.OrdinalIgnoreCase) ? "Group" : null;
            // A status: both names, at most MaxStateLength characters, no control characters. A name
            // that breaks these rules is not cut to fit: the annotation is skipped (counted).
            string? state = e["state"], model = e["statemodel"];
            bool BadName(string? s) => s != null && (s.Length > MaxStateLength || s.Any(char.IsControl));
            if (BadName(state) || BadName(model)) return null;
            if (state is { Length: > 0 } && model is { Length: > 0 })
            {
                a.State = state;
                a.StateModel = model;
            }
        }
        if (subtype == "FreeText")
        {
            a.DefaultAppearance = e.DefaultAppearance ?? Limit(e["defaultappearance"], 1000);
            a.Justification = e["justification"] switch { "centered" => 1, "right" => 2, _ => 0 };
            // A callout: its line of two or three points, the ending at its first point, its
            // intent and the text box inside rect. A line of another length is left out, not the annotation.
            if (Numbers(e["callout"]) is { Length: 4 or 6 } cl) a.CalloutLine = cl;
            if (e["head"] is { } head && Ending(head) is var ending && ending != "None") a.LineEndings = new[] { ending };
            if (IsIntentName(e["intent"])) a.Intent = e["intent"];
            if (Numbers(e["fringe"]) is { Length: 4 } fringe && fringe.All(v => v >= 0)) a.RectDifferences = fringe;
        }
        if (subtype == "Ink")
        {
            int total = 0;
            foreach (var gesture in e.Gestures)
            {
                if (a.InkList.Count >= MaxInkStrokes || total >= PdfAnnotationLimits.MaxInkPoints) break;
                var points = new List<PdfPoint>();
                foreach (var pair in gesture.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (points.Count >= MaxInkPoints || total + points.Count >= PdfAnnotationLimits.MaxInkPoints) break;
                    if (Numbers(pair) is { Length: 2 } p) points.Add(new PdfPoint(p[0], p[1]));
                }
                total += points.Count;
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
        if (e.Popup is { } popup && Numbers(popup.GetValueOrDefault("rect")) is { Length: 4 } pr)
        {
            a.PopupRect = new PdfRect(Math.Min(pr[0], pr[2]), Math.Min(pr[1], pr[3]), Math.Abs(pr[2] - pr[0]), Math.Abs(pr[3] - pr[1]));
            a.PopupOpen = popup.GetValueOrDefault("open") == "yes";
        }
        return a;
    }

    /// <summary>A dash pattern: 1 to <see cref="MaxDashes"/> numbers, none negative, not all zero; else the default [3].</summary>
    internal static double[] Dashes(string? text) =>
        Numbers(text) is { Length: > 0 and <= MaxDashes } dashes && dashes.All(d => d >= 0) && dashes.Sum() > 0 ? dashes : new[] { 3.0 };

    /// <summary>An /IT name this class writes and reads: 1 to 64 ASCII letters (FreeTextCallout, FreeTextTypewriter).</summary>
    private static bool IsIntentName(string? name) => name is { Length: > 0 and <= 64 } && name.All(char.IsAsciiLetter);

    /// <summary>A line ending name of Table 179, else None.</summary>
    private static string Ending(string? name) => name != null && PdfAnnotationLimits.LineEndings.Contains(name) ? name : "None";

    private static string? Limit(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

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
