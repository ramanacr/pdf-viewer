using System;
using System.Threading;

namespace PdfEngine.Vector.Diagnostics;

/// <summary>
/// What the host platform lets the engine do. Browser WebAssembly runs .NET on a single thread
/// (<c>Thread.Start</c> throws there) and has no installed fonts or colour profiles to read, so the
/// engine skips its background work and its system lookups there. Android has threads, but its
/// fonts are in <c>/system/fonts</c> under other family names than Windows uses. Every other
/// platform keeps the behaviour it had before.
/// </summary>
internal static class PdfPlatform
{
    // Per async flow, so that a test overriding it does not change what tests running beside it see.
    private static readonly AsyncLocal<bool?> ThreadsOverride = new();
    private static readonly AsyncLocal<bool?> InstalledFontsOverride = new();
    private static readonly AsyncLocal<bool?> AndroidOverride = new();

    /// <summary>Where Android keeps the fonts every app may read.</summary>
    public const string AndroidFontDirectory = "/system/fonts";

    /// <summary>True when the engine may start threads of its own (not in single-threaded browser WebAssembly).</summary>
    public static bool CanStartThreads => ThreadsOverride.Value ?? !IsWebAssembly;

    /// <summary>True when the machine has an installed-fonts folder to read (not in a browser).</summary>
    public static bool HasInstalledFonts => InstalledFontsOverride.Value ?? !IsWebAssembly;

    /// <summary>True on Android, whose installed fonts are <see cref="AndroidFontDirectory"/> with Android's family names.</summary>
    public static bool IsAndroid => AndroidOverride.Value ?? OperatingSystem.IsAndroid();

    /// <summary>
    /// The folder of installed fonts: <see cref="AndroidFontDirectory"/> on Android, the platform's
    /// fonts folder elsewhere (Windows\Fonts), null when there is none (a browser).
    /// </summary>
    public static string? InstalledFontDirectory =>
        !HasInstalledFonts ? null
        : IsAndroid ? AndroidFontDirectory
        : Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static bool IsWebAssembly => OperatingSystem.IsBrowser() || OperatingSystem.IsWasi();

    /// <summary>Tests: behave as on a platform with (or without) threads and installed fonts, in the calling flow only.</summary>
    /// <param name="android">Behave as on Android (its font folder and family names); null keeps the real answer.</param>
    internal static IDisposable Simulate(bool canStartThreads, bool hasInstalledFonts, bool? android = null)
    {
        var previous = (ThreadsOverride.Value, InstalledFontsOverride.Value, AndroidOverride.Value);
        ThreadsOverride.Value = canStartThreads;
        InstalledFontsOverride.Value = hasInstalledFonts;
        AndroidOverride.Value = android;
        return new Restore(() => (ThreadsOverride.Value, InstalledFontsOverride.Value, AndroidOverride.Value) = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
