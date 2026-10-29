namespace PdfViewer.Web.Engine.Annotations;

/// <summary>
/// What a document provider says about a file: its size in bytes and its last-modified time
/// (milliseconds since 1970). Either can be unknown (null): not every provider gives them.
/// </summary>
public readonly record struct ViewerFileStamp(long? Size, long? LastModified)
{
    /// <summary>
    /// True when the file is known to differ from <paramref name="opened"/>: a size or a time that
    /// both stamps have and that is not the same. Unknown values prove nothing either way.
    /// </summary>
    public bool ChangedSince(ViewerFileStamp opened) =>
        (Size is { } size && opened.Size is { } openedSize && size != openedSize)
        || (LastModified is { } time && opened.LastModified is { } openedTime && time != openedTime);
}

/// <summary>What "Save" does now (<see cref="ViewerSaveBack.Check"/>).</summary>
public enum ViewerSaveBackStep
{
    /// <summary>No edits that a save has not written.</summary>
    NothingToSave,

    /// <summary>A save runs already.</summary>
    Busy,

    /// <summary>The app may not write the file it opened: save a copy instead.</summary>
    CannotWrite,

    /// <summary>The file changed after it was opened (another app wrote it): ask before overwriting.</summary>
    ChangedOnDisk,

    /// <summary>Write the edits back to the file.</summary>
    Write,
}


/// <summary>
/// Thrown by a writer given to <see cref="ViewerSaveBack.SaveAsync"/> when the file could not be
/// opened for writing at all: nothing was written, so the file is unchanged.
/// </summary>
public sealed class ViewerFileNotOpenedException(string reason) : IOException(reason);

/// <summary>How a save back to the opened file (or a copy) ended.</summary>
/// <param name="Saved">The bytes were written. For a save back, the session is no longer dirty for them.</param>
/// <param name="Problem">Why it was not saved (a message for the reader), or null.</param>
/// <param name="FileMayBeDamaged">Writing started, failed, and the file's previous content could not be written back: the file may now be incomplete.</param>
/// <param name="Length">The number of bytes written when saved.</param>
/// <param name="Unverified">Saved, but the platform could not tell the written length.</param>
/// <param name="Restored">Writing failed part-way and the file's previous content was written back.</param>
/// <param name="OfferCopy">Not saved for a reason a copy avoids (the file cannot be written, or writing failed).</param>
public readonly record struct ViewerSaveBackResult(bool Saved, string? Problem, bool FileMayBeDamaged, long Length,
    bool Unverified = false, bool Restored = false, bool OfferCopy = false);

/// <summary>
/// "Save": the reader's edits written back to the file they opened (a native viewer, the Android
/// app), next to "Save a copy". The new bytes are complete before the file is touched: the file
/// as opened with the edits appended as an incremental update (<see cref="ViewerEditSession.BeginSaveAsync"/>),
/// so signatures of a signed file stay valid. They are then written in one go, and the written
/// length is checked when the platform can tell it. The session stops being dirty only when the
/// opened file has the edits (<see cref="SaveAsync"/>); a copy (<see cref="CopyAsync"/>) leaves it
/// dirty, because the opened file still lacks them. The undo history stays, as in the desktop
/// and web viewers.
/// </summary>
public static class ViewerSaveBack
{
    public const string NothingToSaveMessage = "There are no unsaved changes.";

    public const string BusyMessage = "A save is running already.";

    public const string CannotWriteMessage =
        "This app may only read this file, so the changes cannot be saved into it. Save a copy instead.";

    public const string ChangedOnDiskMessage =
        "The file changed after it was opened here (another app may have saved it). Saving replaces those changes with this version.";

    public const string NotWrittenMessage = "The changes could not be written into the document, so nothing was saved.";

    /// <summary>What the unsaved-changes question says: a saved copy does not count as saving this file.</summary>
    public const string UnsavedMessage = "Your changes are not saved in this file. Save them, or discard them?";

    /// <summary>What "Save" does now. <paramref name="overwrite"/>: the reader agreed to replace a file that changed.</summary>
    public static ViewerSaveBackStep Check(ViewerEditSession? session, bool canWrite, ViewerFileStamp opened, ViewerFileStamp now, bool overwrite = false)
    {
        if (session is not { IsDirty: true }) return ViewerSaveBackStep.NothingToSave;
        if (session.IsSaving) return ViewerSaveBackStep.Busy;
        if (!canWrite) return ViewerSaveBackStep.CannotWrite;
        if (!overwrite && now.ChangedSince(opened)) return ViewerSaveBackStep.ChangedOnDisk;
        return ViewerSaveBackStep.Write;
    }

