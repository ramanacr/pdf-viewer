using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PdfViewer.Models;

namespace PdfViewer.Views.Dialogs;

public partial class ExportImagesDialog : Window
{
    private readonly DocumentMetadata _metadata;

    public string OutputDirectory { get; private set; } = string.Empty;
    public string FileNamePrefix { get; private set; } = string.Empty;
    public int StartPage { get; private set; } = 1;
    public int EndPage { get; private set; } = 1;
    public string SelectedFormat { get; private set; } = "PNG";
    public int SelectedDpi { get; private set; } = 300;

    public ExportImagesDialog(DocumentMetadata metadata, int currentPageNumber)
    {
        InitializeComponent();
        _metadata = metadata;

        string docDir = Path.GetDirectoryName(metadata.FilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string docName = Path.GetFileNameWithoutExtension(metadata.FileName);

        FolderPathBox.Text = docDir;
        PrefixBox.Text = $"{docName}_export";
        StartPageBox.Text = "1";
        EndPageBox.Text = metadata.PageCount.ToString();
        CurrentPageRadio.Content = $"Current Page Only (Page {currentPageNumber})";
        CurrentPageRadio.Tag = currentPageNumber;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Output Folder",
            InitialDirectory = Directory.Exists(FolderPathBox.Text) ? FolderPathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog() == true)
        {
            FolderPathBox.Text = dialog.FolderName;
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderPathBox.Text) || !Directory.Exists(FolderPathBox.Text))
        {
            MessageBox.Show("Please specify a valid output directory.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        OutputDirectory = FolderPathBox.Text.Trim();
        FileNamePrefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? "page" : PrefixBox.Text.Trim();

        var selectedFormatItem = FormatComboBox.SelectedItem as ComboBoxItem;
        SelectedFormat = selectedFormatItem?.Tag?.ToString() ?? "PNG";

        var selectedDpiItem = DpiComboBox.SelectedItem as ComboBoxItem;
        if (selectedDpiItem != null && int.TryParse(selectedDpiItem.Tag?.ToString(), out int dpi))
        {
            SelectedDpi = dpi;
        }

        if (AllPagesRadio.IsChecked == true)
        {
            StartPage = 1;
            EndPage = _metadata.PageCount;
        }
        else if (CurrentPageRadio.IsChecked == true)
        {
            int cur = (int)(CurrentPageRadio.Tag ?? 1);
            StartPage = cur;
            EndPage = cur;
        }
        else
        {
            if (!int.TryParse(StartPageBox.Text, out int s) || !int.TryParse(EndPageBox.Text, out int end) || s < 1 || end < s || end > _metadata.PageCount)
            {
                MessageBox.Show($"Please enter a valid page range between 1 and {_metadata.PageCount}.", "Invalid Page Range", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StartPage = s;
            EndPage = end;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
