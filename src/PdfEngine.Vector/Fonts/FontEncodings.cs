using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Simple-font base encodings from ISO 32000-2 Annex D, expressed as code → glyph name tables
/// (<see langword="null"/> = undefined code).
/// </summary>
internal static class FontEncodings
{
    /// <summary>Adobe StandardEncoding (Annex D.2, STD column).</summary>
    public static readonly string?[] Standard = null!;

    /// <summary>WinAnsiEncoding (Annex D.2, WIN column).</summary>
    public static readonly string?[] WinAnsi = null!;

    /// <summary>
    /// MacRomanEncoding (Annex D.2, MAC column), extended with the Mac OS Roman math glyphs
    /// (notequal, infinity, …) so that text extraction of such fonts is not lossy.
    /// </summary>
    public static readonly string?[] MacRoman = null!;

    /// <summary>Built-in encoding of the standard Symbol font (Annex D.5).</summary>
    public static readonly string?[] Symbol = null!;

    /// <summary>
    /// Built-in encoding of the standard ZapfDingbats font (Annex D.6), with glyphs expressed as
    /// <c>uniXXXX</c> names of their Unicode Dingbats equivalents (approximation, see
    /// <see cref="Standard14Fonts"/> remarks).
    /// </summary>
    public static readonly string?[] ZapfDingbats = null!;

    // Tables are built in the static constructor so that the helper arrays below are initialized first.
    static FontEncodings()
    {
        Standard = BuildStandard();
        WinAnsi = BuildWinAnsi();
        MacRoman = BuildMacRoman();
        Symbol = BuildSymbol();
        ZapfDingbats = BuildZapfDingbats();
    }

    private static readonly Lazy<Dictionary<string, int>> MacRomanReverse = new(() => BuildReverse(MacRoman));

    /// <summary>Returns the base encoding table for a PDF encoding name, or null when unknown.</summary>
    public static string?[]? ForName(string? encodingName) => encodingName switch
    {
        "StandardEncoding" => Standard,
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        // MacExpertEncoding holds expert (small caps/oldstyle) glyphs only; treated as unknown.
        _ => null
    };

    /// <summary>Reverse MacRoman lookup (glyph name → code), used for (1,0) TrueType cmaps.</summary>
    public static bool TryGetMacRomanCode(string glyphName, out int code) =>
        MacRomanReverse.Value.TryGetValue(glyphName, out code);

