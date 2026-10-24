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
}
