using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    /// <summary>Acrobat's "Print as image" (Advanced): pages go to the printer as images drawn by the screen renderer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrintMethodDescription))]
    private bool _printAsImage;

    /// <summary>How the pages will reach the printer (vectors, or images and why).</summary>
    public string PrintMethodDescription => DocumentPrinter.DescribeMethod(_docService, PrintAsImage);

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

    /// <summary>What to print, once the user pressed Print; the owner runs the job (with progress and cancel).</summary>
    public PrintRequest? Request { get; private set; }

    [RelayCommand]
    public void ExecutePrint()
    {
        try
        {
            StatusMessage = "Preparing print job...";
            Request = BuildRequest();
            DialogResult = true;
            CloseAction?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to print document: {ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>The job the current settings describe: the printer's own ticket with these choices merged in.</summary>
    internal PrintRequest BuildRequest()
    {
        var queue = SelectedPrinter?.Queue ?? LocalPrintServer.GetDefaultPrintQueue()
            ?? throw new InvalidOperationException("No printer is available.");

        var delta = new PrintTicket { CopyCount = Math.Max(1, Copies) };
        delta.Collation = Collate ? System.Printing.Collation.Collated : System.Printing.Collation.Uncollated;
        if (Orientation == PrintOrientationMode.Portrait)
            delta.PageOrientation = PageOrientation.Portrait;
        else if (Orientation == PrintOrientationMode.Landscape)
            delta.PageOrientation = PageOrientation.Landscape;
        if (ColorMode == PrintColorMode.Grayscale)
            delta.OutputColor = OutputColor.Grayscale;
        var baseTicket = queue.UserPrintTicket ?? queue.DefaultPrintTicket ?? new PrintTicket();
        var ticket = queue.MergeAndValidatePrintTicket(baseTicket, delta).ValidatedPrintTicket;

        IReadOnlyList<int> pages = RangeMode switch
        {
            PrintRangeMode.CurrentPage => new[] { PreviewPageNumber },
            PrintRangeMode.CustomRange => ParsePageList(CustomPageRange, _documentPageCount),
            _ => Enumerable.Range(1, _documentPageCount).ToArray(),
        };

        return new PrintRequest
        {
            Queue = queue,
            Ticket = ticket,
            Pages = pages,
            Rotation = Orientation == PrintOrientationMode.Landscape ? 90 : 0,
            Scaling = Scaling switch
            {
                PrintScalingMode.ActualSize => PdfEngine.Vector.Direct2D.PrintScaling.ActualSize,
                PrintScalingMode.CustomScale => PdfEngine.Vector.Direct2D.PrintScaling.Custom,
                _ => PdfEngine.Vector.Direct2D.PrintScaling.Fit,
            },
            ScalePercent = Math.Clamp(CustomScalePercent, 1, 1000),
            PrintAsImage = PrintAsImage,
            JobName = $"PDF Viewer - {Path.GetFileName(_documentFilePath)}",
        };
    }

    [RelayCommand]
    public void Cancel()
    {
        DialogResult = false;
        CloseAction?.Invoke();
    }

    /// <summary>
    /// Pages named by a range text such as "1-5, 8, 11-13", in the order given, each clamped to
    /// the document; all pages when nothing usable is given.
    /// </summary>
    internal static IReadOnlyList<int> ParsePageList(string rangeText, int maxPages)
    {
        var pages = new List<int>();
        foreach (var part in (rangeText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length == 1 && int.TryParse(bounds[0], out int single))
            {
                pages.Add(Math.Clamp(single, 1, maxPages));
            }
            else if (bounds.Length == 2 && int.TryParse(bounds[0], out int from))
            {
                int to = bounds[1].Length == 0 ? maxPages : int.TryParse(bounds[1], out int t) ? t : from;
                from = Math.Clamp(from, 1, maxPages);
                to = Math.Clamp(to, 1, maxPages);
                int step = to >= from ? 1 : -1;
                for (int p = from; p != to + step; p += step) pages.Add(p);
            }
        }
        return pages.Count > 0 ? pages : Enumerable.Range(1, maxPages).ToArray();
    }
}

