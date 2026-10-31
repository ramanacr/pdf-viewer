using Android.App;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// Unsaved edits that survive Android ending the app in the background. The saved state (the
/// Bundle) reopens the document at the same place (MainActivity.cs); the edits themselves (the
/// annotations and form values of <see cref="ViewerEditSession"/>) are kept in a file in this app's
/// private storage (<c>files/edits/pending.json</c>, a <see cref="ViewerEditSnapshot"/> of at most
/// <see cref="ViewerEditSnapshot.MaxBytes"/>), written at <c>onSaveInstanceState</c> and
/// <c>onStop</c> when the session has unsaved edits. The snapshot is taken on the UI thread (it only
/// collects references); the JSON is made and written on a worker thread, one file operation at a
/// time, so neither hashing nor writing holds up the UI.
/// <para>
/// A new process that reopens the document from the saved state gives the edits back, pending,
/// when the file is still the bytes they were made on (or those bytes with incremental updates
/// after them, as a save here appends one). A document opened in any other way whose bytes fit kept
/// edits offers them: restore or discard. Kept edits for another document are left alone until
/// this app's own edits replace them. The file is deleted when its edits are saved (before the
/// write starts, so a crash after the save cannot give them back twice) or discarded, when a
/// restore fails, and when the document can no longer be read. It never goes into a log. An
/// encrypted document's edits are not kept: they would be stored unencrypted. Backups are off
/// (AndroidManifest.xml), so the file never leaves the device.
/// </para>
/// <para>
/// <c>files/edits/made-here.txt</c> keeps the ids of the annotations made on this device (at most
/// <see cref="MaxMadeHere"/>), so that a reply is the reader's own (Edit, Delete) only when it was
/// written here, not because an imported file carries the same name.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>Most ids of annotations made on this device that are kept.</summary>
    private const int MaxMadeHere = 10000;

    /// <summary>The SHA-256 of the open document's bytes as opened (<see cref="PdfViewer.Web.Engine.ViewerDocument.OriginalBytes"/>), computed on a worker thread at open.</summary>
    private Task<string>? _openedHash;

    /// <summary>The session version last kept, so an unchanged session is not written again.</summary>
    private int _keptVersion = -1;

    /// <summary>The kept edits on storage (if any) are the open document's: this session wrote or restored them.</summary>
    private bool _keptOurs;

    /// <summary>File operations on kept edits, one after another on a worker thread.</summary>
    private Task _keptIo = Task.CompletedTask;
    private readonly object _keptGate = new();

    private string EditsFolder => System.IO.Path.Combine(FilesDir!.AbsolutePath, "edits");
    private string KeptEditsPath => System.IO.Path.Combine(EditsFolder, "pending.json");
    private string MadeHerePath => System.IO.Path.Combine(EditsFolder, "made-here.txt");

    /// <summary>Runs <paramref name="io"/> after the file operations queued before it, on a worker thread. It must not throw.</summary>
    private void QueueKeptIo(Action io)
    {
        lock (_keptGate) _keptIo = _keptIo.ContinueWith(_ => io(), TaskScheduler.Default);
    }

    /// <summary>
    /// Keeps the open document's unsaved edits in the private file (written on a worker thread), or
    /// deletes this document's kept edits when there are none. Never throws, never blocks on I/O.
    /// </summary>
    private void KeepEdits()
    {
        // During a save into the file the saved state keeps no document (MainActivity.Edits.cs).
        if (_savingBack) return;
        var session = _session;
        var doc = _doc;
        KeepMadeHere(session);
        if (session == null || doc == null || _uri == null || !session.IsDirty || doc.IsEncrypted || _openedHash is not { } hash)
        {
            ForgetOurKeptEdits();
            return;
        }
        if (session.Version == _keptVersion && _keptOurs) return;
        // Only references are collected here; the hash, the JSON and the write are on the worker thread.
        var snapshot = session.Snapshot(_uri.ToString()!, "");
        _keptVersion = session.Version;
        _keptOurs = true;
        string path = KeptEditsPath, folder = EditsFolder;
        QueueKeptIo(() =>
        {
            string temporary = path + ".new";
            try
            {
                byte[]? json = (snapshot with { OriginalHash = hash.GetAwaiter().GetResult() }).ToUtf8();
                if (json == null)
                {
                    // Larger than a snapshot may be: not kept (and an older one would be out of date).
                    File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(temporary, json);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                           or System.Security.Cryptography.CryptographicException)
            {
                // Not kept: nothing is logged (the edits hold the reader's text). The edits stay in memory.
                TryDelete(temporary);
            }
        });
    }

    /// <summary>The ids made on this device, written with the kept edits (on the worker thread).</summary>
    private void KeepMadeHere(ViewerEditSession? session)
    {
        if (session == null || session.MadeHere.Count == 0) return;
        var ids = session.MadeHere.TakeLast(MaxMadeHere).ToArray();
        string path = MadeHerePath, folder = EditsFolder;
        QueueKeptIo(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllLines(path + ".new", ids);
                File.Move(path + ".new", path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });
    }

    /// <summary>The ids made on this device (call on a worker thread); none when the file is missing or damaged.</summary>
    private string[] ReadMadeHere()
    {
        try
        {
            var file = new FileInfo(MadeHerePath);
            if (!file.Exists || file.Length > 64L * MaxMadeHere) return [];
            return File.ReadAllLines(file.FullName).Where(l => l.Length is > 0 and <= ViewerAnnotationStore.MaxIdLength).TakeLast(MaxMadeHere).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The kept edits on storage, or null (none, unreadable, larger than a snapshot may be). Reads
    /// the file: call on a worker thread. A damaged file is deleted.
    /// </summary>
    private ViewerEditSnapshot? ReadKeptEditsNow()
    {
        string path = KeptEditsPath;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length <= ViewerEditSnapshot.MaxBytes) return ViewerEditSnapshot.FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        TryDelete(path);
        return null;
    }

    /// <summary>Deletes the kept edits, whichever document they are for (a failed restore, a declined offer, an unreadable file).</summary>
    private void ForgetKeptEdits()
    {
        _keptVersion = -1;
        _keptOurs = false;
        string path = KeptEditsPath;
        QueueKeptIo(() => TryDelete(path));
    }

    /// <summary>Deletes the kept edits when they are the open document's (saved, discarded, left); another document's stay.</summary>
    private void ForgetOurKeptEdits()
    {
        if (_keptOurs) ForgetKeptEdits();
        else _keptVersion = -1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>After Android ended the process: the kept edits (read on a worker thread) go with the document of the saved state.</summary>
    private async Task ReopenAfterProcessDeathAsync(global::Android.Net.Uri uri, string location, (PageAnchor TopLeft, double Zoom) view)
    {
        var kept = await Task.Run(ReadKeptEditsNow);
        await OpenUriAsync(uri, view, restore: kept?.Location == location ? kept : null);
    }

    /// <summary>
    /// A document opened without a saved state whose bytes fit kept edits: restore them or discard
    /// them (the dialog cannot be dismissed without a choice).
    /// </summary>
    private void OfferKeptEdits(ViewerEditSnapshot kept, Action restore, Action discard)
    {
        new AlertDialog.Builder(this)
            .SetTitle("Unsaved changes found")!
            .SetMessage("This file has changes from an earlier session that were not saved. Restore them? Discard deletes them.")!
            .SetCancelable(false)!
            .SetPositiveButton("Restore", (_, _) => restore())!
            .SetNegativeButton("Discard", (_, _) =>
            {
                ForgetKeptEdits();
                discard();
            })!
            .Show();
    }

    /// <summary>Gives the kept edits back to the session just started, then has the engine write them.</summary>
    private void RestoreKeptEdits(ViewerEditSession session, ViewerEditSnapshot kept)
    {
        bool restored;
        try
        {
            restored = session.Restore(kept);
        }
        catch (ViewerAnnotationException)
        {
            // The message would name an annotation's id only; the reader is told in plain words.
            ForgetKeptEdits();
            Toast.MakeText(this, "Your unsaved changes could not be restored.", ToastLength.Long)?.Show();
            return;
        }
        if (!restored)
        {
            ForgetKeptEdits();
            return;
        }
        _keptOurs = true;
        Toast.MakeText(this, "Your unsaved changes were restored. Save to keep them in the file.", ToastLength.Long)?.Show();
        _ = WriteAsync(session);
    }
}
