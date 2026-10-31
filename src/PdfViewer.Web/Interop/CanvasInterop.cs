using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace PdfViewer.Web.Interop;

/// <summary>
/// Synchronous calls into wwwroot/js/viewer.js through [JSImport]: rendered pixels go to the
/// canvas as a view of WebAssembly memory (one copy, in the script), not serialized.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class CanvasInterop
{
    public const string ModuleName = "viewer";

    [JSImport("drawPage", ModuleName)]
    public static partial void DrawPage(int page, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bgra, int width, int height);

    [JSImport("clearPage", ModuleName)]
    public static partial void ClearPage(int page);

    [JSImport("viewportSize", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static partial double[] ViewportSize();

    [JSImport("devicePixelRatio", ModuleName)]
    public static partial double DevicePixelRatio();

    [JSImport("scrollToPage", ModuleName)]
    public static partial void ScrollToPage(int page, double offsetY, bool smooth);

    [JSImport("rescaleScroll", ModuleName)]
    public static partial void RescaleScroll(double ratio);

    [JSImport("observePages", ModuleName)]
    public static partial void ObservePages();

    [JSImport("pickFile", ModuleName)]
    public static partial void PickFile();

    [JSImport("focusElement", ModuleName)]
    public static partial void FocusElement(string id, bool select);

    [JSImport("readSetting", ModuleName)]
    public static partial string? ReadSetting(string key);

    [JSImport("writeSetting", ModuleName)]
    public static partial void WriteSetting(string key, string value);

    [JSImport("setTheme", ModuleName)]
    public static partial void SetTheme(string theme);

    [JSImport("hasLocalFonts", ModuleName)]
    public static partial bool HasLocalFonts();

    /// <summary>True when the view is phone-narrow: the side panel is then a drawer over the pages (app.css).</summary>
    [JSImport("isNarrow", ModuleName)]
    public static partial bool IsNarrow();

    /// <summary>Focuses the first, last, next or previous enabled item of a role="menu" element (key: Home, End, ArrowDown, ArrowUp).</summary>
    [JSImport("moveMenuFocus", ModuleName)]
    public static partial void MoveMenuFocus(string menuId, string key);

    [JSImport("drawThumbnail", ModuleName)]
    public static partial void DrawThumbnail(int page, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bgra, int width, int height);

    [JSImport("observeThumbnails", ModuleName)]
    public static partial void ObserveThumbnails();

    [JSImport("scrollToLocation", ModuleName)]
    public static partial void ScrollToLocation(int page, double x, double y);

    [JSImport("openExternal", ModuleName)]
    public static partial bool OpenExternal(string url);

    [JSImport("setLinkBack", ModuleName)]
    public static partial void SetLinkBack(bool value);

    [JSImport("viewLocation", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static partial double[] ViewLocation();

    [JSImport("setSelectionText", ModuleName)]
    public static partial void SetSelectionText(string? text);

    [JSImport("downloadBytes", ModuleName)]
    public static partial void DownloadBytes([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes, string name);

    [JSImport("printBegin", ModuleName)]
    public static partial void PrintBegin();

    [JSImport("printAddPage", ModuleName)]
    public static partial void PrintAddPage([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bgra, int width, int height, double widthPt, double heightPt);

    [JSImport("printPending", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    public static partial Task<int> PrintPending();

    [JSImport("printRun", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    public static partial Task<bool> PrintRun(string title);

    [JSImport("canFullscreen", ModuleName)]
    public static partial bool CanFullscreen();

    [JSImport("toggleFullscreen", ModuleName)]
    public static partial void ToggleFullscreen();

    [JSImport("embedPost", ModuleName)]
    public static partial void EmbedPost(string json);

    /// <summary>A file read by ranges showed its first page: a performance mark (pdfviewer:first-page) for measurements; nothing leaves the page.</summary>
    /// <summary>A page was drawn: a performance mark (pdfviewer:render) with its interpretation and drawing times, for measurements; nothing leaves the page.</summary>
    [JSImport("markRender", ModuleName)]
    public static partial void MarkRender(int page, double interpretMs, double drawMs);

    [JSImport("markFirstPage", ModuleName)]
    public static partial void MarkFirstPage(double milliseconds, double bytesRead);

    /// <summary>Posts a message with bytes beside it (a copy, transferred to the host page as an ArrayBuffer).</summary>
    [JSImport("embedPostBytes", ModuleName)]
    public static partial void EmbedPostBytes(string json, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes);

    /// <summary>The annotation tool the pages' pointer handling follows ("select", "highlight", "ink", …).</summary>
    [JSImport("setTool", ModuleName)]
    public static partial void SetTool(string tool);

    // The opt-in render worker (index.html?renderInWorker=1, wwwroot/js/render-worker.js).

    /// <summary>
    /// Starts the render worker: true once its runtime is up, false when it cannot start. The worker
    /// fetches fonts from <paramref name="fontBase"/> (<see cref="PdfViewer.Web.Engine.Embed.EmbedFrame.FontBase"/>), or the default folder when null.
    /// </summary>
    [JSImport("startRenderWorker", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    public static partial Task<bool> StartRenderWorker(string? fontBase);

    /// <summary>Opens a copy of the document in the worker (the script copies the bytes before it returns); its page count.</summary>
    [JSImport("workerOpen", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    public static partial Task<int> WorkerOpen([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes);

    /// <summary>Renders a page in the worker and keeps the bitmap in the script; its size and timings as JSON.</summary>
    [JSImport("workerRender", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> WorkerRender(int page, double scale, bool night);

    /// <summary>Shows the page that <see cref="WorkerRender"/> kept in its canvas.</summary>
    [JSImport("workerDraw", ModuleName)]
    public static partial void WorkerDraw(int page);

    [JSImport("workerDiscard", ModuleName)]
    public static partial void WorkerDiscard(int page);

    [JSImport("workerClose", ModuleName)]
    public static partial void WorkerClose();

    /// <summary>Whether the worker is still running (false after it crashed, stopped answering or was stopped).</summary>
    [JSImport("workerRunning", ModuleName)]
    public static partial bool WorkerRunning();

    /// <summary>Stops the worker; calls still waiting on it fail.</summary>
    [JSImport("workerTerminate", ModuleName)]
    public static partial void WorkerTerminate();

    // ---------------------------------------------------------------- custom stamps (in this browser only)

    /// <summary>Opens the file picker for a custom stamp: the bytes come to OnStampFile.</summary>
    [JSImport("pickStampFile", ModuleName)]
    public static partial void PickStampFile();

    [JSImport("drawStampPreview", ModuleName)]
    public static partial void DrawStampPreview([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bgra, int width, int height);

    /// <summary>The saved stamps as JSON [{ id, name, kind, size }]; "null" when the browser keeps none.</summary>
    [JSImport("stampLibraryList", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> StampLibraryList();

    /// <summary>Keeps a stamp's one-page PDF in the browser; "" when kept, else why not.</summary>
    [JSImport("stampLibrarySave", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    public static partial Task<string> StampLibrarySave(string id, string name, string kind, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> pdf);

    /// <summary>Gives a saved stamp's bytes to OnStampLibraryBytes; false when it is not there.</summary>
    [JSImport("stampLibraryLoad", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    public static partial Task<bool> StampLibraryLoad(string id);

    [JSImport("stampLibraryDelete", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    public static partial Task<bool> StampLibraryDelete(string id);
}
