using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfEngine.Vector.Export;

internal enum CellKind { Text, Number, Date }

/// <summary>Which part of a date written with numbers (3/4/2024) is the day, as a table's column shows it.</summary>
internal enum DateOrder { Unknown, MonthFirst, DayFirst }

/// <summary>A cell's value as Excel keeps it: a number with the format that shows it as the page did, or text.</summary>
internal readonly record struct CellValue(CellKind Kind, double Number, string Format)
{
    public static readonly CellValue Text = new(CellKind.Text, 0, string.Empty);
}

/// <summary>
/// Recognizes numbers and dates in a cell's text: thousands separators, decimals, negatives
/// with a minus or in parentheses, currency and percent (kept as number formats), and dates
/// whose reading is not in doubt. Anything else, and numbers that are really codes (leading
/// zeros, more digits than Excel keeps), stays text.
/// </summary>
internal static class CellValues
{
    private static readonly Regex NumberPattern = new(
        @"^(?<open>\()?\s*(?<sign>[-+])?\s*(?<cur>[$€£¥])?\s*(?<sign2>-)?\s*(?<num>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d{1,3}(?:\.\d{3})+,\d+|\d+(?:\.\d+)?|\.\d+)\s*(?<pct>%)?\s*(?<cur2>[€£¥]|kr)?\s*(?<close>\))?\s*(?<trail>-)?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex IsoDate = new(@"^(\d{4})-(\d{1,2})-(\d{1,2})$", RegexOptions.CultureInvariant);
    private static readonly Regex NumericDate = new(@"^(\d{1,2})([/.-])(\d{1,2})\2(\d{4})$", RegexOptions.CultureInvariant);
    // March 5, 2024 / Mar. 5 2024
    private static readonly Regex MonthFirstName = new(@"^([A-Za-z]{3,9})\.?\s+(\d{1,2}),?\s+(\d{4})$", RegexOptions.CultureInvariant);
    // 5 March 2024 / 5-Mar-2024
    private static readonly Regex DayFirstName = new(@"^(\d{1,2})([\s-])([A-Za-z]{3,9})\.?\2(\d{4})$", RegexOptions.CultureInvariant);

    private static readonly string[] Months = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    /// <summary>What a numeric date says about its column's order: the part over 12 is the day.</summary>
    public static DateOrder Evidence(string text)
    {
        var m = NumericDate.Match(Normalize(text));
        if (!m.Success) return DateOrder.Unknown;
        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (a > 12 && b <= 12) return DateOrder.DayFirst;
        if (b > 12 && a <= 12) return DateOrder.MonthFirst;
        return DateOrder.Unknown;
    }

    public static CellValue Parse(string text, DateOrder order = DateOrder.Unknown)
    {
        string s = Normalize(text);
        if (s.Length == 0 || s.Length > 40) return CellValue.Text;
        if (char.IsDigit(s[^1]) || s[^1] is '%' or ')' or '-' or '€' or '£' or '¥' or 'r')
        {
            var number = Number(s);
            if (number.Kind != CellKind.Text) return number;
        }
        return Date(s, order);
    }

    private static string Normalize(string text) =>
        text.Trim().Replace('−', '-').Replace('–', '-').Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');

    private static CellValue Number(string s)
    {
        var m = NumberPattern.Match(s);
        if (!m.Success) return CellValue.Text;
        bool open = m.Groups["open"].Success, close = m.Groups["close"].Success;
        if (open != close) return CellValue.Text;
        int signs = (m.Groups["sign"].Success ? 1 : 0) + (m.Groups["sign2"].Success ? 1 : 0) + (m.Groups["trail"].Success ? 1 : 0);
        if (signs > 1 || open && signs > 0) return CellValue.Text;
        string currency = m.Groups["cur"].Success ? m.Groups["cur"].Value : m.Groups["cur2"].Success ? m.Groups["cur2"].Value : string.Empty;
        if (m.Groups["cur"].Success && m.Groups["cur2"].Success) return CellValue.Text;
        bool percent = m.Groups["pct"].Success;
        if (percent && currency.Length > 0) return CellValue.Text;

        string num = m.Groups["num"].Value;
        bool european = num.Contains(',') && num.Contains('.') && num.LastIndexOf(',') > num.LastIndexOf('.');
        bool thousands = european || num.Contains(',');
        string plain = european ? num.Replace(".", string.Empty).Replace(',', '.') : num.Replace(",", string.Empty);
        // Codes, not quantities: a leading zero (a ZIP code, an account number) or more digits than Excel keeps.
        string whole = plain.Split('.')[0];
        if (whole.Length > 1 && whole[0] == '0' || plain.Replace(".", string.Empty).Length > 15) return CellValue.Text;
        if (!double.TryParse(plain, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)) return CellValue.Text;
        int decimals = plain.Contains('.') ? plain.Length - plain.IndexOf('.') - 1 : 0;
        bool negative = open || m.Groups["sign"].Value == "-" || m.Groups["sign2"].Success || m.Groups["trail"].Success;
        if (negative) value = -value;
        if (percent) value /= 100;

        string body = (thousands ? "#,##0" : "0") + (decimals > 0 ? "." + new string('0', Math.Min(decimals, 10)) : string.Empty);
        if (percent) body += "%";
        if (currency.Length > 0)
            body = m.Groups["cur"].Success ? $"\"{currency}\"{body}" : $"{body}\\ \"{currency}\"";
        string format = open ? $"{body};\\({body}\\)" : body;
        // A plain number Excel shows the same way on its own needs no format (2.50 does: its zero).
        if (!thousands && !percent && currency.Length == 0 && !open && (decimals == 0 || plain[^1] != '0' && decimals <= 9))
            format = string.Empty;
        return new CellValue(CellKind.Number, value, format);
    }

    private static CellValue Date(string s, DateOrder order)
    {
        int year, month, day;
        string format;
        Match m;
        if ((m = IsoDate.Match(s)).Success)
        {
            year = Int(m.Groups[1]); month = Int(m.Groups[2]); day = Int(m.Groups[3]);
            format = "yyyy\\-mm\\-dd";
        }
        else if ((m = NumericDate.Match(s)).Success)
        {
            int a = Int(m.Groups[1]), b = Int(m.Groups[3]);
            year = Int(m.Groups[4]);
            var own = a > 12 && b <= 12 ? DateOrder.DayFirst : b > 12 && a <= 12 ? DateOrder.MonthFirst : order;
            if (own == DateOrder.Unknown) return CellValue.Text;
            (month, day) = own == DateOrder.MonthFirst ? (a, b) : (b, a);
            string sep = m.Groups[2].Value == "/" ? "/" : "\\" + m.Groups[2].Value;
            bool monthFirst = own == DateOrder.MonthFirst;
            // Written with leading zeros (03/04): shown with them.
            static string Part(char unit, Group g) => new(unit, g.Value.Length == 2 ? 2 : 1);
            format = Part(monthFirst ? 'm' : 'd', m.Groups[1]) + sep + Part(monthFirst ? 'd' : 'm', m.Groups[3]) + sep + "yyyy";
        }
        else if ((m = MonthFirstName.Match(s)).Success)
        {
            month = Month(m.Groups[1].Value, out bool full);
            day = Int(m.Groups[2]); year = Int(m.Groups[3]);
            format = (full ? "mmmm" : "mmm") + " d" + (s.Contains(',') ? "," : string.Empty) + " yyyy";
        }
        else if ((m = DayFirstName.Match(s)).Success)
        {
            day = Int(m.Groups[1]); month = Month(m.Groups[3].Value, out bool full); year = Int(m.Groups[4]);
            string sep = m.Groups[2].Value == "-" ? "\\-" : " ";
            format = "d" + sep + (full ? "mmmm" : "mmm") + sep + "yyyy";
        }
        else return CellValue.Text;
        // Excel's serial numbers are right from March 1900 (it counts a 29 February 1900 that never was).
        if (month < 1 || year < 1901 || year > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month)) return CellValue.Text;
        double serial = (new DateTime(year, month, day) - new DateTime(1899, 12, 30)).TotalDays;
        return new CellValue(CellKind.Date, serial, format);
    }

    private static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);

    /// <summary>A month's number from its name or a three-letter (or "Sept") abbreviation; 0 when it is not one.</summary>
    private static int Month(string name, out bool full)
    {
        string n = name.ToLowerInvariant();
        full = false;
        for (int i = 0; i < Months.Length; i++)
        {
            if (n == Months[i]) { full = n.Length > 3; return i + 1; }
            if (n == Months[i][..3] || n == "sept" && i == 8) return i + 1;
        }
        return 0;
    }
}
