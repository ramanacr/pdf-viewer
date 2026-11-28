using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Web.Engine;

/// <summary>
/// Snapping a measuring point to the page's drawing, as the desktop viewer does: the ends and middles
/// of its lines and the nearest point on a line or curve (<see cref="PdfSnapIndex"/>, built from the
/// page's display list in default user space), found and given back in shown-page points.
/// </summary>
public static class ViewerMeasureSnap
{
    /// <summary>How near (density-independent pixels on screen) a point of the drawing must be for a point to snap to it.</summary>
    public const double ToleranceDp = 12;

    /// <summary>
    /// The page's drawing as a snap index, or null when the page cannot be drawn (it is then measured
    /// without snapping). Call it on a worker thread: it interprets the page.
    /// </summary>
    public static async Task<PdfSnapIndex?> BuildAsync(PdfVectorDocument document, int page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (page < 1 || page > document.PageCount) return null;
        try
        {
            return PdfSnapIndex.Build(await document.GetPageDisplayListAsync(page, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return null;
        }
    }

    /// <summary>
    /// Where <paramref name="shown"/> (a point of the shown page) snaps to within
    /// <paramref name="tolerance"/> points: an end, a middle or the nearest point of the drawing
    /// (in that order of preference), with what it snapped to; the point itself and
    /// <see cref="PdfSnapKind.None"/> when nothing is near or there is no index.
    /// </summary>
    public static (ViewerPoint Point, PdfSnapKind Kind) Find(PdfSnapIndex? index, ViewerPageGeometry geometry, ViewerPoint shown, double tolerance)
    {
        if (index == null || !(tolerance > 0) || !double.IsFinite(shown.X) || !double.IsFinite(shown.Y)) return (shown, PdfSnapKind.None);
        var (ux, uy) = geometry.ToUser(shown.X, shown.Y);
        var hit = index.Find(new PdfPoint(ux, uy), tolerance);
        if (hit.Kind == PdfSnapKind.None) return (shown, PdfSnapKind.None);
        var (sx, sy) = geometry.ToShown(hit.Point.X, hit.Point.Y);
        return (new ViewerPoint(sx, sy), hit.Kind);
    }

    /// <summary>The tolerance in points for a page shown at <paramref name="pixelsPerPoint"/> view pixels per point and <paramref name="density"/> pixels per dp.</summary>
    public static double Tolerance(double pixelsPerPoint, double density) =>
        ToleranceDp * Math.Max(0.1, density) / Math.Max(0.01, pixelsPerPoint);
}
