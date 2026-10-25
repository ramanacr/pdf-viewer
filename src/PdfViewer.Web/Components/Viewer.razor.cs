using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Skia;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Demo;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// The viewer page: toolbar, side panel (outline, search results), the pages and the status bar.
/// Pages are laid out at their full size and drawn only when near the view, most central first,
/// one at a time; rendered bitmaps are kept in a bounded cache and canvases far from the view are
/// emptied. All engine work goes through <see cref="ViewerDocument"/>.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class Viewer : IAsyncDisposable
{
    private enum ZoomMode { FitWidth, FitPage, Custom }
    private enum Panel { Outline, Results }
    private enum Dialog { None, Help, Password }

    private static readonly double[] ZoomPresets = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
    private const double MinZoom = 0.1, MaxZoom = 8;

    /// <summary>CSS pixels per PDF point at 100 % (CSS has 96 pixels per inch, PDF 72 points).</summary>
    private const double CssPerPoint = 96.0 / 72.0;

    /// <summary>Canvases more than this many pages from the current one are emptied.</summary>
    private const int KeepDrawnPages = 4;

    /// <summary>
    /// Session limits; the font source is the device's fonts once the user lent them, else the
    /// bundled Liberation fonts, fetched as pages need them (a browser has no installed fonts).
    /// </summary>
    private ViewerOptions Options => new()
    {
        MaxFileBytes = 512L * 1024 * 1024,
        ImageCacheBytes = 64L * 1024 * 1024,
        MaxBitmapPixels = 4096L * 4096,
        Fonts = _lentFonts ?? _bundledFonts,
    };

    /// <summary>Families the font substitutes are chosen from (the Skia backend's chain), read from the device on request.</summary>
    private static readonly string[] LocalFontFamilies =
    [
        "Arial", "Times New Roman", "Courier New", "Symbol", "Segoe UI", "Arial Narrow", "Calibri", "Cambria", "Georgia", "Verdana",
        "Tahoma", "Trebuchet MS", "Consolas", "Palatino Linotype", "Book Antiqua", "Garamond", "Century Gothic", "Wingdings",
        "Segoe UI Symbol", "Cambria Math", "Monotype Corsiva", "Segoe Script", "Helvetica", "Helvetica Neue", "Times", "Courier",
        "Liberation Sans", "Liberation Serif", "Liberation Mono", "DejaVu Sans", "DejaVu Serif", "DejaVu Sans Mono", "Noto Sans",
        "Noto Serif", "Nirmala UI", "Ebrima", "Malgun Gothic", "Yu Gothic", "Microsoft YaHei",
    ];
    private const string LocalFontDirectory = "/local-fonts";

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private DotNetObjectReference<Viewer>? _self;
    private IJSObjectReference? _module;

    // Document.
    private ViewerDocument? _doc;
    private string _fileName = "";
    private IReadOnlyList<ViewerOutlineItem> _outline = [];
    private int _currentPage = 1;
    private readonly HashSet<int> _visible = new();

    // View.
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private double _zoom = 1;
    private bool _night;
    private string _theme = "system";
    private bool _sidebarOpen = true;
    private Panel _panel = Panel.Outline;
    private Dialog _dialog;
    private bool _layoutChanged, _refit;
    private double? _rescale;

    // Messages.
    private string? _error, _notice;
    private bool _noticeIsFonts, _noticeDismissed;
    private bool _loading;
    private string _loadingText = "";
    private string _status = "Loading…", _perf = "", _engineInfo = "";

    // Password prompt (for engines and platforms that can decrypt).
    private byte[]? _pendingBytes;
    private string _pendingName = "", _passwordPrompt = "", _password = "";

    // Search.
    private string _query = "", _searchedFor = "";
    private bool _matchCase, _searching, _shiftEnter;
    private int _searchPagesDone, _currentHit = -1;
    private readonly List<ViewerSearchHit> _hits = new();
    private readonly Dictionary<int, List<(ViewerSearchHit Hit, int Index)>> _hitsByPage = new();
    private CancellationTokenSource? _searchCts;

    // Rendering.
    private readonly PageBitmapCache _cache = new(160L * 1024 * 1024);
    private readonly Dictionary<int, (double Scale, bool Night)> _drawn = new();
    private readonly HashSet<(int Page, double Scale, bool Night)> _failed = new();
    private bool _pumping;
    private readonly List<double> _renderTimes = new();

    // Device fonts (Local Font Access API, Chromium browsers).
    private bool _canUseLocalFonts, _localFontsLoaded;
    private int _localFontCount;
    private SkiaFontSource? _lentFonts;

    // Bundled fonts (wwwroot/fonts): fetched the first time a page needs a face, kept for the session.
    private readonly OnDemandFontSource _bundledFonts;

    public Viewer()
    {
        _bundledFonts = OnDemandFontSource.Liberation(FetchBundledFontAsync);
    }

    private async Task<byte[]?> FetchBundledFontAsync(string file, CancellationToken cancellationToken) =>
        _module == null ? null : await _module.InvokeAsync<byte[]?>("fetchFont", cancellationToken, file);

    // Focus moves into a dialog when it opens, and back to the pages when it closes.
    private Dialog _focusedDialog;

    private double RenderScale => _zoom * CssPerPoint * CanvasInterop.DevicePixelRatio();

    private string ThemeIcon => _theme switch { "light" => "sun", "dark" => "moon", _ => "system" };

    private string SearchStatus =>
        string.IsNullOrWhiteSpace(_searchedFor) ? ""
        : _searching ? $"{_hits.Count} found, page {_searchPagesDone} of {_doc?.PageCount}"
        : _hits.Count == 0 ? "No matches"
        : $"{_currentHit + 1} of {_hits.Count}";

    private (double Width, double Height) CssSize(ViewerPage page) =>
        (Math.Round(page.Width * _zoom * CssPerPoint, 2), Math.Round(page.Height * _zoom * CssPerPoint, 2));

    private static string Px(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "px";
    private static string Pct(double v) => (v * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    private static string Percent(double zoom) => Math.Round(zoom * 100).ToString(CultureInfo.InvariantCulture) + "%";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/viewer.js?v=2");
            await _module.InvokeVoidAsync("init", _self, "pages");
            _theme = CanvasInterop.ReadSetting("theme") is "light" or "dark" ? CanvasInterop.ReadSetting("theme")! : "system";
            _sidebarOpen = CanvasInterop.ReadSetting("sidebar") != "closed";
            CanvasInterop.SetTheme(_theme);
            _canUseLocalFonts = CanvasInterop.HasLocalFonts();
            _engineInfo = $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}. " +
                          $"Engine: PdfEngine.Vector with the Skia backend (SkiaSharp {typeof(SkiaSharp.SKCanvas).Assembly.GetName().Version}).";
            _status = "Ready";
            StateHasChanged();
            return;
        }
        if (_layoutChanged)
        {
            _layoutChanged = false;
            CanvasInterop.ObservePages();
        }
        if (_rescale is double ratio)
        {
            _rescale = null;
            CanvasInterop.RescaleScroll(ratio);
        }
        if (_refit)
        {
            _refit = false;
            if (Refit()) StateHasChanged();
        }
        if (_dialog != _focusedDialog)
        {
            _focusedDialog = _dialog;
            CanvasInterop.FocusElement(_dialog switch { Dialog.Help => "dialog-close", Dialog.Password => "password-input", _ => "pages" }, select: false);
        }
        _ = PumpAsync();
    }

    // ---------------------------------------------------------------- opening

    private void PickFile() => CanvasInterop.PickFile();

    private async Task OpenSampleAsync()
    {
        _loading = true;
        _loadingText = "Writing the engine tour…";
        StateHasChanged();
        await Task.Delay(1);
        byte[] bytes = DemoDocument.Create();
        await OpenAsync(bytes, "Engine tour (generated in your browser).pdf", null);
    }

    [JSInvokable]
    public void OnFileReading(string name)
    {
        _loading = true;
        _loadingText = $"Reading {name}…";
        _status = _loadingText;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnFileRejected(string name, string reason)
    {
        _loading = false;
        _error = $"{name}: {reason}";
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnFileOpened(string name, byte[] bytes) => OpenAsync(bytes, name, null);

    private async Task OpenAsync(byte[] bytes, string name, string? password)
    {
        _loading = true;
        _loadingText = $"Opening {name}…";
        _status = _loadingText;
        _error = null;
        StateHasChanged();
        await Task.Delay(1);

        var clock = Stopwatch.StartNew();
        ViewerDocument doc;
        try
        {
            doc = await ViewerDocument.OpenAsync(bytes, password, Options);
        }
        catch (ViewerOpenException ex) when (ex.Failure is ViewerOpenFailure.PasswordRequired or ViewerOpenFailure.WrongPassword)
        {
            _pendingBytes = bytes;
            _pendingName = name;
            _password = "";
            _passwordPrompt = ex.Failure == ViewerOpenFailure.WrongPassword ? $"That password does not open {name}. Try again." : $"{name} is protected by a password.";
            _dialog = Dialog.Password;
            _loading = false;
            _status = "Password required";
            StateHasChanged();
            return;
        }
        catch (ViewerOpenException ex)
        {
            _error = $"{name}: {ex.Message}" + (ex.Detail.Length > 0 && ex.Failure == ViewerOpenFailure.NotReadable ? $" (Engine detail: {ex.Detail}.)" : "");
            _loading = false;
            _status = _doc == null ? "Ready" : $"{_doc.PageCount} {(_doc.PageCount == 1 ? "page" : "pages")}";
            StateHasChanged();
            return;
        }
        var opened = clock.Elapsed;

        await CloseDocumentAsync();
        _doc = doc;
        _fileName = name;
        _currentPage = 1;
        _outline = await doc.GetOutlineAsync();
        _panel = Panel.Outline;
        _noticeDismissed = false;
        _notice = null;
        _query = _query.Trim();
        _loading = false;
        _status = $"{doc.PageCount} {(doc.PageCount == 1 ? "page" : "pages")}, opened in {opened.TotalMilliseconds:0} ms" +
                  (doc.WasRepaired ? " (damaged file, repaired)" : "");
        // Fit again once the side panel is laid out beside the pages.
        Refit();
        _refit = true;
        _layoutChanged = true;
        StateHasChanged();
        await Task.Delay(1);
        CanvasInterop.ScrollToPage(1, 0, smooth: false);
        if (!string.IsNullOrWhiteSpace(_query)) await RestartSearchAsync();
    }

    private async Task SubmitPasswordAsync()
    {
        _dialog = Dialog.None;
        if (_pendingBytes is { } bytes)
        {
            string password = _password;
            _password = "";
            _pendingBytes = null;
            await OpenAsync(bytes, _pendingName, password);
        }
    }

    private void CancelPassword()
    {
        _dialog = Dialog.None;
        _pendingBytes = null;
        _password = "";
        _status = _doc == null ? "Ready" : _status;
    }

    private async Task CloseDocumentAsync()
    {
        _searchCts?.Cancel();
        ClearSearchResults();
        _searchedFor = "";
        foreach (var page in _drawn.Keys) CanvasInterop.ClearPage(page);
        _drawn.Clear();
        _failed.Clear();
        _visible.Clear();
        _cache.Clear();
        _renderTimes.Clear();
        if (_doc != null) await _doc.DisposeAsync();
        _doc = null;
    }

    // ---------------------------------------------------------------- rendering

    [JSInvokable]
    public void OnViewport(int[] visible, int current)
    {
        _visible.Clear();
        foreach (int p in visible) _visible.Add(p);
        if (_doc != null && current != _currentPage && current >= 1 && current <= _doc.PageCount)
        {
            _currentPage = current;
            StateHasChanged();
        }
        // Far pages give their canvas memory back (their bitmaps stay in the cache for a while).
        foreach (var page in _drawn.Keys.ToList())
        {
            if (!_visible.Contains(page) && Math.Abs(page - _currentPage) > KeepDrawnPages)
            {
                CanvasInterop.ClearPage(page);
                _drawn.Remove(page);
            }
        }
        _ = PumpAsync();
    }

    [JSInvokable]
    public void OnResize()
    {
        if (_zoomMode != ZoomMode.Custom && Refit()) StateHasChanged();
        else _ = PumpAsync();
    }

    /// <summary>Draws the pages in view that are not drawn at the current scale, nearest the current page first.</summary>
    private async Task PumpAsync()
    {
        if (_pumping || _doc == null) return;
        _pumping = true;
        var doc = _doc;
        try
        {
            while (_doc == doc)
            {
                double scale = RenderScale;
                bool night = _night;
                int page = _visible
                    .Where(p => !(_drawn.TryGetValue(p, out var d) && d == (scale, night)) && !_failed.Contains((p, scale, night)))
                    .OrderBy(p => Math.Abs(p - _currentPage)).ThenBy(p => p)
                    .FirstOrDefault();
                if (page == 0) break;

                if (!_cache.TryGet(page, scale, night, out var bitmap))
                {
                    try
                    {
                        bitmap = await doc.RenderPageAsync(page, scale, night);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
                    {
                        // Diagnostics name the failure type only, never document content.
                        _failed.Add((page, scale, night));
                        _status = $"Page {page} could not be drawn ({ex.GetType().Name}).";
                        StateHasChanged();
                        continue;
                    }
                    if (_doc != doc) break;
                    _cache.Add(page, scale, night, bitmap);
                    Measure(bitmap);
                }
                CanvasInterop.DrawPage(page, bitmap.Pixels, bitmap.Width, bitmap.Height);
                _drawn[page] = (scale, night);
                if (page == _currentPage) ShowPageNotices(bitmap);
                // Let the browser paint before the next page.
                await Task.Delay(1);
            }
        }
        catch (ObjectDisposedException)
        {
            // The document was closed while a page was drawn.
        }
        finally
        {
            _pumping = false;
        }
    }

    private void Measure(ViewerBitmap bitmap)
    {
        double ms = (bitmap.BuildTime + bitmap.RasterTime).TotalMilliseconds;
        _renderTimes.Add(ms);
        double median = _renderTimes.Order().ElementAt(_renderTimes.Count / 2);
        _perf = $"Page {bitmap.Page}: {ms:0} ms (interpret {bitmap.BuildTime.TotalMilliseconds:0}, draw {bitmap.RasterTime.TotalMilliseconds:0}), " +
                $"{bitmap.Width}×{bitmap.Height} px; median {median:0} ms over {_renderTimes.Count}";
        StateHasChanged();
    }

    private void ShowPageNotices(ViewerBitmap bitmap)
    {
        if (_noticeDismissed || _notice != null) return;
        if (bitmap.TextRegionsWithoutFont > 0 && !_localFontsLoaded)
        {
            _noticeIsFonts = true;
            // Shown only for text that the bundled fonts (Liberation Sans, Serif and Mono) do not cover either: Symbol, Dingbats, other scripts.
            _notice = "Some text on this page uses fonts that the PDF does not embed and that the viewer's own fonts do not cover (for example Symbol, Dingbats or other scripts), so that text is missing." +
                      (_canUseLocalFonts ? " This browser can let the page read your device's fonts (they stay on your device)." : " Chrome and Edge can lend the page your device's fonts.");
            StateHasChanged();
        }
        else if (bitmap.ImagesNotDecoded > 0)
        {
            _noticeIsFonts = false;
            _notice = "An image on this page could not be decoded here (four-component CMYK JPEG images are not supported by the web renderer yet).";
            StateHasChanged();
        }
    }

    private void DismissNotice()
    {
        _notice = null;
        _noticeDismissed = true;
    }

    private void Redraw()
    {
        // Pages keep their old bitmap, stretched, until the new one is drawn.
        _failed.Clear();
        _ = PumpAsync();
    }

    // ---------------------------------------------------------------- navigation and zoom

    private void GoToPage(int page)
    {
        if (_doc == null) return;
        page = Math.Clamp(page, 1, _doc.PageCount);
        _currentPage = page;
        CanvasInterop.ScrollToPage(page, 0, smooth: false);
    }

    private void OnPageInput(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)) GoToPage(page);
        else StateHasChanged();
    }

    private void SelectPageInput() => CanvasInterop.FocusElement("page-input", select: true);

    /// <summary>Recomputes a fit zoom from the view size; true when it changed.</summary>
    private bool Refit()
    {
        if (_doc == null || _zoomMode == ZoomMode.Custom) return false;
        var size = CanvasInterop.ViewportSize();
        double width = Math.Max(100, size[0]), height = Math.Max(100, size[1]);
        double maxWidth = _doc.Pages.Max(p => p.Width);
        var current = _doc.Pages[Math.Clamp(_currentPage, 1, _doc.PageCount) - 1];
        double zoom = width / (maxWidth * CssPerPoint);
        if (_zoomMode == ZoomMode.FitPage)
            zoom = Math.Min(width / (current.Width * CssPerPoint), height / (current.Height * CssPerPoint));
        return SetZoom(Math.Clamp(zoom, MinZoom, MaxZoom), _zoomMode);
    }

    private bool SetZoom(double zoom, ZoomMode mode)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _zoomMode = mode;
        if (Math.Abs(zoom - _zoom) < 1e-4) return false;
        _rescale = zoom / _zoom;
        _zoom = zoom;
        Redraw();
        return true;
    }

    private void ZoomIn() => SetZoom(ZoomPresets.FirstOrDefault(p => p > _zoom + 1e-3, Math.Min(MaxZoom, _zoom * 1.25)), ZoomMode.Custom);

    private void ZoomOut() => SetZoom(ZoomPresets.Reverse().FirstOrDefault(p => p < _zoom - 1e-3, Math.Max(MinZoom, _zoom / 1.25)), ZoomMode.Custom);

    private void OnZoomSelect(ChangeEventArgs e)
    {
        switch (e.Value?.ToString())
        {
            case "fit-width":
                _zoomMode = ZoomMode.FitWidth;
                Refit();
                break;
            case "fit-page":
                _zoomMode = ZoomMode.FitPage;
                Refit();
                break;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double z):
                SetZoom(z, ZoomMode.Custom);
                break;
        }
    }

    private void ToggleSidebar()
    {
        _sidebarOpen = !_sidebarOpen;
        CanvasInterop.WriteSetting("sidebar", _sidebarOpen ? "open" : "closed");
        _refit = true;
    }

    private void ToggleNight()
    {
        _night = !_night;
        Redraw();
    }

    private void CycleTheme()
    {
        _theme = _theme switch { "system" => "light", "light" => "dark", _ => "system" };
        CanvasInterop.SetTheme(_theme);
        CanvasInterop.WriteSetting("theme", _theme);
    }

    // ---------------------------------------------------------------- search

    private void OnSearchKey(KeyboardEventArgs e) => _shiftEnter = e.Key == "Enter" && e.ShiftKey;

    private async Task SearchOrNextAsync()
    {
        string query = _query.Trim();
        if (query.Length > 0 && query == _searchedFor && _hits.Count > 0)
            StepHit(_shiftEnter ? -1 : 1);
        else
            await RestartSearchAsync();
    }

    private void ClearSearchResults()
    {
        _hits.Clear();
        _hitsByPage.Clear();
        _currentHit = -1;
    }

    private async Task RestartSearchAsync()
    {
        _searchCts?.Cancel();
        ClearSearchResults();
        _searchedFor = _query.Trim();
        var doc = _doc;
        if (doc == null || _searchedFor.Length == 0)
        {
            _searching = false;
            StateHasChanged();
            return;
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _searching = true;
        _searchPagesDone = 0;
        _panel = Panel.Results;
        StateHasChanged();
        var clock = Stopwatch.StartNew();
        try
        {
            var progress = new Immediate<int>(p =>
            {
                _searchPagesDone = p;
                StateHasChanged();
            });
            await foreach (var hit in doc.SearchAsync(_searchedFor, _matchCase, wholeWord: false, progress, cts.Token))
            {
                int index = _hits.Count;
                _hits.Add(hit);
                if (!_hitsByPage.TryGetValue(hit.Page, out var list)) _hitsByPage[hit.Page] = list = new();
                list.Add((hit, index));
                if (index == 0) SelectHit(0);
            }
            _status = $"Searched {doc.PageCount} {(doc.PageCount == 1 ? "page" : "pages")} in {clock.Elapsed.TotalMilliseconds:0} ms";
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _status = $"Search stopped ({ex.GetType().Name}).";
        }
        finally
        {
            if (_searchCts == cts) _searching = false;
            StateHasChanged();
        }
    }

    private void StepHit(int step)
    {
        if (_hits.Count == 0) return;
        SelectHit(((_currentHit < 0 ? 0 : _currentHit + step) % _hits.Count + _hits.Count) % _hits.Count);
    }

    private void SelectHit(int index)
    {
        if (_doc == null || index < 0 || index >= _hits.Count) return;
        _currentHit = index;
        var hit = _hits[index];
        double offset = hit.Rects.Count > 0 ? hit.Rects[0].Y * _zoom * CssPerPoint : 0;
        _currentPage = hit.Page;
        CanvasInterop.ScrollToPage(hit.Page, offset, smooth: false);
        StateHasChanged();
    }

    // ---------------------------------------------------------------- shortcuts

    [JSInvokable]
    public async Task OnShortcut(string action)
    {
        switch (action)
        {
            case "open": PickFile(); break;
            case "find":
                if (_doc == null) break;
                CanvasInterop.FocusElement("search-input", select: true);
                break;
            case "find-next": StepHit(1); break;
            case "find-previous": StepHit(-1); break;
            case "escape":
                if (_dialog == Dialog.Password) CancelPassword();
                else if (_dialog != Dialog.None) _dialog = Dialog.None;
                else if (_searchedFor.Length > 0)
                {
                    _query = "";
                    await RestartSearchAsync();
                }
                break;
            case "zoom-in": if (_doc != null) ZoomIn(); break;
            case "zoom-out": if (_doc != null) ZoomOut(); break;
            case "fit-width": _zoomMode = ZoomMode.FitWidth; Refit(); break;
            case "fit-page": _zoomMode = ZoomMode.FitPage; Refit(); break;
            case "next-page": GoToPage(_currentPage + 1); break;
            case "previous-page": GoToPage(_currentPage - 1); break;
            case "first-page": GoToPage(1); break;
            case "last-page": GoToPage(_doc?.PageCount ?? 1); break;
            case "toggle-theme": CycleTheme(); break;
            case "toggle-night": ToggleNight(); break;
            case "toggle-sidebar": if (_doc != null) ToggleSidebar(); break;
            case "help": _dialog = _dialog == Dialog.Help ? Dialog.None : Dialog.Help; break;
        }
        StateHasChanged();
    }

    // ---------------------------------------------------------------- device fonts

    private async Task UseLocalFontsAsync()
    {
        if (_module == null || _doc == null) return;
        try
        {
            Directory.CreateDirectory(LocalFontDirectory);
            _localFontCount = 0;
            _status = "Reading your device's fonts…";
            StateHasChanged();
            int found = await _module.InvokeAsync<int>("loadLocalFonts", LocalFontFamilies, 96 * 1024 * 1024);
            if (_localFontCount == 0)
            {
                _notice = found < 0 ? "The browser did not allow the page to read your fonts." : "None of the fonts the engine substitutes with were found on this device.";
                StateHasChanged();
                return;
            }
            _localFontsLoaded = true;
            _lentFonts = new SkiaFontSource(SystemFontCatalog.ForDirectory(LocalFontDirectory));
            await _doc.UseFontsAsync(_lentFonts);
            _cache.Clear();
            _drawn.Clear();
            _failed.Clear();
            _notice = null;
            _status = $"Using {_localFontCount} font files from this device (kept in this tab's memory only).";
            StateHasChanged();
            _ = PumpAsync();
        }
        catch (JSException)
        {
            _notice = "The browser did not allow the page to read your fonts.";
            StateHasChanged();
        }
    }

    /// <summary>One font file from the device, written to the runtime's in-memory file system for the font catalog.</summary>
    [JSInvokable]
    public void OnLocalFont(string name, byte[] data)
    {
        if (data.Length < 12) return;
        // The catalog reads TrueType files and collections; CFF-flavoured OpenType ("OTTO") is skipped.
        uint tag = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
        string? extension = tag switch { 0x00010000 or 0x74727565 => ".ttf", 0x74746366 => ".ttc", _ => null };
        if (extension == null) return;
        string safe = new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        File.WriteAllBytes(Path.Combine(LocalFontDirectory, safe + extension), data);
        _localFontCount++;
    }

    public async ValueTask DisposeAsync()
    {
        _searchCts?.Cancel();
        if (_doc != null) await _doc.DisposeAsync();
        if (_module != null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _self?.Dispose();
    }

    /// <summary>A progress sink that runs its callback at once (Progress&lt;T&gt; would post it).</summary>
    private sealed class Immediate<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
