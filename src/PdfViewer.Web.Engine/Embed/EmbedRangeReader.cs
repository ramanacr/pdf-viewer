using PdfEngine.Vector.Parsing;

namespace PdfViewer.Web.Engine.Embed;

/// <summary>
/// The viewer's side of range loading in the embed SDK: an <see cref="IPdfRangeReader"/> whose
/// reads are fetchRange messages to the host page (<see cref="EmbedProtocol.FetchRange"/>). The
/// host page does the network access (the viewer's own policy allows no other origin), and only
/// for the URL or linearizationInfo it was given: a message names bytes, never a URL. A read is
/// split into requests of at most <see cref="EmbedProtocol.MaxRangeBytes"/>, at most
/// <see cref="EmbedProtocol.MaxRangeRequestsInFlight"/> at a time. An answer is accepted only for
/// a request that is waiting, of this file, with exactly the bytes asked for.
/// </summary>
public sealed class EmbedRangeReader : IPdfRangeReader
{
    private sealed record Pending(long Start, long End, Memory<byte> Destination, TaskCompletionSource Done);

    private readonly Action<string> _post;
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly SemaphoreSlim _slots = new(EmbedProtocol.MaxRangeRequestsInFlight);
    private readonly object _sync = new();
    private int _nextId;
    private bool _closed;
    private long _bytesReceived;

    /// <param name="file">The open request of the file (the host page knows the file by it).</param>
    /// <param name="length">The file's length, from the open message.</param>
    /// <param name="post">Sends a message (JSON) to the host page.</param>
    public EmbedRangeReader(int file, long length, Action<string> post)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(file, 1);
        if (length < 1 || length > EmbedProtocol.MaxFileBytes) throw new ArgumentOutOfRangeException(nameof(length));
        File = file;
        Length = length;
        _post = post ?? throw new ArgumentNullException(nameof(post));
    }

    /// <summary>The open request of the file.</summary>
    public int File { get; }

    /// <inheritdoc/>
    public long Length { get; }

    /// <summary>How long a request may wait for its answer (default 60 seconds).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The bytes received so far.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>The requests waiting for an answer.</summary>
    public int InFlight { get { lock (_sync) return _pending.Count; } }

    /// <inheritdoc/>
    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var parts = new List<Task>();
        for (int at = 0; at < destination.Length; at += EmbedProtocol.MaxRangeBytes)
        {
            int n = Math.Min(EmbedProtocol.MaxRangeBytes, destination.Length - at);
            parts.Add(RequestAsync(offset + at, destination.Slice(at, n), cancellationToken));
        }
        await Task.WhenAll(parts).ConfigureAwait(false);
    }

    private async Task RequestAsync(long start, Memory<byte> destination, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        int id = 0;
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_closed) throw new ViewerRangeReadException("The file was closed.");
                id = ++_nextId;
                _pending[id] = new Pending(start, start + destination.Length, destination, done);
            }
            _post(EmbedProtocol.FetchRange(File, id, start, start + destination.Length, Length));
            try
            {
                await done.Task.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new ViewerRangeReadException("The host page did not answer a range request in time.", ex);
            }
        }
        finally
        {
            if (id != 0) lock (_sync) _pending.Remove(id);
            _slots.Release();
        }
    }

    /// <summary>
    /// An answer from the host page. False (and nothing changes) when it is not for a waiting
    /// request of this file; a waiting request whose bytes are not exactly the ones asked for fails.
    /// </summary>
    public bool Receive(EmbedRangeReply reply, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(reply);
        Pending? pending;
        lock (_sync)
        {
            if (reply.File != File || !_pending.Remove(reply.RequestId, out pending)) return false;
        }
        if (!reply.Ok)
        {
            pending.Done.TrySetException(new ViewerRangeReadException($"The host page could not fetch bytes {pending.Start} to {pending.End} ({reply.ErrorCode}): {reply.ErrorMessage}"));
            return true;
        }
        if (data.Length != pending.End - pending.Start)
        {
            pending.Done.TrySetException(new ViewerRangeReadException($"The host page sent {data.Length} bytes for a range of {pending.End - pending.Start}."));
            return true;
        }
        data.CopyTo(pending.Destination.Span);
        Interlocked.Add(ref _bytesReceived, data.Length);
        pending.Done.TrySetResult();
        return true;
    }

    /// <summary>Fails every waiting request and refuses new ones (the file was closed or replaced).</summary>
    public void Close()
    {
        Pending[] waiting;
        lock (_sync)
        {
            _closed = true;
            waiting = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var p in waiting) p.Done.TrySetException(new ViewerRangeReadException("The file was closed."));
    }
}
