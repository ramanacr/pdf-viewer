using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfEngine.Vector.Forms;

public enum PdfFieldFormatKind { None, Number, Percent, Date, Time, Special, Mask }

/// <summary>A value refused by a field's keystroke or validate rule.</summary>
public sealed class PdfFieldValidationException : Exception
{
    public string FieldName { get; }
    public PdfFieldValidationException(string fieldName, string message) : base(message) => FieldName = fieldName;
}

/// <summary>What a field's scripts ask for, as understood without running them.</summary>
public sealed class PdfFieldBehaviour
{
    public PdfFieldFormatKind Format { get; init; }
    public int Decimals { get; init; }
    /// <summary>AFNumber sepStyle: 0 "1,234.56", 1 "1234.56", 2 "1.234,56", 3 "1234,56", 4 "1'234.56".</summary>
    public int SeparatorStyle { get; init; }
    /// <summary>0 minus, 1 red, 2 parentheses, 3 red parentheses.</summary>
    public int NegativeStyle { get; init; }
    public string Currency { get; init; } = string.Empty;
    public bool CurrencyPrepend { get; init; } = true;
    public bool PercentPrepend { get; init; }
    /// <summary>Date or time pattern (Acrobat tokens: d dd ddd dddd m mm mmm mmmm yy yyyy H HH h hh MM ss tt).</summary>
    public string Pattern { get; init; } = string.Empty;
    /// <summary>AFSpecial_Format: 0 zip, 1 zip+4, 2 phone, 3 social security number.</summary>
    public int Special { get; init; }
    /// <summary>AFSpecial_KeystrokeEx mask: 9 digit, A letter, O letter or digit, X anything, others literal.</summary>
    public string Mask { get; init; } = string.Empty;

    public double? RangeMin { get; init; }
    public double? RangeMax { get; init; }

    /// <summary>AFSimple_Calculate: SUM, AVG, PRD, MIN or MAX over the named fields.</summary>
    public string? CalculateOperation { get; init; }
    public IReadOnlyList<string> CalculateFields { get; init; } = Array.Empty<string>();
    /// <summary>A simplified-field-notation expression (Acrobat's /** BVCALC ... EVCALC **/).</summary>
    public string? CalculateExpression { get; init; }

    /// <summary>Triggers whose script is custom code this reader does not run.</summary>
    public IReadOnlyList<string> Unsupported { get; init; } = Array.Empty<string>();

    public bool Calculates => CalculateOperation != null || CalculateExpression != null;
    public bool IsNumeric => Format is PdfFieldFormatKind.Number or PdfFieldFormatKind.Percent;
}

/// <summary>A value as the field shows it.</summary>
public readonly record struct PdfFieldDisplay(string Text, bool Red);

/// <summary>
/// The standard Acrobat form functions (AFNumber_*, AFPercent_*, AFDate_*, AFTime_*,
/// AFSpecial_*, AFRange_Validate, AFSimple_Calculate and simplified field notation), recognised
/// in a field's scripts and carried out natively. Document JavaScript is never executed: a
/// script that is anything more than these calls is reported as unsupported.
/// </summary>
public static class PdfFormScripts
{
    private static readonly string[] DateFormats =
    {
        "m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd",
        "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM",
    };
    private static readonly string[] TimeFormats = { "HH:MM", "h:MM tt", "HH:MM:ss", "h:MM:ss tt" };
    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[..12];
    private static readonly string[] DayNames = CultureInfo.InvariantCulture.DateTimeFormat.DayNames;

