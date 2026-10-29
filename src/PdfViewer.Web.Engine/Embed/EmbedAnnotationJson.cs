using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Web.Engine.Embed;

/// <summary>What <c>annotationManager.setConfig</c> changes; null for a key the host page did not give.</summary>
public sealed record EmbedAnnotationConfig(bool? ShowToolbar, bool? ShowCommentsPanel, bool? DownloadWithAnnotations, bool? PrintWithAnnotations, string? AuthorName);

/// <summary>The options of the host page's SAVE_API callback.</summary>
public sealed record EmbedSaveOptions(bool Enabled, int AutoSaveSeconds, bool FocusPolling, bool ShowSaveButton)
{
    public static EmbedSaveOptions None { get; } = new(false, 0, false, false);

    /// <summary>Longest autosave interval accepted, in seconds (one day).</summary>
    public const int MaxAutoSaveSeconds = 86400;
}

/// <summary>
/// The annotation JSON of the embed SDK, close to the Adobe PDF Embed API's (W3C Web Annotation
/// style). Every value comes from the host page and is untrusted: types, lengths, counts and
/// numbers are checked here; whether an annotation fits its page is checked by
/// <see cref="ViewerAnnotationStore.Check"/>.
/// </summary>
/// <remarks>
/// <code>
/// { id, type: "Annotation", motivation: "commenting" | "highlighting", bodyValue,
///   target: { source, selector: { node: { index }, subtype, boundingBox: [x1, y1, x2, y2],
///             quadPoints, inkList, vertices, lineEndings, stampName, strokeColor, fillColor,
///             dashPattern, strokeWidth, opacity, fontSize } },
///   creator: { type: "Person", name }, created, modified }
/// </code>
/// A reply is <c>{ id, type, motivation: "replying", bodyValue, target: "&lt;id it answers&gt;", creator, created, modified }</c>.
/// node.index counts pages from 0; coordinates are PDF points in the page's user space, origin at
/// the lower-left, y up.
/// </remarks>
public static class EmbedAnnotationJson
{
    private static readonly Regex Colour = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

    private static EmbedProtocolException Bad(int requestId, string message) => new(EmbedErrors.InvalidArgument, message, requestId);

    /// <summary>A list of annotations (addAnnotations): 1 to <see cref="ViewerAnnotationStore.MaxPerCall"/>.</summary>
    public static List<ViewerAnnotation> ParseList(JsonElement e, int requestId, string author, DateTimeOffset now)
    {
        if (e.ValueKind != JsonValueKind.Array) throw Bad(requestId, "addAnnotations: give an array of annotations.");
        int n = e.GetArrayLength();
        if (n == 0 || n > ViewerAnnotationStore.MaxPerCall) throw Bad(requestId, $"addAnnotations: give 1 to {ViewerAnnotationStore.MaxPerCall} annotations.");
        var list = new List<ViewerAnnotation>(n);
        int i = 0;
        long points = 0;
        foreach (var item in e.EnumerateArray())
        {
            var a = Parse(item, requestId, author, now, requireId: false, $"annotation {i++}");
            // The document-wide budget, checked again by the store against what the document has.
            points += ViewerAnnotationStore.Points(a);
            if (points > ViewerAnnotationStore.MaxPoints)
                throw Bad(requestId, $"addAnnotations: the annotations have more than {ViewerAnnotationStore.MaxPoints} ink and quadPoints points together.");
            list.Add(a);
        }
        return list;
    }

