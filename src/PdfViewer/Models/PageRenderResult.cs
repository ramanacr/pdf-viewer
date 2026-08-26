using System.Windows.Media.Imaging;

namespace PdfViewer.Models;

/// <summary>
/// Container for a rendered page image and its dimensions.
/// </summary>
public class PageRenderResult
{
    public int PageNumber { get; init; }
    public BitmapSource? Image { get; init; }
    public double OriginalWidthPt { get; init; }
    public double OriginalHeightPt { get; init; }
    public int Dpi { get; init; }
    public int RotationDegrees { get; init; }
}
