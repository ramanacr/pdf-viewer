using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PdfEngine.Vector.Annotations;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;
using PdfEngine.Vector.Fonts;

namespace PdfViewer.Web.Components;

/// <summary>
/// Annotations: the annotation toolbar (highlight, underline and strike-out on a text selection,
/// note, free text, callouts, ink, rectangle, ellipse, lines, polygons, stamps; select, move (a
/// callout's box, and its anchor by its handle), delete; undo
/// and redo), the comments panel with replies and review statuses, and the host page's annotation
/// manager. The edits live in a <see cref="ViewerEditSession"/>, the editing core the Android app
/// shares, so both follow one set of rules; after every change the engine writes them into the
/// document (<see cref="ViewerEditSession.ApplyAsync"/>) and the pages are drawn again from it,
/// so what the reader sees is what a save writes.
/// </summary>
public sealed partial class Viewer
{
    private enum Tool { Select, Highlight, Underline, StrikeOut, Note, FreeText, Callout, Ink, Square, Circle, Line, Arrow, Polygon, PolyLine, Stamp, CustomStamp }

    private static readonly string[] ToolNames = ["select", "highlight", "underline", "strikeout", "note", "freetext", "callout", "ink", "square", "circle", "line", "arrow", "polygon", "polyline", "stamp", "customstamp"];

    /// <summary>What the host page is told when it opened the file with hasReadOnlyAccess and tries a change.</summary>
    private const string ReadOnlyReason = "The file was opened with hasReadOnlyAccess.";

    /// <summary>The stamp the stamp tool places (a /Name of <see cref="PdfStamps.Standard"/>).</summary>
    private string _stampName = "Approved";

    // A reply being written in the comments panel: the annotation it answers and its text.
    private string? _replyTo;
    private string _replyText = "";

    /// <summary>The colours offered in the annotation toolbar.</summary>
    private static readonly string[] Palette = ["#FFD400", "#22A06B", "#E5484D", "#2563EB", "#000000"];

    /// <summary>The reader's edits of the shown document; null while there is none, or a ranged one is not read whole.</summary>
    private ViewerEditSession? _session;
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
    private Task? _appearanceFonts;

    /// <summary>The reader or the host page may change annotations.</summary>
    private bool CanEditAnnotations => _session?.CanAnnotate == true;

    private bool ShowsAnnotationToolbar => CanEditAnnotations && _config.HasAnnotationTools && _showAnnotationToolbar;

    private bool ShowsCommentsPanel => _session != null && _showCommentsPanel && _config.HasSidePanel
                                       && (_config.HasAnnotationTools || _config.IncludePdfAnnotations);

    /// <summary>Annotations can be clicked (selected) on the pages.</summary>
    private bool AnnotationsClickable => _session != null && _tool == Tool.Select && (_config.HasAnnotationTools || _config.EnableAnnotationApis);

    private bool IsDirty => _session?.IsDirty == true;

    /// <summary>The comments panel's count: the annotations and replies (a review status is not a comment of its own).</summary>
    private int CommentCount => _session?.Store.Current.Count(a => !a.IsStatus) ?? 0;

    private bool Saving => _session?.IsSaving == true;

    private ViewerAnnotation? Selected => _selectedId == null ? null : _session?.Store.Find(_selectedId);

    private string ToolColor(Tool tool) => _toolColors.TryGetValue(tool, out var c) ? c : ViewerAnnotationPdf.DefaultColor(KindOf(tool) ?? ViewerAnnotationKind.Square);

