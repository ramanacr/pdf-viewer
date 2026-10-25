namespace PdfViewer.Web.Engine;

/// <summary>
/// Page thumbnails in a grid that scrolls vertically: as many columns of at least
/// <see cref="MinCellWidth"/> pixels as the width holds, every cell the same size, each page
/// fitted into its cell's picture area above a line for its number. Pure geometry, shared by the
/// Android viewer and the tests.
/// </summary>
/// <remarks>Not thread-safe: one viewer uses it from its UI thread.</remarks>
public sealed class ThumbnailGridLayout
{
    private readonly IReadOnlyList<ViewerPage> _pages;

    public ThumbnailGridLayout(IReadOnlyList<ViewerPage> pages, double minCellWidth, double gap, double labelHeight)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minCellWidth);
        _pages = pages;
        MinCellWidth = minCellWidth;
        Gap = gap;
        LabelHeight = labelHeight;
        Resize(minCellWidth + 2 * gap, 1);
    }

    public double MinCellWidth { get; }
    public double Gap { get; }
    public double LabelHeight { get; }
    public int Columns { get; private set; }
    public double CellWidth { get; private set; }
    public double CellHeight { get; private set; }
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }
    public double ScrollY { get; private set; }
    public int Rows => (_pages.Count + Columns - 1) / Columns;
    public double ContentHeight => Gap + Rows * (CellHeight + Gap);
    public double MaxScrollY => Math.Max(0, ContentHeight - ViewHeight);

    public void Resize(double viewWidth, double viewHeight)
    {
        ViewWidth = Math.Max(1, viewWidth);
        ViewHeight = Math.Max(1, viewHeight);
        Columns = Math.Max(1, (int)((ViewWidth - Gap) / (MinCellWidth + Gap)));
        CellWidth = (ViewWidth - Gap * (Columns + 1)) / Columns;
        // The tallest page shape decides the picture height, so every row lines up.
        double aspect = _pages.Count == 0 ? 1.4 : _pages.Max(p => p.Height / Math.Max(1e-6, p.Width));
        CellHeight = CellWidth * Math.Min(aspect, 2) + LabelHeight;
        ScrollTo(ScrollY);
    }

    public void ScrollTo(double y) => ScrollY = Math.Clamp(y, 0, MaxScrollY);

    public void ScrollBy(double dy) => ScrollTo(ScrollY + dy);

    /// <summary>The cell of page <paramref name="page"/> (1-based) in view pixels.</summary>
    public LayoutRect Cell(int page)
    {
        int i = page - 1, row = i / Columns, col = i % Columns;
        return new LayoutRect(Gap + col * (CellWidth + Gap), Gap + row * (CellHeight + Gap) - ScrollY, CellWidth, CellHeight);
    }

    /// <summary>The page's picture: fitted into the cell above its label, centred.</summary>
    public LayoutRect Picture(int page)
    {
        var cell = Cell(page);
        var p = _pages[page - 1];
        double areaH = cell.Height - LabelHeight;
        double s = Math.Min(cell.Width / p.Width, areaH / p.Height);
        double w = p.Width * s, h = p.Height * s;
        return new LayoutRect(cell.X + (cell.Width - w) / 2, cell.Y + (areaH - h) / 2, w, h);
    }

    /// <summary>The scale (pixels per point) to render page <paramref name="page"/>'s thumbnail at.</summary>
    public double Scale(int page) => Picture(page).Width / _pages[page - 1].Width;

    /// <summary>The pages with a cell in view, in order.</summary>
    public IEnumerable<int> VisiblePages(double extra = 0)
    {
        int first = Math.Max(0, (int)((ScrollY - extra - Gap) / (CellHeight + Gap))) * Columns;
        for (int i = first; i < _pages.Count; i++)
        {
            var c = Cell(i + 1);
            if (c.Y > ViewHeight + extra) yield break;
            if (c.Bottom >= -extra) yield return i + 1;
        }
    }

    /// <summary>The page whose cell holds the view point, or 0.</summary>
    public int PageAt(double x, double y)
    {
        double cy = y + ScrollY - Gap, cx = x - Gap;
        if (cx < 0 || cy < 0) return 0;
        int col = (int)(cx / (CellWidth + Gap)), row = (int)(cy / (CellHeight + Gap));
        if (col >= Columns || cx - col * (CellWidth + Gap) > CellWidth || cy - row * (CellHeight + Gap) > CellHeight) return 0;
        int page = row * Columns + col + 1;
        return page <= _pages.Count ? page : 0;
    }

    /// <summary>Scrolls so that page <paramref name="page"/>'s row is in view (the current page when the grid opens).</summary>
    public void Reveal(int page)
    {
        var cell = Cell(Math.Clamp(page, 1, _pages.Count));
        if (cell.Y < 0 || cell.Bottom > ViewHeight) ScrollBy(cell.Y - (ViewHeight - CellHeight) / 2);
    }
}

/// <summary>One line of an outline shown as a list: its title, depth (0 at the top level) and page (null when it goes nowhere here).</summary>
public sealed record ViewerOutlineLine(string Title, int Depth, int? Page);

public static class ViewerOutline
{
    /// <summary>The outline as an indented list, depth first; deeper levels than <paramref name="maxDepth"/> are left out.</summary>
    public static IReadOnlyList<ViewerOutlineLine> Flatten(IReadOnlyList<ViewerOutlineItem> items, int maxDepth = 16)
    {
        var lines = new List<ViewerOutlineLine>();
        void Walk(IReadOnlyList<ViewerOutlineItem> level, int depth)
        {
            if (depth > maxDepth) return;
            foreach (var item in level)
            {
                // Titles are single lines on screen: control characters (line breaks in a title) become spaces.
                string spaced = new((item.Title ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());
                string title = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                lines.Add(new ViewerOutlineLine(title.Length == 0 ? "(untitled)" : title, depth, item.Page));
                Walk(item.Children, depth + 1);
            }
        }
        Walk(items, 0);
        return lines;
    }
}
