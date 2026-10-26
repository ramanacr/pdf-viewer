using System.Text.Json;
using Microsoft.JSInterop;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// The viewer embedded in a host page by the embed SDK (wwwroot/pdfviewer-embed.js): the frame's
/// URL (index.html?embed=1&amp;mode=…&amp;origin=…) puts it in embed mode before the first render,
/// the host sends the file and the config, then calls the APIs; the viewer answers and sends the
/// events. Messages are checked in viewer.js (window and origin) and in EmbedProtocol (content).
/// </summary>
public sealed partial class Viewer
{
    /// <summary>The frame's URL settings; null in the demo.</summary>
    private EmbedFrame? _frame;

    /// <summary>The config of the current file (the demo shows everything).</summary>
    private EmbedConfig _config = EmbedConfig.Demo;

    private int _openRequest;      // the open request that waits for the document, 0 when none
    private int _documentId;       // the open request that opened the shown document
    private string? _fileId;
    private bool _passwordWasWrong;
    private int _reportedPage;
    private double _reportedZoom;
    private int _searchId;
    private bool _openReadOnly;    // the file was given with hasReadOnlyAccess

    private bool Embedded => _frame != null;

    /// <summary>Embedded, with a host origin to listen to and write to.</summary>
    private bool Listening => _frame?.Origin != null;

    private bool IsInline => _frame?.Mode == EmbedMode.InLine;

    /// <summary>Reads the frame's URL. Called before the first render, so the demo chrome never shows in a host page.</summary>
    private void ReadFrameUrl()
    {
        _frame = EmbedFrame.FromQuery(new Uri(Nav.Uri).Query);
        if (_frame == null) return;
        _config = new EmbedConfig
        {
            Mode = _frame.Mode,
            FocusOnRendering = _frame.Mode is EmbedMode.FullWindow or EmbedMode.LightBox,
            ViewMode = EmbedViewMode.FitWidth,
            EnableAnnotationApis = false,
            IncludePdfAnnotations = false,
            DownloadWithAnnotations = false,
            PrintWithAnnotations = false,
        };
        _sidebarOpen = false;
        _status = "";
        if (_frame.Origin == null)
            _error = "This page was opened to be embedded, but its address does not name a valid host page origin. Load it with pdfviewer-embed.js.";
    }

    private void Post(string json)
    {
        if (Listening) CanvasInterop.EmbedPost(json);
    }

    private void Emit(string type, Action<Utf8JsonWriter>? data = null)
    {
        if (Listening && _documentId != 0) Post(EmbedProtocol.Event(type, data));
    }

    /// <summary>Applies the config of a file that is about to open.</summary>
    private void ApplyConfig()
    {
        CanvasInterop.SetTheme(_config.Theme);
        _theme = _config.Theme;
        _zoomMode = _config.FitPage ? ZoomMode.FitPage : ZoomMode.FitWidth;
        _twoColumns = _config.TwoColumns;
        _panel = _config.ShowBookmarks ? Panel.Outline : _config.ShowThumbnails ? Panel.Thumbnails : Panel.Results;
    }

    [JSInvokable]
    public async Task OnEmbedOpen(string json, byte[] bytes)
    {
        if (_frame == null) return;
        EmbedOpen open;
        try
        {
            if (EmbedProtocol.Parse(json, _frame.Mode) is not EmbedOpen o) return;
            open = o;
        }
        catch (EmbedProtocolException ex)
        {
            if (ex.RequestId > 0) Post(EmbedProtocol.Error(ex.RequestId, ex.Code, ex.Message));
            return;
        }
        if (_openRequest != 0) Post(EmbedProtocol.Error(_openRequest, EmbedErrors.Closed, "Another file was opened before this one was ready."));
        _openRequest = open.RequestId;
        // An open still running is stale from now on: it may not show its document or answer the host.
        _opens.Supersede();
        _passwordWasWrong = false;
        _dialog = Dialog.None;
        // A ranged open still waiting (for a password, or for its bytes) is replaced: its requests end.
        _pendingRange?.Reader.Close();
        _pendingRange = null;
        _openingReader?.Close();
        _openingReader = null;
        _pendingBytes = null;
        if (bytes.Length == 0 && open.Range == null)
        {
            FailOpen(open.RequestId, EmbedErrors.NotAPdf, "The file is empty.");
            return;
        }
        _config = open.Config;
        _fileId = open.FileId;
        _openReadOnly = open.ReadOnly;
        _save = open.Save;
        ApplyConfig();
        if (open.Range is { } range)
        {
            // The host page sent what it already has (the end of the file, or Adobe's initial
            // buffer); the viewer asks it for the other ranges as pages need them.
            var reader = new EmbedRangeReader(open.RequestId, range.Length, Post);
            _openingReader = reader;
            var options = new PdfEngine.Vector.Parsing.PdfRangedOptions
            {
                InitialData = bytes.Length > 0 ? [(range.InitialOffset, bytes)] : [],
                MaxConcurrentRequests = EmbedProtocol.MaxRangeRequestsInFlight,
                MaxRequestBytes = EmbedProtocol.MaxRangeBytes,
            };
            await OpenRangedAsync(new RangedFile(reader, options), open.FileName, null);
            if (_openingReader == reader) _openingReader = null;
            return;
        }
        await OpenAsync(bytes, open.FileName, null);
    }