    private static Dictionary<string, int> BuildReverse(string?[] table)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i] is { } n && !map.ContainsKey(n))
                map[n] = i;
        }
        return map;
    }

    private static readonly string[] AsciiNames =
    {
        // 32..126, with StandardEncoding's quoteright (39) and quoteleft (96).
        "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quoteright",
        "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "colon", "semicolon", "less", "equal", "greater", "question", "at",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S",
        "T", "U", "V", "W", "X", "Y", "Z",
        "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "quoteleft",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s",
        "t", "u", "v", "w", "x", "y", "z",
        "braceleft", "bar", "braceright", "asciitilde",
    };

    private static string?[] AsciiBase(bool standardQuotes)
    {
        var t = new string?[256];
        for (int i = 0; i < AsciiNames.Length; i++)
            t[32 + i] = AsciiNames[i];
        if (!standardQuotes)
        {
            t[39] = "quotesingle";
            t[96] = "grave";
        }
        return t;
    }

    private static void Put(string?[] t, int start, params string?[] names)
    {
        for (int i = 0; i < names.Length; i++)
            t[start + i] = names[i];
    }

    private static string?[] BuildStandard()
    {
        var t = AsciiBase(standardQuotes: true);
        Put(t, 161, "exclamdown", "cent", "sterling", "fraction", "yen", "florin", "section", "currency",
            "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft", "guilsinglright", "fi", "fl");
        Put(t, 177, "endash", "dagger", "daggerdbl", "periodcentered");
        Put(t, 182, "paragraph", "bullet", "quotesinglbase", "quotedblbase", "quotedblright", "guillemotright",
            "ellipsis", "perthousand");
        t[191] = "questiondown";
        Put(t, 193, "grave", "acute", "circumflex", "tilde", "macron", "breve", "dotaccent", "dieresis");
        Put(t, 202, "ring", "cedilla");
        Put(t, 205, "hungarumlaut", "ogonek", "caron", "emdash");
        t[225] = "AE";
        t[227] = "ordfeminine";
        Put(t, 232, "Lslash", "Oslash", "OE", "ordmasculine");
        t[241] = "ae";
        t[245] = "dotlessi";
        Put(t, 248, "lslash", "oslash", "oe", "germandbls");
        return t;
    }

    private static readonly string[] Latin1Upper =
    {
        // 161..255 (WinAnsi); 173 is "hyphen" per Annex D.
        "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section", "dieresis",
        "copyright", "ordfeminine", "guillemotleft", "logicalnot", "hyphen", "registered", "macron", "degree",
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

    private static string?[] BuildWinAnsi()
    {
        var t = AsciiBase(standardQuotes: false);
        t[128] = "Euro";
        Put(t, 130, "quotesinglbase", "florin", "quotedblbase", "ellipsis", "dagger", "daggerdbl", "circumflex",
            "perthousand", "Scaron", "guilsinglleft", "OE");
        t[142] = "Zcaron";
        Put(t, 145, "quoteleft", "quoteright", "quotedblleft", "quotedblright", "bullet", "endash", "emdash",
            "tilde", "trademark", "scaron", "guilsinglright", "oe");
        Put(t, 158, "zcaron", "Ydieresis", "space");
        Put(t, 161, Latin1Upper);
        return t;
    }

    private static string?[] BuildMacRoman()
    {
        var t = AsciiBase(standardQuotes: false);
        Put(t, 128,
            "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute",
            "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave",
            "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute",
            "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave", "ucircumflex", "udieresis",
            "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph", "germandbls",
            "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash",
            "infinity", "plusminus", "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation",
            "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega", "ae", "oslash",
            "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal", "Delta", "guillemotleft",
            "guillemotright", "ellipsis", "space", "Agrave", "Atilde", "Otilde", "OE", "oe",
            "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge",
            "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl",
            "daggerdbl", "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute",
            "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex",
            "apple", "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex", "tilde",
            "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek", "caron");
        return t;
    }

    private static string?[] BuildSymbol()
    {
        var t = new string?[256];
        Put(t, 32,
            "space", "exclam", "universal", "numbersign", "existential", "percent", "ampersand", "suchthat",
            "parenleft", "parenright", "asteriskmath", "plus", "comma", "minus", "period", "slash",
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "colon", "semicolon", "less", "equal", "greater", "question", "congruent",
            "Alpha", "Beta", "Chi", "Delta", "Epsilon", "Phi", "Gamma", "Eta", "Iota", "theta1", "Kappa",
            "Lambda", "Mu", "Nu", "Omicron", "Pi", "Theta", "Rho", "Sigma", "Tau", "Upsilon", "sigma1",
            "Omega", "Xi", "Psi", "Zeta", "bracketleft", "therefore", "bracketright", "perpendicular",
            "underscore", "radicalex",
            "alpha", "beta", "chi", "delta", "epsilon", "phi", "gamma", "eta", "iota", "phi1", "kappa",
            "lambda", "mu", "nu", "omicron", "pi", "theta", "rho", "sigma", "tau", "upsilon", "omega1",
            "omega", "xi", "psi", "zeta", "braceleft", "bar", "braceright", "similar");
        Put(t, 160,
            "Euro", "Upsilon1", "minute", "lessequal", "fraction", "infinity", "florin", "club",
            "diamond", "heart", "spade", "arrowboth", "arrowleft", "arrowup", "arrowright", "arrowdown",
            "degree", "plusminus", "second", "greaterequal", "multiply", "proportional", "partialdiff", "bullet",
            "divide", "notequal", "equivalence", "approxequal", "ellipsis", "arrowvertex", "arrowhorizex", "carriagereturn",
            "aleph", "Ifraktur", "Rfraktur", "weierstrass", "circlemultiply", "circleplus", "emptyset", "intersection",
            "union", "propersuperset", "reflexsuperset", "notsubset", "propersubset", "reflexsubset", "element", "notelement",
            "angle", "gradient", "registerserif", "copyrightserif", "trademarkserif", "product", "radical", "dotmath",
            "logicalnot", "logicaland", "logicalor", "arrowdblboth", "arrowdblleft", "arrowdblup", "arrowdblright", "arrowdbldown",
            "lozenge", "angleleft", "registersans", "copyrightsans", "trademarksans", "summation", "parenlefttp", "parenleftex",
            "parenleftbt", "bracketlefttp", "bracketleftex", "bracketleftbt", "bracelefttp", "braceleftmid", "braceleftbt", "braceex",
            null, "angleright", "integral", "integraltp", "integralex", "integralbt", "parenrighttp", "parenrightex",
            "parenrightbt", "bracketrighttp", "bracketrightex", "bracketrightbt", "bracerighttp", "bracerightmid", "bracerightbt");
        // In the Symbol font, the Greek "Delta"/"Omega"/"mu" are the Greek letters.
        t[68] = "uni0394";
        t[87] = "uni03A9";
        t[109] = "uni03BC";
        return t;
    }

    private static string?[] BuildZapfDingbats()
    {
        var t = new string?[256];
        t[32] = "space";
        for (int c = 0x21; c <= 0x7E; c++)
        {
            int u = c switch
            {
                0x25 => 0x260E,
                0x2A => 0x261B,
                0x2B => 0x261E,
                0x48 => 0x2605,
                0x6C => 0x25CF,
                0x6E => 0x25A0,
                0x73 => 0x25B2,
                0x74 => 0x25BC,
                0x75 => 0x25C6,
                0x77 => 0x25D7,
                _ => 0x2700 + (c - 0x20)
            };
            t[c] = "uni" + u.ToString("X4");
        }
        for (int c = 0x80; c <= 0x8D; c++)
            t[c] = "uni" + (0x2768 + (c - 0x80)).ToString("X4");
        for (int c = 0xA1; c <= 0xFE; c++)
        {
            if (c == 0xF0)
                continue;
            int u = c switch
            {
                <= 0xA7 => 0x2761 + (c - 0xA1),
                0xA8 => 0x2663,
                0xA9 => 0x2666,
                0xAA => 0x2665,
                0xAB => 0x2660,
                <= 0xB5 => 0x2460 + (c - 0xAC),
                0xD5 => 0x2192,
                0xD6 => 0x2194,
                0xD7 => 0x2195,
                _ => 0x2776 + (c - 0xB6)
            };
            t[c] = "uni" + u.ToString("X4");
        }
        return t;
    }
}
