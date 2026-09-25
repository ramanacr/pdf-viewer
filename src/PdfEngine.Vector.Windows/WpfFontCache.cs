using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// Resolves <see cref="PdfFontFace"/> to WPF glyph typefaces: the embedded program when WPF can load
/// it (TrueType / OpenType), otherwise a metric-compatible system substitute chosen from the face's
/// style hints. Only used on the render STA thread.
/// </summary>
internal sealed class WpfFontCache : IDisposable
{
    private readonly Dictionary<string, GlyphTypeface?> _embedded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlyphTypeface?> _substitutes = new(StringComparer.Ordinal);
    private readonly string _tempDirectory;
    private readonly List<string> _tempFiles = new();

    public WpfFontCache()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PdfViewer", "fonts", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Embedded program as a glyph typeface, or null when absent or unloadable.</summary>
    public GlyphTypeface? GetEmbedded(PdfFontFace face)
    {
        if (face.Format is not (PdfFontProgramFormat.TrueType or PdfFontProgramFormat.OpenTypeCff) || face.ProgramData.IsEmpty)
            return null;

        if (_embedded.TryGetValue(face.Key, out var cached))
            return cached;

        GlyphTypeface? typeface = null;
        try
        {
            // GlyphTypeface only loads from a URI. Font bytes come from an untrusted PDF; they are
            // parsed by the same platform font stack that renders web fonts.
            Directory.CreateDirectory(_tempDirectory);
            string file = Path.Combine(_tempDirectory, SafeFileName(face.Key) + (face.Format == PdfFontProgramFormat.OpenTypeCff ? ".otf" : ".ttf"));
            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, face.ProgramData.ToArray());
                _tempFiles.Add(file);
            }
            typeface = new GlyphTypeface(new Uri(file));
            _ = typeface.GlyphCount; // force the parse now so failures surface here
        }
        catch (Exception ex) when (ex is FileFormatException or IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or NullReferenceException)
        {
            // Subset fonts frequently lack tables (name, OS/2, post) that WPF insists on.
            typeface = null;
        }

        _embedded[face.Key] = typeface;
        return typeface;
    }

    /// <summary>System substitute for a face; null when no honest substitute exists (e.g. dingbats).</summary>
    public GlyphTypeface? GetSubstitute(PdfFontFace? face, string? fontName)
    {
        string name = face?.PostScriptName ?? fontName ?? string.Empty;
        int plus = name.IndexOf('+');
        if (plus >= 0 && plus < name.Length - 1) name = name[(plus + 1)..];

        bool bold = face?.IsBold == true || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) || name.Contains("Black", StringComparison.OrdinalIgnoreCase);
        bool italic = face?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        string family;
        if (name.Contains("Dingbat", StringComparison.OrdinalIgnoreCase))
            return null; // no metric- or glyph-compatible system font
        if (name.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase))
            family = "Symbol";
        else if (face?.IsFixedPitch == true || name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Mono", StringComparison.OrdinalIgnoreCase))
            family = "Courier New";
        else if (face?.IsSerif == true || name.Contains("Times", StringComparison.OrdinalIgnoreCase) || name.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !name.Contains("Sans", StringComparison.OrdinalIgnoreCase))
            family = "Times New Roman";
        else if (TryInstalledFamily(name, out string installed))
            family = installed;
        else
            family = "Arial"; // metric-compatible with Helvetica

        string key = $"{family}|{bold}|{italic}";
        if (_substitutes.TryGetValue(key, out var cached))
            return cached;

        var typeface = new Typeface(new FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        GlyphTypeface? glyphTypeface = typeface.TryGetGlyphTypeface(out var gt) ? gt : null;
        _substitutes[key] = glyphTypeface;
        return glyphTypeface;
    }

    private static bool TryInstalledFamily(string postScriptName, out string family)
    {
        // "ArialMT", "Calibri-Bold", "SegoeUI" → installed family when the base name matches one.
        string baseName = postScriptName.Split('-', ',')[0];
        if (baseName.EndsWith("MT", StringComparison.Ordinal)) baseName = baseName[..^2];
        foreach (var f in Fonts.SystemFontFamilies)
        {
            string source = f.Source.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (string.Equals(source, baseName, StringComparison.OrdinalIgnoreCase))
            {
                family = f.Source;
                return true;
            }
        }
        family = string.Empty;
        return false;
    }

    private static string SafeFileName(string key)
    {
        var chars = key.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        }
        return new string(chars);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
