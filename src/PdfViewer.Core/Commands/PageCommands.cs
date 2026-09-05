using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfViewer.Core.Session;

namespace PdfViewer.Core.Commands;

/// <summary>
/// Atomic undoable command to rotate a page in a document session.
/// </summary>
public sealed class RotatePageCommand : IDocumentCommand
{
    private readonly IPdfPageOrganizerService _organizer;
    private readonly int _pageNumber;
    private readonly PageRotation _newRotation;
    private readonly PageRotation _oldRotation;

    public string Name => $"Rotate Page {_pageNumber} to {_newRotation}";

    public RotatePageCommand(
        IPdfPageOrganizerService organizer,
        int pageNumber,
        PageRotation newRotation,
        PageRotation oldRotation)
    {
        _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
        _pageNumber = pageNumber;
        _newRotation = newRotation;
        _oldRotation = oldRotation;
    }

    public async ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session.Document == null) return;
        await _organizer.RotatePageAsync(session.Document, _pageNumber, _newRotation, cancellationToken);
    }

    public async ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session.Document == null) return;
        await _organizer.RotatePageAsync(session.Document, _pageNumber, _oldRotation, cancellationToken);
    }
}

/// <summary>
/// Atomic command to insert a blank page.
/// </summary>
public sealed class InsertBlankPageCommand : IDocumentCommand
{
    private readonly IPdfPageOrganizerService _organizer;
    private readonly int _targetIndex;
    private readonly double _width;
    private readonly double _height;

    public string Name => $"Insert Blank Page at index {_targetIndex + 1}";

    public InsertBlankPageCommand(
        IPdfPageOrganizerService organizer,
        int targetIndex,
        double width = 612,
        double height = 792)
    {
        _organizer = organizer ?? throw new ArgumentNullException(nameof(organizer));
        _targetIndex = targetIndex;
        _width = width;
        _height = height;
    }

    // The index the page was ACTUALLY inserted at. InsertBlankPageAsync clamps its argument,
    // so undoing with the raw requested index deleted the wrong page (or threw) whenever the
    // caller passed an out-of-range index - e.g. insert at 50 in a 10-page document appended
    // page 11, but undo tried to delete page 51.
    private int _effectiveIndex;

    public async ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session.Document == null) return;

        _effectiveIndex = Math.Clamp(_targetIndex, 0, session.Document.PageCount);
        await _organizer.InsertBlankPageAsync(session.Document, _effectiveIndex, _width, _height, cancellationToken);
    }

    public async ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session.Document == null) return;
        // Page number is 1-indexed
        await _organizer.DeletePageAsync(session.Document, _effectiveIndex + 1, cancellationToken);
    }
}
