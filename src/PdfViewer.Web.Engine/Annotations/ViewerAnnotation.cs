using System.Globalization;
using System.Text.RegularExpressions;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Services;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>
/// The annotation types the viewer creates, shows in its comments panel and lets a host page
/// manage. A reply is a note (/Subtype /Text) that answers another annotation (/IRT, /RT /R).
/// </summary>
public enum ViewerAnnotationKind { Highlight, Underline, StrikeOut, Note, FreeText, Ink, Square, Circle, Line, Polygon, PolyLine, Stamp, Reply }

/// <summary>A rectangle in PDF user space (points, y up): (X1, Y1) its lower-left corner, (X2, Y2) its upper-right.</summary>
public readonly record struct ViewerBox(double X1, double Y1, double X2, double Y2)
{
    public double Width => X2 - X1;
    public double Height => Y2 - Y1;

    /// <summary>The box with its corners in order (X1 ≤ X2, Y1 ≤ Y2).</summary>
    public static ViewerBox Normalized(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    /// <summary>The smallest box around the points (x, y, x, y, …).</summary>
    public static ViewerBox Around(IEnumerable<double> xy, double grow = 0)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        bool isX = true;
        foreach (double v in xy)
        {
            if (isX) { x1 = Math.Min(x1, v); x2 = Math.Max(x2, v); }
            else { y1 = Math.Min(y1, v); y2 = Math.Max(y2, v); }
            isX = !isX;
        }
        return x1 > x2 ? default : new ViewerBox(x1 - grow, y1 - grow, x2 + grow, y2 + grow);
    }

    public ViewerBox Offset(double dx, double dy) => new(X1 + dx, Y1 + dy, X2 + dx, Y2 + dy);

    public PdfRect ToRect() => new(X1, Y1, Width, Height);
}

/// <summary>
/// One annotation as the viewer and its host page see it: geometry in PDF user space (points,
/// origin at the lower-left of the page's coordinate system, y up, the page's /Rotate not
/// applied), its colours as "#RRGGBB", and its text. Immutable: every change makes a new record,
/// so the editor tells changed annotations by reference.
/// </summary>
public sealed record ViewerAnnotation
{
    public required string Id { get; init; }

    /// <summary>1-based page number.</summary>
    public required int Page { get; init; }

    public required ViewerAnnotationKind Kind { get; init; }

    /// <summary>/Rect: where the annotation is drawn.</summary>
    public required ViewerBox Box { get; init; }

    /// <summary>Text markup: eight numbers per quadrilateral (x1 y1 x2 y2 x3 y3 x4 y4: upper-left, upper-right, lower-left, lower-right).</summary>
    public IReadOnlyList<double> QuadPoints { get; init; } = [];

    /// <summary>Ink: the strokes, each x, y, x, y, ….</summary>
    public IReadOnlyList<IReadOnlyList<double>> InkList { get; init; } = [];

    /// <summary>A line's two points (/L: x1, y1, x2, y2), or a polygon's or polyline's points (/Vertices: x, y, x, y, …).</summary>
    public IReadOnlyList<double> Vertices { get; init; } = [];

    /// <summary>
    /// A callout's line (/CL): the spot it points to (its anchor) first, then an optional knee, then
    /// the point on the text box; 4 or 6 numbers in user space. Empty for a plain text box and every
    /// other kind. A callout is free text: its <see cref="Box"/> is its text box (/Rect less /RD),
    /// and its /Rect, which holds the line too, is worked out when it is written.
    /// </summary>
    public IReadOnlyList<double> Callout { get; init; } = [];

    /// <summary>A text box with a line to the spot it points to (/IT /FreeTextCallout).</summary>
    public bool IsCallout => Kind == ViewerAnnotationKind.FreeText && Callout.Count >= 4;

    /// <summary>
    /// The line endings (/LE, ISO 32000-2 Table 179) at the first and the last point of a line or
    /// polyline. A callout's ending at its anchor is <see cref="StartEnding"/> (its /LE).
    /// </summary>
    public string StartEnding { get; init; } = "None";
    public string EndEnding { get; init; } = "None";

    /// <summary>A stamp's /Name (Approved, Draft, Confidential, …).</summary>
    public string StampName { get; init; } = "";

    /// <summary>
    /// A new stamp's second line (a dynamic stamp: "By Jane Doe at 10:42, 25 Oct 2026"), or null.
    /// Written into the appearance it is given; a file's stamp keeps the appearance it has.
    /// </summary>
    public string? StampSubtitle { get; init; }

    /// <summary>
    /// A new stamp's turn in degrees counter-clockwise (0, 90, 180 or 270): its page's /Rotate, so
    /// that it reads upright as the page is shown. <see cref="Box"/> holds the turned stamp.
    /// </summary>
    public int StampTurn { get; init; }

    /// <summary>
    /// A custom stamp made here: the picture or PDF page it draws (its appearance is written from
    /// it, kept to its shape). Null for every other annotation, and for a file's stamp (which keeps
    /// the appearance it has).
    /// </summary>
    public ViewerCustomStamp? CustomStamp { get; init; }

    /// <summary>A custom stamp: made here from a picture or a PDF page, or a file's stamp named <see cref="ViewerCustomStamp.StampName"/>.</summary>
    public bool IsCustomStamp => Kind == ViewerAnnotationKind.Stamp && (CustomStamp != null || StampName == ViewerCustomStamp.StampName);