    /// <summary>One annotation. An id is made when it has none and <paramref name="requireId"/> is false.</summary>
    public static ViewerAnnotation Parse(JsonElement e, int requestId, string author, DateTimeOffset now, bool requireId, string what = "annotation")
    {
        if (e.ValueKind != JsonValueKind.Object) throw Bad(requestId, $"{what} must be an object.");
        string id = String(e, "id", ViewerAnnotationStore.MaxIdLength, what, requestId) ?? "";
        if (requireId && id.Length == 0) throw Bad(requestId, $"{what}: id is required.");
        if (id.Any(char.IsControl)) throw Bad(requestId, $"{what}: id has control characters.");
        string contents = String(e, "bodyValue", ViewerAnnotationStore.MaxContentsLength, what, requestId) ?? "";
        string name = author;
        if (Object(e, "creator", what, requestId) is { } creator && String(creator, "name", ViewerAnnotationStore.MaxAuthorLength, what + ".creator", requestId) is { } n) name = n;
        // Missing dates stay unset (default): the viewer gives them when it adds or updates.
        var created = Date(e, "created", what, requestId) ?? now;
        var modified = Date(e, "modified", what, requestId) ?? (created == default ? default : created);

        // A reply: motivation "replying" and target the id of the annotation it answers (its page and box come from that one).
        if (e.TryGetProperty("motivation", out var motivation) && motivation.ValueKind == JsonValueKind.String && motivation.GetString() == "replying")
        {
            string parent = e.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            if (parent.Length is 0 or > ViewerAnnotationStore.MaxIdLength || parent.Any(char.IsControl))
                throw Bad(requestId, $"{what}: a reply's target is the id of the annotation it answers.");
            return new ViewerAnnotation
            {
                Id = id, Page = 1, Kind = ViewerAnnotationKind.Reply, Box = default, InReplyTo = parent,
                Color = ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Reply),
                Contents = contents, Author = name, Created = created, Modified = modified,
            };
        }

        var target = Object(e, "target", what, requestId) ?? throw Bad(requestId, $"{what}: target is required.");
        var selector = Object(target, "selector", what + ".target", requestId) ?? throw Bad(requestId, $"{what}: target.selector is required.");
        string w = what + ".target.selector";
        var node = Object(selector, "node", w, requestId) ?? throw Bad(requestId, $"{w}: node is required.");
        int index = node.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number && ix.TryGetInt32(out int p) && p >= 0 && p < 1_000_000
            ? p : throw Bad(requestId, $"{w}.node.index must be a page index from 0.");
        string subtypeName = String(selector, "subtype", 32, w, requestId) ?? throw Bad(requestId, $"{w}: subtype is required.");
        var kind = ViewerAnnotation.ParseSubtype(subtypeName)
                   ?? throw Bad(requestId, $"{w}.subtype must be one of {string.Join(", ", ViewerAnnotation.SubtypeNames.Where(n => n != "reply"))} (a reply has motivation \"replying\").");

