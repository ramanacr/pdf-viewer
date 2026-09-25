using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Subset of the Adobe Glyph List (AGL) covering every glyph name used by the PDF simple-font
/// encodings (ISO 32000-2 Annex D: Standard, WinAnsi, MacRoman, Symbol), Latin-1, Latin Extended-A
/// and the common f-ligatures, plus the AGL specification algorithm for <c>uniXXXX</c>,
/// <c>uXXXX[XX]</c>, suffixes (<c>a.sc</c>) and underscore ligatures (<c>f_i</c>).
/// </summary>
internal static class GlyphList
{
    private static readonly Dictionary<string, int> NameToCodePoint = BuildTable();

    /// <summary>
    /// Maps a glyph name to its Unicode string, or <see langword="null"/> when the name is unknown.
    /// Never throws.
    /// </summary>
    public static string? ToUnicode(string? glyphName)
    {
        if (string.IsNullOrEmpty(glyphName) || glyphName.Length > 256)
            return null;

        // AGL spec: drop everything after the first period (a.sc, one.oldstyle).
        int dot = glyphName.IndexOf('.');
        string baseName = dot >= 0 ? glyphName[..dot] : glyphName;
        if (baseName.Length == 0)
            return null;

        // Ligature components separated by underscores: f_f_i -> "ffi".
        if (baseName.IndexOf('_') >= 0)
        {
            var sb = new StringBuilder();
            foreach (string part in baseName.Split('_'))
            {
                string? mapped = MapComponent(part);
                if (mapped == null)
                    return null;
                sb.Append(mapped);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        return MapComponent(baseName);
    }

    /// <summary>Returns true when the name is a known glyph name (table entry or algorithmic form).</summary>
    public static bool IsKnown(string? glyphName) => ToUnicode(glyphName) != null;

    /// <summary>All table glyph names (used by the Standard 14 metric generator).</summary>
    internal static IEnumerable<string> TableNames => NameToCodePoint.Keys;

    private static string? MapComponent(string name)
    {
        if (name.Length == 0)
            return null;

        if (NameToCodePoint.TryGetValue(name, out int cp))
            return char.ConvertFromUtf32(cp);

        // uniXXXX[XXXX...] : sequence of 4-hex-digit BMP code units (no surrogates).
        if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && (name.Length - 3) % 4 == 0)
        {
            var sb = new StringBuilder();
            for (int i = 3; i < name.Length; i += 4)
            {
                if (!TryParseHex(name.AsSpan(i, 4), out int unit) || (unit >= 0xD800 && unit <= 0xDFFF))
                    return null;
                sb.Append((char)unit);
            }
            return sb.ToString();
        }

        // uXXXX .. uXXXXXX : a single scalar value.
        if (name.Length >= 5 && name.Length <= 7 && name[0] == 'u')
        {
            if (TryParseHex(name.AsSpan(1), out int scalar) &&
                scalar <= 0x10FFFF && !(scalar >= 0xD800 && scalar <= 0xDFFF))
            {
                return char.ConvertFromUtf32(scalar);
            }
        }

        return null;
    }

    private static bool TryParseHex(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        if (s.Length == 0 || s.Length > 6)
            return false;
        foreach (char c in s)
        {
            // AGL requires uppercase hex digits; accept lowercase leniently.
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1
            };
            if (d < 0)
                return false;
            value = (value << 4) | d;
        }
        return true;
    }

