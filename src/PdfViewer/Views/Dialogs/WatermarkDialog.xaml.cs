using System.Windows;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Watermark: text, a picture or a PDF page over the content or behind it.</summary>
public partial class WatermarkDialog : Window
{
    private readonly WatermarkViewModel _vm;

    public WatermarkDialog(WatermarkViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        PageMarkDialogs.Attach(this, vm);
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => PageMarkDialogs.Browse(this, _vm);
}
