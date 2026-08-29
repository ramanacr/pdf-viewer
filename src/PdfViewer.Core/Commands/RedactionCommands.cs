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
        _redactions = redactions;
    }

    public async ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (session.Document == null) return;
        await _redactionService.ApplyRedactionsAsync(session.Document, _targetPath, _redactions, cancellationToken);
    }

    public ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        // Redactions are permanent by design; undo in a new session can revert to pre-redacted copy if available
        return ValueTask.CompletedTask;
    }
}
