using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Tags the open document (Make Accessible) and saves the copy under a new name; then lists what
/// was tagged, what to review and what still fails, and offers to open the copy and check it. The
/// summary is a live region, so screen readers announce the result.
/// </summary>
public partial class MakeAccessibleWindow : Window
{
    private readonly MakeAccessibleViewModel _vm;
    private readonly Func<string, Task>? _openAndCheck;

    /// <param name="openAndCheck">Opens a file and runs the accessibility check on it (null hides the offer).</param>
    public MakeAccessibleWindow(MakeAccessibleViewModel vm, Func<string, Task>? openAndCheck)
    {
        InitializeComponent();
        _vm = vm;
        _openAndCheck = openAndCheck;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        Loaded += async (_, _) =>
        {
            LanguageBox.Focus();
            LanguageBox.SelectAll();
            await _vm.LoadAsync();
        };
        Closed += (_, _) => vm.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MakeAccessibleViewModel.Summary)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnViewModelChanged(sender, e));
            return;
        }
        // WPF announces a live region only when told that it changed.
        var peer = UIElementAutomationPeer.FromElement(SummaryText) ?? UIElementAutomationPeer.CreatePeerForElement(SummaryText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the tagged copy",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = System.IO.Path.GetFileName(_vm.SuggestedOutputPath),
            InitialDirectory = System.IO.Path.GetDirectoryName(_vm.SuggestedOutputPath),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (await _vm.SaveAsAsync(dialog.FileName))
        {
            if (_openAndCheck == null) OpenButton.Visibility = Visibility.Collapsed;
            else OpenButton.Focus();
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SavedPath is { } path && _openAndCheck != null)
        {
            Close();
            await _openAndCheck(path);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
