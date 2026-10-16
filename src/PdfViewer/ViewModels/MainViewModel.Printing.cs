using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>Print jobs: run in the background with progress in the status bar, and cancellable.</summary>
public partial class MainViewModel
{
    /// <summary>
    /// Shows the print dialog (the print preview in the app) and returns what to print, or null
    /// when the user cancelled. Takes precedence over <see cref="ShowPrintDialogFunc"/>.
    /// </summary>
    public Func<IPdfDocumentService, int, PrintRequest?>? ShowPrintSetupFunc { get; set; }

    /// <summary>A print job is being sent; the status bar offers to cancel it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelPrintCommand))]
    private bool _isPrinting;

    private CancellationTokenSource? _printCts;

    /// <summary>
    /// Sends <paramref name="request"/> to the printer. Pages are prepared off the UI thread; the
    /// status bar shows which page is being sent, and <see cref="CancelPrintCommand"/> stops it.
    /// </summary>
    public async Task<PrintOutcome?> PrintAsync(PrintRequest request)
    {
        if (IsPrinting)
            return null;
        using var cts = new CancellationTokenSource();
        _printCts = cts;
        IsPrinting = true;
        StatusText = $"Preparing to print {PageCountText(request.Pages.Count)}...";
        var progress = new Progress<PrintProgress>(p =>
        {
            if (IsPrinting && p.Done < p.Total)
                StatusText = $"Printing page {p.Done + 1} of {p.Total}{(p.Method == PrintMethod.Image ? " (as images)" : string.Empty)}...";
        });
        try
        {
            var outcome = await DocumentPrinter.PrintAsync(_docService, request, progress, cts.Token);
            StatusText = outcome.Note != null ? $"Print job sent to printer. {outcome.Note}"
                : outcome.Method == PrintMethod.Vector && outcome.ImagePages > 0
                    ? $"Print job sent to printer ({PageCountText(outcome.ImagePages)} as images)."
                    : "Print job sent to printer.";
            return outcome;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Printing cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            ShowAlert($"Print failed: {ex.Message}", "Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Print error: {ex.Message}";
            return null;
        }
        finally
        {
            _printCts = null;
            IsPrinting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsPrinting))]
    private void CancelPrint()
    {
        StatusText = "Cancelling print job...";
        _printCts?.Cancel();
    }

    private static string PageCountText(int count) => count == 1 ? "1 page" : $"{count} pages";
}
