using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Download and print. Download saves the bytes the viewer was given, unchanged, as a Blob in the
/// browser. Print renders every page to an image and prints the images through a hidden iframe:
/// the browser's print dialog sees pictures of the pages, not the PDF, so text in the printout is
/// not selectable and the quality is that of the images (<see cref="PrintDpi"/>).
/// </summary>
public sealed partial class Viewer
{
    /// <summary>Resolution of the page images sent to the printer.</summary>
    private const double PrintDpi = 150;

    /// <summary>Longest document printed from the browser; a longer one is downloaded and printed elsewhere.</summary>
    private const int MaxPrintPages = 500;

    private byte[]? _docBytes;
    private bool _printing;

    private string DownloadName => _fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? _fileName : _fileName + ".pdf";

    private void Download()
    {
        if (_doc == null || _docBytes == null) return;
        CanvasInterop.DownloadBytes(_docBytes, DownloadName);
        _status = $"Saved {DownloadName}";
        Emit(EmbedEvents.DocumentDownload, w => w.WriteString("fileName", DownloadName));
    }

    private async Task PrintAsync()
    {
        var doc = _doc;
        if (doc == null || _printing) return;
        if (!doc.CanPrint)
        {
            _error = "This document does not allow printing.";
            return;
        }
        if (doc.PageCount > MaxPrintPages)
        {
            _error = $"This document has more than {MaxPrintPages} pages, too many to print from the browser. Download it and print it from a PDF application.";
            return;
        }
        _printing = true;
        try
        {
            CanvasInterop.PrintBegin();
            foreach (var page in doc.Pages)
            {
                _status = $"Preparing page {page.Number} of {doc.PageCount} for printing…";
                StateHasChanged();
                var bitmap = await doc.RenderPageAsync(page.Number, PrintDpi / 72);
                if (_doc != doc) return;
                CanvasInterop.PrintAddPage(bitmap.Pixels, bitmap.Width, bitmap.Height, page.Width, page.Height);
                await CanvasInterop.PrintPending();
            }
            Emit(EmbedEvents.DocumentPrint, w => w.WriteString("fileName", DownloadName));
            _status = "Printing…";
            StateHasChanged();
            await CanvasInterop.PrintRun(_fileName);
            _status = "";
        }
        catch (ObjectDisposedException)
        {
            // The document was closed while it was prepared.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _error = $"Printing stopped ({ex.GetType().Name}).";
        }
        finally
        {
            _printing = false;
            StateHasChanged();
        }
    }
}
