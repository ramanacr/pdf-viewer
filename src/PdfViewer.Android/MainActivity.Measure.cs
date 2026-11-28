using System.Globalization;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// Measuring, as the desktop viewer's Tools → Measure: a distance (two points), a perimeter (two or
/// more) or an area (three or more), placed with taps as a polygon is, the result shown live while
/// the finger aims. The scale is the page's own where a viewport (/VP with /Measure) covers the
/// first point, otherwise the reader's ("1 in = 10 ft"), kept in app-private preferences
/// (<c>measure-scale</c>; the document is not named). Points snap to the ends, middles and lines of
/// the page's drawing within 12 dp, with a marker where they snap (<see cref="ViewerMeasureSnap"/>,
/// the desktop's snapping); "Snap to drawing" in the Measure list turns it off, as Shift does on the
/// desktop, and the choice is kept with the scale. The results are the engine's
/// (<see cref="PdfMeasure"/>, <see cref="PdfMeasurements.Label"/>). Add to page writes a measurement
/// annotation through the shared session (<see cref="ViewerEditSession.AddMeasurement"/>), one undo step.
/// </summary>
public sealed partial class MainActivity
{
    private const string MeasurePreferences = "measure-scale";

    /// <summary>The colour measurements are drawn in (the engine's measurement blue).</summary>
    private const string MeasureColour = "#0033CC";

    private PdfMeasurementKind _measureKind = PdfMeasurementKind.Distance;
    private PdfMeasure? _measure;
    private bool _measureFromPage;

    // The pages' viewports (/VP), read once per document on a worker thread.
    private ViewerDocument? _viewportsFor;
    private IReadOnlyList<IReadOnlyList<PdfViewport>>? _viewports;

    private bool IsAreaMeasure => _measureKind == PdfMeasurementKind.Area;

    private TextView MeasureToolButton() =>
        ToolButton("Measure", "Measure a distance, perimeter or area, and set the scale", (_, _) => ChooseMeasure());

    private string MeasureHint() => _measureKind switch
    {
        PdfMeasurementKind.Distance => "Tap the two ends of the distance.",
        PdfMeasurementKind.Perimeter => "Tap each point of the perimeter; Done or a double tap finishes.",
        _ => "Tap each corner of the area; Done or a double tap finishes.",
    };

    /// <summary>
    /// Tools → Measure: the annotate bar with Measure, then what to measure. A document that does not
    /// allow annotations can still be measured; only Add to page needs that permission.
    /// </summary>
    private void MeasureFromTools()
    {
        if (!NeedDocument()) return;
        if (_annotateBar.Visibility != ViewStates.Visible) ShowAnnotateBar(true, measureOnly: !CanAnnotate);
        if (_annotateBar.Visibility != ViewStates.Visible) return;
        if (_pages.Tool != PageView.PageTool.Measure) ChooseMeasure();
    }

