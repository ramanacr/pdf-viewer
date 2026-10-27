using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Color;

/// <summary>
/// Gives the engine the CMYK colour profiles of the machine it runs on. The engine core reads no profile
/// itself, so it behaves the same on every platform (Windows, Linux, macOS, Android, a browser); a host
/// that has profiles to offer registers a provider with <see cref="ColorProfiles.Register"/>. Profiles are
/// read at run time and never shipped.
/// </summary>
public interface IColorProfileProvider
{
    /// <summary>
    /// A CMYK profile to convert DeviceCMYK with for display (Acrobat uses Adobe's U.S. Web Coated (SWOP) v2),
    /// or null to use the built-in model of it (<see cref="BuiltInColorProfiles"/>).
    /// </summary>
    byte[]? GetDisplayCmykProfile();

    /// <summary>
    /// A CMYK printer or monitor profile that PDF/A conversion embeds as the output intent of a CMYK document
    /// when the caller gives none (<c>PdfAConversionOptions.CmykProfile</c>), or null when there is none.
    /// </summary>
    byte[]? GetOutputIntentCmykProfile();
}

/// <summary>The default provider: no installed profiles. DeviceCMYK uses the built-in SWOP model (<see cref="SwopApproximation"/>).</summary>
public sealed class BuiltInColorProfiles : IColorProfileProvider
{
    /// <summary>The single instance.</summary>
    public static BuiltInColorProfiles Instance { get; } = new();

    private BuiltInColorProfiles() { }

    /// <inheritdoc />
    public byte[]? GetDisplayCmykProfile() => null;

    /// <inheritdoc />
    public byte[]? GetOutputIntentCmykProfile() => null;
}

/// <summary>The colour profile provider the engine uses (<see cref="BuiltInColorProfiles"/> until a host registers another).</summary>
public static class ColorProfiles
{
    private static IColorProfileProvider _registered = BuiltInColorProfiles.Instance;

    // Per async flow, so that a test overriding it does not change what tests running beside it see.
    private static readonly AsyncLocal<IColorProfileProvider?> Override = new();

    /// <summary>The provider in use.</summary>
    public static IColorProfileProvider Provider => Override.Value ?? Volatile.Read(ref _registered);

    /// <summary>
    /// Makes <paramref name="provider"/> the provider for the process. Register it at start-up, before
    /// documents are opened: pages already read keep the colours of the provider they were read with.
    /// </summary>
    public static void Register(IColorProfileProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Volatile.Write(ref _registered, provider);
    }

