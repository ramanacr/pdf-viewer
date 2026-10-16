using System;

namespace PdfEngine.Vector.Editing;

/// <summary>The Unicode Vertical_Orientation property (UAX #50): how a character is set in a vertical column.</summary>
internal enum VerticalOrientationClass : byte
{
    /// <summary>Rotated 90° clockwise, as in horizontal text turned sideways (Latin, digits, Cyrillic, Greek...).</summary>
    R,
    /// <summary>Upright, as in horizontal text (ideographs, kana, Hangul).</summary>
    U,
    /// <summary>A vertical form where the font has one, else upright (small kana, ideographic comma and full stop).</summary>
    Tu,
    /// <summary>A vertical form where the font has one, else rotated (brackets, the long vowel mark, dashes).</summary>
    Tr,
}

/// <summary>
/// The Vertical_Orientation property of a code point, from a compact table of the ranges that are
/// not R (everything else is R, the property's default outside the East Asian blocks). Compiled by
/// hand from UAX #50's VerticalOrientation.txt; neighbouring ranges of the same value are merged,
/// and unassigned code points inside the East Asian blocks take that block's value.
/// </summary>
internal static class VerticalOrientation
{
    private const byte U = (byte)VerticalOrientationClass.U, Tu = (byte)VerticalOrientationClass.Tu, Tr = (byte)VerticalOrientationClass.Tr;