    public static PdfFieldBehaviour Read(PdfFormField field)
    {
        var unsupported = new List<string>();
        var calls = new Dictionary<string, (string Name, List<object?> Args)>();
        string? sfn = null;
        foreach (var (trigger, script) in field.Scripts)
        {
            if (trigger == "C" && Regex.Match(script, @"/\*\*\s*BVCALC(.*?)EVCALC\s*\*\*/", RegexOptions.Singleline) is { Success: true } m)
            {
                sfn = m.Groups[1].Value.Trim();
                continue;
            }
            if (ParseCall(script) is { } call) calls[trigger] = call;
            else unsupported.Add(trigger);
        }

        var b = new Builder();
        foreach (var (trigger, (name, args)) in calls)
        {
            bool known = trigger switch
            {
                "F" or "K" => ReadFormat(name, args, b),
                "V" => name == "AFRange_Validate" && ReadRange(args, b),
                "C" => name == "AFSimple_Calculate" && ReadCalculate(args, b),
                _ => false,
            };
            if (!known) unsupported.Add(trigger);
        }
        return new PdfFieldBehaviour
        {
            Format = b.Format, Decimals = b.Decimals, SeparatorStyle = b.Sep, NegativeStyle = b.Neg, Currency = b.Currency,
            CurrencyPrepend = b.CurrencyPrepend, PercentPrepend = b.PercentPrepend, Pattern = b.Pattern, Special = b.Special,
            Mask = b.Mask, RangeMin = b.Min, RangeMax = b.Max, CalculateOperation = b.Op, CalculateFields = b.Fields,
            CalculateExpression = sfn, Unsupported = unsupported,
        };
    }

    private sealed class Builder
    {
        public PdfFieldFormatKind Format;
        public int Decimals, Sep, Neg, Special;
        public string Currency = string.Empty, Pattern = string.Empty, Mask = string.Empty;
        public bool CurrencyPrepend = true, PercentPrepend;
        public double? Min, Max;
        public string? Op;
        public List<string> Fields = new();
    }

    private static bool ReadFormat(string name, List<object?> a, Builder b)
    {
        int I(int i, int d = 0) => i < a.Count && a[i] is double v ? (int)v : d;
        string S(int i) => i < a.Count && a[i] is string s ? s : string.Empty;
        bool B(int i, bool d) => i < a.Count ? a[i] is bool v ? v : a[i] is double n ? n != 0 : d : d;
        switch (name)
        {
            case "AFNumber_Format" or "AFNumber_Keystroke":
                if (b.Format == PdfFieldFormatKind.None || name == "AFNumber_Format")
                {
                    b.Format = PdfFieldFormatKind.Number;
                    b.Decimals = Math.Clamp(I(0, 2), 0, 12); b.Sep = I(1); b.Neg = I(2); b.Currency = S(4); b.CurrencyPrepend = B(5, true);
                }
                return true;
            case "AFPercent_Format" or "AFPercent_Keystroke":
                b.Format = PdfFieldFormatKind.Percent;
                b.Decimals = Math.Clamp(I(0, 2), 0, 12); b.Sep = I(1); b.PercentPrepend = B(2, false);
                return true;
            case "AFDate_FormatEx" or "AFDate_KeystrokeEx":
                b.Format = PdfFieldFormatKind.Date; b.Pattern = S(0).Length > 0 ? S(0) : "mm/dd/yyyy";
                return true;
            case "AFDate_Format" or "AFDate_Keystroke":
                b.Format = PdfFieldFormatKind.Date; b.Pattern = DateFormats[Math.Clamp(I(0), 0, DateFormats.Length - 1)];
                return true;
            case "AFTime_FormatEx" or "AFTime_KeystrokeEx":
                b.Format = PdfFieldFormatKind.Time; b.Pattern = S(0).Length > 0 ? S(0) : "HH:MM";
                return true;
            case "AFTime_Format" or "AFTime_Keystroke":
                b.Format = PdfFieldFormatKind.Time; b.Pattern = TimeFormats[Math.Clamp(I(0), 0, TimeFormats.Length - 1)];
                return true;
            case "AFSpecial_Format" or "AFSpecial_Keystroke":
                if (b.Format != PdfFieldFormatKind.Mask) { b.Format = PdfFieldFormatKind.Special; b.Special = Math.Clamp(I(0), 0, 3); }
                return true;
            case "AFSpecial_KeystrokeEx":
                b.Format = PdfFieldFormatKind.Mask; b.Mask = S(0);
                return true;
            default:
                return false;
        }
    }

