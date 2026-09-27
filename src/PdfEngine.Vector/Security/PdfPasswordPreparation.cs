using System;
using System.Globalization;
using System.Text;

namespace PdfEngine.Vector.Security;

/// <summary>
/// Password preparation for the AES-256 Standard security handler (ISO 32000-2 7.6.4.3.3): the
/// SASLprep profile (RFC 4013) of stringprep (RFC 3454), then UTF-8, truncated to 127 bytes.
/// Mapping (table B.1 to nothing, C.1.2 to a space), NFKC, the prohibited tables (C.1.2, C.2.1,
/// C.2.2, C.3 to C.9) and the bidirectional rule (6) are applied. Two approximations: table D.2
/// (left-to-right characters) is taken as every letter or spacing mark that is not right-to-left,
/// and "unassigned" means unassigned in the runtime's Unicode version rather than Unicode 3.2.
/// </summary>
public static class PdfPasswordPreparation
{
    /// <summary>ISO 32000-2 truncates the UTF-8 password to this many bytes.</summary>
    public const int MaxBytes = 127;

    /// <summary>
    /// A password about to be stored in a new encryption dictionary. Unassigned code points are
    /// refused as well as prohibited ones (a "stored string" in RFC 3454 terms).
    /// </summary>
    /// <exception cref="ArgumentException">The password contains a character SASLprep prohibits.</exception>
    public static byte[] PrepareForEncryption(string password) => Truncate(SaslPrep(password, allowUnassigned: false));

    /// <summary>A password typed to open a document (a "query": unassigned code points are allowed). Null when prohibited.</summary>
    internal static byte[]? TryPrepareForAuthentication(string password)
    {
        try { return Truncate(SaslPrep(password, allowUnassigned: true)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>NFKC alone, as many writers prepare passwords: the fallback when SASLprep gives different bytes.</summary>
    internal static byte[] NfkcOnly(string password)
    {
        string normalized;
        try { normalized = password.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { normalized = password; } // lone surrogates
        return Truncate(Encoding.UTF8.GetBytes(normalized));
    }

    private static byte[] Truncate(byte[] utf8) => utf8.Length > MaxBytes ? utf8[..MaxBytes] : utf8;

    private static byte[] Truncate(string prepared) => Truncate(Encoding.UTF8.GetBytes(prepared));

    /// <summary>RFC 4013 on <paramref name="input"/>; throws <see cref="ArgumentException"/> naming the rule broken.</summary>
    public static string SaslPrep(string input, bool allowUnassigned)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Map: non-ASCII spaces to SPACE, "commonly mapped to nothing" removed.
        var mapped = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            int cp = CodePointAt(input, ref i);
            if (IsNonAsciiSpace(cp)) mapped.Append(' ');
            else if (!IsMappedToNothing(cp)) mapped.Append(char.ConvertFromUtf32(cp));
        }

        // 2. Normalize with NFKC.
        string normalized = mapped.ToString().Normalize(NormalizationForm.FormKC);

        // 3. Prohibit, 4. check bidi, and refuse unassigned code points in stored strings.
        bool hasRandAL = false, hasL = false;
        int first = -1, last = -1;
        for (int i = 0; i < normalized.Length; i++)
        {
            int cp = CodePointAt(normalized, ref i);
            if (IsProhibited(cp))
                throw new ArgumentException($"The password contains a character that is not allowed (U+{cp:X4}).", nameof(input));
            if (!allowUnassigned && CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.OtherNotAssigned)
                throw new ArgumentException($"The password contains an unassigned character (U+{cp:X4}).", nameof(input));
            bool randAL = IsRandAL(cp);
            hasRandAL |= randAL;
            hasL |= !randAL && IsLeftToRight(cp);
            if (first < 0) first = cp;
            last = cp;
        }
        if (hasRandAL && (hasL || !IsRandAL(first) || !IsRandAL(last)))
            throw new ArgumentException("The password mixes right-to-left and left-to-right text in a way SASLprep does not allow.", nameof(input));
        return normalized;
    }

    private static int CodePointAt(string s, ref int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            return char.ConvertToUtf32(c, s[++i]);
        if (char.IsSurrogate(c))
            throw new ArgumentException("The password contains an incomplete surrogate pair.", "input"); // C.5
        return c;
    }

    // RFC 3454 table C.1.2.
    private static bool IsNonAsciiSpace(int cp) =>
        cp is 0x00A0 or 0x1680 or (>= 0x2000 and <= 0x200B) or 0x202F or 0x205F or 0x3000;

    // RFC 3454 table B.1.
    private static bool IsMappedToNothing(int cp) =>
        cp is 0x00AD or 0x034F or 0x1806 or (>= 0x180B and <= 0x180D) or (>= 0x200B and <= 0x200D)
            or 0x2060 or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF;

    // RFC 3454 tables C.1.2 and C.2.1 to C.9, as RFC 4013 section 2.3 lists them.
    private static bool IsProhibited(int cp) =>
        IsNonAsciiSpace(cp)
        || cp is <= 0x001F or 0x007F                                                         // C.2.1
        || cp is (>= 0x0080 and <= 0x009F) or 0x06DD or 0x070F or 0x180E or 0x200C or 0x200D
            or 0x2028 or 0x2029 or (>= 0x2060 and <= 0x2063) or (>= 0x206A and <= 0x206F)
            or 0xFEFF or (>= 0xFFF9 and <= 0xFFFC) or (>= 0x1D173 and <= 0x1D17A)            // C.2.2
        || cp is (>= 0xE000 and <= 0xF8FF) or (>= 0xF0000 and <= 0xFFFFD) or (>= 0x100000 and <= 0x10FFFD) // C.3
        || cp is (>= 0xFDD0 and <= 0xFDEF) || (cp & 0xFFFE) == 0xFFFE                         // C.4
        || cp is >= 0xD800 and <= 0xDFFF                                                      // C.5
        || cp is >= 0xFFF9 and <= 0xFFFD                                                      // C.6
        || cp is >= 0x2FF0 and <= 0x2FFB                                                      // C.7
        || cp is 0x0340 or 0x0341 or 0x200E or 0x200F or (>= 0x202A and <= 0x202E)            // C.8
        || cp is 0xE0001 or (>= 0xE0020 and <= 0xE007F);                                      // C.9

    // RFC 3454 table D.1 (characters with bidirectional property R or AL), plus letters that later
    // Unicode versions added to the right-to-left blocks.
    private static bool IsRandAL(int cp) => IsRandAL32(cp) || (InRightToLeftBlock(cp) && IsLetter(cp));

    private static bool InRightToLeftBlock(int cp) =>
        cp is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF)
            or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF);

