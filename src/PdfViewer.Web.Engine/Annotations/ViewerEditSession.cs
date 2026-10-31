using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Forms;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>
/// One line of an annotation list: the annotation, its reply level, what it is, the start of its
/// text, and its review status ("Accepted"; null for none).
/// </summary>
public sealed record ViewerAnnotationListItem(ViewerAnnotation Annotation, int Level, string Label, string Summary, string? Status = null);

/// <summary>What an XFDF import added, and how many of its annotations were left out (unreadable, off the document's pages or outside the limits).</summary>
public readonly record struct ViewerXfdfImport(int Added, int Skipped)
{
    private readonly IReadOnlyList<ViewerAnnotation>? _annotations;

    /// <summary>The annotations added, as the store keeps them.</summary>
    public IReadOnlyList<ViewerAnnotation> Annotations { get => _annotations ?? []; init => _annotations = value; }
}

/// <summary>
/// A shape's look: its fill (null: none), its dash (null: solid) and, for a line or connected
/// lines, its line endings (/LE names, ISO 32000-2 Table 179).
/// </summary>
public sealed record ViewerShapeStyle(string? Fill = null, IReadOnlyList<double>? Dash = null, string Start = "None", string End = "None")
{
    /// <summary>No fill, solid, no line endings.</summary>
    public static ViewerShapeStyle Plain { get; } = new();

    /// <summary>The style of an annotation (meaningful for a shape).</summary>
    public static ViewerShapeStyle Of(ViewerAnnotation a) => new(a.FillColor, a.Dash, a.StartEnding, a.EndEnding);

    /// <summary>The same style as <paramref name="other"/>.</summary>
    public bool SameAs(ViewerShapeStyle other) =>
        string.Equals(Fill, other.Fill, StringComparison.OrdinalIgnoreCase) && ViewerAnnotation.SameDash(Dash, other.Dash) && Start == other.Start && End == other.End;
}

/// <summary>How a viewer edits a document (<see cref="ViewerEditSession.StartAsync(ViewerDocument, ViewerEditOptions, CancellationToken)"/>).</summary>
public sealed record ViewerEditOptions
{
    /// <summary>The name written as the author of new annotations.</summary>
    public string Author { get; init; } = "";

    /// <summary>The document's own annotations are read, listed and may be changed; when false only the ones added are managed.</summary>
    public bool ReadFileAnnotations { get; init; } = true;

    /// <summary>The reader may fill the document's form fields (when the document allows it).</summary>
    public bool FillForms { get; init; } = true;

    /// <summary>Not null: nothing may change (no annotations, no form values), for this reason.</summary>
    public string? ReadOnlyReason { get; init; }
}

/// <summary>What writing the edits into the document did: written (pages must be drawn again), a value refused, or a failure.</summary>
public readonly record struct ViewerApplyResult(bool Written, string? Refused, string? Failed);

/// <summary>A save in flight: the bytes to write and the id that ends it (<see cref="ViewerEditSession.EndSave"/>).</summary>
public sealed record ViewerSave(int Id, byte[] Bytes);

/// <summary>
/// The reader's edits of one document, shared by the web viewer (and its embed SDK) and the Android
/// app: its annotations in a <see cref="ViewerAnnotationStore"/> with undo and redo, the form
/// values the reader set, and the rules for both. After each change the viewer calls
/// <see cref="ApplyAsync"/>, which has the engine write everything into the document
/// (<see cref="ViewerDocument.ApplyEditsAsync"/>, an incremental update of the file as opened), so
/// the pages drawn again are what a save writes (<see cref="Written"/> says when to draw them).
/// The rules: a read-only open (<see cref="ViewerEditOptions.ReadOnlyReason"/>) and the document's permissions and signatures decide whether
/// annotations and form values may change at all (<see cref="ViewerDocument.CanAnnotate"/>,
/// <see cref="ViewerDocument.CanFillForms"/>), read-only fields are never changed, a text is cut
/// to the field's /MaxLen, a choice must be one of the field's options, and the engine's format
/// and range rules (run without the document's JavaScript) may refuse a value.
/// </summary>
/// <remarks>Not thread-safe: one viewer uses it from its UI thread.</remarks>
public sealed class ViewerEditSession
{
    /// <summary>Longest value taken for a field without a /MaxLen.</summary>
    public const int MaxFieldValueLength = 10000;

    /// <summary>A note's icon, in points of the shown page.</summary>
    public const double NoteSize = 20;

    /// <summary>The line width of a drawing.</summary>
    public const double InkWidth = 2;

    /// <summary>The line width of a rectangle, ellipse, line or arrow (the web viewer's).</summary>
    public const double ShapeWidth = 2;

    /// <summary>A drag shorter than this (points of the shown page) adds no shape: a rectangle or ellipse needs it both ways.</summary>
    public const double MinShapeSize = 4;

    /// <summary>The text size of a new text box.</summary>
    public const double FreeTextSize = 12;

    /// <summary>The widest a new text box is, in points of the shown page; its text wraps at this width.</summary>
    public const double FreeTextWidth = 220;

    private readonly Dictionary<string, IReadOnlyList<string>> _values = new(StringComparer.Ordinal);
    private readonly ViewerEditState _edits = new();
    // Text boxes whose size the reader dragged: their text wraps in that box, which a new text keeps.
    private readonly HashSet<string> _sizedByReader = new(StringComparer.Ordinal);
    private readonly ViewerEditOptions _options;
    private string? _lastField;
    private bool _noticeTaken, _applying;

    private ViewerEditSession(ViewerDocument document, ViewerAnnotationStore store, IReadOnlyList<ViewerFormWidget> widgets, ViewerEditOptions options, string? problem)
    {
        Document = document;
        Store = store;
        Widgets = widgets;
        Author = options.Author;
        _options = options;
        Problem = problem;
    }

    /// <summary>
    /// Starts editing <paramref name="document"/>: its own annotations (when they can be read) and,
    /// when the document allows form filling, its fillable fields.
    /// </summary>
    public static Task<ViewerEditSession> StartAsync(ViewerDocument document, string author = "", CancellationToken cancellationToken = default) =>
        StartAsync(document, new ViewerEditOptions { Author = author }, cancellationToken);

    /// <summary>
    /// Starts editing <paramref name="document"/> as <paramref name="options"/> say: its own
    /// annotations when they are read (and can be), and its fillable fields when the reader may fill them.
    /// </summary>
    public static async Task<ViewerEditSession> StartAsync(ViewerDocument document, ViewerEditOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<ViewerAnnotation> fromFile = [];
        string? problem = null;
        if (options.ReadFileAnnotations)
        {
            try
            {
                fromFile = await document.ReadAnnotationsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException and not OutOfMemoryException)
            {
                // Type names only, never document content; the annotations are still drawn, but not listed.
                problem = $"The document's annotations could not be read ({ex.GetType().Name}).";
            }
        }
        bool fill = options.FillForms && options.ReadOnlyReason == null && document.CanFillForms;
        var widgets = fill ? await document.GetFormWidgetsAsync(cancellationToken).ConfigureAwait(false) : [];
        return new ViewerEditSession(document, new ViewerAnnotationStore(document.Geometry, fromFile), widgets, options, problem);
    }

    /// <summary>
    /// A write of the edits into the document ended with the bytes changed: the pages must be drawn
    /// again. Raised on the thread that called <see cref="ApplyAsync"/> (a save's writes too).
    /// </summary>
    public event Action? Written;

    /// <summary>The document was opened for reading only (<see cref="ViewerEditOptions.ReadOnlyReason"/>).</summary>
    public bool ReadOnly => _options.ReadOnlyReason != null;

    /// <summary>Why nothing may change: read-only, the document's permissions or signatures; or the fallback.</summary>
    private string Restriction(string fallback) => _options.ReadOnlyReason ?? Document.EditRestriction ?? fallback;

    public ViewerDocument Document { get; }

    public ViewerAnnotationStore Store { get; }

    /// <summary>The widgets of the fillable fields with their current values (none when form filling is not allowed).</summary>
    public IReadOnlyList<ViewerFormWidget> Widgets { get; private set; }

    /// <summary>The name written as the author of new annotations.</summary>
    public string Author { get; set; }

    /// <summary>Why the document's annotations are not listed, or null.</summary>
    public string? Problem { get; }

    public bool CanAnnotate => !ReadOnly && Document.CanAnnotate;

    public bool CanFillForms => _options.FillForms && !ReadOnly && Document.CanFillForms;