    private static bool ReadRange(List<object?> a, Builder b)
    {
        bool Flag(int i) => i < a.Count && (a[i] is true || a[i] is double d && d != 0);
        double? Num(int i) => i < a.Count && a[i] is double d ? d : null;
        if (Flag(0)) b.Min = Num(1);
        if (Flag(2)) b.Max = Num(3);
        return true;
    }

    private static bool ReadCalculate(List<object?> a, Builder b)
    {
        if (a.Count < 2 || a[0] is not string op || op.ToUpperInvariant() is not ("SUM" or "AVG" or "PRD" or "MIN" or "MAX")) return false;
        b.Op = op.ToUpperInvariant();
        b.Fields = a[1] switch
        {
            List<object?> list => list.OfType<string>().ToList(),
            string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            _ => new List<string>(),
        };
        return b.Fields.Count > 0;
    }

    // ------------------------------------------------------------------ script parsing

    /// <summary>The single AF call a script consists of, or null when it is anything more.</summary>
    internal static (string Name, List<object?> Args)? ParseCall(string script)
    {
        string s = StripComments(script).Trim();
        var m = Regex.Match(s, @"^(AF\w+)\s*\(");
        if (!m.Success) return null;
        int pos = m.Length;
        var args = new List<object?>();
        if (!ParseArgs(s, ref pos, ')', args)) return null;
        string rest = s[pos..].Trim().TrimEnd(';').Trim();
        return rest.Length == 0 ? (m.Groups[1].Value, args) : null;
    }

    private static string StripComments(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(s, @"//[^\n\r]*", " ");
    }

    private static bool ParseArgs(string s, ref int pos, char close, List<object?> into)
    {
        while (true)
        {
            Skip(s, ref pos);
            if (pos >= s.Length) return false;
            if (s[pos] == close) { pos++; return true; }
            if (!ParseValue(s, ref pos, out var value)) return false;
            into.Add(value);
            Skip(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; continue; }
            if (pos < s.Length && s[pos] == close) { pos++; return true; }
            return false;
        }
    }

