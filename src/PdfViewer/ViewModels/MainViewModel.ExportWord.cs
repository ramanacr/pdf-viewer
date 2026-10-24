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

    private readonly OfficeExportJob _wordExport = new("Word", ".docx", "Word Document (*.docx)|*.docx");

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
    public Task ExportWordAsync() =>
        RunOfficeExportAsync(_wordExport, ChooseWordTargetFunc, v => IsExportingWord = v, async (doc, target, progress, ct) =>
        {
            using var renderer = CreateExportRenderer();
            var result = await Task.Run(() => DocxExporter.ExportAsync(doc, target, new DocxExportOptions { Renderer = renderer }, progress, ct), ct);
            return ($"{result.Paragraphs} paragraphs, {result.Tables} tables, {result.Images} images", result.Pages, result.Warnings.Count);
        });

    /// <summary>One kind of export to Office: what it is called, the file it writes, and the run in progress.</summary>
    private sealed class OfficeExportJob(string app, string extension, string filter)
    {
        public string App { get; } = app;
        public string Extension { get; } = extension;
        public string Filter { get; } = filter;
        /// <summary>Set while it runs: running the command again cancels it.</summary>
        public CancellationTokenSource? Cts;
    }

    /// <summary>
    /// What the exports to Word, Excel and PowerPoint share: the file is chosen (named after the
    /// PDF), the export runs off the UI thread with progress page by page and stops when the
    /// command runs again, a stopped or failed export leaves no file, and afterwards it offers to
    /// open the file in the app that opens it.
    /// </summary>
    private async Task RunOfficeExportAsync(OfficeExportJob job, Func<string, string?>? choose, Action<bool> setRunning,
        Func<PdfVectorDocument, string, IProgress<int>, CancellationToken, Task<(string Summary, int Pages, int Warnings)>> export)
    {
        if (job.Cts != null)
        {
            job.Cts.Cancel();
            return;
        }
        if (!IsDocumentLoaded || _docService.CurrentBytes is not { } bytes) return;
        if (!Permit(DocumentPermissions.CanCopy, "exporting its content")) return;

        string source = string.IsNullOrEmpty(_docService.CurrentFilePath) ? "Document.pdf" : _docService.CurrentFilePath;
        string suggested = Path.GetFileNameWithoutExtension(source) + job.Extension;
        string? target = choose != null ? choose(suggested) : ChooseExportTarget(job, source, suggested);
        if (string.IsNullOrEmpty(target)) return;

        setRunning(true);
        job.Cts = new CancellationTokenSource();
        var ct = job.Cts.Token;
        StatusText = $"Exporting to {job.App}...";
        try
        {
            int pages = PageCount;
            var progress = new Progress<int>(n => StatusText = $"Exporting to {job.App}: page {n} of {pages}... (run again to stop)");
            (string Summary, int Pages, int Warnings) result;
            using (var doc = await PdfVectorDocument.OpenAsync(bytes, source, password: _openPassword, cancellationToken: ct))
                result = await export(doc, target, progress, ct);
            StatusText = $"Exported {result.Pages} page(s) to {job.App}: {result.Summary}.";
            string note = result.Warnings > 0 ? $"\n\nSome content could not be carried over ({result.Warnings} note(s))." : string.Empty;
            if (Confirm($"The document was exported to:\n{target}{note}\n\nOpen it now?", $"Export to {job.App}"))
                (OpenWithDefaultAppAction ?? (p => OpenWithDefaultApp(p, job.App)))(target);
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Export to {job.App} stopped; no file was written.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"Export to {job.App} failed.";
            ShowAlert($"The document could not be exported to {job.App}:\n\n{ex.Message}", $"Export to {job.App}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            setRunning(false);
            job.Cts?.Dispose();
            job.Cts = null;
        }
    }

    /// <summary>The renderer that draws charts and other drawings as pictures for the export; none where Direct2D cannot start.</summary>
    private static PdfEngine.Vector.IPdfVectorRenderer? CreateExportRenderer()
    {
        try { return new PdfEngine.Vector.Direct2D.Direct2DVectorRenderer(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException or DllNotFoundException) { return null; }
    }

    private static string? ChooseExportTarget(OfficeExportJob job, string source, string suggested)
    {
        var dialog = new SaveFileDialog
        {
            Filter = job.Filter,
            Title = $"Export to {job.App}",
            FileName = suggested,
            DefaultExt = job.Extension,
            InitialDirectory = Path.GetDirectoryName(source) ?? string.Empty,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OpenWithDefaultApp(string path, string app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShowAlert($"No app could open the file:\n\n{ex.Message}", $"Export to {app}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
