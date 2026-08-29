using PdfViewer.Core.Session;

namespace PdfViewer.Core.Commands;

public interface IDocumentCommand
{
    string Name { get; }
    ValueTask ExecuteAsync(DocumentSession session, CancellationToken cancellationToken = default);
    ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default);
}

public interface ICommandHistory
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? NextUndoName { get; }
    string? NextRedoName { get; }

    ValueTask ExecuteCommandAsync(IDocumentCommand command, DocumentSession session, CancellationToken cancellationToken = default);
    ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default);
    ValueTask RedoAsync(DocumentSession session, CancellationToken cancellationToken = default);
    void Clear();
}

public sealed class CommandHistory : ICommandHistory
{
    private readonly Stack<IDocumentCommand> _undoStack = new();
    private readonly Stack<IDocumentCommand> _redoStack = new();
    private readonly int _maxHistory;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public string? NextUndoName => CanUndo ? _undoStack.Peek().Name : null;
    public string? NextRedoName => CanRedo ? _redoStack.Peek().Name : null;

    public CommandHistory(int maxHistory = 100)
    {
        _maxHistory = Math.Max(1, maxHistory);
    }

    public async ValueTask ExecuteCommandAsync(IDocumentCommand command, DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));

        await command.ExecuteAsync(session, cancellationToken);
        _undoStack.Push(command);
        _redoStack.Clear();

        if (_undoStack.Count > _maxHistory)
        {
            var temp = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = Math.Min(temp.Length - 1, _maxHistory - 1); i >= 0; i--)
            {
                _undoStack.Push(temp[i]);
            }
        }

        session.IncrementRevision();
    }

    public async ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (!CanUndo) return;

        var cmd = _undoStack.Pop();
        await cmd.UndoAsync(session, cancellationToken);
        _redoStack.Push(cmd);
        session.IncrementRevision();
    }

    public async ValueTask RedoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (!CanRedo) return;

        var cmd = _redoStack.Pop();
        await cmd.ExecuteAsync(session, cancellationToken);
        _undoStack.Push(cmd);
        session.IncrementRevision();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
