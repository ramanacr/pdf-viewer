using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PdfViewer.Views;

/// <summary>
/// Asks about unsaved work when a window is closed, and closes it once the answer allows.
///
/// The question is asynchronous (a "Yes" saves first) and the Closing event is not, so the
/// first close is always cancelled and the window is closed again once the answer is in. That
/// second close is queued on the dispatcher rather than made from inside the handler: "No"
/// saves nothing, so the answer arrives before the Closing event has returned, and WPF refuses
/// Close() while a window is closing. The InvalidOperationException escaped the async handler
/// and took the process down, which looked like a slow close because Windows Error Reporting
/// kept the frozen window on screen for many seconds before the process went.
/// </summary>
internal sealed class ClosePrompt
{
    private readonly Window _window;
    private readonly Func<bool> _hasUnsavedWork;
    private readonly Func<Task<bool>> _confirm;
    private bool _confirmed;
    private bool _asking;

    public ClosePrompt(Window window, Func<bool> hasUnsavedWork, Func<Task<bool>> confirm)
    {
        _window = window;
        _hasUnsavedWork = hasUnsavedWork;
        _confirm = confirm;
    }

    /// <summary>Call from the window's Closing event.</summary>
    public async void OnClosing(CancelEventArgs e)
    {
        if (_confirmed || !_hasUnsavedWork()) return;

        e.Cancel = true;
        // A second close request while the question is still open is the same request.
        if (_asking) return;

        _asking = true;
        bool close;
        try
        {
            close = await _confirm();
        }
        finally
        {
            _asking = false;
        }

        if (!close) return;

        _confirmed = true;
        // Queued, not awaited: there is nothing left to do here once the window closes.
        _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(_window.Close));
    }
}
