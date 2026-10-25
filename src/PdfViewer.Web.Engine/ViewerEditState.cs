namespace PdfViewer.Web.Engine;

/// <summary>
/// Which edits the document's bytes have and which bytes the host page has saved, for a viewer
/// that writes the reader's edits into the document after each change and saves through the
/// host page. Every edit gets a new version. A version is applied when the bytes have it, failed
/// when writing it threw: a failed version is never marked applied, so the document stays dirty
/// and a save refuses to post bytes without the latest edit. One save is in flight at a time, from
/// <see cref="TryBeginSave"/> until the host page answers (<see cref="Answer"/>) or the viewer
/// gives up on it (<see cref="EndSave"/>); a timeout does not end it.
/// </summary>
public sealed class ViewerEditState
{
    /// <summary>The latest edit.</summary>
    public int EditVersion { get; private set; }

    /// <summary>The latest edit the document's bytes have.</summary>
    public int AppliedVersion { get; private set; }

    /// <summary>The edit that could not be written, or 0.</summary>
    public int FailedVersion { get; private set; }

    /// <summary>The edit the host page last saved.</summary>
    public int SavedVersion { get; private set; }

    /// <summary>The edit in the bytes of the save in flight.</summary>
    public int PostedVersion { get; private set; }

    /// <summary>The id of the save in flight, 0 when the bytes are not yet posted.</summary>
    public int PendingSave { get; private set; }

    private int _saveId;

    /// <summary>A save is under way: waiting for the edits to be written, or for the host page's answer.</summary>
    public bool Saving { get; private set; }

    /// <summary>The document has edits the host page has not saved.</summary>
    public bool IsDirty => EditVersion != SavedVersion;

    /// <summary>The document's bytes have every edit.</summary>
    public bool IsCurrent => AppliedVersion == EditVersion;

    /// <summary>The latest edit is neither written nor failed: writing it is worth a try.</summary>
    public bool NeedsApply => AppliedVersion != EditVersion && FailedVersion != EditVersion;

    /// <summary>A new document: no edits, nothing saved, no save in flight.</summary>
    public void Reset()
    {
        EditVersion = AppliedVersion = FailedVersion = SavedVersion = PostedVersion = PendingSave = 0;
        Saving = false;
    }

    /// <summary>Something changed; returns the new version.</summary>
    public int Edit() => ++EditVersion;

    /// <summary>The bytes now have <paramref name="version"/>.</summary>
    public void Applied(int version)
    {
        AppliedVersion = version;
        if (FailedVersion <= version) FailedVersion = 0;
    }

    /// <summary>Writing <paramref name="version"/> threw: the bytes keep the version they had.</summary>
    public void Failed(int version) => FailedVersion = version;

    /// <summary>Starts a save; false when one is under way already.</summary>
    public bool TryBeginSave()
    {
        if (Saving) return false;
        Saving = true;
        return true;
    }

    /// <summary>
    /// The edits are written as far as they go: returns the id of the save whose bytes may be
    /// posted now (they have <see cref="AppliedVersion"/>), or 0 when the bytes lack the latest
    /// edit. The save is then ended, and nothing must be posted.
    /// </summary>
    public int Post()
    {
        if (!Saving) throw new InvalidOperationException("No save was started.");
        if (!IsCurrent)
        {
            EndSave();
            return 0;
        }
        PostedVersion = AppliedVersion;
        PendingSave = ++_saveId;
        return PendingSave;
    }

    /// <summary>The host page's answer to save <paramref name="saveId"/>; false when it is not the save in flight.</summary>
    public bool Answer(int saveId, bool saved)
    {
        if (!Saving || saveId == 0 || saveId != PendingSave) return false;
        if (saved) SavedVersion = PostedVersion;
        EndSave();
        return true;
    }

    /// <summary>Ends the save under way without an answer (nothing was posted, or the document closed).</summary>
    public void EndSave()
    {
        Saving = false;
        PendingSave = 0;
    }
}