    private static ViewerAnnotationKind? KindOf(Tool tool) => tool switch
    {
        Tool.Highlight => ViewerAnnotationKind.Highlight,
        Tool.Underline => ViewerAnnotationKind.Underline,
        Tool.StrikeOut => ViewerAnnotationKind.StrikeOut,
        Tool.Note => ViewerAnnotationKind.Note,
        Tool.FreeText or Tool.Callout => ViewerAnnotationKind.FreeText,
        Tool.Ink => ViewerAnnotationKind.Ink,
        Tool.Square => ViewerAnnotationKind.Square,
        Tool.Circle => ViewerAnnotationKind.Circle,
        Tool.Line or Tool.Arrow => ViewerAnnotationKind.Line,
        Tool.Polygon => ViewerAnnotationKind.Polygon,
        Tool.PolyLine => ViewerAnnotationKind.PolyLine,
        Tool.Stamp or Tool.CustomStamp => ViewerAnnotationKind.Stamp,
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
        (Tool.Callout, "callout", "Add a callout (click the spot it points to, then click or drag where its text goes)"),
        (Tool.Ink, "pen", "Draw"),
        (Tool.Square, "square", "Draw a rectangle"),
        (Tool.Circle, "circle", "Draw an ellipse"),
        (Tool.Line, "line", "Draw a line"),
        (Tool.Arrow, "arrow", "Draw an arrow"),
        (Tool.Polygon, "polygon", "Draw a polygon (double-click to end)"),
        (Tool.PolyLine, "polyline", "Draw connected lines (double-click to end)"),
        (Tool.Stamp, "stamp", "Place a stamp"),
    ];

    private Task SaveFromButton() => SaveAsync("button");

    private ILookup<int, ViewerAnnotation> AnnotationsByPage
    {
        get
        {
            if (_session == null) return _annotsByPage;
            if (_annotsVersion != _session.Store.Version)
            {
                _annotsVersion = _session.Store.Version;
                _annotsByPage = _session.Store.Current.ToLookup(a => a.Page);
            }
            return _annotsByPage;
        }
    }

    /// <summary>A new document: its edit session (its annotations when they are managed, and its form fields).</summary>
    private async Task StartEditsAsync(ViewerDocument doc, bool readOnly)
    {
        _tool = Tool.Select;
        CanvasInterop.SetTool(ToolNames[0]);
        _selectedId = _editingId = null;
        _showAnnotationToolbar = _showCommentsPanel = true;
        _session = null;
        _annotsVersion = -1;
        var session = await ViewerEditSession.StartAsync(doc, new ViewerEditOptions
        {
            Author = _authorName,
            ReadFileAnnotations = _config.IncludePdfAnnotations,
            FillForms = _config.EnableFormFilling,
            ReadOnlyReason = readOnly ? ReadOnlyReason : null,
        });
        if (_doc != doc) return;
        if (session.Problem is { } problem) _status = problem;
        session.Written += () => OnEditsWritten(session);
        session.Reported += result => OnEditsReported(session, result);
        _session = session;
        _formWidgets = session.Widgets;
        _formWidgetsByPage = _formWidgets.ToLookup(w => w.Page);
    }

    private void StopEdits()
    {
        _session = null;
        _selectedId = _editingId = null;
        _formWidgets = [];
        _formWidgetsByPage = _formWidgets.ToLookup(w => w.Page);
        _autoSaveCts?.Cancel();
    }

    // ---------------------------------------------------------------- writing the edits

    /// <summary>Something changed: the document is written again (once at a time, until it has the latest edit).</summary>
    private async Task CommitEditsAsync()
    {
        // A signed document: the reader is told once, at the first change, that changes are a new revision.
        if (_session?.TakeNotice() is { } notice)
        {
            _noticeIsFonts = false;
            _noticeDismissed = false;
            _notice = notice;
        }
        StateHasChanged();
        await ApplyEditsAsync();
    }

    /// <summary>Writes the edits into the document (once at a time, until it has the latest or writing fails).</summary>
    private async Task ApplyEditsAsync()
    {
        if (_session is not { } session) return;
        await PrepareFontsAsync(session);
        if (_session != session) return;
        // A refusal or failure comes through Reported, whoever ran the write.
        await session.ApplyAsync();
        if (_session != session) return;
        StateHasChanged();
    }

