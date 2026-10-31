using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfViewer.Models;

/// <summary>
/// Supported PDF annotation types.
/// </summary>
public enum AnnotationType
{
    Highlight,
    Underline,
    StrikeOut,
    Note,
    FreeText,
    Ink,
    Rectangle,
    Ellipse,
    /// <summary>A distance, perimeter or area measurement (Line, PolyLine or Polygon with /Measure).</summary>
    Measurement,
    /// <summary>A stamp: a standard or dynamic one, a picture or a PDF page.</summary>
    Stamp,
    /// <summary>A straight line (a Line annotation without /Measure).</summary>
    Line,
    /// <summary>A line with an arrow head (a Line annotation with a line ending in /LE).</summary>
    Arrow,
    /// <summary>A closed shape of straight sides (a Polygon annotation without /Measure).</summary>
    Polygon,
    /// <summary>Connected lines that stay open (a PolyLine annotation without /Measure).</summary>
    PolyLine
}

/// <summary>
/// Saving modes for annotated documents.
/// </summary>
public enum AnnotationSaveMode
{
    /// <summary>
    /// Standard PDF with embedded native annotation objects.
    /// Fully editable in standard PDF viewers (Acrobat, Edge, Preview).
    /// </summary>
    Embedded,

    /// <summary>
    /// Annotations are permanently rasterized/merged into the page content stream.
    /// Cannot be removed, edited, or deleted as comments.
    /// </summary>
    Flattened,

    /// <summary>
    /// Annotations are exported to an external XFDF XML comments file.
    /// Leaves the original base PDF completely clean and untouched.
    /// </summary>
    ExportXfdf
}

