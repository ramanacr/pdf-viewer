using Microsoft.JSInterop;
using PdfViewer.Web.Engine.Annotations;
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
        if (_session is not { } session || session.IsSaving) return false;
        try
        {
            _status = "Saving…";
            StateHasChanged();
            await PrepareFontsAsync(session);
            // Another document was opened (or this one closed), or another save began, meanwhile.
            if (_session != session || session.IsSaving) return false;
            // The session writes what is left, then hands out the bytes with every edit (or none).
            ViewerSave? save;
            try
            {
                save = await session.BeginSaveAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The session ended the save itself. Type names only, never document content.
                if (_session == session) NotPosted(ex);
                return false;
            }
            if (_session != session)
            {
                if (save != null) session.EndSave(save, saved: false);
                return false;
            }
            if (save == null)
            {
                NotSaved();
                return false;
            }
            try
            {
                CanvasInterop.EmbedPostBytes(EmbedProtocol.SaveRequest(save.Id, DownloadName, _fileId, reason), save.Bytes);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing reached the host page: the save ends, or the session would stay saving.
                session.EndSave(save, saved: false);
                NotPosted(ex);
                return false;
            }
            _ = SaveSlowAsync(session, save.Id);
            return true;
        }
        finally
        {
            StateHasChanged();
        }
    }

    /// <summary>The document could not be handed to the host page: nothing was saved, and the reader is told.</summary>
    private void NotPosted(Exception ex)
    {
        _status = "Not saved";
        _error = $"The file was not saved: it could not be sent to the page ({ex.GetType().Name}).";
    }

    /// <summary>The latest edits are not in the document's bytes: nothing is posted, and the reader is told.</summary>
    private void NotSaved()
    {
        _status = "Not saved";
        _error = "The file was not saved: the latest changes could not be written into the document.";
    }

    private async Task SaveSlowAsync(ViewerEditSession session, int saveId)
    {
        await Task.Delay(SaveSlowMs);
        if (_session == session && session.IsSaving && session.PendingSave == saveId)
        {
            _status = "Still saving… (the page has not answered yet)";
            StateHasChanged();
        }
    }

    /// <summary>The host page's answer to a save (saveResult).</summary>
    private string SaveResult(EmbedCall call)
    {
        if (_session?.EndSave(call.SaveId, call.Saved) == true)
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
