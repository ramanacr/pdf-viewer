using System;
using System.Threading;

namespace PdfEngine.Vector.Diagnostics;

/// <summary>
/// What the host platform lets the engine do. Browser WebAssembly runs .NET on a single thread
/// (<c>Thread.Start</c> throws there) and has no installed fonts or colour profiles to read, so the
/// engine skips its background work and its system lookups there. Every other platform keeps the
/// behaviour it had before.
/// </summary>
internal static class PdfPlatform
{
    // Per async flow, so that a test overriding it does not change what tests running beside it see.
    private static readonly AsyncLocal<bool?> ThreadsOverride = new();
    private static readonly AsyncLocal<bool?> InstalledFontsOverride = new();

    /// <summary>True when the engine may start threads of its own (not in single-threaded browser WebAssembly).</summary>
    public static bool CanStartThreads => ThreadsOverride.Value ?? !IsWebAssembly;

    /// <summary>True when the machine has an installed-fonts folder to read (not in a browser).</summary>
    public static bool HasInstalledFonts => InstalledFontsOverride.Value ?? !IsWebAssembly;

    private static bool IsWebAssembly => OperatingSystem.IsBrowser() || OperatingSystem.IsWasi();

    /// <summary>Tests: behave as on a platform with (or without) threads and installed fonts, in the calling flow only.</summary>
    internal static IDisposable Simulate(bool canStartThreads, bool hasInstalledFonts)
    {
        var previous = (ThreadsOverride.Value, InstalledFontsOverride.Value);
        ThreadsOverride.Value = canStartThreads;
        InstalledFontsOverride.Value = hasInstalledFonts;
        return new Restore(() => (ThreadsOverride.Value, InstalledFontsOverride.Value) = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