    private static void Skip(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private static bool ParseValue(string s, ref int pos, out object? value)
    {
        value = null;
        Skip(s, ref pos);
        if (pos >= s.Length) return false;
        char c = s[pos];
        if (c is '"' or '\'')
        {
            var sb = new StringBuilder();
            for (pos++; pos < s.Length && s[pos] != c; pos++)
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    pos++;
                    sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => s[pos] });
                }
                else sb.Append(s[pos]);
            }
            if (pos >= s.Length) return false;
            pos++;
            value = sb.ToString();
            return true;
        }
        if (c == '[')
        {
            pos++;
            var list = new List<object?>();
            if (!ParseArgs(s, ref pos, ']', list)) return false;
            value = list;
            return true;
        }
        if (Regex.Match(s[pos..], @"^new\s+Array\s*\(") is { Success: true } arr)
        {
            pos += arr.Length;
            var list = new List<object?>();
            if (!ParseArgs(s, ref pos, ')', list)) return false;
            value = list;
            return true;
        }
        if (Regex.Match(s[pos..], @"^(true|false)\b") is { Success: true } bm)
        {
            pos += bm.Length;
            value = bm.Value == "true";
            return true;
        }
        if (Regex.Match(s[pos..], @"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?") is { Success: true } nm)
        {
            pos += nm.Length;
            value = double.Parse(nm.Value, CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ numbers

    /// <summary>A number as typed or stored: invariant first, then by the field's separator style (AFMakeNumber).</summary>
    public static decimal? ParseNumber(string text, int separatorStyle = 0)
    {
        string t = text.Trim();
        if (t.Length == 0) return null;
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var inv)) return inv;
        bool negative = t.Contains('-') || (t.Contains('(') && t.Contains(')'));
        bool commaDecimal = separatorStyle is 2 or 3;
        var sb = new StringBuilder();
        foreach (char c in t)
        {
            if (char.IsDigit(c)) sb.Append(c);
            else if (c == (commaDecimal ? ',' : '.')) sb.Append('.');
        }
        string digits = sb.ToString();
        if (digits.Count(ch => ch == '.') > 1 || digits.Trim('.').Length == 0) return null;
        return decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? negative ? -v : v : null;
    }

    public static string FormatNumber(decimal value, int decimals, int separatorStyle)
    {
        decimal rounded = Math.Round(Math.Abs(value), decimals, MidpointRounding.AwayFromZero);
        string fixedText = rounded.ToString("F" + decimals, CultureInfo.InvariantCulture);
        string intPart = fixedText, frac = string.Empty;
        int dot = fixedText.IndexOf('.');
        if (dot >= 0) { intPart = fixedText[..dot]; frac = fixedText[(dot + 1)..]; }
        string group = separatorStyle switch { 0 => ",", 2 => ".", 4 => "'", _ => string.Empty };
        string point = separatorStyle is 2 or 3 ? "," : ".";
        if (group.Length > 0)
        {
            var g = new StringBuilder();
            for (int i = 0; i < intPart.Length; i++)
            {
                if (i > 0 && (intPart.Length - i) % 3 == 0) g.Append(group);
                g.Append(intPart[i]);
            }
            intPart = g.ToString();
        }
        return frac.Length > 0 ? intPart + point + frac : intPart;
    }

    /// <summary>The field's value as it shows: formatted by its format script, or as it is.</summary>
    public static PdfFieldDisplay Display(PdfFieldBehaviour b, string value)
    {
        if (value.Length == 0) return new PdfFieldDisplay(value, false);
        switch (b.Format)
        {
            case PdfFieldFormatKind.Number when ParseNumber(value, b.SeparatorStyle) is decimal n:
            {
                bool negative = n < 0 && Math.Round(n, b.Decimals, MidpointRounding.AwayFromZero) != 0;
                string body = FormatNumber(n, b.Decimals, b.SeparatorStyle);
                if (b.Currency.Length > 0) body = b.CurrencyPrepend ? b.Currency + body : body + b.Currency;
                if (!negative) return new PdfFieldDisplay(body, false);
                return b.NegativeStyle switch
                {
                    1 => new PdfFieldDisplay(body, true),
                    2 => new PdfFieldDisplay("(" + body + ")", false),
                    3 => new PdfFieldDisplay("(" + body + ")", true),
                    _ => new PdfFieldDisplay("-" + body, false),
                };
            }
            case PdfFieldFormatKind.Percent when ParseNumber(value, b.SeparatorStyle) is decimal p:
            {
                string body = (p < 0 ? "-" : string.Empty) + FormatNumber(p * 100, b.Decimals, b.SeparatorStyle);
                return new PdfFieldDisplay(b.PercentPrepend ? "%" + body : body + "%", false);
            }
            case PdfFieldFormatKind.Date or PdfFieldFormatKind.Time when ParseDate(value, b.Pattern) is DateTime d:
                return new PdfFieldDisplay(FormatDate(d, b.Pattern), false);
            case PdfFieldFormatKind.Special:
                return new PdfFieldDisplay(FormatSpecial(value, b.Special), false);
            default:
                return new PdfFieldDisplay(value, false);
        }
    }

    /// <summary>
    /// A value typed into the field, checked as its keystroke and validate scripts would on
    /// commit: the value to store, or why it is refused.
    /// </summary>
    public static (string? Value, string? Error) Accept(PdfFieldBehaviour b, string input)
    {
        string text = input.Trim();
        if (text.Length == 0) return (string.Empty, null);
        string stored = input;
        decimal? number = null;
        switch (b.Format)
        {
            case PdfFieldFormatKind.Number or PdfFieldFormatKind.Percent:
            {
                string t = text;
                if (b.Currency.Length > 0) t = t.Replace(b.Currency, string.Empty);
                t = t.Replace("%", string.Empty);
                number = ParseNumber(t, b.SeparatorStyle);
                if (number == null || Regex.IsMatch(t, @"[^\d\s.,'()\-+]"))
                    return (null, "The value entered does not match the format of the field: enter a number.");
                if (b.Format == PdfFieldFormatKind.Percent && text.Contains('%')) number /= 100;
                stored = Canonical(number.Value);
                break;
            }
            case PdfFieldFormatKind.Date or PdfFieldFormatKind.Time:
                if (ParseDate(text, b.Pattern) == null)
                    return (null, $"The value entered does not match the format of the field: {b.Pattern}.");
                break;
            case PdfFieldFormatKind.Special:
            {
                int digits = text.Count(char.IsDigit);
                int[] allowed = b.Special switch { 0 => new[] { 5 }, 1 => new[] { 9 }, 2 => new[] { 7, 10 }, _ => new[] { 9 } };
                if (!allowed.Contains(digits) || Regex.IsMatch(text, @"[^\d\s()\-.]"))
                    return (null, "The value entered does not match the format of the field: " + b.Special switch
                    {
                        0 => "a 5-digit ZIP code.", 1 => "a ZIP+4 code (99999-9999).", 2 => "a phone number.", _ => "a Social Security number (999-99-9999).",
                    });
                break;
            }
            case PdfFieldFormatKind.Mask when b.Mask.Length > 0 && !MatchesMask(text, b.Mask):
                return (null, $"The value entered does not match the format of the field: {b.Mask}.");
        }
        if (b.RangeMin != null || b.RangeMax != null)
        {
            var v = number ?? ParseNumber(text, b.SeparatorStyle);
            if (v is decimal x && ((b.RangeMin is double lo && (double)x < lo) || (b.RangeMax is double hi && (double)x > hi)))
            {
                string range = (b.RangeMin, b.RangeMax) switch
                {
                    (double lo2, double hi2) => $"greater than or equal to {lo2.ToString(CultureInfo.InvariantCulture)} and less than or equal to {hi2.ToString(CultureInfo.InvariantCulture)}",
                    (double lo2, null) => $"greater than or equal to {lo2.ToString(CultureInfo.InvariantCulture)}",
                    (null, double hi2) => $"less than or equal to {hi2.ToString(CultureInfo.InvariantCulture)}",
                    _ => string.Empty,
                };
                return (null, $"Invalid value: must be {range}.");
            }
        }
        return (stored, null);
    }

    /// <summary>Whether a character may be typed into the field (its keystroke script's rule).</summary>
    public static bool AllowsCharacter(PdfFieldBehaviour b, char c) => b.Format switch
    {
        PdfFieldFormatKind.Number => char.IsDigit(c) || c is '.' or ',' or '-' or '\'' or ' ' || b.Currency.Contains(c),
        PdfFieldFormatKind.Percent => char.IsDigit(c) || c is '.' or ',' or '-' or '%' or ' ',
        PdfFieldFormatKind.Special => char.IsDigit(c) || c is '-' or '(' or ')' or ' ' or '.',
        _ => true,
    };

    private static bool MatchesMask(string text, string mask)
    {
        if (text.Length != mask.Length) return false;
        for (int i = 0; i < mask.Length; i++)
        {
            char m = mask[i], c = text[i];
            bool ok = m switch
            {
                '9' => char.IsDigit(c),
                'A' => char.IsLetter(c),
                'O' => char.IsLetterOrDigit(c),
                'X' => true,
                _ => c == m,
            };
            if (!ok) return false;
        }
        return true;
    }

    private static string FormatSpecial(string value, int special)
    {
        string d = new(value.Where(char.IsDigit).ToArray());
        return special switch
        {
            0 when d.Length == 5 => d,
            1 when d.Length == 9 => d[..5] + "-" + d[5..],
            2 when d.Length == 10 => $"({d[..3]}) {d[3..6]}-{d[6..]}",
            2 when d.Length == 7 => $"{d[..3]}-{d[3..]}",
            3 when d.Length == 9 => $"{d[..3]}-{d[3..5]}-{d[5..]}",
            _ => value,
        };
    }

    // ------------------------------------------------------------------ dates

    /// <summary>A date typed in any reasonable way, read in the order the pattern puts day, month and year (AFParseDateEx).</summary>
    public static DateTime? ParseDate(string text, string pattern)
    {
        var tokens = Regex.Matches(text, @"\d+|[A-Za-z]+").Select(m => m.Value).ToList();
        if (tokens.Count == 0) return null;
        int? month = null;
        bool? pm = null;
        var numbers = new List<int>();
        foreach (var t in tokens)
        {
            if (char.IsDigit(t[0]))
            {
                if (t.Length > 8 || !int.TryParse(t, out int n)) return null;
                numbers.Add(n);
                continue;
            }
            string lower = t.ToLowerInvariant();
            if (lower is "am" or "a") { pm = false; continue; }
            if (lower is "pm" or "p") { pm = true; continue; }
            int mi = Array.FindIndex(MonthNames, name => lower.Length >= 3 && name.StartsWith(lower, StringComparison.OrdinalIgnoreCase));
            if (mi >= 0) { month = mi + 1; continue; }
            if (DayNames.Any(name => lower.Length >= 3 && name.StartsWith(lower, StringComparison.OrdinalIgnoreCase))) continue;
            return null; // a word that is neither a month, a day nor am/pm
        }

        // The order of day, month and year in the pattern (minutes are MM, uppercase).
        var order = new List<char>();
        foreach (char c in pattern)
        {
            char k = c switch { 'd' => 'd', 'm' => 'm', 'y' => 'y', _ => '\0' };
            if (k != '\0' && !order.Contains(k)) order.Add(k);
        }
        bool hasDate = order.Count > 0;
        bool hasTime = pattern.Contains('H') || pattern.Contains('h') || pattern.Contains("MM");
        int day = 1, year = DateTime.Today.Year, hour = 0, minute = 0, second = 0;
        int idx = 0;
        if (hasDate)
        {
            if (month != null) order.Remove('m');
            foreach (char k in order)
            {
                if (idx >= numbers.Count) break;
                int n = numbers[idx++];
                switch (k)
                {
                    case 'd': day = n; break;
                    case 'm': month = n; break;
                    case 'y': year = n < 100 ? (n < 50 ? 2000 + n : 1900 + n) : n; break;
                }
            }
            if (month == null) return null;
            if (!pattern.Contains('d')) day = 1;
        }
        if (hasTime)
        {
            if (idx < numbers.Count) hour = numbers[idx++];
            if (idx < numbers.Count) minute = numbers[idx++];
            if (idx < numbers.Count) second = numbers[idx++];
            if (pm == true && hour < 12) hour += 12;
            if (pm == false && hour == 12) hour = 0;
        }
        if (idx < numbers.Count) return null; // numbers left over: not this format
        try
        {
            return hasDate
                ? new DateTime(year, month ?? 1, day, hour, minute, second)
                : new DateTime(2000, 1, 1, hour, minute, second);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string FormatDate(DateTime d, string pattern)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            int run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;
            string piece = (c, run) switch
            {
                ('d', 1) => d.Day.ToString(CultureInfo.InvariantCulture),
                ('d', 2) => d.Day.ToString("00", CultureInfo.InvariantCulture),
                ('d', 3) => DayNames[(int)d.DayOfWeek][..3],
                ('d', _) => DayNames[(int)d.DayOfWeek],
                ('m', 1) => d.Month.ToString(CultureInfo.InvariantCulture),
                ('m', 2) => d.Month.ToString("00", CultureInfo.InvariantCulture),
                ('m', 3) => MonthNames[d.Month - 1][..3],
                ('m', _) => MonthNames[d.Month - 1],
                ('y', 2) => (d.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                ('y', _) => d.Year.ToString("0000", CultureInfo.InvariantCulture),
                ('H', 1) => d.Hour.ToString(CultureInfo.InvariantCulture),
                ('H', _) => d.Hour.ToString("00", CultureInfo.InvariantCulture),
                ('h', 1) => (d.Hour % 12 == 0 ? 12 : d.Hour % 12).ToString(CultureInfo.InvariantCulture),
                ('h', _) => (d.Hour % 12 == 0 ? 12 : d.Hour % 12).ToString("00", CultureInfo.InvariantCulture),
                ('M', 1) => d.Minute.ToString(CultureInfo.InvariantCulture),
                ('M', _) => d.Minute.ToString("00", CultureInfo.InvariantCulture),
                ('s', 1) => d.Second.ToString(CultureInfo.InvariantCulture),
                ('s', _) => d.Second.ToString("00", CultureInfo.InvariantCulture),
                ('t', 1) => d.Hour < 12 ? "a" : "p",
                ('t', _) => d.Hour < 12 ? "am" : "pm",
                _ => new string(c, run),
            };
            sb.Append(piece);
            i += run;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ committing

    /// <summary>
    /// The changes to write for what the user entered: each typed value checked by the field's
    /// keystroke and validate rules (a refusal throws), stored in its canonical form and shown
    /// through its format; then every calculated field recalculated, in calculation order, and
    /// formatted the same way. Fields whose scripts are custom code are filled as typed.
    /// </summary>
    public static List<PdfFieldChange> Prepare(PdfAcroForm form, IEnumerable<PdfFieldChange> changes)
    {
        var result = new List<PdfFieldChange>();
        var values = form.Fields.GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var field = form[change.FullName];
            if (field == null || field.Kind != PdfFormFieldKind.Text || change.Text == null)
            {
                result.Add(change);
                if (field != null && (change.Text ?? change.Choice) is { } v) values[field.FullName] = v;
                continue;
            }
            var behaviour = Read(field);
            var (stored, error) = Accept(behaviour, change.Text);
            if (error != null) throw new PdfFieldValidationException(field.FullName, error);
            var display = Display(behaviour, stored!);
            values[field.FullName] = stored!;
            result.Add(change with { Text = stored, Display = display.Text != stored ? display.Text : null, DisplayRed = display.Red });
        }

        foreach (var (name, value) in Recalculate(form, values))
        {
            var field = form[name];
            if (field == null || field.Kind != PdfFormFieldKind.Text) continue;
            var display = Display(Read(field), value);
            result.RemoveAll(c => c.FullName == name);
            result.Add(new PdfFieldChange(name, Text: value, Display: display.Text != value ? display.Text : null, DisplayRed: display.Red));
        }
        return result;
    }

    // ------------------------------------------------------------------ calculation

    /// <summary>
    /// Recalculates every calculated field in calculation order, each seeing the results of the
    /// ones before it. Returns the fields whose value changed, with the new value.
    /// </summary>
    public static IReadOnlyList<(string Field, string Value)> Recalculate(PdfAcroForm form, IReadOnlyDictionary<string, string> values)
    {
        var current = new Dictionary<string, string>(values, StringComparer.Ordinal);
        var changes = new List<(string, string)>();
        var behaviours = form.Fields.GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => Read(g.First()));
        foreach (var name in form.CalculationOrder)
        {
            if (!behaviours.TryGetValue(name, out var b) || !b.Calculates) continue;
            decimal? result = b.CalculateExpression != null
                ? Evaluate(b.CalculateExpression, n => Values(form, current, behaviours, n))
                : Aggregate(b.CalculateOperation!, b.CalculateFields.SelectMany(n => Values(form, current, behaviours, n)).ToList());
            string text = result is decimal r ? Canonical(r) : string.Empty;
            current.TryGetValue(name, out var old);
            if (text != (old ?? string.Empty))
            {
                current[name] = text;
                changes.Add((name, text));
            }
        }
        return changes;
    }

    /// <summary>A number as JavaScript would print it, but exact: no trailing zeros, no binary noise.</summary>
    public static string Canonical(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>The numbers behind a name: the field's own, or every terminal field under a group name.</summary>
    private static IEnumerable<decimal> Values(PdfAcroForm form, Dictionary<string, string> current, Dictionary<string, PdfFieldBehaviour> behaviours, string name)
    {
        var names = form.Fields.Select(f => f.FullName).Where(n => n == name || n.StartsWith(name + ".", StringComparison.Ordinal)).Distinct();
        foreach (var n in names)
        {
            current.TryGetValue(n, out var v);
            int sep = behaviours.TryGetValue(n, out var b) ? b.SeparatorStyle : 0;
            yield return ParseNumber(v ?? string.Empty, sep) ?? 0; // an empty or non-numeric field counts as 0 (AFMakeNumber)
        }
    }

    private static decimal? Aggregate(string op, List<decimal> values)
    {
        if (values.Count == 0) return op is "SUM" or "AVG" ? 0 : null;
        try
        {
            return op switch
            {
                "SUM" => values.Sum(),
                "AVG" => values.Sum() / values.Count,
                "PRD" => values.Aggregate(1m, (a, v) => a * v),
                "MIN" => values.Min(),
                "MAX" => values.Max(),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Simplified field notation (Acrobat's "Simplified field notation" calculations): numbers,
    /// field names (dots allowed; a backslash escapes other characters), + - * / and parentheses.
    /// Division by zero gives an empty result, as Acrobat shows nothing.
    /// </summary>
    internal static decimal? Evaluate(string expression, Func<string, IEnumerable<decimal>> field)
    {
        int pos = 0;
        try
        {
            decimal v = Expr();
            Space();
            return pos == expression.Length ? v : null;
        }
        catch (Exception ex) when (ex is DivideByZeroException or OverflowException or FormatException)
        {
            return null;
        }

        void Space() { while (pos < expression.Length && char.IsWhiteSpace(expression[pos])) pos++; }
        decimal Expr()
        {
            decimal v = Term();
            while (true)
            {
                Space();
                if (pos < expression.Length && expression[pos] == '+') { pos++; v += Term(); }
                else if (pos < expression.Length && expression[pos] == '-') { pos++; v -= Term(); }
                else return v;
            }
        }
        decimal Term()
        {
            decimal v = Factor();
            while (true)
            {
                Space();
                if (pos < expression.Length && expression[pos] == '*') { pos++; v *= Factor(); }
                else if (pos < expression.Length && expression[pos] == '/') { pos++; v /= Factor(); }
                else return v;
            }
        }
        decimal Factor()
        {
            Space();
            if (pos >= expression.Length) throw new FormatException();
            char c = expression[pos];
            if (c == '-') { pos++; return -Factor(); }
            if (c == '+') { pos++; return Factor(); }
            if (c == '(')
            {
                pos++;
                decimal v = Expr();
                Space();
                if (pos >= expression.Length || expression[pos] != ')') throw new FormatException();
                pos++;
                return v;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = pos;
                while (pos < expression.Length && (char.IsDigit(expression[pos]) || expression[pos] == '.')) pos++;
                return decimal.Parse(expression[start..pos], CultureInfo.InvariantCulture);
            }
            var name = new StringBuilder();
            while (pos < expression.Length)
            {
                char ch = expression[pos];
                if (ch == '\\' && pos + 1 < expression.Length) { name.Append(expression[pos + 1]); pos += 2; continue; }
                if (char.IsLetterOrDigit(ch) || ch is '_' or '.') { name.Append(ch); pos++; continue; }
                break;
            }
            if (name.Length == 0) throw new FormatException();
            return field(name.ToString()).Sum();
        }
    }
}
