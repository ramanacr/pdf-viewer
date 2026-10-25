using Microsoft.JSInterop;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Page thumbnails in the side panel. Only thumbnails near the panel's view are drawn, after the
/// pages in view, and a thumbnail that scrolls far away gives its canvas memory back.
/// </summary>
public sealed partial class Viewer
{
    /// <summary>Width of a thumbnail in CSS pixels.</summary>
    private const double ThumbWidth = 112;

    private readonly HashSet<int> _thumbsVisible = new();
    private readonly HashSet<int> _thumbsDrawn = new();
    private bool _observeThumbs;

    private bool ShowingThumbnails => _doc != null && _sidebarOpen && _panel == Panel.Thumbnails && _config.HasSidePanel && _config.ShowThumbnails;

    private string PagesClass => "pages" + (_night ? " night" : "") + (_twoColumns && _doc?.PageCount > 1 ? " two-column" : "");

    private static (double Width, double Height) ThumbSize(ViewerPage page) =>
        (ThumbWidth, Math.Round(ThumbWidth * page.Height / page.Width, 1));

    private void ShowThumbnails()
    {
        _panel = Panel.Thumbnails;
        _thumbsDrawn.Clear();
        _observeThumbs = true;
    }

    [JSInvokable]
    public void OnThumbnails(int[] visible)
    {
        _thumbsVisible.Clear();
        foreach (int p in visible) _thumbsVisible.Add(p);
        // viewer.js empties a thumbnail's canvas when it leaves the view.
        _thumbsDrawn.IntersectWith(_thumbsVisible);
        _ = PumpAsync();
    }

    /// <summary>The next thumbnail to draw, nearest the current page first; 0 when none.</summary>
    private int NextThumbnail() =>
        !ShowingThumbnails ? 0 : _thumbsVisible.Where(p => !_thumbsDrawn.Contains(p)).OrderBy(p => Math.Abs(p - _currentPage)).FirstOrDefault();

    private async Task DrawThumbnailAsync(ViewerDocument doc, int page)
    {
        _thumbsDrawn.Add(page);
        var pv = doc.Pages[page - 1];
        double scale = ThumbWidth / pv.Width * CanvasInterop.DevicePixelRatio();
        try
        {
            var bitmap = await doc.RenderPageAsync(page, scale, _night);
            if (_doc == doc) CanvasInterop.DrawThumbnail(page, bitmap.Pixels, bitmap.Width, bitmap.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            // The page itself reports the failure when it is drawn; a thumbnail stays blank.
        }
    }
}
