using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// The opt-in render worker (index.html?renderInWorker=1): pages in view are interpreted and drawn
/// by a second, render-only .NET runtime in a dedicated Web Worker (wwwroot/js/render-worker.js),
/// which holds its own copy of the document, so that this thread stays free while a page is drawn.
/// GitHub Pages cannot send the COOP/COEP headers that a multithreaded runtime needs, so the two
/// runtimes share no memory: the bytes go over by postMessage, the pages come back as ImageBitmaps.
/// Everything else (text, search, selection, annotations, thumbnails, print) stays on this thread.
/// The worker serves only complete, unencrypted files drawn with the bundled fonts; when it cannot
/// start, or fails, the pages are drawn here as without the flag.
/// </summary>
public sealed partial class Viewer
{
    private enum WorkerState { Off, Starting, Ready, Failed }

    private WorkerState _worker;
    private Task<bool>? _workerStart;
    private byte[]? _workerBytes;      // the bytes the worker's copy of the document was opened from
    private Task<bool>? _workerOpened;

    /// <summary>How long a page waits for the worker to open the document before it is drawn here instead.</summary>
    private const int OpenWaitMs = 500;

    private sealed record WorkerPage(int Width, int Height, double InterpretMs, double DrawMs, int TextWithoutFont, int ImagesNotDecoded);

    /// <summary>Reads the flag from the page's URL (renderInWorker=1 or true) and starts the worker.</summary>
    private void ReadRenderWorkerFlag()
    {
        foreach (var part in new Uri(Nav.Uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "renderInWorker=1" or "renderInWorker=true")
            {
                _worker = WorkerState.Starting;
                // The frame's font folder, as ReadFrameUrl parsed it: one source for both threads.
                _workerStart = CanvasInterop.StartRenderWorker(_frame?.FontBase);
                return;
            }
        }
    }

    /// <summary>Whether the worker draws <paramref name="doc"/>'s pages now; opens (or reopens, after an edit) its copy first.</summary>
    private async Task<bool> WorkerServesAsync(ViewerDocument doc)
    {
        if (_worker is WorkerState.Off or WorkerState.Failed) return false;
        if (!doc.IsComplete || doc.IsEncrypted || _lentFonts != null) return false;
        if (_worker == WorkerState.Starting)
        {
            // Pages are not held back while the worker's runtime starts: they are drawn here until it is up.
            if (!_workerStart!.IsCompleted) return false;
            bool started = await _workerStart;
            _worker = started ? WorkerState.Ready : WorkerState.Failed;
            if (!started)
            {
                _perf = "The render worker did not start; pages are drawn on the page's thread.";
                return false;
            }
        }
        var bytes = doc.Bytes;
        if (!ReferenceEquals(bytes, _workerBytes))
        {
            _workerBytes = bytes;
            _workerOpened = OpenInWorkerAsync(bytes);
        }
        // A large file takes the worker a while to open: meanwhile the page in view is drawn here.
        var opening = _workerOpened!;
        if (!opening.IsCompleted) await Task.WhenAny(opening, Task.Delay(OpenWaitMs));
        if (!opening.IsCompleted) return false;
        bool opened = await opening;
        return opened && _worker == WorkerState.Ready && _doc == doc && ReferenceEquals(doc.Bytes, bytes);
    }

    private async Task<bool> OpenInWorkerAsync(byte[] bytes)
    {
        try
        {
            await CanvasInterop.WorkerOpen(new ArraySegment<byte>(bytes));
            return true;
        }
        catch (JSException ex)
        {
            WorkerFailed(ex);
            return false;
        }
    }

    /// <summary>
    /// Renders a page in the worker (the script keeps it until <see cref="CanvasInterop.WorkerDraw"/>); null
    /// when the worker could not draw this page, which is then drawn here. A worker that crashed or stopped
    /// answering is turned off; one bad page is not.
    /// </summary>
    private async Task<WorkerPage?> RenderInWorkerAsync(int page, double scale, bool night)
    {
        try
        {
            string json = await CanvasInterop.WorkerRender(page, scale, night);
            using var meta = JsonDocument.Parse(json);
            var r = meta.RootElement;
            return new WorkerPage(r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(),
                r.GetProperty("interpretMs").GetDouble(), r.GetProperty("drawMs").GetDouble(),
                r.GetProperty("textWithoutFont").GetInt32(), r.GetProperty("imagesNotDecoded").GetInt32());
        }
        catch (JSException ex)
        {
            if (!CanvasInterop.WorkerRunning()) WorkerFailed(ex);
            return null;
        }
    }

    /// <summary>The worker failed: it is stopped, with its copy of the document, and the rest of the session draws on this thread.</summary>
    private void WorkerFailed(Exception ex)
    {
        _worker = WorkerState.Failed;
        _workerBytes = null;
        CanvasInterop.WorkerTerminate();
        // Diagnostics name the failure type only, never document content.
        _perf = $"The render worker failed ({ex.GetType().Name}); pages are drawn on the page's thread.";
    }

    /// <summary>Stops the worker when the viewer goes away.</summary>
    private void StopRenderWorker()
    {
        if (_worker == WorkerState.Off) return;
        _worker = WorkerState.Failed;
        _workerBytes = null;
        CanvasInterop.WorkerTerminate();
    }

    private void CloseInWorker()
    {
        if (_worker != WorkerState.Ready || _workerBytes == null) return;
        _workerBytes = null;
        CanvasInterop.WorkerClose();
    }
}
