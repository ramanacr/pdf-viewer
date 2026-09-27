using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Editing;

namespace PdfViewer.ViewModels;

public enum ContentEditKind { Text, Image }

/// <summary>A paragraph or image of a page, as the edit layer shows it.</summary>
public sealed partial class ContentEditItem : ObservableObject
{
    public required ContentEditKind Kind { get; init; }
    /// <summary>The id the engine knows it by (valid for the revision it was read from).</summary>
    public required int Id { get; init; }
    public required int PageNumber { get; init; }
    /// <summary>Normalized to the unrotated page, top-left origin (like every page overlay).</summary>
    public required Rect Bounds { get; init; }

    // ---- paragraphs
    public string Text { get; init; } = string.Empty;
    public string FontName { get; init; } = string.Empty;
    /// <summary>An installed family that looks like the paragraph's font, for editing it on screen.</summary>
    public string? FontFamily { get; init; }
    /// <summary>Em height, in points.</summary>
    public double FontSize { get; init; }
    public double LineSpacing { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public Color Color { get; init; } = Colors.Black;
    public PdfTextAlignment Alignment { get; init; }
    /// <summary>Baseline direction in user space (counter-clockwise from +x); the layer turns the editor by its opposite.</summary>
    public double AngleDegrees { get; init; }
    public int LineCount { get; init; } = 1;

    // ---- images
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public string AccessibleName => Kind == ContentEditKind.Text
        ? $"Paragraph: {(Text.Length > 60 ? Text[..57] + "..." : Text)}"
        : $"Image, {PixelWidth} by {PixelHeight} pixels";
}