    // (first, last, value), sorted and not overlapping.
    private static readonly (int First, int Last, byte Value)[] Ranges =
    {
        (0x00A7, 0x00A7, U), (0x00A9, 0x00A9, U), (0x00AE, 0x00AE, U), (0x00B1, 0x00B1, U), (0x00BC, 0x00BE, U),
        (0x00D7, 0x00D7, U), (0x00F7, 0x00F7, U), (0x02EA, 0x02EB, U),
        (0x1100, 0x11FF, U), (0x1401, 0x167F, U), (0x18B0, 0x18FF, U),
        (0x2016, 0x2016, U), (0x2020, 0x2021, U), (0x2030, 0x2031, U), (0x203B, 0x203C, U), (0x2042, 0x2042, U),
        (0x2047, 0x2049, U), (0x2051, 0x2051, U), (0x2065, 0x2065, U), (0x20DD, 0x20E0, U), (0x20E2, 0x20E4, U),
        (0x2100, 0x2101, U), (0x2103, 0x2109, U), (0x210F, 0x210F, U), (0x2113, 0x2114, U), (0x2116, 0x2117, U),
        (0x211E, 0x2123, U), (0x2125, 0x2125, U), (0x2127, 0x2127, U), (0x2129, 0x2129, U), (0x212E, 0x212E, U),
        (0x2135, 0x213F, U), (0x2145, 0x214A, U), (0x214C, 0x214D, U), (0x214F, 0x2189, U), (0x218C, 0x218F, U),
        (0x221E, 0x221E, U), (0x2234, 0x2235, U),
        (0x2300, 0x2307, U), (0x230C, 0x231F, U), (0x2324, 0x2328, U), (0x2329, 0x232A, Tr), (0x232B, 0x232B, U),
        (0x237D, 0x239A, U), (0x23BE, 0x23CD, U), (0x23CF, 0x23CF, U), (0x23D1, 0x23DB, U), (0x23E2, 0x2422, U),
        (0x2424, 0x24FF, U), (0x25A0, 0x2619, U), (0x2620, 0x2767, U), (0x2776, 0x2793, U),
        (0x2B12, 0x2B2F, U), (0x2B50, 0x2B59, U), (0x2BB8, 0x2BFF, U), (0x2E50, 0x2E51, U),
        // CJK radicals, Kangxi radicals, ideographic description characters, ideographic space.
        (0x2E80, 0x3000, U),
        // CJK symbols and punctuation: comma and full stop, brackets, wave dash.
        (0x3001, 0x3002, Tu), (0x3003, 0x3007, U), (0x3008, 0x3011, Tr), (0x3012, 0x3013, U), (0x3014, 0x301F, Tr),
        (0x3020, 0x302F, U), (0x3030, 0x3030, Tr), (0x3031, 0x3040, U),
        // Hiragana: the small letters take vertical forms (set to the upper right).
        (0x3041, 0x3041, Tu), (0x3042, 0x3042, U), (0x3043, 0x3043, Tu), (0x3044, 0x3044, U), (0x3045, 0x3045, Tu),
        (0x3046, 0x3046, U), (0x3047, 0x3047, Tu), (0x3048, 0x3048, U), (0x3049, 0x3049, Tu), (0x304A, 0x3062, U),
        (0x3063, 0x3063, Tu), (0x3064, 0x3082, U), (0x3083, 0x3083, Tu), (0x3084, 0x3084, U), (0x3085, 0x3085, Tu),
        (0x3086, 0x3086, U), (0x3087, 0x3087, Tu), (0x3088, 0x308D, U), (0x308E, 0x308E, Tu), (0x308F, 0x3094, U),
        (0x3095, 0x3096, Tu), (0x3097, 0x309A, U), (0x309B, 0x309C, Tu), (0x309D, 0x309F, U),
        // Katakana: likewise, and the long vowel mark is turned.
        (0x30A0, 0x30A0, Tr), (0x30A1, 0x30A1, Tu), (0x30A2, 0x30A2, U), (0x30A3, 0x30A3, Tu), (0x30A4, 0x30A4, U),
        (0x30A5, 0x30A5, Tu), (0x30A6, 0x30A6, U), (0x30A7, 0x30A7, Tu), (0x30A8, 0x30A8, U), (0x30A9, 0x30A9, Tu),
        (0x30AA, 0x30C2, U), (0x30C3, 0x30C3, Tu), (0x30C4, 0x30E2, U), (0x30E3, 0x30E3, Tu), (0x30E4, 0x30E4, U),
        (0x30E5, 0x30E5, Tu), (0x30E6, 0x30E6, U), (0x30E7, 0x30E7, Tu), (0x30E8, 0x30ED, U), (0x30EE, 0x30EE, Tu),
        (0x30EF, 0x30F4, U), (0x30F5, 0x30F6, Tu), (0x30F7, 0x30FB, U), (0x30FC, 0x30FC, Tr),
        // Bopomofo, Hangul compatibility jamo, Kanbun, CJK strokes; small katakana; enclosed letters and squared words.
        (0x30FD, 0x31EF, U), (0x31F0, 0x31FF, Tu), (0x3200, 0x32FF, U), (0x3300, 0x3357, Tu), (0x3358, 0x337A, U),
        (0x337B, 0x337F, Tu), (0x3380, 0x33FF, U),
        // Ideographs, Yijing, Yi, Hangul, private use, compatibility ideographs.
        (0x3400, 0xA4CF, U), (0xA960, 0xA97F, U), (0xAC00, 0xD7FF, U), (0xE000, 0xFAFF, U),
        // Vertical forms, compatibility forms, small form variants.
        (0xFE10, 0xFE1F, U), (0xFE30, 0xFE48, U), (0xFE50, 0xFE57, Tu), (0xFE59, 0xFE5E, Tr), (0xFE5F, 0xFE62, U),
        (0xFE67, 0xFE6B, U),
        // Full-width forms (the half-width ones, FF61..FFDC, are R).
        (0xFF01, 0xFF01, Tu), (0xFF02, 0xFF07, U), (0xFF08, 0xFF09, Tr), (0xFF0A, 0xFF0B, U), (0xFF0C, 0xFF0C, Tu),
        (0xFF0D, 0xFF0D, Tr), (0xFF0E, 0xFF0E, Tu), (0xFF0F, 0xFF19, U), (0xFF1A, 0xFF1E, Tr), (0xFF1F, 0xFF1F, Tu),
        (0xFF20, 0xFF3A, U), (0xFF3B, 0xFF3B, Tr), (0xFF3C, 0xFF3C, U), (0xFF3D, 0xFF3D, Tr), (0xFF3E, 0xFF3E, U),
        (0xFF3F, 0xFF3F, Tr), (0xFF40, 0xFF5A, U), (0xFF5B, 0xFF60, Tr), (0xFFE0, 0xFFE2, U), (0xFFE3, 0xFFE3, Tr),
        (0xFFE4, 0xFFE7, U), (0xFFF0, 0xFFF8, U), (0xFFFC, 0xFFFD, U),
        // Supplementary planes: hieroglyphic and East Asian scripts, symbols and emoji, ideographs, private use.
        (0x10980, 0x1099F, U), (0x11580, 0x115FF, U), (0x11A00, 0x11AAF, U), (0x13000, 0x1345F, U), (0x14400, 0x1467F, U),
        (0x16FE0, 0x18AFF, U), (0x1AFF0, 0x1B2FF, U), (0x1D000, 0x1D1FF, U), (0x1D2E0, 0x1D37F, U), (0x1D800, 0x1DAAF, U),
        (0x1F000, 0x1F7FF, U), (0x1F900, 0x1FAFF, U), (0x20000, 0x3FFFD, U), (0xF0000, 0x10FFFD, U),
    };

    public static VerticalOrientationClass Of(int codePoint)
    {
        int lo = 0, hi = Ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var (first, last, value) = Ranges[mid];
            if (codePoint < first) hi = mid - 1;
            else if (codePoint > last) lo = mid + 1;
            else return (VerticalOrientationClass)value;
        }
        return VerticalOrientationClass.R;
    }

    /// <summary>The class of a text element (a character with any marks on it): that of its first character.</summary>
    public static VerticalOrientationClass Of(string element)
    {
        if (element.Length == 0) return VerticalOrientationClass.U;
        int cp = char.IsHighSurrogate(element[0]) && element.Length > 1 && char.IsLowSurrogate(element[1]) ? char.ConvertToUtf32(element[0], element[1]) : element[0];
        return Of(cp);
    }

    /// <summary>Drawn turned sideways in a column (R; Tr when the font has no vertical form for it).</summary>
    public static bool IsSideways(string element, bool hasVerticalForm)
    {
        var c = Of(element);
        return c == VerticalOrientationClass.R || (c == VerticalOrientationClass.Tr && !hasVerticalForm);
    }
}
