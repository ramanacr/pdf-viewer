using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Widget;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Android;

/// <summary>
/// Unsaved edits that survive Android ending the app in the background. The saved state (the
/// Bundle) reopens the document at the same place (MainActivity.cs); the edits themselves (the
/// annotations and form values of <see cref="ViewerEditSession"/>) are kept in a file in this app's
/// private storage, one per document location, since several windows may each have a document with
/// edits (MainActivity.Windows.cs): <c>files/edits/&lt;key&gt;.&lt;length&gt;.&lt;hash&gt;.json</c>,
/// where the key is the first 32 hex digits of the SHA-256 of the location (the content URI itself
/// is never in a name) and the length and hash are those of the bytes the edits were made on, so a
/// document opened at another location finds edits that fit its bytes without reading every file.
/// Each is a <see cref="ViewerEditSnapshot"/> of at most <see cref="ViewerEditSnapshot.MaxBytes"/>,
/// written at <c>onSaveInstanceState</c> and <c>onStop</c> when the session has unsaved edits. The
/// snapshot is taken on the UI thread (it only collects references); the JSON is made and written
/// on a worker thread, one file operation at a time per window and, through
/// <see cref="KeptFilesLock"/>, in the whole process, so neither hashing nor writing holds up the UI
/// and two windows never write the same file at once. A location is open in one window at most
/// (opening it again brings that window forward), and a window writes only its own document's file.
/// <para>
/// A new process that reopens the document from the saved state gives the edits back, pending,
/// when the file is still the bytes they were made on (or those bytes with incremental updates
/// after them, as a save here appends one). A document opened in any other way whose bytes fit kept
/// edits offers them: restore or discard. Edits restored from another location's file move to this
/// location's file (written first, then the old one deleted). Kept edits for other documents are
/// left alone; at most <see cref="MaxKeptDocuments"/> documents keep edits, and beyond that the
/// oldest file of a document no window shows is deleted. A document's file is deleted when its
/// edits are saved (before the write starts, so a crash after the save cannot give them back twice)
/// or discarded, when a restore fails, and when the document can no longer be read. It never goes
/// into a log. An encrypted document's edits are not kept: they would be stored unencrypted.
/// Backups are off (AndroidManifest.xml), so the files never leave the device. The single
/// <c>files/edits/pending.json</c> of earlier versions is moved to its document's file once.
/// </para>
/// <para>
/// <c>files/edits/made-here.txt</c> keeps the ids of the annotations made on this device (at most
/// <see cref="MaxMadeHere"/>), so that a reply is the reader's own (Edit, Delete) only when it was
/// written here, not because an imported file carries the same name. Each window adds its ids to
/// those on storage, so no window drops another's.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>Most ids of annotations made on this device that are kept.</summary>
    private const int MaxMadeHere = 10000;

    /// <summary>Most documents whose unsaved edits are kept at once (each file is at most 32 MB).</summary>
    private const int MaxKeptDocuments = 8;

    /// <summary>Every kept-edits and made-here file operation of the process holds this lock (the windows share the folder).</summary>
    private static readonly object KeptFilesLock = new();

    /// <summary>The SHA-256 of the open document's bytes as opened (<see cref="PdfViewer.Web.Engine.ViewerDocument.OriginalBytes"/>), computed on a worker thread at open.</summary>
    private Task<string>? _openedHash;

    /// <summary>The session version last kept, so an unchanged session is not written again.</summary>
    private int _keptVersion = -1;

    /// <summary>The kept edits on storage for the open document's location (if any) are this session's: it wrote or restored them.</summary>
    private bool _keptOurs;

    /// <summary>File operations on kept edits, one after another on a worker thread.</summary>
    private Task _keptIo = Task.CompletedTask;
    private readonly object _keptGate = new();

    private string EditsFolder => System.IO.Path.Combine(FilesDir!.AbsolutePath, "edits");
    private string MadeHerePath => System.IO.Path.Combine(EditsFolder, "made-here.txt");

    /// <summary>Runs <paramref name="io"/> after the file operations queued before it, on a worker thread. It must not throw.</summary>
    private void QueueKeptIo(Action io)
    {
        lock (_keptGate) _keptIo = _keptIo.ContinueWith(_ => io(), TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ file names

    /// <summary>The file-name key of a location: the first 32 hex digits of its SHA-256 (the location itself is never in a name).</summary>
    private static string LocationKey(string location) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(location)))[..32];

    private static string KeptFileName(string location, long length, string hash) => $"{LocationKey(location)}.{length}.{hash}.json";

    /// <summary>A kept-edits file name's parts; false for any other file (made-here.txt, the old pending.json, a temporary file).</summary>
    private static bool TryParseKeptName(string path, out string key, out long length, out string hash)
    {
        key = hash = "";
        length = 0;
        var parts = System.IO.Path.GetFileName(path).Split('.');
        if (parts.Length != 4 || parts[3] != "json" || parts[0].Length != 32 || parts[2].Length != 64) return false;
        if (!long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length <= 0) return false;
        key = parts[0];
        hash = parts[2];
        return true;
    }

    /// <summary>The kept-edits files on storage (call under <see cref="KeptFilesLock"/>).</summary>
    private static List<(string Path, string Key, long Length, string Hash)> KeptFiles(string folder)
    {
        var files = new List<(string, string, long, string)>();
        if (!Directory.Exists(folder)) return files;
        foreach (string path in Directory.GetFiles(folder, "*.json"))
            if (TryParseKeptName(path, out string key, out long length, out string hash)) files.Add((path, key, length, hash));
        return files;
    }

    /// <summary>Deletes every kept-edits file of a location but <paramref name="except"/> (call under <see cref="KeptFilesLock"/>).</summary>
    private static void DeleteKeptFilesOf(string folder, string location, string? except = null)
    {
        string key = LocationKey(location);
        try
        {
            foreach (var file in KeptFiles(folder))
                if (file.Key == key && file.Path != except) TryDelete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Earlier versions kept one document's edits in <c>pending.json</c>: moved once to that
    /// document's file (unless it already has kept edits), or deleted when damaged. Call under <see cref="KeptFilesLock"/>.
    /// </summary>
    private static void MoveOldKeptEdits(string folder)
    {
        string old = System.IO.Path.Combine(folder, "pending.json");
        try
        {
            var file = new FileInfo(old);
            if (!file.Exists) return;
            if (file.Length <= ViewerEditSnapshot.MaxBytes && ViewerEditSnapshot.FromJson(File.ReadAllText(old)) is { Location.Length: > 0 } kept
                && kept.OriginalHash.Length == 64 && kept.OriginalLength > 0)
            {
                string key = LocationKey(kept.Location);
                if (!KeptFiles(folder).Any(f => f.Key == key))
                {
                    File.Move(old, System.IO.Path.Combine(folder, KeptFileName(kept.Location, kept.OriginalLength, kept.OriginalHash)));
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
        }
        TryDelete(old);
    }

    /// <summary>
    /// Beyond <see cref="MaxKeptDocuments"/> files, deletes the oldest whose location no window
    /// shows (<paramref name="shown"/>: keys of shown locations). Call under <see cref="KeptFilesLock"/>.
    /// </summary>
    private static void TrimKeptEdits(string folder, IReadOnlySet<string> shown)
    {
        try
        {
            var files = KeptFiles(folder);
            int extra = files.Count - MaxKeptDocuments;
            if (extra <= 0) return;
            foreach (var file in files.Where(f => !shown.Contains(f.Key)).OrderBy(f => File.GetLastWriteTimeUtc(f.Path)).Take(extra).ToList())
                TryDelete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ keeping

    /// <summary>
    /// Keeps the open document's unsaved edits in its private file (written on a worker thread), or
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
        string location = _uri.ToString()!;
        // Another window shows this location (opening it again switches there, so it should not happen): the file is that window's.
        if (!OwnsLocation(location)) return;
        // Only references are collected here; the hash, the JSON and the write are on the worker thread.
        var snapshot = session.Snapshot(location, "");
        _keptVersion = session.Version;
        _keptOurs = true;
        string folder = EditsFolder;
        var shown = ShownLocationKeys();
        QueueKeptIo(() =>
        {
            string? temporary = null;
            lock (KeptFilesLock)
            {
                try
                {
                    MoveOldKeptEdits(folder);
                    var complete = snapshot with { OriginalHash = hash.GetAwaiter().GetResult() };
                    byte[]? json = complete.ToUtf8(out int droppedStamps);
                    if (droppedStamps > 0)
                    {
                        // Kept without the custom stamps that did not fit: the reader is told, the other edits are kept.
                        string message = droppedStamps == 1
                            ? "One custom stamp is too large to keep if Android closes the app. Save the document to keep it."
                            : $"{droppedStamps} custom stamps are too large to keep if Android closes the app. Save the document to keep them.";
                        RunOnUiThread(() => Tell(message));
                    }
                    if (json == null)
                    {
                        // Larger than a snapshot may be: not kept (and an older one would be out of date).
                        DeleteKeptFilesOf(folder, location);
                        return;
                    }
                    string path = System.IO.Path.Combine(folder, KeptFileName(location, complete.OriginalLength, complete.OriginalHash));
                    temporary = path + ".new";
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(temporary, json);
                    File.Move(temporary, path, overwrite: true);
                    // Edits kept for earlier bytes at this location are out of date.
                    DeleteKeptFilesOf(folder, location, except: path);
                    TrimKeptEdits(folder, shown);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                                               or CryptographicException)
                {
                    // Not kept: nothing is logged (the edits hold the reader's text). The edits stay in memory.
                    if (temporary != null) TryDelete(temporary);
                }
            }
        });
    }

    /// <summary>The ids made on this device, added to those on storage (on the worker thread), so that windows do not drop each other's.</summary>
    private void KeepMadeHere(ViewerEditSession? session)
    {
        if (session == null || session.MadeHere.Count == 0) return;
        var ids = session.MadeHere.TakeLast(MaxMadeHere).ToArray();
        string path = MadeHerePath, folder = EditsFolder;
        QueueKeptIo(() =>
        {
            lock (KeptFilesLock)
            {
                try
                {
                    var mine = new HashSet<string>(ids, StringComparer.Ordinal);
                    // The stored ids first, then this window's, the newest last.
                    var all = ReadMadeHere(path).Where(id => !mine.Contains(id)).Concat(ids).TakeLast(MaxMadeHere).ToArray();
                    Directory.CreateDirectory(folder);
                    File.WriteAllLines(path + ".new", all);
                    File.Move(path + ".new", path, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }

    /// <summary>The ids made on this device (call on a worker thread); none when the file is missing or damaged.</summary>
    private string[] ReadMadeHere()
    {
        lock (KeptFilesLock) return ReadMadeHere(MadeHerePath);
    }

    private static string[] ReadMadeHere(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 64L * MaxMadeHere) return [];
            return File.ReadAllLines(file.FullName).Where(l => l.Length is > 0 and <= ViewerAnnotationStore.MaxIdLength).TakeLast(MaxMadeHere).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ finding

    /// <summary>A kept-edits file's snapshot, or null (unreadable or too large: then deleted). Call under <see cref="KeptFilesLock"/>.</summary>
    private static ViewerEditSnapshot? ReadSnapshot(string path)
    {
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

    /// <summary>The edits kept for <paramref name="location"/>, or null. Reads the file: call on a worker thread.</summary>
    private ViewerEditSnapshot? ReadKeptEditsNow(string location)
    {
        string folder = EditsFolder, key = LocationKey(location);
        lock (KeptFilesLock)
        {
            MoveOldKeptEdits(folder);
            try
            {
                foreach (var file in KeptFiles(folder).Where(f => f.Key == key))
                    if (ReadSnapshot(file.Path) is { } kept && kept.Location == location) return kept;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }
    }

    /// <summary>
    /// Kept edits for a document being opened: its location's own (whether they fit is the
    /// caller's check), else another location's whose bytes fit <paramref name="file"/> and that no
    /// other window shows (<paramref name="shownElsewhere"/>: keys). Reads files: call on a worker thread.
    /// </summary>
    private ViewerEditSnapshot? FindKeptEditsNow(string location, byte[] file, IReadOnlySet<string> shownElsewhere)
    {
        if (ReadKeptEditsNow(location) is { } own) return own;
        string folder = EditsFolder, key = LocationKey(location);
        lock (KeptFilesLock)
        {
            try
            {
                // Only the names are compared first: one hash of the file per length that occurs.
                var hashes = new Dictionary<long, string>();
                foreach (var kept in KeptFiles(folder))
                {
                    if (kept.Key == key || shownElsewhere.Contains(kept.Key) || kept.Length > file.LongLength) continue;
                    if (!hashes.TryGetValue(kept.Length, out string? hash))
                        hashes[kept.Length] = hash = ViewerEditSnapshot.Hash(file.AsSpan(0, (int)kept.Length));
                    if (hash != kept.Hash) continue;
                    if (ReadSnapshot(kept.Path) is { } found && found.OriginalOf(file) != null) return found;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ forgetting

    /// <summary>Deletes the edits kept for <paramref name="location"/> (a failed restore, a declined offer, an unreadable file).</summary>
    private void ForgetKeptEdits(string location)
    {
        if (location == _uri?.ToString())
        {
            _keptVersion = -1;
            _keptOurs = false;
        }
        string folder = EditsFolder;
        QueueKeptIo(() =>
        {
            lock (KeptFilesLock) DeleteKeptFilesOf(folder, location);
        });
    }

    /// <summary>Deletes the kept edits when they are the open document's (saved, discarded, left); other documents' stay.</summary>
    private void ForgetOurKeptEdits()
    {
        if (_keptOurs && _uri != null) ForgetKeptEdits(_uri.ToString()!);
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

    // ------------------------------------------------------------------ giving back

    /// <summary>After Android ended the process: the kept edits (read on a worker thread) go with the document of the saved state.</summary>
    private async Task ReopenAfterProcessDeathAsync(global::Android.Net.Uri uri, string location, (PageAnchor TopLeft, double Zoom) view)
    {
        _reopening = true;
        try
        {
            var kept = await Task.Run(() => ReadKeptEditsNow(location));
            await OpenUriAsync(uri, view, restore: kept);
        }
        finally
        {
            _reopening = false;
            // The Documents list asked this window to close while it was not running (MainActivity.Windows.cs).
            CloseIfAsked();
        }
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
                ForgetKeptEdits(kept.Location);
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
            ForgetKeptEdits(kept.Location);
            Toast.MakeText(this, "Your unsaved changes could not be restored.", ToastLength.Long)?.Show();
            return;
        }
        if (!restored)
        {
            ForgetKeptEdits(kept.Location);
            return;
        }
        _keptOurs = true;
        if (_uri != null && kept.Location != _uri.ToString())
        {
            // Kept for the same bytes at another location: kept again under this one (queued first), then the old file goes.
            _keptVersion = -1;
            KeepEdits();
            string folder = EditsFolder, old = kept.Location;
            QueueKeptIo(() =>
            {
                lock (KeptFilesLock) DeleteKeptFilesOf(folder, old);
            });
        }
        Toast.MakeText(this, "Your unsaved changes were restored. Save to keep them in the file.", ToastLength.Long)?.Show();
        _ = WriteAsync(session);
    }
}