    /// <summary>
    /// Reads a profile file for a provider: its bytes, or null when the file is missing, unreadable or larger
    /// than a profile may be.
    /// </summary>
    public static byte[]? ReadProfileFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= IccProfile.MaxProfileBytes ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Tests: use <paramref name="provider"/> in the calling flow only.</summary>
    internal static IDisposable Simulate(IColorProfileProvider provider)
    {
        var previous = Override.Value;
        Override.Value = provider;
        return new Restore(() => Override.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>
/// The conversion used for DeviceCMYK (and CMYK spaces without a usable profile), aiming at what Acrobat
/// shows: the provider's display profile (<see cref="IColorProfileProvider.GetDisplayCmykProfile"/>; on
/// Windows, Adobe's own U.S. Web Coated (SWOP) profile when an Adobe application installed it), else the
/// built-in model of it (<see cref="SwopApproximation"/>).
/// </summary>
/// <remarks>
/// The SWOP profile Windows installs (RSWOP.icm) is deliberately not used for display. It is a different
/// characterization: its solid cyan is 0/159/215 where Acrobat shows 0/174/239, and over a 9-point CMYK
/// grid it is 12.7 levels per channel from PDFium's Adobe-like conversion, against 2.4 for the built-in
/// model; on the corpus the built-in model was also closer to PDFium (mean 0.7461 against 0.7491).
/// </remarks>
internal static class DefaultCmyk
{
    // One set of transforms per provider, the last one used kept at hand.
    private static readonly ConditionalWeakTable<IColorProfileProvider, ColorTransform[]> ByProvider = new();
    private static Entry? _last;

    private sealed record Entry(IColorProfileProvider Provider, ColorTransform[] Transforms);

    /// <summary>True when the conversion comes from an installed profile rather than the built-in model.</summary>
    public static bool UsesInstalledProfile => Transforms()[0] is IccTransform;

    public static ColorTransform For(PdfRenderingIntent intent) => Transforms()[(int)intent & 3];

    private static ColorTransform[] Transforms()
    {
        var provider = ColorProfiles.Provider;
        var last = Volatile.Read(ref _last);
        if (last != null && ReferenceEquals(last.Provider, provider))
            return last.Transforms;
        var transforms = ByProvider.GetValue(provider, Load);
        Volatile.Write(ref _last, new Entry(provider, transforms));
        return transforms;
    }

    private static ColorTransform[] Load(IColorProfileProvider provider)
    {
        if (provider.GetDisplayCmykProfile() is { } bytes
            && bytes.Length <= IccProfile.MaxProfileBytes
            && IccProfile.TryParse(bytes, out _) is { Channels: 4 } profile)
        {
            var result = new ColorTransform[4];
            for (int i = 0; i < 4; i++) result[i] = IccTransform.Create(profile, (PdfRenderingIntent)i);
            return result;
        }
        var builtIn = SwopApproximation.Instance;
        return [builtIn, builtIn, builtIn, builtIn];
    }
}

/// <summary>A parsed ICC profile shared by every colour space that embeds it, with one transform per intent.</summary>
internal sealed class IccProfileHandle
{
    private readonly IccTransform?[] _transforms = new IccTransform?[4];

    public IccProfileHandle(IccProfile profile) => Profile = profile;

    public IccProfile Profile { get; }

    public IccTransform Transform(PdfRenderingIntent intent)
    {
        int i = (int)intent & 3;
        return _transforms[i] ??= IccTransform.Create(Profile, intent);
    }

    private sealed class Entry(IccProfileHandle? handle)
    {
        public IccProfileHandle? Handle { get; } = handle;
    }

    // Per profile stream (the resolver keeps stream objects alive and unique), then by content so the
    // same profile embedded in many documents is parsed once.
    private static readonly ConditionalWeakTable<PdfStream, Entry> ByStream = new();
    private static readonly Dictionary<string, IccProfileHandle?> ByContent = new(StringComparer.Ordinal);
    private const int MaxContentEntries = 64;

    /// <summary>The profile of an ICCBased stream, or null when it cannot be decoded or parsed.</summary>
    public static IccProfileHandle? For(PdfStream stream, Func<byte[]> decode)
    {
        if (ByStream.TryGetValue(stream, out var entry))
            return entry.Handle;
        IccProfileHandle? handle = null;
        try
        {
            handle = FromBytes(decode());
        }
        catch (Exception ex) when (ex is PdfVectorException or InvalidDataException or IOException or OverflowException)
        {
            handle = null;
        }
        ByStream.AddOrUpdate(stream, new Entry(handle));
        return handle;
    }

    public static IccProfileHandle? FromBytes(byte[] bytes)
    {
        if (bytes.Length < 132 || bytes.Length > IccProfile.MaxProfileBytes)
            return null;
        string key = Convert.ToHexString(SHA256.HashData(bytes));
        lock (ByContent)
        {
            if (ByContent.TryGetValue(key, out var cached))
                return cached;
        }
        var profile = IccProfile.TryParse(bytes, out _);
        var handle = profile == null ? null : new IccProfileHandle(profile);
        lock (ByContent)
        {
            if (ByContent.Count >= MaxContentEntries)
                ByContent.Clear();
            ByContent[key] = handle;
        }
        return handle;
    }
}
