using System;
using System.Collections.Generic;
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
    Ellipse
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
    public List<List<Point>> InkStrokes { get; set; } = new();

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

    public string DisplaySummary => $"{Type} on Page {PageNumber}" +
        (!string.IsNullOrWhiteSpace(Contents) ? $": \"{Contents}\"" : string.Empty);
}