    /// <summary>The Measure button: what to measure (or the scale); again with measuring on, it stops.</summary>
    private void ChooseMeasure()
    {
        if (_pages.Tool == PageView.PageTool.Measure)
        {
            SetTool(PageView.PageTool.Measure);
            return;
        }
        string[] items = ["Distance", "Perimeter", "Area", $"Scale: {UserScaleText()}…", $"Snap to drawing: {(SnapToDrawing ? "On" : "Off")}"];
        new AlertDialog.Builder(this)
            .SetTitle("Measure")!
            .SetItems(items, (_, e) =>
            {
                if (e.Which == 3)
                {
                    AskScale(ChooseMeasure);
                    return;
                }
                if (e.Which == 4)
                {
                    SetSnapToDrawing(!SnapToDrawing);
                    ChooseMeasure();
                    return;
                }
                _measureKind = e.Which switch { 0 => PdfMeasurementKind.Distance, 1 => PdfMeasurementKind.Perimeter, _ => PdfMeasurementKind.Area };
                LoadViewports();
                SetTool(PageView.PageTool.Measure);
                // The drawing of the page in view is read now, so that the first point can snap.
                if (SnapToDrawing) SnapIndex(_pages.CurrentPage);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ the scale

    private ISharedPreferences? MeasureStore => GetSharedPreferences(MeasurePreferences, FileCreationMode.Private);

    /// <summary>The reader's scale: paper value and unit, real value and unit (1 in = 1 in until one is set).</summary>
    private (double Paper, string PaperUnit, double Real, string RealUnit) UserScale()
    {
        var store = MeasureStore;
        double Read(string key) =>
            double.TryParse(store?.GetString(key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 && double.IsFinite(v) ? v : 1;
        string Unit(string key, IReadOnlyList<string> allowed) => store?.GetString(key, null) is { } u && allowed.Contains(u) ? u : "in";
        return (Read("paper"), Unit("paper-unit", PaperUnits), Read("real"), Unit("real-unit", PdfMeasure.Units));
    }

    /// <summary>The units a scale starts from on paper (a drawing is printed in these).</summary>
    private static readonly string[] PaperUnits = ["pt", "in", "mm", "cm"];

    private PdfMeasure UserMeasure()
    {
        var (paper, paperUnit, real, realUnit) = UserScale();
        return PdfMeasure.FromScale(paper, paperUnit, real, realUnit);
    }

    private string UserScaleText() => UserMeasure().Ratio;

    /// <summary>The scale dialog: "on the page [1] [in] = in reality [10] [ft]", kept for every document.</summary>
    private void AskScale(Action? done = null)
    {
        var (paper, paperUnit, real, realUnit) = UserScale();
        var builder = new AlertDialog.Builder(this);
        var context = builder.Context ?? this;
        var column = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Vertical };
        column.SetPadding((int)Dp(24), (int)Dp(8), (int)Dp(24), 0);
        var note = new TextView(context) { Text = "A page's own scale, where it has one, is used instead." };
        note.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        column.AddView(note);

        (EditText Value, Spinner Unit) Row(string label, double value, IReadOnlyList<string> units, string unit)
        {
            var caption = new TextView(context) { Text = label };
            caption.SetPadding(0, (int)Dp(12), 0, 0);
            column.AddView(caption);
            var row = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            var input = new EditText(context)
            {
                Text = value.ToString("0.######", CultureInfo.InvariantCulture),
                InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal,
                ContentDescription = label,
            };
            input.SetSingleLine(true);
            input.SetMinHeight((int)Dp(48));
            var spinner = new Spinner(context) { ContentDescription = label + " unit" };
            var adapter = new ArrayAdapter<string>(context, global::Android.Resource.Layout.SimpleSpinnerItem, units.ToList());
            adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            spinner.Adapter = adapter;
            spinner.SetSelection(Math.Max(0, units.ToList().IndexOf(unit)));
            spinner.SetMinimumHeight((int)Dp(48));
            row.AddView(input, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            row.AddView(spinner);
            column.AddView(row);
            return (input, spinner);
        }

        var onPaper = Row("On the page", paper, PaperUnits, paperUnit);
        var inReality = Row("In reality", real, PdfMeasure.Units, realUnit);
        builder.SetTitle("Measuring scale")!
            .SetView(column)!
            .SetPositiveButton("OK", (_, _) =>
            {
                // A comma is a decimal separator too ("2,5").
                static double Parse(EditText t) =>
                    double.TryParse((t.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
                double p = Parse(onPaper.Value), r = Parse(inReality.Value);
                if (!(p > 0) || !(r > 0) || !double.IsFinite(p) || !double.IsFinite(r))
                {
                    Toast.MakeText(this, "A scale needs two numbers above zero.", ToastLength.Short)?.Show();
                    return;
                }
                MeasureStore?.Edit()?
                    .PutString("paper", p.ToString("R", CultureInfo.InvariantCulture))?
                    .PutString("paper-unit", PaperUnits[Math.Max(0, onPaper.Unit.SelectedItemPosition)])?
                    .PutString("real", r.ToString("R", CultureInfo.InvariantCulture))?
                    .PutString("real-unit", PdfMeasure.Units[Math.Max(0, inReality.Unit.SelectedItemPosition)])?
                    .Apply();
                Toast.MakeText(this, $"Scale: {UserScaleText()}", ToastLength.Short)?.Show();
                // A measurement being placed takes the new scale (unless the page has its own).
                if (_pathPoints.Count > 0 && !_measureFromPage)
                {
                    _measure = UserMeasure();
                    UpdatePath();
                }
                done?.Invoke();
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ snapping to the drawing

    private const string SnapKey = "snap-to-drawing";

    /// <summary>
    /// Measuring points snap to the ends, middles and lines of the page's drawing (the desktop's
    /// default, where Shift places a point freely); on unless turned off, kept with the scale.
    /// </summary>
    private bool SnapToDrawing => MeasureStore?.GetBoolean(SnapKey, true) ?? true;

    private void SetSnapToDrawing(bool on)
    {
        MeasureStore?.Edit()?.PutBoolean(SnapKey, on)?.Apply();
        ConnectMeasureSnap();
        Toast.MakeText(this, on ? "Points snap to the page's drawing." : "Points go exactly where you tap.", ToastLength.Short)?.Show();
    }

    /// <summary>Gives the pages the snapping (or none when it is off).</summary>
    private void ConnectMeasureSnap() => _pages.MeasureSnap = SnapToDrawing ? SnapMeasurePoint : null;

    // The pages' drawings as snap indexes (a few pages kept), read on a worker thread, per document.
    private ViewerDocument? _snapsFor;
    private readonly Dictionary<int, PdfSnapIndex?> _snaps = new();
    private readonly HashSet<int> _snapsLoading = new();

    /// <summary>
    /// Where a measuring point snaps on <paramref name="page"/> (<see cref="ViewerMeasureSnap"/>);
    /// the point as it is while the page's drawing is still being read, or when it cannot be.
    /// </summary>
    private (ViewerPoint Point, PdfSnapKind Kind) SnapMeasurePoint(int page, ViewerPoint point, double tolerance) =>
        SnapIndex(page) is { } index && _doc is { } doc
            ? ViewerMeasureSnap.Find(index, doc.Geometry[page - 1], point, tolerance)
            : (point, PdfSnapKind.None);

    /// <summary>The page's snap index when it has been read; otherwise null, and it is read (once) on a worker thread.</summary>
    private PdfSnapIndex? SnapIndex(int page)
    {
        if (_doc is not { IsComplete: true } doc || page < 1 || page > doc.PageCount) return null;
        if (!ReferenceEquals(_snapsFor, doc))
        {
            _snapsFor = doc;
            _snaps.Clear();
            _snapsLoading.Clear();
        }
        if (_snaps.TryGetValue(page, out var index)) return index;
        if (!_snapsLoading.Add(page)) return null;
        byte[] bytes = doc.Bytes;
        _ = Task.Run(async () =>
        {
            try
            {
                using var engine = OpenForTool(bytes);
                return await ViewerMeasureSnap.BuildAsync(engine, page);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null; // a page that cannot be read is measured without snapping
            }
        }).ContinueWith(t => RunOnUiThread(() =>
        {
            if (!ReferenceEquals(_snapsFor, doc)) return;
            _snapsLoading.Remove(page);
            // A few pages' drawings are kept: a large map's index holds many segments.
            if (_snaps.Count >= 6) _snaps.Clear();
            _snaps[page] = t.IsCompletedSuccessfully ? t.Result : null;
        }), TaskScheduler.Default);
        return null;
    }

    // ------------------------------------------------------------------ the page's scale

    /// <summary>Reads every page's viewports once per document, on a worker thread; until then the reader's scale is used.</summary>
    private void LoadViewports()
    {
        if (_doc is not { IsComplete: true } doc || ReferenceEquals(_viewportsFor, doc)) return;
        _viewportsFor = doc;
        _viewports = null;
        byte[] bytes = doc.Bytes;
        _ = Task.Run(() =>
        {
            try
            {
                using var engine = OpenForTool(bytes);
                var all = new List<IReadOnlyList<PdfViewport>>(engine.PageCount);
                for (int p = 1; p <= engine.PageCount; p++) all.Add(PdfViewports.Read(engine, p));
                return all;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null; // pages without a readable /VP are measured with the reader's scale
            }
        }).ContinueWith(t => RunOnUiThread(() =>
        {
            if (ReferenceEquals(_viewportsFor, doc)) _viewports = t.Result;
        }), TaskScheduler.Default);
    }

    /// <summary>A point of the shown page in user space, where measures and viewports are.</summary>
    private PdfPoint UserPoint(int page, ViewerPoint shown)
    {
        var (x, y) = _doc!.Geometry[page - 1].ToUser(shown.X, shown.Y);
        return new PdfPoint(x, y);
    }

    /// <summary>The first point of a measurement: the page's viewport there gives the scale, else the reader's.</summary>
    private void StartMeasurement(int page, ViewerPoint point)
    {
        _measure = null;
        _measureFromPage = false;
        if (_viewports is { } all && page <= all.Count && PdfViewports.At(all[page - 1], UserPoint(page, point)) is { Measure: { } fromPage })
        {
            _measure = fromPage;
            _measureFromPage = true;
        }
        _measure ??= UserMeasure();
    }

    private int MeasureNeeds => _measureKind switch { PdfMeasurementKind.Area => 3, _ => 2 };

    /// <summary>A distance is finished at its second point.</summary>
    private bool MeasureDone() => _measureKind == PdfMeasurementKind.Distance && _pathPoints.Count >= 2;

    /// <summary>The result of the points (and the finger while it aims), or null with too few.</summary>
    private string? MeasureResult(bool withAim)
    {
        if (_measure is not { } measure || _doc == null) return null;
        var points = _pathPoints.Select(p => UserPoint(_pathPage, p)).ToList();
        if (withAim && _pathAim is { } aim && !(MeasureDone())) points.Add(UserPoint(_pathPage, aim));
        if (points.Count < MeasureNeeds) return null;
        return PdfMeasurements.Label(_measureKind, points, measure);
    }

    private string MeasureName => _measureKind switch { PdfMeasurementKind.Distance => "Distance", PdfMeasurementKind.Perimeter => "Perimeter", _ => "Area" };

    /// <summary>The live line in the bar: the result so far, or how many points are still needed.</summary>
    private string MeasureReadout()
    {
        if (MeasureResult(withAim: true) is { } result) return $"{MeasureName}: {result}";
        int more = MeasureNeeds - _pathPoints.Count;
        return more <= 1 ? "Tap the next point." : $"Tap {more} more points.";
    }

    /// <summary>
    /// The measurement is done (its second point for a distance, Done or a double tap otherwise):
    /// the result, the scale it was measured with, and Add to page or Copy.
    /// </summary>
    private void FinishMeasurement()
    {
        if (_pathPoints.Count < MeasureNeeds || MeasureResult(withAim: false) is not { } result || _measure is not { } measure)
        {
            Toast.MakeText(this, _measureKind == PdfMeasurementKind.Area ? "An area needs at least three points." : "Place at least two points.", ToastLength.Short)?.Show();
            return;
        }
        int page = _pathPage;
        var points = _pathPoints.ToList();
        var kind = _measureKind;
        string scale = $"Scale {measure.Ratio}" + (_measureFromPage ? " (the page's own)" : "");
        string name = MeasureName;
        ClearPath();
        var builder = new AlertDialog.Builder(this)
            .SetTitle($"{name}: {result}")!
            .SetMessage($"{scale}, page {page}.")!
            .SetNegativeButton("Close", (_, _) => { })!
            .SetNeutralButton("Copy", (_, _) =>
            {
                var clipboard = GetSystemService(ClipboardService) as global::Android.Content.ClipboardManager;
                if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText(name, result);
            })!;
        if (CanAnnotate)
            builder.SetPositiveButton("Add to page", (_, _) => AddMeasurement(kind, page, points, measure));
        builder.Show();
    }

    /// <summary>
    /// The measurement as a measurement annotation, one undo step: a Line with arrows at both ends
    /// (distance), a PolyLine (perimeter) or a Polygon (area) with /Measure and /IT, 1 point wide in
    /// the measurement blue, with the result as its label and comment; moving a point measures again.
    /// </summary>
    private void AddMeasurement(PdfMeasurementKind kind, int page, IReadOnlyList<ViewerPoint> points, PdfMeasure measure) =>
        Change(s => s.AddMeasurement(kind, page, points, measure, MeasureColour) is { } a && Picked(a.Id));
}
