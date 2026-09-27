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

    /// <summary>Faces by squashed family name, each flagged when that name is its legacy family.</summary>
    private readonly Lazy<Dictionary<string, List<(SystemFontFace Face, bool Legacy)>>> _byFamily;

    private SystemFontCatalog(IReadOnlyList<SystemFontFace> faces)
    {
        Faces = faces;
        _byFamily = new Lazy<Dictionary<string, List<(SystemFontFace, bool)>>>(() =>
        {
            var map = new Dictionary<string, List<(SystemFontFace, bool)>>(StringComparer.Ordinal);
            foreach (var face in faces)
            {
                string typographic = Squash(face.Family), legacy = face.LegacyFamily != null ? Squash(face.LegacyFamily) : typographic;
                Add(legacy, face, true);
                if (typographic != legacy) Add(typographic, face, false);
            }
            return map;

            void Add(string key, SystemFontFace face, bool isLegacy)
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<(SystemFontFace, bool)>();
                list.Add((face, isLegacy));
            }
        });
    }

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
                try
                {
                    if (new FileInfo(path).Length > 64L * 1024 * 1024) continue;
                    // Names only: the renderer scans this on its first non-embedded font.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                    int count = TrueTypeFontFile.FontCount(stream);
                    for (int i = 0; i < count; i++)
                        if (TrueTypeFontFile.ReadNames(stream, i) is { } n && n.Family.Length > 0)
                            faces.Add(new SystemFontFace(path, i, n.Family, n.Subfamily, n.PostScript, n.Weight, n.Italic, n.LegacyFamily));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
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
    public SystemFontFace? FindFamily(string family, bool bold, bool italic) => FindFamily(family, bold ? 700 : 400, italic);

    /// <summary>The face of <paramref name="family"/> nearest to <paramref name="weight"/> (100–900) and the slant asked for.</summary>
    public SystemFontFace? FindFamily(string family, int weight, bool italic)
    {
        // Either name finds the family: "Arial Narrow" is a family of its own to older software but
        // only a width of "Arial" typographically, and "Segoe UI" has its Semibold only typographically.
        // A face known by the legacy name wins a tie, so "Arial" bold is Arial Bold, not Arial Narrow Bold.
        if (!_byFamily.Value.TryGetValue(Squash(family), out var candidates)) return null;
        SystemFontFace? best = null;
        int bestScore = int.MaxValue;
        foreach (var (face, legacy) in candidates)
        {
            int score = (face.Italic == italic ? 0 : 10000) + Math.Abs(face.Weight - weight) * 2 + (legacy ? 0 : 1);
            if (score < bestScore) (best, bestScore) = (face, score);
        }
        return best;
    }

    /// <summary>
    /// The installed font that best stands in for a document font, as a viewer that renders with
    /// system fonts chooses it: the same font when it is installed (by PostScript name, then by
    /// family with subset prefix, MT/PS suffixes and style words removed, in the style the name and
    /// the flags ask for), then the design Windows ships under another name (Helvetica → Arial,
    /// Palatino → Palatino Linotype …), otherwise a face of the same kind (monospaced, script, serif
    /// or sans-serif, from the descriptor flags and the name) and style.
    /// </summary>
    public SystemFontFace? Match(string? baseFont, bool bold, bool italic, bool serif, bool fixedPitch, bool script = false)
    {
        string name = StripSubset(baseFont ?? string.Empty);
        int weight = bold ? 700 : 400;
        if (name.Length > 0)
        {
            // "Arial,Bold", "ArialMT", "TimesNewRomanPS-BoldItalicMT", "Calibri-Light", "VerdanaBold", "ArialNarrow,Bold".
            int cut = name.IndexOfAny(new[] { '-', ',' });
            string family = cut > 0 ? name[..cut] : name;
            string style = cut > 0 ? name[(cut + 1)..] : string.Empty;
            string bare = StripStyle(StripSuffix(family), out string glued);
            string words = glued + " " + style;
            if (StyleWeight(words) is int w) weight = bold && w < 600 ? 700 : w;
            italic |= Has(words, "Italic", "Oblique", "Slanted", "Inclined");
            bool narrow = Has(words, "Narrow", "Condensed", "Cond");

            var exact = Faces.FirstOrDefault(f => string.Equals(f.PostScriptName, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null && (exact.Weight >= 600 || weight < 600) && (exact.Italic || !italic)) return exact;

            // Another design stands in with its regular or bold only: those are the metric-compatible
            // ones (Arial Bold ≡ Helvetica-Bold), and /Widths still place every glyph.
            foreach (var (candidate, alias) in FamilyCandidates(family, bare, narrow))
                if (FindFamily(candidate, alias ? Math.Clamp(weight, 400, 700) : weight, italic) is { } f) return f;

            string standard = Fonts.Standard14Fonts.NormalizeName(name) ?? string.Empty;
            if (standard.StartsWith("Times", StringComparison.Ordinal)) serif = true;
            else if (standard.StartsWith("Courier", StringComparison.Ordinal)) fixedPitch = true;
            // Descriptor flags are often wrong (Verdana marked serif), so a name that says what it is wins.
            fixedPitch |= Has(name, "Mono", "Courier", "Typewriter", "Consolas");
            script |= Has(name, "Script", "Chancery", "Corsiva", "Brush", "Handwriting", "Calligraph");
            serif = !LooksSans(name) && (serif || LooksSerif(name));
        }
        string[] generic = fixedPitch ? new[] { "Courier New" }
            : script ? new[] { "Monotype Corsiva", "Segoe Script", "Times New Roman" }
            : serif ? new[] { "Times New Roman" }
            : new[] { "Arial" };
        foreach (var family in generic.Append("Segoe UI"))
            if (FindFamily(family, Math.Clamp(weight, 400, 700), italic) is { } f) return f;
        return null;
    }

    /// <summary>Faces tried, in order, for characters the chosen font does not have.</summary>
    public IEnumerable<SystemFontFace> Fallbacks(bool bold, bool italic)
    {
        foreach (var family in new[] { "Segoe UI", "Arial", "Segoe UI Symbol", "Nirmala UI", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic",
                     "Microsoft JhengHei", "Ebrima", "Gadugi", "Leelawadee UI", "Segoe UI Historic", "Cambria Math", "Segoe UI Emoji" })
            if (FindFamily(family, bold, italic) is { } f) yield return f;
    }

    /// <summary>Family names to look up, in order: as written, without MT/PS and style words, then the Windows alias.</summary>
    private static IEnumerable<(string Name, bool Alias)> FamilyCandidates(string family, string bare, bool narrow)
    {
        var names = new List<(string, bool)> { (family, false), (StripSuffix(family), false), (bare, false) };
        // PostScript families Windows installs under other names (or their closest design).
        foreach (var name in new[] { family, bare })
        {
            var alias = Aliases.FirstOrDefault(a => name.StartsWith(a.Prefix, StringComparison.OrdinalIgnoreCase)).Family;
            if (alias != null) { names.Add((alias, true)); break; }
        }
        foreach (var (name, alias) in names.DistinctBy(n => n.Item1, StringComparer.OrdinalIgnoreCase))
        {
            // "Helvetica-Narrow" → Arial Narrow, the only narrow width Windows ships.
            if (narrow && !name.Contains("Narrow", StringComparison.OrdinalIgnoreCase)) yield return (name + " Narrow", alias);
            yield return (name, alias);
        }
    }

    /// <summary>PostScript family prefixes and the Windows family of the same (or the closest) design; longer prefixes first.</summary>
    private static readonly (string Prefix, string Family)[] Aliases =
    {
        ("HelveticaNarrow", "Arial Narrow"), ("HelveticaCondensed", "Arial Narrow"), ("HelveticaNeue", "Arial"), ("Helvetica", "Arial"), ("Helv", "Arial"),
        ("TimesRoman", "Times New Roman"), ("TimesNewRoman", "Times New Roman"), ("Times", "Times New Roman"), ("Tms", "Times New Roman"),
        ("CourierNew", "Courier New"), ("Courier", "Courier New"), ("Palatino", "Palatino Linotype"), ("BookAntiqua", "Book Antiqua"),
        ("ITCAvantGarde", "Century Gothic"), ("AvantGarde", "Century Gothic"), ("Futura", "Century Gothic"), ("ITCBookman", "Bookman Old Style"), ("Bookman", "Bookman Old Style"),
        ("NewCenturySchlbk", "Century Schoolbook"), ("CenturySchoolbook", "Century Schoolbook"), ("ITCZapfChancery", "Monotype Corsiva"), ("ZapfChancery", "Monotype Corsiva"),
        ("GillSans", "Gill Sans MT"), ("Garamond", "Garamond"), ("Frutiger", "Arial"), ("Univers", "Arial"),
        ("Myriad", "Segoe UI"), ("Minion", "Cambria"), ("LucidaGrande", "Lucida Sans Unicode"), ("Tahoma", "Tahoma"), ("Verdana", "Verdana"), ("Georgia", "Georgia"),
    };

    private static string StripSuffix(string family)
    {
        foreach (var suffix in new[] { "PSMT", "MT", "PS" })
            if (family.EndsWith(suffix, StringComparison.Ordinal) && family.Length > suffix.Length)
                return family[..^suffix.Length];
        return family;
    }

    /// <summary>Style words written onto the family ("VerdanaBold", "TimesRoman"); <paramref name="styles"/> gets what was removed.</summary>
    private static string StripStyle(string family, out string styles)
    {
        styles = string.Empty;
        for (bool stripped = true; stripped;)
        {
            stripped = false;
            foreach (var word in StyleWords)
                if (family.Length > word.Length + 1 && family.EndsWith(word, StringComparison.Ordinal))
                {
                    family = family[..^word.Length];
                    styles = word + styles;
                    stripped = true;
                    break;
                }
        }
        return family;
    }

    private static readonly string[] StyleWords =
    {
        "Italic", "Oblique", "SemiBold", "Semibold", "DemiBold", "Demibold", "ExtraBold", "UltraBold", "Bold", "Black", "Heavy", "Demi",
        "ExtraLight", "UltraLight", "SemiLight", "Light", "Thin", "Medium", "Regular", "Roman", "Book", "Narrow", "Condensed", "Cond",
    };

    /// <summary>The weight style words ask for (100–900), null when they name none.</summary>
    private static int? StyleWeight(string words) =>
        Has(words, "ExtraBold", "UltraBold") ? 800
        : Has(words, "Black", "Heavy") ? 900
        : Has(words, "SemiBold", "DemiBold", "Demi") ? 600
        : Has(words, "Bold") ? 700
        : Has(words, "Medium") ? 500
        : Has(words, "ExtraLight", "UltraLight") ? 200
        : Has(words, "SemiLight") ? 350
        : Has(words, "Light") ? 300
        : Has(words, "Thin", "Hairline") ? 100
        : null;

    private static bool Has(string s, params string[] words) => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Serif by its name when there is no font descriptor to say so.</summary>
    private static bool LooksSerif(string name) =>
        Has(name, "Serif", "Times", "Roman", "Garamond", "Palatino", "Bookman", "Century", "Georgia", "Minion", "Caslon", "Baskerville", "Cambria", "Antiqua")
        && !Has(name, "Sans");

    /// <summary>Sans-serif by its name, whatever the descriptor flags claim.</summary>
    private static bool LooksSans(string name) =>
        Has(name, "Sans", "Gothic", "Grotesk", "Grotesque", "Helvetica", "Arial", "Verdana", "Tahoma", "Univers", "Frutiger", "Futura", "Myriad",
            "AvantGarde", "Franklin", "Optima", "Segoe", "Calibri");

    internal static string StripSubset(string name) =>
        name.Length > 7 && name[6] == '+' && name.Take(6).All(char.IsAsciiLetterUpper) ? name[7..] : name;

    private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
