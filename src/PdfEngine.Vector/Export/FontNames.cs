using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>
/// The family name Word should use for a PDF font, and the style its name carries:
/// ABCDEF+Calibri-Bold is Calibri, bold; TimesNewRomanPS-ItalicMT is Times New Roman, italic.
/// </summary>
internal static class FontNames
{
    // Names whose family cannot be read off the PostScript name, and the base 14 fonts, which
    // Windows has under other names with the same metrics.
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial", ["HelveticaNeue"] = "Arial", ["Arial"] = "Arial", ["ArialMT"] = "Arial",
        ["ArialNarrow"] = "Arial Narrow", ["ArialUnicodeMS"] = "Arial Unicode MS", ["ArialBlack"] = "Arial Black",
        ["Times"] = "Times New Roman", ["TimesNewRoman"] = "Times New Roman", ["TimesNewRomanPS"] = "Times New Roman",
        ["Courier"] = "Courier New", ["CourierNew"] = "Courier New", ["CourierNewPS"] = "Courier New",
        ["Symbol"] = "Symbol", ["ZapfDingbats"] = "Wingdings", ["Wingdings"] = "Wingdings",
        ["MicrosoftYaHei"] = "Microsoft YaHei", ["MicrosoftJhengHei"] = "Microsoft JhengHei", ["MSMincho"] = "MS Mincho",
        ["MSGothic"] = "MS Gothic", ["MSPMincho"] = "MS PMincho", ["MSPGothic"] = "MS PGothic", ["MSUIGothic"] = "MS UI Gothic",
        ["SimSun"] = "SimSun", ["NSimSun"] = "NSimSun", ["SimHei"] = "SimHei", ["KaiTi"] = "KaiTi", ["FangSong"] = "FangSong",
        ["MingLiU"] = "MingLiU", ["PMingLiU"] = "PMingLiU", ["Batang"] = "Batang", ["Gulim"] = "Gulim", ["Dotum"] = "Dotum",
        ["MalgunGothic"] = "Malgun Gothic", ["YuGothic"] = "Yu Gothic", ["YuMincho"] = "Yu Mincho", ["Meiryo"] = "Meiryo",
        ["PalatinoLinotype"] = "Palatino Linotype", ["Palatino"] = "Palatino Linotype", ["BookAntiqua"] = "Book Antiqua",
        ["CenturyGothic"] = "Century Gothic", ["CenturySchoolbook"] = "Century Schoolbook", ["LucidaConsole"] = "Lucida Console",
        ["LucidaSansUnicode"] = "Lucida Sans Unicode", ["FranklinGothic"] = "Franklin Gothic Medium", ["CambriaMath"] = "Cambria Math",
        ["SegoeUI"] = "Segoe UI", ["TrebuchetMS"] = "Trebuchet MS", ["ComicSansMS"] = "Comic Sans MS",
        ["NewCenturySchlbk"] = "Century Schoolbook", ["Bookman"] = "Bookman Old Style", ["AvantGarde"] = "Century Gothic",
    };

    // Words a PostScript or font name uses for its style, not its family.
    private static readonly string[] StyleWords =
    {
        "BoldItalic", "BoldOblique", "SemiboldItalic", "Semibold", "SemiBold", "Demibold", "DemiBold", "Demi", "ExtraBold", "UltraBold",
        "Bold", "Black", "Heavy", "Italic", "Oblique", "Regular", "Roman", "Book", "Medium", "Normal", "Plain", "Light", "Thin",
        "Condensed",
    };

    /// <summary>The Word family, and whether the name or font says bold and italic.</summary>
    public static (string Family, bool Bold, bool Italic) Resolve(string? postScriptName, bool fontBold, bool fontItalic)
    {
        string name = Decode(SystemFontCatalog.StripSubset(postScriptName ?? string.Empty)).Trim();
        if (name.Length == 0) return ("Arial", fontBold, fontItalic);
        bool bold = fontBold || Has(name, "Bold") || Has(name, "Black") || Has(name, "Heavy") || Has(name, "Semibold") || Has(name, "Demi");
        bool italic = fontItalic || Has(name, "Italic") || Has(name, "Oblique") || name.EndsWith("-It", StringComparison.Ordinal) || name.EndsWith("-BdIt", StringComparison.Ordinal);

        // Family: before the style part (after '-' or ','), without PS suffixes and style words.
        string family = name;
        int cut = family.IndexOfAny(new[] { '-', ',' });
        if (cut > 0) family = family[..cut];
        family = family.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (Known.TryGetValue(family, out var known)) return (known, bold, italic);
        foreach (string suffix in new[] { "PSMT", "PS", "MT" })
            if (family.Length > suffix.Length + 2 && family.EndsWith(suffix, StringComparison.Ordinal) && Known.TryGetValue(family[..^suffix.Length], out known))
                return (known, bold, italic);
        if (family.Length > 6 && family.EndsWith("PSMT", StringComparison.Ordinal)) family = family[..^4];
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string w in StyleWords)
                if (family.Length > w.Length + 2 && family.EndsWith(w, StringComparison.Ordinal))
                {
                    family = family[..^w.Length];
                    changed = true;
                    break;
                }
        }
        if (Known.TryGetValue(family, out known)) return (known, bold, italic);
        if (name.Contains(' ')) return (StripStyle(name), bold, italic); // already a family name, "Book Antiqua Bold"
        return (SplitWords(family), bold, italic);
    }

    private static readonly string[] SerifWords =
    {
        "Times", "Roman", "Serif", "Garamond", "Georgia", "Minion", "Melior", "Book", "Century", "Palatino", "Bodoni", "Caslon",
        "Cambria", "Baskerville", "Utopia", "Janson", "Sabon", "Bembo", "Mincho", "SimSun", "Song", "Batang", "Myungjo", "Constantia",
    };

    /// <summary>The name says the font has serifs (a sans-serif face of the family aside).</summary>
    public static bool LooksSerif(string? name) =>
        name != null && !Has(name, "Sans") && !Has(name, "Gothic") && SerifWords.Any(w => Has(name, w));

    private static bool Has(string name, string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string StripStyle(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && StyleWords.Contains(words[^1], StringComparer.OrdinalIgnoreCase)) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>CamelCase to words: TimesNewRoman is Times New Roman, SegoeUI is Segoe UI.</summary>
    private static string SplitWords(string s)
    {
        if (s.Length == 0 || s.Any(c => c > 0x7F)) return s;
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Names written in a legacy encoding (#CB#CE#CC#E5 is SimSun in GBK) read back as their
    /// characters; names in plain ASCII are unchanged.
    /// </summary>
    private static string Decode(string name)
    {
        if (name.All(c => c < 0x80) || name.Any(c => c > 0xFF)) return name;
        var bytes = name.Select(c => (byte)c).ToArray();
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { }
        try
        {
            var gbk = CodePagesEncodingProvider.Instance.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (gbk != null) return gbk.GetString(bytes);
        }
        catch (DecoderFallbackException) { }
        return name;
    }
}
