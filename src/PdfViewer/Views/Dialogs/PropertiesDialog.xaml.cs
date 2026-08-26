using System.Windows;
using PdfViewer.Models;

namespace PdfViewer.Views.Dialogs;

public partial class PropertiesDialog : Window
{
    public PropertiesDialog(DocumentMetadata metadata)
    {
        InitializeComponent();
        DataContext = metadata;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