    private static bool IsRandAL32(int cp) =>
        cp is 0x05BE or 0x05C0 or 0x05C3 or (>= 0x05D0 and <= 0x05EA) or (>= 0x05F0 and <= 0x05F4)
            or 0x061B or 0x061F or (>= 0x0621 and <= 0x063A) or (>= 0x0640 and <= 0x064A)
            or (>= 0x066D and <= 0x066F) or (>= 0x0671 and <= 0x06D5) or 0x06DD or (>= 0x06E5 and <= 0x06E6)
            or (>= 0x06FA and <= 0x06FE) or (>= 0x0700 and <= 0x070D) or 0x0710 or (>= 0x0712 and <= 0x072C)
            or (>= 0x0780 and <= 0x07A5) or 0x07B1 or 0x200F or 0xFB1D or (>= 0xFB1F and <= 0xFB28)
            or (>= 0xFB2A and <= 0xFB36) or (>= 0xFB38 and <= 0xFB3C) or 0xFB3E or (>= 0xFB40 and <= 0xFB41)
            or (>= 0xFB43 and <= 0xFB44) or (>= 0xFB46 and <= 0xFBB1) or (>= 0xFBD3 and <= 0xFD3D)
            or (>= 0xFD50 and <= 0xFD8F) or (>= 0xFD92 and <= 0xFDC7) or (>= 0xFDF0 and <= 0xFDFC)
            or (>= 0xFE70 and <= 0xFE74) or (>= 0xFE76 and <= 0xFEFC);

    // Table D.2 approximated: letters and spacing marks outside the right-to-left blocks (digits,
    // punctuation and symbols are neutral).
    private static bool IsLeftToRight(int cp) =>
        !InRightToLeftBlock(cp) && (IsLetter(cp) || CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.SpacingCombiningMark);

    private static bool IsLetter(int cp) => CharUnicodeInfo.GetUnicodeCategory(cp) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
}
