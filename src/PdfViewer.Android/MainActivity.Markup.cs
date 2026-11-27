using Android.App;
using Android.Views;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// Shapes beyond a drag, as the web viewer makes them: polygons and connected lines (a tap for each
/// point, with a rubber band from the last one while the finger is down; Done or a double tap
/// finishes, a polygon with at least three points and connected lines with two), the style of the
/// chosen shape (fill, line style and, for lines and connected lines, the endings at either end:
/// <see cref="ViewerEditSession.SetShapeStyle"/>, one undo step), and dragging the chosen
/// annotation: it moves (<see cref="ViewerEditSession.Move"/>), a corner handle resizes a rectangle,
/// ellipse, text box or stamp (<see cref="ViewerEditSession.UpdateAnnotation"/> with the new box),
/// and a point handle moves one point of a line, polygon or connected lines
/// (<see cref="ViewerEditSession.MoveVertex"/>). Each is one undo step. The points of the shape being
/// placed are shared with measuring (MainActivity.Measure.cs).
/// </summary>
public sealed partial class MainActivity
{
    private TextView _styleButton = null!, _redactSelectionButton = null!;

    // The points being placed (polygon, connected lines, measuring), on one page, in shown-page points.
    private readonly List<ViewerPoint> _pathPoints = new();
    private int _pathPage;
    private ViewerPoint? _pathAim;

    private LinearLayout _pathBar = null!;
    private TextView _pathText = null!, _pathUndoButton = null!, _pathDoneButton = null!, _pathCancelButton = null!;

    private IEnumerable<TextView> PathButtons => [_pathUndoButton, _pathDoneButton, _pathCancelButton];

    /// <summary>The smallest side of a resized box, in points (a smaller one is too small to grab again).</summary>
    private const double MinResize = 8;

    /// <summary>The line endings (/LE, ISO 32000-2 Table 179) offered for lines and connected lines, as the desktop viewer names them.</summary>
    private static readonly (string Name, string Label)[] Endings =
    [
        ("None", "None"), ("OpenArrow", "Open arrow"), ("ClosedArrow", "Closed arrow"), ("Circle", "Circle"), ("Square", "Square"),
        ("Diamond", "Diamond"), ("Butt", "Bar"), ("ROpenArrow", "Reversed open arrow"), ("RClosedArrow", "Reversed closed arrow"), ("Slash", "Slash"),
    ];

    /// <summary>An ending with an inside, which a line's fill colours (/IC); a line without one has no fill to choose.</summary>
    private static bool Closed(string ending) => ending is "ClosedArrow" or "RClosedArrow" or "Circle" or "Square" or "Diamond";

    private void BuildStyleButton() =>
        _styleButton = ToolButton("Style", "Style of the chosen shape: fill, line style and line endings", (_, _) => ChooseStyle());

    /// <summary>The bar shown in place of the tools once a point is placed: what is placed so far, undo point, Done and Cancel.</summary>
    private LinearLayout BuildPathBar()
    {
        _pathBar = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal, Visibility = ViewStates.Gone };
        _pathBar.SetGravity(GravityFlags.CenterVertical);
        _pathText = new TextView(this);
        _pathText.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        _pathText.SetPadding((int)Dp(8), 0, (int)Dp(4), 0);
        _pathText.SetMaxLines(2);
        _pathText.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        // TalkBack reads the count or the result as it changes.
        _pathText.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        _pathUndoButton = ToolButton("↶", "Take back the last point", (_, _) => UndoPathPoint());
        _pathDoneButton = ToolButton("Done", "Finish", (_, _) => FinishPath());
        _pathCancelButton = ToolButton("✕", "Cancel these points", (_, _) => ClearPath());
        _pathBar.AddView(_pathText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        foreach (var b in PathButtons) _pathBar.AddView(b);
        return _pathBar;
    }

    private void ConnectMarkup()
    {
        _pages.PathPointPlaced += OnPathPoint;
        _pages.PathAimed += (page, aim) =>
        {
            _pathAim = aim;
            if (_pages.Tool == PageView.PageTool.Measure) UpdatePath();
        };
        _pages.PathFinished += FinishPath;
        // Back first drops the points being placed.
        _backHandlers.Add(() =>
        {
            if (_pathPoints.Count == 0) return false;
            ClearPath();
            return true;
        });
        _pages.AnnotationDragged += (id, dx, dy) =>
        {
            Change(s => s.Move(id, dx, dy));
            if (_session?.Store.Find(id) != null) Pick(id);
        };
        _pages.VertexDragged += (id, index, to) => Change(s => s.MoveVertex(id, index, to));
        _pages.AnnotationResized += (id, box) => Change(s => Resize(s, id, box));
    }

    // ------------------------------------------------------------------ points

