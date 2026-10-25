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
        // Unique per cache instance: WPF remembers folders it has read fonts from, so a path that is
        // deleted and recreated (a disposed renderer followed by a new one) must never be reused.
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PdfViewer", "fonts",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
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
            //
            // One directory per program: WPF's font cache fails (NullReferenceException inside
            // FontFaceLayoutInfo) for a second font file loaded from a folder it has already seen,
            // which previously made every embedded font after the first fall back silently.
            string folder = Path.Combine(_tempDirectory, SafeFileName(face.Key));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "font" + (face.Format == PdfFontProgramFormat.OpenTypeCff ? ".otf" : ".ttf"));
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
        // The installed font of that name or design first, as the Direct2D backend chooses it.
        if (!name.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase)
            && PdfEngine.Vector.Editing.SystemFontCatalog.Installed.Match(name, bold, italic, face?.IsSerif == true, face?.IsFixedPitch == true, face?.IsScript == true) is { } catalogFace
            && GetInstalled(catalogFace, bold, italic) is { } match)
            return match;
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

    /// <summary>The installed face the shared font catalogue chose, from its file (a single-font file only).</summary>
    private GlyphTypeface? GetInstalled(PdfEngine.Vector.Editing.SystemFontFace installed, bool bold, bool italic)
    {
        if (installed.Index != 0 || installed.Path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
            return null;
        var simulations = (bold && !installed.Bold ? StyleSimulations.BoldSimulation : StyleSimulations.None)
                          | (italic && !installed.Italic ? StyleSimulations.ItalicSimulation : StyleSimulations.None);
        string key = $"file:{installed.Path}|{simulations}";
        if (_substitutes.TryGetValue(key, out var cached))
            return cached;
        GlyphTypeface? typeface;
        try
        {
            typeface = new GlyphTypeface(new Uri(installed.Path), simulations);
            _ = typeface.GlyphCount;
        }
        catch (Exception ex) when (ex is FileFormatException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            typeface = null;
        }
        _substitutes[key] = typeface;
        return typeface;
    }

    private static bool TryInstalledFamily(string postScriptName, out string family)
    {
        // "ArialMT", "Calibri-Bold", "SegoeUI" → installed family when the base name matches one.
        string baseName = postScriptName.Split('-', ',')[0];
        if (baseName.EndsWith("MT", StringComparison.Ordinal)) baseName = baseName[..^2];
        foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
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

    private readonly Dictionary<GlyphTypeface, PdfEngine.Vector.Editing.TrueTypeFontFile?> _shapingFonts = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A substitute's font file as the editing shaper reads it (its cmap, GSUB and GPOS), read once on
    /// first use; null when its file is not an installed font or cannot be read. A collection's face is
    /// found by its names in the shared font catalogue, as WPF does not say which face it loaded.
    /// </summary>
    public PdfEngine.Vector.Editing.TrueTypeFontFile? GetShapingFont(GlyphTypeface typeface)
    {
        if (_shapingFonts.TryGetValue(typeface, out var cached))
            return cached;
        PdfEngine.Vector.Editing.TrueTypeFontFile? result = null;
        try
        {
            if (typeface.FontUri is { IsFile: true } uri && FaceIndex(uri.LocalPath, typeface) is int index)
            {
                result = PdfEngine.Vector.Editing.TrueTypeFontFile.TryLoad(File.ReadAllBytes(uri.LocalPath), index);
                if (result != null && result.GlyphCount != typeface.GlyphCount) result = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            result = null;
        }
        _shapingFonts[typeface] = result;
        return result;
    }

    /// <summary>The index of the typeface's face in its file: 0 for a single font, else the catalogue face of its names.</summary>
    private static int? FaceIndex(string path, GlyphTypeface typeface)
    {
        if (!path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".otc", StringComparison.OrdinalIgnoreCase))
            return 0;
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        string? family = typeface.Win32FamilyNames.TryGetValue(english, out var f) ? f : null;
        string? style = typeface.Win32FaceNames.TryGetValue(english, out var s) ? s : null;
        int? found = null;
        foreach (var face in PdfEngine.Vector.Editing.SystemFontCatalog.Installed.Faces)
        {
            if (!string.Equals(face.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
            if ((string.Equals(face.Family, family, StringComparison.OrdinalIgnoreCase) || string.Equals(face.LegacyFamily, family, StringComparison.OrdinalIgnoreCase))
                && (style == null || string.Equals(face.Subfamily, style, StringComparison.OrdinalIgnoreCase)))
            {
                if (style != null) return face.Index;
                // Without a style name only one face of the family may match: two faces of a
                // collection can have the same glyph count, so a guess would draw the wrong glyphs.
                if (found != null) return null;
                found = face.Index;
            }
        }
        return found;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                File.Delete(file);
                Directory.Delete(Path.GetDirectoryName(file)!);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