    /// <summary>
    /// The stamp to compare with after this app saved <paramref name="length"/> bytes: that size,
    /// and no last-modified time, because a provider (a cloud one, a pipe) may set the time later,
    /// which must not look like another app's change.
    /// </summary>
    public static ViewerFileStamp StampAfterSave(long length) => new(length, null);

    /// <summary>
    /// Why <paramref name="written"/> bytes (as the platform reports them; null when it cannot
    /// tell) are not the <paramref name="expected"/> length, or null when they are or it is unknown.
    /// </summary>
    public static string? Verify(long expected, long? written) =>
        written is { } length && length != expected
            ? $"The saved file has {length} bytes instead of {expected}: it may be damaged. Your changes are still open here: save a copy now."
            : null;

    /// <summary>
    /// Saves back: takes the session's complete bytes, then has <paramref name="write"/> write them
    /// all to the file (it returns the length the file has afterwards, or null when unknown; it
    /// throws <see cref="ViewerFileNotOpenedException"/> when it could not open the file), then
    /// checks that length. When writing fails part-way and <paramref name="previous"/> (the bytes
    /// the file had) is given, they are written back once. The save ends either way; only a written
    /// file clears the dirty state.
    /// </summary>
    public static async Task<ViewerSaveBackResult> SaveAsync(ViewerEditSession session, Func<byte[], Task<long?>> write, byte[]? previous = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(write);
        if (session.IsSaving) return new(false, BusyMessage, false, 0);
        var save = await session.BeginSaveAsync(cancellationToken).ConfigureAwait(true);
        if (save == null) return new(false, session.IsSaving ? BusyMessage : NotWrittenMessage, false, 0);
        bool saved = false;
        try
        {
            long? written;
            try
            {
                written = await write(save.Bytes).ConfigureAwait(true);
            }
            catch (ViewerFileNotOpenedException ex)
            {
                // Nothing was written: the file is as it was.
                return new(false, $"This file cannot be written here ({ex.Message}). It was not changed. Save a copy instead.", false, 0, OfferCopy: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Type names only, never document content.
                return await Failed(ex.GetType().Name, write, previous).ConfigureAwait(true);
            }
            if (Verify(save.Bytes.Length, written) != null)
                return await Failed($"it has {written} bytes instead of {save.Bytes.Length}", write, previous).ConfigureAwait(true);
            saved = true;
            return new(true, null, false, save.Bytes.Length, Unverified: written == null);
        }
        finally
        {
            session.EndSave(save, saved);
        }
    }

    /// <summary>Writing failed part-way: the previous bytes are written back once, if there are any.</summary>
    private static async Task<ViewerSaveBackResult> Failed(string reason, Func<byte[], Task<long?>> write, byte[]? previous)
    {
        bool restored = false;
        if (previous != null)
        {
            try
            {
                restored = Verify(previous.Length, await write(previous).ConfigureAwait(true)) == null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                restored = false;
            }
        }
        string why = $"The file could not be saved ({reason}). ";
        string message = restored
            ? why + "Its previous content was written back, so it is unchanged. Your changes are still open here: save a copy now."
            : why + "It may now be incomplete. Your changes are still open here: save a copy now.";
        return new(false, message, !restored, 0, Restored: restored, OfferCopy: true);
    }

    /// <summary>
    /// "Save a copy": the session's complete bytes written by <paramref name="write"/> to a new
    /// file. The session stays dirty: the opened file does not have the edits, so leaving still
    /// asks, and "Save" still writes them into the opened file only when the reader chooses it.
    /// </summary>
    public static async Task<ViewerSaveBackResult> CopyAsync(ViewerEditSession session, Func<byte[], Task<long?>> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(write);
        if (session.IsSaving) return new(false, BusyMessage, false, 0);
        var save = await session.BeginSaveAsync(cancellationToken).ConfigureAwait(true);
        if (save == null) return new(false, session.IsSaving ? BusyMessage : NotWrittenMessage, false, 0);
        try
        {
            long? written = await write(save.Bytes).ConfigureAwait(true);
            return Verify(save.Bytes.Length, written) is { } problem
                ? new(false, problem, true, 0)
                : new(true, null, false, save.Bytes.Length, Unverified: written == null);
        }
        finally
        {
            // Not "saved": that is the opened file's state, which a copy does not change.
            session.EndSave(save, saved: false);
        }
    }
}