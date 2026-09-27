using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PdfEngine.Vector.Editing;

/// <summary>An installed font face: where it is and what it is called.</summary>
/// <param name="LegacyFamily">The family as older software names it ("Arial Narrow" where the typographic family is "Arial").</param>
public sealed record SystemFontFace(string Path, int Index, string Family, string Subfamily, string PostScriptName, int Weight, bool Italic, string? LegacyFamily = null)
{
    public bool Bold => Weight >= 600;
}

/// <summary>
/// The TrueType fonts in a font directory, found by PostScript name or by family and style, with
/// a fallback chain for characters the chosen font lacks. Only fonts whose licence allows
/// embedding are used.
/// </summary>
public sealed class SystemFontCatalog
{
    private static readonly ConcurrentDictionary<string, SystemFontCatalog> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, int), TrueTypeFontFile?> _loaded = new();

    public IReadOnlyList<SystemFontFace> Faces { get; }

    private SystemFontCatalog(IReadOnlyList<SystemFontFace> faces) => Faces = faces;

    /// <summary>The fonts installed for everyone on this machine (Windows\Fonts), read once.</summary>
    public static SystemFontCatalog Installed =>
        ForDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));

    public static SystemFontCatalog ForDirectory(string directory) => Catalogs.GetOrAdd(directory, Scan);

    private static SystemFontCatalog Scan(string directory)
    {
        var faces = new List<SystemFontFace>();
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".ttf" or ".ttc")) continue;
                byte[] data;
                try
                {
                    if (new FileInfo(path).Length > 64L * 1024 * 1024) continue;
                    data = File.ReadAllBytes(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                int count = TrueTypeFontFile.FontCount(data);
                for (int i = 0; i < count; i++)
                    if (TrueTypeFontFile.ReadNames(data, i) is { } n && n.Family.Length > 0)
                        faces.Add(new SystemFontFace(path, i, n.Family, n.Subfamily, n.PostScript, n.Weight, n.Italic, n.LegacyFamily));
            }
        }
        return new SystemFontCatalog(faces);
    }

    public TrueTypeFontFile? Load(SystemFontFace face) => _loaded.GetOrAdd((face.Path, face.Index), key =>
    {
        try
        {
            var font = TrueTypeFontFile.TryLoad(File.ReadAllBytes(key.Item1), key.Item2);
            return font is { EmbeddingAllowed: true } ? font : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    });

    /// <summary>The face of <paramref name="family"/> nearest to the weight and slant asked for.</summary>
    public SystemFontFace? FindFamily(string family, bool bold, bool italic)
    {
        string key = Squash(family);
        // The legacy family first: "Arial Narrow" is its own family there, but only a style of "Arial" typographically.
        var candidates = Faces.Where(f => f.LegacyFamily != null && Squash(f.LegacyFamily) == key).ToList();
        if (candidates.Count == 0) candidates = Faces.Where(f => Squash(f.Family) == key).ToList();
        if (candidates.Count == 0) return null;
        int weight = bold ? 700 : 400;
        return candidates.OrderBy(f => (f.Italic == italic ? 0 : 1000) + Math.Abs(f.Weight - weight)).First();
    }

    /// <summary>
    /// The installed font that best stands in for a document font: the same font when it is
    /// installed (by PostScript name, then by family), otherwise a face of the same kind
    /// (serif, sans-serif or monospaced) and style.
    /// </summary>
    public SystemFontFace? Match(string? baseFont, bool bold, bool italic, bool serif, bool fixedPitch)
    {
        string name = StripSubset(baseFont ?? string.Empty);
        if (name.Length > 0)
        {
            var exact = Faces.FirstOrDefault(f => string.Equals(f.PostScriptName, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // "Arial,Bold", "ArialMT", "TimesNewRomanPS-BoldItalicMT", "Calibri-Light".
            string style = name.Contains('-') ? name[(name.IndexOf('-') + 1)..] : name.Contains(',') ? name[(name.IndexOf(',') + 1)..] : string.Empty;
            string family = name.Split('-', ',')[0];
            bold |= style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || style.Contains("Black", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("Semibold", StringComparison.OrdinalIgnoreCase) || style.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("Demi", StringComparison.OrdinalIgnoreCase);
            serif |= LooksSerif(name);
            italic |= style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
            foreach (var candidate in FamilyCandidates(family))
                if (FindFamily(candidate, bold, italic) is { } f) return f;
            string standard = Fonts.Standard14Fonts.NormalizeName(name) ?? string.Empty;
            if (standard.StartsWith("Times", StringComparison.Ordinal)) serif = true;
            else if (standard.StartsWith("Courier", StringComparison.Ordinal)) fixedPitch = true;
        }
        string generic = fixedPitch ? "Courier New" : serif ? "Times New Roman" : "Arial";
        return FindFamily(generic, bold, italic) ?? FindFamily("Segoe UI", bold, italic);
    }

    /// <summary>Faces tried, in order, for characters the chosen font does not have.</summary>
    public IEnumerable<SystemFontFace> Fallbacks(bool bold, bool italic)
    {
        foreach (var family in new[] { "Segoe UI", "Arial", "Segoe UI Symbol", "Nirmala UI", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic",
                     "Microsoft JhengHei", "Ebrima", "Gadugi", "Leelawadee UI", "Segoe UI Historic", "Cambria Math", "Segoe UI Emoji" })
            if (FindFamily(family, bold, italic) is { } f) yield return f;
    }

    private static IEnumerable<string> FamilyCandidates(string family)
    {
        yield return family;
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
            if (family.EndsWith(suffix, StringComparison.Ordinal) && family.Length > suffix.Length)
                yield return family[..^suffix.Length];
        // PostScript families Windows installs under other names (or their closest design).
        foreach (var (prefix, alias) in Aliases)
            if (family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { yield return alias; break; }
    }

    private static readonly (string Prefix, string Family)[] Aliases =
    {
        ("HelveticaNeue", "Arial"), ("Helvetica", "Arial"), ("TimesRoman", "Times New Roman"), ("TimesNewRoman", "Times New Roman"), ("Times", "Times New Roman"),
        ("CourierNew", "Courier New"), ("Courier", "Courier New"), ("Palatino", "Palatino Linotype"), ("BookAntiqua", "Book Antiqua"),
        ("AvantGarde", "Century Gothic"), ("ITCAvantGarde", "Century Gothic"), ("Futura", "Century Gothic"), ("Bookman", "Bookman Old Style"),
        ("NewCenturySchlbk", "Century Schoolbook"), ("CenturySchoolbook", "Century Schoolbook"), ("ZapfChancery", "Monotype Corsiva"),
        ("GillSans", "Gill Sans MT"), ("Garamond", "Garamond"), ("Frutiger", "Arial"), ("Univers", "Arial"), ("Optima", "Candara"),
        ("Myriad", "Segoe UI"), ("Minion", "Cambria"), ("Tahoma", "Tahoma"), ("Verdana", "Verdana"), ("Georgia", "Georgia"),
    };

    /// <summary>Serif by its name when there is no font descriptor to say so.</summary>
    private static bool LooksSerif(string name) =>
        new[] { "Serif", "Times", "Roman", "Garamond", "Palatino", "Bookman", "Century", "Georgia", "Minion", "Caslon", "Baskerville", "Cambria", "Antiqua" }
            .Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)) && !name.Contains("Sans", StringComparison.OrdinalIgnoreCase);

    internal static string StripSubset(string name) =>
        name.Length > 7 && name[6] == '+' && name.Take(6).All(char.IsAsciiLetterUpper) ? name[7..] : name;

    private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
