using System.Windows;
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
        ViewModel = new PrintPreviewViewModel(docService, currentPage)
        {
            CloseAction = () => Close()
        };
        DataContext = ViewModel;
    }
}
