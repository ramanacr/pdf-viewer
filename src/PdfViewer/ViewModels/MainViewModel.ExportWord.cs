using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Export;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>A Word export is running.</summary>
    [ObservableProperty]
    private bool _isExportingWord;

    private CancellationTokenSource? _exportWordCts;

    /// <summary>Chooses where the .docx goes (tests set it; null: the save dialog).</summary>
    public Func<string, string?>? ChooseWordTargetFunc { get; set; }

    /// <summary>Opens a file with the system's default app (tests replace it).</summary>
    public Action<string>? OpenWithDefaultAppAction { get; set; }

    /// <summary>
    /// Exports the document as an editable Word document (.docx): text that flows with its fonts
    /// and layout, headings, lists, tables, images, links, headers and footers. Progress is shown
    /// page by page; running it again while it runs cancels it. Afterwards it offers to open the
    /// file in the app that opens .docx files.
    /// </summary>
    [RelayCommand]
    public async Task ExportWordAsync()
    {
        if (IsExportingWord)
        {
            _exportWordCts?.Cancel();
            return;
        }
        if (!IsDocumentLoaded || _docService.CurrentBytes is not { } bytes) return;
        if (!Permit(DocumentPermissions.CanCopy, "exporting its content")) return;

        string source = string.IsNullOrEmpty(_docService.CurrentFilePath) ? "Document.pdf" : _docService.CurrentFilePath;
        string suggested = Path.GetFileNameWithoutExtension(source) + ".docx";
        string? target = ChooseWordTargetFunc != null ? ChooseWordTargetFunc(suggested) : ChooseWordTarget(source, suggested);
        if (string.IsNullOrEmpty(target)) return;

        IsExportingWord = true;
        _exportWordCts = new CancellationTokenSource();
        var ct = _exportWordCts.Token;
        StatusText = "Exporting to Word...";
        try
        {
            int pages = PageCount;
            var progress = new Progress<int>(n => StatusText = $"Exporting to Word: page {n} of {pages}... (run again to stop)");
            DocxExportResult result;
            using (var doc = await PdfVectorDocument.OpenAsync(bytes, source, password: _openPassword, cancellationToken: ct))
                result = await Task.Run(() => DocxExporter.ExportAsync(doc, target, progress, ct), ct);
            StatusText = $"Exported {result.Pages} page(s) to Word: {result.Paragraphs} paragraphs, {result.Tables} tables, {result.Images} images.";
            string note = result.Warnings.Count > 0 ? $"\n\nSome content could not be carried over ({result.Warnings.Count} note(s))." : string.Empty;
            if (Confirm($"The document was exported to:\n{target}{note}\n\nOpen it now?", "Export to Word"))
                (OpenWithDefaultAppAction ?? OpenWithDefaultApp)(target);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export to Word stopped; no file was written.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = "Export to Word failed.";
            ShowAlert($"The document could not be exported to Word:\n\n{ex.Message}", "Export to Word", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsExportingWord = false;
            _exportWordCts?.Dispose();
            _exportWordCts = null;
        }
    }

    private static string? ChooseWordTarget(string source, string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Word Document (*.docx)|*.docx",
            Title = "Export to Word",
            FileName = suggested,
            DefaultExt = ".docx",
            InitialDirectory = Path.GetDirectoryName(source) ?? string.Empty,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OpenWithDefaultApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowAlert($"No app could open the file:\n\n{ex.Message}", "Export to Word", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
