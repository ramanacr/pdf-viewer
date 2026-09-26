using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// The predefined CJK CMaps of ISO 32000-2 Table 116 (Adobe cmap-resources, BSD-3-Clause; see
/// Fonts/CMaps/LICENSE-cmap-resources.md), shipped as an embedded zip built by
/// eng/vectorpdf/cmaps/build-cmaps.ps1. Parsed on first use and cached for the process.
/// </summary>
internal static class PredefinedCMaps
{
    private const string ResourceName = "PdfEngine.Vector.PredefinedCMaps.zip";

    private static readonly Lazy<ZipArchive?> Archive = new(() =>
    {
        var stream = typeof(PredefinedCMaps).Assembly.GetManifestResourceStream(ResourceName);
        return stream == null ? null : new ZipArchive(stream, ZipArchiveMode.Read);
    });

    private static readonly ConcurrentDictionary<string, PdfCMap?> Cache = new(StringComparer.Ordinal);
    private static readonly object ArchiveLock = new();

    /// <summary>True when <paramref name="name"/> is one of the shipped predefined CMaps.</summary>
    public static bool Contains(string name)
    {
        if (Archive.Value is not { } zip)
            return false;
        lock (ArchiveLock)
            return zip.GetEntry(name) != null;
    }

    /// <summary>The predefined CMap called <paramref name="name"/>, or null when it is not shipped.</summary>
    public static PdfCMap? Get(string name) => Get(name, 0);

    internal static PdfCMap? Get(string name, int depth)
    {
        if (name is "Identity-H") return PdfCMap.IdentityH;
        if (name is "Identity-V") return PdfCMap.IdentityV;
        if (depth > PdfCMap.MaxUseCMapDepth)
            return null;
        if (Cache.TryGetValue(name, out var cached))
            return cached;

        byte[]? data = null;
        if (Archive.Value is { } zip)
        {
            lock (ArchiveLock)
            {
                var entry = zip.GetEntry(name);
                if (entry != null)
                {
                    using var s = entry.Open();
                    using var ms = new MemoryStream((int)entry.Length);
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
            }
        }

        PdfCMap? cmap = null;
        if (data != null)
        {
            var parsed = CMapParser.Parse(data);
            var baseCMap = parsed.UseCMapName is { } useName ? Get(useName, depth + 1) : null;
            cmap = PdfCMap.FromParsed(parsed, baseCMap, null, out bool unsupportedBase);
            if (unsupportedBase)
                cmap = null;
        }
        Cache[name] = cmap;
        return cmap;
    }
}