    /// <summary>What a points tool asks for, as a short hint.</summary>
    private string PathHint() => _pages.Tool switch
    {
        PageView.PageTool.Polygon => "Tap each corner of the polygon; Done or a double tap finishes.",
        PageView.PageTool.PolyLine => "Tap each point of the lines; Done or a double tap finishes.",
        _ => MeasureHint(),
    };

    private void OnPathPoint(int page, ViewerPoint point)
    {
        if (!PageView.TakesPoints(_pages.Tool)) return;
        if (_pathPoints.Count > 0 && page != _pathPage)
        {
            Toast.MakeText(this, "Place every point on the same page.", ToastLength.Short)?.Show();
            return;
        }
        if (_pathPoints.Count >= ViewerAnnotationStore.MaxVertices)
        {
            Toast.MakeText(this, "That is as many points as a shape can have.", ToastLength.Short)?.Show();
            return;
        }
        if (_pathPoints.Count == 0)
        {
            _pathPage = page;
            if (_pages.Tool == PageView.PageTool.Measure) StartMeasurement(page, point);
        }
        _pathPoints.Add(point);
        UpdatePath();
        // A distance is done at its second point.
        if (_pages.Tool == PageView.PageTool.Measure && MeasureDone()) FinishPath();
    }

    private void UndoPathPoint()
    {
        if (_pathPoints.Count == 0) return;
        _pathPoints.RemoveAt(_pathPoints.Count - 1);
        UpdatePath();
    }

    /// <summary>The points drop (cancelled, finished, another tool, the bar closed, another document); the tools come back.</summary>
    private void ClearPath()
    {
        _pathPoints.Clear();
        _pathAim = null;
        _pages.PathPreview = null;
        if (_pathBar == null) return;
        _pathBar.Visibility = ViewStates.Gone;
        if (_placeHint?.Visibility != ViewStates.Visible) _annotateStrip.Visibility = ViewStates.Visible;
    }

    /// <summary>The preview over the page and the bar: once a point is placed it replaces the tools.</summary>
    private void UpdatePath()
    {
        bool placing = _pathPoints.Count > 0;
        bool closed = _pages.Tool == PageView.PageTool.Polygon || _pages.Tool == PageView.PageTool.Measure && IsAreaMeasure;
        _pages.PathPreview = placing ? (_pathPage, _pathPoints.ToList(), closed) : null;
        _pathBar.Visibility = placing ? ViewStates.Visible : ViewStates.Gone;
        _annotateStrip.Visibility = placing ? ViewStates.Gone : ViewStates.Visible;
        if (!placing) return;
        int needed = _pages.Tool == PageView.PageTool.Polygon ? 3 : 2;
        if (_pages.Tool == PageView.PageTool.Measure)
        {
            _pathText.Text = MeasureReadout();
            needed = IsAreaMeasure ? 3 : 2;
        }
        else
        {
            int n = _pathPoints.Count;
            _pathText.Text = n < needed ? $"{n} of at least {needed} points" : $"{n} points";
        }
        bool enough = _pathPoints.Count >= needed;
        _pathDoneButton.Enabled = enough;
        _pathDoneButton.Alpha = enough ? 1f : 0.4f;
    }

    /// <summary>Done or a double tap: the polygon or connected lines are added (one undo step), or the measurement is shown.</summary>
    private void FinishPath()
    {
        var tool = _pages.Tool;
        if (tool == PageView.PageTool.Measure)
        {
            FinishMeasurement();
            return;
        }
        if (tool is not (PageView.PageTool.Polygon or PageView.PageTool.PolyLine)) return;
        var kind = tool == PageView.PageTool.Polygon ? ViewerAnnotationKind.Polygon : ViewerAnnotationKind.PolyLine;
        int needed = kind == ViewerAnnotationKind.Polygon ? 3 : 2;
        if (_pathPoints.Count < needed)
        {
            Toast.MakeText(this, kind == ViewerAnnotationKind.Polygon ? "A polygon needs at least three points." : "Connected lines need at least two points.", ToastLength.Short)?.Show();
            return;
        }
        int page = _pathPage;
        var points = _pathPoints.ToList();
        ClearPath();
        // The tool stays on for the next shape.
        Change(s => s.AddPolyline(kind, page, points, ViewerShapeStyle.Plain, KeptColour(kind)) != null);
    }

    // ------------------------------------------------------------------ resizing

    /// <summary>The annotation in the box dragged to (shown-page points, kept on the page); false when it did not change.</summary>
    private bool Resize(ViewerEditSession session, string id, ViewerRect box)
    {
        if (session.Store.Find(id) is not { } a || !PageView.Resizable(a)) return false;
        var g = session.Document.Geometry[a.Page - 1];
        double x1 = Math.Clamp(box.X, 0, g.ShownWidth), y1 = Math.Clamp(box.Y, 0, g.ShownHeight);
        double x2 = Math.Clamp(box.X + box.Width, 0, g.ShownWidth), y2 = Math.Clamp(box.Y + box.Height, 0, g.ShownHeight);
        if (x2 - x1 < MinResize || y2 - y1 < MinResize)
        {
            Toast.MakeText(this, "That is too small.", ToastLength.Short)?.Show();
            return false;
        }
        var user = g.ToUser(new ViewerRect(x1, y1, x2 - x1, y2 - y1));
        if (user == a.Box) return false;
        // A callout keeps the spot it points to: its line is laid out again to the new box.
        session.UpdateAnnotation(a with { Box = user });
        return true;
    }

