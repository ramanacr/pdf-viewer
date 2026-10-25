using System;
using System.Collections.Generic;
using System.Linq;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Which face draws a character that a run's substitute lacks: the Windows families for its script
/// (Arabic and Hebrew in Segoe UI, Indic in Nirmala UI, Han in the face of the text's CJK language,
/// symbols in Segoe UI Symbol), and the CJK language of a run. The Direct2D backend picks installed
/// faces by these rules; the web demo's bundled faces use <see cref="CjkLanguage"/> too.
/// </summary>
public static class CharacterFaces
{
    /// <summary>The CJK languages in the order they are tried when the run's own has no face for a character.</summary>
    public static IReadOnlyList<string> CjkLanguages { get; } = new[] { "sc", "tc", "jp", "kr" };

    /// <summary>
    /// The CJK language of a run ("sc", "tc", "jp" or "kr"): the font's character collection when it
    /// has one, else kana (Japanese) or hangul (Korean) in the text, else the font's name (MS Mincho,
    /// SimSun, MingLiU, Batang …); Simplified Chinese when nothing tells.
    /// </summary>
    public static string CjkLanguage(string? cidOrdering, string? fontName, string? text)
    {
        switch (cidOrdering)
        {
            case "Japan1": return "jp";
            case "GB1": return "sc";
            case "CNS1": return "tc";
            case "Korea1": return "kr";
        }
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            int c = rune.Value;
            if (c is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) return "jp";
            if (c is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) return "kr";
        }
        string name = new string((fontName ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (name.Length > 0)
        {
            if (ContainsAny(name, KoreanNames)) return "kr";
            if (ContainsAny(name, TraditionalChineseNames)) return "tc";
            if (ContainsAny(name, SimplifiedChineseNames)) return "sc";
            if (ContainsAny(name, JapaneseNames)) return "jp";
        }
        return "sc";
    }

    /// <summary>True for a character of a CJK block (Han, kana, hangul, bopomofo, CJK symbols and full-width forms).</summary>
    public static bool IsCjk(int c) =>
        c is >= 0x1100 and <= 0x11FF or >= 0x2E80 and <= 0x2FDF or >= 0x3000 and <= 0x31FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF;

    /// <summary>
    /// The Windows families that draw <paramref name="codePoint"/>, best first, by its script; empty
    /// for a script without one (the caller then asks the system's font fallback). Han takes the face
    /// of <paramref name="cjkLanguage"/> first (<see cref="CjkLanguage"/>), then those of the other languages.
    /// </summary>
    public static IReadOnlyList<string> WindowsFamilies(int codePoint, string cjkLanguage)
    {
        int c = codePoint;
        if (c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF)
            return Arabic;
        if (c is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F)
            return Hebrew;
        if (c is >= 0x0900 and <= 0x0DFF or >= 0xA8E0 and <= 0xA8FF or >= 0x1CD0 and <= 0x1CFF)
            return Indic;
        if (IsCjk(c))
            return CjkFamilies.TryGetValue(cjkLanguage, out var families) ? families : CjkFamilies["sc"];
        if (c is >= 0x2000 and <= 0x2BFF or >= 0x1F000 and <= 0x1FAFF)
            return Symbols;
        return Array.Empty<string>();
    }

    private static readonly string[] Arabic = { "Segoe UI", "Arial" };
    private static readonly string[] Hebrew = { "Segoe UI", "Arial" };
    private static readonly string[] Indic = { "Nirmala UI" };
    private static readonly string[] Symbols = { "Segoe UI Symbol", "Segoe UI Emoji" };

    private static readonly Dictionary<string, string> CjkFamily = new()
    {
        ["sc"] = "Microsoft YaHei", ["tc"] = "Microsoft JhengHei", ["jp"] = "Yu Gothic", ["kr"] = "Malgun Gothic",
    };

    // Each language's face first, then the others in the order of CjkLanguages.
    private static readonly Dictionary<string, string[]> CjkFamilies = CjkLanguages.ToDictionary(
        language => language,
        language => CjkLanguages.Where(l => l != language).Prepend(language).Select(l => CjkFamily[l]).ToArray());

    private static bool ContainsAny(string name, string[] parts) => parts.Any(name.Contains);

    // Squashed, lower-case parts of common font names. Korean first: "Malgun Gothic" is not Japanese.
    private static readonly string[] KoreanNames =
        { "batang", "gulim", "dotum", "gungsuh", "malgun", "applegothic", "applemyungjo", "hygothic", "hymyeongjo", "hysmyeongjo", "nanum", "myeongjo", "unbatang", "undotum" };

    private static readonly string[] TraditionalChineseNames =
        { "mingliu", "pmingliu", "mingli", "msung", "mhei", "dfkai", "kaiu", "jhenghei", "heititc", "songtitc", "big5", "adobemingstd", "adobefanheitistd" };

    private static readonly string[] SimplifiedChineseNames =
        { "simsun", "simhei", "simkai", "simfang", "nsimsun", "stsong", "stheiti", "stkaiti", "stfangsong", "stxihei", "kaiti", "fangsong", "yahei", "songti", "dengxian", "gb2312", "gbk", "adobesongstd", "adobeheitistd", "adobekaitistd", "adobefangsongstd" };

    private static readonly string[] JapaneseNames =
        { "mincho", "gothic", "meiryo", "hiragino", "kozuka", "ryumin", "yugoth", "yumin", "heiseimin", "heiseikaku", "ipaex", "ipamincho", "ipagothic", "osaka", "kozmin", "kozgo" };
}
