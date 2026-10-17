using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Color;

/// <summary>Colour profiles installed on the machine, read at run time and never shipped.</summary>
internal static class SystemColorProfiles
{
    /// <summary>The SWOP profile Windows installs (RSWOP.icm), or null when it is not available.</summary>
    public static byte[]? WindowsCmykProfile() =>
        OperatingSystem.IsWindows()
            ? Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm"))
            : null;

    /// <summary>
    /// Adobe's U.S. Web Coated (SWOP) v2 profile, which Acrobat and other Adobe applications install and
    /// convert DeviceCMYK with by default; null when no Adobe application installed it.
    /// </summary>
    public static byte[]? AdobeSwopProfile()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var folder in new[] { Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && Read(Path.Combine(root, "Adobe", "Color", "Profiles", "Recommended", "USWebCoatedSWOP.icc")) is { } bytes)
                return bytes;
        }
        return null;
    }

    private static byte[]? Read(string path)
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
}

/// <summary>
/// The conversion used for DeviceCMYK (and CMYK spaces without a usable profile), aiming at what Acrobat
/// shows: Adobe's own U.S. Web Coated (SWOP) profile when an Adobe application installed it, else the
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
    private static readonly Lazy<ColorTransform[]> Transforms = new(Load);

    /// <summary>True when the conversion comes from an installed profile rather than the built-in model.</summary>
    public static bool UsesInstalledProfile => Transforms.Value[0] is IccTransform;

    public static ColorTransform For(PdfRenderingIntent intent) => Transforms.Value[(int)intent & 3];

    private static ColorTransform[] Load()
    {
        if (SystemColorProfiles.AdobeSwopProfile() is { } bytes
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