    // ------------------------------------------------------------------ shape style

    /// <summary>
    /// "Style": the chosen shape's fill (none or a palette colour; for a line or connected lines only
    /// with a closed ending, whose inside it colours), line style (solid, dashed, dotted) and, for a
    /// line or connected lines, the endings at its start and end. OK changes them as one undo step.
    /// </summary>
    private void ChooseStyle()
    {
        if (!CanAnnotate || _pages.PickedAnnotation is not { } id || _session?.Store.Find(id) is not { HasShapeStyle: true } a) return;
        var now = ViewerShapeStyle.Of(a);
        var builder = new AlertDialog.Builder(this);
        var context = builder.Context ?? this;
        var column = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(24), (int)Dp(8), (int)Dp(24), 0);

        Spinner Row(string label, IList<string> items, int selected, out LinearLayout row)
        {
            row = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight((int)Dp(56));
            var text = new TextView(context) { Text = label };
            text.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
            text.SetMinWidth((int)Dp(96));
            var spinner = new Spinner(context) { ContentDescription = label };
            var adapter = new ArrayAdapter<string>(context, global::Android.Resource.Layout.SimpleSpinnerItem, items);
            adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            spinner.Adapter = adapter;
            spinner.SetSelection(Math.Max(0, selected));
            spinner.SetMinimumHeight((int)Dp(48));
            row.AddView(text);
            row.AddView(spinner, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            column.AddView(row);
            return spinner;
        }

        // Fill: none, the palette, and a file's own colour when it is none of them.
        var fills = new List<(string Label, string? Value)> { ("None", null) };
        fills.AddRange(ViewerToolColors.Palette.Select(p => (p.Name, (string?)p.Value)));
        int fillIndex = fills.FindIndex(f => string.Equals(f.Value, now.Fill, StringComparison.OrdinalIgnoreCase));
        if (fillIndex < 0)
        {
            fills.Add(("As it is", now.Fill));
            fillIndex = fills.Count - 1;
        }
        var fill = Row("Fill", fills.Select(f => f.Label).ToList(), fillIndex, out var fillRow);

        // Line style: solid, dashed, dotted, and a file's own dash when it is neither.
        var dashes = new List<(string Label, IReadOnlyList<double>? Value)> { ("Solid", null), ("Dashed", ViewerAnnotation.Dashed), ("Dotted", ViewerAnnotation.Dotted) };
        int dashIndex = dashes.FindIndex(d => ViewerAnnotation.SameDash(d.Value, now.Dash));
        if (dashIndex < 0)
        {
            dashes.Add(("As it is", now.Dash));
            dashIndex = dashes.Count - 1;
        }
        var dash = Row("Line", dashes.Select(d => d.Label).ToList(), dashIndex, out _);

        Spinner? start = null, end = null;
        var labels = Endings.Select(e => e.Label).ToList();
        if (a.HasEndings)
        {
            start = Row("Start", labels, Array.FindIndex(Endings, e => e.Name == now.Start), out _);
            end = Row("End", labels, Array.FindIndex(Endings, e => e.Name == now.End), out _);
        }
        string Ending(Spinner? s, string was) => s == null || s.SelectedItemPosition < 0 ? was : Endings[s.SelectedItemPosition].Name;
        bool FillApplies() => !a.HasEndings || Closed(Ending(start, now.Start)) || Closed(Ending(end, now.End));
        void ShowFill() => fillRow.Visibility = FillApplies() ? ViewStates.Visible : ViewStates.Gone;
        ShowFill();
        if (start != null) start.ItemSelected += (_, _) => ShowFill();
        if (end != null) end.ItemSelected += (_, _) => ShowFill();

        var scroll = new ScrollView(context);
        scroll.AddView(column);
        builder.SetTitle($"Style of this {ViewerEditSession.Label(a).ToLowerInvariant()}")!
            .SetView(scroll)!
            .SetPositiveButton("OK", (_, _) =>
            {
                // A fill that does not apply (a line without a closed ending) stays as it was.
                string? chosenFill = FillApplies() ? fills[Math.Max(0, fill.SelectedItemPosition)].Value : now.Fill;
                var style = new ViewerShapeStyle(chosenFill, dashes[Math.Max(0, dash.SelectedItemPosition)].Value, Ending(start, now.Start), Ending(end, now.End));
                Change(s => s.SetShapeStyle(id, style));
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }
}
