namespace PdfViewer.Web.Engine;

/// <summary>A caret in a document: a page and a position between its characters (<see cref="ViewerTextLayout"/>).</summary>
public readonly record struct ViewerCaret(int Page, int Index) : IComparable<ViewerCaret>
{
    public int CompareTo(ViewerCaret other) => Page != other.Page ? Page.CompareTo(other.Page) : Index.CompareTo(other.Index);

    public static bool operator <(ViewerCaret a, ViewerCaret b) => a.CompareTo(b) < 0;
    public static bool operator >(ViewerCaret a, ViewerCaret b) => a.CompareTo(b) > 0;
    public static bool operator <=(ViewerCaret a, ViewerCaret b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ViewerCaret a, ViewerCaret b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// A touch selection that runs across pages, as the desktop viewer's selection continues across
/// lines, columns and pages: a long press selects a word, and the two handles move its start and
/// end caret by caret onto other pages; dragged past each other they swap, as on one page
/// (<see cref="ViewerTextSelection"/>). Each page's part follows that page's reading order
/// (<see cref="ViewerTextLayout"/>); pages in between are selected whole. The host gives each
/// page's layout before a handle reaches it (<see cref="MissingLayouts"/>). It spans at most
/// <see cref="MaxPages"/> pages.
/// </summary>
public sealed class ViewerTextSpan
{
    /// <summary>The most pages one selection spans (the text of each is kept while it is selected).</summary>
    public const int MaxPages = 200;

    private readonly Dictionary<int, ViewerTextLayout> _layouts = new();

    public ViewerTextSpan(int page, ViewerTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layouts[page] = layout;
        Start = End = new ViewerCaret(page, 0);
    }

    public ViewerCaret Start { get; private set; }
    public ViewerCaret End { get; private set; }

    /// <summary>The first and the last page the selection is on.</summary>
    public int FirstPage => Start.Page;
    public int LastPage => End.Page;

    public bool IsEmpty => Parts.Count == 0;

    /// <summary>Gives a page's layout (read on a worker thread by the host).</summary>
    public void AddLayout(int page, ViewerTextLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layouts[page] = layout;
    }

    /// <summary>A page's layout when it has been given, else null.</summary>
    public ViewerTextLayout? LayoutOf(int page) => _layouts.GetValueOrDefault(page);

    /// <summary>
    /// Whether a handle may go to <paramref name="page"/>: the selection, stretched to it, spans at
    /// most <see cref="MaxPages"/> pages.
    /// </summary>
    public bool CanReach(int page) => Math.Max(LastPage, page) - Math.Min(FirstPage, page) < MaxPages;

    /// <summary>The pages whose layouts are still needed for the selection to reach <paramref name="page"/> (it and the pages before it).</summary>
    public IReadOnlyList<int> MissingLayouts(int page)
    {
        if (!CanReach(page)) return [];
        int from = Math.Min(FirstPage, page), to = Math.Max(LastPage, page);
        var missing = new List<int>();
        for (int p = from; p <= to; p++)
            if (!_layouts.ContainsKey(p)) missing.Add(p);
        return missing;
    }

    /// <summary>Selects the word under <paramref name="p"/> on <paramref name="page"/>; false when the page has no layout or no text within <paramref name="slack"/> points.</summary>
    public bool SelectWordAt(int page, ViewerPoint p, double slack)
    {
        if (LayoutOf(page) is not { } layout) return false;
        var one = new ViewerTextSelection(page, layout);
        if (!one.SelectWordAt(p, slack)) return false;
        (Start, End) = (new ViewerCaret(page, one.Start), new ViewerCaret(page, one.End));
        return true;
    }

    /// <summary>
    /// Moves the start handle (<paramref name="startHandle"/>) or the end handle to the caret
    /// nearest <paramref name="p"/> on <paramref name="page"/>; returns whether the handles swapped.
    /// Nothing moves when a layout it needs is missing or the page is out of reach (<see cref="CanReach"/>).
    /// </summary>
    public bool MoveHandle(bool startHandle, int page, ViewerPoint p)
    {
        if (MissingLayouts(page).Count > 0 || !CanReach(page)) return false;
        var layout = _layouts[page];
        var caret = new ViewerCaret(page, layout.HitTest(p));
        bool swapped;
        if (startHandle)
        {
            if (caret < End) { Start = caret; return false; }
            (Start, End) = (End, caret);
            swapped = true;
        }
        else
        {
            if (caret > Start) { End = caret; return false; }
            (Start, End) = (caret, Start);
            swapped = true;
        }
        // At least one character stays selected.
        if (End <= Start)
        {
            int length = _layouts[End.Page].Length;
            if (End.Index < length) End = End with { Index = Start.Index + 1 };
            else Start = Start with { Index = Math.Max(0, End.Index - 1) };
        }
        return swapped;
    }

    /// <summary>Selects every page the selection is on, whole (All).</summary>
    public void SelectPages()
    {
        Start = new ViewerCaret(FirstPage, 0);
        End = new ViewerCaret(LastPage, _layouts.TryGetValue(LastPage, out var last) ? last.Length : 0);
    }

    /// <summary>Each page's part that has characters, first page first: for drawing its bands, copying and marking it up.</summary>
    public IReadOnlyList<ViewerTextSelection> Parts
    {
        get
        {
            var parts = new List<ViewerTextSelection>();
            for (int page = FirstPage; page <= LastPage; page++)
            {
                if (!_layouts.TryGetValue(page, out var layout)) continue;
                int s = page == FirstPage ? Start.Index : 0, e = page == LastPage ? End.Index : layout.Length;
                var part = new ViewerTextSelection(page, layout);
                part.Select(s, e);
                if (!part.IsEmpty) parts.Add(part);
            }
            return parts;
        }
    }

    /// <summary>
    /// The text as it reads: each page's part (<see cref="ViewerTextLayout.GetText"/>, without a line
    /// break it starts with), joined by line breaks, as the desktop viewer copies it.
    /// </summary>
    public string Text => string.Join("\n", Parts.Select(p => p.Text.TrimStart('\r', '\n')).Where(t => t.Length > 0));

    /// <summary>
    /// The start handle at the bottom-left of the first band (on the first page with a part), the end
    /// handle at the bottom-right of the last (on the last such page), in shown-page points; null when empty.
    /// </summary>
    public ((int Page, ViewerPoint Point) Start, (int Page, ViewerPoint Point) End)? HandlePositions()
    {
        var parts = Parts;
        if (parts.Count == 0) return null;
        var first = parts[0].HandlePositions();
        var last = parts[^1].HandlePositions();
        if (first is not { } f || last is not { } l) return null;
        return ((parts[0].Page, f.Start), (parts[^1].Page, l.End));
    }
}
