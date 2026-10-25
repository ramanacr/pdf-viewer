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

    [JSImport("drawThumbnail", ModuleName)]
    public static partial void DrawThumbnail(int page, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bgra, int width, int height);

    [JSImport("observeThumbnails", ModuleName)]
    public static partial void ObserveThumbnails();

    [JSImport("scrollToLocation", ModuleName)]
    public static partial void ScrollToLocation(int page, double x, double y);

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
    [JSImport("markFirstPage", ModuleName)]
    public static partial void MarkFirstPage(double milliseconds, double bytesRead);

    /// <summary>Posts a message with bytes beside it (a copy, transferred to the host page as an ArrayBuffer).</summary>
    [JSImport("embedPostBytes", ModuleName)]
    public static partial void EmbedPostBytes(string json, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bytes);

    /// <summary>The annotation tool the pages' pointer handling follows ("select", "highlight", "ink", …).</summary>
    [JSImport("setTool", ModuleName)]
    public static partial void SetTool(string tool);
}
