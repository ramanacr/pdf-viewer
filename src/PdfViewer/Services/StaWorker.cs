using System;
using System.Threading;
using System.Threading.Tasks;

namespace PdfViewer.Services;

/// <summary>
/// Runs work on a thread of its own in a single-threaded apartment: COM servers such as Windows
/// Image Acquisition and the Office applications expect to be called from one.
/// </summary>
internal static class StaWorker
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "COM worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
