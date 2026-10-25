using System.Text.Json;
using Microsoft.JSInterop;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Forms;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Annotations: the annotation toolbar (highlight, underline and strike-out on a text selection,
/// note, free text, ink, rectangle, ellipse; select, move, delete; undo and redo), the comments
/// panel, and the host page's annotation manager. The annotations live in a
/// <see cref="ViewerAnnotationStore"/>; after every change the engine writes them into the
/// document (<see cref="ViewerDocument.ApplyEditsAsync"/>) and the pages are drawn again from
/// it, so what the reader sees is what a save writes.
/// </summary>
public sealed partial class Viewer
{
    private enum Tool { Select, Highlight, Underline, StrikeOut, Note, FreeText, Ink, Square, Circle }

    private static readonly string[] ToolNames = ["select", "highlight", "underline", "strikeout", "note", "freetext", "ink", "square", "circle"];

    /// <summary>The colours offered in the annotation toolbar.</summary>
    private static readonly string[] Palette = ["#FFD400", "#22A06B", "#E5484D", "#2563EB", "#000000"];

    private ViewerAnnotationStore? _store;
    private ILookup<int, ViewerAnnotation> _annotsByPage = Array.Empty<ViewerAnnotation>().ToLookup(a => a.Page);
    private int _annotsVersion = -1;
    private Tool _tool = Tool.Select;
    private readonly Dictionary<Tool, string> _toolColors = new();
    private string? _selectedId;
    private string? _editingId;
    private string _editText = "";
    private bool _editIsNew;
    private bool _showAnnotationToolbar = true, _showCommentsPanel = true;
    private string _authorName = "Guest";
    private bool _readOnly;

    // Edits (annotations and form values) are counted; the document is written again until it has the latest.
    private int _editVersion, _appliedVersion, _savedVersion;
    private bool _applying;
    private bool _appearanceFontsReady;

    /// <summary>The reader or the host page may change annotations.</summary>
    private bool CanEditAnnotations => _store != null && !_readOnly && _doc?.CanAnnotate == true;

    private bool ShowsAnnotationToolbar => CanEditAnnotations && _config.HasAnnotationTools && _showAnnotationToolbar;

    private bool ShowsCommentsPanel => _store != null && _showCommentsPanel && _config.HasSidePanel
                                       && (_config.HasAnnotationTools || _config.IncludePdfAnnotations);

    /// <summary>Annotations can be clicked (selected) on the pages.</summary>
    private bool AnnotationsClickable => _store != null && _tool == Tool.Select && (_config.HasAnnotationTools || _config.EnableAnnotationApis);

    private bool IsDirty => _editVersion != _savedVersion;

    private ViewerAnnotation? Selected => _selectedId == null ? null : _store?.Find(_selectedId);

    private string ToolColor(Tool tool) => _toolColors.TryGetValue(tool, out var c) ? c : ViewerAnnotationPdf.DefaultColor(KindOf(tool) ?? ViewerAnnotationKind.Square);

    private static ViewerAnnotationKind? KindOf(Tool tool) => tool switch
    {
        Tool.Highlight => ViewerAnnotationKind.Highlight,
        Tool.Underline => ViewerAnnotationKind.Underline,
        Tool.StrikeOut => ViewerAnnotationKind.StrikeOut,
        Tool.Note => ViewerAnnotationKind.Note,
        Tool.FreeText => ViewerAnnotationKind.FreeText,
        Tool.Ink => ViewerAnnotationKind.Ink,
        Tool.Square => ViewerAnnotationKind.Square,
        Tool.Circle => ViewerAnnotationKind.Circle,
        _ => null,
    };

    private static bool IsMarkupTool(Tool tool) => tool is Tool.Highlight or Tool.Underline or Tool.StrikeOut;

    private static readonly (Tool Tool, string Icon, string Label)[] ToolButtons =
    [
        (Tool.Select, "pointer", "Select text and annotations"),
        (Tool.Highlight, "highlight", "Highlight text"),
        (Tool.Underline, "underline", "Underline text"),
        (Tool.StrikeOut, "strike", "Strike through text"),
        (Tool.Note, "note", "Add a note"),
        (Tool.FreeText, "text", "Add a text box"),
        (Tool.Ink, "pen", "Draw"),
        (Tool.Square, "square", "Draw a rectangle"),
        (Tool.Circle, "circle", "Draw an ellipse"),
    ];

    private Task SaveFromButton() => SaveAsync("button");

    private ILookup<int, ViewerAnnotation> AnnotationsByPage
    {
        get
        {
            if (_store == null) return _annotsByPage;
            if (_annotsVersion != _store.Version)
            {
                _annotsVersion = _store.Version;
                _annotsByPage = _store.Current.ToLookup(a => a.Page);
            }
            return _annotsByPage;
        }
    }

    /// <summary>A new document: its annotations (when they are managed) and form fields.</summary>
    private async Task StartEditsAsync(ViewerDocument doc, bool readOnly)
    {
        _readOnly = readOnly;
        _tool = Tool.Select;
        CanvasInterop.SetTool(ToolNames[0]);
        _selectedId = _editingId = null;
        _showAnnotationToolbar = _showCommentsPanel = true;
        _editVersion = _appliedVersion = _savedVersion = 0;
        _formValues.Clear();
        _annotsVersion = -1;
        IReadOnlyList<ViewerAnnotation> fromFile = [];
        if (_config.IncludePdfAnnotations)
        {
            try
            {
                fromFile = await doc.ReadAnnotationsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                _status = $"The document's annotations could not be read ({ex.GetType().Name}).";
            }
        }
        _store = new ViewerAnnotationStore(doc.Geometry, fromFile);
        _formWidgets = CanFillForms ? await doc.GetFormWidgetsAsync() : [];
        _formWidgetsByPage = _formWidgets.ToLookup(w => w.Page);
    }

    private void StopEdits()
    {
        _store = null;
        _selectedId = _editingId = null;
        _formWidgets = [];
        _formWidgetsByPage = _formWidgets.ToLookup(w => w.Page);
        _autoSaveCts?.Cancel();
        _saving = false;
    }

    // ---------------------------------------------------------------- writing the edits

    /// <summary>Something changed: the document is written again (once at a time, until it has the latest edit).</summary>
    private async Task CommitEditsAsync()
    {
        _editVersion++;
        StateHasChanged();
        if (_applying) return;
        _applying = true;
        var doc = _doc;
        try
        {
            int attempts = 0;
            while (doc != null && _doc == doc && _store != null && _appliedVersion != _editVersion && attempts++ < 50)
            {
                int version = _editVersion;
                var store = _store;
                if (store.Current.Any(a => a.Kind == ViewerAnnotationKind.FreeText)) await EnsureAppearanceFontsAsync();
                var values = _formValues.Select(p => (p.Key, p.Value)).ToList();
                try
                {
                    await doc.ApplyEditsAsync(store.FromFile, store.Current, values);
                }
                catch (PdfFieldValidationException ex)
                {
                    // The field's own rule refused the value: it goes back to what it was.
                    _formValues.Remove(ex.FieldName);
                    if (_lastFormField != null) _formValues.Remove(_lastFormField);
                    _status = ex.Message;
                    _error = $"{ex.FieldName}: {ex.Message}";
                    continue;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException && _lastFormField != null && _formValues.ContainsKey(_lastFormField))
                {
                    _formValues.Remove(_lastFormField!);
                    _status = $"That value is not accepted ({ex.GetType().Name}).";
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Type names only, never document content.
                    _error = $"The changes could not be written into the document ({ex.GetType().Name}).";
                    _appliedVersion = version;
                    break;
                }
                _appliedVersion = version;
                if (_doc != doc) return;
                _cache.Clear();
                _drawn.Clear();
                _failed.Clear();
                _thumbsDrawn.Clear();
                if (CanFillForms)
                {
                    _formWidgets = await doc.GetFormWidgetsAsync();
                    _formWidgetsByPage = _formWidgets.ToLookup(w => w.Page);
                }
                StateHasChanged();
                _ = PumpAsync();
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
    }

    /// <summary>Waits until the document has every edit made so far.</summary>
    private async Task WaitForEditsAsync()
    {
        for (int i = 0; i < 600 && (_applying || _appliedVersion != _editVersion); i++)
        {
            if (!_applying && _appliedVersion != _editVersion) await CommitEditsAsync();
            else await Task.Delay(20);
        }
    }

    /// <summary>
    /// Free text appearances embed a font, and a browser has none installed: the bundled
    /// Liberation Sans is written to the runtime's in-memory file system once and given to the
    /// engine's appearance generator, standing in for Arial and Helvetica.
    /// </summary>
    private async Task EnsureAppearanceFontsAsync()
    {
        if (_appearanceFontsReady || !OperatingSystem.IsBrowser()) return;
        _appearanceFontsReady = true;
        const string directory = "/annotation-fonts";
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in new[] { "LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf" })
            {
                if (await FetchBundledFontAsync(file, CancellationToken.None) is { Length: > 0 } data)
                    File.WriteAllBytes(Path.Combine(directory, file), data);
            }
            string[] sans = ["Liberation Sans"];
            var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Arial"] = sans, ["Helvetica"] = sans, ["Segoe UI"] = sans,
            };
            PdfAnnotationAppearances.Fonts = SystemFontCatalog.ForDirectory(directory).WithFamilyAliases(aliases);
        }
        catch (Exception ex) when (ex is IOException or JSException or UnauthorizedAccessException)
        {
            // Free text then shows its box without text.
        }
    }

    // ---------------------------------------------------------------- changes and their events

    /// <summary>Runs a change of the reader's, then tells the host page what it added, changed and removed.</summary>
    private void Change(Action change)
    {
        if (_store == null) return;
        var before = _store.Current;
        try
        {
            change();
        }
        catch (ViewerAnnotationException ex)
        {
            _status = ex.Message;
            StateHasChanged();
            return;
        }
        if (ReferenceEquals(before, _store.Current)) return;
        EmitDifference(before, _store.Current);
        if (_selectedId != null && _store.Find(_selectedId) == null) _selectedId = null;
        _ = CommitEditsAsync();
    }

    private bool AnnotationEventsOn => Listening && _config.EnableAnnotationApis;

    private string Source => _fileId ?? _fileName;

    private void EmitAnnotation(string type, ViewerAnnotation a)
    {
        if (AnnotationEventsOn) Emit(type, w => WriteAnnotationData(w, a));
    }

    /// <summary>The event's data is the annotation's JSON object.</summary>
    private void WriteAnnotationData(Utf8JsonWriter w, ViewerAnnotation a)
    {
        // Emit writes inside the data object: copy the annotation's properties into it.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var inner = new Utf8JsonWriter(buffer)) EmbedAnnotationJson.Write(inner, a, Source);
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        foreach (var p in doc.RootElement.EnumerateObject()) p.WriteTo(w);
    }

    private void EmitDifference(IReadOnlyList<ViewerAnnotation> before, IReadOnlyList<ViewerAnnotation> after)
    {
        if (!AnnotationEventsOn) return;
        var old = before.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var now = after.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var a in before) if (!now.ContainsKey(a.Id)) EmitAnnotation(EmbedEvents.AnnotationDeleted, a);
        foreach (var a in after)
        {
            if (!old.TryGetValue(a.Id, out var was)) EmitAnnotation(EmbedEvents.AnnotationAdded, a);
            else if (!ReferenceEquals(was, a)) EmitAnnotation(EmbedEvents.AnnotationUpdated, a);
        }
    }

    // ---------------------------------------------------------------- the toolbar

    private void SetTool(Tool tool)
    {
        if (!CanEditAnnotations) return;
        // A markup tool on selected text marks it at once, as Acrobat does.
        if (IsMarkupTool(tool) && _selection is { IsEmpty: false })
        {
            MarkSelection(tool);
            return;
        }
        _tool = _tool == tool && tool != Tool.Select ? Tool.Select : tool;
        if (_tool != Tool.Select) Unselect(user: true);
        CanvasInterop.SetTool(ToolNames[(int)_tool]);
        _status = _tool switch
        {
            Tool.Select => "",
            Tool.Highlight or Tool.Underline or Tool.StrikeOut => "Drag over the text to mark it.",
            Tool.Note => "Click where the note goes.",
            Tool.FreeText => "Click or drag where the text goes.",
            Tool.Ink => "Draw on the page.",
            _ => "Drag on the page to draw the shape.",
        };
    }

    private void PickColor(string color)
    {
        if (Selected is { } a && CanEditAnnotations)
        {
            Change(() => _store!.Update(a with { Color = color, Modified = DateTimeOffset.UtcNow }));
            return;
        }
        if (_tool != Tool.Select) _toolColors[_tool] = color;
    }

    private string CurrentColor => Selected?.Color ?? (_tool == Tool.Select ? "" : ToolColor(_tool));

    private void Undo()
    {
        if (!CanEditAnnotations) return;
        _editingId = null;
        Change(() => _store!.Undo());
    }

    private void Redo()
    {
        if (!CanEditAnnotations) return;
        _editingId = null;
        Change(() => _store!.Redo());
    }

    private void DeleteSelected()
    {
        if (!CanEditAnnotations || Selected is not { } a) return;
        _editingId = null;
        Change(() => _store!.Delete(new ViewerAnnotationFilter(new HashSet<string> { a.Id })));
        _selectedId = null;
        _status = "Annotation deleted";
    }

    // ---------------------------------------------------------------- the pages (from viewer.js)

    private (ViewerPageGeometry Geometry, double Width, double Height)? PageFrame(int page) =>
        _doc != null && page >= 1 && page <= _doc.PageCount
            ? (_doc.Geometry[page - 1], _doc.Geometry[page - 1].ShownWidth, _doc.Geometry[page - 1].ShownHeight)
            : null;

    private ViewerAnnotation NewAnnotation(ViewerAnnotationKind kind, int page, ViewerBox box)
    {
        var now = DateTimeOffset.UtcNow;
        return new ViewerAnnotation
        {
            Id = ViewerAnnotationStore.NewId(),
            Page = page,
            Kind = kind,
            Box = box,
            Color = ToolColor(_tool),
            Author = _authorName,
            Created = now,
            Modified = now,
            StrokeWidth = kind switch { ViewerAnnotationKind.Ink or ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle => 2, ViewerAnnotationKind.FreeText => 0, _ => 1 },
        };
    }

    /// <summary>
    /// A tool was used on a page: <paramref name="points"/> are fractions of the shown page
    /// (x, y, x, y, …): two corners for a shape or free text, the click for a note, the stroke for ink.
    /// </summary>
    [JSInvokable]
    public void OnToolStroke(string tool, int page, double[] points)
    {
        if (!CanEditAnnotations || PageFrame(page) is not (var g, var width, var height) || points.Length < 2 || points.Length > 2 * ViewerAnnotationStore.MaxInkPointsPerStroke
            || points.Any(p => !double.IsFinite(p))) return;
        int t = Array.IndexOf(ToolNames, tool);
        if (t < 0 || (Tool)t != _tool || KindOf(_tool) is not { } kind) return;
        (double X, double Y) Shown(int i) => (Math.Clamp(points[i], 0, 1) * width, Math.Clamp(points[i + 1], 0, 1) * height);

        ViewerAnnotation a;
        switch (kind)
        {
            case ViewerAnnotationKind.Ink:
            {
                var user = new List<double>();
                (double X, double Y)? last = null;
                for (int i = 0; i + 1 < points.Length; i += 2)
                {
                    var (sx, sy) = Shown(i);
                    if (last is (var lx, var ly) && Math.Abs(sx - lx) + Math.Abs(sy - ly) < 0.75) continue;
                    last = (sx, sy);
                    var (ux, uy) = g.ToUser(sx, sy);
                    user.Add(ux);
                    user.Add(uy);
                }
                if (user.Count < 4) return;
                var ink = NewAnnotation(kind, page, default);
                a = ink with { InkList = [user.ToArray()], Box = ClampBox(ViewerBox.Around(user, ink.StrokeWidth / 2 + 1), g) };
                break;
            }
            case ViewerAnnotationKind.Note:
            {
                var (sx, sy) = Shown(0);
                var box = g.ToUser(new ViewerRect(Math.Min(sx, width - 20), Math.Min(sy, height - 20), 20, 20));
                a = NewAnnotation(kind, page, ClampBox(box, g));
                break;
            }
            default:
            {
                if (points.Length < 4) return;
                var (x0, y0) = Shown(0);
                var (x1, y1) = Shown(2);
                var rect = new ViewerRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
                if (kind == ViewerAnnotationKind.FreeText && (rect.Width < 24 || rect.Height < 12))
                    rect = new ViewerRect(Math.Min(x0, width - 200), Math.Min(y0, height - 40), 200, 40);
                else if (rect.Width < 4 || rect.Height < 4)
                    return;
                a = NewAnnotation(kind, page, ClampBox(g.ToUser(rect), g));
                break;
            }
        }
        Change(() => _store!.Add([a]));
        if (kind is ViewerAnnotationKind.Note or ViewerAnnotationKind.FreeText)
        {
            // A new note or text box: type its text at once.
            _tool = Tool.Select;
            CanvasInterop.SetTool(ToolNames[0]);
            Select(a.Id, user: true);
            StartEditing(a, isNew: true);
        }
        StateHasChanged();
    }

    private static ViewerBox ClampBox(ViewerBox b, ViewerPageGeometry g)
    {
        var m = g.MediaBox;
        return new ViewerBox(Math.Clamp(b.X1, m.Left, m.Right), Math.Clamp(b.Y1, m.Top, m.Bottom), Math.Clamp(b.X2, m.Left, m.Right), Math.Clamp(b.Y2, m.Top, m.Bottom));
    }

    /// <summary>The mouse button was released after a text selection: with a markup tool, the selection is marked.</summary>
    [JSInvokable]
    public void OnSelectEnd()
    {
        if (IsMarkupTool(_tool) && _selection is { IsEmpty: false } && CanEditAnnotations) MarkSelection(_tool);
    }

    private void MarkSelection(Tool tool)
    {
        if (_selection is not { IsEmpty: false } s || PageFrame(s.Page) is not (var g, _, _) || KindOf(tool) is not { } kind) return;
        var quads = s.Bands.SelectMany(g.ToQuad).ToArray();
        if (quads.Length == 0) return;
        var toolBefore = _tool;
        _tool = tool;
        var a = NewAnnotation(kind, s.Page, ClampBox(ViewerBox.Around(quads), g)) with { QuadPoints = quads, Contents = "" };
        _tool = toolBefore;
        Change(() => _store!.Add([a]));
        ClearSelection();
        _status = kind switch { ViewerAnnotationKind.Highlight => "Highlighted", ViewerAnnotationKind.Underline => "Underlined", _ => "Struck out" };
    }

    /// <summary>The reader pressed on an annotation (the select tool).</summary>
    [JSInvokable]
    public void OnAnnotationPointer(string id)
    {
        if (_store?.Find(id) is not { } a) return;
        if (AnnotationEventsOn) EmitAnnotation(EmbedEvents.AnnotationClicked, a);
        if (_selectedId == id && a.Kind is ViewerAnnotationKind.Note or ViewerAnnotationKind.FreeText && CanEditAnnotations && _editingId == null)
            StartEditing(a, isNew: false);
        else
            Select(id, user: true);
        StateHasChanged();
    }

    /// <summary>The reader dragged an annotation by (dx, dy), fractions of the shown page.</summary>
    [JSInvokable]
    public void OnAnnotationDrag(string id, double dx, double dy)
    {
        if (!CanEditAnnotations || _store?.Find(id) is not { } a || !double.IsFinite(dx) || !double.IsFinite(dy) || PageFrame(a.Page) is not (var g, var width, var height)) return;
        var (ux, uy) = g.ToUserDelta(Math.Clamp(dx, -1, 1) * width, Math.Clamp(dy, -1, 1) * height);
        Change(() => _store!.Move(id, ux, uy, DateTimeOffset.UtcNow));
        StateHasChanged();
    }

    private void Select(string id, bool user)
    {
        if (_selectedId == id) return;
        if (_selectedId != null) Unselect(user);
        _selectedId = id;
        if (user && _store?.Find(id) is { } a) EmitAnnotation(EmbedEvents.AnnotationSelected, a);
    }

    private void Unselect(bool user)
    {
        if (_selectedId == null) return;
        var was = _store?.Find(_selectedId);
        _selectedId = null;
        if (_editingId != null) CommitEditing();
        if (user && was != null) EmitAnnotation(EmbedEvents.AnnotationUnselected, was);
    }

    private void SelectFromPanel(ViewerAnnotation a)
    {
        Select(a.Id, user: true);
        ScrollToAnnotation(a);
    }

    private void ScrollToAnnotation(ViewerAnnotation a)
    {
        if (PageFrame(a.Page) is not (var g, _, _)) return;
        var r = g.ToShown(a.Box);
        _currentPage = a.Page;
        CanvasInterop.ScrollToPage(a.Page, r.Y * _zoom * CssPerPoint, smooth: false);
    }

    // ---------------------------------------------------------------- note and free text

    private void StartEditing(ViewerAnnotation a, bool isNew)
    {
        _editingId = a.Id;
        _editText = a.Contents;
        _editIsNew = isNew;
        _focusEditor = true;
    }

    private bool _focusEditor;

    private void CommitEditing()
    {
        if (_editingId == null || _store?.Find(_editingId) is not { } a)
        {
            _editingId = null;
            return;
        }
        _editingId = null;
        string text = _editText.Length > ViewerAnnotationStore.MaxContentsLength ? _editText[..ViewerAnnotationStore.MaxContentsLength] : _editText;
        if (_editIsNew && text.Trim().Length == 0 && a.Kind == ViewerAnnotationKind.FreeText)
        {
            // An empty new text box is not kept.
            Change(() => _store!.Delete(new ViewerAnnotationFilter(new HashSet<string> { a.Id })));
            return;
        }
        if (text != a.Contents) Change(() => _store!.Update(a with { Contents = text, Modified = DateTimeOffset.UtcNow }));
    }

    private void CancelEditing()
    {
        bool wasNew = _editIsNew;
        _editText = Selected?.Contents ?? "";
        if (wasNew) _editText = Selected?.Kind == ViewerAnnotationKind.FreeText ? "" : _editText;
        CommitEditing();
    }

    private static string KindLabel(ViewerAnnotationKind kind) => kind switch
    {
        ViewerAnnotationKind.Highlight => "Highlight",
        ViewerAnnotationKind.Underline => "Underline",
        ViewerAnnotationKind.StrikeOut => "Strike-through",
        ViewerAnnotationKind.Note => "Note",
        ViewerAnnotationKind.FreeText => "Text box",
        ViewerAnnotationKind.Ink => "Drawing",
        ViewerAnnotationKind.Square => "Rectangle",
        _ => "Ellipse",
    };

    // ---------------------------------------------------------------- the host page's annotation manager

    /// <summary>The annotation manager's calls; null for a method that is not one of them.</summary>
    private string? AnswerAnnotationCall(EmbedCall call)
    {
        int id = call.RequestId;
        if (call.Method is not (EmbedMethod.GetAnnotationManager or EmbedMethod.AddAnnotations or EmbedMethod.GetAnnotations or EmbedMethod.UpdateAnnotation
            or EmbedMethod.DeleteAnnotations or EmbedMethod.SelectAnnotation or EmbedMethod.UnselectAnnotation or EmbedMethod.SetAnnotationConfig
            or EmbedMethod.ExportXfdf or EmbedMethod.ImportXfdf)) return null;
        if (!_config.EnableAnnotationApis || _store == null)
            return EmbedProtocol.Error(id, EmbedErrors.NotEnabled, "Set enableAnnotationAPIs: true in the config of previewFile to use the annotation manager.");
        var store = _store;
        bool writes = call.Method is EmbedMethod.AddAnnotations or EmbedMethod.UpdateAnnotation or EmbedMethod.DeleteAnnotations or EmbedMethod.ImportXfdf;
        if (writes && !CanEditAnnotations)
            return EmbedProtocol.Error(id, EmbedErrors.NotAllowed, _readOnly ? "The file was opened with hasReadOnlyAccess." : "This document does not allow annotations.");
        var now = DateTimeOffset.UtcNow;
        try
        {
            switch (call.Method)
            {
                case EmbedMethod.GetAnnotationManager:
                    return EmbedProtocol.Ok(id, null);
                case EmbedMethod.GetAnnotations:
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(store.Get(call.Filter), Source));
                case EmbedMethod.AddAnnotations:
                {
                    var given = call.Annotations.Select(a => Defaults(a, now, null)).ToList();
                    var added = store.Add(given);
                    _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(added, Source));
                }
                case EmbedMethod.UpdateAnnotation:
                {
                    var given = call.Annotations[0];
                    var old = store.Find(given.Id) ?? throw new ViewerAnnotationException($"No annotation has the id \"{given.Id}\".");
                    var updated = store.Update(Defaults(given, now, old));
                    _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List([updated], Source));
                }
                case EmbedMethod.DeleteAnnotations:
                {
                    var removed = store.Delete(call.Filter ?? ViewerAnnotationFilter.All);
                    if (_selectedId != null && store.Find(_selectedId) == null) _selectedId = _editingId = null;
                    if (removed.Count > 0) _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(removed, Source));
                }
                case EmbedMethod.SelectAnnotation:
                {
                    var a = store.Find(call.AnnotationId!) ?? throw new ViewerAnnotationException($"No annotation has the id \"{call.AnnotationId}\".");
                    Select(a.Id, user: false);
                    ScrollToAnnotation(a);
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List([a], Source));
                }
                case EmbedMethod.UnselectAnnotation:
                    Unselect(user: false);
                    return EmbedProtocol.Ok(id, null);
                case EmbedMethod.SetAnnotationConfig:
                {
                    var c = call.AnnotationConfig!;
                    if (c.ShowToolbar is bool t) _showAnnotationToolbar = t;
                    if (c.ShowCommentsPanel is bool p) _showCommentsPanel = p;
                    if (c.DownloadWithAnnotations is bool d) _config = _config with { DownloadWithAnnotations = d };
                    if (c.PrintWithAnnotations is bool pr) _config = _config with { PrintWithAnnotations = pr };
                    if (c.AuthorName is { } n) _authorName = n.Trim().Length > 0 ? n.Trim() : "Guest";
                    if (!_showCommentsPanel && _panel == Panel.Comments) _panel = Panel.Outline;
                    return EmbedProtocol.Ok(id, null);
                }
                case EmbedMethod.ExportXfdf:
                {
                    string xfdf = PdfAnnotationXfdf.Export(store.Current.Select(a => ViewerAnnotationPdf.ToPdf(a, null)), _fileName);
                    return EmbedProtocol.Ok(id, w => w.WriteStringValue(xfdf));
                }
                case EmbedMethod.ImportXfdf:
                {
                    List<PdfEngine.Vector.Services.PdfAnnotation> read;
                    try
                    {
                        read = PdfAnnotationXfdf.Import(call.Xfdf!);
                    }
                    catch (FormatException ex)
                    {
                        return EmbedProtocol.Error(id, EmbedErrors.InvalidArgument, "importXfdf: " + ex.Message);
                    }
                    var ids = store.Current.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                    var list = read.Select(p => ViewerAnnotationPdf.FromPdf(p, ""))
                        .OfType<ViewerAnnotation>()
                        .Select(a => Defaults(ids.Add(a.Id) ? a : a with { Id = "" }, now, null) with { SourceIndex = -1 })
                        .ToList();
                    var added = store.Add(list);
                    if (added.Count > 0) _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(added, Source));
                }
            }
        }
        catch (ViewerAnnotationException ex)
        {
            return EmbedProtocol.Error(id, EmbedErrors.InvalidArgument, ex.Message);
        }
        return null;
    }

    /// <summary>What the host page left out: the author, the dates (a change keeps the creation date).</summary>
    private ViewerAnnotation Defaults(ViewerAnnotation a, DateTimeOffset now, ViewerAnnotation? old) => a with
    {
        Author = a.Author.Length > 0 ? a.Author : old?.Author ?? _authorName,
        Created = a.Created != default ? a.Created : old?.Created ?? now,
        Modified = a.Modified != default && old == null ? a.Modified : now,
    };
}
