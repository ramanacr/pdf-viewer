using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PdfEngine.Vector.Fonts;

namespace PdfEngine.Vector.Text;

/// <summary>
/// A /ToUnicode CMap read the way PDFium reads it for text extraction (CPDF_ToUnicodeMap), which
/// is stricter than the CMap reader used for rendering: a bfchar or bfrange section counts only
/// when it has exactly the number of entries its count says and every entry is well formed; a
/// range never crosses a 256-code block (the high code's last byte is taken with the low code's
/// other bytes); destinations are read four hex digits per UTF-16 unit; when a code is mapped more
/// than once, the smallest value wins. An Adobe UCS2 CMap name makes that collection's CID table
/// the fallback.
/// </summary>
internal sealed class ToUnicodeReader
{
    private const int CidLimit = 0xFFFF;
    private const int OutOfSpecLimit = 160000;

    private readonly Dictionary<int, uint> _map = new();
    private readonly List<string> _multi = new();
    private string? _baseOrdering;

    public int Count => _map.Count;

    public static ToUnicodeReader Parse(byte[] data)
    {
        var r = new ToUnicodeReader();
        try
        {
            r.Load(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
        }
        return r;
    }

    /// <summary>The text for a code; empty when it has none.</summary>
    public string Lookup(int code)
    {
        if (!_map.TryGetValue(code, out uint value))
        {
            if (_baseOrdering != null && PredefinedCMaps.CidToUnicode(_baseOrdering, code & 0xFFFF) is { } fromBase)
                return fromBase;
            return string.Empty;
        }
        if ((value & 0xFFFF) != 0xFFFF) return ((char)value).ToString();
        int index = (int)(value >> 16);
        return index < _multi.Count ? _multi[index] : string.Empty;
    }

    /// <summary>The first code mapped to a single character, or 0.</summary>
    public int ReverseLookup(char c)
    {
        int best = -1;
        foreach (var (code, value) in _map)
        {
            if ((value & 0xFFFF) != 0xFFFF && (char)value == c && (best < 0 || code < best)) best = code;
        }
        return best < 0 ? 0 : best;
    }

    private void Insert(int code, uint value)
    {
        if (!_map.TryGetValue(code, out uint existing) || value < existing) _map[code] = value;
    }

    private void SetCode(int code, string dest, List<(int, uint)> pending, List<string> pendingMulti)
    {
        if (dest.Length == 0) return;
        if (dest.Length == 1)
        {
            pending.Add((code, dest[0]));
            return;
        }
        pending.Add((code, (uint)((_multi.Count + pendingMulti.Count) << 16) | 0xFFFF));
        pendingMulti.Add(dest);
    }

    private void Commit(List<(int, uint)> pending, List<string> pendingMulti)
    {
        foreach (var (code, value) in pending) Insert(code, value);
        _multi.AddRange(pendingMulti);
    }

    private void Load(byte[] data)
    {
        var words = new Tokenizer(data);
        string previous = string.Empty;
        while (words.Next() is { } word)
        {
            switch (word)
            {
                case "beginbfchar":
                    BfChar(words, StringToInt(previous));
                    break;
                case "beginbfrange":
                    BfRange(words, StringToInt(previous));
                    break;
                case "/Adobe-Korea1-UCS2": _baseOrdering = "Korea1"; break;
                case "/Adobe-Japan1-UCS2": _baseOrdering = "Japan1"; break;
                case "/Adobe-CNS1-UCS2": _baseOrdering = "CNS1"; break;
                case "/Adobe-GB1-UCS2": _baseOrdering = "GB1"; break;
            }
            previous = word;
        }
    }

    private void BfChar(Tokenizer words, long count)
    {
        bool valid = count is >= 0 and <= OutOfSpecLimit;
        var pending = new List<(int, uint)>();
        var pendingMulti = new List<string>();
        long n = 0;
        while (words.Next() is { } src)
        {
            if (src == "endbfchar") break;
            string? dest = words.Next();
            if (dest == null) break;
            if (!valid) continue;
            if (StringToCode(src) is not long code || code > CidLimit || ++n > count) { valid = false; continue; }
            SetCode((int)code, StringToWideString(dest), pending, pendingMulti);
        }
        if (valid && n == count) Commit(pending, pendingMulti);
    }

    private void BfRange(Tokenizer words, long count)
    {
        bool valid = count is >= 0 and <= OutOfSpecLimit;
        var pending = new List<(int, uint)>();
        var pendingMulti = new List<string>();
        long n = 0;
        while (words.Next() is { } lowWord)
        {
            if (lowWord == "endbfrange") break;
            string? highWord = words.Next();
            if (highWord == null) break;
            long? low = StringToCode(lowWord), highToken = StringToCode(highWord);
            string? start = words.Next();
            if (start == null) break;
            bool entryValid = valid && low.HasValue && highToken.HasValue;
            long lowCode = low ?? 0;
            long high = (lowCode & 0xFFFFFF00) | ((highToken ?? 0) & 0xFF);
            if (entryValid && (lowCode > CidLimit || high > CidLimit || lowCode > high)) entryValid = false;
            if (entryValid && ++n > count) entryValid = false;
            if (start == "[")
            {
                // One destination per code of the range, then the closing bracket; anything else
                // (a range wider than its 256-code block, say) invalidates the section.
                var items = new List<string>();
                string? d;
                while ((d = words.Next()) != null && d != "]") items.Add(d);
                if (entryValid && items.Count == high - lowCode + 1)
                {
                    for (int k = 0; k < items.Count; k++)
                        SetCode((int)(lowCode + k), StringToWideString(items[k]), pending, pendingMulti);
                }
                else valid = false;
                continue;
            }
            if (!entryValid) { valid = false; continue; }
            string destination = StringToWideString(start);
            if (destination.Length == 1)
            {
                uint value = StringToCode(start) is long v ? (uint)v : destination[0];
                for (long code = lowCode; code <= high; code++)
                    pending.Add(((int)code, value++));
            }
            else
            {
                var current = destination.ToCharArray();
                for (long code = lowCode; code <= high; code++)
                {
                    if (current.Length > 0)
                    {
                        SetCode((int)code, new string(current), pending, pendingMulti);
                        Increment(current);
                    }
                }
            }
        }
        if (valid && n == count) Commit(pending, pendingMulti);
    }

    /// <summary>Adds one to the last UTF-16 unit, carrying into the ones before.</summary>
    private static void Increment(char[] s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] != '￿') { s[i]++; return; }
            s[i] = '\0';
        }
    }

    private static long StringToInt(string s)
    {
        int i = 0;
        bool negative = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { negative = s[i] == '-'; i++; }
        long v = 0;
        for (; i < s.Length && s[i] is >= '0' and <= '9'; i++)
        {
            v = v * 10 + (s[i] - '0');
            if (v > int.MaxValue) return int.MaxValue;
        }
        return negative ? -v : v;
    }

    private static long? StringToCode(string s)
    {
        if (s.Length <= 2 || s[0] != '<' || s[^1] != '>') return null;
        uint v = 0;
        for (int i = 1; i < s.Length - 1; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) continue;
            int d = Hex(c);
            if (d < 0) return null;
            if (v > 0x0FFFFFFF) return null;
            v = (v << 4) | (uint)d;
        }
        return v;
    }

    private static string StringToWideString(string s)
    {
        if (s.Length <= 2 || s[0] != '<' || s[^1] != '>') return string.Empty;
        var sb = new StringBuilder();
        int unit = 0, digits = 0;
        for (int i = 1; i < s.Length - 1; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) continue;
            int d = Hex(c);
            if (d < 0) break;
            unit = (unit << 4) | d;
            if (++digits == 4)
            {
                sb.Append((char)unit);
                unit = 0;
                digits = 0;
            }
        }
        return sb.ToString();
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>The words of a CMap: hex strings, literal strings, names, brackets and plain tokens; comments skipped.</summary>
    private sealed class Tokenizer
    {
        private readonly byte[] _d;
        private int _p;

        public Tokenizer(byte[] d) => _d = d;

        private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        public string? Next()
        {
            while (true)
            {
                while (_p < _d.Length && IsSpace(_d[_p])) _p++;
                if (_p >= _d.Length) return null;
                if (_d[_p] != '%') break;
                while (_p < _d.Length && _d[_p] != '\r' && _d[_p] != '\n') _p++;
            }
            int start = _p;
            byte b = _d[_p];
            if (b == '<')
            {
                if (_p + 1 < _d.Length && _d[_p + 1] == '<') { _p += 2; return "<<"; }
                while (_p < _d.Length && _d[_p] != '>') _p++;
                if (_p < _d.Length) _p++;
                return Encoding.Latin1.GetString(_d, start, _p - start);
            }
            if (b == '>')
            {
                _p += _p + 1 < _d.Length && _d[_p + 1] == '>' ? 2 : 1;
                return Encoding.Latin1.GetString(_d, start, _p - start);
            }
            if (b == '(')
            {
                int depth = 0;
                while (_p < _d.Length)
                {
                    byte c = _d[_p++];
                    if (c == '\\') { _p++; continue; }
                    if (c == '(') depth++;
                    else if (c == ')' && --depth == 0) break;
                }
                return Encoding.Latin1.GetString(_d, start, Math.Min(_p, _d.Length) - start);
            }
            if (b is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
            {
                _p++;
                return ((char)b).ToString();
            }
            _p++;
            while (_p < _d.Length && !IsSpace(_d[_p]) && !IsDelimiter(_d[_p])) _p++;
            return Encoding.Latin1.GetString(_d, start, _p - start);
        }
    }
}