    /// <summary>Why annotations may not change (read-only, the document's permissions or signatures), or null when they may.</summary>
    public string? AnnotateRestriction => CanAnnotate ? null : Restriction("This document does not allow annotations.");

    /// <summary>There are edits that no save has written.</summary>
    public bool IsDirty => _edits.IsDirty;

    /// <summary>Counts the edits (each change, undo and redo): a viewer that keeps the edits elsewhere tells by it whether they changed since.</summary>
    public int Version => _edits.EditVersion;

    /// <summary>The document's bytes have every edit.</summary>
    public bool IsCurrent => _edits.IsCurrent;

    /// <summary>A save is under way (<see cref="BeginSaveAsync"/> until <see cref="EndSave"/>).</summary>
    public bool IsSaving => _edits.Saving;

    public bool CanUndo => CanAnnotate && Store.CanUndo;

    public bool CanRedo => CanAnnotate && Store.CanRedo;

    /// <summary>The value the reader set for a field, or null when it is the document's.</summary>
    public IReadOnlyList<string>? ValueOf(string field) => _values.TryGetValue(field, out var v) ? v : null;

    /// <summary>
    /// What a signed document's reader is told at the first change (changes are a new revision),
    /// once per session; null otherwise.
    /// </summary>
    public string? TakeNotice()
    {
        if (_noticeTaken || _edits.EditVersion == 0 || Document.EditNotice is not { } notice) return null;
        _noticeTaken = true;
        return notice;
    }

    // ------------------------------------------------------------------ form fields

    /// <summary>The topmost widget at a point of the shown page, or null.</summary>
    public ViewerFormWidget? WidgetAt(int page, ViewerPoint point)
    {
        for (int i = Widgets.Count - 1; i >= 0; i--)
        {
            var w = Widgets[i];
            if (w.Page == page && Contains(w.Rect, point, 0)) return w;
        }
        return null;
    }

