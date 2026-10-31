using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfViewer.Models;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// Links on the pages: a hand cursor and a tooltip with the target over a link, and a click that
/// follows it. A press that turns into a drag selects text (or pans, with the hand tool) instead;
/// a link is followed only on a click with no tool in use. The view's place is handed to the
/// view model so that Back returns to it exactly.
/// </summary>
public partial class MainWindow
{
    private PageLink? _pendingLinkClick;
    private Point _pendingLinkStart;
    private readonly ToolTip _linkToolTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private Canvas? _linkToolTipCanvas;

    /// <summary>The link under the pointer on a page canvas, when links can be followed now.</summary>
    private PageLink? LinkUnder(Canvas canvas, PageViewModel page, MouseEventArgs e)
    {
        if (!_vm.CanFollowLinks || page.UnrotatedDisplayWidth <= 0 || page.UnrotatedDisplayHeight <= 0) return null;
        var p = e.GetPosition(canvas);
        return _vm.LinkAt(page.PageNumber, new Point(p.X / page.UnrotatedDisplayWidth, p.Y / page.UnrotatedDisplayHeight));
    }

    /// <summary>The page canvas under the pointer (the hand tool's press is taken before the canvas sees it).</summary>
    private (Canvas Canvas, PageViewModel Page)? PageCanvasUnder(MouseEventArgs e)
    {
        (Canvas, PageViewModel)? found = null;
        VisualTreeHelper.HitTest(DocumentScrollViewer, null, result =>
        {
            for (DependencyObject? node = result.VisualHit; node != null && !ReferenceEquals(node, DocumentScrollViewer);
                 node = node is Visual ? VisualTreeHelper.GetParent(node) : null)
            {
                if (node is Canvas { Tag: PageViewModel page } canvas)
                {
                    found = (canvas, page);
                    return HitTestResultBehavior.Stop;
                }
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(e.GetPosition(DocumentScrollViewer)));
        if (found is { } f) return f;
        // The canvas sits under the page's other layers: find it beside the element hit.
        var hit = DocumentScrollViewer.InputHitTest(e.GetPosition(DocumentScrollViewer)) as DependencyObject;
        for (var node = hit; node != null && !ReferenceEquals(node, DocumentScrollViewer); node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: PageViewModel page } element && FindPageCanvas(element, page) is { } canvas)
                return (canvas, page);
        }
        return null;
    }

    /// <summary>Remembers a press on a link; it is followed on release if it did not become a drag.</summary>
    private void NoteLinkPress(PageLink? link, Point start)
    {
        _pendingLinkClick = link;
        _pendingLinkStart = start;
    }

    /// <summary>A drag of more than a few pixels is a selection or a pan, not a click.</summary>
    private void CancelLinkPressIfDragged(Point current)
    {
        if (_pendingLinkClick != null && (Math.Abs(current.X - _pendingLinkStart.X) > 4 || Math.Abs(current.Y - _pendingLinkStart.Y) > 4))
            _pendingLinkClick = null;
    }

    /// <summary>Follows the pressed link, if the press was a click; true when it did.</summary>
    private bool FollowPendingLink()
    {
        if (_pendingLinkClick is not { } link) return false;
        _pendingLinkClick = null;
        HideLinkToolTip();
        _vm.ClearSelection();
        _vm.ActivateLink(link);
        return true;
    }

    /// <summary>Hand cursor and tooltip over a link; true when the pointer is over one.</summary>
    private bool UpdateLinkHover(Canvas canvas, PageViewModel page, MouseEventArgs e)
    {
        if (LinkUnder(canvas, page, e) is not { } link)
        {
            HideLinkToolTip();
            return false;
        }
        canvas.Cursor = Cursors.Hand;
        if (!ReferenceEquals(_linkToolTipCanvas, canvas))
        {
            HideLinkToolTip();
            _linkToolTipCanvas = canvas;
            canvas.ToolTip = _linkToolTip;
        }
        if (!Equals(_linkToolTip.Content, link.Description)) _linkToolTip.Content = link.Description;
        return true;
    }

    private void HideLinkToolTip()
    {
        if (_linkToolTipCanvas == null) return;
        _linkToolTip.IsOpen = false;
        _linkToolTipCanvas.ToolTip = null;
        _linkToolTipCanvas = null;
    }

    // ------------------------------------------------------------------ the view's place, for Back

    /// <summary>The current page and the point of it at the view's top-left corner (shown-page fractions).</summary>
    private (int Page, double X, double Y)? CaptureView()
    {
        if (_vm.Pages.Count == 0) return null;
        int number = Math.Clamp(_vm.CurrentPageNumber, 1, _vm.Pages.Count);
        var page = _vm.Pages[number - 1];
        if (page.DisplayHeight <= 0 || page.DisplayWidth <= 0) return null;
        double top = _vm.IsMultiPageLayout ? _vm.GetPageOffset(number) : 0;
        double y = (DocumentScrollViewer.VerticalOffset - top) / page.DisplayHeight;
        double x = DocumentScrollViewer.HorizontalOffset / page.DisplayWidth;
        return (number, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    /// <summary>Scrolls so that a point of a page (shown-page fractions) is at the view's top-left corner.</summary>
    private void ScrollToLocation(int pageNumber, double? normX, double normY)
    {
        if (pageNumber < 1 || pageNumber > _vm.Pages.Count) return;
        if (_vm.IsMultiPageLayout)
        {
            Apply();
        }
        else
        {
            // The single page view shows the new page once it is laid out.
            Dispatcher.BeginInvoke(Apply, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        ScrollThumbnailIntoView(pageNumber);

        void Apply()
        {
            var page = _vm.Pages[pageNumber - 1];
            double top = (_vm.IsMultiPageLayout ? _vm.GetPageOffset(pageNumber) : 0) + Math.Clamp(normY, 0, 1) * page.DisplayHeight;
            DocumentScrollViewer.ScrollToVerticalOffset(Math.Max(0, top));
            if (normX is { } x) DocumentScrollViewer.ScrollToHorizontalOffset(Math.Max(0, Math.Clamp(x, 0, 1) * page.DisplayWidth));
            if (_vm.IsMultiPageLayout) _vm.RenderPagesInViewport(Math.Max(0, top), DocumentScrollViewer.ViewportHeight);
        }
    }
}
