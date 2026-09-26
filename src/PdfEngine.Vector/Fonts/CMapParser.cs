using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PdfEngine.Vector.Fonts;

/// <summary>
/// Token-based parser for CMap programs (ISO 32000-2 9.7.5 and 9.10.3; Adobe TN 5014/5411).
/// Handles embedded code→CID CMaps and ToUnicode CMaps alike. Entries may span or share lines.
/// Never throws on malformed input: bad entries are skipped. All expansions are capped.
/// </summary>
internal static class CMapParser
{
    /// <summary>Maximum number of codes expanded from a single range entry.</summary>
    public const int MaxCodesPerRange = 65536;

    /// <summary>Maximum number of mapping entries (expanded ToUnicode codes / CID ranges) per CMap.</summary>
    public const int MaxTotalEntries = 1_000_000;

    /// <summary>Maximum length of a single hex/literal string token in bytes.</summary>
    private const int MaxStringBytes = 1024;

    /// <summary>Maximum number of items in an array operand.</summary>
    private const int MaxArrayItems = MaxCodesPerRange;

    private enum Mode { None, Codespace, BfChar, BfRange, CidChar, CidRange, NotDefChar, NotDefRange }

    /// <summary>Parses a CMap program. Always returns a (possibly empty) result.</summary>
    public static ParsedCMap Parse(ReadOnlySpan<byte> data)
    {
        var result = new ParsedCMap();
        try
        {
            ParseCore(data, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Defensive: a CMap must never take down font resolution.
        }
        return result;
    }

    private static void ParseCore(ReadOnlySpan<byte> data, ParsedCMap result)
    {
        var lexer = new Lexer(data);
        var mode = Mode.None;
        var pending = new List<Token>(3);
        var operands = new List<Token>(8);

        while (true)
        {
            Token tok = lexer.Next();
            if (tok.Kind == TokenKind.Eof)
                break;

            if (tok.Kind == TokenKind.Keyword)
            {
                string kw = tok.Text!;
                switch (kw)
                {
                    case "begincodespacerange": mode = Mode.Codespace; pending.Clear(); break;
                    case "beginbfchar": mode = Mode.BfChar; pending.Clear(); break;
                    case "beginbfrange": mode = Mode.BfRange; pending.Clear(); break;
                    case "begincidchar": mode = Mode.CidChar; pending.Clear(); break;
                    case "begincidrange": mode = Mode.CidRange; pending.Clear(); break;
                    case "beginnotdefchar": mode = Mode.NotDefChar; pending.Clear(); break;
                    case "beginnotdefrange": mode = Mode.NotDefRange; pending.Clear(); break;
                    case "endcodespacerange":
                    case "endbfchar":
                    case "endbfrange":
                    case "endcidchar":
                    case "endcidrange":
                    case "endnotdefchar":
                    case "endnotdefrange":
                        mode = Mode.None;
                        pending.Clear();
                        break;
                    case "usecmap":
                        if (operands.Count > 0 && operands[^1].Kind == TokenKind.Name)
                            result.UseCMapName = operands[^1].Text;
                        break;
                    case "def":
                        if (operands.Count >= 2 && operands[^2].Kind == TokenKind.Name)
                        {
                            var value = operands[^1];
                            switch (operands[^2].Text)
                            {
                                case "WMode" when value.Kind == TokenKind.Number:
                                    result.WMode = (int)value.Number;
                                    break;
                                case "CMapName" when value.Kind == TokenKind.Name:
                                    result.CMapName = value.Text;
                                    break;
                            }
                        }
                        break;
                }

                if (mode == Mode.None)
                    operands.Clear();
                continue;
            }

            if (mode == Mode.None)
            {
                if (operands.Count >= 16)
                    operands.RemoveAt(0);
                operands.Add(tok);
                continue;
            }

            pending.Add(tok);
            int arity = mode is Mode.Codespace or Mode.BfChar or Mode.CidChar or Mode.NotDefChar ? 2 : 3;
            if (pending.Count < arity)
                continue;

            switch (mode)
            {
                case Mode.Codespace: AddCodespace(result, pending[0], pending[1]); break;
                case Mode.BfChar: AddBfChar(result, pending[0], pending[1]); break;
                case Mode.BfRange: AddBfRange(result, pending[0], pending[1], pending[2]); break;
                case Mode.CidChar: AddCidRange(result, pending[0], pending[0], pending[1]); break;
                case Mode.CidRange: AddCidRange(result, pending[0], pending[1], pending[2]); break;
            }
            pending.Clear();
        }
    }

    private static void AddCodespace(ParsedCMap r, Token lo, Token hi)
    {
        if (!IsCode(lo) || !IsCode(hi) || lo.Bytes!.Length != hi.Bytes!.Length)
            return;
        if (r.Codespaces.Count >= 256)
            return;
        r.Codespaces.Add(new CodespaceRange(lo.Bytes, hi.Bytes));
    }

    private static void AddCidRange(ParsedCMap r, Token lo, Token hi, Token cid)
    {
        if (!IsCode(lo) || !IsCode(hi) || cid.Kind != TokenKind.Number || lo.Bytes!.Length != hi.Bytes!.Length)
            return;
        if (r.CidRanges.Count >= MaxTotalEntries)
            return;
        uint l = ToCode(lo.Bytes), h = ToCode(hi.Bytes);
        if (h < l || cid.Number < 0 || cid.Number > int.MaxValue)
            return;
        r.CidRanges.Add(new CidRange(lo.Bytes.Length, l, h, (int)cid.Number));
    }

    private static void AddBfChar(ParsedCMap r, Token src, Token dst)
    {
        if (!IsCode(src))
            return;
        string? text = dst.Kind switch
        {
            TokenKind.String => DecodeUtf16(dst.Bytes!),
            TokenKind.Name => GlyphList.ToUnicode(dst.Text),
            _ => null
        };
        if (text != null)
            r.TryAddUnicode((int)ToCode(src.Bytes!), text);
    }

    private static void AddBfRange(ParsedCMap r, Token lo, Token hi, Token dst)
    {
        if (!IsCode(lo) || !IsCode(hi))
            return;
        uint l = ToCode(lo.Bytes!), h = ToCode(hi.Bytes!);
        if (h < l)
            return;
        long count = Math.Min((long)h - l + 1, MaxCodesPerRange);

        if (dst.Kind == TokenKind.String)
        {
            byte[] baseBytes = dst.Bytes!;
            for (long i = 0; i < count; i++)
            {
                string? text = IncrementDestination(baseBytes, (int)i);
                if (text == null || !r.TryAddUnicode((int)(l + i), text))
                    break;
            }
        }
        else if (dst.Kind == TokenKind.Array)
        {
            var items = dst.Items!;
            for (int i = 0; i < count && i < items.Count; i++)
            {
                var item = items[i];
                string? text = item.Kind switch
                {
                    TokenKind.String => DecodeUtf16(item.Bytes!),
                    TokenKind.Name => GlyphList.ToUnicode(item.Text),
                    _ => null
                };
                if (text != null && !r.TryAddUnicode((int)(l + (uint)i), text))
                    break;
            }
        }
    }

    private static bool IsCode(Token t) => t.Kind == TokenKind.String && t.Bytes!.Length is >= 1 and <= 4;

    internal static uint ToCode(byte[] bytes)
    {
        uint v = 0;
        foreach (byte b in bytes)
            v = (v << 8) | b;
        return v;
    }

    /// <summary>Decodes UTF-16BE (surrogate pairs and multi-unit ligatures preserved).</summary>
    internal static string? DecodeUtf16(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;
        if (bytes.Length == 1)
            return ((char)bytes[0]).ToString();
        int len = bytes.Length & ~1;
        return Encoding.BigEndianUnicode.GetString(bytes, 0, len);
    }

    /// <summary>
    /// bfrange destination for offset <paramref name="delta"/>: the last code point of the
    /// destination is incremented (a surrogate pair is incremented as one scalar value).
    /// </summary>
    private static string? IncrementDestination(byte[] dst, int delta)
    {
        string? baseText = DecodeUtf16(dst);
        if (baseText == null)
            return null;
        if (delta == 0)
            return baseText;

        int lastLen = baseText.Length >= 2 && char.IsSurrogatePair(baseText[^2], baseText[^1]) ? 2 : 1;
        int last = lastLen == 2 ? char.ConvertToUtf32(baseText[^2], baseText[^1]) : baseText[^1];
        long next = (long)last + delta;
        if (next > 0x10FFFF)
            return null;
        if (next >= 0xD800 && next <= 0xDFFF)
            return baseText[..^lastLen] + "�";
        return baseText[..^lastLen] + char.ConvertFromUtf32((int)next);
    }

    private enum TokenKind { Eof, String, Name, Number, Keyword, Array }

    private readonly struct Token
    {
        public TokenKind Kind { get; init; }
        public byte[]? Bytes { get; init; }
        public string? Text { get; init; }
        public double Number { get; init; }
        public List<Token>? Items { get; init; }
    }

    private ref struct Lexer
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Lexer(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public Token Next()
        {
            while (true)
            {
                Token t = NextRaw(out bool isArrayStart, out bool skip);
                if (skip)
                    continue;
                if (!isArrayStart)
                    return t;

                var items = new List<Token>();
                while (true)
                {
                    Token item = NextRaw(out bool nestedStart, out bool itemSkip);
                    if (item.Kind == TokenKind.Eof)
                        break;
                    if (itemSkip || nestedStart)
                        continue;
                    if (item.Kind == TokenKind.Keyword && item.Text == "]")
                        break;
                    if (items.Count < MaxArrayItems)
                        items.Add(item);
                }
                return new Token { Kind = TokenKind.Array, Items = items };
            }
        }

        private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelimiter(byte b) =>
            b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        private Token NextRaw(out bool isArrayStart, out bool skip)
        {
            isArrayStart = false;
            skip = false;

            while (_pos < _data.Length)
            {
                byte b = _data[_pos];
                if (IsWhite(b))
                {
                    _pos++;
                }
                else if (b == '%')
                {
                    while (_pos < _data.Length && _data[_pos] != '\n' && _data[_pos] != '\r')
                        _pos++;
                }
                else
                {
                    break;
                }
            }

            if (_pos >= _data.Length)
                return new Token { Kind = TokenKind.Eof };

            byte c = _data[_pos];
            switch (c)
            {
                case (byte)'<':
                    if (_pos + 1 < _data.Length && _data[_pos + 1] == '<')
                    {
                        _pos += 2;
                        skip = true;
                        return default;
                    }
                    _pos++;
                    return ReadHex();
                case (byte)'>':
                    _pos += (_pos + 1 < _data.Length && _data[_pos + 1] == '>') ? 2 : 1;
                    skip = true;
                    return default;
                case (byte)'[':
                    _pos++;
                    isArrayStart = true;
                    return default;
                case (byte)']':
                    _pos++;
                    return new Token { Kind = TokenKind.Keyword, Text = "]" };
                case (byte)'{':
                case (byte)'}':
                case (byte)')':
                    _pos++;
                    skip = true;
                    return default;
                case (byte)'(':
                    _pos++;
                    return ReadLiteral();
                case (byte)'/':
                    _pos++;
                    return new Token { Kind = TokenKind.Name, Text = ReadRegular() };
            }

            string word = ReadRegular();
            if (word.Length == 0)
            {
                _pos++;
                skip = true;
                return default;
            }

            char first = word[0];
            if ((first is >= '0' and <= '9') || first is '+' or '-' or '.')
            {
                if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    return new Token { Kind = TokenKind.Number, Number = num };
            }
            return new Token { Kind = TokenKind.Keyword, Text = word };
        }

        private string ReadRegular()
        {
            int start = _pos;
            while (_pos < _data.Length && !IsWhite(_data[_pos]) && !IsDelimiter(_data[_pos]))
                _pos++;
            int len = Math.Min(_pos - start, 256);
            return Encoding.ASCII.GetString(_data.Slice(start, len));
        }

        private Token ReadHex()
        {
            var bytes = new List<byte>();
            int hi = -1;
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b == '>')
                    break;
                int d = b switch
                {
                    >= (byte)'0' and <= (byte)'9' => b - '0',
                    >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                    >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                    _ => -1
                };
                if (d < 0)
                    continue; // whitespace or garbage: ignored
                if (hi < 0)
                {
                    hi = d;
                }
                else
                {
                    if (bytes.Count < MaxStringBytes)
                        bytes.Add((byte)((hi << 4) | d));
                    hi = -1;
                }
            }
            if (hi >= 0 && bytes.Count < MaxStringBytes)
                bytes.Add((byte)(hi << 4)); // odd digit count: trailing 0 assumed (ISO 32000-2 7.3.4.3)
            return new Token { Kind = TokenKind.String, Bytes = bytes.ToArray() };
        }

