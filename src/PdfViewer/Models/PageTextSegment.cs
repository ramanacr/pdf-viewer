using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PdfViewer.Models;

/// <summary>
/// Represents an individual text segment/word on a PDF page with normalized coordinates (0.0 to 1.0).
/// </summary>
public partial class PageTextSegment : ObservableObject
{
    [ObservableProperty]
    private int _segmentIndex;

    [ObservableProperty]
    private int _pageNumber;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private double _height;

    [ObservableProperty]
    private bool _isSelected;

    public Rect NormalizedBounds => new(X, Y, Width, Height);

    public Rect GetScreenBounds(double displayWidth, double displayHeight)
    {
        return new Rect(X * displayWidth, Y * displayHeight, Width * displayWidth, Height * displayHeight);
    }
}
