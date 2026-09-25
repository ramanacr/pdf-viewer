using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// Marks the element that shows one page, so the window can work out which part of each page is
/// visible and ask for a viewport detail tile at device resolution (Direct2D host).
/// </summary>
public static class PageDetailHost
{
    public static readonly DependencyProperty IsHostProperty = DependencyProperty.RegisterAttached(
        "IsHost", typeof(bool), typeof(PageDetailHost), new PropertyMetadata(false, OnIsHostChanged));

    public static bool GetIsHost(DependencyObject element) => (bool)element.GetValue(IsHostProperty);
    public static void SetIsHost(DependencyObject element, bool value) => element.SetValue(IsHostProperty, value);

    private static readonly List<WeakReference<FrameworkElement>> Hosts = new();

    private static void OnIsHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;
        lock (Hosts)
        {
            Hosts.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, element));
            if ((bool)e.NewValue)
                Hosts.Add(new WeakReference<FrameworkElement>(element));
        }
    }

    /// <summary>
    /// Page hosts inside <paramref name="viewport"/> with the part of each page that is visible,
    /// in the page element's own coordinates (DIPs, displayed orientation).
    /// </summary>
    public static IEnumerable<(PageViewModel Page, Rect Visible)> VisiblePages(FrameworkElement viewport, Size viewportSize)
    {
        List<FrameworkElement> hosts;
        lock (Hosts)
        {
            Hosts.RemoveAll(w => !w.TryGetTarget(out _));
            hosts = new List<FrameworkElement>(Hosts.Count);
            foreach (var w in Hosts)
                if (w.TryGetTarget(out var h)) hosts.Add(h);
        }

        var viewRect = new Rect(0, 0, viewportSize.Width, viewportSize.Height);
        foreach (var host in hosts)
        {
            if (host.DataContext is not PageViewModel page || !host.IsVisible || host.ActualWidth <= 0 || !host.IsDescendantOf(viewport))
                continue;

            GeneralTransform toViewport;
            try
            {
                toViewport = host.TransformToAncestor(viewport);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var bounds = toViewport.TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            var visible = Rect.Intersect(bounds, viewRect);
            if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1)
                continue;

            if (toViewport.Inverse is not GeneralTransform fromViewport)
                continue;
            var local = fromViewport.TransformBounds(visible);
            yield return (page, Rect.Intersect(local, new Rect(0, 0, host.ActualWidth, host.ActualHeight)));
        }
    }
}