        private Token ReadLiteral()
        {
            var bytes = new List<byte>();
            int depth = 1;
            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b == '\\' && _pos < _data.Length)
                {
                    byte e = _data[_pos++];
                    int v;
                    switch (e)
                    {
                        case (byte)'n': v = '\n'; break;
                        case (byte)'r': v = '\r'; break;
                        case (byte)'t': v = '\t'; break;
                        case (byte)'b': v = '\b'; break;
                        case (byte)'f': v = '\f'; break;
                        case >= (byte)'0' and <= (byte)'7':
                            v = e - '0';
                            for (int k = 0; k < 2 && _pos < _data.Length && _data[_pos] is >= (byte)'0' and <= (byte)'7'; k++)
                                v = (v << 3) | (_data[_pos++] - '0');
                            break;
                        case (byte)'\r':
                        case (byte)'\n':
                            continue;
                        default: v = e; break;
                    }
                    if (bytes.Count < MaxStringBytes)
                        bytes.Add((byte)v);
                    continue;
                }
                if (b == '(')
                    depth++;
                else if (b == ')' && --depth == 0)
                    break;
                if (bytes.Count < MaxStringBytes)
                    bytes.Add(b);
            }
            return new Token { Kind = TokenKind.String, Bytes = bytes.ToArray() };
        }
    }
}

