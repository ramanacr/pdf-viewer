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
    private CancellationTokenSource? _autoSaveCts;

    /// <summary>
    /// How long the viewer waits for the host page's answer before it says that the save is still
    /// going on. The save stays in flight until the host page answers or the frame closes.
    /// </summary>
    private const int SaveSlowMs = 120000;

    private bool ShowsSaveButton => Listening && _save.Enabled && _save.ShowSaveButton && _doc != null;

    /// <summary>
    /// Saves (posts the document to the host page's SAVE_API callback, or downloads it on Ctrl+S
    /// without one). Returns false when nothing was saved or posted: no document, a save under
    /// way already, or edits that could not be written into the document (the reader sees an
    /// error then, never "Saved"). One save runs at a time, from before the edits are awaited
    /// until the host page answers, so a blur, an autosave and Ctrl+S cannot post twice.
    /// </summary>
    private async Task<bool> SaveAsync(string reason)
    {
        var doc = _doc;
        if (doc == null) return false;
        if (!Listening || !_save.Enabled)
        {
            if (reason is not "shortcut" || Embedded && !_config.ShowDownload) return false;
            bool ready = await WaitForEditsAsync();
            if (_doc != doc) return false;
            if (!ready)
            {
                NotSaved();
                return false;
            }
            DownloadEdited();
            return true;
        }
        if (!_edits.TryBeginSave()) return false;
        bool posted = false;
        try
        {
            _status = "Saving…";
            StateHasChanged();
            bool current = await WaitForEditsAsync();
            // Another document was opened (or this one closed) meanwhile: its state is not this save's.
            if (_doc != doc || !_edits.Saving) return false;
            int saveId = current ? _edits.Post() : 0;
            if (saveId == 0)
            {
                NotSaved();
                return false;
            }
            // The bytes posted are the ones Post recorded the version of: nothing runs in between.
            CanvasInterop.EmbedPostBytes(EmbedProtocol.SaveRequest(saveId, DownloadName, _fileId, reason), doc.Bytes);
            posted = true;
            _ = SaveSlowAsync(saveId);
            return true;
        }
        finally
        {
            if (!posted && _doc == doc) _edits.EndSave();
            StateHasChanged();
        }
    }

    /// <summary>The latest edits are not in the document's bytes: nothing is posted, and the reader is told.</summary>
    private void NotSaved()
    {
        _status = "Not saved";
        _error = "The file was not saved: the latest changes could not be written into the document.";
    }

    private async Task SaveSlowAsync(int saveId)
    {
        await Task.Delay(SaveSlowMs);
        if (_edits.Saving && _edits.PendingSave == saveId)
        {
            _status = "Still saving… (the page has not answered yet)";
            StateHasChanged();
        }
    }

    /// <summary>The host page's answer to a save (saveResult).</summary>
    private string SaveResult(EmbedCall call)
    {
        if (_edits.Answer(call.SaveId, call.Saved))
        {
            if (call.Saved)
            {
                if (call.SavedFileName is { } name) _fileName = name;
                _status = IsDirty ? "Saved (newer changes are not saved yet)" : "Saved";
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
                if (IsDirty && !Saving) await SaveAsync("autosave");
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
        if (_save.Enabled && _save.FocusPolling && IsDirty && !Saving) await SaveAsync("focus");
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
