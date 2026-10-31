using System;
using System.Collections.Generic;
using System.Windows;
using PdfEngine.Vector.Services;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// Links on pages (/Link annotations): which link is under the pointer, and what clicking it
/// does. A link to a place in the document goes there (Back returns); a web link opens in the
/// browser after the reader agrees; anything else (another file, a program, document JavaScript)
/// is never followed.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Asks before a web link opens and opens it; replaced in tests so that no browser starts.</summary>
    public IExternalLinkOpener LinkOpener { get; set; } = new ShellLinkOpener();

    // Links per page, read once per revision of the document (a new revision has new bytes).
    private readonly Dictionary<int, IReadOnlyList<PageLink>> _pageLinks = new();
    private object? _pageLinksRevision;

    /// <summary>The page's links (none while no document is open).</summary>
    public IReadOnlyList<PageLink> GetPageLinks(int pageNumber)
    {
        if (!IsDocumentLoaded || pageNumber < 1 || pageNumber > PageCount) return Array.Empty<PageLink>();
        object? revision = (object?)_docService.CurrentBytes ?? _docService.CurrentFilePath;
        if (!ReferenceEquals(revision, _pageLinksRevision))
        {
            _pageLinks.Clear();
            _pageLinksRevision = revision;
        }
        if (!_pageLinks.TryGetValue(pageNumber, out var links))
            _pageLinks[pageNumber] = links = _docService.GetPageLinks(pageNumber);
        return links;
    }

    /// <summary>
    /// Clicking a link follows it only with no tool in use: browsing (text selection) or the hand
    /// tool. Drawing, measuring, stamping, signing, redacting and editing keep their clicks.
    /// </summary>
    public bool CanFollowLinks =>
        IsDocumentLoaded && ActiveAnnotationTool == null && !IsMeasuring && !IsPlacingStamp
        && !IsPlacingSignature && !IsMarkingRedaction && !IsEditingContent;

    /// <summary>The topmost link at a point of the page (fractions of the unrotated page from its top-left), or null.</summary>
    public PageLink? LinkAt(int pageNumber, Point normalized)
    {
        var links = GetPageLinks(pageNumber);
        for (int i = links.Count - 1; i >= 0; i--)
            if (links[i].Contains(normalized)) return links[i];
        return null;
    }

    /// <summary>
    /// Follows a link as a click on it does. Every way in (mouse, keyboard, UI Automation Invoke)
    /// comes here, so a tool in use blocks them all. True when the link was acted on.
    /// </summary>
    public bool ActivateLink(PageLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!CanFollowLinks) return false;
        var target = link.Target;
        switch (target.Kind)
        {
            case PdfLinkTargetKind.Page:
                GoToLinkTarget(link);
                break;
            case PdfLinkTargetKind.NextPage:
                NextPage();
                break;
            case PdfLinkTargetKind.PreviousPage:
                PreviousPage();
                break;
            case PdfLinkTargetKind.FirstPage:
                FirstPage();
                break;
            case PdfLinkTargetKind.LastPage:
                LastPage();
                break;
            case PdfLinkTargetKind.WebUri:
                OpenWebLink(target.Uri ?? string.Empty);
                break;
            case PdfLinkTargetKind.BlockedUri:
                StatusText = $"This link was not opened: only web (http, https) and email links open. It points to: {target.Uri}";
                ShowAlert($"This link was not opened. Only web (http, https) and email (mailto) links open from a document.\n\nIt points to:\n{target.Uri}",
                    "Link not opened", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case PdfLinkTargetKind.External:
                StatusText = target.Description;
                break;
            default:
                StatusText = target.Description;
                break;
        }
        return true;
    }

    private void GoToLinkTarget(PageLink link)
    {
        int page = link.Target.PageIndex + 1;
        if (page < 1 || page > PageCount) return;
        if (link.TargetPoint is not { } p || (double.IsNaN(p.X) && double.IsNaN(p.Y)))
        {
            NavigateTo(new ViewLocation(page), recordHistory: true);
            return;
        }
        // The destination is in the page as stored; the view scrolls the page as shown (turned).
        var shown = Pages.Count >= page ? Pages[page - 1] : null;
        double x = double.IsNaN(p.X) ? 0 : p.X, y = double.IsNaN(p.Y) ? 0 : p.Y;
        var (dx, dy) = shown?.ToDisplayNormalized(x, y) ?? (x, y);
        int turn = shown == null ? 0 : ((int)Math.Round(shown.OverlayRotationAngle / 90.0) % 4 + 4) % 4;
        bool turned = turn is 1 or 3;
        bool hasX = !double.IsNaN(turned ? p.Y : p.X), hasY = !double.IsNaN(turned ? p.X : p.Y);
        NavigateTo(new ViewLocation(page, hasX ? dx : null, hasY ? dy : 0), recordHistory: true);
        StatusText = link.Description;
    }

    private void OpenWebLink(string text)
    {
        if (!PdfLinkTargets.TryGetWebUri(text, out var uri))
        {
            StatusText = $"This link was not opened: {text}";
            return;
        }
        if (!LinkOpener.Confirm(uri))
        {
            StatusText = "The link was not opened.";
            return;
        }
        try
        {
            LinkOpener.Open(uri);
            StatusText = $"Opened {PdfLinkTargets.DisplayHost(uri)} in your browser.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = $"The link could not be opened: {ex.Message}";
        }
    }
}
