using Microsoft.JSInterop;
using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Saving through the host page's SAVE_API callback: Save, Ctrl+S, autosave and focus polling
/// send the document with the reader's annotations and form data (the engine's incremental
/// update) to the host page as a transferred ArrayBuffer; the host's answer decides the message.
/// Without a SAVE_API callback, Ctrl+S downloads the annotated document.
/// </summary>
public sealed partial class Viewer
{
    private EmbedSaveOptions _save = EmbedSaveOptions.None;
    private int _saveId, _pendingSave, _pendingSaveVersion;
    private bool _saving;
    private CancellationTokenSource? _autoSaveCts;

    /// <summary>How long the viewer waits for the host page's answer to a save.</summary>
    private const int SaveTimeoutMs = 120000;

    private bool ShowsSaveButton => Listening && _save.Enabled && _save.ShowSaveButton && _doc != null;

    private async Task SaveAsync(string reason)
    {
        var doc = _doc;
        if (doc == null) return;
        if (!Listening || !_save.Enabled)
        {
            if (reason is "shortcut" && (!Embedded || _config.ShowDownload))
            {
                await WaitForEditsAsync();
                DownloadEdited();
            }
            return;
        }
        if (_saving) return;
        await WaitForEditsAsync();
        if (_doc != doc) return;
        _saving = true;
        _pendingSave = ++_saveId;
        _pendingSaveVersion = _editVersion;
        _status = "Saving…";
        StateHasChanged();
        CanvasInterop.EmbedPostBytes(EmbedProtocol.SaveRequest(_pendingSave, DownloadName, _fileId, reason), doc.Bytes);
        _ = SaveTimeoutAsync(_pendingSave);
    }

    private async Task SaveTimeoutAsync(int saveId)
    {
        await Task.Delay(SaveTimeoutMs);
        if (_pendingSave == saveId)
        {
            _pendingSave = 0;
            _saving = false;
            _status = "The page did not answer the save.";
            StateHasChanged();
        }
    }

    /// <summary>The host page's answer to a save (saveResult).</summary>
    private string SaveResult(EmbedCall call)
    {
        if (call.SaveId == _pendingSave && _saving)
        {
            _pendingSave = 0;
            _saving = false;
            if (call.Saved)
            {
                _savedVersion = _pendingSaveVersion;
                if (call.SavedFileName is { } name) _fileName = name;
                _status = "Saved";
            }
            else
            {
                _status = "The file was not saved.";
            }
        }
        return EmbedProtocol.Ok(call.RequestId, null);
    }

    private void SetSaveOptions(EmbedSaveOptions options)
    {
        _save = options;
        RestartAutoSave();
    }

    private void RestartAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts = null;
        if (!_save.Enabled || _save.AutoSaveSeconds <= 0 || _doc == null) return;
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = AutoSaveAsync(_save.AutoSaveSeconds, cts.Token);
    }

    private async Task AutoSaveAsync(int seconds, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token);
                if (IsDirty && !_saving) await SaveAsync("autosave");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The viewer lost the focus (the reader went back to the host page): with focus polling, unsaved edits are saved.</summary>
    [JSInvokable]
    public async Task OnViewerBlur()
    {
        if (_save.Enabled && _save.FocusPolling && IsDirty && !_saving) await SaveAsync("focus");
    }

    /// <summary>The document with the reader's annotations and form data, as a download.</summary>
    private void DownloadEdited()
    {
        if (_doc == null) return;
        CanvasInterop.DownloadBytes(_doc.Bytes, DownloadName);
        _status = $"Saved {DownloadName}";
        Emit(EmbedEvents.DocumentDownload, w => w.WriteString("fileName", DownloadName));
    }
}
