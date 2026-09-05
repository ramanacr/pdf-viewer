using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Printing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

public enum PrintRangeMode
{
    AllPages,
    CurrentPage,
    CustomRange
}

public enum PrintOrientationMode
{
    Auto,
    Portrait,
    Landscape
}

public enum PrintScalingMode
{
    FitToPrintableArea,
    ActualSize,
    CustomScale
}

public enum PrintColorMode
{
    Color,
    Grayscale
}

public class PrinterItem
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Ready";
    public bool IsDefault { get; set; }
    public PrintQueue? Queue { get; set; }

    public override string ToString() => IsDefault ? $"{Name} (Default)" : Name;
}

public partial class PrintPreviewViewModel : ObservableObject
{
    private readonly IPdfDocumentService _docService;
    private readonly int _documentPageCount;
    private readonly string _documentFilePath;
    private CancellationTokenSource? _previewCts;

    public ObservableCollection<PrinterItem> Printers { get; } = new();

    /// <summary>
    /// Why the preview could not be produced, or empty when it rendered fine.
    /// Bindable so the preview pane shows the reason instead of an empty rectangle.
    /// </summary>
    [ObservableProperty]
    private string _previewErrorMessage = string.Empty;

    [ObservableProperty]
    private PrinterItem? _selectedPrinter;

    [ObservableProperty]
    private PrintRangeMode _rangeMode = PrintRangeMode.AllPages;

    [ObservableProperty]
    private string _customPageRange = "1";

    [ObservableProperty]
    private int _copies = 1;

    [ObservableProperty]
    private bool _collate = true;

    [ObservableProperty]
    private PrintOrientationMode _orientation = PrintOrientationMode.Auto;

    [ObservableProperty]
    private PrintScalingMode _scaling = PrintScalingMode.FitToPrintableArea;

    [ObservableProperty]
    private int _customScalePercent = 100;

    [ObservableProperty]
    private PrintColorMode _colorMode = PrintColorMode.Color;

    [ObservableProperty]
    private int _previewPageNumber = 1;

    [ObservableProperty]
    private int _previewPageCount = 1;

    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public Action? CloseAction { get; set; }
    public bool DialogResult { get; private set; }

    public PrintPreviewViewModel(IPdfDocumentService docService, int initialPage = 1)
    {
        _docService = docService ?? throw new ArgumentNullException(nameof(docService));
        _documentPageCount = docService.PageCount > 0 ? docService.PageCount : 1;
        _documentFilePath = docService.CurrentFilePath;
        _previewPageCount = _documentPageCount;
        _previewPageNumber = Math.Clamp(initialPage, 1, _documentPageCount);
        _customPageRange = $"1-{_documentPageCount}";

        LoadPrinters();
        UpdatePreview();
    }

    private void LoadPrinters()
    {
        try
        {
            var server = new LocalPrintServer();
            var defaultQueue = LocalPrintServer.GetDefaultPrintQueue();
            var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });

            PrinterItem? defaultItem = null;

            foreach (var q in queues)
            {
                bool isDef = defaultQueue != null && string.Equals(q.FullName, defaultQueue.FullName, StringComparison.OrdinalIgnoreCase);
                string status = q.IsOffline ? "Offline" : (q.IsInError ? "Error" : "Ready");
                var item = new PrinterItem
                {
                    Name = q.FullName,
                    Status = status,
                    IsDefault = isDef,
                    Queue = q
                };
                Printers.Add(item);
                if (isDef) defaultItem = item;
            }

