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

    // Serializes the whole execute-and-record sequence. The stacks were previously mutated
    // across await points with no synchronization, so two commands started in quick
    // succession (e.g. a double-clicked Rotate button) recorded in completion order rather
    // than invocation order - or corrupted the Stack outright under real parallelism.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask ExecuteCommandAsync(IDocumentCommand command, DocumentSession session, CancellationToken cancellationToken = default)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await command.ExecuteAsync(session, cancellationToken);

            // Only record AFTER the command actually succeeded.
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
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask UndoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_undoStack.Count == 0) return;

            // Peek, await, and only then move between stacks. Popping first meant a failed
            // or cancelled undo lost the command from BOTH stacks, leaving that edit
            // permanently un-undoable and the history out of sync with the document.
            var cmd = _undoStack.Peek();
            await cmd.UndoAsync(session, cancellationToken);

            _undoStack.Pop();
            _redoStack.Push(cmd);
            session.IncrementRevision();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RedoAsync(DocumentSession session, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_redoStack.Count == 0) return;

            var cmd = _redoStack.Peek();
            await cmd.ExecuteAsync(session, cancellationToken);

            _redoStack.Pop();
            _undoStack.Push(cmd);
            session.IncrementRevision();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        _gate.Wait();
        try
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