    /// <summary>A write of the edits refused a value (it went back to what it was) or failed: the reader is told.</summary>
    private void OnEditsReported(ViewerEditSession session, ViewerApplyResult result)
    {
        if (_session != session) return;
        if (result.Refused is { } refused)
        {
            _status = "That value is not accepted.";
            _error = refused;
        }
        if (result.Failed is { } failed)
        {
            _error = failed;
            _status = "Changes not written";
        }
        StateHasChanged();
    }

    /// <summary>The engine wrote the edits into the document: the pages and the form fields are drawn again from it.</summary>
    private void OnEditsWritten(ViewerEditSession session)
    {
        if (_session != session) return;
        _cache.Clear();
        _drawn.Clear();
        _failed.Clear();
        _thumbsDrawn.Clear();
        if (!ReferenceEquals(_formWidgets, session.Widgets))
        {
            _formWidgets = session.Widgets;
            _formWidgetsByPage = _formWidgets.ToLookup(w => w.Page);
        }
        StateHasChanged();
        _ = PumpAsync();
    }

    /// <summary>
    /// Waits until the document has every edit made so far, or writing the latest failed. Returns
    /// true when the document's bytes have every edit.
    /// </summary>
    private async Task<bool> WaitForEditsAsync()
    {
        if (_session is not { } session) return true;
        await PrepareFontsAsync(session);
        return await session.WriteAllAsync();
    }

    /// <summary>Text boxes and stamps need the appearance fonts before the engine writes or measures them.</summary>
    private Task PrepareFontsAsync(ViewerEditSession session) =>
        session.Store.Current.Any(a => a.Kind is ViewerAnnotationKind.FreeText or ViewerAnnotationKind.Stamp) ? EnsureAppearanceFontsAsync() : Task.CompletedTask;

    /// <summary>
    /// Free text appearances embed a font, and a browser has none installed: the bundled
    /// Liberation Sans is written to the runtime's in-memory file system once and given to the
    /// engine's appearance generator, standing in for Arial and Helvetica.
    /// </summary>
    private Task EnsureAppearanceFontsAsync() => _appearanceFonts ??= LoadAppearanceFontsAsync();

