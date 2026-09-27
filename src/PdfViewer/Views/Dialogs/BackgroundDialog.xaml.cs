using System.Windows;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Background: a colour, a picture or a PDF page behind each page's content.</summary>
public partial class BackgroundDialog : Window
{
    private readonly WatermarkViewModel _vm;

    public BackgroundDialog(WatermarkViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        PageMarkDialogs.Attach(this, vm);
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => PageMarkDialogs.Browse(this, _vm);
}
