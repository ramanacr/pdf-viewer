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

/// <summary>How a save back to the opened file ended.</summary>
/// <param name="Saved">The file now has every edit of the save, and the session is no longer dirty for them.</param>
/// <param name="Problem">Why it was not saved (a message for the reader), or null.</param>
/// <param name="FileMayBeDamaged">Writing started and failed: the file may now be incomplete.</param>
/// <param name="Length">The number of bytes written when saved.</param>
public readonly record struct ViewerSaveBackResult(bool Saved, string? Problem, bool FileMayBeDamaged, long Length);

/// <summary>
/// "Save": the reader's edits written back to the file they opened (a native viewer, the Android
/// app), next to "Save a copy". The new bytes are complete before the file is touched: the file
/// as opened with the edits appended as an incremental update (<see cref="ViewerEditSession.BeginSaveAsync"/>),
/// so signatures of a signed file stay valid. They are then written in one go, and the written
/// length is checked when the platform can tell it. The session stops being dirty only when all of
/// that succeeded; its undo history stays, as in the desktop and web viewers.
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
    /// Why <paramref name="written"/> bytes (as the platform reports them; null when it cannot
    /// tell) are not the <paramref name="expected"/> length, or null when they are or it is unknown.
    /// </summary>
    public static string? Verify(long expected, long? written) =>
        written is { } length && length != expected
            ? $"The saved file has {length} bytes instead of {expected}: it may be damaged. Your changes are still open here: save a copy now."
            : null;

    /// <summary>
    /// Saves back: takes the session's complete bytes, then has <paramref name="write"/> write them
    /// all to the file (it returns the length the file has afterwards, or null when unknown), then
    /// checks that length. The save ends either way; only a verified write clears the dirty state.
    /// </summary>
    public static async Task<ViewerSaveBackResult> SaveAsync(ViewerEditSession session, Func<byte[], Task<long?>> write, CancellationToken cancellationToken = default)
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Type names only, never document content.
                return new(false, $"The file could not be saved ({ex.GetType().Name}). It may now be incomplete. Your changes are still open here: save a copy now.", true, 0);
            }
            if (Verify(save.Bytes.Length, written) is { } problem) return new(false, problem, true, 0);
            saved = true;
            return new(true, null, false, save.Bytes.Length);
        }
        finally
        {
            session.EndSave(save, saved);
        }
    }
}
