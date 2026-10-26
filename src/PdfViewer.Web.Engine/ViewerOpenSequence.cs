namespace PdfViewer.Web.Engine;

/// <summary>
/// Keeps only the latest of opens that overlap. Each open takes a ticket (<see cref="Begin"/>);
/// a newer open, or a cancel (<see cref="Supersede"/>), makes the earlier tickets stale. An open
/// that finishes after it went stale is disposed and drops its error, so a slow first file can
/// never replace a second one that the reader opened after it.
/// </summary>
public sealed class ViewerOpenSequence
{
    private int _current;

    /// <summary>Starts an open: earlier tickets are stale from now on.</summary>
    public int Begin() => Interlocked.Increment(ref _current);

    /// <summary>Makes every ticket stale (another file is coming, or the open was given up).</summary>
    public void Supersede() => Interlocked.Increment(ref _current);

    /// <summary>True when no open began after <paramref name="ticket"/>.</summary>
    public bool IsCurrent(int ticket) => Volatile.Read(ref _current) == ticket;

    /// <summary>
    /// Runs <paramref name="open"/> for <paramref name="ticket"/>. Returns its value while the
    /// ticket is current, and throws its error while it is current. When the ticket went stale
    /// meanwhile, the value is disposed (the error dropped) and the result is null.
    /// </summary>
    public async Task<T?> RunAsync<T>(int ticket, Func<Task<T>> open) where T : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(open);
        T value;
        try
        {
            value = await open().ConfigureAwait(false);
        }
        catch when (!IsCurrent(ticket))
        {
            return null;
        }
        if (IsCurrent(ticket)) return value;
        await value.DisposeAsync().ConfigureAwait(false);
        return null;
    }
}

/// <summary>
/// When to try again a background read that failed (the rest of a file read by ranges): after
/// 2 s, 4 s, 8 s and so on, at most 60 s, and not at all after <see cref="MaxFailures"/> failures
/// in a row, until the reader asks (<see cref="Reset"/>). Not thread-safe: one viewer uses it.
/// </summary>
public sealed class ViewerRetryBackoff
{
    private readonly Func<long> _clock;
    private long _retryAt;

    /// <param name="clock">Milliseconds since some start (default <see cref="Environment.TickCount64"/>).</param>
    public ViewerRetryBackoff(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    /// <summary>Failures in a row after which only the reader starts a new try (default 5).</summary>
    public int MaxFailures { get; init; } = 5;

    /// <summary>The failures since the last success or <see cref="Reset"/>.</summary>
    public int Failures { get; private set; }

    /// <summary>True when the tries stopped: only <see cref="Reset"/> (a retry the reader asked for) starts them again.</summary>
    public bool GaveUp => Failures >= MaxFailures;

    /// <summary>True when a new try may start now.</summary>
    public bool CanTry => !GaveUp && _clock() >= _retryAt;

    /// <summary>The wait after <paramref name="failures"/> failures in a row: 2 s, 4 s, 8 s…, at most 60 s.</summary>
    public static TimeSpan DelayAfter(int failures) =>
        failures <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(60, 1L << Math.Min(failures, 6)));

    /// <summary>Notes a failure; returns the wait before the next try.</summary>
    public TimeSpan Failed()
    {
        Failures++;
        var delay = DelayAfter(Failures);
        _retryAt = _clock() + (long)delay.TotalMilliseconds;
        return delay;
    }

    /// <summary>Back to the start: after a success, a new document, or a retry the reader asked for.</summary>
    public void Reset()
    {
        Failures = 0;
        _retryAt = 0;
    }
}
