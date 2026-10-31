using PdfEngine.Geometry;

namespace PdfEngine.Vector.Services;

/// <summary>What a viewer does when a link is activated.</summary>
public enum PdfLinkTargetKind
{
    /// <summary>The link goes nowhere this viewer can show (no destination, or a page not in this document).</summary>
    None,

    /// <summary>A page of this document (an explicit or named destination, or a go-to action).</summary>
    Page,

    /// <summary>The Named actions NextPage, PrevPage, FirstPage and LastPage.</summary>
    NextPage,
    PreviousPage,
    FirstPage,
    LastPage,

    /// <summary>An http, https or mailto URI: opened in the system browser after the reader agrees.</summary>
    WebUri,

    /// <summary>A URI with another scheme (file:, javascript:, a custom protocol...): shown, never opened.</summary>
    BlockedUri,

    /// <summary>
    /// Anything else (another file, a launch, an embedded file, document JavaScript, another named
    /// action...): never followed.
    /// </summary>
    External,
}

/// <summary>
/// Where a link goes, in the terms a viewer acts on. <see cref="Left"/> and <see cref="Top"/> are
/// the destination's view corner in the target page's default user space (null where the
/// destination leaves it unchanged or fits the page). Text values come from the document and are
/// untrusted: show them as text, never as markup.
/// </summary>
public sealed record PdfLinkTarget(PdfLinkTargetKind Kind, string Description)
{
    /// <summary>The 0-based page of a <see cref="PdfLinkTargetKind.Page"/> target, else -1.</summary>
    public int PageIndex { get; init; } = -1;

    public double? Left { get; init; }
    public double? Top { get; init; }

    /// <summary>The URI of a <see cref="PdfLinkTargetKind.WebUri"/> or <see cref="PdfLinkTargetKind.BlockedUri"/> target.</summary>
    public string? Uri { get; init; }

    /// <summary>The action type (/S) of the link's action, when it has one.</summary>
    public string? ActionType { get; init; }

    /// <summary>True when the viewer follows the link inside the document (a page or a named page action).</summary>
    public bool IsInternal => Kind is PdfLinkTargetKind.Page or PdfLinkTargetKind.NextPage or PdfLinkTargetKind.PreviousPage
        or PdfLinkTargetKind.FirstPage or PdfLinkTargetKind.LastPage;
}

/// <summary>
/// Link annotations for a viewer: where a link goes (<see cref="Resolve(PdfLink, int)"/>), which
/// link is under a point (<see cref="HitTest"/>), and which URIs may be handed to a browser
/// (<see cref="TryGetWebUri"/>). Shared by the desktop, web and Android viewers. Nothing here runs
/// or opens anything: the viewer decides, and never runs document JavaScript.
/// </summary>
public static class PdfLinkTargets
{
    /// <summary>The message shown for a link that is not followed because it leaves the document.</summary>
    public const string OutsideMessage = "This link points outside the document and was not followed.";

    /// <summary>The longest URI handed to a browser; a longer one is shown and not opened.</summary>
    public const int MaxUriLength = 8192;

    /// <summary>Where <paramref name="link"/> goes in a document of <paramref name="pageCount"/> pages.</summary>
    public static PdfLinkTarget Resolve(PdfLink link, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Resolve(link.Destination, link.Action, pageCount);
    }

