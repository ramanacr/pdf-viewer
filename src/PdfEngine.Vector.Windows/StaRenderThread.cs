using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// One long-lived STA thread that owns all WPF drawing work for a renderer. Replaces the former
/// thread-per-render, which paid thread start-up and a fresh WPF context on every page.
/// </summary>
internal sealed class StaRenderThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaRenderThread(string name)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        if (IsCurrentThread)
            return Task.FromResult(work());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        _queue.Add(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });
        return tcs.Task;
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
    }
}
