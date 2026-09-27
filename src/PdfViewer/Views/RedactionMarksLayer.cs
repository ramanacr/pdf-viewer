using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// The areas marked for redaction on a page: red outlined, lightly shaded boxes, drawn over the
/// page until the marks are applied or cleared. Clicking a mark removes it.
/// </summary>
public sealed class RedactionMarksLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(RedactionMarksLayer),
        new PropertyMetadata(null, (d, e) => ((RedactionMarksLayer)d).OnPageChanged(e)));

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25)));
    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xD9, 0x30, 0x25)));

    public RedactionMarksLayer()
    {
        Background = null; // clicks between marks reach the page
        IsHitTestVisible = true;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private void OnPageChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PageViewModel old)
        {
            old.RedactionMarks.CollectionChanged -= OnMarksChanged;
            old.PropertyChanged -= OnPagePropertyChanged;
        }
        if (e.NewValue is PageViewModel page)
        {
            page.RedactionMarks.CollectionChanged += OnMarksChanged;
            page.PropertyChanged += OnPagePropertyChanged;
        }
        Rebuild();
    }

    private void OnMarksChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.UnrotatedDisplayWidth) or nameof(PageViewModel.UnrotatedDisplayHeight))
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Page is not { } page) return;
        double w = page.UnrotatedDisplayWidth, h = page.UnrotatedDisplayHeight;
        foreach (var mark in page.RedactionMarks)
        {
            var box = new Rectangle
            {
                Width = System.Math.Max(2, mark.Width * w),
                Height = System.Math.Max(2, mark.Height * h),
                Stroke = Outline,
                StrokeThickness = 1.5,
                Fill = Shade,
                Cursor = Cursors.Hand,
                ToolTip = "Marked for redaction (click to remove the mark)",
            };
            AutomationProperties.SetName(box, "Marked for redaction");
            var captured = mark;
            box.MouseLeftButtonDown += (_, e) =>
            {
                page.Owner?.RemoveRedactionMark(page.PageNumber, captured);
                e.Handled = true;
            };
            SetLeft(box, mark.X * w);
            SetTop(box, mark.Y * h);
            Children.Add(box);
        }
    }
}