    /// <summary>A reply: the id of the annotation it answers. Empty for every other kind.</summary>
    public string InReplyTo { get; init; } = "";

    /// <summary>
    /// A status reply's /State and /StateModel ("Accepted" in the "Review" model), as Acrobat writes
    /// a review status: a hidden reply. Empty for an ordinary reply and for every other kind.
    /// </summary>
    public string State { get; init; } = "";
    public string StateModel { get; init; } = "";

    /// <summary>
    /// A reply that sets a status on the annotation it answers (it has a state model) rather than
    /// saying something. Read from a file only when it is hidden (/F with the Hidden bit, as Acrobat writes it).
    /// </summary>
    public bool IsStatus => Kind == ViewerAnnotationKind.Reply && StateModel.Length > 0;

    /// <summary>The line (or text, for free text) colour.</summary>
    public string Color { get; init; } = "#FFD400";

    /// <summary>The inside of a rectangle or ellipse, the background of free text; null for none.</summary>
    public string? FillColor { get; init; }

    public double Opacity { get; init; } = 1;

    /// <summary>
    /// The dash of a rectangle's, ellipse's, line's, polygon's or polyline's line (/BS /S /D /D:
    /// dash and gap lengths in points); null for a solid line.
    /// </summary>
    public IReadOnlyList<double>? Dash { get; init; }

    /// <summary>A file's annotation with /Measure: its fill, dash and line endings are not changed.</summary>
    public bool IsMeasurement { get; init; }

    /// <summary>
    /// A measurement's scale (/Measure as read from the file), so it is measured again when one of
    /// its points moves. Not in a host page's JSON nor in a kept snapshot (the store then takes it
    /// from the file's annotation with the same id).
    /// </summary>
    public PdfMeasure? Measure { get; init; }

    /// <summary>The line width of ink, rectangles, ellipses and the border of free text (0: none).</summary>
    public double StrokeWidth { get; init; } = 1;

    /// <summary>The text size of free text.</summary>
    public double FontSize { get; init; } = 12;

    /// <summary>/Contents: the comment of a note or markup, the text of free text.</summary>
    public string Contents { get; init; } = "";

    public string Author { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }

    /// <summary>Its position in the page's /Annots in the file as opened; -1 for one added since.</summary>
    public int SourceIndex { get; init; } = -1;

    public bool FromFile => SourceIndex >= 0;

    /// <summary>The annotation moved by (dx, dy) points: its box and every point.</summary>
    public ViewerAnnotation Moved(double dx, double dy) => this with
    {
        Box = Box.Offset(dx, dy),
        QuadPoints = QuadPoints.Select((v, i) => i % 2 == 0 ? v + dx : v + dy).ToArray(),
        InkList = InkList.Select(s => (IReadOnlyList<double>)s.Select((v, i) => i % 2 == 0 ? v + dx : v + dy).ToArray()).ToArray(),
        Vertices = Vertices.Select((v, i) => i % 2 == 0 ? v + dx : v + dy).ToArray(),
        Callout = Callout.Select((v, i) => i % 2 == 0 ? v + dx : v + dy).ToArray(),
    };

    /// <summary>The kinds' names in the host page's JSON (target.selector.subtype); a reply has motivation "replying" instead.</summary>
    public static readonly string[] SubtypeNames = ["highlight", "underline", "strikeout", "note", "freetext", "ink", "square", "circle", "line", "polygon", "polyline", "stamp", "reply"];

    public static string SubtypeName(ViewerAnnotationKind kind) => SubtypeNames[(int)kind];

    /// <summary>The kind of a target.selector.subtype; null for an unknown name and for "reply" (a reply has no selector).</summary>
    public static ViewerAnnotationKind? ParseSubtype(string? name)
    {
        int i = Array.IndexOf(SubtypeNames, name);
        return i < 0 || (ViewerAnnotationKind)i == ViewerAnnotationKind.Reply ? null : (ViewerAnnotationKind)i;
    }

    /// <summary>Text markup (motivation "highlighting"); the others are comments.</summary>
    public bool IsMarkup => Kind is ViewerAnnotationKind.Highlight or ViewerAnnotationKind.Underline or ViewerAnnotationKind.StrikeOut;

    /// <summary>A line, polygon or polyline: its geometry is <see cref="Vertices"/>.</summary>
    public bool HasVertices => Kind is ViewerAnnotationKind.Line or ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine;

    /// <summary>A line or polyline, which may have line endings.</summary>
    public bool HasEndings => Kind is ViewerAnnotationKind.Line or ViewerAnnotationKind.PolyLine;

    /// <summary>A rectangle, ellipse, line, polygon or polyline: it may have a fill and a dash.</summary>
    public bool IsShape => Kind is ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle || HasVertices;

    /// <summary>A shape whose fill, dash and line endings the reader may change (not a measurement).</summary>
    public bool HasShapeStyle => IsShape && !IsMeasurement;

    /// <summary>The dash presets, in points: dashed and dotted.</summary>
    public static IReadOnlyList<double> Dashed { get; } = [4, 3];
    public static IReadOnlyList<double> Dotted { get; } = [1, 2];

    /// <summary>
    /// Two dashes are the same (null: solid), each number within 0.1 % (at least 0.001 points), so
    /// a file's dash given back by a host page with fewer digits is not a change.
    /// </summary>
    public static bool SameDash(IReadOnlyList<double>? a, IReadOnlyList<double>? b) =>
        a == null ? b == null
        : b != null && a.Count == b.Count && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 1e-3 * Math.Max(1, Math.Max(Math.Abs(p.First), Math.Abs(p.Second))));
}