    /// <summary>
    /// The values a field takes from <paramref name="values"/>, or null with the reason when it
    /// takes none: a text is cut to /MaxLen (and a single-line text loses its line breaks), a
    /// check box or radio button takes its own state or Off, a choice one of its options.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(ViewerFormWidget widget, IReadOnlyList<string> values, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentNullException.ThrowIfNull(values);
        refusal = null;
        if (widget.ReadOnly)
        {
            refusal = "This field is read-only.";
            return null;
        }
        int max = widget.MaxLength > 0 ? Math.Min(widget.MaxLength, MaxFieldValueLength) : MaxFieldValueLength;
        string Cut(string s) => s.Length > max ? s[..max] : s;
        var exports = widget.Options.Select(o => o.Export).ToHashSet(StringComparer.Ordinal);
        switch (widget.Kind)
        {
            case ViewerFormKind.Text:
            {
                string text = values.Count > 0 ? values[0] : "";
                if (!widget.Multiline) text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                return [Cut(text)];
            }
            case ViewerFormKind.CheckBox or ViewerFormKind.Radio:
            {
                string on = widget.OnState ?? "Yes";
                if (values.Count == 1 && (values[0] == on || values[0] == "Off")) return [values[0]];
                refusal = "That is not a state of this field.";
                return null;
            }
            case ViewerFormKind.ComboBox:
            {
                if (values.Count != 1)
                {
                    refusal = "This field takes one choice.";
                    return null;
                }
                if (widget.Editable) return [Cut(values[0])];
                if (exports.Contains(values[0])) return [values[0]];
                refusal = "Choose one of the field's options.";
                return null;
            }
            default:
            {
                var chosen = values.Distinct(StringComparer.Ordinal).ToList();
                if (chosen.Count > 1 && !widget.MultiSelect)
                {
                    refusal = "This field takes one choice.";
                    return null;
                }
                if (chosen.Any(v => !exports.Contains(v)))
                {
                    refusal = "Choose one of the field's options.";
                    return null;
                }
                return chosen;
            }
        }
    }

    /// <summary>
    /// The reader set a field: true when the value changed (then <see cref="ApplyAsync"/> writes
    /// it). False with <paramref name="refusal"/> when the document, the field or the value does
    /// not allow it; false without one when nothing changed.
    /// </summary>
    public bool SetFieldValue(ViewerFormWidget widget, IReadOnlyList<string> values, out string? refusal)
    {
        if (!CanFillForms)
        {
            refusal = Restriction("This document does not allow form filling.");
            return false;
        }
        if (Normalize(widget, values, out refusal) is not { } taken) return false;
        var known = ValueOf(widget.Name) ?? widget.Values;
        if (known.SequenceEqual(taken, StringComparer.Ordinal)) return false;
        _values[widget.Name] = taken;
        _lastField = widget.Name;
        _edits.Edit();
        return true;
    }

    // ------------------------------------------------------------------ annotations

    /// <summary>The topmost annotation (not a reply) within <paramref name="slack"/> points of a point of the shown page; the smallest when several cover it.</summary>
    public ViewerAnnotation? AnnotationAt(int page, ViewerPoint point, double slack)
    {
        if (page < 1 || page > Document.Geometry.Count) return null;
        var g = Document.Geometry[page - 1];
        ViewerAnnotation? best = null;
        double bestArea = double.MaxValue;
        foreach (var a in Store.Current)
        {
            if (a.Page != page || a.Kind == ViewerAnnotationKind.Reply) continue;
            var r = g.ToShown(a.Box);
            if (!Contains(r, point, slack)) continue;
            double area = r.Width * r.Height;
            if (area <= bestArea)
            {
                best = a;
                bestArea = area;
            }
        }
        return best;
    }

    private ViewerAnnotation New(ViewerAnnotationKind kind, int page, ViewerBox box, string? color)
    {
        var now = DateTimeOffset.UtcNow;
        return new ViewerAnnotation
        {
            Id = ViewerAnnotationStore.NewId(),
            Page = page,
            Kind = kind,
            Box = box,
            Color = color ?? ViewerAnnotationPdf.DefaultColor(kind),
            Author = Author,
            Created = now,
            Modified = now,
            StrokeWidth = kind switch
            {
                ViewerAnnotationKind.Ink => InkWidth,
                ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle or ViewerAnnotationKind.Line
                    or ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine => ShapeWidth,
                ViewerAnnotationKind.FreeText => 0,
                _ => 1,
            },
            FontSize = kind == ViewerAnnotationKind.FreeText ? FreeTextSize : 12,
        };
    }

    private ViewerPageGeometry Geometry(int page) =>
        page >= 1 && page <= Document.Geometry.Count ? Document.Geometry[page - 1] : throw new ViewerAnnotationException($"There is no page {page}.");

    /// <summary>A sticky note at a point of the shown page (its icon kept on the page), with its text.</summary>
    public ViewerAnnotation AddNote(int page, ViewerPoint at, string text)
    {
        var g = Geometry(page);
        double x = Math.Clamp(at.X, 0, Math.Max(0, g.ShownWidth - NoteSize)), y = Math.Clamp(at.Y, 0, Math.Max(0, g.ShownHeight - NoteSize));
        var a = New(ViewerAnnotationKind.Note, page, ClampBox(g.ToUser(new ViewerRect(x, y, NoteSize, NoteSize)), g), null) with { Contents = Clip(text) };
        return Add(a);
    }

    /// <summary>
    /// A freehand drawing from a stroke of shown-page points; points closer than 0.75 point to the
    /// last one kept are dropped. Null when the stroke is too short to keep.
    /// </summary>
    public ViewerAnnotation? AddInk(int page, IReadOnlyList<ViewerPoint> stroke, string? color = null)
    {
        var g = Geometry(page);
        var user = new List<double>();
        ViewerPoint? last = null;
        foreach (var p in stroke)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            var s = new ViewerPoint(Math.Clamp(p.X, 0, g.ShownWidth), Math.Clamp(p.Y, 0, g.ShownHeight));
            if (last is { } l && Math.Abs(s.X - l.X) + Math.Abs(s.Y - l.Y) < 0.75) continue;
            if (user.Count / 2 >= ViewerAnnotationStore.MaxInkPointsPerStroke) break;
            last = s;
            var (ux, uy) = g.ToUser(s.X, s.Y);
            user.Add(ux);
            user.Add(uy);
        }
        if (user.Count < 4) return null;
        var ink = New(ViewerAnnotationKind.Ink, page, default, color);
        return Add(ink with { InkList = [user.ToArray()], Box = ClampBox(ViewerBox.Around(user, ink.StrokeWidth / 2 + 1), g) });
    }

    /// <summary>
    /// A rectangle or ellipse dragged from <paramref name="from"/> to <paramref name="to"/>
    /// (shown-page points, kept on the page). Null when the drag is shorter than
    /// <see cref="MinShapeSize"/> across or down.
    /// </summary>
    public ViewerAnnotation? AddShape(ViewerAnnotationKind kind, int page, ViewerPoint from, ViewerPoint to, string? color = null, ViewerShapeStyle? style = null)
    {
        if (kind is not (ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle)) throw new ArgumentOutOfRangeException(nameof(kind));
        var g = Geometry(page);
        if (!Finite(from) || !Finite(to)) return null;
        var (a, b) = (OnPage(from, g), OnPage(to, g));
        var rect = new ViewerRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (rect.Width < MinShapeSize || rect.Height < MinShapeSize) return null;
        var s = style ?? ViewerShapeStyle.Plain;
        // A rectangle or ellipse has no line endings.
        return Add(New(kind, page, ClampBox(g.ToUser(rect), g), color) with { FillColor = s.Fill, Dash = s.Dash });
    }

    /// <summary>
    /// A line (with an open arrowhead at its end when <paramref name="arrow"/>: /LE [/None /OpenArrow])
    /// dragged from <paramref name="from"/> to <paramref name="to"/> (shown-page points, kept on the
    /// page). Null when it is shorter than <see cref="MinShapeSize"/>.
    /// </summary>
    public ViewerAnnotation? AddLine(int page, ViewerPoint from, ViewerPoint to, bool arrow, string? color = null) =>
        AddLine(page, from, to, new ViewerShapeStyle(End: arrow ? "OpenArrow" : "None"), color);

    /// <summary>
    /// A line with the style of <paramref name="style"/> (its fill is the inside of closed endings, /IC),
    /// dragged from <paramref name="from"/> to <paramref name="to"/> (shown-page points, kept on the
    /// page). Null when it is shorter than <see cref="MinShapeSize"/>.
    /// </summary>
    public ViewerAnnotation? AddLine(int page, ViewerPoint from, ViewerPoint to, ViewerShapeStyle style, string? color = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        var g = Geometry(page);
        if (!Finite(from) || !Finite(to)) return null;
        var (a, b) = (OnPage(from, g), OnPage(to, g));
        if (Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) < MinShapeSize) return null;
        var (x1, y1) = g.ToUser(a.X, a.Y);
        var (x2, y2) = g.ToUser(b.X, b.Y);
        double[] points = [x1, y1, x2, y2];
        var line = New(ViewerAnnotationKind.Line, page, default, color) with { Vertices = points, FillColor = style.Fill, Dash = style.Dash, StartEnding = style.Start, EndEnding = style.End };
        return Add(line with { Box = ClampBox(ViewerBox.Around(points, ViewerAnnotationStore.ShapeMargin(line.StrokeWidth, line.StartEnding, line.EndEnding)), g) });
    }

    /// <summary>
    /// A polygon or connected lines through <paramref name="points"/> (shown-page points, kept on
    /// the page; a point within two points of the one before is dropped, as a double-click gives its
    /// point twice). A polygon needs three points, connected lines two; null with fewer. Both take
    /// the fill and dash of <paramref name="style"/>; connected lines its line endings too.
    /// </summary>
    public ViewerAnnotation? AddPolyline(ViewerAnnotationKind kind, int page, IReadOnlyList<ViewerPoint> points, ViewerShapeStyle? style = null, string? color = null)
    {
        if (kind is not (ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(points);
        var g = Geometry(page);
        var s = style ?? ViewerShapeStyle.Plain;
        var user = new List<double>();
        ViewerPoint? last = null;
        foreach (var p in points)
        {
            if (!Finite(p)) continue;
            if (user.Count >= 2 * ViewerAnnotationStore.MaxVertices) break;
            var q = OnPage(p, g);
            if (last is { } l && Math.Abs(q.X - l.X) + Math.Abs(q.Y - l.Y) < 2) continue;
            last = q;
            var (ux, uy) = g.ToUser(q.X, q.Y);
            user.Add(ux);
            user.Add(uy);
        }
        if (user.Count / 2 < (kind == ViewerAnnotationKind.Polygon ? 3 : 2)) return null;
        bool polygon = kind == ViewerAnnotationKind.Polygon;
        var shape = New(kind, page, default, color) with
        {
            Vertices = user.ToArray(),
            FillColor = s.Fill,
            Dash = s.Dash,
            StartEnding = polygon ? "None" : s.Start,
            EndEnding = polygon ? "None" : s.End,
        };
        return Add(shape with { Box = ClampBox(ViewerBox.Around(user, ViewerAnnotationStore.ShapeMargin(shape.StrokeWidth, shape.StartEnding, shape.EndEnding)), g) });
    }

    /// <summary>
    /// A text box with its top-left corner at a point of the shown page: black <see cref="FreeTextSize"/>
    /// point text, at most <see cref="FreeTextWidth"/> wide and as tall as its text, kept on the page.
    /// Null when the text is empty or only spaces.
    /// </summary>
    public ViewerAnnotation? AddFreeText(int page, ViewerPoint at, string text, string? color = null)
    {
        var g = Geometry(page);
        text = Clip(text);
        if (string.IsNullOrWhiteSpace(text) || !Finite(at)) return null;
        var a = New(ViewerAnnotationKind.FreeText, page, default, color) with { Contents = text };
        return Add(a with { Box = FreeTextBox(g, OnPage(at, g), text, a.FontSize) });
    }

    /// <summary>The size of a text box placed with a click (not dragged), in points of the shown page.</summary>
    public const double TextBoxWidth = 200, TextBoxHeight = 40;

    /// <summary>
    /// An empty text box for the reader to type into (<see cref="SetContents"/>), in the box dragged
    /// from <paramref name="from"/> to <paramref name="to"/> (shown-page points, kept on the page).
    /// A drag smaller than 24 by 12 points is a click: the box is <see cref="TextBoxWidth"/> by
    /// <see cref="TextBoxHeight"/> from there. Its text wraps in the box, which keeps its size.
    /// Delete it when the reader leaves it empty: a text box needs text.
    /// </summary>
    public ViewerAnnotation? AddTextBox(int page, ViewerPoint from, ViewerPoint to, string? color = null)
    {
        var g = Geometry(page);
        if (!Finite(from) || !Finite(to)) return null;
        var (a, b) = (OnPage(from, g), OnPage(to, g));
        var rect = new ViewerRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (rect.Width < 24 || rect.Height < 12)
        {
            double w = Math.Min(TextBoxWidth, g.ShownWidth), h = Math.Min(TextBoxHeight, g.ShownHeight);
            rect = new ViewerRect(Math.Clamp(a.X, 0, g.ShownWidth - w), Math.Clamp(a.Y, 0, g.ShownHeight - h), w, h);
        }
        var added = Add(New(ViewerAnnotationKind.FreeText, page, ClampBox(g.ToUser(rect), g), color));
        _sizedByReader.Add(added.Id);
        return added;
    }

    /// <summary>The line width of a callout's text box border and line.</summary>
    public const double CalloutWidth = 1;

    /// <summary>
    /// A callout: a text box with a line to the spot it points to, as one undo step. The line starts
    /// at <paramref name="anchor"/> (a point of the shown page, which must be on the page) with an
    /// open arrow, and goes to the side of the box that faces it, with a knee
    /// (<see cref="ViewerCallout.Leader"/>). <paramref name="box"/> is the text box on the shown page,
    /// moved onto the page when it reaches past an edge. A box smaller than 24 by 12 points is a
    /// click at its top-left corner: the box is then as wide as a text box and as tall as
    /// <paramref name="text"/> (<see cref="TextBoxWidth"/> by <see cref="TextBoxHeight"/> while it is
    /// empty). Its text follows the text box rules: a character without a font on this device is
    /// refused, and an empty callout is for the reader to type into (delete it when they leave it
    /// empty). Refused when a number is not finite or the anchor is off the page.
    /// </summary>
    public ViewerAnnotation AddCallout(int page, ViewerPoint anchor, ViewerRect box, string text, string? color = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var g = Geometry(page);
        if (!Finite(anchor) || !double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
            throw new ViewerAnnotationException("A callout needs a point on the page and a place for its text.");
        if (anchor.X < 0 || anchor.Y < 0 || anchor.X > g.ShownWidth || anchor.Y > g.ShownHeight)
            throw new ViewerAnnotationException("The spot a callout points to must be on the page.");
        text = Clip(text);
        var a = New(ViewerAnnotationKind.FreeText, page, default, color) with
        {
            Contents = text,
            StrokeWidth = CalloutWidth,
            StartEnding = ViewerCallout.DefaultEnding,
        };
        var r = new ViewerRect(Math.Min(box.X, box.X + box.Width), Math.Min(box.Y, box.Y + box.Height), Math.Abs(box.Width), Math.Abs(box.Height));
        bool sized = r.Width >= 24 && r.Height >= 12;
        ViewerBox user;
        if (!sized && !string.IsNullOrWhiteSpace(text))
        {
            user = FreeTextBox(g, OnPage(new ViewerPoint(r.X, r.Y), g), text, a.FontSize, CalloutWidth);
        }
        else
        {
            if (!sized) r = new ViewerRect(r.X, r.Y, TextBoxWidth, TextBoxHeight);
            double w = Math.Min(r.Width, g.ShownWidth), h = Math.Min(r.Height, g.ShownHeight);
            r = new ViewerRect(Math.Clamp(r.X, 0, g.ShownWidth - w), Math.Clamp(r.Y, 0, g.ShownHeight - h), w, h);
            if (text.Length > 0) MeasureText(text, a.FontSize, Math.Max(r.Width, 1), CalloutWidth); // only the fonts are checked
            user = ClampBox(g.ToUser(r), g);
        }
        var (ax, ay) = g.ToUser(anchor.X, anchor.Y);
        var added = Add(a with { Box = user, Callout = ViewerCallout.Leader(g, user, ax, ay) });
        if (sized || string.IsNullOrWhiteSpace(text)) _sizedByReader.Add(added.Id);
        return added;
    }

    /// <summary>
    /// Moves the spot a callout points to (its anchor) to a point of the shown page, as one undo
    /// step: the knee and the end at the box are laid out again. False when it is the same point;
    /// refused when the point is not finite or off the page, or the annotation is not a callout.
    /// </summary>
    public bool MoveCalloutAnchor(string id, ViewerPoint to)
    {
        CheckCanAnnotate();
        var a = Store.Find(id) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        if (!a.IsCallout) throw new ViewerAnnotationException("Only a callout has a point to move.");
        var g = Geometry(a.Page);
        if (!Finite(to) || to.X < 0 || to.Y < 0 || to.X > g.ShownWidth || to.Y > g.ShownHeight)
            throw new ViewerAnnotationException("The spot a callout points to must be on the page.");
        var (x, y) = g.ToUser(to.X, to.Y);
        if (Math.Abs(x - a.Callout[0]) < 1e-6 && Math.Abs(y - a.Callout[1]) < 1e-6) return false;
        // A file's line 0 wide is not drawn: once the reader moves it, it is drawn (1 point).
        Store.Update(a with
        {
            Callout = ViewerCallout.Leader(g, a.Box, x, y),
            StrokeWidth = a.StrokeWidth > 0 ? a.StrokeWidth : CalloutWidth,
            Modified = DateTimeOffset.UtcNow,
        });
        _edits.Edit();
        return true;
    }

    /// <summary>
    /// A text box's place for its text, on the shown page: wrapped at <see cref="FreeTextWidth"/>
    /// (less near the page's right edge) and exactly as tall as its lines, measured with the fonts,
    /// shaping and wrap of the appearance the engine draws (on a turned page the appearance is
    /// drawn upright as shown, so the shown width is the width the text wraps at). Refused when a
    /// character has no font on this device: the appearance would leave it out.
    /// </summary>
    private static ViewerBox FreeTextBox(ViewerPageGeometry g, ViewerPoint topLeft, string text, double size, double border = 0)
    {
        double x = Math.Min(topLeft.X, Math.Max(0, g.ShownWidth - 40));
        double width = Math.Min(FreeTextWidth, g.ShownWidth - x);
        double height = Math.Min(MeasureText(text, size, width, border).Height + 0.5, g.ShownHeight);
        double y = Math.Min(topLeft.Y, g.ShownHeight - height);
        return ClampBox(g.ToUser(new ViewerRect(x, Math.Max(0, y), width, height)), g);
    }

    /// <summary>The text laid out as the engine draws a text box (/DA /Helv, a border <paramref name="border"/> wide); refused when a font is missing.</summary>
    private static PdfFreeTextLayout MeasureText(string text, double size, double width, double border = 0)
    {
        var layout = PdfAnnotationAppearances.MeasureFreeText(text, "Helv", size, width, border)
            ?? throw new ViewerAnnotationException("This device has no font to draw a text box with.");
        if (layout.Missing > 0)
            throw new ViewerAnnotationException(layout.Missing == 1
                ? "One character of this text has no font on this device, so it would not show in the text box. Remove it and try again."
                : $"{layout.Missing} characters of this text have no font on this device, so they would not show in the text box. Remove them and try again.");
        return layout;
    }

    /// <summary>The longest name a dynamic stamp shows (its second line stays within <see cref="ViewerAnnotationStore.MaxStampSubtitleLength"/>).</summary>
    public const int MaxStampSignerLength = 48;

    /// <summary>
    /// The stamps a reader can place: the engine's standard stamps (<see cref="PdfStamps.Standard"/>),
    /// then its dynamic stamps (<see cref="PdfStamps.Dynamic"/>), which add who stamped and when.
    /// </summary>
    public static IReadOnlyList<PdfStandardStamp> Stamps { get; } = [.. PdfStamps.Standard, .. PdfStamps.Dynamic];

    /// <summary>
    /// A stamp centred on a point of the shown page, at the engine's usual size for its label
    /// (<see cref="PdfStamps.DefaultSize"/>), kept on the page and upright as the page is shown (on
    /// a page with /Rotate its appearance is turned by its form's /Matrix). A dynamic stamp needs
    /// <paramref name="signer"/>: its second line names them and <paramref name="when"/> (now when
    /// null), and they are its author. The name stays in the document; nothing is sent anywhere.
    /// </summary>
    public ViewerAnnotation AddStamp(int page, ViewerPoint at, PdfStandardStamp stamp, string? signer = null, DateTime? when = null)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var g = Geometry(page);
        if (!Stamps.Contains(stamp)) throw new ViewerAnnotationException("That is not one of the viewer's stamps.");
        if (!Finite(at)) throw new ViewerAnnotationException("Tap on the page where the stamp goes.");
        string? subtitle = null;
        string author = Author;
        if (stamp.Dynamic)
        {
            string name = new string((signer ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) throw new ViewerAnnotationException("A stamp with a name and date needs your name.");
            if (name.Length > MaxStampSignerLength) name = name[..MaxStampSignerLength].TrimEnd();
            subtitle = PdfStamps.DynamicLine(name, when ?? DateTime.Now);
            author = name;
        }
        var (w, h) = PdfStamps.DefaultSize(stamp.Label, stamp.Dynamic);
        w = Math.Min(w, g.ShownWidth);
        h = Math.Min(h, g.ShownHeight);
        var p = OnPage(at, g);
        var rect = new ViewerRect(Math.Clamp(p.X - w / 2, 0, g.ShownWidth - w), Math.Clamp(p.Y - h / 2, 0, g.ShownHeight - h), w, h);
        var a = New(ViewerAnnotationKind.Stamp, page, ClampBox(g.ToUser(rect), g), null) with
        {
            StampName = stamp.Name,
            StampSubtitle = subtitle,
            StampTurn = g.Rotation is 90 or 180 or 270 ? g.Rotation : 0,
            Author = author,
        };
        return Add(a);
    }

    /// <summary>A drag smaller than this (points of the shown page, either way) places a custom stamp at its usual size, as a click does.</summary>
    public const double MinStampDrag = 8;

    /// <summary>The most bytes the custom stamps of one document may hold together (each picture or page counted once).</summary>
    public const long MaxCustomStampBytes = 48L * 1024 * 1024;

    /// <summary>
    /// A custom stamp (<see cref="ViewerCustomStamp"/>: a picture or a PDF page) as one undo step,
    /// kept to its shape: in <paramref name="box"/> (a rectangle of the shown page) as large as fits
    /// and centred there, or, for a box smaller than <see cref="MinStampDrag"/> either way (a click
    /// at its corner), at its usual size (<see cref="ViewerCustomStamp.DefaultSize"/>) centred on
    /// that point. It is kept on the page (made smaller when the page is smaller) and drawn upright
    /// as the page is shown. Refused when a number is not finite, or the document's custom stamps
    /// would hold more than <see cref="MaxCustomStampBytes"/>.
    /// </summary>
    public ViewerAnnotation AddCustomStamp(int page, ViewerRect box, ViewerCustomStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var g = Geometry(page);
        if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
            throw new ViewerAnnotationException("Tap or drag on the page where the stamp goes.");
        var r = new ViewerRect(Math.Min(box.X, box.X + box.Width), Math.Min(box.Y, box.Y + box.Height), Math.Abs(box.Width), Math.Abs(box.Height));
        double w, h, cx, cy;
        if (r.Width >= MinStampDrag && r.Height >= MinStampDrag)
        {
            (w, h) = stamp.FitInside(r.Width, r.Height);
            (cx, cy) = (r.X + r.Width / 2, r.Y + r.Height / 2);
        }
        else
        {
            (w, h) = stamp.DefaultSize;
            (cx, cy) = (r.X, r.Y);
        }
        if (w > g.ShownWidth || h > g.ShownHeight)
            (w, h) = stamp.FitInside(Math.Min(w, g.ShownWidth), Math.Min(h, g.ShownHeight));
        var c = OnPage(new ViewerPoint(cx, cy), g);
        var rect = new ViewerRect(Math.Clamp(c.X - w / 2, 0, g.ShownWidth - w), Math.Clamp(c.Y - h / 2, 0, g.ShownHeight - h), w, h);
        CheckCanAnnotate();
        long held = ViewerAnnotationStore.CustomStampBytes(Store.Current.Select(a => a.CustomStamp).Append(stamp));
        if (held > MaxCustomStampBytes)
            throw new ViewerAnnotationException($"The custom stamps of one document can hold at most {MaxCustomStampBytes / (1024 * 1024)} MB of pictures and pages; this one would make it more.");
        var a = New(ViewerAnnotationKind.Stamp, page, ClampBox(g.ToUser(rect), g), null) with
        {
            StampName = ViewerCustomStamp.StampName,
            StampTurn = g.Rotation is 90 or 180 or 270 ? g.Rotation : 0,
            CustomStamp = stamp,
        };
        return Add(a);
    }

    /// <summary>
    /// A custom stamp's box made to its shape again: the largest box of the stamp's shape (upright as
    /// its turn shows it) inside <paramref name="box"/>, centred there.
    /// </summary>
    private static ViewerBox Shaped(ViewerBox box, ViewerCustomStamp stamp, int turn)
    {
        bool quarter = (turn % 360 + 360) % 360 is 90 or 270;
        double aspect = quarter ? 1 / stamp.Aspect : stamp.Aspect;
        double w = box.Width, h = box.Height;
        if (w <= 0 || h <= 0) return box;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        double cx = (box.X1 + box.X2) / 2, cy = (box.Y1 + box.Y2) / 2;
        return new ViewerBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
    }

    /// <summary>The text a stamp shows ("APPROVED"), from its /Name; "STAMP" for a name the viewer does not offer.</summary>
    public static string StampLabel(string name) => Stamps.FirstOrDefault(s => s.Name == name)?.Label ?? "STAMP";

    private static bool Finite(ViewerPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static ViewerPoint OnPage(ViewerPoint p, ViewerPageGeometry g) => new(Math.Clamp(p.X, 0, g.ShownWidth), Math.Clamp(p.Y, 0, g.ShownHeight));

    /// <summary>Highlights, underlines or strikes out the selected text. Null when nothing is selected.</summary>
    public ViewerAnnotation? MarkText(ViewerAnnotationKind kind, ViewerTextSelection selection, string? color = null)
    {
        if (kind is not (ViewerAnnotationKind.Highlight or ViewerAnnotationKind.Underline or ViewerAnnotationKind.StrikeOut))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (selection.IsEmpty) return null;
        var g = Geometry(selection.Page);
        var quads = selection.Bands.SelectMany(g.ToQuad).ToArray();
        if (quads.Length == 0) return null;
        return Add(New(kind, selection.Page, ClampBox(ViewerBox.Around(quads), g), color) with { QuadPoints = quads });
    }

    /// <summary>Changes an annotation's text (a note's comment); false when it is the same.</summary>
    public bool SetContents(string id, string text)
    {
        CheckCanAnnotate();
        var a = Store.Find(id) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        text = Clip(text);
        if (text == a.Contents) return false;
        if (a.Kind == ViewerAnnotationKind.FreeText && string.IsNullOrWhiteSpace(text)) throw new ViewerAnnotationException("A text box needs text; delete it instead.");
        var changed = a with { Contents = text, Modified = DateTimeOffset.UtcNow };
        // A text box made here fits its new text; one from the file, or one the reader sized, keeps its size.
        if (a.Kind == ViewerAnnotationKind.FreeText)
        {
            var g = Geometry(a.Page);
            var shown = g.ToShown(a.Box);
            // A callout's text box has a border: it is measured with it.
            double border = a.IsCallout ? a.StrokeWidth : 0;
            if (_sizedByReader.Contains(a.Id)) MeasureText(text, a.FontSize, Math.Max(shown.Width, 1), border); // only the fonts are checked
            else if (!a.FromFile) changed = changed with { Box = FreeTextBox(g, new ViewerPoint(shown.X, shown.Y), text, a.FontSize, border) };
            else MeasureText(text, a.FontSize, FreeTextWidth, border); // its size is kept: only the fonts are checked
            // A callout's box that changed size keeps its anchor: the line meets the box again.
            if (a.IsCallout && changed.Box != a.Box) changed = ViewerCallout.Relaid(changed, g);
        }
        Store.Update(changed);
        _edits.Edit();
        return true;
    }

    /// <summary>Changes an annotation's colour; false when it is the same, or it is a stamp (drawn in the colour of its kind).</summary>
    public bool SetColor(string id, string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        CheckCanAnnotate();
        var a = Store.Find(id) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        if (a.Kind == ViewerAnnotationKind.Stamp || string.Equals(a.Color, color, StringComparison.OrdinalIgnoreCase)) return false;
        Store.Update(a with { Color = color, Modified = DateTimeOffset.UtcNow });
        _edits.Edit();
        return true;
    }

    /// <summary>
    /// Changes a shape's fill, dash and (for a line or connected lines) line endings as one undo
    /// step; its box grows to hold new endings. False when nothing changed, or it is not a shape
    /// whose style may change (a measurement keeps its own).
    /// </summary>
    public bool SetShapeStyle(string id, ViewerShapeStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        CheckCanAnnotate();
        var a = Store.Find(id) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        if (!a.HasShapeStyle) return false;
        var was = ViewerShapeStyle.Of(a);
        var s = a.HasEndings ? style : style with { Start = was.Start, End = was.End };
        if (s.SameAs(was)) return false;
        var changed = a with { FillColor = s.Fill, Dash = s.Dash, StartEnding = s.Start, EndEnding = s.End, Modified = DateTimeOffset.UtcNow };
        // New endings may reach past the box: it grows to hold them.
        if (s.Start != was.Start || s.End != was.End) changed = changed with { Box = ViewerAnnotationStore.GrownForEndings(changed) };
        Store.Update(changed);
        _edits.Edit();
        return true;
    }

    /// <summary>
    /// Moves an annotation by (<paramref name="dx"/>, <paramref name="dy"/>) points of the shown
    /// page, kept on its page. False when it did not move (stopped by the page edge, a reply, or a
    /// move that is not a finite number).
    /// </summary>
    public bool Move(string id, double dx, double dy)
    {
        CheckCanAnnotate();
        var a = Store.Find(id) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || a.Kind == ViewerAnnotationKind.Reply) return false;
        var g = Geometry(a.Page);
        var (ux, uy) = g.ToUserDelta(Math.Clamp(dx, -g.ShownWidth, g.ShownWidth), Math.Clamp(dy, -g.ShownHeight, g.ShownHeight));
        // The store keeps the box on the page (as here): a move that the page edge stops is no step.
        var m = g.MediaBox;
        static double Clamp(double v, double lo, double hi) => lo > hi ? 0 : Math.Clamp(v, lo, hi);
        if (Clamp(ux, m.Left - a.Box.X1, m.Right - a.Box.X2) == 0 && Clamp(uy, m.Top - a.Box.Y1, m.Bottom - a.Box.Y2) == 0) return false;
        Store.Move(id, ux, uy, DateTimeOffset.UtcNow);
        _edits.Edit();
        return true;
    }

    /// <summary>Deletes an annotation with its replies; false when it is not there.</summary>
    public bool Delete(string id)
    {
        CheckCanAnnotate();
        if (Store.Delete(new ViewerAnnotationFilter(new HashSet<string>(StringComparer.Ordinal) { id })).Count == 0) return false;
        _edits.Edit();
        return true;
    }

    // ------------------------------------------------------------------ a host page's annotations (the embed SDK)

    /// <summary>
    /// Adds annotations a host page gives (all or none), as the store checks them: a missing
    /// author is the reader's name, missing dates are now. They are not the reader's own
    /// (<see cref="MadeHere"/>): the page made them.
    /// </summary>
    public IReadOnlyList<ViewerAnnotation> AddAnnotations(IReadOnlyList<ViewerAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        CheckCanAnnotate();
        var now = DateTimeOffset.UtcNow;
        var added = Store.Add(annotations.Select(a => a with
        {
            Author = a.Author.Length > 0 ? a.Author : Author,
            Created = a.Created != default ? a.Created : now,
            Modified = a.Modified != default ? a.Modified : now,
        }).ToList());
        if (added.Count > 0) _edits.Edit();
        return added;
    }

    /// <summary>
    /// Replaces the annotation with the same id by one a host page gives: a missing author or
    /// creation date stays the annotation's, the modification date is now. With
    /// <paramref name="keepDash"/> (the page gave no dash) the dash stays.
    /// </summary>
    public ViewerAnnotation UpdateAnnotation(ViewerAnnotation annotation, bool keepDash = false)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        CheckCanAnnotate();
        var old = Store.Find(annotation.Id) ?? throw new ViewerAnnotationException($"No annotation has the id \"{annotation.Id}\".");
        // A custom stamp keeps what it draws and its turn (the host page has neither), and its shape: a new box is fitted to it.
        if (old.CustomStamp is { } custom && annotation.Kind == ViewerAnnotationKind.Stamp)
        {
            annotation = annotation with { CustomStamp = custom, StampName = old.StampName, StampTurn = old.StampTurn, StampSubtitle = null };
            if (annotation.Box != old.Box) annotation = annotation with { Box = Shaped(annotation.Box, custom, old.StampTurn) };
        }
        // A callout whose box moved but whose line was given back as it was keeps its anchor: the line meets the box again.
        if (annotation.IsCallout && old.IsCallout && annotation.Page == old.Page && annotation.Box != old.Box && annotation.Callout.SequenceEqual(old.Callout))
            annotation = ViewerCallout.Relaid(annotation, Geometry(old.Page));
        var updated = Store.Update(annotation with
        {
            // Authorship is not rewritten: without an author given, the annotation keeps its own (empty stays empty).
            Author = annotation.Author.Length > 0 ? annotation.Author : old.Author,
            Created = annotation.Created != default ? annotation.Created : old.Created,
            Modified = DateTimeOffset.UtcNow,
            Dash = keepDash ? old.Dash : annotation.Dash,
        });
        _edits.Edit();
        return updated;
    }

    /// <summary>Deletes the annotations the filter matches, with their replies and statuses; returns them.</summary>
    public IReadOnlyList<ViewerAnnotation> DeleteAnnotations(ViewerAnnotationFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        CheckCanAnnotate();
        var removed = Store.Delete(filter);
        if (removed.Count > 0) _edits.Edit();
        return removed;
    }

    public bool Undo()
    {
        CheckCanAnnotate();
        if (!Store.Undo()) return false;
        _edits.Edit();
        return true;
    }

    public bool Redo()
    {
        CheckCanAnnotate();
        if (!Store.Redo()) return false;
        _edits.Edit();
        return true;
    }

    /// <summary>An annotation the reader makes here: added, and remembered as made on this device.</summary>
    private ViewerAnnotation Add(ViewerAnnotation a)
    {
        CheckCanAnnotate();
        var added = Store.Add([a])[0];
        MadeHere.Add(added.Id);
        _edits.Edit();
        return added;
    }

    private void CheckCanAnnotate()
    {
        if (!CanAnnotate) throw new ViewerAnnotationException(Restriction("This document does not allow annotations."));
    }

    private static string Clip(string text) => text.Length > ViewerAnnotationStore.MaxContentsLength ? text[..ViewerAnnotationStore.MaxContentsLength] : text;

    private static ViewerBox ClampBox(ViewerBox b, ViewerPageGeometry g)
    {
        var m = g.MediaBox;
        return new ViewerBox(Math.Clamp(b.X1, m.Left, m.Right), Math.Clamp(b.Y1, m.Top, m.Bottom), Math.Clamp(b.X2, m.Left, m.Right), Math.Clamp(b.Y2, m.Top, m.Bottom));
    }

    private static bool Contains(ViewerRect r, ViewerPoint p, double slack) =>
        p.X >= r.X - slack && p.X <= r.X + r.Width + slack && p.Y >= r.Y - slack && p.Y <= r.Y + r.Height + slack;

    /// <summary>What an annotation is, for a list or a message.</summary>
    public static string Label(ViewerAnnotationKind kind) => kind switch
    {
        ViewerAnnotationKind.Highlight => "Highlight",
        ViewerAnnotationKind.Underline => "Underline",
        ViewerAnnotationKind.StrikeOut => "Strike-through",
        ViewerAnnotationKind.Note => "Note",
        ViewerAnnotationKind.FreeText => "Text box",
        ViewerAnnotationKind.Ink => "Drawing",
        ViewerAnnotationKind.Square => "Rectangle",
        ViewerAnnotationKind.Circle => "Ellipse",
        ViewerAnnotationKind.Line => "Line",
        ViewerAnnotationKind.Polygon => "Polygon",
        ViewerAnnotationKind.PolyLine => "Connected lines",
        ViewerAnnotationKind.Stamp => "Stamp",
        _ => "Reply",
    };

    /// <summary>What an annotation is, for a list or a message: a text box with a line is a callout.</summary>
    public static string Label(ViewerAnnotation a) => a.IsCallout ? "Callout" : Label(a.Kind);

    /// <summary>
    /// The annotations as a list: each one that starts a thread, then its replies with their
    /// level. Statuses are not lines of their own: each line carries its annotation's review status.
    /// </summary>
    public IReadOnlyList<ViewerAnnotationListItem> List()
    {
        var list = Store.Current;
        var replies = list.Where(a => a.Kind == ViewerAnnotationKind.Reply).ToLookup(a => a.InReplyTo);
        var result = new List<ViewerAnnotationListItem>();
        foreach (var a in list.Where(a => a.Kind != ViewerAnnotationKind.Reply)) AddThread(result, a, 0, replies);
        return result;
    }

    /// <summary>
    /// One thread: the annotation <paramref name="id"/> answers none of, or the one given, then
    /// its replies (not its statuses) with their level. Empty when it is not there or is a status.
    /// </summary>
    public IReadOnlyList<ViewerAnnotationListItem> Thread(string id)
    {
        if (Store.Find(id) is not { IsStatus: false } a) return [];
        var replies = Store.Current.Where(r => r.Kind == ViewerAnnotationKind.Reply).ToLookup(r => r.InReplyTo);
        var result = new List<ViewerAnnotationListItem>();
        AddThread(result, a, 0, replies);
        return result;
    }

    private void AddThread(List<ViewerAnnotationListItem> result, ViewerAnnotation a, int level, ILookup<string, ViewerAnnotation> replies)
    {
        // A stamp without a comment is listed by what it shows.
        string summary = a.Kind == ViewerAnnotationKind.Stamp && string.IsNullOrWhiteSpace(a.Contents)
            ? a.IsCustomStamp ? "Custom stamp" : StampLabel(a.StampName)
            : Summary(a.Contents);
        result.Add(new ViewerAnnotationListItem(a, level, Label(a), summary, ReviewState(replies[a.Id])));
        // The store keeps threads within the depth limit; the level check is a second guard.
        if (level < ViewerAnnotationStore.MaxReplyDepth)
            foreach (var r in replies[a.Id].Where(r => !r.IsStatus)) AddThread(result, r, level + 1, replies);
    }

    // ------------------------------------------------------------------ replies and review status

    /// <summary>The review statuses of Acrobat's Review model, as the desktop viewer offers them; "None" clears the status.</summary>
    public static IReadOnlyList<string> ReviewStates { get; } = ["Accepted", "Rejected", "Cancelled", "Completed", "None"];

    /// <summary>The state model of review statuses (/StateModel /Review).</summary>
    public const string ReviewStateModel = "Review";

    /// <summary>
    /// The ids of the annotations made on this device: those this session makes are added, and a
    /// viewer adds the ones it kept from earlier sessions. An annotation imported from XFDF or read
    /// from a file is not among them, even when it carries the reader's name.
    /// </summary>
    public ISet<string> MadeHere { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// An annotation the reader may change and delete as their own: made on this device
    /// (<see cref="MadeHere"/>) under the name they have now (set, and its author). A name alone
    /// is not enough: anyone can write any name into a file.
    /// </summary>
    public bool IsOwn(ViewerAnnotation a) =>
        Author.Length > 0 && string.Equals(a.Author, Author, StringComparison.Ordinal) && MadeHere.Contains(a.Id);

    /// <summary>
    /// What deleting annotation <paramref name="id"/> also deletes: the number of replies under it
    /// (statuses not counted) and how many of them other people wrote (not the reader's own).
    /// </summary>
    public (int Replies, int ByOthers) DeletedWith(string id)
    {
        var replies = Store.DeletedWith(id).Where(a => !a.IsStatus).ToList();
        return (replies.Count, replies.Count(a => !IsOwn(a)));
    }

    /// <summary>
    /// A reply to annotation <paramref name="parentId"/> (a comment or a reply) with the reader's
    /// text, under their name; null when the text is empty. Written as Acrobat and the desktop
    /// viewer write one: a note with /IRT, /RT /R, the Comment icon and /F 28.
    /// </summary>
    public ViewerAnnotation? AddReply(string parentId, string text)
    {
        CheckCanAnnotate();
        var parent = Store.Find(parentId) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        if (parent.IsStatus) throw new ViewerAnnotationException("A status cannot be answered.");
        text = Clip(text.Trim());
        if (text.Length == 0) return null;
        return Add(NewReply(parent, text));
    }

    private ViewerAnnotation NewReply(ViewerAnnotation parent, string text)
    {
        var now = DateTimeOffset.UtcNow;
        return new ViewerAnnotation
        {
            Id = ViewerAnnotationStore.NewId(),
            Page = parent.Page,
            Kind = ViewerAnnotationKind.Reply,
            Box = parent.Box,
            InReplyTo = parent.Id,
            Color = ViewerAnnotationPdf.DefaultColor(ViewerAnnotationKind.Reply),
            Contents = text,
            Author = Author,
            Created = now,
            Modified = now,
        };
    }

    /// <summary>
    /// The review status of annotation <paramref name="id"/>: the latest Review status set on it
    /// (by date, then as listed), or null when there is none or it was cleared ("None").
    /// </summary>
    public string? ReviewStateOf(string id) =>
        ReviewState(Store.Current.Where(a => a.Kind == ViewerAnnotationKind.Reply && a.InReplyTo == id));

    private static string? ReviewState(IEnumerable<ViewerAnnotation> replies)
    {
        var latest = replies.Where(r => r.IsStatus && string.Equals(r.StateModel, ReviewStateModel, StringComparison.OrdinalIgnoreCase))
            .Select((r, i) => (r, i)).OrderBy(x => x.r.Created).ThenBy(x => x.i).Select(x => x.r).LastOrDefault();
        return latest == null || string.Equals(latest.State, "None", StringComparison.OrdinalIgnoreCase) ? null : latest.State;
    }

    /// <summary>
    /// Sets the review status of annotation <paramref name="id"/> as Acrobat and the desktop viewer
    /// do: a hidden reply (/F 30) with /State and /StateModel /Review, under the reader's name,
    /// saying who set it. "None" clears it. One step in the undo history. Null when the status is
    /// the annotation's already; refused when it is not one of <see cref="ReviewStates"/>.
    /// </summary>
    public ViewerAnnotation? SetReviewState(string id, string state)
    {
        CheckCanAnnotate();
        var parent = Store.Find(id) ?? throw new ViewerAnnotationException("That annotation is no longer there.");
        if (parent.Kind == ViewerAnnotationKind.Reply) throw new ViewerAnnotationException("A status is set on a comment, not on a reply.");
        string known = ReviewStates.FirstOrDefault(s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase))
            ?? throw new ViewerAnnotationException($"A review status is one of {string.Join(", ", ReviewStates)}.");
        if (string.Equals(ReviewStateOf(id) ?? "None", known, StringComparison.Ordinal)) return null;
        string by = Author.Length > 0 ? Author : "the reader";
        return Add(NewReply(parent, $"{known} set by {by}") with { State = known, StateModel = ReviewStateModel });
    }

    // ------------------------------------------------------------------ XFDF

    /// <summary>
    /// The annotations as an XFDF document (the engine's <see cref="PdfAnnotationXfdf"/>): replies
    /// with name, inreplyto and replyType "reply", statuses with state and statemodel.
    /// <paramref name="href"/> names the PDF.
    /// </summary>
    public string ExportXfdf(string? href = null) =>
        PdfAnnotationXfdf.Export(Store.Current.Select(a => ViewerAnnotationPdf.ToPdf(a, null)), href);

    /// <summary>
    /// Adds the annotations of an XFDF document as one change (one undo step), as the web viewer's
    /// importXfdf does: names in use get new ids, replies are linked to their parents (in the
    /// import or the document) or added on their own. Annotations off the document's pages or
    /// outside the store's limits are left out and counted. Refused when the text is not XFDF.
    /// </summary>
    public ViewerXfdfImport ImportXfdf(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        CheckCanAnnotate();
        PdfXfdfImport import;
        try
        {
            import = PdfAnnotationXfdf.ImportWithReport(xml);
        }
        catch (FormatException ex)
        {
            throw new ViewerAnnotationException("This is not an XFDF file that can be read: " + ex.Message);
        }
        int pages = Document.Geometry.Count;
        var onPages = import.Annotations.Where(p => p.PageNumber >= 1 && p.PageNumber <= pages).ToList();
        int skipped = import.Skipped + import.Annotations.Count - onPages.Count;
        var ids = Store.Current.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var taken = new List<ViewerAnnotation>();
        // The document's annotations and the ones taken so far, by id: a reply's thread depth is counted through them.
        var known = Store.Current.ToDictionary(a => a.Id, StringComparer.Ordinal);
        // Parents come before their replies: a reply whose parent was left out is left out too.
        foreach (var read in ViewerAnnotationPdf.FromXfdf(onPages, ids))
        {
            var a = read with
            {
                Author = read.Author.Length > 0 ? read.Author : Author,
                Created = read.Created != default ? read.Created : now,
                Modified = read.Modified != default ? read.Modified : now,
            };
            bool reply = a.Kind == ViewerAnnotationKind.Reply;
            if (!Store.Accepts(a)
                || reply && !known.ContainsKey(a.InReplyTo)
                // A reply deeper than the thread limit is left out (with its own replies).
                || reply && ViewerAnnotationPdf.Depth(known[a.InReplyTo], known) + 1 > ViewerAnnotationStore.MaxReplyDepth)
            {
                skipped++;
                continue;
            }
            known[a.Id] = a;
            taken.Add(a);
        }
        // The limits of the whole document refuse the whole import: leaving out an arbitrary part would mislead.
        if (taken.Count > ViewerAnnotationStore.MaxPerCall)
            throw new ViewerAnnotationException($"An XFDF file can add at most {ViewerAnnotationStore.MaxPerCall} annotations at once; this one has {taken.Count}.");
        if (Store.Current.Count + taken.Count > ViewerAnnotationStore.MaxAnnotations)
            throw new ViewerAnnotationException($"This XFDF file would give the document more than {ViewerAnnotationStore.MaxAnnotations} annotations ({Store.Current.Count} now, {taken.Count} to add); nothing was added.");
        if (ViewerAnnotationStore.Points(Store.Current) + ViewerAnnotationStore.Points(taken) > ViewerAnnotationStore.MaxPoints)
            throw new ViewerAnnotationException($"This XFDF file would give the document's annotations more than {ViewerAnnotationStore.MaxPoints} ink, quadPoints and vertices points together; nothing was added.");
        IReadOnlyList<ViewerAnnotation> added = [];
        if (taken.Count > 0)
        {
            added = Store.Add(taken);
            _edits.Edit();
        }
        return new ViewerXfdfImport(added.Count, skipped) { Annotations = added };
    }

    // ------------------------------------------------------------------ keeping the edits while the app is ended

    /// <summary>
    /// The reader's edits as a <see cref="ViewerEditSnapshot"/> for the document at
    /// <paramref name="location"/> whose bytes as opened have <paramref name="originalHash"/>
    /// (<see cref="ViewerEditSnapshot.Hash"/>): the annotations (a file's annotation that did not
    /// change is kept by its id only) and the form values the reader set.
    /// </summary>
    public ViewerEditSnapshot Snapshot(string location, string originalHash)
    {
        var file = new HashSet<ViewerAnnotation>(Store.FromFile, ReferenceEqualityComparer.Instance);
        return new ViewerEditSnapshot
        {
            Location = location,
            OriginalLength = Document.OriginalBytes.LongLength,
            OriginalHash = originalHash,
            Annotations = Store.Current.Select(a => file.Contains(a) ? new ViewerSnapshotItem(a.Id, null) : new ViewerSnapshotItem(null, a)).ToList(),
            Values = _values.Select(p => new KeyValuePair<string, IReadOnlyList<string>>(p.Key, p.Value)).ToList(),
        };
    }

    /// <summary>
    /// Gives the edits of <paramref name="snapshot"/> back to this session, opened on the same
    /// bytes, as one change (undo goes back to the file as opened): its annotations, when the
    /// document allows annotations, and the values of fields it still has, when it allows form
    /// filling, each checked as when the reader set it. Then <see cref="ApplyAsync"/> writes them.
    /// False when there was nothing to give back. Refused (nothing changed) when an annotation
    /// fails the store's checks.
    /// </summary>
    public bool Restore(ViewerEditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var fileById = new Dictionary<string, ViewerAnnotation>(StringComparer.Ordinal);
        foreach (var f in Store.FromFile) fileById.TryAdd(f.Id, f);
        var list = new List<ViewerAnnotation>();
        foreach (var item in snapshot.Annotations)
        {
            if (item.KeptId is { } kept)
            {
                if (fileById.TryGetValue(kept, out var f)) list.Add(f);
                continue;
            }
            if (item.Annotation is not { } a) continue;
            // A file's annotation keeps its place in the file only where the file has it there.
            bool inFile = a.FromFile && fileById.TryGetValue(a.Id, out var at) && at.SourceIndex == a.SourceIndex && at.Page == a.Page;
            list.Add(inFile ? a : a with { SourceIndex = -1 });
        }
        bool changed = false;
        bool annotations = list.Count != Store.Current.Count || list.Where((a, i) => !ReferenceEquals(a, Store.Current[i])).Any();
        if (annotations && CanAnnotate)
        {
            Store.Restore(list);
            changed = true;
        }
        if (CanFillForms)
        {
            foreach (var (field, values) in snapshot.Values)
            {
                if (Widgets.FirstOrDefault(w => w.Name == field) is not { } widget || Normalize(widget, values, out _) is not { } taken) continue;
                if ((ValueOf(field) ?? widget.Values).SequenceEqual(taken, StringComparer.Ordinal)) continue;
                _values[field] = taken;
                changed = true;
            }
        }
        if (changed) _edits.Edit();
        return changed;
    }

    /// <summary>The first line of a text, at most 80 characters.</summary>
    private static string Summary(string text)
    {
        string line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 80 ? line[..79] + "…" : line;
    }

    // ------------------------------------------------------------------ writing and saving

    /// <summary>
    /// Writes the edits into the document until it has the latest or writing fails, one write at
    /// a time (a call while one runs returns at once: the running one takes the new edits too).
    /// A value the engine refuses goes back to what it was and is named in the result.
    /// </summary>
    public async Task<ViewerApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        if (_applying) return default;
        _applying = true;
        bool written = false;
        string? refused = null, failed = null;
        try
        {
            int attempts = 0;
            while (_edits.NeedsApply && attempts++ < 50)
            {
                int version = _edits.EditVersion;
                var fromFile = Store.FromFile;
                var current = Store.Current;
                var values = _values.Select(p => (p.Key, p.Value)).ToList();
                try
                {
                    if (BeforeWrite is { } before) await before(cancellationToken);
                    // The engine writes on a worker thread where there are threads (not in a browser).
                    if (OperatingSystem.IsBrowser())
                        await Document.ApplyEditsAsync(fromFile, current, values, cancellationToken);
                    else
                        await Task.Run(() => Document.ApplyEditsAsync(fromFile, current, values, cancellationToken), cancellationToken);
                }
                catch (PdfFieldValidationException ex)
                {
                    // The field's own rule refused the value: it goes back to what it was. The
                    // engine's validation message says what the rule wants; the field is named.
                    _values.Remove(ex.FieldName);
                    if (_lastField != null) _values.Remove(_lastField);
                    refused = $"{ex.FieldName}: {ex.Message}";
                    continue;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException && _lastField != null && _values.ContainsKey(_lastField))
                {
                    // Not the exception's own text: the field is named, with what went wrong.
                    string field = _lastField!;
                    _values.Remove(field);
                    refused = $"{field}: that value is not accepted ({ex.GetType().Name}).";
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Type names only, never document content. The version is not applied: the
                    // document stays dirty, and a save refuses bytes that lack these edits.
                    _edits.Failed(version);
                    failed = $"The changes could not be written into the document ({ex.GetType().Name}). They are not saved.";
                    break;
                }
                _edits.Applied(version);
                written = true;
                if (CanFillForms)
                {
                    // The bytes have the edit already: a failure to list the fields again keeps the
                    // last list, and the viewer still draws the pages again.
                    try
                    {
                        Widgets = await Document.GetFormWidgetsAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException and not OutOfMemoryException)
                    {
                    }
                }
                Written?.Invoke();
            }
        }
        catch (ObjectDisposedException)
        {
            // The document was closed.
        }
        finally
        {
            _applying = false;
        }
        var result = new ViewerApplyResult(written, refused, failed);
        // Whoever started the write (the viewer after a change, or a save), the viewer hears of a refusal or failure.
        if (refused != null || failed != null) Reported?.Invoke(result);
        return result;
    }

    /// <summary>
    /// A write refused a value or failed (<see cref="ViewerApplyResult.Refused"/>,
    /// <see cref="ViewerApplyResult.Failed"/>), whoever started it: the viewer tells the reader.
    /// Raised on the thread that called <see cref="ApplyAsync"/>.
    /// </summary>
    public event Action<ViewerApplyResult>? Reported;

    /// <summary>For tests: runs before each write of the edits (a slow or failing write).</summary>
    internal Func<CancellationToken, Task>? BeforeWrite { get; set; }

    /// <summary>
    /// Starts a save: the document's bytes with every edit (written first when needed), or null
    /// when they cannot have the latest edit or a save runs already. End it with <see cref="EndSave"/>.
    /// </summary>
    public async Task<ViewerSave?> BeginSaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_edits.TryBeginSave()) return null;
        try
        {
            await WriteOutAsync(cancellationToken);
        }
        catch
        {
            _edits.EndSave();
            throw;
        }
        if (_applying || !_edits.Saving)
        {
            _edits.EndSave();
            return null;
        }
        int id = _edits.Post();
        if (id == 0) return null;
        try
        {
            return new ViewerSave(id, Document.Bytes);
        }
        catch
        {
            // No bytes to hand out (the document was closed): the save ends here.
            _edits.EndSave();
            throw;
        }
    }

    /// <summary>
    /// Waits until the document's bytes have every edit made so far, writing them when needed (a
    /// download or a print takes the bytes then). False when the latest edit could not be written.
    /// </summary>
    public async Task<bool> WriteAllAsync(CancellationToken cancellationToken = default)
    {
        await WriteOutAsync(cancellationToken);
        return !_applying && _edits.IsCurrent;
    }

    private async Task WriteOutAsync(CancellationToken cancellationToken)
    {
        // A write under way always ends: it is waited for however long it takes (a large
        // document), until the token cancels. Only new writes count against the limit.
        int writes = 0;
        while (_applying || _edits.NeedsApply)
        {
            if (_applying)
            {
                await Task.Delay(20, cancellationToken);
                continue;
            }
            if (writes++ >= 200) break;
            await ApplyAsync(cancellationToken);
        }
    }

    /// <summary>The id of the save whose bytes were handed out and not yet answered, or 0.</summary>
    public int PendingSave => _edits.PendingSave;

    /// <summary>The save ended: <paramref name="saved"/> when its bytes were written to the file. False when it is not the save in flight.</summary>
    public bool EndSave(ViewerSave save, bool saved)
    {
        ArgumentNullException.ThrowIfNull(save);
        return _edits.Answer(save.Id, saved);
    }

    /// <summary>
    /// The answer to save <paramref name="saveId"/> (<see cref="ViewerSave.Id"/>), for a viewer that
    /// hands the bytes to another party and gets back only the id. False when it is not the save in flight.
    /// </summary>
    public bool EndSave(int saveId, bool saved) => _edits.Answer(saveId, saved);
}
