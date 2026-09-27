using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PdfViewer.Core.Comparison;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>A compared page: its image, and its changed words highlighted (red removed, green added, amber changed).</summary>
public sealed class ComparePageView : Grid
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(ComparePageViewModel), typeof(ComparePageView), new PropertyMetadata(null, (d, _) => ((ComparePageView)d).Rebuild()));
    public static readonly DependencyProperty DisplayWidthProperty = DependencyProperty.Register(
        nameof(DisplayWidth), typeof(double), typeof(ComparePageView), new PropertyMetadata(560.0, (d, _) => ((ComparePageView)d).Rebuild()));
    public static readonly DependencyProperty SelectedChangeProperty = DependencyProperty.Register(
        nameof(SelectedChange), typeof(int), typeof(ComparePageView), new PropertyMetadata(-1, (d, _) => ((ComparePageView)d).Restyle()));

    public ComparePageViewModel? Page { get => (ComparePageViewModel?)GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public double DisplayWidth { get => (double)GetValue(DisplayWidthProperty); set => SetValue(DisplayWidthProperty, value); }
    public int SelectedChange { get => (int)GetValue(SelectedChangeProperty); set => SetValue(SelectedChangeProperty, value); }

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _marks = new() { IsHitTestVisible = false };
    private ComparePageViewModel? _subscribed;

    public ComparePageView()
    {
        Children.Add(new Border { Background = Brushes.White, Child = _image, BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)), BorderThickness = new Thickness(1) });
        Children.Add(_marks);
        Margin = new Thickness(0, 0, 0, 12);
        HorizontalAlignment = HorizontalAlignment.Center;
        Loaded += (_, _) => _ = Page?.EnsureRenderedAsync(Dpi());
    }

    private int Dpi() => Page == null ? 96 : (int)Math.Clamp(DisplayWidth / Math.Max(Page.WidthPoints, 1) * 72 * 1.5, 48, 200);

    private void Rebuild()
    {
        if (_subscribed != null) _subscribed.PropertyChanged -= OnPageChanged;
        _subscribed = Page;
        if (Page == null) return;
        Page.PropertyChanged += OnPageChanged;
        double w = DisplayWidth, h = DisplayWidth * Page.HeightPoints / Math.Max(Page.WidthPoints, 1);
        Width = w;
        Height = h;
        _image.Source = Page.Image;
        AutomationProperties.SetName(this, $"Page {Page.PageNumber}, {Page.Highlights.Count} changed word(s)");
        _marks.Children.Clear();
        foreach (var hl in Page.Highlights)
        {
            var r = Page.ToDisplay(hl.Bounds);
            var box = new Rectangle { Width = Math.Max(2, r.Width * w), Height = Math.Max(2, r.Height * h), Tag = hl };
            Canvas.SetLeft(box, r.X * w);
            Canvas.SetTop(box, r.Y * h);
            _marks.Children.Add(box);
        }
        Restyle();
        if (IsLoaded) _ = Page.EnsureRenderedAsync(Dpi());
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComparePageViewModel.Image)) _image.Source = Page?.Image;
    }

    private void Restyle()
    {
        foreach (var child in _marks.Children)
        {
            if (child is not Rectangle { Tag: CompareHighlight hl } box) continue;
            var c = hl.Kind switch
            {
                ChangeKind.Deleted => Color.FromRgb(0xD9, 0x30, 0x25),
                ChangeKind.Inserted => Color.FromRgb(0x1E, 0x8E, 0x3E),
                _ => Color.FromRgb(0xE3, 0x74, 0x00),
            };
            bool selected = hl.ChangeIndex == SelectedChange;
            box.Fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)0x70 : (byte)0x40, c.R, c.G, c.B));
            box.Stroke = new SolidColorBrush(c);
            box.StrokeThickness = selected ? 2 : 0.75;
        }
    }
}