        double[]? quads = kind is ViewerAnnotationKind.Highlight or ViewerAnnotationKind.Underline or ViewerAnnotationKind.StrikeOut
            ? Numbers(selector, "quadPoints", 8 * ViewerAnnotationStore.MaxQuads, w, requestId) : null;
        if (quads != null && (quads.Length == 0 || quads.Length % 8 != 0)) throw Bad(requestId, $"{w}.quadPoints must have 8 numbers per quadrilateral.");
        List<IReadOnlyList<double>>? ink = null;
        if (kind == ViewerAnnotationKind.Ink)
        {
            if (!selector.TryGetProperty("inkList", out var il) || il.ValueKind != JsonValueKind.Array) throw Bad(requestId, $"{w}: an ink annotation needs inkList, an array of strokes.");
            int strokes = il.GetArrayLength();
            if (strokes == 0 || strokes > ViewerAnnotationStore.MaxInkStrokes) throw Bad(requestId, $"{w}.inkList must have 1 to {ViewerAnnotationStore.MaxInkStrokes} strokes.");
            ink = new();
            int total = 0;
            foreach (var stroke in il.EnumerateArray())
            {
                var pts = NumberArray(stroke, 2 * ViewerAnnotationStore.MaxInkPointsPerStroke, w + ".inkList", requestId);
                if (pts.Length < 2 || pts.Length % 2 != 0) throw Bad(requestId, $"{w}.inkList: each stroke is x, y pairs.");
                total += pts.Length / 2;
                if (total > ViewerAnnotationStore.MaxInkPoints) throw Bad(requestId, $"{w}.inkList has more than {ViewerAnnotationStore.MaxInkPoints} points.");
                ink.Add(pts);
            }
        }
        double[]? vertices = null;
        string start = "None", end = "None";
        if (kind is ViewerAnnotationKind.Line or ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine)
        {
            vertices = Numbers(selector, "vertices", 2 * ViewerAnnotationStore.MaxVertices, w, requestId)
                       ?? throw Bad(requestId, $"{w}: a {subtypeName} needs vertices, x, y pairs ([x1, y1, x2, y2] for a line).");
            int least = kind switch { ViewerAnnotationKind.Polygon => 6, _ => 4 };
            if (vertices.Length % 2 != 0 || vertices.Length < least || kind == ViewerAnnotationKind.Line && vertices.Length != 4)
                throw Bad(requestId, $"{w}.vertices must be x, y pairs: two points for a line, {least / 2} or more for a {subtypeName}.");
            if (selector.TryGetProperty("lineEndings", out var le) && le.ValueKind != JsonValueKind.Null)
            {
                if (le.ValueKind != JsonValueKind.Array || le.GetArrayLength() != 2 || le.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    throw Bad(requestId, $"{w}.lineEndings must be two names, [start, end].");
                start = le[0].GetString()!;
                end = le[1].GetString()!;
            }
        }
        string stampName = "";
        if (kind == ViewerAnnotationKind.Stamp)
        {
            stampName = String(selector, "stampName", 64, w, requestId) ?? "Draft";
            if (!ViewerAnnotationStore.StampNames.Contains(stampName))
                throw Bad(requestId, $"{w}.stampName must be one of {string.Join(", ", ViewerAnnotationStore.StampNames)}.");
        }
        double strokeWidth = Number(selector, "strokeWidth", 0, ViewerAnnotationStore.MaxStrokeWidth, w, requestId) ?? 1;
        var bb = Numbers(selector, "boundingBox", 4, w, requestId);
        ViewerBox box;
        if (bb is { Length: 4 }) box = ViewerBox.Normalized(bb[0], bb[1], bb[2], bb[3]);
        else if (bb != null) throw Bad(requestId, $"{w}.boundingBox must be [x1, y1, x2, y2].");
        else if (quads != null) box = ViewerBox.Around(quads);
        else if (ink != null) box = ViewerBox.Around(ink.SelectMany(s => s), strokeWidth / 2 + 1);
        else if (vertices != null) box = ViewerBox.Around(vertices, ViewerAnnotationStore.ShapeMargin(strokeWidth, start, end));
        else throw Bad(requestId, $"{w}: boundingBox is required.");

        string color = ColourValue(selector, "strokeColor", w, requestId) ?? ViewerAnnotationPdf.DefaultColor(kind);
        // dashPattern: dash and gap lengths in points; left out, null or [] is a solid line (the store checks the values).
        var dash = Numbers(selector, "dashPattern", ViewerAnnotationPdf.MaxDashCount, w, requestId);
        return new ViewerAnnotation
        {
            Id = id,
            Page = index + 1,
            Kind = kind,
            Box = box,
            QuadPoints = quads ?? [],
            InkList = ink ?? [],
            Vertices = vertices ?? [],
            StartEnding = start,
            EndEnding = end,
            StampName = stampName,
            Color = color.ToUpperInvariant(),
            FillColor = ColourValue(selector, "fillColor", w, requestId)?.ToUpperInvariant(),
            Opacity = Number(selector, "opacity", 0, 1, w, requestId) ?? 1,
            Dash = dash is { Length: > 0 } ? dash : null,
            StrokeWidth = strokeWidth,
            FontSize = Number(selector, "fontSize", ViewerAnnotationStore.MinFontSize, ViewerAnnotationStore.MaxFontSize, w, requestId) ?? 12,
            Contents = contents,
            Author = name,
            Created = created,
            Modified = modified,
        };
    }

