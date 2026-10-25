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
        _passwordWasWrong = false;
        _dialog = Dialog.None;
        if (bytes.Length == 0)
        {
            FailOpen(EmbedErrors.NotAPdf, "The file is empty.");
            return;
        }
        _config = open.Config;
        _fileId = open.FileId;
        ApplyConfig();
        await OpenAsync(bytes, open.FileName, null);
    }

    /// <summary>The document is open: the host's previewFile promise resolves.</summary>
    private void EmbedOpened()
    {
        if (_openRequest == 0) return;
        int request = _openRequest;
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
    }

    private void FailOpen(string code, string message)
    {
        if (_openRequest == 0) return;
        Post(EmbedProtocol.Error(_openRequest, code, message));
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
        switch (call.Method)
        {
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