    private static Dictionary<string, int> BuildTable()
    {
        var t = new Dictionary<string, int>(StringComparer.Ordinal);

        // ASCII
        string[] ascii =
        {
            "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle",
            "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "colon", "semicolon", "less", "equal", "greater", "question", "at",
        };
        for (int i = 0; i < ascii.Length; i++)
            t[ascii[i]] = 0x20 + i;
        for (int c = 'A'; c <= 'Z'; c++)
            t[((char)c).ToString()] = c;
        for (int c = 'a'; c <= 'z'; c++)
            t[((char)c).ToString()] = c;
        t["bracketleft"] = 0x5B; t["backslash"] = 0x5C; t["bracketright"] = 0x5D;
        t["asciicircum"] = 0x5E; t["underscore"] = 0x5F; t["grave"] = 0x60;
        t["braceleft"] = 0x7B; t["bar"] = 0x7C; t["braceright"] = 0x7D; t["asciitilde"] = 0x7E;

        // Latin-1 supplement U+00A1..U+00FF
        string[] latin1 =
        {
            "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section", "dieresis",
            "copyright", "ordfeminine", "guillemotleft", "logicalnot", "sfthyphen", "registered", "macron", "degree",
            "plusminus", "twosuperior", "threesuperior", "acute", "mu", "paragraph", "periodcentered", "cedilla",
            "onesuperior", "ordmasculine", "guillemotright", "onequarter", "onehalf", "threequarters", "questiondown",
            "Agrave", "Aacute", "Acircumflex", "Atilde", "Adieresis", "Aring", "AE", "Ccedilla",
            "Egrave", "Eacute", "Ecircumflex", "Edieresis", "Igrave", "Iacute", "Icircumflex", "Idieresis",
            "Eth", "Ntilde", "Ograve", "Oacute", "Ocircumflex", "Otilde", "Odieresis", "multiply",
            "Oslash", "Ugrave", "Uacute", "Ucircumflex", "Udieresis", "Yacute", "Thorn", "germandbls",
            "agrave", "aacute", "acircumflex", "atilde", "adieresis", "aring", "ae", "ccedilla",
            "egrave", "eacute", "ecircumflex", "edieresis", "igrave", "iacute", "icircumflex", "idieresis",
            "eth", "ntilde", "ograve", "oacute", "ocircumflex", "otilde", "odieresis", "divide",
            "oslash", "ugrave", "uacute", "ucircumflex", "udieresis", "yacute", "thorn", "ydieresis",
        };
        for (int i = 0; i < latin1.Length; i++)
            t[latin1[i]] = 0xA1 + i;
        t["nbspace"] = 0xA0;
        t["nonbreakingspace"] = 0xA0;

        // Latin Extended-A U+0100..U+017F
        string[] latinExtA =
        {
            "Amacron", "amacron", "Abreve", "abreve", "Aogonek", "aogonek", "Cacute", "cacute",
            "Ccircumflex", "ccircumflex", "Cdotaccent", "cdotaccent", "Ccaron", "ccaron", "Dcaron", "dcaron",
            "Dcroat", "dcroat", "Emacron", "emacron", "Ebreve", "ebreve", "Edotaccent", "edotaccent",
            "Eogonek", "eogonek", "Ecaron", "ecaron", "Gcircumflex", "gcircumflex", "Gbreve", "gbreve",
            "Gdotaccent", "gdotaccent", "Gcommaaccent", "gcommaaccent", "Hcircumflex", "hcircumflex", "Hbar", "hbar",
            "Itilde", "itilde", "Imacron", "imacron", "Ibreve", "ibreve", "Iogonek", "iogonek",
            "Idotaccent", "dotlessi", "IJ", "ij", "Jcircumflex", "jcircumflex", "Kcommaaccent", "kcommaaccent",
            "kgreenlandic", "Lacute", "lacute", "Lcommaaccent", "lcommaaccent", "Lcaron", "lcaron", "Ldot",
            "ldot", "Lslash", "lslash", "Nacute", "nacute", "Ncommaaccent", "ncommaaccent", "Ncaron",
            "ncaron", "napostrophe", "Eng", "eng", "Omacron", "omacron", "Obreve", "obreve",
            "Ohungarumlaut", "ohungarumlaut", "OE", "oe", "Racute", "racute", "Rcommaaccent", "rcommaaccent",
            "Rcaron", "rcaron", "Sacute", "sacute", "Scircumflex", "scircumflex", "Scedilla", "scedilla",
            "Scaron", "scaron", "Tcommaaccent", "tcommaaccent", "Tcaron", "tcaron", "Tbar", "tbar",
            "Utilde", "utilde", "Umacron", "umacron", "Ubreve", "ubreve", "Uring", "uring",
            "Uhungarumlaut", "uhungarumlaut", "Uogonek", "uogonek", "Wcircumflex", "wcircumflex", "Ycircumflex", "ycircumflex",
            "Ydieresis", "Zacute", "zacute", "Zdotaccent", "zdotaccent", "Zcaron", "zcaron", "longs",
        };
        for (int i = 0; i < latinExtA.Length; i++)
            t[latinExtA[i]] = 0x100 + i;
        t["Gcedilla"] = 0x122; t["gcedilla"] = 0x123; t["Kcedilla"] = 0x136; t["kcedilla"] = 0x137;
        t["Lcedilla"] = 0x13B; t["lcedilla"] = 0x13C; t["Ncedilla"] = 0x145; t["ncedilla"] = 0x146;
        t["Rcedilla"] = 0x156; t["rcedilla"] = 0x157; t["Tcedilla"] = 0x162; t["tcedilla"] = 0x163;
        t["Dslash"] = 0x110; t["dslash"] = 0x111; t["Edot"] = 0x116; t["edot"] = 0x117;
        t["Gdot"] = 0x120; t["gdot"] = 0x121; t["Idot"] = 0x130; t["Ldotaccent"] = 0x13F; t["ldotaccent"] = 0x140;
        t["Odblacute"] = 0x150; t["odblacute"] = 0x151; t["Udblacute"] = 0x170; t["udblacute"] = 0x171;
        t["Zdot"] = 0x17B; t["zdot"] = 0x17C; t["slong"] = 0x17F; t["kra"] = 0x138; t["nquoteright"] = 0x149;

        // Other Latin text glyphs
        t["florin"] = 0x192; t["Scommaaccent"] = 0x218; t["scommaaccent"] = 0x219;
        t["Tcommabelow"] = 0x21A; t["tcommabelow"] = 0x21B; t["dotlessj"] = 0x237; t["uni0237"] = 0x237;
        t["circumflex"] = 0x2C6; t["caron"] = 0x2C7; t["breve"] = 0x2D8; t["dotaccent"] = 0x2D9;
        t["ring"] = 0x2DA; t["ogonek"] = 0x2DB; t["tilde"] = 0x2DC; t["hungarumlaut"] = 0x2DD;
        t["commaaccent"] = 0xF6C3; t["gravecomb"] = 0x300; t["acutecomb"] = 0x301; t["tildecomb"] = 0x303;
        t["endash"] = 0x2013; t["emdash"] = 0x2014; t["quoteleft"] = 0x2018; t["quoteright"] = 0x2019;
        t["quotesinglbase"] = 0x201A; t["quotereversed"] = 0x201B; t["quotedblleft"] = 0x201C; t["quotedblright"] = 0x201D;
        t["quotedblbase"] = 0x201E; t["dagger"] = 0x2020; t["daggerdbl"] = 0x2021; t["bullet"] = 0x2022;
        t["onedotenleader"] = 0x2024; t["twodotenleader"] = 0x2025; t["ellipsis"] = 0x2026; t["perthousand"] = 0x2030;
        t["guilsinglleft"] = 0x2039; t["guilsinglright"] = 0x203A; t["fraction"] = 0x2044; t["Euro"] = 0x20AC;
        t["trademark"] = 0x2122; t["minus"] = 0x2212; t["figuredash"] = 0x2012; t["afii00208"] = 0x2015;
        t["ff"] = 0xFB00; t["fi"] = 0xFB01; t["fl"] = 0xFB02; t["ffi"] = 0xFB03; t["ffl"] = 0xFB04;
        t["apple"] = 0xF8FF; t["onethird"] = 0x2153; t["twothirds"] = 0x2154; t["oneeighth"] = 0x215B;
        t["threeeighths"] = 0x215C; t["fiveeighths"] = 0x215D; t["seveneighths"] = 0x215E;
        t["Delta"] = 0x2206; t["Omega"] = 0x2126; t["mu"] = 0xB5; t["estimated"] = 0x212E;
        t["zerosuperior"] = 0x2070; t["foursuperior"] = 0x2074; t["zeroinferior"] = 0x2080;

        // Symbol font glyph names (built-in encoding of the standard Symbol font)
        (string, int)[] symbol =
        {
            ("universal", 0x2200), ("existential", 0x2203), ("suchthat", 0x220B), ("asteriskmath", 0x2217),
            ("congruent", 0x2245), ("Alpha", 0x391), ("Beta", 0x392), ("Chi", 0x3A7), ("Epsilon", 0x395),
            ("Phi", 0x3A6), ("Gamma", 0x393), ("Eta", 0x397), ("Iota", 0x399), ("theta1", 0x3D1),
            ("Kappa", 0x39A), ("Lambda", 0x39B), ("Mu", 0x39C), ("Nu", 0x39D), ("Omicron", 0x39F),
            ("Pi", 0x3A0), ("Theta", 0x398), ("Rho", 0x3A1), ("Sigma", 0x3A3), ("Tau", 0x3A4),
            ("Upsilon", 0x3A5), ("sigma1", 0x3C2), ("Xi", 0x39E), ("Psi", 0x3A8), ("Zeta", 0x396),
            ("therefore", 0x2234), ("perpendicular", 0x22A5), ("radicalex", 0x203E), ("alpha", 0x3B1),
            ("beta", 0x3B2), ("chi", 0x3C7), ("delta", 0x3B4), ("epsilon", 0x3B5), ("phi", 0x3C6),
            ("gamma", 0x3B3), ("eta", 0x3B7), ("iota", 0x3B9), ("phi1", 0x3D5), ("kappa", 0x3BA),
            ("lambda", 0x3BB), ("nu", 0x3BD), ("omicron", 0x3BF), ("pi", 0x3C0), ("theta", 0x3B8),
            ("rho", 0x3C1), ("sigma", 0x3C3), ("tau", 0x3C4), ("upsilon", 0x3C5), ("omega1", 0x3D6),
            ("omega", 0x3C9), ("xi", 0x3BE), ("psi", 0x3C8), ("zeta", 0x3B6), ("similar", 0x223C),
            ("Upsilon1", 0x3D2), ("minute", 0x2032), ("lessequal", 0x2264), ("infinity", 0x221E),
            ("club", 0x2663), ("diamond", 0x2666), ("heart", 0x2665), ("spade", 0x2660),
            ("arrowboth", 0x2194), ("arrowleft", 0x2190), ("arrowup", 0x2191), ("arrowright", 0x2192),
            ("arrowdown", 0x2193), ("second", 0x2033), ("greaterequal", 0x2265), ("proportional", 0x221D),
            ("partialdiff", 0x2202), ("notequal", 0x2260), ("equivalence", 0x2261), ("approxequal", 0x2248),
            ("arrowvertex", 0x23D0), ("arrowhorizex", 0x23AF), ("carriagereturn", 0x21B5), ("aleph", 0x2135),
            ("Ifraktur", 0x2111), ("Rfraktur", 0x211C), ("weierstrass", 0x2118), ("circlemultiply", 0x2297),
            ("circleplus", 0x2295), ("emptyset", 0x2205), ("intersection", 0x2229), ("union", 0x222A),
            ("propersuperset", 0x2283), ("reflexsuperset", 0x2287), ("notsubset", 0x2284), ("propersubset", 0x2282),
            ("reflexsubset", 0x2286), ("element", 0x2208), ("notelement", 0x2209), ("angle", 0x2220),
            ("gradient", 0x2207), ("registerserif", 0xAE), ("copyrightserif", 0xA9), ("trademarkserif", 0x2122),
            ("product", 0x220F), ("radical", 0x221A), ("dotmath", 0x22C5), ("logicaland", 0x2227),
            ("logicalor", 0x2228), ("arrowdblboth", 0x21D4), ("arrowdblleft", 0x21D0), ("arrowdblup", 0x21D1),
            ("arrowdblright", 0x21D2), ("arrowdbldown", 0x21D3), ("lozenge", 0x25CA), ("angleleft", 0x2329),
            ("registersans", 0xAE), ("copyrightsans", 0xA9), ("trademarksans", 0x2122), ("summation", 0x2211),
            ("parenlefttp", 0x239B), ("parenleftex", 0x239C), ("parenleftbt", 0x239D), ("bracketlefttp", 0x23A1),
            ("bracketleftex", 0x23A2), ("bracketleftbt", 0x23A3), ("bracelefttp", 0x23A7), ("braceleftmid", 0x23A8),
            ("braceleftbt", 0x23A9), ("braceex", 0x23AA), ("angleright", 0x232A), ("integral", 0x222B),
            ("integraltp", 0x2320), ("integralex", 0x23AE), ("integralbt", 0x2321), ("parenrighttp", 0x239E),
            ("parenrightex", 0x239F), ("parenrightbt", 0x23A0), ("bracketrighttp", 0x23A4), ("bracketrightex", 0x23A5),
            ("bracketrightbt", 0x23A6), ("bracerighttp", 0x23AB), ("bracerightmid", 0x23AC), ("bracerightbt", 0x23AD),
        };
        foreach (var (name, cp) in symbol)
            t[name] = cp;

        return t;
    }
}
