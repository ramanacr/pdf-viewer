using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Preflight: choose a profile, analyze, and go through the results by severity; selecting one on a page shows
/// that page with the object highlighted. The report exports as text or JSON. The summary is a live region.
/// </summary>
public partial class PreflightWindow : Window
{
    private readonly PreflightViewModel _vm;
    private readonly Action? _closed;

    public PreflightWindow(PreflightViewModel vm, Action? closed)
    {
        InitializeComponent();
        _vm = vm;
        _closed = closed;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => ProfileBox.Focus();
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnViewModelChanged;
            _closed?.Invoke();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreflightViewModel.Summary)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnViewModelChanged(sender, e));
            return;
        }
        var peer = UIElementAutomationPeer.FromElement(SummaryText) ?? UIElementAutomationPeer.CreatePeerForElement(SummaryText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RunAsync();
        FindingTree.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void FindingTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedFinding = e.NewValue as PreflightFindingItem;

    private void FindingTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedFinding is { } item)
        {
            _vm.ShowOnPage(item);
            e.Handled = true;
        }
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedFinding is { } item) _vm.ShowOnPage(item);
    }

    private void ExportText_Click(object sender, RoutedEventArgs e) => Export(json: false);

    private void ExportJson_Click(object sender, RoutedEventArgs e) => Export(json: true);

    private void Export(bool json)
    {
        if (_vm.Export(json) is not { } text) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = json ? "Export the preflight report as JSON" : "Export the preflight report as text",
            Filter = json ? "JSON files (*.json)|*.json" : "Text files (*.txt)|*.txt",
            FileName = _vm.SuggestedReportName(json),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"The report could not be saved: {ex.Message}", "Preflight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
