using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Confirms applying the redaction marks: the label, metadata removal and where the redacted copy goes.</summary>
public partial class ApplyRedactionsDialog : Window
{
    public RedactionApplyOptions? Result { get; private set; }

    public ApplyRedactionsDialog(int marks, string suggestedPath)
    {
        InitializeComponent();
        SummaryText.Text = $"{marks} marked area(s) will be redacted. A new copy is saved and opened; the original document is not changed.";
        OutputBox.Text = suggestedPath;
        OverlayBox.Text = "Redacted";
    }

    private void OverlayCheck_Changed(object sender, RoutedEventArgs e) => OverlayBox.IsEnabled = OverlayCheck.IsChecked == true;

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the redacted copy",
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Path.GetFileName(OutputBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutputBox.Text),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FileName;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Result = new RedactionApplyOptions(OutputBox.Text, OverlayCheck.IsChecked == true ? OverlayBox.Text : null, MetadataCheck.IsChecked == true);
        DialogResult = true;
    }
}
