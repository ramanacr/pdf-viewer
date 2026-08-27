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

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Normalized coordinate points (0.0 to 1.0) for freehand ink drawings.
    /// </summary>
    public List<Point> InkPoints { get; set; } = new();

    public string DisplaySummary => $"{Type} on Page {PageNumber}" +
        (!string.IsNullOrWhiteSpace(Contents) ? $": \"{Contents}\"" : string.Empty);
}
