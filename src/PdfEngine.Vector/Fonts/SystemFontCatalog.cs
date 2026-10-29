using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Fonts;

/// <summary>An installed font face: where it is and what it is called.</summary>
/// <param name="LegacyFamily">The family as older software names it ("Arial Narrow" where the typographic family is "Arial").</param>
/// <param name="OpenTypeCff">The face has CFF outlines (an <c>.otf</c> file, or a CFF face of a <c>.ttc</c> collection).</param>
public sealed record SystemFontFace(string Path, int Index, string Family, string Subfamily, string PostScriptName, int Weight, bool Italic, string? LegacyFamily = null, bool OpenTypeCff = false)
{
    public bool Bold => Weight >= 600;
}

/// <summary>
/// The TrueType fonts in a font directory (on Android also those with CFF outlines), found by PostScript name or by family and style, with
/// a fallback chain for characters the chosen font lacks. Only fonts whose licence allows
/// embedding are used. Faces with CFF outlines are embedded as CIDFontType0 (FontFile3); where a
/// TrueType face can stand in, it is preferred (see <see cref="Fallbacks"/>).
/// </summary>
public sealed class SystemFontCatalog
{
    private static readonly ConcurrentDictionary<string, SystemFontCatalog> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, int), TrueTypeFontFile?> _loaded = new();

    public IReadOnlyList<SystemFontFace> Faces { get; }

    /// <summary>Faces by squashed family name, each flagged when that name is its legacy family.</summary>
    private readonly Lazy<Dictionary<string, List<(SystemFontFace Face, bool Legacy)>>> _byFamily;

    /// <summary>Families looked up when a family is missing (squashed name → stand-ins in order), or null.</summary>
    private readonly Dictionary<string, string[]>? _aliases;

    private SystemFontCatalog(IReadOnlyList<SystemFontFace> faces, IReadOnlyDictionary<string, string[]>? aliases = null)
    {
        Faces = faces;
        if (aliases != null)
        {
            _aliases = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (family, standIns) in aliases) _aliases[Squash(family)] = standIns;
        }
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

    /// <summary>
    /// The fonts installed for everyone on this machine (Windows\Fonts), read once. On Android,
    /// <c>/system/fonts</c> with <see cref="AndroidFamilyAliases"/> (<see cref="ForAndroid"/>). Empty
    /// in a browser, which has no font folder: a web host passes its own catalog (<see cref="ForDirectory"/>).
    /// </summary>
    public static SystemFontCatalog Installed =>
        PdfEngine.Vector.Diagnostics.PdfPlatform.InstalledFontDirectory is not { } directory ? None
        : PdfEngine.Vector.Diagnostics.PdfPlatform.IsAndroid ? ForAndroid(directory)
        : ForDirectory(directory);

    private static readonly ConcurrentDictionary<string, SystemFontCatalog> AndroidCatalogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The fonts of an Android font folder (<c>/system/fonts</c>), where the families that
    /// substitution asks for by their Windows names stand in by <see cref="AndroidFamilyAliases"/>:
    /// Arial and Helvetica are drawn with Roboto, Times with Noto Serif, Courier with Droid Sans Mono,
    /// each glyph still placed by the document's widths. Fonts with CFF outlines (<c>.otf</c>, and
    /// the CFF faces of <c>.ttc</c> collections such as Noto Sans CJK) are catalogued too: the Skia
    /// backend, the only renderer on Android, loads them itself.
    /// </summary>
    public static SystemFontCatalog ForAndroid(string directory = PdfEngine.Vector.Diagnostics.PdfPlatform.AndroidFontDirectory) =>
        AndroidCatalogs.GetOrAdd(directory, d => new SystemFontCatalog(ScanFaces(d, openTypeCff: true), AndroidFamilyAliases));

    /// <summary>This catalog's faces, where a missing family is looked up as the families <paramref name="aliases"/> maps it to, in order.</summary>
    public SystemFontCatalog WithFamilyAliases(IReadOnlyDictionary<string, string[]> aliases) => new(Faces, aliases);

    /// <summary>
    /// The families substitution asks for (the Windows families of <see cref="Match"/>, <see cref="Fallbacks"/>
    /// and the renderers' Unicode fallbacks) and the Android families that stand in for them. The
    /// names are those in the fonts' name tables on Android 7 to 15; a family a device lacks is skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> AndroidFamilyAliases { get; } = BuildAndroidAliases();

    private static Dictionary<string, string[]> BuildAndroidAliases()
    {
        string[] sans = { "Roboto", "Noto Sans", "Droid Sans" };
        string[] serif = { "Noto Serif", "Droid Serif" };
        string[] mono = { "Droid Sans Mono", "Cutive Mono" };
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { "Arial", "Arial Narrow", "Helvetica", "Segoe UI", "Calibri", "Verdana", "Tahoma", "Century Gothic", "Gill Sans MT", "Lucida Sans Unicode", "Franklin Gothic" })
            map[f] = sans;
        foreach (var f in new[] { "Times New Roman", "Times", "Georgia", "Cambria", "Palatino Linotype", "Book Antiqua", "Garamond", "Century Schoolbook", "Bookman Old Style" })
            map[f] = serif;
        foreach (var f in new[] { "Courier New", "Courier", "Consolas", "Lucida Console" })
            map[f] = mono;
        foreach (var f in new[] { "Monotype Corsiva", "Segoe Script" })
            map[f] = new[] { "Dancing Script" };
        map["Segoe UI Symbol"] = new[] { "Noto Sans Symbols 2", "Noto Sans Symbols" };
        map["Cambria Math"] = new[] { "Noto Sans Math" };
        map["Microsoft YaHei"] = new[] { "Noto Sans CJK SC", "Noto Sans SC", "Droid Sans Fallback" };
        map["Microsoft JhengHei"] = new[] { "Noto Sans CJK TC", "Noto Sans TC", "Droid Sans Fallback" };
        map["Yu Gothic"] = new[] { "Noto Sans CJK JP", "Noto Sans JP", "Droid Sans Fallback" };
        map["Malgun Gothic"] = new[] { "Noto Sans CJK KR", "Noto Sans KR", "Droid Sans Fallback" };
        map["Nirmala UI"] = new[] { "Noto Sans Devanagari" };
        map["Leelawadee UI"] = new[] { "Noto Sans Thai" };
        map["Ebrima"] = new[] { "Noto Sans Ethiopic" };
        map["Segoe UI Historic"] = new[] { "Noto Sans" };
        return map;
    }

    /// <summary>
    /// The families substitution asks for and the Liberation family that stands in for each: Sans
    /// for Arial and Helvetica (and the other sans-serif families), Serif for Times New Roman and
    /// Times, Mono for Courier New and Courier. Liberation Sans, Serif and Mono have the metrics of
    /// Arial, Times New Roman and Courier New, so text keeps its widths. Symbol, Wingdings and the
    /// other symbol, math and script families have no Liberation equivalent and are not mapped.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> LiberationFamilyAliases { get; } = BuildLiberationAliases();

    private static Dictionary<string, string[]> BuildLiberationAliases()
    {
        string[] sans = { "Liberation Sans" }, serif = { "Liberation Serif" }, mono = { "Liberation Mono" };
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { "Arial", "Arial Narrow", "Helvetica", "Segoe UI", "Calibri", "Verdana", "Tahoma", "Century Gothic", "Gill Sans MT", "Lucida Sans Unicode", "Franklin Gothic" })
            map[f] = sans;
        foreach (var f in new[] { "Times New Roman", "Times", "Georgia", "Cambria", "Palatino Linotype", "Book Antiqua", "Garamond", "Century Schoolbook", "Bookman Old Style" })
            map[f] = serif;
        foreach (var f in new[] { "Courier New", "Courier", "Consolas", "Lucida Console" })
            map[f] = mono;
        return map;
    }

    /// <summary>
    /// A catalog of faces a host describes itself (fonts it bundles and loads on demand, so that
    /// there is no folder to scan yet), with optional family aliases as in <see cref="WithFamilyAliases"/>.
    /// </summary>
    public static SystemFontCatalog FromFaces(IEnumerable<SystemFontFace> faces, IReadOnlyDictionary<string, string[]>? aliases = null) =>
        new(faces.ToArray(), aliases);

    /// <summary>A catalog without faces.</summary>
    private static readonly SystemFontCatalog None = new(Array.Empty<SystemFontFace>());

    /// <summary>The TrueType fonts (<c>.ttf</c>, <c>.ttc</c>) of a folder, read once per folder.</summary>
    public static SystemFontCatalog ForDirectory(string directory) => Catalogs.GetOrAdd(directory, d => new SystemFontCatalog(ScanFaces(d, openTypeCff: false)));

    /// <param name="openTypeCff">Also <c>.otf</c> files and faces with CFF outlines (a renderer that loads fonts itself draws them; new text embeds them as CFF).</param>
    private static List<SystemFontFace> ScanFaces(string directory, bool openTypeCff)
    {
        var faces = new List<SystemFontFace>();
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            var paths = Directory.EnumerateFiles(directory)
                .Where(p => System.IO.Path.GetExtension(p).ToLowerInvariant() is ".ttf" or ".ttc" || openTypeCff && System.IO.Path.GetExtension(p).Equals(".otf", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // Names only: the renderer scans this on its first non-embedded font, which the first page
            // waits for. Files are read in parallel (opening each one is most of the cost) and the
            // faces kept in directory order, so matching does not depend on the scheduling.
            var perFile = new List<SystemFontFace>?[paths.Length];
            System.Threading.Tasks.Parallel.For(0, paths.Length, i =>
            {
                string path = paths[i];
                var found = perFile[i] = new List<SystemFontFace>();
                try
                {
                    if (new FileInfo(path).Length > 64L * 1024 * 1024) return;
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                    int count = TrueTypeFontFile.FontCount(stream);
                    for (int f = 0; f < count; f++)
                        if (TrueTypeFontFile.ReadNames(stream, f, openTypeCff) is { } n && n.Family.Length > 0)
                            found.Add(new SystemFontFace(path, f, n.Family, n.Subfamily, n.PostScript, n.Weight, n.Italic, n.LegacyFamily, n.Cff));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            });
            foreach (var found in perFile)
                if (found != null) faces.AddRange(found);
        }
        return faces;
    }

    /// <summary>
    /// The tables read from each font file, by offset and length: the faces of a collection share
    /// them (Noto Sans CJK's SC, TC, JP and KR faces have one CFF table of about 16 MB).
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(long Offset, int Length), byte[]>> _tables = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, int), TrueTypeCmap?> _cmaps = new();

    private ConcurrentDictionary<(long Offset, int Length), byte[]> TablesOf(string path) => _tables.GetOrAdd(path, _ => new());

    /// <summary>The face as a font for new text (only the tables it uses are read, once per file); null when it cannot be loaded or embedded.</summary>
    internal TrueTypeFontFile? Load(SystemFontFace face) => _loaded.GetOrAdd((face.Path, face.Index), key =>
    {
        try
        {
            using var stream = new FileStream(key.Item1, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            var font = TrueTypeFontFile.TryLoad(stream, key.Item2, openTypeCff: true, TablesOf(key.Item1));
            return font is { EmbeddingAllowed: true } ? font : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    });

    /// <summary>
    /// Whether the face may have a glyph for <paramref name="codePoint"/>: false when its character
    /// map (read alone, without loading the face) has none, so a walk through the fallbacks for a
    /// character no font has does not load every face.
    /// </summary>
    internal bool MayHave(SystemFontFace face, int codePoint)
    {
        if (_loaded.TryGetValue((face.Path, face.Index), out var font)) return font != null && font.GlyphFor(codePoint) != 0;
        var cmap = _cmaps.GetOrAdd((face.Path, face.Index), key =>
        {
            try
            {
                using var stream = new FileStream(key.Item1, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                return TrueTypeFontFile.ReadCharacterMap(stream, key.Item2, openTypeCff: true, TablesOf(key.Item1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        });
        return cmap != null && TrueTypeFontFile.Maps(cmap, codePoint);
    }

    /// <summary>The face of <paramref name="family"/> nearest to the weight and slant asked for.</summary>
    public SystemFontFace? FindFamily(string family, bool bold, bool italic) => FindFamily(family, bold ? 700 : 400, italic);

    /// <summary>The face of <paramref name="family"/> nearest to <paramref name="weight"/> (100–900) and the slant asked for.</summary>
    public SystemFontFace? FindFamily(string family, int weight, bool italic) =>
        FindOwnFamily(family, weight, italic) ?? FindAliasedFamily(family, weight, italic);

    /// <summary>
    /// The face that stands in for <paramref name="family"/> by this catalog's family aliases (Android:
    /// Roboto for Arial), nearest to the weight and slant asked for; null when the catalog has no alias
    /// for the family or none of its stand-ins is there.
    /// </summary>
    public SystemFontFace? FindAliasedFamily(string family, int weight, bool italic) => FindAliasedFamily(family, weight, italic, trueTypeOnly: false);

    private SystemFontFace? FindAliasedFamily(string family, int weight, bool italic, bool trueTypeOnly)
    {
        if (_aliases == null || !_aliases.TryGetValue(Squash(family), out var standIns)) return null;
        foreach (var standIn in standIns)
            if (FindOwnFamily(standIn, weight, italic, trueTypeOnly) is { } face) return face;
        return null;
    }

    /// <param name="trueTypeOnly">Leave out faces with CFF outlines.</param>
    private SystemFontFace? FindOwnFamily(string family, int weight, bool italic, bool trueTypeOnly = false)
    {
        // Either name finds the family: "Arial Narrow" is a family of its own to older software but
        // only a width of "Arial" typographically, and "Segoe UI" has its Semibold only typographically.
        // A face known by the legacy name wins a tie, so "Arial" bold is Arial Bold, not Arial Narrow Bold.
        if (!_byFamily.Value.TryGetValue(Squash(family), out var candidates)) return null;
        SystemFontFace? best = null;
        int bestScore = int.MaxValue;
        foreach (var (face, legacy) in candidates)
        {
            if (trueTypeOnly && face.OpenTypeCff) continue;
            // A TrueType face wins a tie with a CFF one.
            int score = ((face.Italic == italic ? 0 : 10000) + Math.Abs(face.Weight - weight) * 2 + (legacy ? 0 : 1)) * 2 + (face.OpenTypeCff ? 1 : 0);
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

            // "MS-Gothic", "MS-PMincho,Bold": the '-' joins the words of the family, not the family and its style.
            if (JoinedFamily(name) is { } joined && FindFamily(joined, weight, italic) is { } byWords) return byWords;

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

    /// <summary>
    /// Faces tried, in order, for characters the chosen font does not have. TrueType faces come
    /// first (for each family, a TrueType stand-in before a CFF one: "Droid Sans Fallback" before
    /// "Noto Sans CJK"); faces with CFF outlines follow, for the characters only they have.
    /// </summary>
    public IEnumerable<SystemFontFace> Fallbacks(bool bold, bool italic)
    {
        int weight = bold ? 700 : 400;
        var cff = new List<SystemFontFace>();
        foreach (var family in new[] { "Segoe UI", "Arial", "Segoe UI Symbol", "Nirmala UI", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic",
                     "Microsoft JhengHei", "Ebrima", "Gadugi", "Leelawadee UI", "Segoe UI Historic", "Cambria Math", "Segoe UI Emoji" })
        {
            if ((FindOwnFamily(family, weight, italic, trueTypeOnly: true) ?? FindAliasedFamily(family, weight, italic, trueTypeOnly: true)) is { } f) yield return f;
            if (FindFamily(family, weight, italic) is { OpenTypeCff: true } c) cff.Add(c);
        }
        foreach (var c in cff.Distinct()) yield return c;
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

    /// <summary>
    /// The family of a PostScript-style name whose '-' joins the words of the family ("MS-Gothic" is
    /// MS Gothic): the words up to the first that is only style ("MS-PGothic-Bold" is MS PGothic).
    /// Null when the part after the first '-' is only style ("Arial-BoldMT"), so the family is before it.
    /// </summary>
    private static string? JoinedFamily(string name)
    {
        int comma = name.IndexOf(',');
        var words = (comma >= 0 ? name[..comma] : name).Split('-');
        int n = 1;
        while (n < words.Length && !IsStyleOnly(words[n])) n++;
        return n > 1 && words[0].Length > 0 ? string.Join(" ", words[..n]) : null;
    }

    /// <summary>Whether a name part is style words only, with an MT/PS suffix ("BoldItalicMT", "Light", "Roman").</summary>
    private static bool IsStyleOnly(string part)
    {
        part = StripSuffix(part);
        while (part.Length > 0)
        {
            var word = StyleWords.FirstOrDefault(w => part.EndsWith(w, StringComparison.OrdinalIgnoreCase));
            if (word == null) return false;
            part = part[..^word.Length];
        }
        return true;
    }

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
