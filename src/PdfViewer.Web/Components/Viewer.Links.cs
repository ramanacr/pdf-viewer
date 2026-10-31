using Microsoft.JSInterop;
using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Links on the pages (/Link annotations), read by the engine layer that the Android app shares
/// (ViewerLinks). Each link is an element over the page: a pointer cursor and its target as a
/// tooltip, reachable with Tab and named for screen readers. With the select tool, a click (not
/// a drag, which still selects text) or Enter follows it: a place in the document is scrolled to
/// (Back or Alt+Left returns), a web link opens in a new tab after the reader agrees, and
/// anything else is never followed. Document JavaScript is never run.
/// </summary>
public sealed partial class Viewer
{
    private readonly Dictionary<int, IReadOnlyList<ViewerLink>> _linksByPage = new();
    private readonly HashSet<int> _linksLoading = new();
    private readonly ViewerHistory<ViewerLocation> _linkBack = new();
    private ViewerDocument? _linksFor;

    // The web link waiting for the reader's answer.
    private string? _linkUri;
    private string _linkHost = "";

    /// <summary>Reads the links of pages in view (once per page and document).</summary>
    private void LoadLinks(IEnumerable<int> pages)
    {
        var doc = _doc;
        if (doc == null) return;
        if (!ReferenceEquals(_linksFor, doc))
        {
            _linksByPage.Clear();
            _linksLoading.Clear();
            _linkBack.Clear();
            _linksFor = doc;
        }
        foreach (int page in pages)
        {
            if (page < 1 || page > doc.PageCount || _linksByPage.ContainsKey(page) || !_linksLoading.Add(page)) continue;
            _ = LoadLinksAsync(doc, page);
        }
    }

    private async Task LoadLinksAsync(ViewerDocument doc, int page)
    {
        IReadOnlyList<ViewerLink> links;
        try
        {
            links = await doc.GetLinksAsync(page);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            links = []; // a damaged annotation array: the page has no links
        }
        if (!ReferenceEquals(_linksFor, doc) || _doc != doc) return;
        _linksLoading.Remove(page);
        _linksByPage[page] = links;
        if (links.Count > 0) StateHasChanged();
    }

    /// <summary>Links are followed with the select tool only; the drawing tools keep their clicks.</summary>
    private bool LinksActive => _tool == Tool.Select && _editingId == null;

    private static string LinkLabel(ViewerLink link) => link.Target.Kind switch
    {
        PdfLinkTargetKind.WebUri or PdfLinkTargetKind.BlockedUri => $"Link: {link.Target.Uri}",
        _ when link.Target.IsInternal => $"Link: {link.Description}",
        _ => "Link outside the document",
    };

    /// <summary>viewer.js: a click (not a drag) or Enter on the link with this place in the page's list.</summary>
    [JSInvokable]
    public void OnLinkActivate(int page, int index)
    {
        if (_doc == null || !LinksActive || !_linksByPage.TryGetValue(page, out var links) || index < 0 || index >= links.Count) return;
        FollowLink(links[index]);
        StateHasChanged();
    }

    private void FollowLink(ViewerLink link)
    {
        var target = link.Target;
        switch (target.Kind)
        {
            case PdfLinkTargetKind.WebUri when PdfLinkTargets.TryGetWebUri(target.Uri, out var uri):
                _linkUri = uri.AbsoluteUri;
                _linkHost = PdfLinkTargets.DisplayHost(uri);
                _dialog = Dialog.Link;
                break;
            case PdfLinkTargetKind.BlockedUri:
            case PdfLinkTargetKind.WebUri:
                _status = $"This link was not opened (only web and email links open): {target.Uri}";
                break;
            case PdfLinkTargetKind.None:
                _status = target.Description;
                break;
            default:
                if (target.IsInternal && ViewerLinks.Destination(target, _currentPage, _doc!.Geometry) is { } location)
                {
                    JumpTo(location);
                    if (target.Kind == PdfLinkTargetKind.Page) _status = target.Description;
                }
                else
                {
                    _status = target.Description; // outside the document: not followed
                }
                break;
        }
    }

    /// <summary>Goes to a place by a link, remembering where the view was for Back.</summary>
    private void JumpTo(ViewerLocation location)
    {
        if (_doc == null) return;
        _linkBack.Push(CurrentLocation());
        ShowLocation(location);
    }

    private ViewerLocation CurrentLocation()
    {
        double scale = _zoom * CssPerPoint;
        var view = CanvasInterop.ViewLocation();
        if (view.Length >= 3 && view[0] >= 1 && view[0] <= (_doc?.PageCount ?? 0) && scale > 0)
            return new ViewerLocation((int)view[0], view[1] / scale, view[2] / scale);
        return new ViewerLocation(_currentPage);
    }

    private void ShowLocation(ViewerLocation location)
    {
        if (_doc == null) return;
        int page = Math.Clamp(location.Page, 1, _doc.PageCount);
        _currentPage = page;
        if (location.X == null && location.Y == null)
        {
            CanvasInterop.ScrollToPage(page, 0, smooth: false);
            return;
        }
        double scale = _zoom * CssPerPoint;
        CanvasInterop.ScrollToLocation(page, (location.X ?? 0) * scale, (location.Y ?? 0) * scale);
    }

    private bool CanGoBackByLink => _linkBack.CanGoBack;

    /// <summary>Back (the toolbar button, Alt+Left): returns to where the last link was followed from.</summary>
    private void GoBackByLink()
    {
        if (_doc == null || !_linkBack.TryPop(out var location)) return;
        ShowLocation(location);
    }

    private void OpenPendingLink()
    {
        string? uri = _linkUri;
        _linkUri = null;
        _dialog = Dialog.None;
        if (uri == null || !PdfLinkTargets.TryGetWebUri(uri, out var safe)) return;
        _status = CanvasInterop.OpenExternal(safe.AbsoluteUri) ? $"Opened {_linkHost}." : "The link could not be opened.";
    }

    private void CancelPendingLink()
    {
        _linkUri = null;
        _dialog = Dialog.None;
        _status = "The link was not opened.";
    }
}
