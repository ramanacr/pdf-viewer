using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PdfViewer.Core.Components;

namespace PdfViewer.Services;

/// <summary>
/// Creates the reader, if the optional Read Aloud component is installed.
///
/// The speech assembly is not shipped in the installer and is not inside the executable. The
/// runtime resolves an assembly when it first compiles a method that mentions one of its
/// types, so every mention of System.Speech is confined to <see cref="WindowsTextReader"/> and
/// reached only through the NoInlining method below. Without that, a machine without the
/// component would fail to start rather than simply lack a feature.
/// </summary>
public static class TextReaderFactory
{
    /// <summary>True when the component is present and matches its expected checksum.</summary>
    public static bool IsComponentInstalled(string? directory = null) =>
        OptionalComponents.ReadAloud.IsInstalled(directory ?? AppContext.BaseDirectory);

    /// <summary>
    /// Returns a reader, or null when the component is not installed or cannot be loaded.
    /// Never throws: a missing optional feature is a fact to report, not a failure.
    /// </summary>
    public static ITextReader? TryCreate(string? directory = null)
    {
        if (!IsComponentInstalled(directory)) return null;

        try
        {
            var reader = CreateReader();
            return reader.IsAvailable ? reader : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException
                                      or TypeLoadException or MissingMethodException)
        {
            // The component is there but unusable - wrong build, blocked by policy, corrupt.
            return null;
        }
    }

    /// <summary>
    /// Isolated so that resolving System.Speech happens here and nowhere else. Never inline
    /// this into a caller that runs before the component has been checked for.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WindowsTextReader CreateReader() => new();
}

/// <summary>
/// Reads text out loud.
///
/// Behind an interface because the view model's reading loop - advancing pages, stopping
/// cleanly, reporting a page with no text layer - is the part worth getting right and worth
/// testing, and none of that should require a sound card.
/// </summary>
public interface ITextReader : IDisposable
{
    /// <summary>False when the machine has no usable voice installed.</summary>
    bool IsAvailable { get; }

    bool IsPaused { get; }

    /// <summary>
    /// Speaks the text, completing when it has been read. Returns false if it was stopped
    /// part-way, which is how the caller knows not to move on to the next page.
    /// </summary>
    Task<bool> SpeakAsync(string text, CancellationToken cancellationToken);

    void Pause();
    void Resume();
    void Stop();
}

/// <summary>
/// Speaks through the voice built into Windows. No network, no account, no external service -
/// the same promise as the rest of the application.
/// </summary>
public sealed class WindowsTextReader : ITextReader
{
    private readonly System.Speech.Synthesis.SpeechSynthesizer? _synth;
    private readonly object _gate = new();
    private bool _isDisposed;

    public WindowsTextReader()
    {
        try
        {
            var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            synth.SetOutputToDefaultAudioDevice();

            // A machine with no installed voice throws only when asked to speak, so the
            // check happens here rather than at the first Read Aloud.
            if (synth.GetInstalledVoices().Count == 0)
            {
                synth.Dispose();
                return;
            }

            _synth = synth;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or NotSupportedException)
        {
            _synth = null;
        }
    }

    public bool IsAvailable => _synth != null;

    public bool IsPaused { get; private set; }

    public async Task<bool> SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (_synth == null || string.IsNullOrWhiteSpace(text)) return true;
        if (cancellationToken.IsCancellationRequested) return false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? sender, System.Speech.Synthesis.SpeakCompletedEventArgs e)
        {
            // Cancelled means the user stopped it; the caller must not move on.
            completion.TrySetResult(!e.Cancelled);
        }

        _synth.SpeakCompleted += OnCompleted;

        using var registration = cancellationToken.Register(Stop);

        try
        {
            lock (_gate)
            {
                IsPaused = false;
                _synth.SpeakAsync(text);
            }

            return await completion.Task;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The device disappeared or the synthesizer was torn down mid-sentence. Reading
            // stops; it is not worth an error dialog over.
            return false;
        }
        finally
        {
            _synth.SpeakCompleted -= OnCompleted;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_synth == null || _isDisposed || IsPaused) return;
            try
            {
                _synth.Pause();
                IsPaused = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_synth == null || _isDisposed || !IsPaused) return;
            try
            {
                _synth.Resume();
                IsPaused = false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_synth == null || _isDisposed) return;
            try
            {
                // A paused synthesizer ignores a cancel until it is running again, which
                // would leave Read Aloud stuck "stopping" forever.
                if (IsPaused)
                {
                    _synth.Resume();
                    IsPaused = false;
                }

                _synth.SpeakAsyncCancelAll();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (IsPaused) _synth?.Resume();
                _synth?.SpeakAsyncCancelAll();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }

            _synth?.Dispose();
        }
    }
}
