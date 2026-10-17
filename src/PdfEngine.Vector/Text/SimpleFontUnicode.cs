using System;
using System.Collections.Generic;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Text;

/// <summary>
/// The Unicode value of each code of a simple font when it has no /ToUnicode entry, by the rules
/// PDFium's font loaders use (CPDF_SimpleFont, CPDF_Type1Font, CPDF_TrueTypeFont): the base
/// encoding chosen from the flags, the font name and /Encoding; glyph names from /Differences or
/// the base encoding mapped by the Adobe Glyph List; for symbolic programs the program's built-in
/// encoding; for symbolic TrueType programs the Mac Roman meaning of the code. Codes without a
/// value read as the code itself (decided by the caller). Rendering uses its own encoding rules;
/// these exist so that extracted text matches what readers built on PDFium return.
/// </summary>
internal static class SimpleFontUnicode
{
    private enum Encoding { Builtin, WinAnsi, MacRoman, MacExpert, Standard, AdobeSymbol, ZapfDingbats, PdfDoc, MsSymbol }

    private const int FlagSymbolic = 4, FlagNonSymbolic = 32;

    /// <summary>Per code: its Unicode value (null when none) and the glyph name PDFium selects the glyph by (null when none).</summary>
    public static (string?[] Unicode, string?[] Names) Build(PdfFont font, PdfDictionary? dict, PdfObjectResolver? r)
    {
        var table = new string?[256];
        var glyphNames = new string?[256];
        if (dict == null) return (Fallback(font), glyphNames);
        try
        {
            BuildCore(font, dict, r, table, glyphNames);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
        }
        return (table, glyphNames);
    }

    /// <summary>A substitute font (no dictionary): the engine's own encoding.</summary>
    private static string?[] Fallback(PdfFont font)
    {
        var t = new string?[256];
        for (int c = 0; c < 256; c++)
            if (font.GetGlyphName(c) is { } n && Agl(n) is { } u) t[c] = u;
        return t;
    }

