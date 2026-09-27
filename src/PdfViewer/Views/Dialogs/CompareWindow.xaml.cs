using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfViewer.Core.Comparison;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Two documents side by side with their differences highlighted and listed.</summary>
public partial class CompareWindow : Window
{
    private readonly CompareViewModel _vm;
    private bool _syncing;

    public CompareWindow(CompareViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(+1);

    private void Step(int delta)
    {
        if (_vm.Changes.Count == 0) return;
        int i = _vm.SelectedChange == null ? (delta > 0 ? 0 : _vm.Changes.Count - 1)
            : Math.Clamp(_vm.SelectedChange.Index + delta, 0, _vm.Changes.Count - 1);
        _vm.SelectedChange = _vm.Changes[i];
        ChangeList.ScrollIntoView(_vm.SelectedChange);
    }

    /// <summary>Brings the change into view on both sides.</summary>
    private void ChangeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.SelectedChange is not { } item) return;
        _syncing = true;
        try
        {
            var c = item.Change;
            if (c.Old.Count > 0) ScrollTo(OldScroll, OldItems, c.Old[0].PageNumber, c.Old[0].Y);
            if (c.New.Count > 0) ScrollTo(NewScroll, NewItems, c.New[0].PageNumber, c.New[0].Y);
            // A change on one side only: the other side goes to the matching place near it.
            if (c.Old.Count == 0 && c.New.Count > 0) ScrollTo(OldScroll, OldItems, Math.Min(c.New[0].PageNumber, _vm.OldPages.Count), c.New[0].Y);
            if (c.New.Count == 0 && c.Old.Count > 0) ScrollTo(NewScroll, NewItems, Math.Min(c.Old[0].PageNumber, _vm.NewPages.Count), c.Old[0].Y);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void ScrollTo(ScrollViewer scroll, ItemsControl items, int page, double y)
    {
        if (page < 1) return;
        items.UpdateLayout();
        if (items.ItemContainerGenerator.ContainerFromIndex(page - 1) is FrameworkElement container)
        {
            var origin = container.TransformToAncestor(scroll).Transform(new Point(0, 0));
            double target = scroll.VerticalOffset + origin.Y + y * container.ActualHeight - scroll.ViewportHeight / 3;
            scroll.ScrollToVerticalOffset(Math.Max(0, target));
        }
    }

    private void OldScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => Sync(OldScroll, NewScroll, e);
    private void NewScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => Sync(NewScroll, OldScroll, e);

    /// <summary>Scrolling one side moves the other to the same relative place.</summary>
    private void Sync(ScrollViewer from, ScrollViewer to, ScrollChangedEventArgs e)
    {
        if (_syncing || SyncCheck.IsChecked != true || e.VerticalChange == 0 || from.ScrollableHeight <= 0) return;
        _syncing = true;
        try { to.ScrollToVerticalOffset(from.VerticalOffset / from.ScrollableHeight * to.ScrollableHeight); }
        finally { _syncing = false; }
    }
}
