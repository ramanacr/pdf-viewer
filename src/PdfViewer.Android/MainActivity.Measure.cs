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
/// (<c>measure-scale</c>; the document is not named). The results are the engine's
/// (<see cref="PdfMeasure"/>, <see cref="PdfMeasurements.Label"/>). The shared edit session writes no
/// measurement annotations (/Measure and /IT) for new annotations, so a result can be added to the
/// page as a line, connected lines or a polygon with the result as its comment, one undo step.
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

    /// <summary>The Measure button: what to measure (or the scale); again with measuring on, it stops.</summary>
    private void ChooseMeasure()
    {
        if (_pages.Tool == PageView.PageTool.Measure)
        {
            SetTool(PageView.PageTool.Measure);
            return;
        }
        string[] items = ["Distance", "Perimeter", "Area", $"Scale: {UserScaleText()}…"];
        new AlertDialog.Builder(this)
            .SetTitle("Measure")!
            .SetItems(items, (_, e) =>
            {
                if (e.Which == 3)
                {
                    AskScale(ChooseMeasure);
                    return;
                }
                _measureKind = e.Which switch { 0 => PdfMeasurementKind.Distance, 1 => PdfMeasurementKind.Perimeter, _ => PdfMeasurementKind.Area };
                LoadViewports();
                SetTool(PageView.PageTool.Measure);
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
            builder.SetPositiveButton("Add to page", (_, _) => AddMeasurement(kind, page, points, $"{name}: {result} ({scale})"));
        builder.Show();
    }

    /// <summary>
    /// The measurement as an annotation, one undo step: a line with arrows at both ends for a
    /// distance, connected lines for a perimeter, a polygon for an area, drawn 1 point wide in the
    /// measurement blue, with the result as its comment.
    /// </summary>
    private void AddMeasurement(PdfMeasurementKind kind, int page, IReadOnlyList<ViewerPoint> points, string label)
    {
        Change(s =>
        {
            var g = s.Document.Geometry[page - 1];
            var flat = new List<double>(points.Count * 2);
            foreach (var p in points)
            {
                var (x, y) = g.ToUser(Math.Clamp(p.X, 0, g.ShownWidth), Math.Clamp(p.Y, 0, g.ShownHeight));
                flat.Add(x);
                flat.Add(y);
            }
            bool distance = kind == PdfMeasurementKind.Distance;
            if (distance) flat = [flat[0], flat[1], flat[^2], flat[^1]];
            // Room for the arrowheads (nine line widths), kept on the page.
            var box = ViewerBox.Around(flat, distance ? 10 : 2);
            var m = g.MediaBox;
            box = new ViewerBox(Math.Max(box.X1, m.Left), Math.Max(box.Y1, m.Top), Math.Min(box.X2, m.Right), Math.Min(box.Y2, m.Bottom));
            var a = new ViewerAnnotation
            {
                Id = "",
                Page = page,
                Kind = distance ? ViewerAnnotationKind.Line : kind == PdfMeasurementKind.Perimeter ? ViewerAnnotationKind.PolyLine : ViewerAnnotationKind.Polygon,
                Box = box,
                Vertices = flat,
                Color = MeasureColour,
                StrokeWidth = 1,
                StartEnding = distance ? "OpenArrow" : "None",
                EndEnding = distance ? "OpenArrow" : "None",
                Contents = label,
            };
            return s.AddAnnotations([a]).Count > 0;
        });
    }
}