    private EmbedRangeReader? _openingReader;  // the reader of a ranged open in progress

    /// <summary>The host page's answer to a fetchRange request, with the bytes.</summary>
    [JSInvokable]
    public void OnEmbedRange(string json, byte[] bytes)
    {
        if (_frame == null) return;
        EmbedRangeReply reply;
        try
        {
            if (EmbedProtocol.Parse(json, _frame.Mode) is not EmbedRangeReply r) return;
            reply = r;
        }
        catch (EmbedProtocolException)
        {
            return; // a malformed answer: its request times out and fails
        }
        // Only a request that is waiting, of the file being opened or shown, is answered.
        if (_openingReader?.Receive(reply, bytes) == true) return;
        if (_pendingRange?.Reader.Receive(reply, bytes) == true) return;
        _docReader?.Receive(reply, bytes);
    }

    /// <summary>API calls that only move or ask about the view: they answer at once, also while a ranged file is still loading.</summary>
    private static bool IsViewCall(EmbedMethod method) => method is EmbedMethod.GetCurrentPage or EmbedMethod.GotoLocation
        or EmbedMethod.ZoomIn or EmbedMethod.ZoomOut or EmbedMethod.SetZoomLevel or EmbedMethod.GetZoomLimits
        or EmbedMethod.GetSelectedContent or EmbedMethod.SetSaveOptions or EmbedMethod.SaveResult;

    /// <summary>The document of open request <paramref name="request"/> is open: the host's previewFile promise resolves.</summary>
    private void EmbedOpened(int request)
    {
        if (request == 0 || request != _openRequest) return;
        _openRequest = 0;
        _documentId = request;
        _reportedPage = 0;
        _reportedZoom = 0;
        // Only the panels this config shows; open when the document has an outline to show.
        _sidebarOpen = _config.HasSidePanel && _config.ShowBookmarks && _outline.Count > 0;
        _panel = _config.ShowBookmarks && _outline.Count > 0 ? Panel.Outline : _config.ShowThumbnails ? Panel.Thumbnails : Panel.Results;
        _refit = true;
        Post(EmbedProtocol.Ok(request, w =>
        {
            w.WriteStartObject();
            w.WriteNumber("numPages", _doc?.PageCount ?? 0);
            w.WriteBoolean("focus", _config.FocusOnRendering);
            w.WriteEndObject();
        }));
        Emit(EmbedEvents.DocumentOpen, w =>
        {
            w.WriteString("fileName", _fileName);
            w.WriteNumber("numPages", _doc?.PageCount ?? 0);
        });
        if (_config.FocusOnRendering) CanvasInterop.FocusElement("pages", select: false);
        RestartAutoSave();
    }

    /// <summary>Open request <paramref name="request"/> failed; nothing when a newer request replaced it (that one was told already).</summary>
    private void FailOpen(int request, string code, string message)
    {
        if (request == 0 || request != _openRequest) return;
        Post(EmbedProtocol.Error(request, code, message));
        _openRequest = 0;
    }

    [JSInvokable]
    public void OnContentHeight(double height) => Post(EmbedProtocol.Size(height));

    [JSInvokable]
    public void OnScrollHost(double top) => Post(EmbedProtocol.Scroll(top));

    /// <summary>After a render: the page or zoom the host was last told about changed.</summary>
    private void ReportView()
    {
        if (!Listening || _doc == null || _documentId == 0) return;
        if (_currentPage != _reportedPage)
        {
            _reportedPage = _currentPage;
            Emit(EmbedEvents.PageView, w => w.WriteNumber("pageNumber", _currentPage));
        }
        if (Math.Abs(_zoom - _reportedZoom) > 1e-6)
        {
            _reportedZoom = _zoom;
            Emit(EmbedEvents.ZoomChange, w => w.WriteNumber("zoom", Math.Round(_zoom, 4)));
        }
    }

    private void PostSearchUpdate()
    {
        if (!Listening || _searchId == 0) return;
        int page = _currentHit >= 0 && _currentHit < _hits.Count ? _hits[_currentHit].Page : 0;
        Post(EmbedProtocol.SearchUpdate(_searchId, page, _currentHit + 1, _hits.Count, completed: !_searching));
    }