    /// <summary>Where a destination (/Dest) or, when there is none, an action (/A) goes.</summary>
    public static PdfLinkTarget Resolve(PdfDestination? destination, PdfAction? action, int pageCount)
    {
        if (destination != null) return ToPage(destination, pageCount, null);
        if (action == null) return new PdfLinkTarget(PdfLinkTargetKind.None, "This link has no destination.");
        string type = action.Type;
        switch (action.Kind)
        {
            case PdfActionKind.GoTo:
                return action.Destination is { } d
                    ? ToPage(d, pageCount, type)
                    : new PdfLinkTarget(PdfLinkTargetKind.None, "This link goes to a place that is not in this document.") { ActionType = type };
            case PdfActionKind.Named:
                return action.Name switch
                {
                    "NextPage" => new PdfLinkTarget(PdfLinkTargetKind.NextPage, "Go to the next page") { ActionType = type },
                    "PrevPage" => new PdfLinkTarget(PdfLinkTargetKind.PreviousPage, "Go to the previous page") { ActionType = type },
                    "FirstPage" => new PdfLinkTarget(PdfLinkTargetKind.FirstPage, "Go to the first page") { ActionType = type },
                    "LastPage" => new PdfLinkTarget(PdfLinkTargetKind.LastPage, "Go to the last page") { ActionType = type },
                    _ => new PdfLinkTarget(PdfLinkTargetKind.External, OutsideMessage) { ActionType = type },
                };
            case PdfActionKind.Uri:
            {
                string uri = action.Uri ?? string.Empty;
                return TryGetWebUri(uri, out _)
                    ? new PdfLinkTarget(PdfLinkTargetKind.WebUri, uri.Trim()) { Uri = uri.Trim(), ActionType = type }
                    : new PdfLinkTarget(PdfLinkTargetKind.BlockedUri, uri.Trim()) { Uri = uri.Trim(), ActionType = type };
            }
            case PdfActionKind.JavaScript:
                return new PdfLinkTarget(PdfLinkTargetKind.External, "This link would run a script. Document JavaScript is never run.") { ActionType = type };
            default:
                return new PdfLinkTarget(PdfLinkTargetKind.External, OutsideMessage) { ActionType = type };
        }
    }

    private static PdfLinkTarget ToPage(PdfDestination d, int pageCount, string? actionType)
    {
        if (d.PageIndex < 0 || d.PageIndex >= pageCount)
            return new PdfLinkTarget(PdfLinkTargetKind.None, "This link goes to a page that is not in this document.") { ActionType = actionType };
        double? P(int i) => i < d.Parameters.Count && d.Parameters[i] is { } v && double.IsFinite(v) ? v : null;
        var (left, top) = d.Fit switch
        {
            PdfDestinationFit.XYZ => (P(0), P(1)),
            PdfDestinationFit.FitH or PdfDestinationFit.FitBH => ((double?)null, P(0)),
            PdfDestinationFit.FitV or PdfDestinationFit.FitBV => (P(0), (double?)null),
            PdfDestinationFit.FitR => (P(0), P(3) is { } t && P(1) is { } b ? Math.Max(t, b) : P(3) ?? P(1)),
            _ => ((double?)null, (double?)null),
        };
        return new PdfLinkTarget(PdfLinkTargetKind.Page, $"Go to page {d.PageIndex + 1}")
        {
            PageIndex = d.PageIndex,
            Left = left,
            Top = top,
            ActionType = actionType,
        };
    }

