using System.Collections.Generic;
using System.Windows;
using PdfEngine.Geometry;
using PdfEngine.Vector.Services;

namespace PdfViewer.Models;

/// <summary>
/// A link annotation on a page, in the viewer's overlay space: fractions of the unrotated page
/// (the crop box as the PDF stores it) from its top-left corner, the space annotations and text
/// boxes use. Hit testing goes back to user space and uses the engine's rules (/QuadPoints when
/// usable, else /Rect), shared with the web and Android viewers.
/// </summary>
public sealed class PageLink
{
    private readonly PdfLink _link;
    private readonly PdfRect _crop;

    public PageLink(int pageNumber, PdfLink link, PdfLinkTarget target, PdfRect cropBox, IReadOnlyList<Rect> areas, Point? targetPoint)
    {
        PageNumber = pageNumber;
        _link = link;
        _crop = cropBox;
        Target = target;
        Areas = areas;
        TargetPoint = targetPoint;
    }

    public int PageNumber { get; }

    /// <summary>The annotation's position in the page's /Annots array.</summary>
    public int Index => _link.Index;

    public PdfLinkTarget Target { get; }

    /// <summary>The link's areas (its quadrilaterals' bounds, or its rectangle) as page fractions.</summary>
    public IReadOnlyList<Rect> Areas { get; }

    /// <summary>
    /// For a destination with a position (XYZ, FitH, FitV, FitR), that position on the target page
    /// as fractions of its unrotated page; NaN for a coordinate the destination leaves unchanged.
    /// </summary>
    public Point? TargetPoint { get; }

    /// <summary>"Go to page 3", the URL, or why the link is not followed: document text, shown as text.</summary>
    public string Description => Target.Description;

    /// <summary>The name a screen reader announces.</summary>
    public string AccessibleName => Target.Kind switch
    {
        PdfLinkTargetKind.WebUri or PdfLinkTargetKind.BlockedUri => $"Link: {Target.Uri}",
        _ when Target.IsInternal => $"Link: {Description}",
        _ => "Link outside the document",
    };

    /// <summary>True when the point (page fractions from the top-left of the unrotated page) is on the link.</summary>
    public bool Contains(Point normalized)
    {
        double w = _crop.Width > 0 ? _crop.Width : 612, h = _crop.Height > 0 ? _crop.Height : 792;
        return PdfLinkTargets.Contains(_link, _crop.Left + normalized.X * w, _crop.Top + (1.0 - normalized.Y) * h);
    }
}