            if (Printers.Count > 0)
            {
                SelectedPrinter = defaultItem ?? Printers[0];
            }
        }
        catch
        {
            // Fallback generic printer if spooler discovery fails
            var fallback = new PrinterItem { Name = "Default System Printer", Status = "Ready", IsDefault = true };
            Printers.Add(fallback);
            SelectedPrinter = fallback;
        }
    }

    partial void OnPreviewPageNumberChanged(int value) => UpdatePreview();
    partial void OnOrientationChanged(PrintOrientationMode value) => UpdatePreview();
    partial void OnColorModeChanged(PrintColorMode value) => UpdatePreview();
    partial void OnScalingChanged(PrintScalingMode value) => UpdatePreview();
    partial void OnRangeModeChanged(PrintRangeMode value)
    {
        if (value == PrintRangeMode.CurrentPage)
        {
            PreviewPageCount = 1;
            PreviewPageNumber = 1;
        }
        else
        {
            PreviewPageCount = _documentPageCount;
        }
    }

    [RelayCommand]
    public void NextPreviewPage()
    {
        if (PreviewPageNumber < PreviewPageCount)
        {
            PreviewPageNumber++;
        }
    }

    [RelayCommand]
    public void PrevPreviewPage()
    {
        if (PreviewPageNumber > 1)
        {
            PreviewPageNumber--;
        }
    }

    [RelayCommand]
    public void FirstPreviewPage()
    {
        PreviewPageNumber = 1;
    }

    [RelayCommand]
    public void LastPreviewPage()
    {
        PreviewPageNumber = PreviewPageCount;
    }

    public void UpdatePreview()
    {
        // Dispose the superseded CTS as well as cancelling it - the old one was simply
        // dropped, leaking a CancellationTokenSource on every page flip.
        var previousCts = _previewCts;
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        try
        {
            previousCts?.Cancel();
            previousCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        IsPreviewLoading = true;
        int pageToRender = PreviewPageNumber;

        Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                int rot = Orientation switch
                {
                    PrintOrientationMode.Portrait => 0,
                    PrintOrientationMode.Landscape => 90,
                    _ => 0
                };

                // Render high-clarity preview at 150 DPI
                var bitmap = _docService.RenderPage(pageToRender, 150, rot);
                if (bitmap != null && ColorMode == PrintColorMode.Grayscale)
                {
                    bitmap = ConvertToGrayscale(bitmap);
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Re-check INSIDE the dispatcher callback. Checking only before the
                    // marshal let a slow render for page 5 post after a newer render for
                    // page 6 had already landed, leaving the wrong page on screen.
                    if (token.IsCancellationRequested) return;

                    if (bitmap != null)
                    {
                        PreviewImage = bitmap;
                        PreviewErrorMessage = string.Empty;
                    }

                    // Always clear the spinner for the current request, even when the render
                    // returned null (page out of range, document closed underneath, bitmap
                    // allocation failure) - previously the overlay stuck forever.
                    IsPreviewLoading = false;
                });
            }
            catch (OperationCanceledException) { }
            catch (PdfEngine.Exceptions.PdfSecurityPolicyException ex)
            {
                // Surface policy refusals in the preview rather than showing an empty pane
                // that is indistinguishable from a broken preview.
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    PreviewImage = null;
                    PreviewErrorMessage = ex.Message;
                    IsPreviewLoading = false;
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    PreviewErrorMessage = $"Preview failed: {ex.Message}";
                    IsPreviewLoading = false;
                });
            }
        }, token);
    }

    private static FormatConvertedBitmap ConvertToGrayscale(BitmapSource source)
    {
        var grayBitmap = new FormatConvertedBitmap();
        grayBitmap.BeginInit();
        grayBitmap.Source = source;
        grayBitmap.DestinationFormat = PixelFormats.Gray8;
        grayBitmap.EndInit();
        if (grayBitmap.CanFreeze) grayBitmap.Freeze();
        return grayBitmap;
    }

    [RelayCommand]
    public void ExecutePrint()
    {
        try
        {
            StatusMessage = "Preparing print job...";

            var printDialog = new System.Windows.Controls.PrintDialog();
            if (SelectedPrinter?.Queue != null)
            {
                printDialog.PrintQueue = SelectedPrinter.Queue;
            }

            var ticket = printDialog.PrintTicket ?? new PrintTicket();
            ticket.CopyCount = Math.Max(1, Copies);
            if (CollationSupported(ticket))
            {
                ticket.Collation = Collate ? System.Printing.Collation.Collated : System.Printing.Collation.Uncollated;
            }

            if (Orientation == PrintOrientationMode.Portrait)
            {
                ticket.PageOrientation = PageOrientation.Portrait;
            }
            else if (Orientation == PrintOrientationMode.Landscape)
            {
                ticket.PageOrientation = PageOrientation.Landscape;
            }

            printDialog.PrintTicket = ticket;

            int startPage = 1;
            int endPage = _documentPageCount;

            if (RangeMode == PrintRangeMode.CurrentPage)
            {
                startPage = PreviewPageNumber;
                endPage = PreviewPageNumber;
            }
            else if (RangeMode == PrintRangeMode.CustomRange)
            {
                var (start, end) = ParseCustomRange(CustomPageRange, _documentPageCount);
                startPage = start;
                endPage = end;
            }

            int rotAngle = Orientation switch
            {
                PrintOrientationMode.Landscape => 90,
                _ => 0
            };

            var paginator = new PdfiumPdfPaginator(
                _docService,
                startPage,
                endPage,
                printDialog.PrintableAreaWidth > 0 ? printDialog.PrintableAreaWidth : 612,
                printDialog.PrintableAreaHeight > 0 ? printDialog.PrintableAreaHeight : 792,
                rotAngle);

            string jobName = $"PDF Viewer - {Path.GetFileName(_documentFilePath)}";
            printDialog.PrintDocument(paginator, jobName);

            DialogResult = true;
            CloseAction?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to print document: {ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }

    private static (int Start, int End) ParseCustomRange(string rangeText, int maxPages)
    {
        if (string.IsNullOrWhiteSpace(rangeText)) return (1, maxPages);

        try
        {
            var parts = rangeText.Split(new[] { '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out int single))
            {
                int p = Math.Clamp(single, 1, maxPages);
                return (p, p);
            }
            if (parts.Length >= 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e))
            {
                int start = Math.Clamp(s, 1, maxPages);
                int end = Math.Clamp(e, start, maxPages);
                return (start, end);
            }
        }
        catch { }

        return (1, maxPages);
    }

    private static bool CollationSupported(PrintTicket ticket)
    {
        return ticket != null;
    }
}
