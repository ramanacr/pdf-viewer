using PdfEngine.Redaction;
using PdfViewer.Core.Session;

namespace PdfViewer.Core.Commands;

public sealed class ApplyRedactionsCommand : IDocumentCommand
{
    private readonly IPdfRedactionService _redactionService;
    private readonly string _targetPath;
    private readonly IReadOnlyList<RedactionArea> _redactions;

    public string Name => $"Apply {_redactions.Count} Redactions";

    public ApplyRedactionsCommand(
        IPdfRedactionService redactionService,
        string targetPath,
        IReadOnlyList<RedactionArea> redactions)
    {
        _redactionService = redactionService ?? throw new ArgumentNullException(nameof(redactionService));
        _targetPath = targetPath;

        // Snapshot by VALUE. Callers naturally pass a live ObservableCollection from the UI;
        // holding it by reference meant later edits retroactively changed this command's Name
        // and made a redo apply a different redaction set than the original execute.
        _redactions = redactions?.ToArray() ?? throw new ArgumentNullException(nameof(redactions));
    }

    public async ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session.Document == null) return;
        await _redactionService.ApplyRedactionsAsync(session.Document, _targetPath, _redactions, cancellationToken);
    }

    public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        // Redaction destroys the underlying content by design, so it genuinely cannot be
        // undone. Returning CompletedTask made CommandHistory treat the undo as successful:
        // Ctrl+Z appeared to work, did nothing, and the NEXT Ctrl+Z then undid a command
        // whose precondition no longer held. Fail loudly instead.
        throw new NotSupportedException(
            "Applying redactions is irreversible and cannot be undone. Work from a copy of the " +
            "document if you need to revert.");
    }
}
