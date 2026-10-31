using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Forms;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>
/// One line of an annotation list: the annotation, its reply level, what it is, the start of its
/// text, and its review status ("Accepted"; null for none).
/// </summary>
public sealed record ViewerAnnotationListItem(ViewerAnnotation Annotation, int Level, string Label, string Summary, string? Status = null);

/// <summary>What an XFDF import added, and how many of its annotations were left out (unreadable, off the document's pages or outside the limits).</summary>
public readonly record struct ViewerXfdfImport(int Added, int Skipped);

/// <summary>What writing the edits into the document did: written (pages must be drawn again), a value refused, or a failure.</summary>
public readonly record struct ViewerApplyResult(bool Written, string? Refused, string? Failed);

/// <summary>A save in flight: the bytes to write and the id that ends it (<see cref="ViewerEditSession.EndSave"/>).</summary>
public sealed record ViewerSave(int Id, byte[] Bytes);

/// <summary>
/// The reader's edits of one document for a native viewer (the Android app): its annotations in a
/// <see cref="ViewerAnnotationStore"/> with undo and redo, the form values the reader set, and
/// the rules for both. After each change the viewer calls <see cref="ApplyAsync"/>, which has the
/// engine write everything into the document (<see cref="ViewerDocument.ApplyEditsAsync"/>, an
/// incremental update of the file as opened), so the pages drawn again are what a save writes.
/// The rules are the web viewer's: the document's permissions and signatures decide whether
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
    private string? _lastField;
    private bool _noticeTaken, _applying;

    private ViewerEditSession(ViewerDocument document, ViewerAnnotationStore store, IReadOnlyList<ViewerFormWidget> widgets, string author, string? problem)
    {
        Document = document;
        Store = store;
        Widgets = widgets;
        Author = author;
        Problem = problem;
    }

    /// <summary>
    /// Starts editing <paramref name="document"/>: its own annotations (when they can be read) and,
    /// when the document allows form filling, its fillable fields.
    /// </summary>
    public static async Task<ViewerEditSession> StartAsync(ViewerDocument document, string author = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<ViewerAnnotation> fromFile = [];
        string? problem = null;
        try
        {
            fromFile = await document.ReadAnnotationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException and not OutOfMemoryException)
        {
            // Type names only, never document content; the annotations are still drawn, but not listed.
            problem = $"The document's annotations could not be read ({ex.GetType().Name}).";
        }
        var widgets = document.CanFillForms ? await document.GetFormWidgetsAsync(cancellationToken).ConfigureAwait(false) : [];
        return new ViewerEditSession(document, new ViewerAnnotationStore(document.Geometry, fromFile), widgets, author, problem);
    }

    public ViewerDocument Document { get; }

    public ViewerAnnotationStore Store { get; }

    /// <summary>The widgets of the fillable fields with their current values (none when form filling is not allowed).</summary>
    public IReadOnlyList<ViewerFormWidget> Widgets { get; private set; }

    /// <summary>The name written as the author of new annotations.</summary>
    public string Author { get; set; }

    /// <summary>Why the document's annotations are not listed, or null.</summary>
    public string? Problem { get; }

    public bool CanAnnotate => Document.CanAnnotate;

    public bool CanFillForms => Document.CanFillForms;

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
            refusal = Document.EditRestriction ?? "This document does not allow form filling.";
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
                ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle or ViewerAnnotationKind.Line => ShapeWidth,
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
    public ViewerAnnotation? AddShape(ViewerAnnotationKind kind, int page, ViewerPoint from, ViewerPoint to, string? color = null)
    {
        if (kind is not (ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle)) throw new ArgumentOutOfRangeException(nameof(kind));
        var g = Geometry(page);
        if (!Finite(from) || !Finite(to)) return null;
        var (a, b) = (OnPage(from, g), OnPage(to, g));
        var rect = new ViewerRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        if (rect.Width < MinShapeSize || rect.Height < MinShapeSize) return null;
        return Add(New(kind, page, ClampBox(g.ToUser(rect), g), color));
    }

    /// <summary>
    /// A line (with an open arrowhead at its end when <paramref name="arrow"/>: /LE [/None /OpenArrow])
    /// dragged from <paramref name="from"/> to <paramref name="to"/> (shown-page points, kept on the
    /// page). Null when it is shorter than <see cref="MinShapeSize"/>.
    /// </summary>
    public ViewerAnnotation? AddLine(int page, ViewerPoint from, ViewerPoint to, bool arrow, string? color = null)
    {
        var g = Geometry(page);
        if (!Finite(from) || !Finite(to)) return null;
        var (a, b) = (OnPage(from, g), OnPage(to, g));
        if (Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) < MinShapeSize) return null;
        var (x1, y1) = g.ToUser(a.X, a.Y);
        var (x2, y2) = g.ToUser(b.X, b.Y);
        double[] points = [x1, y1, x2, y2];
        var line = New(ViewerAnnotationKind.Line, page, default, color) with { Vertices = points, EndEnding = arrow ? "OpenArrow" : "None" };
        return Add(line with { Box = ClampBox(ViewerBox.Around(points, ViewerAnnotationStore.ShapeMargin(line.StrokeWidth, arrow)), g) });
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

    /// <summary>
    /// A text box's place for its text, on the shown page: wrapped at <see cref="FreeTextWidth"/>
    /// (less near the page's right edge) and exactly as tall as its lines, measured with the fonts,
    /// shaping and wrap of the appearance the engine draws (on a turned page the appearance is
    /// drawn upright as shown, so the shown width is the width the text wraps at). Refused when a
    /// character has no font on this device: the appearance would leave it out.
    /// </summary>
    private static ViewerBox FreeTextBox(ViewerPageGeometry g, ViewerPoint topLeft, string text, double size)
    {
        double x = Math.Min(topLeft.X, Math.Max(0, g.ShownWidth - 40));
        double width = Math.Min(FreeTextWidth, g.ShownWidth - x);
        double height = Math.Min(MeasureText(text, size, width).Height + 0.5, g.ShownHeight);
        double y = Math.Min(topLeft.Y, g.ShownHeight - height);
        return ClampBox(g.ToUser(new ViewerRect(x, Math.Max(0, y), width, height)), g);
    }

    /// <summary>The text laid out as the engine draws a text box (/DA /Helv, no border); refused when a font is missing.</summary>
    private static PdfFreeTextLayout MeasureText(string text, double size, double width)
    {
        var layout = PdfAnnotationAppearances.MeasureFreeText(text, "Helv", size, width)
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
        // A text box made here fits its new text; one from the file keeps its size.
        if (a.Kind == ViewerAnnotationKind.FreeText)
        {
            var g = Geometry(a.Page);
            var shown = g.ToShown(a.Box);
            if (!a.FromFile) changed = changed with { Box = FreeTextBox(g, new ViewerPoint(shown.X, shown.Y), text, a.FontSize) };
            else MeasureText(text, a.FontSize, FreeTextWidth); // its size is kept: only the fonts are checked
        }
        Store.Update(changed);
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

    private ViewerAnnotation Add(ViewerAnnotation a)
    {
        CheckCanAnnotate();
        var added = Store.Add([a])[0];
        _edits.Edit();
        return added;
    }

    private void CheckCanAnnotate()
    {
        if (!CanAnnotate) throw new ViewerAnnotationException(Document.EditRestriction ?? "This document does not allow annotations.");
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
        string summary = a.Kind == ViewerAnnotationKind.Stamp && string.IsNullOrWhiteSpace(a.Contents) ? StampLabel(a.StampName) : Summary(a.Contents);
        result.Add(new ViewerAnnotationListItem(a, level, Label(a.Kind), summary, ReviewState(replies[a.Id])));
        // The store keeps threads within the depth limit; the level check is a second guard.
        if (level < ViewerAnnotationStore.MaxReplyDepth)
            foreach (var r in replies[a.Id].Where(r => !r.IsStatus)) AddThread(result, r, level + 1, replies);
    }

    // ------------------------------------------------------------------ replies and review status

    /// <summary>The review statuses of Acrobat's Review model, as the desktop viewer offers them; "None" clears the status.</summary>
    public static IReadOnlyList<string> ReviewStates { get; } = ["Accepted", "Rejected", "Cancelled", "Completed", "None"];

    /// <summary>The state model of review statuses (/StateModel /Review).</summary>
    public const string ReviewStateModel = "Review";

    /// <summary>An annotation the reader made under their own name (their name is set and is its author): they may change and delete it.</summary>
    public bool IsOwn(ViewerAnnotation a) => Author.Length > 0 && string.Equals(a.Author, Author, StringComparison.Ordinal);

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
        var known = new HashSet<string>(ids, StringComparer.Ordinal);
        // Parents come before their replies: a reply whose parent was left out is left out too.
        foreach (var read in ViewerAnnotationPdf.FromXfdf(onPages, ids))
        {
            var a = read with
            {
                Author = read.Author.Length > 0 ? read.Author : Author,
                Created = read.Created != default ? read.Created : now,
                Modified = read.Modified != default ? read.Modified : now,
            };
            if (!Store.Accepts(a) || a.Kind == ViewerAnnotationKind.Reply && !known.Contains(a.InReplyTo))
            {
                skipped++;
                continue;
            }
            known.Add(a.Id);
            taken.Add(a);
        }
        if (taken.Count > ViewerAnnotationStore.MaxPerCall)
            throw new ViewerAnnotationException($"An XFDF file can add at most {ViewerAnnotationStore.MaxPerCall} annotations at once; this one has {taken.Count}.");
        if (taken.Count > 0)
        {
            Store.Add(taken);
            _edits.Edit();
        }
        return new ViewerXfdfImport(taken.Count, skipped);
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
                    // The engine writes on a worker thread where there are threads (not in a browser).
                    if (OperatingSystem.IsBrowser())
                        await Document.ApplyEditsAsync(fromFile, current, values, cancellationToken);
                    else
                        await Task.Run(() => Document.ApplyEditsAsync(fromFile, current, values, cancellationToken), cancellationToken);
                }
                catch (PdfFieldValidationException ex)
                {
                    // The field's own rule refused the value: it goes back to what it was.
                    _values.Remove(ex.FieldName);
                    if (_lastField != null) _values.Remove(_lastField);
                    refused = ex.Message;
                    continue;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException && _lastField != null && _values.ContainsKey(_lastField))
                {
                    // The engine's message names the field and the rule (an option, a length), never other content.
                    _values.Remove(_lastField!);
                    refused = ex is ArgumentException ? ex.Message : "That value is not accepted.";
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
        return new ViewerApplyResult(written, refused, failed);
    }

    /// <summary>
    /// Starts a save: the document's bytes with every edit (written first when needed), or null
    /// when they cannot have the latest edit or a save runs already. End it with <see cref="EndSave"/>.
    /// </summary>
    public async Task<ViewerSave?> BeginSaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_edits.TryBeginSave()) return null;
        try
        {
            // A write under way always ends; new writes are tried a few times.
            for (int tries = 0; (_applying || _edits.NeedsApply) && tries < 200; tries++)
            {
                if (_applying) await Task.Delay(20, cancellationToken);
                else await ApplyAsync(cancellationToken);
            }
        }
        catch
        {
            _edits.EndSave();
            throw;
        }
        if (_applying)
        {
            _edits.EndSave();
            return null;
        }
        int id = _edits.Post();
        return id == 0 ? null : new ViewerSave(id, Document.Bytes);
    }

    /// <summary>The save ended: <paramref name="saved"/> when its bytes were written to the file.</summary>
    public void EndSave(ViewerSave save, bool saved)
    {
        ArgumentNullException.ThrowIfNull(save);
        _edits.Answer(save.Id, saved);
    }
}
