using System;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Saves a smaller copy of the open document.</summary>
public partial class ReduceSizeWindow : Window
{
    private readonly ReduceSizeViewModel _vm;
    private readonly Func<string, Task>? _open;

    public ReduceSizeWindow(ReduceSizeViewModel vm, Func<string, Task>? open)
    {
        InitializeComponent();
        _vm = vm;
        _open = open;
        DataContext = vm;
        vm.Confirm = message => MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private async void Reduce_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the smaller copy",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = System.IO.Path.GetFileName(_vm.SuggestedOutputPath),
            InitialDirectory = System.IO.Path.GetDirectoryName(_vm.SuggestedOutputPath),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (await _vm.ReduceAsync(dialog.FileName) && _open != null) OpenButton.Visibility = Visibility.Visible;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SavedPath is { } path && _open != null)
        {
            Close();
            await _open(path);
        }
    }
}
