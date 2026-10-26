using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Opens that overlap in the web viewer (only the latest shows its document) and the backoff of
/// the background read of a file opened by ranges.
/// </summary>
public class ViewerOpenSequenceTests
{
    private sealed class FakeDocument : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TwoOpens_TheFirstFinishingLast_TheSecondWins_AndTheFirstIsDisposed()
    {
        var sequence = new ViewerOpenSequence();
        var first = new TaskCompletionSource<FakeDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new FakeDocument();
        var b = new FakeDocument();

        int ticketA = sequence.Begin();
        var openA = sequence.RunAsync(ticketA, () => first.Task);
        int ticketB = sequence.Begin();
        var openB = sequence.RunAsync(ticketB, () => Task.FromResult(b));

        Assert.Same(b, await openB);
        Assert.True(sequence.IsCurrent(ticketB));
        first.SetResult(a);
        Assert.Null(await openA);
        Assert.True(a.Disposed);
        Assert.False(b.Disposed);
    }

    [Fact]
    public async Task AStaleFailure_IsDropped_ACurrentOneIsThrown()
    {
        var sequence = new ViewerOpenSequence();
        var first = new TaskCompletionSource<FakeDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ticketA = sequence.Begin();
        var openA = sequence.RunAsync(ticketA, () => first.Task);
        int ticketB = sequence.Begin();
        // The newer open fails: its failure still reaches the caller (and so the host page).
        await Assert.ThrowsAsync<ViewerOpenException>(() =>
            sequence.RunAsync<FakeDocument>(ticketB, () => throw new ViewerOpenException(ViewerOpenFailure.NotReadable, "damaged")));
        first.SetException(new ViewerOpenException(ViewerOpenFailure.NetworkError, "closed"));
        Assert.Null(await openA);
    }

    [Fact]
    public async Task Supersede_MakesARunningOpenStale()
    {
        var sequence = new ViewerOpenSequence();
        var pending = new TaskCompletionSource<FakeDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ticket = sequence.Begin();
        var open = sequence.RunAsync(ticket, () => pending.Task);
        sequence.Supersede();
        var doc = new FakeDocument();
        pending.SetResult(doc);
        Assert.Null(await open);
        Assert.True(doc.Disposed);
    }

    [Fact]
    public void Backoff_Waits2Then4Then8_AtMost60s_AndStopsAfterAFewFailures()
    {
        Assert.Equal(new double[] { 2, 4, 8, 16, 32, 60, 60 }, Enumerable.Range(1, 7).Select(n => ViewerRetryBackoff.DelayAfter(n).TotalSeconds).ToArray());
        long now = 0;
        var backoff = new ViewerRetryBackoff(() => now) { MaxFailures = 3 };
        Assert.True(backoff.CanTry);
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Failed());
        Assert.False(backoff.CanTry);   // a page drawn at once does not retry
        now = 1999;
        Assert.False(backoff.CanTry);
        now = 2000;
        Assert.True(backoff.CanTry);
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.Failed());
        now += 4000;
        Assert.True(backoff.CanTry);
        backoff.Failed();
        Assert.True(backoff.GaveUp);
        now += 3_600_000;
        Assert.False(backoff.CanTry);   // after the last failure, only the reader's retry
        backoff.Reset();
        Assert.True(backoff.CanTry);
        Assert.Equal(0, backoff.Failures);
    }
}