/// <summary>A codespace range: codes of <see cref="Length"/> bytes whose every byte lies within [Low[i], High[i]].</summary>
internal sealed record CodespaceRange(byte[] Low, byte[] High)
{
    public int Length => Low.Length;

    public bool Matches(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Low.Length)
            return false;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] < Low[i] || bytes[i] > High[i])
                return false;
        }
        return true;
    }

    /// <summary>Whether the first <paramref name="n"/> bytes match this range's prefix.</summary>
    public bool MatchesPrefix(ReadOnlySpan<byte> bytes, int n)
    {
        for (int i = 0; i < n && i < Low.Length && i < bytes.Length; i++)
        {
            if (bytes[i] < Low[i] || bytes[i] > High[i])
                return false;
        }
        return true;
    }
}

/// <summary>Code range of a given byte length mapped to consecutive CIDs starting at <see cref="Cid"/>.</summary>
internal readonly record struct CidRange(int Length, uint Low, uint High, int Cid);

/// <summary>Raw result of parsing a CMap program.</summary>
internal sealed class ParsedCMap
{
    public List<CodespaceRange> Codespaces { get; } = new();
    public List<CidRange> CidRanges { get; } = new();
    public Dictionary<int, string> Unicode { get; } = new();
    public string? UseCMapName { get; set; }
    public string? CMapName { get; set; }
    public int WMode { get; set; }

    public bool TryAddUnicode(int code, string text)
    {
        if (Unicode.Count >= CMapParser.MaxTotalEntries && !Unicode.ContainsKey(code))
            return false;
        Unicode[code] = text;
        return true;
    }
}
