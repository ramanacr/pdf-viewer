using System;

namespace PdfViewer.Services;

/// <summary>
/// A save that would break the document's signatures was refused: the document is signed and
/// cannot take an incremental update (its file structure had to be repaired, or it has no
/// startxref), or the save flattens it. Save As can still write a copy, once the user agrees
/// that its signatures will no longer be valid.
/// </summary>
public sealed class SignedSaveException : InvalidOperationException
{
    public SignedSaveException()
        : base("This signed document can only be saved as a copy without valid signatures: its file structure had to be repaired " +
               "(or the save flattens it), so the change cannot be added as an incremental update that keeps the signatures. " +
               "Use Save As to save a copy; its signatures will no longer be valid.")
    {
    }
}