    [JSInvokable]
    public async Task OnEmbedCall(string json)
    {
        if (_frame == null) return;
        EmbedCall call;
        try
        {
            if (EmbedProtocol.Parse(json, _frame.Mode) is not EmbedCall c) return;
            call = c;
        }
        catch (EmbedProtocolException ex)
        {
            if (ex.RequestId > 0) Post(EmbedProtocol.Error(ex.RequestId, ex.Code, ex.Message));
            return;
        }
        var doc = _doc;
        if (doc == null || call.Document != _documentId)
        {
            Post(EmbedProtocol.Error(call.RequestId, EmbedErrors.NoDocument, "No document is open, or another one replaced it."));
            return;
        }
        try
        {
            if (!IsViewCall(call.Method) && !doc.IsComplete)
            {
                // The rest of a ranged file first: the annotations, search and metadata need all of it.
                await WaitReadyAsync(doc);
                if (_doc != doc || call.Document != _documentId)
                {
                    Post(EmbedProtocol.Error(call.RequestId, EmbedErrors.NoDocument, "No document is open, or another one replaced it."));
                    return;
                }
            }
            string reply = await AnswerAsync(call, doc);
            if (reply.Length > 0) Post(reply);
        }
        catch (ObjectDisposedException)
        {
            Post(EmbedProtocol.Error(call.RequestId, EmbedErrors.NoDocument, "The document was closed."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type names only, never document content.
            Post(EmbedProtocol.Error(call.RequestId, EmbedErrors.ViewerError, $"The viewer failed ({ex.GetType().Name})."));
        }
        StateHasChanged();
    }

    private async Task<string> AnswerAsync(EmbedCall call, ViewerDocument doc)
    {
        int id = call.RequestId;
        if (AnswerAnnotationCall(call) is { } annotationReply) return annotationReply;
        switch (call.Method)
        {
            case EmbedMethod.SetSaveOptions:
                SetSaveOptions(call.SaveOptions!);
                return EmbedProtocol.Ok(id, null);
            case EmbedMethod.SaveResult:
                return SaveResult(call);

            case EmbedMethod.GetPdfMetadata:
                return EmbedProtocol.Ok(id, EmbedProtocol.Metadata(await doc.GetMetadataAsync(), _fileName, _fileId));

            case EmbedMethod.GetCurrentPage:
                return EmbedProtocol.Ok(id, w => w.WriteNumberValue(_currentPage));

            case EmbedMethod.GotoLocation:
                if (call.Page > doc.PageCount)
                    return EmbedProtocol.Error(id, EmbedErrors.InvalidArgument, $"gotoLocation: the document has {doc.PageCount} pages.");
                GoToLocation(call.Page, call.X, call.Y);
                return EmbedProtocol.Ok(id, null);

            case EmbedMethod.GetSelectedContent:
                if (!doc.CanCopy) return EmbedProtocol.Error(id, EmbedErrors.NotAllowed, "This document does not allow copying its text.");
                string text = _selection is { IsEmpty: false } s ? s.Text : "";
                return EmbedProtocol.Ok(id, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "text");
                    w.WriteString("data", text);
                    w.WriteEndObject();
                });

            case EmbedMethod.ZoomIn:
                ZoomIn();
                return ZoomReply(id);
            case EmbedMethod.ZoomOut:
                ZoomOut();
                return ZoomReply(id);
            case EmbedMethod.SetZoomLevel:
                SetZoom(call.Level, ZoomMode.Custom);
                return ZoomReply(id);
            case EmbedMethod.GetZoomLimits:
                return EmbedProtocol.Ok(id, w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("minZoom", MinZoom);
                    w.WriteNumber("maxZoom", MaxZoom);
                    w.WriteEndObject();
                });

            case EmbedMethod.Search:
            {
                if (!_config.EnableSearchApis) return EmbedProtocol.Error(id, EmbedErrors.NotEnabled, "Set enableSearchAPIs: true in the config of previewFile to use search().");
                _query = call.Term!;
                var search = RestartSearchAsync();
                int searchId = _searchId;
                Post(EmbedProtocol.Ok(id, w =>
                {
                    w.WriteStartObject();
                    w.WriteNumber("searchId", searchId);
                    w.WriteEndObject();
                }));
                _ = search; // runs on; its progress goes to the host as search updates
                return "";
            }
            case EmbedMethod.SearchNext or EmbedMethod.SearchPrevious or EmbedMethod.SearchClear:
                if (call.SearchId != _searchId || _searchedFor.Length == 0)
                    return EmbedProtocol.Error(id, EmbedErrors.SearchEnded, "This search was replaced or cleared.");
                if (call.Method == EmbedMethod.SearchClear)
                {
                    _query = "";
                    await RestartSearchAsync();
                }
                else
                {
                    StepHit(call.Method == EmbedMethod.SearchNext ? 1 : -1);
                }
                return EmbedProtocol.Ok(id, null);
        }
        return EmbedProtocol.Error(id, EmbedErrors.InvalidArgument, "Unknown method.");
    }

    private string ZoomReply(int id) => EmbedProtocol.Ok(id, w => w.WriteNumberValue(Math.Round(_zoom, 4)));

    /// <summary>Scrolls a point of a page (points from its top-left corner) to the top-left of the view.</summary>
    private void GoToLocation(int page, double x, double y)
    {
        if (_doc == null) return;
        _currentPage = Math.Clamp(page, 1, _doc.PageCount);
        CanvasInterop.ScrollToLocation(_currentPage, x * _zoom * CssPerPoint, y * _zoom * CssPerPoint);
    }
}
