using PdfEngine.Vector.Forms;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>One line of an annotation list: the annotation, its reply level, what it is and the start of its text.</summary>
public sealed record ViewerAnnotationListItem(ViewerAnnotation Annotation, int Level, string Label, string Summary);

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
            StrokeWidth = kind == ViewerAnnotationKind.Ink ? InkWidth : 1,
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
        Store.Update(a with { Contents = text, Modified = DateTimeOffset.UtcNow });
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

    /// <summary>The annotations as a list: each one that starts a thread, then its replies with their level.</summary>
    public IReadOnlyList<ViewerAnnotationListItem> List()
    {
        var list = Store.Current;
        var replies = list.Where(a => a.Kind == ViewerAnnotationKind.Reply).ToLookup(a => a.InReplyTo);
        var result = new List<ViewerAnnotationListItem>();
        void Add(ViewerAnnotation a, int level)
        {
            result.Add(new ViewerAnnotationListItem(a, level, Label(a.Kind), Summary(a.Contents)));
            // The store keeps threads within the depth limit; the level check is a second guard.
            if (level < ViewerAnnotationStore.MaxReplyDepth)
                foreach (var r in replies[a.Id]) Add(r, level + 1);
        }
        foreach (var a in list.Where(a => a.Kind != ViewerAnnotationKind.Reply)) Add(a, 0);
        return result;
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
