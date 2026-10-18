using System;
using System.ComponentModel;
using System.Windows;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Create PDF from Scanner: device and settings, scanning (stoppable), then the PDF.</summary>
public partial class ScanWindow : Window
{
    private readonly ScanViewModel _vm;
    private bool _closeWhenStopped;

    public ScanWindow(ScanViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ScanViewModel.IsBusy)) return;
            CancelButton.Content = vm.IsBusy ? "Stop" : "Close";
            if (!vm.IsBusy && _closeWhenStopped) Dispatcher.BeginInvoke(Close);
        };
        Loaded += async (_, _) => await _vm.LoadDevicesAsync();
        Closed += (_, _) => _vm.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.LoadDevicesAsync();

    private async void Scan_Click(object sender, RoutedEventArgs e) => await _vm.ScanAsync();

    private void RemoveLast_Click(object sender, RoutedEventArgs e) => _vm.RemoveLastPage();

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.CreateAndOpenAsync()) Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) _vm.Cancel();
        else Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsBusy)
        {
            _closeWhenStopped = true;
            _vm.Cancel();
            e.Cancel = true;
        }
        else if (_vm.PageCount > 0 && MessageBox.Show(this, $"{_vm.PagesText} Close without creating a PDF? The scanned pages are discarded.",
                     "Create PDF from Scanner", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
