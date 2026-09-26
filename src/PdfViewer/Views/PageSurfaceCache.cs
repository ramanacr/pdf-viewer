using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PdfViewer.Views;

/// <summary>
/// Keeps a live vector page cheap to scroll: while the page fits in a GPU texture, WPF caches its
/// rendering at the current size (<see cref="BitmapCache"/> at scale 1) and only re-renders the
/// vectors when the size changes (zoom). Larger pages draw live, which measured at 60 fps on the
/// same content (eng/vectorpdf/tools/VectorPdf.Tool "live").
/// </summary>
/// <remarks>
/// Only drawing sources are cached; bitmaps are already textures. The cache is rebuilt at the new
/// size on zoom, so it never scales a stale raster.
/// </remarks>
public static class PageSurfaceCache
{
    /// <summary>Largest cached side, in device pixels; beyond it textures get expensive or unsupported.</summary>
    public const double MaxCachedPixels = 4096;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(PageSurfaceCache), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static readonly DependencyPropertyDescriptor? SourceDescriptor =
        DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        if ((bool)e.NewValue)
        {
            image.SizeChanged += OnSizeChanged;
            SourceDescriptor?.AddValueChanged(image, OnSourceChanged);
            Update(image);
        }
        else
        {
            image.SizeChanged -= OnSizeChanged;
            SourceDescriptor?.RemoveValueChanged(image, OnSourceChanged);
            image.CacheMode = null;
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((Image)sender);

    private static void OnSourceChanged(object? sender, EventArgs e)
    {
        if (sender is Image image) Update(image);
    }

    /// <summary>Exposed for tests: the cache mode the rule chooses for a source and size.</summary>
    public static CacheMode? Choose(ImageSource? source, double width, double height, double dpiScale)
    {
        if (source is not DrawingImage || width <= 0 || height <= 0)
            return null;
        return Math.Max(width, height) * dpiScale <= MaxCachedPixels
            ? new BitmapCache { RenderAtScale = 1, SnapsToDevicePixels = false }
            : null;
    }

    private static void Update(Image image)
    {
        double scale = VisualTreeHelper.GetDpi(image).DpiScaleX;
        var chosen = Choose(image.Source, image.ActualWidth, image.ActualHeight, scale);
        // Keep an existing cache object when the decision does not change: a new instance would
        // force a re-render even though the size changed only by layout rounding.
        if ((chosen == null) != (image.CacheMode == null))
            image.CacheMode = chosen;
    }
}
