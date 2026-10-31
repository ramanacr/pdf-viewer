using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PdfViewer.Web.Engine;

namespace PdfViewer.Web.Interop;

/// <summary>
/// The render-only side of the opt-in render worker (wwwroot/js/render-worker.js): a second .NET
/// runtime, started from this app's own _framework inside a dedicated Web Worker, opens a copy of
/// the document and draws pages, so that the page's own thread stays free for input. Program.Main
/// never runs there; render-worker.js calls these methods through [JSExport]. The pixels go back
/// through <see cref="Deliver"/>, as a view of WebAssembly memory that the script copies once.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class RenderWorkerExports
{
    public const string ModuleName = "render-worker";

    private static ViewerDocument? _doc;
    private static OnDemandFontSource? _fonts;

    /// <summary>Opens a copy of the document; returns its page count. A document open before is closed.</summary>
    [JSExport]
    public static async Task<int> Open(byte[] bytes, string? password)
    {
        if (_doc is { } old)
        {
            _doc = null;
            await old.DisposeAsync();
        }
        _fonts ??= OnDemandFontSource.Bundled((file, _) => FetchFontAsync(file, false), (file, _) => FetchFontAsync(file, true));
        _doc = await ViewerDocument.OpenAsync(bytes, string.IsNullOrEmpty(password) ? null : password, new ViewerOptions
        {
            MaxFileBytes = 512L * 1024 * 1024,
            ImageCacheBytes = 64L * 1024 * 1024,
            MaxBitmapPixels = 4096L * 4096,
            Fonts = _fonts,
        });
        return _doc.PageCount;
    }

    /// <summary>Draws a page and hands its pixels to the script (<see cref="Deliver"/>) under <paramref name="id"/>.</summary>
    [JSExport]
    public static async Task Render(int id, int page, double scale, bool night)
    {
        var doc = _doc ?? throw new InvalidOperationException("No document is open in the render worker.");
        var bitmap = await doc.RenderPageAsync(page, scale, night);
        Deliver(id, bitmap.Pixels, bitmap.Width, bitmap.Height, bitmap.Scale,
            bitmap.BuildTime.TotalMilliseconds, bitmap.RasterTime.TotalMilliseconds, bitmap.TextRegionsWithoutFont, bitmap.ImagesNotDecoded);
    }

    /// <summary>Closes the document.</summary>
    [JSExport]
    public static async Task Close()
    {
        if (_doc is { } old)
        {
            _doc = null;
            await old.DisposeAsync();
        }
    }

    /// <summary>A bundled font file from this origin; with <paramref name="reload"/>, past the HTTP cache.</summary>
    private static async Task<byte[]?> FetchFontAsync(string file, bool reload)
    {
        int length = await FetchFont(file, reload);
        if (length < 0) return null;
        var bytes = new byte[length];
        TakeFont(file, bytes);
        return bytes;
    }

    [JSImport("deliver", ModuleName)]
    private static partial void Deliver(int id, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> bgra, int width, int height, double scale,
        double interpretMs, double drawMs, int textWithoutFont, int imagesNotDecoded);

    /// <summary>Fetches a bundled font file (same origin); its length, or -1. <see cref="TakeFont"/> copies it out.</summary>
    [JSImport("fetchFont", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    private static partial Task<int> FetchFont(string file, bool reload);

    [JSImport("takeFont", ModuleName)]
    private static partial void TakeFont(string file, [JSMarshalAs<JSType.MemoryView>] Span<byte> into);
}