    private static void BuildCore(PdfFont font, PdfDictionary dict, PdfObjectResolver? r, string?[] table, string?[] glyphNames)
    {
        string subtype = font.Subtype;
        bool trueType = subtype == "TrueType";
        bool type3 = font.IsType3;
        bool embedded = font.EmbeddedFontData != null;
        string baseFont = embedded ? PdfFont.StripSubsetPrefix(font.BaseFont) : font.BaseFont;
        string? std = trueType || type3 ? null : Standard14Fonts.NormalizeName(font.BaseFont);
        var descriptor = r?.Resolve(dict["FontDescriptor"]) as PdfDictionary;

        int flags;
        if (descriptor != null)
            flags = descriptor["Flags"] is { } f && f.TryGetNumber(out double fv) ? (int)fv : FlagNonSymbolic;
        else if (std is "Symbol" or "ZapfDingbats")
            flags = FlagSymbolic;
        else
            flags = FlagNonSymbolic;
        bool symbolic = (flags & FlagSymbolic) != 0;
        bool nonSymbolic = (flags & FlagNonSymbolic) != 0;

        var enc = Encoding.Builtin;
        if (std == "Symbol") enc = Encoding.AdobeSymbol;
        else if (std == "ZapfDingbats") enc = Encoding.ZapfDingbats;
        else if (!symbolic) enc = Encoding.Standard;

        var names = new string?[256];
        var encObj = r?.Resolve(dict["Encoding"]) ?? dict["Encoding"];
        if (encObj == null)
        {
            if (baseFont == "Symbol") enc = trueType ? Encoding.MsSymbol : Encoding.AdobeSymbol;
            else if (!embedded && enc == Encoding.Builtin) enc = Encoding.WinAnsi;
        }
        else if (encObj is PdfName name)
        {
            if (enc is Encoding.AdobeSymbol or Encoding.ZapfDingbats) { }
            else if (symbolic && baseFont == "Symbol") { if (!trueType) enc = Encoding.AdobeSymbol; }
            else if (name.Value == "MacExpertEncoding") enc = Encoding.WinAnsi;
            else if (Predefined(name.Value) is { } p) enc = p;
        }
        else if (encObj is PdfDictionary encDict)
        {
            if (enc is not (Encoding.AdobeSymbol or Encoding.ZapfDingbats))
            {
                if (encDict.GetName("BaseEncoding") is { } baseName)
                {
                    if (baseName == "MacExpertEncoding" && trueType) enc = Encoding.WinAnsi;
                    else if (Predefined(baseName) is { } p) enc = p;
                }
                if (enc == Encoding.Builtin && (trueType || !embedded)) enc = Encoding.Standard;
            }
            if ((r?.Resolve(encDict["Differences"]) ?? encDict["Differences"]) is PdfArray diffs)
            {
                int code = 0;
                foreach (var raw in diffs.Items)
                {
                    var item = r?.Resolve(raw) ?? raw;
                    if (item is PdfInteger i) code = (int)i.Value;
                    else if (item.TryGetNumber(out double d)) code = (int)d;
                    else if (item is PdfName n)
                    {
                        if (code >= 0 && code < 256) names[code] = n.Value;
                        code++;
                    }
                }
            }
        }
        bool hasDifferences = Array.Exists(names, n => n != null);

        string? Name(int code) => names[code] is { Length: > 0 } n ? n : enc != Encoding.Builtin ? PredefinedName(enc, code) : null;

        for (int c = 0; c < 256; c++) glyphNames[c] = Name(c);
        // Type 3 glyph names are only keys of /CharProcs: PDFium reads such text by /ToUnicode alone.
        if (type3) return;

        if (trueType)
        {
            var cmap = embedded ? font.TrueTypeCmap : null;
            bool has31 = embedded ? cmap?.Has(3, 1) == true : baseFont != "Symbol";
            bool has30 = embedded ? cmap?.Has(3, 0) == true : baseFont == "Symbol" || symbolic;
            bool has10 = embedded ? cmap?.Has(1, 0) == true : true;
            bool hasUnicode = has31 || (embedded && cmap != null && (cmap.Has(0, 3) || cmap.Has(0, 4) || cmap.Has(0, 0) || cmap.Has(0, 1)));
            if (embedded && symbolic && enc is Encoding.WinAnsi or Encoding.MacRoman)
            {
                if (enc == Encoding.WinAnsi && !has31) enc = has10 ? Encoding.MacRoman : Encoding.Builtin;
                else if (enc == Encoding.MacRoman && !has10) enc = has31 ? Encoding.WinAnsi : Encoding.Builtin;
            }
            if ((enc is Encoding.WinAnsi or Encoding.MacRoman && !hasDifferences) || nonSymbolic)
            {
                for (int c = 0; c < 256; c++)
                    if (Name(c) is { } n) table[c] = Agl(n);
                return;
            }
            if (has30)
            {
                if (enc != Encoding.Builtin)
                {
                    for (int c = 0; c < 256; c++)
                        if (Name(c) is { } n) table[c] = Agl(n);
                }
                else if (has10)
                {
                    for (int c = 0; c < 256; c++) table[c] = AppleRoman(c);
                }
                return;
            }
            if (has10)
            {
                for (int c = 0; c < 256; c++) table[c] = AppleRoman(c);
                return;
            }
            if (hasUnicode)
            {
                for (int c = 0; c < 256; c++)
                {
                    if (embedded) table[c] = c == 0 ? null : ((char)c).ToString();
                    else if (names[c] is { } n) table[c] = Agl(n);
                    else if (PredefinedName(enc, c) is { } pn) table[c] = Agl(pn);
                }
            }
            return;
        }

        // Type 1 (Type1, MMType1, and CFF programs of Type1 fonts).
        bool substituteIsTrueType = !embedded && std == null;
        if (!embedded && !symbolic && substituteIsTrueType)
        {
            if (enc == Encoding.Builtin) enc = Encoding.Standard;
            for (int c = 0; c < 256; c++)
            {
                if (Name(c) is not { } n) continue;
                table[c] = n == ".notdef" ? " " : Agl(n);
            }
            return;
        }
        if (symbolic)
        {
            string?[]? builtIn = null;
            for (int c = 0; c < 256; c++)
            {
                if (Name(c) is { } n)
                {
                    table[c] = Agl(n);
                    continue;
                }
                builtIn ??= ProgramEncoding(font) ?? Array.Empty<string?>();
                if (c < builtIn.Length && builtIn[c] is { } bn) table[c] = Agl(bn);
            }
            return;
        }
        for (int c = 0; c < 256; c++)
        {
            if (Name(c) is not { } n) continue;
            table[c] = n is ".notdef" or "space" ? " " : Agl(n);
        }
    }