/// <summary>
/// How a page's user space maps to the page as shown: the crop box's origin, y down, then the
/// page's /Rotate (clockwise). Shown coordinates are PDF points from the shown page's top-left corner.
/// </summary>
public readonly record struct ViewerPageGeometry(PdfRect MediaBox, PdfRect CropBox, int Rotation)
{
    public double ShownWidth => Rotation is 90 or 270 ? CropBox.Height : CropBox.Width;
    public double ShownHeight => Rotation is 90 or 270 ? CropBox.Width : CropBox.Height;

    /// <summary>A user-space point on the shown page.</summary>
    public (double X, double Y) ToShown(double x, double y)
    {
        double w = CropBox.Width, h = CropBox.Height;
        double u = x - CropBox.X, v = CropBox.Y + h - y;
        return Rotation switch
        {
            90 => (h - v, u),
            180 => (w - u, h - v),
            270 => (v, w - u),
            _ => (u, v),
        };
    }

    /// <summary>A point of the shown page in user space.</summary>
    public (double X, double Y) ToUser(double sx, double sy)
    {
        double w = CropBox.Width, h = CropBox.Height;
        var (u, v) = Rotation switch
        {
            90 => (sy, h - sx),
            180 => (w - sx, h - sy),
            270 => (w - sy, sx),
            _ => (sx, sy),
        };
        return (CropBox.X + u, CropBox.Y + h - v);
    }

    /// <summary>The shown rectangle around a user-space box.</summary>
    public ViewerRect ToShown(ViewerBox box)
    {
        var a = ToShown(box.X1, box.Y1);
        var b = ToShown(box.X2, box.Y2);
        return new ViewerRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    /// <summary>The user-space box around a shown rectangle.</summary>
    public ViewerBox ToUser(ViewerRect rect)
    {
        var a = ToUser(rect.X, rect.Y);
        var b = ToUser(rect.X + rect.Width, rect.Y + rect.Height);
        return ViewerBox.Normalized(a.X, a.Y, b.X, b.Y);
    }

    /// <summary>A shown rectangle as a quadrilateral in user space (upper-left, upper-right, lower-left, lower-right as shown).</summary>
    public double[] ToQuad(ViewerRect rect)
    {
        var tl = ToUser(rect.X, rect.Y);
        var tr = ToUser(rect.X + rect.Width, rect.Y);
        var bl = ToUser(rect.X, rect.Y + rect.Height);
        var br = ToUser(rect.X + rect.Width, rect.Y + rect.Height);
        return [tl.X, tl.Y, tr.X, tr.Y, bl.X, bl.Y, br.X, br.Y];
    }

    /// <summary>A move on the shown page as a move in user space.</summary>
    public (double Dx, double Dy) ToUserDelta(double dx, double dy)
    {
        var a = ToUser(0, 0);
        var b = ToUser(dx, dy);
        return (b.X - a.X, b.Y - a.Y);
    }
}

/// <summary>Between <see cref="ViewerAnnotation"/> and the engine's <see cref="PdfAnnotation"/>.</summary>
public static class ViewerAnnotationPdf
{
    private static readonly Dictionary<string, ViewerAnnotationKind> Kinds = new(StringComparer.Ordinal)
    {
        ["Highlight"] = ViewerAnnotationKind.Highlight,
        ["Underline"] = ViewerAnnotationKind.Underline,
        ["StrikeOut"] = ViewerAnnotationKind.StrikeOut,
        ["Text"] = ViewerAnnotationKind.Note,
        ["FreeText"] = ViewerAnnotationKind.FreeText,
        ["Ink"] = ViewerAnnotationKind.Ink,
        ["Square"] = ViewerAnnotationKind.Square,
        ["Circle"] = ViewerAnnotationKind.Circle,
        ["Line"] = ViewerAnnotationKind.Line,
        ["Polygon"] = ViewerAnnotationKind.Polygon,
        ["PolyLine"] = ViewerAnnotationKind.PolyLine,
        ["Stamp"] = ViewerAnnotationKind.Stamp,
    };

    private static readonly string[] Subtypes = ["Highlight", "Underline", "StrikeOut", "Text", "FreeText", "Ink", "Square", "Circle", "Line", "Polygon", "PolyLine", "Stamp", "Text"];

    public static string Subtype(ViewerAnnotationKind kind) => Subtypes[(int)kind];

    /// <summary>
    /// The viewer's model of an annotation read from a file, or null for a type it does not
    /// manage or a hidden annotation. <paramref name="id"/> is used when it has no usable /NM. A
    /// reply (/IRT) is managed when <paramref name="parentId"/> gives the id of the annotation
    /// it answers and it is a note; any other reply, and a group member (/RT /Group), is not. A
    /// status (a reply with /State and /StateModel) is managed though hidden, as Acrobat hides it.
    /// </summary>
    public static ViewerAnnotation? FromPdf(PdfAnnotation p, string id, string? parentId = null)
    {
        if (!Kinds.TryGetValue(p.Subtype, out var kind) || (p.Flags & 32) != 0) return null;
        bool status = false;
        if (p.InReplyTo > 0 || !string.IsNullOrEmpty(p.InReplyToName))
        {
            if (kind != ViewerAnnotationKind.Note || p.ReplyType is not (null or "R") || string.IsNullOrEmpty(parentId)) return null;
            kind = ViewerAnnotationKind.Reply;
            // A status is hidden, as Acrobat writes it (/F 30); a visible reply with /State is an ordinary reply.
            status = (p.Flags & 2) != 0 && IsStateName(p.State) && IsStateName(p.StateModel);
        }
        if ((p.Flags & 2) != 0 && !status) return null;
        if (!double.IsFinite(p.Rect.X) || !double.IsFinite(p.Rect.Y) || !double.IsFinite(p.Rect.Width) || !double.IsFinite(p.Rect.Height)
            || !double.IsFinite(p.Rect.Right) || !double.IsFinite(p.Rect.Bottom) || p.Rect.Width < 0 || p.Rect.Height < 0) return null;
        // A shape needs its points: a line its /L, a polygon at least three and a polyline two vertices, all finite.
        double[] vertices = kind switch
        {
            ViewerAnnotationKind.Line => p.Line ?? [],
            ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine => p.Vertices.SelectMany(v => new[] { v.X, v.Y }).ToArray(),
            _ => [],
        };
        if (kind == ViewerAnnotationKind.Line && vertices.Length != 4
            || kind == ViewerAnnotationKind.Polygon && vertices.Length < 6
            || kind == ViewerAnnotationKind.PolyLine && vertices.Length < 4
            || vertices.Length > 2 * PdfAnnotationLimits.MaxVertices || !vertices.All(double.IsFinite)) return null;
        string Ending(int i) => p.LineEndings is { Length: 2 } le && PdfAnnotationLimits.LineEndings.Contains(le[i]) ? le[i] : "None";
        bool endings = kind is ViewerAnnotationKind.Line or ViewerAnnotationKind.PolyLine;
        // A callout: free text with a line of two or three finite points. Its box is its text box:
        // /Rect less /RD, where the engine draws it.
        double[] callout = kind == ViewerAnnotationKind.FreeText && p.CalloutLine is { Length: 4 or 6 } cl && cl.All(double.IsFinite) ? cl : [];
        var box = new ViewerBox(p.Rect.Left, p.Rect.Top, p.Rect.Right, p.Rect.Bottom);
        if (callout.Length > 0 && p.RectDifferences is { Length: 4 } rd && rd[0] + rd[2] < box.Width && rd[1] + rd[3] < box.Height)
            box = new ViewerBox(box.X1 + rd[0], box.Y1 + rd[1], box.X2 - rd[2], box.Y2 - rd[3]);
        string calloutEnding = callout.Length > 0 && p.LineEndings is { Length: >= 1 } ce && PdfAnnotationLimits.LineEndings.Contains(ce[0]) ? ce[0] : "None";
        string? textColor = null;
        double fontSize = 12;
        if (kind == ViewerAnnotationKind.FreeText) (textColor, fontSize) = ReadDefaultAppearance(p.DefaultAppearance);
        string color = kind == ViewerAnnotationKind.FreeText ? textColor ?? "#000000" : Hex(p.Color) ?? DefaultColor(kind);
        string? fill = kind switch
        {
            ViewerAnnotationKind.FreeText => Hex(p.InteriorColor ?? p.Color),
            ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle or ViewerAnnotationKind.Line or ViewerAnnotationKind.Polygon
                or ViewerAnnotationKind.PolyLine => Hex(p.InteriorColor),
            _ => null,
        };
        bool shape = kind is ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle || vertices.Length > 0;
        return new ViewerAnnotation
        {
            Id = !string.IsNullOrEmpty(p.Name) && p.Name.Length <= 200 && !p.Name.Any(char.IsControl) ? p.Name : id,
            Page = p.PageNumber,
            Kind = kind,
            Box = box,
            QuadPoints = p.QuadPoints.SelectMany(q => q).ToArray(),
            InkList = p.InkList.Select(s => (IReadOnlyList<double>)s.SelectMany(pt => new[] { pt.X, pt.Y }).ToArray()).ToArray(),
            Vertices = vertices,
            Callout = callout,
            StartEnding = endings ? Ending(0) : calloutEnding,
            EndEnding = endings ? Ending(1) : "None",
            StampName = kind == ViewerAnnotationKind.Stamp ? StampNameOf(p.Icon) : "",
            InReplyTo = kind == ViewerAnnotationKind.Reply ? parentId! : "",
            State = status ? p.State! : "",
            StateModel = status ? p.StateModel! : "",
            Color = color,
            FillColor = fill,
            Opacity = p.Opacity,
            Dash = shape ? DashOf(p) : null,
            IsMeasurement = p.Measure != null,
            Measure = p.Measure,
            StrokeWidth = p.BorderWidth ?? 1,
            FontSize = fontSize,
            Contents = p.Contents ?? "",
            Author = p.Author ?? "",
            Created = ParseDate(p.CreationDate) ?? ParseDate(p.ModifiedDate) ?? default,
            Modified = ParseDate(p.ModifiedDate) ?? ParseDate(p.CreationDate) ?? default,
            SourceIndex = p.Index,
        };
    }

    /// <summary>Most numbers of a dash (/D), and the longest dash or gap (points).</summary>
    public const int MaxDashCount = 16;
    public const double MaxDashLength = 1000;

    /// <summary>A dash the viewer keeps: 1 to 16 numbers from 0 to 1000, not all 0.</summary>
    public static bool IsValidDash(IReadOnlyList<double>? dash) =>
        dash is { Count: > 0 and <= MaxDashCount } && dash.All(v => v >= 0 && v <= MaxDashLength) && dash.Sum() > 0;

    /// <summary>
    /// The dash the engine draws: /BS /S /D with its /D (else [3]), or the /Border dash when there
    /// is no /BS; null for a solid line. A dash outside the viewer's limits is read as [3].
    /// </summary>
    private static IReadOnlyList<double>? DashOf(PdfAnnotation p)
    {
        bool dashed = p.BorderStyle == "D"
                      || p.BorderStyle == null && p.BorderDash is { Length: > 0 } d && d.All(v => v >= 0) && d.Sum() > 0;
        if (!dashed) return null;
        return IsValidDash(p.BorderDash) ? p.BorderDash! : [3];
    }

    public static string DefaultColor(ViewerAnnotationKind kind) => kind switch
    {
        ViewerAnnotationKind.Highlight or ViewerAnnotationKind.Note or ViewerAnnotationKind.Reply => "#FFD400",
        ViewerAnnotationKind.Underline => "#1F9D55",
        ViewerAnnotationKind.FreeText => "#000000",
        ViewerAnnotationKind.Stamp => "#1F3399",
        _ => "#E5484D",
    };

    /// <summary>A stamp name this viewer writes: 1 to 64 ASCII letters and digits (a file's other names are shown as Draft).</summary>
    public static bool IsStampName(string? name) => name is { Length: > 0 and <= 64 } && name.All(char.IsAsciiLetterOrDigit);

    private static string StampNameOf(string? icon) => IsStampName(icon) ? icon! : "Draft";

    /// <summary>Longest /State or /StateModel the viewer keeps.</summary>
    public const int MaxStateLength = PdfAnnotationXfdf.MaxStateLength;

    /// <summary>A /State or /StateModel the viewer keeps: 1 to 64 characters, no control characters.</summary>
    public static bool IsStateName(string? name) => name is { Length: > 0 and <= MaxStateLength } && !name.Any(char.IsControl);

    /// <summary>/F of a reply made here: Print, NoZoom and NoRotate, as Acrobat writes replies (the desktop and web viewers too).</summary>
    public const int ReplyFlags = 4 | 8 | 16;

    /// <summary>/F of a status made here: hidden as well, as Acrobat writes them; the comment lists show them.</summary>
    public const int StatusFlags = 2 | ReplyFlags;

    /// <summary>
    /// The annotation for the engine's editor. <paramref name="basis"/> is the annotation as read
    /// from the file when it is changed (its other entries are kept), null for a new one.
    /// </summary>
    public static PdfAnnotation ToPdf(ViewerAnnotation a, PdfAnnotation? basis)
    {
        // A file's shape as read: its fill, dash, endings and width are written only when changed.
        var was = basis != null && a.IsShape ? FromPdf(basis, a.Id) : null;
        var p = basis ?? new PdfAnnotation { Flags = 4 };
        p.PageNumber = a.Page;
        p.Subtype = Subtype(a.Kind);
        p.Rect = a.Box.ToRect();
        p.Opacity = Math.Clamp(a.Opacity, 0, 1);
        p.Contents = a.Contents.Length > 0 ? a.Contents : null;
        p.Author = a.Author.Length > 0 ? a.Author : null;
        p.Name = a.Id;
        p.CreationDate = FormatDate(a.Created);
        p.ModifiedDate = FormatDate(a.Modified);
        p.QuadPoints = a.IsMarkup ? Quads(a.QuadPoints, a.Box) : new List<double[]>();
        p.InkList = a.Kind == ViewerAnnotationKind.Ink
            ? a.InkList.Select(s => (IReadOnlyList<PdfPoint>)Enumerable.Range(0, s.Count / 2).Select(i => new PdfPoint(s[2 * i], s[2 * i + 1])).ToList()).ToList()
            : new List<IReadOnlyList<PdfPoint>>();
        switch (a.Kind)
        {
            case ViewerAnnotationKind.FreeText:
                // /C is the background of free text; the text colour is in /DA.
                p.Color = a.FillColor is { } bg ? Rgb(bg) : null;
                p.InteriorColor = null;
                p.DefaultAppearance = string.Create(CultureInfo.InvariantCulture, $"/Helv {Number(a.FontSize)} Tf {RgbOperands(a.Color)} rg");
                p.BorderWidth = a.StrokeWidth;
                if (a.IsCallout)
                {
                    // As Acrobat writes a callout: /IT /FreeTextCallout, /CL, /LE at the anchor, and a
                    // /Rect around the text box and the line with /RD giving the text box inside it.
                    var outer = ViewerCallout.Outer(a);
                    p.Rect = outer.ToRect();
                    p.RectDifferences = [a.Box.X1 - outer.X1, a.Box.Y1 - outer.Y1, outer.X2 - a.Box.X2, outer.Y2 - a.Box.Y2];
                    p.Intent = ViewerCallout.Intent;
                    p.CalloutLine = a.Callout.ToArray();
                    p.LineEndings = a.StartEnding != "None" ? [a.StartEnding] : null;
                }
                else if (basis?.CalloutLine != null)
                {
                    // A file's callout given back without its line (a host page's update): a plain text box.
                    p.CalloutLine = null;
                    p.LineEndings = null;
                    if (p.Intent == ViewerCallout.Intent) p.Intent = null;
                    p.RectDifferences = [0, 0, 0, 0];
                }
                break;
            case ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle:
                p.Color = Rgb(a.Color);
                WriteShapeStyle(a, p, basis, was);
                break;
            case ViewerAnnotationKind.Ink:
                p.Color = Rgb(a.Color);
                p.BorderWidth = a.StrokeWidth;
                break;
            case ViewerAnnotationKind.Note:
                p.Color = Rgb(a.Color);
                p.Icon ??= "Note";
                break;
            case ViewerAnnotationKind.Line or ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine:
                p.Color = Rgb(a.Color);
                WriteShapeStyle(a, p, basis, was);
                var v = a.Vertices;
                p.Line = a.Kind == ViewerAnnotationKind.Line && v.Count == 4 ? [v[0], v[1], v[2], v[3]] : null;
                p.Vertices = a.Kind == ViewerAnnotationKind.Line ? new List<PdfPoint>()
                    : Enumerable.Range(0, v.Count / 2).Select(i => new PdfPoint(v[2 * i], v[2 * i + 1])).ToList();
                bool keepEndings = basis?.Measure != null || was != null && a.StartEnding == was.StartEnding && a.EndEnding == was.EndEnding;
                if (!keepEndings) p.LineEndings = a.HasEndings && (a.StartEnding != "None" || a.EndEnding != "None") ? [a.StartEnding, a.EndEnding] : null;
                break;
            case ViewerAnnotationKind.Stamp:
                // A stamp is drawn in the colour of its kind (green for Approved, red for Confidential);
                // a file's stamp keeps its own appearance and a /Name this viewer does not write.
                if (basis == null || IsStampName(basis.Icon)) p.Icon = a.StampName;
                p.Color = basis?.Color;
                if (basis == null && a.CustomStamp is { } custom)
                {
                    // The picture or page as a form XObject (the engine's custom-stamp path, as the desktop
                    // app writes one), stretched to the box, which keeps its shape, and turned upright.
                    int turn = (a.StampTurn % 360 + 360) % 360;
                    bool quarter = turn is 90 or 270;
                    p.Stamp = new PdfStampAppearance
                    {
                        Rotation = turn,
                        Width = quarter ? a.Box.Height : a.Box.Width,
                        Height = quarter ? a.Box.Width : a.Box.Height,
                        SourcePdf = custom.Pdf,
                        SourcePage = 1,
                    };
                }
                else if (basis == null && (a.StampSubtitle != null || a.StampTurn != 0))
                {
                    // Drawn upright at its own size and turned by the form's /Matrix (the engine's turned-stamp path).
                    int turn = (a.StampTurn % 360 + 360) % 360;
                    bool quarter = turn is 90 or 270;
                    p.Stamp = new PdfStampAppearance
                    {
                        Subtitle = a.StampSubtitle,
                        Rotation = turn,
                        Width = quarter ? a.Box.Height : a.Box.Width,
                        Height = quarter ? a.Box.Width : a.Box.Height,
                    };
                }
                break;
            case ViewerAnnotationKind.Reply:
                p.Color = Rgb(a.Color);
                p.Icon ??= "Comment";
                // /RT /R, as the desktop viewer and Acrobat write a reply (ISO 32000-2 12.5.6.4).
                p.ReplyType = "R";
                // The parent's object number is known for a parent from the file; Write sets it, else /NM finds it.
                p.InReplyToName = a.InReplyTo;
                p.Flags = basis?.Flags ?? (a.IsStatus ? StatusFlags : ReplyFlags);
                p.State = a.IsStatus ? a.State : null;
                p.StateModel = a.IsStatus ? a.StateModel : null;
                break;
            default:
                p.Color = Rgb(a.Color);
                break;
        }
        return p;
    }

    /// <summary>
    /// A shape's fill (/IC), dash (/BS /S and /D) and width (/BS /W). A file's shape keeps each of
    /// its own values that the reader did not change, and gets no default it did not have; a
    /// measurement keeps its fill and dash.
    /// </summary>
    private static void WriteShapeStyle(ViewerAnnotation a, PdfAnnotation p, PdfAnnotation? basis, ViewerAnnotation? was)
    {
        bool measurement = basis?.Measure != null;
        if (!measurement && (was == null || a.FillColor != was.FillColor))
            p.InteriorColor = a.FillColor is { } fill ? Rgb(fill) : null;
        if (!measurement && (was == null || !ViewerAnnotation.SameDash(a.Dash, was.Dash)))
        {
            p.BorderStyle = a.Dash != null ? "D" : "S";
            p.BorderDash = a.Dash?.ToArray();
        }
        // /BS is written for a new shape, when the file has one, or when the width changed.
        if (was == null || p.BorderWidth != null || p.BorderStyle != null || a.StrokeWidth != was.StrokeWidth)
            p.BorderWidth = a.StrokeWidth;
    }

    private static List<double[]> Quads(IReadOnlyList<double> flat, ViewerBox box)
    {
        var quads = new List<double[]>();
        for (int i = 0; i + 7 < flat.Count; i += 8) quads.Add(flat.Skip(i).Take(8).ToArray());
        if (quads.Count == 0) quads.Add([box.X1, box.Y2, box.X2, box.Y2, box.X1, box.Y1, box.X2, box.Y1]);
        return quads;
    }

    /// <summary>"#RRGGBB" as RGB in 0..1.</summary>
    public static double[] Rgb(string hex)
    {
        int v = int.Parse(hex.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return [(v >> 16 & 255) / 255.0, (v >> 8 & 255) / 255.0, (v & 255) / 255.0];
    }

    private static string RgbOperands(string hex) => string.Join(" ", Rgb(hex).Select(c => Number(Math.Round(c, 4))));

    /// <summary>A PDF colour (gray, RGB or CMYK) as "#RRGGBB", null when there is none.</summary>
    public static string? Hex(double[]? c)
    {
        if (PdfAnnotation.ToRgb(c) is not (double r, double g, double b)) return null;
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{B(r):X2}{B(g):X2}{B(b):X2}");
    }

    private static string Number(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static readonly Regex SizeRegex = new(@"(\d+(?:\.\d+)?)\s+Tf", RegexOptions.CultureInvariant);
    private static readonly Regex RgbRegex = new(@"(\d*\.?\d+)\s+(\d*\.?\d+)\s+(\d*\.?\d+)\s+rg", RegexOptions.CultureInvariant);
    private static readonly Regex GrayRegex = new(@"(?<![\d.])(\d*\.?\d+)\s+g\b", RegexOptions.CultureInvariant);

    /// <summary>The text colour and size of a free text /DA ("/Helv 12 Tf 0 0 1 rg").</summary>
    private static (string? Color, double Size) ReadDefaultAppearance(string? da)
    {
        if (string.IsNullOrEmpty(da) || da.Length > 1000) return (null, 12);
        double size = SizeRegex.Match(da) is { Success: true } s && double.TryParse(s.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) && z >= 1 && z <= 1000 ? z : 12;
        double D(Group g) => double.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        string? color = RgbRegex.Match(da) is { Success: true } m ? Hex([D(m.Groups[1]), D(m.Groups[2]), D(m.Groups[3])])
            : GrayRegex.Match(da) is { Success: true } g ? Hex([D(g.Groups[1])]) : null;
        return (color, size);
    }

    /// <summary>A PDF date ("D:20261026101500+05'30'") in UTC, or null when it cannot be read.</summary>
    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = Regex.Match(s, @"^(?:D:)?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?\s*(?:([+\-Z])(?:(\d{2})'?(?:(\d{2})'?)?)?)?", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        int G(int i, int fallback) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : fallback;
        try
        {
            var offset = TimeSpan.Zero;
            if (m.Groups[7].Value is "+" or "-")
            {
                offset = new TimeSpan(G(8, 0), G(9, 0), 0);
                if (m.Groups[7].Value == "-") offset = -offset;
            }
            return new DateTimeOffset(G(1, 1), Math.Clamp(G(2, 1), 1, 12), Math.Clamp(G(3, 1), 1, 31), Math.Min(G(4, 0), 23), Math.Min(G(5, 0), 59), Math.Min(G(6, 0), 59), offset).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A date as a PDF date in UTC ("D:20261026101500Z00'00'"), null for none.</summary>
    public static string? FormatDate(DateTimeOffset date) =>
        date == default ? null : "D:" + date.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z00'00'";

    /// <summary>The annotations of every page that the viewer manages, in page and /Annots order.</summary>
    public static List<ViewerAnnotation> ReadAll(PdfEngine.Vector.Document.PdfVectorDocument document)
    {
        var all = new List<PdfAnnotation>();
        for (int page = 1; page <= document.PageCount; page++)
        {
            try
            {
                all.AddRange(PdfAnnotationReader.Read(document, page));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // a damaged /Annots: that page's annotations are not managed
            }
        }
        // Replies whose /IRT chain reaches a thread's first annotation (no loops, within the depth limit).
        var roots = PdfAnnotationThreads.Roots(all);
        var result = new List<ViewerAnnotation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var idOf = new Dictionary<int, string>();
        bool Take(PdfAnnotation p, string? parentId)
        {
            string fallback = $"pdf-{p.PageNumber}-{p.Index}";
            if (FromPdf(p, fallback, parentId) is not { } a) return false;
            // Two annotations with the same /NM: the second is named by its place.
            if (!ids.Add(a.Id))
            {
                a = a with { Id = fallback };
                if (!ids.Add(a.Id)) return false;
            }
            if (p.ObjectNumber > 0) idOf.TryAdd(p.ObjectNumber, a.Id);
            result.Add(a);
            return true;
        }
        foreach (var p in all)
            if (p.InReplyTo == 0) Take(p, null);
        // Then the replies, a level at a time: a reply is managed when the annotation it answers is.
        var pending = all.Where(p => p.InReplyTo > 0 && roots.ContainsKey(p.ObjectNumber)).ToList();
        for (int level = 0; level < PdfAnnotationLimits.MaxReplyDepth && pending.Count > 0; level++)
        {
            var next = new List<PdfAnnotation>();
            foreach (var p in pending)
            {
                if (idOf.TryGetValue(p.InReplyTo, out var parentId)) Take(p, parentId);
                else next.Add(p);
            }
            if (next.Count == pending.Count) break;
            pending = next;
        }
        // Page order, as the store keeps its list (OrderBy is stable: replies stay after their parents).
        return result.OrderBy(a => a.Page).ToList();
    }

    /// <summary>
    /// Writes the difference between the file's annotations and the current ones into the
    /// original document as one incremental update: deleted ones removed, changed ones replaced
    /// (with a new appearance), new ones added. Null when nothing differs.
    /// </summary>
    public static byte[]? Write(PdfEngine.Vector.Document.PdfVectorDocument original, byte[] bytes,
        IReadOnlyList<ViewerAnnotation> fromFile, IReadOnlyList<ViewerAnnotation> current)
    {
        var now = new Dictionary<string, ViewerAnnotation>(StringComparer.Ordinal);
        foreach (var a in current) now[a.Id] = a;
        bool changed = false;
        var read = new Dictionary<int, IReadOnlyList<PdfAnnotation>>();
        PdfAnnotation? Basis(ViewerAnnotation f)
        {
            if (!read.TryGetValue(f.Page, out var list)) read[f.Page] = list = PdfAnnotationReader.Read(original, f.Page);
            return list.FirstOrDefault(p => p.Index == f.SourceIndex);
        }
        var result = PdfAnnotationEditor.Apply(original, bytes, editor =>
        {
            foreach (var f in fromFile)
            {
                if (now.TryGetValue(f.Id, out var a) && a.FromFile)
                {
                    if (ReferenceEquals(a, f)) continue;
                    editor.Modify(ToPdf(a, Basis(f) ?? throw new InvalidOperationException("The annotation is no longer in the file.")));
                }
                else
                {
                    editor.Delete(f.Page, f.SourceIndex);
                }
                changed = true;
            }
            // New annotations before new replies, and each reply after the one it answers, so the
            // editor can find a new parent by its /NM; a parent from the file is given by its object number.
            var fileById = new Dictionary<string, ViewerAnnotation>(StringComparer.Ordinal);
            foreach (var f in fromFile) fileById.TryAdd(f.Id, f);
            foreach (var a in current.Where(a => !a.FromFile).OrderBy(a => Depth(a, now)))
            {
                var p = ToPdf(a, MovedShapeBasis(a));
                // (one written directly in /Annots is made an object first, so /IRT can refer to it).
                if (a.Kind == ViewerAnnotationKind.Reply && now.TryGetValue(a.InReplyTo, out var parent) && parent.FromFile
                    && Basis(parent) is { } fileParent)
                    p.InReplyTo = fileParent.ObjectNumber > 0 ? fileParent.ObjectNumber : editor.MakeIndirect(parent.Page, fileParent.Index);
                editor.Add(p);
                changed = true;
            }

            // A file's shape moved to another page is written there as a new annotation from its
            // own entries (a measurement's /Measure, /IT, /Subj, its style), not from the model alone.
            PdfAnnotation? MovedShapeBasis(ViewerAnnotation a)
            {
                if (!a.IsShape || !fileById.TryGetValue(a.Id, out var origin) || origin.Page == a.Page
                    || origin.Kind != a.Kind || origin.IsMeasurement != a.IsMeasurement || Basis(origin) is not { } b) return null;
                b.Index = -1;
                b.ObjectNumber = 0;
                b.PopupObjectNumber = 0;
                b.PopupRect = null;
                b.InReplyTo = 0;
                b.InReplyToName = null;
                return b;
            }
        });
        return changed ? result : null;
    }

    /// <summary>
    /// The viewer's annotations for XFDF annotations being imported into a document whose ids are
    /// <paramref name="ids"/> (added to as ids are given). An XFDF name in use gets a new id. A
    /// reply (inreplyto) answers the annotation of that name in the same import, before or after
    /// it, else one the document has; a reply whose parent is neither, or that the viewer does not
    /// manage as a reply (a group member, a highlight with inreplyto), is added on its own, as an
    /// import without replies would. Parents come before their replies in the result.
    /// </summary>
    public static List<ViewerAnnotation> FromXfdf(IReadOnlyList<PdfAnnotation> read, HashSet<string> ids)
    {
        var result = new List<ViewerAnnotation>();
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        void Take(ViewerAnnotation? a)
        {
            if (a == null) return;
            string name = a.Id;
            if (!ids.Add(a.Id)) a = a with { Id = ViewerAnnotationStore.NewId() };
            ids.Add(a.Id);
            if (name.Length > 0) renamed.TryAdd(name, a.Id);
            result.Add(a with { SourceIndex = -1 });
        }
        ViewerAnnotation? Alone(PdfAnnotation p)
        {
            p.InReplyToName = null;
            p.ReplyType = null;
            return FromPdf(p, "");
        }
        var pending = read.ToList();
        // A level of replies per pass; what is left after the depth limit, or a pass that places nothing, goes on its own.
        for (int pass = 0; pass <= PdfAnnotationLimits.MaxReplyDepth + 1 && pending.Count > 0; pass++)
        {
            var waiting = pending.Where(p => p.Name is { Length: > 0 }).Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
            var next = new List<PdfAnnotation>();
            foreach (var p in pending)
            {
                if (p.InReplyToName is not { } n) { Take(FromPdf(p, "")); continue; }
                string? parent = renamed.GetValueOrDefault(n);
                if (parent == null && waiting.Contains(n) && n != p.Name) { next.Add(p); continue; }
                parent ??= ids.Contains(n) ? n : null;
                Take(parent != null && FromPdf(p, "", parent) is { } reply ? reply : Alone(p));
            }
            bool stuck = next.Count == pending.Count;
            pending = next;
            if (stuck) break;
        }
        foreach (var p in pending) Take(Alone(p));
        return result;
    }

    /// <summary>How many replies lie between an annotation and its thread's first one (0 for that one); bounded.</summary>
    public static int Depth(ViewerAnnotation a, IReadOnlyDictionary<string, ViewerAnnotation> byId)
    {
        int depth = 0;
        while (a.Kind == ViewerAnnotationKind.Reply && depth <= PdfAnnotationLimits.MaxReplyDepth && byId.TryGetValue(a.InReplyTo, out var parent))
        {
            depth++;
            a = parent;
        }
        return depth;
    }
}
