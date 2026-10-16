using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Lists the open document's accessibility issues by category. Selecting an issue on a page shows
/// that page with the element highlighted; the summary is a live region, so screen readers announce
/// each check's result.
/// </summary>
public partial class AccessibilityCheckWindow : Window
{
    private readonly AccessibilityCheckViewModel _vm;
    private readonly Action? _closed;

    public AccessibilityCheckWindow(AccessibilityCheckViewModel vm, Action? closed)
    {
        InitializeComponent();
        _vm = vm;
        _closed = closed;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        Loaded += async (_, _) =>
        {
            IssueTree.Focus();
            await _vm.CheckAsync();
        };
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnViewModelChanged;
            _closed?.Invoke();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccessibilityCheckViewModel.Summary)) return;
        // WPF announces a live region only when told that it changed.
        var peer = UIElementAutomationPeer.FromElement(SummaryText) ?? UIElementAutomationPeer.CreatePeerForElement(SummaryText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await _vm.CheckAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void IssueTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedIssue = e.NewValue as AccessibilityIssueItem;

    /// <summary>Enter shows the selected issue on its page again (after scrolling away, say).</summary>
    private void IssueTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedIssue is { } issue)
        {
            _vm.ShowOnPage(issue);
            e.Handled = true;
        }
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedIssue is { } issue) _vm.ShowOnPage(issue);
    }
}