    /// <summary>The glyph names of an embedded Type 1 or CFF program's built-in encoding (code → name).</summary>
    private static string?[]? ProgramEncoding(PdfFont font)
    {
        var data = font.EmbeddedFontData;
        if (data == null) return null;
        try
        {
            switch (font.EmbeddedProgramKind)
            {
                case PdfFontProgramKind.Type1:
                    return Type1Converter.TryConvert(data)?.BuiltInEncoding;
                case PdfFontProgramKind.Type1C:
                case PdfFontProgramKind.OpenType:
                {
                    byte[]? cffData = font.EmbeddedProgramKind == PdfFontProgramKind.Type1C ? data
                        : Sfnt.ReadTables(data, out _) is { } t && t.TryGetValue("CFF ", out var c) ? c : null;
                    if (cffData == null || CffFont.TryParse(cffData) is not { IsCidKeyed: false } cff) return null;
                    var names = new string?[256];
                    for (int code = 0; code < 256; code++)
                        if (cff.GidForBuiltInCode(code) is int gid and > 0) names[code] = cff.GlyphName(gid);
                    return names;
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or System.IO.InvalidDataException)
        {
        }
        return null;
    }

    private static Encoding? Predefined(string name) => name switch
    {
        "WinAnsiEncoding" => Encoding.WinAnsi,
        "MacRomanEncoding" => Encoding.MacRoman,
        "MacExpertEncoding" => Encoding.MacExpert,
        "StandardEncoding" => Encoding.Standard,
        "PDFDocEncoding" => Encoding.PdfDoc,
        _ => null,
    };

    private static string? PredefinedName(Encoding enc, int code) => enc switch
    {
        Encoding.WinAnsi => WinAnsi[code],
        Encoding.MacRoman => FontEncodings.MacRoman[code],
        Encoding.Standard => FontEncodings.Standard[code],
        Encoding.AdobeSymbol => Symbol[code],
        Encoding.ZapfDingbats => code < ZapfNames.Length ? ZapfNames[code] : null,
        Encoding.PdfDoc => PdfDocName(code),
        _ => null,
    };

    /// <summary>WinAnsiEncoding as PDFium names it: the undefined codes read as bullets, 160 as a space.</summary>
    private static readonly string?[] WinAnsi = BuildWinAnsi();

    private static string?[] BuildWinAnsi()
    {
        var t = (string?[])FontEncodings.WinAnsi.Clone();
        foreach (int c in new[] { 0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x9D }) t[c] = "bullet";
        t[0xA0] = "space";
        t[0xAD] = "hyphen";
        return t;
    }

    /// <summary>The Symbol font's encoding by its own glyph names (Delta, Omega and mu read as the Adobe Glyph List has them).</summary>
    private static readonly string?[] Symbol = BuildSymbol();

    private static string?[] BuildSymbol()
    {
        var t = (string?[])FontEncodings.Symbol.Clone();
        t[68] = "Delta";
        t[87] = "Omega";
        t[109] = "mu";
        return t;
    }

    private static string? PdfDocName(int code) => code is >= 0x20 and < 0x7F ? FontEncodings.Standard[code] is { } s && code != 0x27 && code != 0x60 ? s : code == 0x27 ? "quotesingle" : "grave" : FontEncodings.WinAnsi[code];

    /// <summary>The ZapfDingbats encoding's glyph names (a1 … a191), code order.</summary>
    private static readonly string?[] ZapfNames = BuildZapfNames();

    private static string?[] BuildZapfNames()
    {
        var t = new string?[256];
        t[32] = "space";
        string[] low =
        {
            "a1", "a2", "a202", "a3", "a4", "a5", "a119", "a118", "a117", "a11", "a12", "a13", "a14", "a15", "a16", "a105",
            "a17", "a18", "a19", "a20", "a21", "a22", "a23", "a24", "a25", "a26", "a27", "a28", "a6", "a7", "a8", "a9",
            "a10", "a29", "a30", "a31", "a32", "a33", "a34", "a35", "a36", "a37", "a38", "a39", "a40", "a41", "a42", "a43",
            "a44", "a45", "a46", "a47", "a48", "a49", "a50", "a51", "a52", "a53", "a54", "a55", "a56", "a57", "a58", "a59",
            "a60", "a61", "a62", "a63", "a64", "a65", "a66", "a67", "a68", "a69", "a70", "a71", "a72", "a73", "a74", "a203",
            "a75", "a204", "a76", "a77", "a78", "a79", "a81", "a82", "a83", "a84", "a97", "a98", "a99", "a100",
        };
        for (int i = 0; i < low.Length; i++) t[0x21 + i] = low[i];
        string[] mid = { "a89", "a90", "a93", "a94", "a91", "a92", "a205", "a85", "a206", "a86", "a87", "a88", "a95", "a96" };
        for (int i = 0; i < mid.Length; i++) t[0x80 + i] = mid[i];
        string[] high =
        {
            "a101", "a102", "a103", "a104", "a106", "a107", "a108", "a112", "a111", "a110", "a109",
            "a120", "a121", "a122", "a123", "a124", "a125", "a126", "a127", "a128", "a129",
            "a130", "a131", "a132", "a133", "a134", "a135", "a136", "a137", "a138", "a139",
            "a140", "a141", "a142", "a143", "a144", "a145", "a146", "a147", "a148", "a149",
            "a150", "a151", "a152", "a153", "a154", "a155", "a156", "a157", "a158", "a159",
            "a160", "a161", "a163", "a164", "a196", "a165", "a192", "a166", "a167", "a168", "a169", "a170", "a171", "a172", "a173",
            "a162", "a174", "a175", "a176", "a177", "a178", "a179", "a193", "a180", "a199", "a181", "a200", "a182",
        };
        for (int i = 0; i < high.Length; i++) t[0xA1 + i] = high[i];
        string[] top = { "a201", "a183", "a184", "a197", "a185", "a194", "a198", "a186", "a195", "a187", "a188", "a189", "a190", "a191" };
        for (int i = 0; i < top.Length; i++) t[0xF1 + i] = top[i];
        return t;
    }

    private static readonly Dictionary<string, string> ZapfUnicode = BuildZapfUnicode();

    private static Dictionary<string, string> BuildZapfUnicode()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int c = 0x21; c < 256; c++)
        {
            if (ZapfNames[c] is not { } n || FontEncodings.ZapfDingbats[c] is not { } uni) continue;
            if (GlyphList.ToUnicode(uni) is { } u) map.TryAdd(n, u);
        }
        return map;
    }

