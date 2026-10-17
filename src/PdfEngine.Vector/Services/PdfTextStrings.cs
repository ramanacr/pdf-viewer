using System.Globalization;
using System.Text;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Text strings (ISO 32000-2 7.9.2.2): UTF-16BE or UTF-16LE with a byte order mark, UTF-8 with
/// one (PDF 2.0), else PDFDocEncoding. Language escapes (U+001B ... U+001B) inside UTF-16 text
/// are dropped, as PDFium does.
/// </summary>
public static class PdfTextStrings
{
    /// <summary>PDFDocEncoding where it differs from Latin-1 (Annex D.2); 0 marks an undefined code.</summary>
    private static readonly Dictionary<byte, char> PdfDocDifferences = new()
    {
        [0x18] = '˘', [0x19] = 'ˇ', [0x1A] = 'ˆ', [0x1B] = '˙',
        [0x1C] = '˝', [0x1D] = '˛', [0x1E] = '˚', [0x1F] = '˜',
        [0x80] = '•', [0x81] = '†', [0x82] = '‡', [0x83] = '…',
        [0x84] = '—', [0x85] = '–', [0x86] = 'ƒ', [0x87] = '⁄',
        [0x88] = '‹', [0x89] = '›', [0x8A] = '−', [0x8B] = '‰',
        [0x8C] = '„', [0x8D] = '“', [0x8E] = '”', [0x8F] = '‘',
        [0x90] = '’', [0x91] = '‚', [0x92] = '™', [0x93] = 'ﬁ',
        [0x94] = 'ﬂ', [0x95] = 'Ł', [0x96] = 'Œ', [0x97] = 'Š',
        [0x98] = 'Ÿ', [0x99] = 'Ž', [0x9A] = 'ı', [0x9B] = 'ł',
        [0x9C] = 'œ', [0x9D] = 'š', [0x9E] = 'ž', [0x9F] = '\0',
        [0xA0] = '€', [0xAD] = '\0',
    };

    /// <summary>Decodes a text string object; null for anything that is not a string.</summary>
    public static string? Decode(PdfObject? obj) => obj is PdfString s ? Decode(s.RawBytes.Span) : null;

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return StripLanguageEscapes(Encoding.BigEndianUnicode.GetString(bytes[2..(2 + (bytes.Length - 2) / 2 * 2)]));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return StripLanguageEscapes(Encoding.Unicode.GetString(bytes[2..(2 + (bytes.Length - 2) / 2 * 2)]));
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);

        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (PdfDocDifferences.TryGetValue(b, out char c))
            {
                if (c != '\0') sb.Append(c);
            }
            else
            {
                sb.Append((char)b);
            }
        }
        return sb.ToString();
    }

    private static string StripLanguageEscapes(string text)
    {
        int start = text.IndexOf('\u001B');
        if (start < 0) return text;
        var sb = new StringBuilder(text.Length);
        bool inEscape = false;
        foreach (char c in text)
        {
            if (c == '\u001B') { inEscape = !inEscape; continue; }
            if (!inEscape) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A PDF date (7.9.4, "D:YYYYMMDDHHmmSSOHH'mm'"), every part after the year optional. The
    /// result keeps the wall-clock time written in the file, as the viewer has always shown it.
    /// </summary>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.Trim();
        if (s.StartsWith("D:", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        int digits = 0;
        while (digits < s.Length && digits < 14 && char.IsAsciiDigit(s[digits])) digits++;
        if (digits < 4) return null;
        int Part(int at, int length, int fallback) =>
            at + length <= digits && int.TryParse(s.AsSpan(at, length), NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        int year = Part(0, 4, 0), month = Part(4, 2, 1), day = Part(6, 2, 1);
        int hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }

    /// <summary>"D:YYYYMMDDHHmmSS+HH'mm'" for a local time, the form every reader shows.</summary>
    public static string FormatDate(DateTime value)
    {
        var offset = value.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(value);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return string.Format(CultureInfo.InvariantCulture, "D:{0:yyyyMMddHHmmss}{1}{2:00}'{3:00}'", value, sign, magnitude.Hours, magnitude.Minutes);
    }
}
