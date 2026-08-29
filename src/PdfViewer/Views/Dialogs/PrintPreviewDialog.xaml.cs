using System;
using System.Windows;
using System.Windows.Media.Imaging;
using PdfViewer.Services;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Interaction logic for PrintPreviewDialog.xaml
/// </summary>
public partial class PrintPreviewDialog : Window
{
    public PrintPreviewViewModel ViewModel { get; }

    public PrintPreviewDialog(IPdfDocumentService docService, int currentPage = 1)
    {
        InitializeComponent();

        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/PdfViewer;component/assets/app_icon.png", UriKind.RelativeOrAbsolute));
        }
        catch { }

        ViewModel = new PrintPreviewViewModel(docService, currentPage)
        {
            CloseAction = () => Close()
        };
        DataContext = ViewModel;
    }
}
