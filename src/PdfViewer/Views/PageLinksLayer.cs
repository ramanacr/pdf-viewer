using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using PdfViewer.Models;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The page's links for screen readers: one hyperlink element per link, named by where it goes
/// ("Link: Go to page 3", "Link: https://..."), that a screen reader can invoke. It draws nothing
/// and takes no mouse input: the page canvas below handles the pointer (cursor, tooltip, click).
/// </summary>
public sealed class PageLinksLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(PageLinksLayer),
        new PropertyMetadata(null, (d, e) => ((PageLinksLayer)d).OnPageChanged(e)));

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public PageLinksLayer()
    {
        Background = null;
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Rebuild();
    }

    private void OnPageChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PageViewModel old) old.PropertyChanged -= OnPagePropertyChanged;
        if (e.NewValue is PageViewModel page) page.PropertyChanged += OnPagePropertyChanged;
        Rebuild();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.UnrotatedDisplayWidth) or nameof(PageViewModel.UnrotatedDisplayHeight))
            Rebuild();
    }

    /// <summary>Gives the hotspot of <paramref name="link"/> under <paramref name="root"/> the keyboard; false when it is not laid out.</summary>
    public static bool FocusHotspot(DependencyObject root, PageLink link)
    {
        var spot = Find(root);
        return spot != null && spot.Focus();

        LinkHotspot? Find(DependencyObject node)
        {
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is LinkHotspot hot && hot.Link.PageNumber == link.PageNumber && hot.Link.Index == link.Index && hot.IsVisible) return hot;
                if (Find(child) is { } found) return found;
            }
            return null;
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Page is not { Owner: { } owner } page || !IsLoaded) return;
        double w = page.UnrotatedDisplayWidth, h = page.UnrotatedDisplayHeight;
        foreach (var link in owner.GetPageLinks(page.PageNumber))
        {
            Rect bounds = Rect.Empty;
            foreach (var area in link.Areas) bounds.Union(area);
            if (bounds.IsEmpty) continue;
            var spot = new LinkHotspot(link, owner) { Width = System.Math.Max(1, bounds.Width * w), Height = System.Math.Max(1, bounds.Height * h) };
            AutomationProperties.SetName(spot, link.AccessibleName);
            AutomationProperties.SetHelpText(spot, link.Target.IsInternal || link.Target.Kind == PdfEngine.Vector.Services.PdfLinkTargetKind.WebUri
                ? "Press Enter to follow the link." : link.Description);
            SetLeft(spot, bounds.X * w);
            SetTop(spot, bounds.Y * h);
            Children.Add(spot);
        }
    }

    /// <summary>A link's place on the page, seen by UI Automation as a hyperlink that can be invoked.</summary>
    private sealed class LinkHotspot : FrameworkElement
    {
        public LinkHotspot(PageLink link, MainViewModel owner)
        {
            Link = link;
            Document = owner;
            IsHitTestVisible = false;
            // Tab reaches it (the window's Tab cycle: the page's annotations, then its links).
            Focusable = true;
            FocusVisualStyle = null; // drawn below, turned with the page
        }

        public PageLink Link { get; }
        public MainViewModel Document { get; }

        private static readonly Pen FocusOuter = Freeze(new Pen(Brushes.White, 3));
        private static readonly Pen FocusInner = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)), 2) { DashStyle = DashStyles.Dash });

        private static Pen Freeze(Pen pen)
        {
            pen.Freeze();
            return pen;
        }

        protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnIsKeyboardFocusedChanged(e);
            InvalidateVisual();
            // Focus taken away (a click elsewhere, Tab out of the window): the link no longer has the keyboard.
            if (!IsKeyboardFocused && ReferenceEquals(Document.FocusedLink, Link)) Document.FocusedLink = null;
        }

        /// <summary>The focus rectangle: a dashed accent line on a white one, visible on any page.</summary>
        protected override void OnRender(DrawingContext dc)
        {
            if (!IsKeyboardFocused) return;
            var r = new Rect(-2, -2, ActualWidth + 4, ActualHeight + 4);
            dc.DrawRectangle(null, FocusOuter, r);
            dc.DrawRectangle(null, FocusInner, r);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(LinkHotspot owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Hyperlink;
            protected override string GetClassNameCore() => "PdfLink";
            protected override bool IsContentElementCore() => true;
            protected override bool IsControlElementCore() => true;
            protected override List<AutomationPeer>? GetChildrenCore() => null;
            protected override bool IsKeyboardFocusableCore() => ((LinkHotspot)Owner).Document.CanFollowLinks;

            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

            // A tool in use (drawing, measuring, ...) disables the links, as it does for the mouse.
            protected override bool IsEnabledCore() => ((LinkHotspot)Owner).Document.CanFollowLinks;

            public void Invoke()
            {
                var spot = (LinkHotspot)Owner;
                if (!spot.Document.ActivateLink(spot.Link)) throw new ElementNotEnabledException();
            }
        }
    }
}
