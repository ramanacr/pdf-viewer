using Android.Widget;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// Unsaved edits that survive Android ending the app in the background. The saved state (the
/// Bundle) reopens the document at the same place (MainActivity.cs); the edits themselves (the
/// annotations and form values of <see cref="ViewerEditSession"/>) are kept in a file in this app's
/// private storage (<c>files/edits/pending.json</c>, a <see cref="ViewerEditSnapshot"/>), written at
/// <c>onSaveInstanceState</c> and <c>onStop</c> when the session has unsaved edits. A new process
/// that reopens the document from the saved state gives the edits back, pending, when the file is
/// still the bytes they were made on (or those bytes with incremental updates after them, as a save
/// here appends one); otherwise the reader is told and they are dropped. The file is deleted when
/// the edits are saved or discarded, when another document opens, and when the app starts without
/// a saved state. It never goes into a log. An encrypted document's edits are not kept: they would
/// be stored unencrypted. Backups are off (AndroidManifest.xml), so the file never leaves the device.
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The SHA-256 of the open document's bytes as opened, computed on a worker thread at open.</summary>
    private Task<string>? _openedHash;

    /// <summary>The session version last kept, so an unchanged session is not written again.</summary>
    private int _keptVersion = -1;

    private string KeptEditsPath => System.IO.Path.Combine(FilesDir!.AbsolutePath, "edits", "pending.json");

    /// <summary>
    /// Keeps the open document's unsaved edits in the private file, or deletes the file when there are
    /// none. Synchronous: Android may end the process right after <c>onStop</c>. Never throws.
    /// </summary>
    private void KeepEdits()
    {
        // During a save into the file the saved state keeps no document (MainActivity.Edits.cs).
        if (_savingBack) return;
        var session = _session;
        var doc = _doc;
        if (session == null || doc == null || _uri == null || !session.IsDirty || doc.IsEncrypted || _openedHash == null)
        {
            ForgetKeptEdits();
            return;
        }
        if (session.Version == _keptVersion && File.Exists(KeptEditsPath)) return;
        string path = KeptEditsPath;
        string temporary = path + ".new";
        try
        {
            string json = session.Snapshot(_uri.ToString()!, _openedHash.GetAwaiter().GetResult()).ToJson();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            _keptVersion = session.Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Not kept: nothing is logged (the edits hold the reader's text). The edits stay in memory.
            TryDelete(temporary);
        }
    }

    /// <summary>The kept edits for the document at <paramref name="location"/>, or null (none, another document, unreadable).</summary>
    private ViewerEditSnapshot? ReadKeptEdits(string location)
    {
        try
        {
            if (!File.Exists(KeptEditsPath)) return null;
            var kept = ViewerEditSnapshot.FromJson(File.ReadAllText(KeptEditsPath));
            if (kept.Location == location) return kept;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        ForgetKeptEdits();
        return null;
    }

    /// <summary>Deletes the kept edits (saved, discarded, another document, or a start without a saved state).</summary>
    private void ForgetKeptEdits()
    {
        _keptVersion = -1;
        TryDelete(KeptEditsPath);
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

    /// <summary>
    /// The bytes to open for kept edits, from the file as read now (on a worker thread: it hashes),
    /// or null when the file is no longer what the edits were made on.
    /// </summary>
    private static byte[]? BytesForKeptEdits(ViewerEditSnapshot kept, byte[] file) => kept.OriginalOf(file);

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
            Toast.MakeText(this, "Your unsaved changes could not be restored.", ToastLength.Long)?.Show();
            return;
        }
        if (!restored) return;
        Toast.MakeText(this, "Your unsaved changes were restored. Save to keep them in the file.", ToastLength.Long)?.Show();
        _ = WriteAsync(session);
    }
}
