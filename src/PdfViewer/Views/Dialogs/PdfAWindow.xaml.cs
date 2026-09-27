using System;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Checks the open document against PDF/A and converts it to a PDF/A copy.</summary>
public partial class PdfAWindow : Window
{
    private readonly PdfAViewModel _vm;
    private readonly Func<string, Task>? _open;

    public PdfAWindow(PdfAViewModel vm, Func<string, Task>? open)
    {
        InitializeComponent();
        _vm = vm;
        _open = open;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.CheckAsync();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await _vm.CheckAsync();

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Save the {_vm.SelectedFlavour} copy",
            Filter = "PDF files (*.pdf)|*.pdf",
            FileName = System.IO.Path.GetFileName(_vm.SuggestedOutputPath),
            InitialDirectory = System.IO.Path.GetDirectoryName(_vm.SuggestedOutputPath),
        };
        if (dialog.ShowDialog(this) != true) return;
        if (await _vm.ConvertAsync(dialog.FileName) && _open != null) OpenButton.Visibility = Visibility.Visible;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ConvertedPath is { } path && _open != null)
        {
            Close();
            await _open(path);
        }
    }
}
