using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Annotations;

namespace PdfViewer.Services;

/// <summary>A measuring scale the user set: "<see cref="PaperValue"/> <see cref="PaperUnit"/> = <see cref="RealValue"/> <see cref="RealUnit"/>".</summary>
public sealed record MeasureScale(double PaperValue, string PaperUnit, double RealValue, string RealUnit, int Decimals = 2)
{
    /// <summary>One inch is one inch: what a page measures before a scale is set.</summary>
    public static MeasureScale Default { get; } = new(1, "in", 1, "in");

    /// <summary>The paper units a scale can start from (a drawing is printed in these).</summary>
    public static IReadOnlyList<string> PaperUnits { get; } = new[] { "pt", "in", "mm", "cm" };

    public PdfMeasure ToMeasure() => PdfMeasure.FromScale(PaperValue, PaperUnit, RealValue, RealUnit, Decimals);

    public bool IsValid =>
        PaperValue > 0 && RealValue > 0 && double.IsFinite(PaperValue) && double.IsFinite(RealValue)
        && PdfMeasure.PointsPer(PaperUnit) != null && PdfMeasure.PointsPer(RealUnit) != null && Decimals is >= 0 and <= 6;
}

/// <summary>
/// The scale set for each document, remembered on this computer (in %LOCALAPPDATA%, never in the
/// document): keyed by a hash of the file's path, so the file is not named in the settings. The
/// last 200 documents are kept.
/// </summary>
public static class MeasureScaleStore
{
    private const int MaxDocuments = 200;

    private sealed class Entry
    {
        public string Key { get; set; } = string.Empty;
        public double PaperValue { get; set; }
        public string PaperUnit { get; set; } = "in";
        public double RealValue { get; set; }
        public string RealUnit { get; set; } = "in";
        public int Decimals { get; set; } = 2;
        public DateTime Used { get; set; }
    }

    private static string FilePath => Path.Combine(SigningSettings.Directory, "measure-scales.json");

    private static string KeyOf(string documentPath)
    {
        string full;
        try { full = Path.GetFullPath(documentPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = documentPath; }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
    }

    private static List<Entry> Read()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>The scale set for the document at <paramref name="documentPath"/>, or null.</summary>
    public static MeasureScale? Load(string? documentPath)
    {
        if (string.IsNullOrEmpty(documentPath)) return null;
        string key = KeyOf(documentPath);
        var e = Read().FirstOrDefault(x => x.Key == key);
        if (e == null) return null;
        var scale = new MeasureScale(e.PaperValue, e.PaperUnit, e.RealValue, e.RealUnit, e.Decimals);
        return scale.IsValid ? scale : null;
    }

    public static void Save(string? documentPath, MeasureScale scale)
    {
        if (string.IsNullOrEmpty(documentPath) || !scale.IsValid) return;
        string key = KeyOf(documentPath);
        var list = Read().Where(x => x.Key != key).ToList();
        list.Insert(0, new Entry
        {
            Key = key, PaperValue = scale.PaperValue, PaperUnit = scale.PaperUnit, RealValue = scale.RealValue,
            RealUnit = scale.RealUnit, Decimals = scale.Decimals, Used = DateTime.UtcNow,
        });
        try
        {
            Directory.CreateDirectory(SigningSettings.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Take(MaxDocuments).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering the scale is a convenience; measuring does not depend on it.
        }
    }
}
