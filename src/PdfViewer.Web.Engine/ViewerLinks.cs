using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Web.Engine;

/// <summary>
/// A link annotation on a shown page: its areas in shown-page points (the page's /Rotate applied,
/// from the top-left corner) and where it goes. The description is document text: show it as text.
/// </summary>
public sealed record ViewerLink(int Page, int Index, IReadOnlyList<ViewerRect> Areas, PdfLinkTarget Target)
{
    /// <summary>The annotation as the engine read it, for exact hit testing in user space.</summary>
    internal PdfLink Source { get; init; } = null!;

    /// <summary>What the link does, for a tooltip or a screen reader: "Go to page 3", or the URL.</summary>
    public string Description => Target.Description;
}

/// <summary>A place to scroll to: a page and, where the destination gives them, a point on it in shown-page points.</summary>
public readonly record struct ViewerLocation(int Page, double? X = null, double? Y = null);

/// <summary>Hit testing and destinations of <see cref="ViewerLink"/>s, shared by the web and Android viewers.</summary>
public static class ViewerLinks
{
    /// <summary>
    /// The topmost link under <paramref name="shown"/> (shown-page points) on a page with
    /// <paramref name="geometry"/>, or null. <paramref name="slack"/> (points) widens the areas for a finger.
    /// </summary>
    public static ViewerLink? At(IReadOnlyList<ViewerLink> links, ViewerPageGeometry geometry, ViewerPoint shown, double slack = 0)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (links.Count == 0) return null;
        var (x, y) = geometry.ToUser(shown.X, shown.Y);
        var sources = new PdfLink[links.Count];
        for (int i = 0; i < links.Count; i++) sources[i] = links[i].Source;
        var hit = PdfLinkTargets.HitTest(sources, x, y, slack);
        if (hit == null) return null;
        for (int i = 0; i < links.Count; i++)
            if (ReferenceEquals(links[i].Source, hit)) return links[i];
        return null;
    }

    /// <summary>
    /// Where an internal link goes from <paramref name="currentPage"/> in a document of
    /// <paramref name="geometry"/>.Count pages: the page and, for an XYZ, FitH, FitV or FitR
    /// destination, the point on it (shown-page points). Null for a link that is not internal.
    /// </summary>
    public static ViewerLocation? Destination(PdfLinkTarget target, int currentPage, IReadOnlyList<ViewerPageGeometry> geometry)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(geometry);
        int count = geometry.Count;
        if (count == 0) return null;
        switch (target.Kind)
        {
            case PdfLinkTargetKind.NextPage: return new ViewerLocation(Math.Min(count, currentPage + 1));
            case PdfLinkTargetKind.PreviousPage: return new ViewerLocation(Math.Max(1, currentPage - 1));
            case PdfLinkTargetKind.FirstPage: return new ViewerLocation(1);
            case PdfLinkTargetKind.LastPage: return new ViewerLocation(count);
            case PdfLinkTargetKind.Page when target.PageIndex >= 0 && target.PageIndex < count:
                break;
            default:
                return null;
        }
        int page = target.PageIndex + 1;
        if (target.Left == null && target.Top == null) return new ViewerLocation(page);
        var g = geometry[page - 1];
        double left = target.Left ?? g.CropBox.X, top = target.Top ?? g.CropBox.Y + g.CropBox.Height;
        var (sx, sy) = g.ToShown(left, top);
        sx = Math.Clamp(sx, 0, g.ShownWidth);
        sy = Math.Clamp(sy, 0, g.ShownHeight);
        // A turned page shows user-space x down the screen and y across it.
        bool turned = g.Rotation is 90 or 270;
        double? x = (turned ? target.Top : target.Left) != null ? sx : null;
        double? y = (turned ? target.Left : target.Top) != null ? sy : null;
        return new ViewerLocation(page, x, y);
    }
}

/// <summary>
/// The places a reader jumped away from (by a link), newest last, for a back button or the
/// system Back key. Bounded: the oldest entries go first.
/// </summary>
public sealed class ViewerHistory<T>
{
    private readonly List<T> _entries = new();

    public ViewerHistory(int capacity = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _entries.Count;
    public bool CanGoBack => _entries.Count > 0;

    public void Push(T entry)
    {
        _entries.Add(entry);
        if (_entries.Count > Capacity) _entries.RemoveAt(0);
    }

    public bool TryPop(out T entry)
    {
        if (_entries.Count == 0)
        {
            entry = default!;
            return false;
        }
        entry = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        return true;
    }

    public void Clear() => _entries.Clear();
}