    /// <summary>A glyph name's Unicode by the Adobe Glyph List (and the ZapfDingbats list); null when it has none.</summary>
    public static string? Agl(string name)
    {
        if (ZapfUnicode.TryGetValue(name, out var z)) return z;
        if (AdobePrivateUse.TryGetValue(name, out int pua)) return ((char)pua).ToString();
        if (MoreGlyphNames.Value.TryGetValue(name, out int more)) return ((char)more).ToString();
        if (name.Length > 8 && name.EndsWith("oldstyle", StringComparison.Ordinal) && OldStyle(name[..^8]) is int os) return ((char)os).ToString();
        if (name.Length == 6 && name.EndsWith("small", StringComparison.Ordinal) && name[0] is >= 'A' and <= 'Z') return ((char)(0xF761 + name[0] - 'A')).ToString();
        // Underscore ligature names (f_i) are not in the list PDFium reads glyph names with.
        if (name.IndexOf('_') > 0 && !name.StartsWith("uni", StringComparison.Ordinal)) return null;
        return GlyphList.ToUnicode(name);
    }

    /// <summary>
    /// Adobe Glyph List names beyond the engine's table: Cyrillic and Hebrew (afii), Greek with
    /// tonos, and the geometric and other symbols fonts commonly name by the list.
    /// </summary>
    private static readonly Lazy<Dictionary<string, int>> MoreGlyphNames = new(() =>
    {
        var t = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i <= 5; i++) t["afii" + (10017 + i)] = 0x410 + i;
        t["afii10023"] = 0x401;
        for (int i = 0; i <= 25; i++) t["afii" + (10024 + i)] = 0x416 + i;
        for (int i = 0; i <= 5; i++) t["afii" + (10065 + i)] = 0x430 + i;
        t["afii10071"] = 0x451;
        for (int i = 0; i <= 25; i++) t["afii" + (10072 + i)] = 0x436 + i;
        int[] cyrUpper = { 0x402, 0x403, 0x404, 0x405, 0x406, 0x407, 0x408, 0x409, 0x40A, 0x40B, 0x40C, 0x40E };
        for (int i = 0; i < cyrUpper.Length; i++) t["afii" + (10051 + i)] = cyrUpper[i];
        int[] cyrLower = { 0x452, 0x453, 0x454, 0x455, 0x456, 0x457, 0x458, 0x459, 0x45A, 0x45B, 0x45C, 0x45E };
        for (int i = 0; i < cyrLower.Length; i++) t["afii" + (10099 + i)] = cyrLower[i];
        t["afii10145"] = 0x40F; t["afii10193"] = 0x45F; t["afii10050"] = 0x490; t["afii10098"] = 0x491;
        for (int i = 0; i <= 26; i++) t["afii" + (57664 + i)] = 0x5D0 + i;
        (string, int)[] list =
        {
            ("tonos", 0x384), ("dieresistonos", 0x385), ("Alphatonos", 0x386), ("anoteleia", 0x387), ("Epsilontonos", 0x388),
            ("Etatonos", 0x389), ("Iotatonos", 0x38A), ("Omicrontonos", 0x38C), ("Upsilontonos", 0x38E), ("Omegatonos", 0x38F),
            ("iotadieresistonos", 0x390), ("Iotadieresis", 0x3AA), ("Upsilondieresis", 0x3AB), ("alphatonos", 0x3AC),
            ("epsilontonos", 0x3AD), ("etatonos", 0x3AE), ("iotatonos", 0x3AF), ("upsilondieresistonos", 0x3B0),
            ("iotadieresis", 0x3CA), ("upsilondieresis", 0x3CB), ("omicrontonos", 0x3CC), ("upsilontonos", 0x3CD), ("omegatonos", 0x3CE),
            ("openbullet", 0x25E6), ("circle", 0x25CB), ("filledbox", 0x25A0), ("H18533", 0x25CF), ("H18543", 0x25AA), ("H18551", 0x25AB),
            ("H22073", 0x25A1), ("filledrect", 0x25AC), ("triagup", 0x25B2), ("triagdn", 0x25BC), ("triagrt", 0x25BA), ("triaglf", 0x25C4),
            ("invbullet", 0x25D8), ("invcircle", 0x25D9), ("smileface", 0x263A), ("invsmileface", 0x263B), ("sun", 0x263C),
            ("female", 0x2640), ("male", 0x2642), ("musicalnote", 0x266A), ("musicalnotedbl", 0x266B), ("dkshade", 0x2593),
            ("ltshade", 0x2591), ("shade", 0x2592), ("block", 0x2588), ("lfblock", 0x258C), ("rtblock", 0x2590), ("upblock", 0x2580),
            ("dnblock", 0x2584), ("house", 0x2302), ("revlogicalnot", 0x2310), ("arrowupdn", 0x2195), ("arrowupdnbse", 0x21A8),
            ("orthogonal", 0x221F), ("afii61352", 0x2116), ("afii61248", 0x2105), ("afii61289", 0x2113), ("Ohm", 0x2126),
            ("middot", 0xB7), ("overscore", 0xAF), ("exclamdbl", 0x203C), ("franc", 0x20A3), ("lira", 0x20A4), ("peseta", 0x20A7),
            ("colonmonetary", 0x20A1), ("dong", 0x20AB), ("prescription", 0x211E), ("Ifraktur", 0x2111), ("integraltp", 0x2320),
            ("integralbt", 0x2321), ("dotlessj", 0x237), ("quotedblreversed", 0x201F), ("primereversed", 0x2035), ("lozenge", 0x25CA),
        };
        foreach (var (n, cp) in list) t[n] = cp;
        return t;
    });

    private static int? OldStyle(string digit) => Array.IndexOf(Digits, digit) is int i and >= 0 ? 0xF730 + i : digit == "dollar" ? 0xF724 : null;

    private static readonly string[] Digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    /// <summary>Adobe's private-use assignments the glyph list keeps for Symbol font pieces and serif/sans marks.</summary>
    private static readonly Dictionary<string, int> AdobePrivateUse = new(StringComparer.Ordinal)
    {
        ["radicalex"] = 0xF8E5, ["arrowvertex"] = 0xF8E6, ["arrowhorizex"] = 0xF8E7, ["registersans"] = 0xF8E8, ["copyrightsans"] = 0xF8E9,
        ["trademarksans"] = 0xF8EA, ["parenlefttp"] = 0xF8EB, ["parenleftex"] = 0xF8EC, ["parenleftbt"] = 0xF8ED, ["bracketlefttp"] = 0xF8EE,
        ["bracketleftex"] = 0xF8EF, ["bracketleftbt"] = 0xF8F0, ["bracelefttp"] = 0xF8F1, ["braceleftmid"] = 0xF8F2, ["braceleftbt"] = 0xF8F3,
        ["braceex"] = 0xF8F4, ["integralex"] = 0xF8F5, ["parenrighttp"] = 0xF8F6, ["parenrightex"] = 0xF8F7, ["parenrightbt"] = 0xF8F8,
        ["bracketrighttp"] = 0xF8F9, ["bracketrightex"] = 0xF8FA, ["bracketrightbt"] = 0xF8FB, ["bracerighttp"] = 0xF8FC, ["bracerightmid"] = 0xF8FD,
        ["bracerightbt"] = 0xF8FE, ["apple"] = 0xF8FF, ["copyrightserif"] = 0xF6D9, ["registerserif"] = 0xF6DA, ["trademarkserif"] = 0xF6DB,
    };

    private static string? AppleRoman(int code) =>
        code < 0x20 ? null : FontEncodings.MacRoman[code] is { } n ? GlyphList.ToUnicode(n) : code < 0x80 ? ((char)code).ToString() : null;
}
