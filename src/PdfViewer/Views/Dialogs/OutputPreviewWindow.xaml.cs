using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Output Preview: the page separated by the engine, beside the plate checklist, the overprint and total ink
/// options and the ink readout under the mouse. A window of its own, so the main view is never changed.
/// </summary>
public partial class OutputPreviewWindow : Window
{
    private readonly OutputPreviewViewModel _vm;

    public OutputPreviewWindow(OutputPreviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        Loaded += async (_, _) =>
        {
            PlateList.Focus();
            await _vm.RenderAsync();
        };
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnViewModelChanged;
            vm.Dispose();
        };
    }

    /// <summary>Follows the page shown in the main window.</summary>
    public void ShowPage(int page)
    {
        if (page != _vm.PageNumber) _vm.PageNumber = page;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OutputPreviewViewModel.Summary)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnViewModelChanged(sender, e));
            return;
        }
        var peer = UIElementAutomationPeer.FromElement(SummaryText) ?? UIElementAutomationPeer.CreatePeerForElement(SummaryText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_vm.Preview is not { } bitmap) return;
        var p = e.GetPosition(PreviewImage);
        // The image is shown at its pixel size (scaled down by the Viewbox), so element units are pixels.
        _vm.ReadAt((int)(p.X * bitmap.PixelWidth / Math.Max(1, PreviewImage.ActualWidth)), (int)(p.Y * bitmap.PixelHeight / Math.Max(1, PreviewImage.ActualHeight)));
    }

    private void Preview_MouseLeave(object sender, MouseEventArgs e) => _vm.ClearReadout();

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.PageNumber > 1) _vm.PageNumber--;
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.PageNumber < _vm.PageCount) _vm.PageNumber++;
    }

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PageBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
