using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PdfViewer.Models;

namespace PdfViewer.Views.Dialogs;

public partial class SaveAnnotatedDialog : Window
{
    private readonly string _originalPath;

    public bool Confirmed { get; private set; }
    public string TargetPath { get; private set; } = string.Empty;
    public AnnotationSaveMode SelectedMode { get; private set; } = AnnotationSaveMode.Embedded;

    public SaveAnnotatedDialog(string originalPath)
    {
        InitializeComponent();
        _originalPath = originalPath ?? string.Empty;
        OriginalPathTextBlock.Text = !string.IsNullOrEmpty(_originalPath) ? _originalPath : "Untitled.pdf";

        // Pre-populate default target path with "_annotated" suffix
        SuggestDefaultTargetPath();
        ValidateInputs();
    }

    private void SuggestDefaultTargetPath()
    {
        if (string.IsNullOrEmpty(_originalPath))
        {
            TargetPathTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AnnotatedDocument.pdf");
            return;
        }

        string dir = Path.GetDirectoryName(_originalPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(_originalPath);
        string ext = SelectedMode == AnnotationSaveMode.ExportXfdf ? ".xfdf" : ".pdf";
        string suffix = SelectedMode == AnnotationSaveMode.Flattened ? "_flattened" : "_annotated";

        string suggested = Path.Combine(dir, $"{nameWithoutExt}{suffix}{ext}");
        TargetPathTextBox.Text = suggested;
    }

    private void SaveMode_Changed(object sender, RoutedEventArgs e)
    {
        if (EmbeddedRadio == null || FlattenedRadio == null || ExportXfdfRadio == null) return;

        if (FlattenedRadio.IsChecked == true)
            SelectedMode = AnnotationSaveMode.Flattened;
        else if (ExportXfdfRadio.IsChecked == true)
            SelectedMode = AnnotationSaveMode.ExportXfdf;
        else
            SelectedMode = AnnotationSaveMode.Embedded;

        // Update target file extension and default name
        string currentText = TargetPathTextBox.Text;
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            string dir = Path.GetDirectoryName(currentText) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(currentText);
            string newExt = SelectedMode == AnnotationSaveMode.ExportXfdf ? ".xfdf" : ".pdf";
            TargetPathTextBox.Text = Path.Combine(dir, $"{name}{newExt}");
        }

        ValidateInputs();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Title = "Select Destination for Annotated PDF",
            FileName = Path.GetFileName(TargetPathTextBox.Text)
        };

        if (SelectedMode == AnnotationSaveMode.ExportXfdf)
        {
            sfd.Filter = "XFDF Comments File (*.xfdf)|*.xfdf|FDF Comments File (*.fdf)|*.fdf|All Files (*.*)|*.*";
            sfd.DefaultExt = ".xfdf";
        }
        else
        {
            sfd.Filter = "PDF Document (*.pdf)|*.pdf|All Files (*.*)|*.*";
            sfd.DefaultExt = ".pdf";
        }

        if (sfd.ShowDialog() == true)
        {
            TargetPathTextBox.Text = sfd.FileName;
            ValidateInputs();
        }
    }

    private void TargetPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateInputs();
    }

    private void ValidateInputs()
    {
        if (SaveButton == null || ValidationWarningTextBlock == null) return;

        string target = TargetPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            SaveButton.IsEnabled = false;
            ValidationWarningTextBlock.Text = "Please specify a destination file path.";
            ValidationWarningTextBlock.Visibility = Visibility.Visible;
            return;
        }

        // Check if target is same as original
        if (!string.IsNullOrEmpty(_originalPath))
        {
            try
            {
                string fullTarget = Path.GetFullPath(target);
                string fullOriginal = Path.GetFullPath(_originalPath);

                if (string.Equals(fullTarget, fullOriginal, StringComparison.OrdinalIgnoreCase))
                {
                    SaveButton.IsEnabled = false;
                    ValidationWarningTextBlock.Text = "⚠ Overwriting the original file is blocked. Please choose a different name or destination.";
                    ValidationWarningTextBlock.Visibility = Visibility.Visible;
                    return;
                }
            }
            catch
            {
                // Invalid path format
                SaveButton.IsEnabled = false;
                ValidationWarningTextBlock.Text = "Invalid file path format.";
                ValidationWarningTextBlock.Visibility = Visibility.Visible;
                return;
            }
        }

        SaveButton.IsEnabled = true;
        ValidationWarningTextBlock.Visibility = Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        TargetPath = TargetPathTextBox.Text.Trim();
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