    private async Task LoadAppearanceFontsAsync()
    {
        if (!OperatingSystem.IsBrowser()) return;
        const string directory = "/annotation-fonts";
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in new[] { "LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf" })
            {
                if (await FetchBundledFontAsync(file, false, CancellationToken.None) is { Length: > 0 } data)
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
            // A text box then cannot be given text: the session says so.
        }
    }

    // ---------------------------------------------------------------- changes and their events

    /// <summary>
    /// Runs a change of the reader's on the session, then tells the host page what it added,
    /// changed and removed. False when the session refused it (the reason is in the status line).
    /// </summary>
    private bool Change(Action<ViewerEditSession> change)
    {
        if (_session is not { } session) return false;
        var before = session.Store.Current;
        try
        {
            change(session);
        }
        catch (ViewerAnnotationException ex)
        {
            _status = ex.Message;
            StateHasChanged();
            return false;
        }
        if (ReferenceEquals(before, session.Store.Current)) return true;
        EmitDifference(before, session.Store.Current);
        if (_selectedId != null && session.Store.Find(_selectedId) == null) _selectedId = null;
        _ = CommitEditsAsync();
        return true;
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
        // The stamp button also ends placing a custom stamp.
        bool again = _tool == tool || tool == Tool.Stamp && _tool == Tool.CustomStamp;
        _tool = again && tool != Tool.Select ? Tool.Select : tool;
        if (_tool == Tool.Stamp)
        {
            _customStamp = null;
            // The saved custom stamps join the stamp menu.
            if (CanKeepStamps) _ = RefreshSavedStampsThenRenderAsync();
        }
        if (_tool != Tool.Select) Unselect(user: true);
        // A text box or stamp needs the appearance fonts: they are read while the reader places it.
        if (_tool is Tool.FreeText or Tool.Callout or Tool.Stamp) _ = EnsureAppearanceFontsAsync();
        CanvasInterop.SetTool(ToolNames[(int)_tool]);
        _status = _tool switch
        {
            Tool.Select => "",
            Tool.Highlight or Tool.Underline or Tool.StrikeOut => "Drag over the text to mark it.",
            Tool.Note => "Click where the note goes.",
            Tool.FreeText => "Click or drag where the text goes.",
            Tool.Callout => "Click the spot the callout points to, then click or drag where its text goes. Esc starts again.",
            Tool.Ink => "Draw on the page.",
            Tool.Line or Tool.Arrow => "Drag from where the line starts to where it ends.",
            Tool.Polygon or Tool.PolyLine => "Click each point; double-click or press Enter to end, Esc to cancel.",
            Tool.Stamp => "Click where the stamp goes.",
            Tool.CustomStamp => "Click where the stamp goes, or drag its box.",
            _ => "Drag on the page to draw the shape.",
        };
    }

    private void PickColor(string color)
    {
        if (Selected is { } a && CanEditAnnotations)
        {
            // A stamp is drawn in the colour of its kind: the session leaves it.
            Change(s => s.SetColor(a.Id, color));
            return;
        }
        if (_tool != Tool.Select) _toolColors[_tool] = color;
    }

    private string CurrentColor => Selected?.Color ?? (_tool == Tool.Select ? "" : ToolColor(_tool));

    // ---------------------------------------------------------------- shape style

    private readonly Dictionary<Tool, ViewerShapeStyle> _toolStyles = new();

    private static readonly (string Value, string Label)[] PaletteNames =
        [("#FFD400", "Yellow"), ("#22A06B", "Green"), ("#E5484D", "Red"), ("#2563EB", "Blue"), ("#000000", "Black")];

    /// <summary>The line endings (/LE, ISO 32000-2 Table 179) offered for lines and connected lines.</summary>
    private static readonly (string Name, string Label)[] EndingNames =
    [
        ("None", "None"), ("OpenArrow", "Open arrow"), ("ClosedArrow", "Closed arrow"), ("Circle", "Circle"), ("Square", "Square"),
        ("Diamond", "Diamond"), ("Butt", "Bar"), ("ROpenArrow", "Reverse open arrow"), ("RClosedArrow", "Reverse closed arrow"), ("Slash", "Slash"),
    ];

    private static bool IsShapeTool(Tool tool) => tool is Tool.Square or Tool.Circle or Tool.Line or Tool.Arrow or Tool.Polygon or Tool.PolyLine;

    private ViewerShapeStyle ToolStyle(Tool tool) =>
        _toolStyles.TryGetValue(tool, out var s) ? s : new ViewerShapeStyle(End: tool == Tool.Arrow ? "OpenArrow" : "None");

    /// <summary>
    /// The style the shape style controls show and change: the selected shape's, else the shape
    /// tool's; null hides them (another kind, or a measurement).
    /// </summary>
    private ViewerShapeStyle? CurrentShapeStyle =>
        Selected is { } a ? (a.HasShapeStyle ? ViewerShapeStyle.Of(a) : null)
        : IsShapeTool(_tool) ? ToolStyle(_tool) : null;

    /// <summary>The selected shape, or the shape tool, has line endings.</summary>
    private bool CurrentHasEndings => Selected is { } a ? a.HasEndings : _tool is Tool.Line or Tool.Arrow or Tool.PolyLine;

    /// <summary>Changes the selected shape's style (one undo step), else the shape tool's.</summary>
    private void SetShapeStyle(Func<ViewerShapeStyle, ViewerShapeStyle> change)
    {
        if (Selected is { } a)
        {
            if (!a.HasShapeStyle || !CanEditAnnotations) return;
            Change(s => s.SetShapeStyle(a.Id, change(ViewerShapeStyle.Of(a))));
            return;
        }
        if (IsShapeTool(_tool)) _toolStyles[_tool] = change(ToolStyle(_tool));
    }

    private static string FillValue(string? fill) =>
        fill == null ? "none" : PaletteNames.FirstOrDefault(p => string.Equals(p.Value, fill, StringComparison.OrdinalIgnoreCase)).Value ?? "current";

    private static string DashValue(IReadOnlyList<double>? dash) =>
        dash == null ? "solid" : ViewerAnnotation.SameDash(dash, ViewerAnnotation.Dashed) ? "dashed"
        : ViewerAnnotation.SameDash(dash, ViewerAnnotation.Dotted) ? "dotted" : "current";

    private void PickFill(ChangeEventArgs e)
    {
        string? v = e.Value as string;
        if (v == "none") SetShapeStyle(s => s with { Fill = null });
        else if (PaletteNames.Any(p => p.Value == v)) SetShapeStyle(s => s with { Fill = v });
    }

    private void PickDash(ChangeEventArgs e)
    {
        IReadOnlyList<double>? dash;
        switch (e.Value as string)
        {
            case "solid": dash = null; break;
            case "dashed": dash = ViewerAnnotation.Dashed; break;
            case "dotted": dash = ViewerAnnotation.Dotted; break;
            default: return;
        }
        SetShapeStyle(s => s with { Dash = dash });
    }

    private void PickEnding(bool start, ChangeEventArgs e)
    {
        if (e.Value is not string name || !PdfAnnotationLimits.LineEndings.Contains(name)) return;
        SetShapeStyle(s => start ? s with { Start = name } : s with { End = name });
    }

    private void Undo()
    {
        if (!CanEditAnnotations) return;
        _editingId = null;
        Change(s => s.Undo());
    }

    private void Redo()
    {
        if (!CanEditAnnotations) return;
        _editingId = null;
        Change(s => s.Redo());
    }

    private void DeleteSelected()
    {
        if (!CanEditAnnotations || Selected is not { } a) return;
        _editingId = null;
        if (Change(s => s.Delete(a.Id))) _status = "Annotation deleted";
        _selectedId = null;
    }

    // ---------------------------------------------------------------- the pages (from viewer.js)

    private (ViewerPageGeometry Geometry, double Width, double Height)? PageFrame(int page) =>
        _doc != null && page >= 1 && page <= _doc.PageCount
            ? (_doc.Geometry[page - 1], _doc.Geometry[page - 1].ShownWidth, _doc.Geometry[page - 1].ShownHeight)
            : null;

    /// <summary>
    /// A tool was used on a page: <paramref name="points"/> are fractions of the shown page
    /// (x, y, x, y, …): two corners for a shape or free text, the click for a note or stamp, the
    /// stroke for ink, the points of a line, polygon or connected lines. The session makes the annotation.
    /// </summary>
    [JSInvokable]
    public void OnToolStroke(string tool, int page, double[] points)
    {
        if (!CanEditAnnotations || PageFrame(page) is not (_, var width, var height) || points.Length < 2 || points.Length > 2 * ViewerAnnotationStore.MaxInkPointsPerStroke
            || points.Any(p => !double.IsFinite(p))) return;
        int t = Array.IndexOf(ToolNames, tool);
        if (t < 0 || (Tool)t != _tool || KindOf(_tool) is not { } kind) return;
        var shown = new List<ViewerPoint>(points.Length / 2);
        for (int i = 0; i + 1 < points.Length; i += 2)
            shown.Add(new ViewerPoint(Math.Clamp(points[i], 0, 1) * width, Math.Clamp(points[i + 1], 0, 1) * height));
        var first = shown[0];
        var last = shown[^1];
        string color = ToolColor(_tool);
        var style = ToolStyle(_tool);

        if (_tool == Tool.Callout)
        {
            // The spot it points to, then the text box's two corners (the same point for a click).
            if (shown.Count < 3) return;
            var (c1, c2) = (shown[1], shown[^1]);
            var box = new ViewerRect(Math.Min(c1.X, c2.X), Math.Min(c1.Y, c2.Y), Math.Abs(c2.X - c1.X), Math.Abs(c2.Y - c1.Y));
            ViewerAnnotation? callout = null;
            if (Change(s => callout = s.AddCallout(page, first, box, "", color)) && callout != null)
            {
                // Type its text at once, as for a text box; an empty one is not kept.
                _tool = Tool.Select;
                CanvasInterop.SetTool(ToolNames[0]);
                Select(callout.Id, user: true);
                StartEditing(callout, isNew: true);
            }
            StateHasChanged();
            return;
        }

        ViewerAnnotation? a = null;
        bool changed = Change(s => a = kind switch
        {
            ViewerAnnotationKind.Ink => s.AddInk(page, shown, color),
            ViewerAnnotationKind.Note => s.AddNote(page, first, ""),
            ViewerAnnotationKind.FreeText => s.AddTextBox(page, first, last, color),
            ViewerAnnotationKind.Stamp when _tool == Tool.CustomStamp && _customStamp is { } custom =>
                s.AddCustomStamp(page, new ViewerRect(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Abs(last.X - first.X), Math.Abs(last.Y - first.Y)), custom),
            ViewerAnnotationKind.Stamp => s.AddStamp(page, first, PdfStamps.Standard.FirstOrDefault(st => st.Name == _stampName) ?? PdfStamps.Standard[0]),
            ViewerAnnotationKind.Line => s.AddLine(page, first, last, style, color),
            ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine => s.AddPolyline(kind, page, shown, style, color),
            ViewerAnnotationKind.Square or ViewerAnnotationKind.Circle => s.AddShape(kind, page, first, last, color, style),
            _ => null,
        });
        if (changed && a != null && kind is ViewerAnnotationKind.Note or ViewerAnnotationKind.FreeText)
        {
            // A new note or text box: type its text at once.
            _tool = Tool.Select;
            CanvasInterop.SetTool(ToolNames[0]);
            Select(a.Id, user: true);
            StartEditing(a, isNew: true);
        }
        StateHasChanged();
    }

    /// <summary>The mouse button was released after a text selection: with a markup tool, the selection is marked.</summary>
    [JSInvokable]
    public void OnSelectEnd()
    {
        if (IsMarkupTool(_tool) && _selection is { IsEmpty: false } && CanEditAnnotations) MarkSelection(_tool);
    }

    private void MarkSelection(Tool tool)
    {
        if (_selection is not { IsEmpty: false } selection || KindOf(tool) is not { } kind) return;
        ViewerAnnotation? a = null;
        if (!Change(s => a = s.MarkText(kind, selection, ToolColor(tool))) || a == null) return;
        ClearSelection();
        _status = kind switch { ViewerAnnotationKind.Highlight => "Highlighted", ViewerAnnotationKind.Underline => "Underlined", _ => "Struck out" };
    }

    /// <summary>The reader pressed on an annotation (the select tool).</summary>
    [JSInvokable]
    public void OnAnnotationPointer(string id)
    {
        if (_session?.Store.Find(id) is not { } a) return;
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
        if (!CanEditAnnotations || _session?.Store.Find(id) is not { } a || !double.IsFinite(dx) || !double.IsFinite(dy) || PageFrame(a.Page) is not (_, var width, var height)) return;
        Change(s => s.Move(id, Math.Clamp(dx, -1, 1) * width, Math.Clamp(dy, -1, 1) * height));
        StateHasChanged();
    }

    /// <summary>The reader dragged a callout's anchor handle to (x, y), fractions of the shown page: one undo step.</summary>
    [JSInvokable]
    public void OnCalloutAnchorDrag(string id, double x, double y)
    {
        if (!CanEditAnnotations || _session?.Store.Find(id) is not { IsCallout: true } a || !double.IsFinite(x) || !double.IsFinite(y)
            || PageFrame(a.Page) is not (_, var width, var height)) return;
        Change(s => s.MoveCalloutAnchor(id, new ViewerPoint(Math.Clamp(x, 0, 1) * width, Math.Clamp(y, 0, 1) * height)));
        StateHasChanged();
    }

    private void Select(string id, bool user)
    {
        if (_selectedId == id) return;
        if (_selectedId != null) Unselect(user);
        _selectedId = id;
        if (user && _session?.Store.Find(id) is { } a) EmitAnnotation(EmbedEvents.AnnotationSelected, a);
    }

    private void Unselect(bool user)
    {
        if (_selectedId == null) return;
        var was = _session?.Store.Find(_selectedId);
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
        if (a.Kind == ViewerAnnotationKind.FreeText) _ = EnsureAppearanceFontsAsync();
    }

    private bool _focusEditor;

    private void CommitEditing()
    {
        if (_editingId == null || _session?.Store.Find(_editingId) is not { } a)
        {
            _editingId = null;
            return;
        }
        _editingId = null;
        string text = _editText;
        bool isNew = _editIsNew;
        if (a.Kind == ViewerAnnotationKind.FreeText)
        {
            // An empty new text box is not kept; a text box with text needs the fonts it is measured and drawn with.
            if (isNew && text.Trim().Length == 0) Change(s => s.Delete(a.Id));
            else _ = CommitTextBoxAsync(a.Id, text, isNew);
            return;
        }
        Change(s => s.SetContents(a.Id, text));
    }

    private async Task CommitTextBoxAsync(string id, string text, bool isNew)
    {
        var session = _session;
        await EnsureAppearanceFontsAsync();
        if (_session != session || session?.Store.Find(id) == null) return;
        // The reader may have moved on to another annotation meanwhile: then the editor stays closed.
        if (!Change(s => s.SetContents(id, text)) && (_selectedId == null || _selectedId == id) && _editingId == null)
        {
            // Refused (a character without a font, or no text): the reader can correct it.
            _selectedId = id;
            _editingId = id;
            _editText = text;
            _editIsNew = isNew;
            _focusEditor = true;
        }
        StateHasChanged();
    }

    private void CancelEditing()
    {
        bool wasNew = _editIsNew;
        _editText = Selected?.Contents ?? "";
        if (wasNew) _editText = Selected?.Kind == ViewerAnnotationKind.FreeText ? "" : _editText;
        CommitEditing();
    }

    // ---------------------------------------------------------------- comment threads

    private void StartReply(ViewerAnnotation a)
    {
        if (!CanEditAnnotations) return;
        _replyTo = a.Id;
        _replyText = "";
    }

    private void SendReply()
    {
        if (_replyTo is not { } parent || !CanEditAnnotations) return;
        string text = _replyText;
        _replyTo = null;
        _replyText = "";
        ViewerAnnotation? reply = null;
        // The status says so only when the session took it; a refusal leaves Change's message.
        if (Change(s => reply = s.AddReply(parent, text)) && reply != null) _status = "Reply added";
    }

    /// <summary>The reader set a comment's review status (Acrobat's Review model; "None" clears it).</summary>
    private void SetReviewState(ViewerAnnotation a, ChangeEventArgs e)
    {
        if (!CanEditAnnotations || e.Value is not string state) return;
        ViewerAnnotation? status = null;
        if (Change(s => status = s.SetReviewState(a.Id, state)) && status != null)
            _status = state == "None" ? "Status cleared" : $"Status: {state}";
    }

    // ---------------------------------------------------------------- the host page's annotation manager

    /// <summary>The annotation manager's calls; null for a method that is not one of them.</summary>
    private string? AnswerAnnotationCall(EmbedCall call)
    {
        int id = call.RequestId;
        if (call.Method is not (EmbedMethod.GetAnnotationManager or EmbedMethod.AddAnnotations or EmbedMethod.GetAnnotations or EmbedMethod.UpdateAnnotation
            or EmbedMethod.DeleteAnnotations or EmbedMethod.SelectAnnotation or EmbedMethod.UnselectAnnotation or EmbedMethod.SetAnnotationConfig
            or EmbedMethod.ExportXfdf or EmbedMethod.ImportXfdf or EmbedMethod.AddCustomStamp)) return null;
        if (!_config.EnableAnnotationApis || _session is not { } session)
            return EmbedProtocol.Error(id, EmbedErrors.NotEnabled, "Set enableAnnotationAPIs: true in the config of previewFile to use the annotation manager.");
        bool writes = call.Method is EmbedMethod.AddAnnotations or EmbedMethod.UpdateAnnotation or EmbedMethod.DeleteAnnotations or EmbedMethod.ImportXfdf
            or EmbedMethod.AddCustomStamp;
        if (writes && session.AnnotateRestriction is { } restriction)
            return EmbedProtocol.Error(id, EmbedErrors.NotAllowed, restriction);
        try
        {
            switch (call.Method)
            {
                case EmbedMethod.GetAnnotationManager:
                    return EmbedProtocol.Ok(id, null);
                case EmbedMethod.GetAnnotations:
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(session.Store.Get(call.Filter), Source, call.IncludeStampData));
                case EmbedMethod.AddCustomStamp:
                {
                    var added = AddCustomStampFromHost(session, call);
                    _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List([added], Source));
                }
                case EmbedMethod.AddAnnotations:
                {
                    var added = session.AddAnnotations(call.Annotations);
                    if (added.Count > 0) _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(added, Source));
                }
                case EmbedMethod.UpdateAnnotation:
                {
                    // No dashPattern property: the dash stays (null or [] makes the line solid).
                    var updated = session.UpdateAnnotation(call.Annotations[0], keepDash: call.KeepsDash);
                    _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List([updated], Source));
                }
                case EmbedMethod.DeleteAnnotations:
                {
                    var removed = session.DeleteAnnotations(call.Filter ?? ViewerAnnotationFilter.All);
                    if (_selectedId != null && session.Store.Find(_selectedId) == null) _selectedId = _editingId = null;
                    if (removed.Count > 0) _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(removed, Source));
                }
                case EmbedMethod.SelectAnnotation:
                {
                    var a = session.Store.Find(call.AnnotationId!) ?? throw new ViewerAnnotationException($"No annotation has the id \"{call.AnnotationId}\".");
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
                    if (c.AuthorName is { } n)
                    {
                        _authorName = n.Trim().Length > 0 ? n.Trim() : "Guest";
                        session.Author = _authorName;
                    }
                    if (!_showCommentsPanel && _panel == Panel.Comments) _panel = Panel.Outline;
                    return EmbedProtocol.Ok(id, null);
                }
                case EmbedMethod.ExportXfdf:
                {
                    string xfdf = session.ExportXfdf(_fileName);
                    return EmbedProtocol.Ok(id, w => w.WriteStringValue(xfdf));
                }
                case EmbedMethod.ImportXfdf:
                {
                    // Names in use get new ids; replies are linked to their parents, or added on their own.
                    ViewerXfdfImport import;
                    try
                    {
                        import = session.ImportXfdf(call.Xfdf!);
                    }
                    catch (ViewerAnnotationException ex)
                    {
                        return EmbedProtocol.Error(id, EmbedErrors.InvalidArgument, "importXfdf: " + ex.Message);
                    }
                    if (import.Skipped > 0)
                    {
                        _status = $"{import.Skipped} XFDF {(import.Skipped == 1 ? "annotation was" : "annotations were")} skipped (unreadable, off the document's pages, or larger than the limits).";
                        StateHasChanged();
                    }
                    if (import.Added > 0) _ = CommitEditsAsync();
                    return EmbedProtocol.Ok(id, EmbedAnnotationJson.List(import.Annotations, Source));
                }
            }
        }
        catch (ViewerAnnotationException ex)
        {
            return EmbedProtocol.Error(id, EmbedErrors.InvalidArgument, ex.Message);
        }
        return null;
    }
}