    /// <summary>A filter: { annotationIds: [...], pageRange: { startPage, endPage } } (pages from 1); null or {} is every annotation.</summary>
    public static ViewerAnnotationFilter ParseFilter(JsonElement e, int requestId, string method)
    {
        if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return ViewerAnnotationFilter.All;
        if (e.ValueKind != JsonValueKind.Object) throw Bad(requestId, $"{method}: the filter must be an object.");
        HashSet<string>? ids = null;
        if (e.TryGetProperty("annotationIds", out var a) && a.ValueKind != JsonValueKind.Null)
        {
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() > ViewerAnnotationStore.MaxAnnotations)
                throw Bad(requestId, $"{method}: annotationIds must be an array of at most {ViewerAnnotationStore.MaxAnnotations} ids.");
            ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in a.EnumerateArray())
            {
                string? s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (s is not { Length: > 0 and <= ViewerAnnotationStore.MaxIdLength }) throw Bad(requestId, $"{method}: each annotation id must be a string of 1 to {ViewerAnnotationStore.MaxIdLength} characters.");
                ids.Add(s);
            }
        }
        int? start = null, end = null;
        if (e.TryGetProperty("pageRange", out var r) && r.ValueKind != JsonValueKind.Null)
        {
            if (r.ValueKind != JsonValueKind.Object) throw Bad(requestId, $"{method}: pageRange must be {{ startPage, endPage }}.");
            start = Page(r, "startPage");
            end = Page(r, "endPage");
            if (start > end) throw Bad(requestId, $"{method}: pageRange.startPage is after endPage.");
        }
        return ids == null && start == null && end == null ? ViewerAnnotationFilter.All : new ViewerAnnotationFilter(ids, start, end);

        int? Page(JsonElement range, string name) =>
            !range.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null ? null
            : v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int page) && page >= 1 ? page
            : throw Bad(requestId, $"{method}: pageRange.{name} must be a page number from 1.");
    }

    /// <summary>setConfig: { showToolbar, showCommentsPanel, downloadWithAnnotations, printWithAnnotations, authorName }.</summary>
    public static EmbedAnnotationConfig ParseConfig(JsonElement e, int requestId)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Bad(requestId, "setConfig: give an object.");
        bool? Flag(string name) =>
            !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null ? null
            : v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Bad(requestId, $"setConfig: {name} must be true or false.") };
        string? author = String(e, "authorName", ViewerAnnotationStore.MaxAuthorLength, "setConfig", requestId);
        if (author != null && author.Any(char.IsControl)) throw Bad(requestId, "setConfig: authorName has control characters.");
        return new EmbedAnnotationConfig(Flag("showToolbar"), Flag("showCommentsPanel"), Flag("downloadWithAnnotations"), Flag("printWithAnnotations"), author);
    }

    /// <summary>The SAVE_API options: { enabled, autoSaveFrequency (seconds, 0: never), enableFocusPolling, showSaveButton }.</summary>
    public static EmbedSaveOptions ParseSaveOptions(JsonElement e, int requestId)
    {
        if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return EmbedSaveOptions.None;
        if (e.ValueKind != JsonValueKind.Object) throw Bad(requestId, "The SAVE_API options must be an object.");
        bool Flag(string name, bool fallback) =>
            !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null ? fallback
            : v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Bad(requestId, $"SAVE_API: {name} must be true or false.") };
        double seconds = Number(e, "autoSaveFrequency", 0, EmbedSaveOptions.MaxAutoSaveSeconds, "SAVE_API", requestId) ?? 0;
        return new EmbedSaveOptions(Flag("enabled", true), (int)Math.Round(seconds), Flag("enableFocusPolling", false), Flag("showSaveButton", true));
    }

    // ------------------------------------------------------------------ writing

    /// <summary>One annotation as the host page gets it. <paramref name="source"/> is the file's id or name (target.source).</summary>
    public static void Write(Utf8JsonWriter w, ViewerAnnotation a, string source)
    {
        w.WriteStartObject();
        w.WriteStartArray("@context");
        w.WriteStringValue("http://www.w3.org/ns/anno.jsonld");
        w.WriteEndArray();
        w.WriteString("id", a.Id);
        w.WriteString("type", "Annotation");
        if (a.Kind == ViewerAnnotationKind.Reply)
        {
            w.WriteString("motivation", "replying");
            w.WriteString("bodyValue", a.Contents);
            w.WriteString("target", a.InReplyTo);
            WriteCreator(w, a);
            return;
        }
        w.WriteString("motivation", a.IsMarkup ? "highlighting" : "commenting");
        w.WriteString("bodyValue", a.Contents);
        w.WriteStartObject("target");
        w.WriteString("source", source);
        w.WriteStartObject("selector");
        w.WriteStartObject("node");
        w.WriteNumber("index", a.Page - 1);
        w.WriteEndObject();
        w.WriteString("type", "AnnotationSelector");
        w.WriteString("subtype", ViewerAnnotation.SubtypeName(a.Kind));
        WriteNumbers(w, "boundingBox", [a.Box.X1, a.Box.Y1, a.Box.X2, a.Box.Y2]);
        if (a.IsMarkup) WriteNumbers(w, "quadPoints", a.QuadPoints);
        if (a.Kind == ViewerAnnotationKind.Ink)
        {
            w.WriteStartArray("inkList");
            foreach (var stroke in a.InkList)
            {
                w.WriteStartArray();
                foreach (double v in stroke) w.WriteNumberValue(Round(v));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        if (a.HasVertices) WriteNumbers(w, "vertices", a.Vertices);
        if (a.HasEndings)
        {
            w.WriteStartArray("lineEndings");
            w.WriteStringValue(a.StartEnding);
            w.WriteStringValue(a.EndEnding);
            w.WriteEndArray();
        }
        if (a.Kind == ViewerAnnotationKind.Stamp) w.WriteString("stampName", a.StampName);
        w.WriteString("strokeColor", a.Color);
        if (a.FillColor != null) w.WriteString("fillColor", a.FillColor);
        if (a.Dash != null) WriteNumbers(w, "dashPattern", a.Dash);
        if (a.Kind is ViewerAnnotationKind.Ink or ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle or ViewerAnnotationKind.FreeText || a.HasVertices)
            w.WriteNumber("strokeWidth", Round(a.StrokeWidth));
        if (a.Kind == ViewerAnnotationKind.FreeText) w.WriteNumber("fontSize", Round(a.FontSize));
        w.WriteNumber("opacity", Round(a.Opacity));
        w.WriteEndObject();
        w.WriteEndObject();
        WriteCreator(w, a);
    }

    /// <summary>The creator, the dates and the end of the annotation's object.</summary>
    private static void WriteCreator(Utf8JsonWriter w, ViewerAnnotation a)
    {
        w.WriteStartObject("creator");
        w.WriteString("type", "Person");
        w.WriteString("name", a.Author);
        w.WriteEndObject();
        WriteDate(w, "created", a.Created);
        WriteDate(w, "modified", a.Modified);
        w.WriteEndObject();
    }

    public static Action<Utf8JsonWriter> List(IEnumerable<ViewerAnnotation> list, string source) => w =>
    {
        w.WriteStartArray();
        foreach (var a in list) Write(w, a, source);
        w.WriteEndArray();
    };

    private static void WriteDate(Utf8JsonWriter w, string name, DateTimeOffset date)
    {
        if (date == default) w.WriteNull(name);
        else w.WriteString(name, date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    private static void WriteNumbers(Utf8JsonWriter w, string name, IEnumerable<double> values)
    {
        w.WriteStartArray(name);
        foreach (double v in values) w.WriteNumberValue(Round(v));
        w.WriteEndArray();
    }

    private static double Round(double v) => Math.Round(v, 3);

    // ------------------------------------------------------------------ reading values

    private static JsonElement? Object(JsonElement e, string name, string what, int requestId)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Object ? v : throw Bad(requestId, $"{what}.{name} must be an object.");
    }

    private static string? String(JsonElement e, string name, int max, string what, int requestId)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        string? s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return s != null && s.Length <= max ? s : throw Bad(requestId, $"{what}.{name} must be a string of at most {max} characters.");
    }

    private static DateTimeOffset? Date(JsonElement e, string name, string what, int requestId)
    {
        string? s = String(e, name, 64, what, requestId);
        if (s == null) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year is >= 1900 and <= 9999
            ? d.ToUniversalTime()
            : throw Bad(requestId, $"{what}.{name} must be an ISO 8601 date (2026-10-26T10:00:00Z).");
    }

    private static double? Number(JsonElement e, string name, double min, double max, string what, int requestId)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double x) && double.IsFinite(x) && x >= min && x <= max
            ? x
            : throw Bad(requestId, $"{what}.{name} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static double[]? Numbers(JsonElement e, string name, int max, string what, int requestId)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return NumberArray(v, max, $"{what}.{name}", requestId);
    }

    /// <summary>An array of at most <paramref name="max"/> finite numbers within ±10⁶.</summary>
    private static double[] NumberArray(JsonElement v, int max, string what, int requestId)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > max) throw Bad(requestId, $"{what} must be an array of at most {max} numbers.");
        var result = new double[v.GetArrayLength()];
        int i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double x) || !double.IsFinite(x) || Math.Abs(x) > 1e6)
                throw Bad(requestId, $"{what} must hold finite numbers (PDF points).");
            result[i++] = x;
        }
        return result;
    }

    private static string? ColourValue(JsonElement e, string name, string what, int requestId)
    {
        string? s = String(e, name, 7, what, requestId);
        return s == null ? null : Colour.IsMatch(s) ? s : throw Bad(requestId, $"{what}.{name} must be a colour \"#RRGGBB\".");
    }
}