    /// <summary>
    /// True when <paramref name="text"/> is an absolute http, https or mailto URI that a viewer may
    /// offer to open in the system browser (after asking). Everything else is only shown.
    /// </summary>
    public static bool TryGetWebUri(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (trimmed.Length > MaxUriLength) return false;
        foreach (char c in trimmed)
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return false; // a URI has no spaces or control characters
        if (!System.Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme == System.Uri.UriSchemeMailto)
        {
            uri = parsed;
            return true;
        }
        if (parsed.Scheme != System.Uri.UriSchemeHttp && parsed.Scheme != System.Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host) || parsed.IsUnc || parsed.IsFile) return false;
        uri = parsed;
        return true;
    }

    /// <summary>
    /// The host a reader should check before opening <paramref name="uri"/>: the host name, with
    /// its punycode form when it has non-ASCII letters (which can look like another site's); the
    /// address of a mailto link.
    /// </summary>
    public static string DisplayHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme == System.Uri.UriSchemeMailto)
        {
            // The addresses before any ?subject=... part, as written.
            string text = uri.OriginalString;
            string address = text[(text.IndexOf(':') + 1)..];
            int query = address.IndexOf('?');
            if (query >= 0) address = address[..query];
            address = System.Uri.UnescapeDataString(address).Trim();
            return address.Length > 0 ? address : "(no address)";
        }
        string host = uri.Host;
        string idn = uri.IdnHost;
        return string.Equals(host, idn, StringComparison.OrdinalIgnoreCase) ? host : $"{host} ({idn})";
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>
    /// The topmost link at (<paramref name="x"/>, <paramref name="y"/>) in the page's default user
    /// space, or null. Later annotations are drawn over earlier ones, so the last match wins.
    /// <paramref name="slack"/> (in points) widens the areas, for a finger.
    /// </summary>
    public static PdfLink? HitTest(IReadOnlyList<PdfLink> links, double x, double y, double slack = 0)
    {
        ArgumentNullException.ThrowIfNull(links);
        for (int i = links.Count - 1; i >= 0; i--)
            if (Contains(links[i], x, y, 0)) return links[i];
        if (slack <= 0) return null;
        for (int i = links.Count - 1; i >= 0; i--)
            if (Contains(links[i], x, y, slack)) return links[i];
        return null;
    }

    /// <summary>
    /// True when the point is on the link: in one of its quadrilaterals when it has usable
    /// /QuadPoints, else in its /Rect.
    /// </summary>
    public static bool Contains(PdfLink link, double x, double y, double slack = 0)
    {
        ArgumentNullException.ThrowIfNull(link);
        var quads = UsableQuads(link);
        if (quads.Count == 0)
            return !link.Rect.IsEmpty && Inflate(link.Rect, slack).Contains(x, y);
        foreach (var q in quads)
        {
            if (InQuad(q, x, y)) return true;
            if (slack > 0 && Inflate(QuadBounds(q), slack).Contains(x, y)) return true;
        }
        return false;
    }

    /// <summary>
    /// The link's areas in default user space: its quadrilaterals' bounds when it has usable
    /// /QuadPoints, else its /Rect (none when the rectangle is empty).
    /// </summary>
    public static IReadOnlyList<PdfRect> Areas(PdfLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var quads = UsableQuads(link);
        if (quads.Count == 0) return link.Rect.IsEmpty ? Array.Empty<PdfRect>() : new[] { link.Rect };
        return quads.Select(QuadBounds).ToArray();
    }

    /// <summary>
    /// The quadrilaterals a reader uses: /QuadPoints are ignored when any point lies outside /Rect
    /// (ISO 32000-2, Table 176), with a point of tolerance for rounding.
    /// </summary>
    internal static IReadOnlyList<double[]> UsableQuads(PdfLink link)
    {
        if (link.QuadPoints.Count == 0 || link.Rect.IsEmpty) return Array.Empty<double[]>();
        var bounds = Inflate(link.Rect, 1);
        foreach (var q in link.QuadPoints)
        {
            if (q.Length < 8) return Array.Empty<double[]>();
            for (int i = 0; i < 8; i += 2)
                if (!double.IsFinite(q[i]) || !double.IsFinite(q[i + 1]) || !bounds.Contains(q[i], q[i + 1])) return Array.Empty<double[]>();
        }
        return link.QuadPoints;
    }

    /// <summary>
    /// In the convex hull of the four points, whatever order the writer listed them in (the
    /// standard says counter-clockwise, most writers use top-left, top-right, bottom-left, bottom-right):
    /// the hull is the union of the four triangles the points make.
    /// </summary>
    private static bool InQuad(double[] q, double x, double y) =>
        InTriangle(q[0], q[1], q[2], q[3], q[4], q[5], x, y) ||
        InTriangle(q[0], q[1], q[2], q[3], q[6], q[7], x, y) ||
        InTriangle(q[0], q[1], q[4], q[5], q[6], q[7], x, y) ||
        InTriangle(q[2], q[3], q[4], q[5], q[6], q[7], x, y);

    private static bool InTriangle(double ax, double ay, double bx, double by, double cx, double cy, double x, double y)
    {
        if (Math.Abs(Cross(ax, ay, bx, by, cx, cy)) < 1e-9) return false; // no area: three points on a line
        double d1 = Cross(ax, ay, bx, by, x, y), d2 = Cross(bx, by, cx, cy, x, y), d3 = Cross(cx, cy, ax, ay, x, y);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    private static double Cross(double ax, double ay, double bx, double by, double x, double y) => (bx - ax) * (y - ay) - (by - ay) * (x - ax);

    private static PdfRect QuadBounds(double[] q)
    {
        double x0 = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), x1 = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
        double y0 = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), y1 = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static PdfRect Inflate(PdfRect r, double by) => by <= 0 ? r : new PdfRect(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