/// <summary>
/// Represents a user or document annotation on a PDF page.
/// </summary>
public partial class AnnotationModel : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty]
    private int _pageNumber = 1;

    [ObservableProperty]
    private AnnotationType _type = AnnotationType.Highlight;

    // Normalized coordinates (0.0 to 1.0 relative to page top-left)
    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private double _height;

    [ObservableProperty]
    private string _colorHex = "#FFFF00"; // Default Yellow

    [ObservableProperty]
    private double _opacity = 0.5;

    /// <summary>
    /// The most opacity the overlay gives a highlight it draws (one made here or changed since it
    /// was loaded). The saved highlight multiplies its colour with the page, so the text under it
    /// stays dark; the WPF overlay can only blend, and above this the text would fade out.
    /// </summary>
    public const double MaxHighlightOverlayOpacity = 0.5;

    /// <summary>The opacity the overlay draws a highlight with: <see cref="Opacity"/>, at most <see cref="MaxHighlightOverlayOpacity"/>.</summary>
    public double HighlightOverlayOpacity => Math.Clamp(Opacity, 0, MaxHighlightOverlayOpacity);

    partial void OnOpacityChanged(double value) => OnPropertyChanged(nameof(HighlightOverlayOpacity));

    [ObservableProperty]
    private double _strokeThickness = 2.0;

    [ObservableProperty]
    private string _contents = string.Empty;

    [ObservableProperty]
    private string _author = Environment.UserName;

    [ObservableProperty]
    private string _title = "Note";

    [ObservableProperty]
    private DateTime _creationDate = DateTime.Now;

    /// <summary>When this annotation was last changed - the PDF's /M entry.</summary>
    [ObservableProperty]
    private DateTime _modifiedDate = DateTime.Now;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Freehand ink, as one list of normalized (0.0 to 1.0) points per stroke.
    ///
    /// A PDF ink annotation carries an /InkList of several strokes - lifting the pen and
    /// carrying on is one annotation, not several. Keeping only a single stroke meant that
    /// opening someone else's multi-stroke drawing and saving threw away everything after
    /// the first stroke, silently.
    /// </summary>
    public List<List<Point>> InkStrokes
    {
        get => _inkStrokes;
        set
        {
            _inkStrokes = value ?? new();
            OnPropertyChanged();
        }
    }
    private List<List<Point>> _inkStrokes = new();

    /// <summary>
    /// Text markup over several lines: one normalized rectangle per line (the PDF's /QuadPoints).
    /// Empty for a markup drawn as one box. A highlight made from a two-line selection covers
    /// the two lines, not the rectangle around them.
    /// </summary>
    public List<Rect> Quads
    {
        get => _quads;
        set
        {
            _quads = value ?? new();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQuads));
            OnPropertyChanged(nameof(HighlightGeometry));
            OnPropertyChanged(nameof(UnderlineGeometry));
            OnPropertyChanged(nameof(StrikeOutGeometry));
        }
    }
    private List<Rect> _quads = new();

    public bool HasQuads => _quads.Count > 1;

    // ------------------------------------------------------------------ measurements and stamps

    /// <summary>A measurement's points, normalized like the box (two for a distance).</summary>
    public List<Point> Vertices
    {
        get => _vertices;
        set
        {
            _vertices = value ?? new();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MeasureGeometry));
        }
    }
    private List<Point> _vertices = new();

    /// <summary>A line's or connected lines' /LE: the start and end styles ("None", "OpenArrow"...); null for none.</summary>
    public string[]? LineEndings
    {
        get => _lineEndings;
        set
        {
            _lineEndings = value;
            OnPropertyChanged();
        }
    }
    private string[]? _lineEndings;

    /// <summary>The /IC fill of a rectangle, ellipse or polygon, and of closed line endings; null for none.</summary>
    public string? InteriorColorHex
    {
        get => _interiorColorHex;
        set
        {
            _interiorColorHex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FillColorHex));
        }
    }
    private string? _interiorColorHex;

    /// <summary>The fill drawn on screen: the /IC, or transparent when there is none.</summary>
    public string FillColorHex => _interiorColorHex ?? "#00000000";

    /// <summary>The dash of the border (/BS /S /D) in points, as dash and gap lengths; null for a solid line.</summary>
    public double[]? BorderDash
    {
        get => _borderDash;
        set
        {
            _borderDash = value is { Length: > 0 } ? value : null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StrokeDashArray));
        }
    }
    private double[]? _borderDash;

    /// <summary>
    /// The dash on screen: WPF counts dashes in line widths, the PDF in points, and an odd PDF
    /// array repeats (so [3] is 3 on, 3 off).
    /// </summary>
    public System.Windows.Media.DoubleCollection? StrokeDashArray =>
        _borderDash == null ? null
        : new System.Windows.Media.DoubleCollection((_borderDash.Length % 2 == 1 ? _borderDash.Concat(_borderDash) : _borderDash).Select(d => d / Math.Max(StrokeThickness, 0.1)));

    partial void OnStrokeThicknessChanged(double value) => OnPropertyChanged(nameof(StrokeDashArray));

    /// <summary>The line ending styles of PDF (Table 179), in the order the style editor lists them.</summary>
    public static IReadOnlyList<string> LineEndingStyles { get; } = new[]
    {
        "None", "OpenArrow", "ClosedArrow", "Circle", "Square", "Diamond", "Butt", "ROpenArrow", "RClosedArrow", "Slash",
    };

    /// <summary>The dash presets of the style editor: dash and gap lengths in points (null is solid).</summary>
    public static IReadOnlyList<(string Name, double[]? Dash)> DashStyles { get; } = new (string, double[]?)[]
    {
        ("Solid", null), ("Dashed", new[] { 4.0, 3.0 }), ("Dotted", new[] { 1.0, 2.0 }),
    };

    /// <summary>A rectangle, ellipse, line, arrow, polygon or connected lines: a shape whose fill, endings or dash can be chosen.</summary>
    public bool IsStyledShape => IsVertexShape || Type is AnnotationType.Rectangle or AnnotationType.Ellipse;

    /// <summary>A line, arrow or connected lines: a shape with line endings.</summary>
    public bool HasLineEndings => Type is AnnotationType.Line or AnnotationType.Arrow or AnnotationType.PolyLine;

    /// <summary>The start ending ("None" when there is none; a single name, which is not a valid /LE for a line, draws none, as in the engine).</summary>
    public string StartEnding => _lineEndings is { Length: >= 2 } e ? e[0] : "None";

    /// <summary>The end ending ("None" when there is none).</summary>
    public string EndEnding => _lineEndings is { Length: >= 2 } e ? e[1] : "None";

    /// <summary>
    /// Sets both line endings. A line becomes an arrow when either end has an ending, and an
    /// arrow a line when neither has, so the tool shown is the one the saved file reopens as.
    /// </summary>
    public void SetLineEndings(string start, string end)
    {
        if (!HasLineEndings) return;
        LineEndings = new[] { start, end };
        if (Type is AnnotationType.Line or AnnotationType.Arrow)
        {
            var type = start != "None" || end != "None" ? AnnotationType.Arrow : AnnotationType.Line;
            // The subject follows the type while it is still the tool's name (the user's own stays).
            if (Title == Type.ToString()) Title = type.ToString();
            Type = type;
        }
    }

    /// <summary>A line, arrow, polygon or connected lines: a shape drawn from its <see cref="Vertices"/>.</summary>
    public bool IsVertexShape => Type is AnnotationType.Line or AnnotationType.Arrow or AnnotationType.Polygon or AnnotationType.PolyLine;

    partial void OnTypeChanged(AnnotationType value)
    {
        OnPropertyChanged(nameof(IsVertexShape));
        OnPropertyChanged(nameof(IsStyledShape));
        OnPropertyChanged(nameof(HasLineEndings));
    }

    /// <summary>
    /// A line, arrow, polygon or connected lines in the coordinates of its box drawn
    /// <paramref name="width"/> by <paramref name="height"/> pixels, with its line endings drawn at
    /// the size the engine draws them (arrows nine line widths long at 30 degrees, other shapes
    /// six line widths across). <paramref name="body"/> and <paramref name="ends"/> choose the
    /// parts: the engine dashes the lines but draws the endings solid. <paramref name="scale"/> is
    /// screen pixels per point (the zoom): the endings are sized in line widths, which are points.
    /// Null when it has too few points.
    /// </summary>
    public System.Windows.Media.Geometry? ShapeGeometry(double width, double height, bool body = true, bool ends = true, double scale = 1)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        if (!IsVertexShape || _vertices.Count < 2 || Width <= 0 || Height <= 0 || width <= 0 || height <= 0) return null;
        Point Local(Point p) => new((p.X - X) / Width * width, (p.Y - Y) / Height * height);
        var points = _vertices.Select(Local).ToList();
        var path = new System.Windows.Media.PathGeometry();
        bool filled = InteriorColorHex != null;
        if (body)
        {
            var figure = new System.Windows.Media.PathFigure { StartPoint = points[0], IsClosed = Type == AnnotationType.Polygon, IsFilled = filled && Type == AnnotationType.Polygon };
            foreach (var p in points.Skip(1)) figure.Segments.Add(new System.Windows.Media.LineSegment(p, true));
            path.Figures.Add(figure);
        }
        if (ends && HasLineEndings)
        {
            Ending(path, StartEnding, points[0], points[0] - points[1], filled, scale);
            Ending(path, EndEnding, points[^1], points[^1] - points[^2], filled, scale);
        }
        path.Freeze();
        return path;
    }

    /// <summary>
    /// A line ending at <paramref name="tip"/>, <paramref name="outward"/> pointing away from the
    /// line, as the engine draws it (a closed one filled with /IC). The screen's y axis points
    /// down, so the side vector is turned the other way to match the page.
    /// </summary>
    private void Ending(System.Windows.Media.PathGeometry path, string style, Point tip, Vector outward, bool filled, double scale)
    {
        if (style == "None" || outward.Length < 1e-9) return;
        outward.Normalize();
        var d = outward;
        var n = new Vector(d.Y, -d.X);
        double s = Math.Max(StrokeThickness, 1) * scale;
        double half = 3 * s, arrow = 9 * s;
        var back = d * (arrow * Math.Cos(Math.PI / 6));
        var side = n * (arrow * Math.Sin(Math.PI / 6));
        void Figure(bool closed, params Point[] pts)
        {
            var f = new System.Windows.Media.PathFigure { StartPoint = pts[0], IsClosed = closed, IsFilled = filled && closed };
            foreach (var p in pts.Skip(1)) f.Segments.Add(new System.Windows.Media.LineSegment(p, true));
            path.Figures.Add(f);
        }
        switch (style)
        {
            case "OpenArrow": Figure(false, tip - back + side, tip, tip - back - side); break;
            case "ClosedArrow": Figure(true, tip - back + side, tip, tip - back - side); break;
            case "ROpenArrow": Figure(false, tip + back + side, tip, tip + back - side); break;
            case "RClosedArrow": Figure(true, tip + back + side, tip, tip + back - side); break;
            case "Square": Figure(true, tip + d * half + n * half, tip - d * half + n * half, tip - d * half - n * half, tip + d * half - n * half); break;
            case "Diamond": Figure(true, tip + d * half, tip + n * half, tip - d * half, tip - n * half); break;
            case "Butt": Figure(false, tip + n * half, tip - n * half); break;
            case "Slash":
            {
                var v = d * Math.Cos(Math.PI / 3) + n * Math.Sin(Math.PI / 3);
                Figure(false, tip + v * (arrow / 2), tip - v * (arrow / 2));
                break;
            }
            case "Circle":
            {
                var radius = new Size(half, half);
                var f = new System.Windows.Media.PathFigure { StartPoint = tip + new Vector(half, 0), IsClosed = true, IsFilled = filled };
                f.Segments.Add(new System.Windows.Media.ArcSegment(tip - new Vector(half, 0), radius, 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
                f.Segments.Add(new System.Windows.Media.ArcSegment(tip + new Vector(half, 0), radius, 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
                path.Figures.Add(f);
                break;
            }
        }
    }

    /// <summary>What a measurement measures.</summary>
    [ObservableProperty]
    private PdfEngine.Vector.Annotations.PdfMeasurementKind _measureKind;

    /// <summary>A measurement's scale and units (its /Measure).</summary>
    [ObservableProperty]
    private PdfEngine.Vector.Annotations.PdfMeasure? _measure;

    /// <summary>
    /// Free text's turn on the page (its /Rotate): 0, 90, 180 or 270 degrees counter-clockwise, so
    /// it reads upright in a view turned that far clockwise. Null when it has none: it then turns
    /// with the page's own /Rotate. Text boxes made in a turned view get the view's turn.
    /// </summary>
    [ObservableProperty]
    private int? _textRotation;

    /// <summary>A stamp's /Name (for a custom stamp, its library name).</summary>
    [ObservableProperty]
    private string _stampName = string.Empty;

    /// <summary>The second line of a dynamic stamp (who and when), drawn into its appearance.</summary>
    [ObservableProperty]
    private string? _stampSubtitle;

    /// <summary>The stamp's turn, degrees counter-clockwise.</summary>
    [ObservableProperty]
    private double _stampRotation;

    /// <summary>The stamp's size before it is turned, in points.</summary>
    [ObservableProperty]
    private Size _stampSize;

    /// <summary>A custom stamp's picture (the PNG or JPEG file) or one-page PDF.</summary>
    public byte[]? StampImage { get; set; }
    public byte[]? StampPdf { get; set; }

    /// <summary>The stamp as it will look in the document, drawn by the engine (null until drawn, and for stamps read from the file).</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? _stampPreview;

    /// <summary>
    /// Read from the file with its own appearance, which the page already shows: the overlay only
    /// marks where it is. Measurements and stamps made here are drawn by the overlay.
    /// </summary>
    public bool HasOwnAppearance
    {
        get => _hasOwnAppearance;
        set
        {
            _hasOwnAppearance = value;
            IsDrawnByPage = value;
        }
    }
    private bool _hasOwnAppearance;

    /// <summary>
    /// The page draws this annotation from the file: loaded with its own appearance and not
    /// changed since. Once it is moved, sized, turned or edited the page leaves it out and the
    /// overlay draws it instead.
    /// </summary>
    [ObservableProperty]
    private bool _isDrawnByPage;

    /// <summary>A loaded stamp's turn as the file has it (its appearance is turned from there when saved); null for one made here.</summary>
    public double? FileStampRotation { get; set; }

    /// <summary>The turn and look <see cref="StampPreview"/> was drawn for, so a new turn draws it again.</summary>
    internal (double Rotation, bool Kept)? StampPreviewKey { get; set; }

    partial void OnMeasureChanged(PdfEngine.Vector.Annotations.PdfMeasure? value) => OnPropertyChanged(nameof(MeasureGeometry));

    /// <summary>A measurement's line or outline relative to the box (0–1), drawn stretched over it.</summary>
    public System.Windows.Media.Geometry? MeasureGeometry
    {
        get
        {
            if (_vertices.Count < 2 || Width <= 0 || Height <= 0) return null;
            var figure = new System.Windows.Media.PathFigure
            {
                StartPoint = Relative(_vertices[0]),
                IsClosed = MeasureKind == PdfEngine.Vector.Annotations.PdfMeasurementKind.Area,
            };
            foreach (var p in _vertices.Skip(1)) figure.Segments.Add(new System.Windows.Media.LineSegment(Relative(p), true));
            var path = new System.Windows.Media.PathGeometry();
            // An unstroked diagonal pins the geometry's bounds to the whole box, so Stretch=Fill maps 0–1 exactly.
            path.Figures.Add(new System.Windows.Media.PathFigure(new Point(0, 0), new[] { new System.Windows.Media.LineSegment(new Point(1, 1), false) }, false));
            path.Figures.Add(figure);
            path.Freeze();
            return path;
        }
    }

    private Point Relative(Point p) => new((p.X - X) / Width, (p.Y - Y) / Height);

    // Moving or resizing the annotation carries its lines with it.
    partial void OnXChanged(double oldValue, double newValue) => MoveQuads(newValue - oldValue, 0, 1, 1);
    partial void OnYChanged(double oldValue, double newValue) => MoveQuads(0, newValue - oldValue, 1, 1);
    partial void OnWidthChanged(double oldValue, double newValue) => MoveQuads(0, 0, oldValue > 0 ? newValue / oldValue : 1, 1);
    partial void OnHeightChanged(double oldValue, double newValue) => MoveQuads(0, 0, 1, oldValue > 0 ? newValue / oldValue : 1);

    private void MoveQuads(double dx, double dy, double sx, double sy)
    {
        if (_quads.Count == 0 || _suspendQuadSync) return;
        var moved = new List<Rect>(_quads.Count);
        foreach (var q in _quads)
        {
            double x = dx != 0 ? q.X + dx : sx != 1 ? X + (q.X - X) * sx : q.X;
            double y = dy != 0 ? q.Y + dy : sy != 1 ? Y + (q.Y - Y) * sy : q.Y;
            moved.Add(new Rect(x, y, q.Width * sx, q.Height * sy));
        }
        Quads = moved;
    }

    private bool _suspendQuadSync;

    /// <summary>Sets the box and the per-line rectangles together (the box is their union).</summary>
    public void SetQuads(IReadOnlyList<Rect> quads)
    {
        if (quads.Count == 0) { Quads = new(); return; }
        var union = quads[0];
        foreach (var q in quads) union.Union(q);
        _suspendQuadSync = true;
        try
        {
            X = union.X; Y = union.Y; Width = Math.Max(1e-4, union.Width); Height = Math.Max(1e-4, union.Height);
        }
        finally { _suspendQuadSync = false; }
        Quads = new List<Rect>(quads);
    }

    /// <summary>Sets the box and the per-line rectangles exactly as given (undo restores both as they were).</summary>
    public void SetGeometry(Rect box, IReadOnlyList<Rect> quads)
    {
        _suspendQuadSync = true;
        try
        {
            X = box.X; Y = box.Y; Width = box.Width; Height = box.Height;
        }
        finally { _suspendQuadSync = false; }
        Quads = new List<Rect>(quads);
    }

    /// <summary>The lines relative to the annotation's box (0–1), drawn stretched over it.</summary>
    private System.Windows.Media.Geometry? MarkupGeometry(Func<Rect, Rect> part)
    {
        if (!HasQuads || Width <= 0 || Height <= 0) return null;
        var group = new System.Windows.Media.GeometryGroup { FillRule = System.Windows.Media.FillRule.Nonzero };
        // Corner marks pin the geometry's bounds to the whole box, so Stretch=Fill maps 0–1 exactly.
        group.Children.Add(new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 1e-6, 1e-6)));
        group.Children.Add(new System.Windows.Media.RectangleGeometry(new Rect(1 - 1e-6, 1 - 1e-6, 1e-6, 1e-6)));
        foreach (var q in _quads)
        {
            var r = new Rect((q.X - X) / Width, (q.Y - Y) / Height, q.Width / Width, q.Height / Height);
            group.Children.Add(new System.Windows.Media.RectangleGeometry(part(r)));
        }
        group.Freeze();
        return group;
    }

    public System.Windows.Media.Geometry? HighlightGeometry => MarkupGeometry(r => r);
    public System.Windows.Media.Geometry? UnderlineGeometry => MarkupGeometry(r => new Rect(r.X, r.Bottom - r.Height * 0.08, r.Width, r.Height * 0.08));
    public System.Windows.Media.Geometry? StrikeOutGeometry => MarkupGeometry(r => new Rect(r.X, r.Y + r.Height * 0.46, r.Width, r.Height * 0.08));

    // ------------------------------------------------------------------ comment threads (ISO 32000-2 12.5.6.4)

    /// <summary>
    /// The comment this one answers (its /IRT, with /RT /R): the parent's <see cref="Id"/>, or
    /// null for a comment that answers none. A reply is a note with no drawing on the page: the
    /// comments panel lists it under the comment it answers. Set when it is made or read, never
    /// changed afterwards.
    /// </summary>
    public Guid? InReplyToId
    {
        get => _inReplyToId;
        set
        {
            _inReplyToId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReply));
            OnPropertyChanged(nameof(IsStateChange));
        }
    }
    private Guid? _inReplyToId;

    /// <summary>
    /// A reply's /State and /StateModel: a review status set on the comment it answers
    /// ("Accepted" in the "Review" model), as Acrobat writes it. Null for an ordinary reply.
    /// </summary>
    public string? State { get; set; }
    public string? StateModel
    {
        get => _stateModel;
        set
        {
            _stateModel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStateChange));
        }
    }
    private string? _stateModel;

    /// <summary>A reply to another comment (it has an <see cref="InReplyToId"/>).</summary>
    public bool IsReply => _inReplyToId != null;

    /// <summary>A reply that sets a status (/StateModel) rather than saying something.</summary>
    public bool IsStateChange => IsReply && !string.IsNullOrEmpty(_stateModel);

    public string DisplaySummary => $"{Type} on Page {PageNumber}" +
        (!string.IsNullOrWhiteSpace(Contents) ? $": \"{Contents}\"" : string.Empty);
}
