using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using PdfEngine.Safety;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Lists a document's embedded files and lets the user pick one to extract. The dialog only
/// chooses; the extraction itself is done by the caller.
/// </summary>
public partial class AttachmentsDialog : Window
{
    /// <summary>One row, formatted for display.</summary>
    public sealed record Row(EmbeddedFileInfo File)
    {
        public string Name => File.Name;

        public string SizeText => File.SizeBytes < 0
            ? "unknown"
            : File.SizeBytes < 1024
                ? $"{File.SizeBytes} B"
                : File.SizeBytes < 1024 * 1024
                    ? $"{File.SizeBytes / 1024.0:N1} KB"
                    : $"{File.SizeBytes / (1024.0 * 1024.0):N1} MB";

        public string RiskText => File.HasExecutableExtension ? "Executable type" : "";
    }

    private readonly List<Row> _rows;

    /// <summary>The file the user chose to extract, or null when they closed the dialog.</summary>
    public EmbeddedFileInfo? Chosen { get; private set; }

    public AttachmentsDialog(IReadOnlyList<EmbeddedFileInfo> files, string documentName)
    {
        InitializeComponent();

        _rows = files.Select(f => new Row(f)).ToList();

        int executables = files.Count(f => f.HasExecutableExtension);
        SubtitleText.Text = executables > 0
            ? $"{documentName} carries {files.Count} embedded file(s), " +
              $"{executables} of which Windows would treat as executable."
            : $"{documentName} carries {files.Count} embedded file(s).";

        FilesGrid.ItemsSource = _rows;
        if (_rows.Count > 0) FilesGrid.SelectedIndex = 0;
    }

    private void Extract()
    {
        if (FilesGrid.SelectedItem is not Row row) return;

        Chosen = row.File;
        DialogResult = true;
        Close();
    }

    private void ExtractButton_Click(object sender, RoutedEventArgs e) => Extract();

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Extract();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Chosen = null;
        DialogResult = false;
        Close();
    }
}
