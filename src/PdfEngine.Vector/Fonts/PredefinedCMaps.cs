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

    private static readonly ConcurrentDictionary<string, System.Collections.Generic.Dictionary<int, string>?> CidUnicode = new(StringComparer.Ordinal);

    /// <summary>
    /// Unicode for a CID of an Adobe character collection (Japan1, GB1, CNS1, Korea1), derived by
    /// inverting the shipped Uni*-UTF16-H CMap (BMP forms first, then the lowest code point).
    /// Used for text extraction when a font has no /ToUnicode and its encoding is not Unicode.
    /// </summary>
    public static string? CidToUnicode(string ordering, int cid)
    {
        var map = CidUnicode.GetOrAdd(ordering, static o => BuildCidToUnicode(o switch
        {
            "Japan1" => "UniJIS-UTF16-H",
            "GB1" => "UniGB-UTF16-H",
            "CNS1" => "UniCNS-UTF16-H",
            "Korea1" => "UniKS-UTF16-H",
            _ => null,
        }));
        return map != null && map.TryGetValue(cid, out var s) ? s : null;
    }

    private static System.Collections.Generic.Dictionary<int, string>? BuildCidToUnicode(string? cmapName)
    {
        if (cmapName == null || Archive.Value is not { } zip)
            return null;
        byte[] data;
        lock (ArchiveLock)
        {
            var entry = zip.GetEntry(cmapName);
            if (entry == null)
                return null;
            using var s = entry.Open();
            using var ms = new MemoryStream((int)entry.Length);
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        var parsed = CMapParser.Parse(data);
        var best = new System.Collections.Generic.Dictionary<int, (string Text, uint Code)>();
        foreach (var range in parsed.CidRanges)
        {
            if (range.High < range.Low || range.High - range.Low > 0xFFFF)
                continue;
            for (uint code = range.Low; code <= range.High; code++)
            {
                int cid = range.Cid + (int)(code - range.Low);
                string text;
                if (range.Length == 2)
                {
                    if (code is >= 0xD800 and <= 0xDFFF) continue;
                    text = ((char)code).ToString();
                }
                else if (range.Length == 4)
                {
                    char hi = (char)(code >> 16), lo = (char)(code & 0xFFFF);
                    if (!char.IsSurrogatePair(hi, lo)) continue;
                    text = new string(new[] { hi, lo });
                }
                else continue;
                // Deterministic choice for CIDs reachable from several code points:
                // BMP before supplementary, then the lowest code point.
                if (!best.TryGetValue(cid, out var existing) || existing.Text.Length > text.Length ||
                    (existing.Text.Length == text.Length && code < existing.Code))
                    best[cid] = (text, code);
            }
        }
        var map = new System.Collections.Generic.Dictionary<int, string>(best.Count);
        foreach (var (cid, entry) in best) map[cid] = entry.Text;
        return map;
    }

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
