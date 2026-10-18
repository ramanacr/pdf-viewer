using System;
using System.Globalization;
using System.IO;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// High-performance lexical analyzer for PDF streams complying with ISO 32000-2 Clause 7.2.
/// </summary>
public sealed class PdfLexer
{
    private readonly IPdfByteSource _source;
    private readonly PdfSecurityLimits _limits;

    public IPdfByteSource Source => _source;
    public long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public PdfLexer(IPdfByteSource source, PdfSecurityLimits? limits = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _limits = limits ?? PdfSecurityLimits.Default;
    }

    // Token text and string bytes are collected in buffers kept by the lexer, not in a new
    // StringBuilder or MemoryStream per token: content streams are mostly short numbers and operators.
    private char[] _chars = new char[64];
    private int _charCount;
    private byte[] _bytes = new byte[64];
    private int _byteCount;

    private void AppendChar(char c)
    {
        if (_charCount == _chars.Length) Array.Resize(ref _chars, _chars.Length * 2);
        _chars[_charCount++] = c;
    }

    private void AppendByte(byte b)
    {
        if (_byteCount == _bytes.Length) Array.Resize(ref _bytes, _bytes.Length * 2);
        _bytes[_byteCount++] = b;
    }

    // Operators and other keywords as shared strings, so the common ones are not allocated per token.
    private static readonly System.Collections.Generic.Dictionary<string, string> Keywords = BuildKeywords();
    private static readonly System.Collections.Generic.Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup =
        Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static System.Collections.Generic.Dictionary<string, string> BuildKeywords()
    {
        string[] words =
        [
            "q", "Q", "cm", "w", "J", "j", "M", "d", "ri", "i", "gs", "CS", "cs", "SC", "SCN", "sc", "scn", "G", "g", "RG", "rg",
            "K", "k", "m", "l", "c", "v", "y", "h", "re", "S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "n", "W", "W*",
            "BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts", "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "\"", "d0", "d1",
            "sh", "BI", "ID", "EI", "Do", "MP", "DP", "BMC", "BDC", "EMC", "BX", "EX", "R", "obj", "endobj", "stream",
            "endstream", "true", "false", "null", "xref", "trailer", "startxref",
        ];
        var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in words) map[word] = word;
        return map;
    }

    // Names repeat across a document (resource and font names): shared strings for short ones, in a
    // bounded table.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Names = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> NameLookup =
        Names.GetAlternateLookup<ReadOnlySpan<char>>();
    private const int MaxSharedNameLength = 32, MaxSharedNames = 8192;

    private static string NameString(ReadOnlySpan<char> text)
    {
        if (text.Length > MaxSharedNameLength)
            return new string(text);
        if (NameLookup.TryGetValue(text, out var shared))
            return shared;
        var name = new string(text);
        if (Names.Count < MaxSharedNames)
            Names.TryAdd(name, name);
        return name;
    }

    public static bool IsWhitespace(byte b) =>
        b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20;

    public static bool IsDelimiter(byte b) =>
        b == (byte)'(' || b == (byte)')' ||
        b == (byte)'<' || b == (byte)'>' ||
        b == (byte)'[' || b == (byte)']' ||
        b == (byte)'{' || b == (byte)'}' ||
        b == (byte)'/' || b == (byte)'%';

    public void SkipWhitespaceAndComments()
    {
        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0) return;

            if (IsWhitespace((byte)b))
            {
                continue;
            }

            if (b == '%')
            {
                // Comment: skip until EOL (0x0A or 0x0D)
                while (true)
                {
                    int c = _source.ReadByte();
                    if (c < 0) return;
                    if (c == 0x0A || c == 0x0D)
                    {
                        // Check for CRLF
                        if (c == 0x0D)
                        {
                            int next = _source.ReadByte();
                            if (next >= 0 && next != 0x0A)
                            {
                                _source.Seek(-1, SeekOrigin.Current);
                            }
                        }
                        break;
                    }
                }
                continue;
            }

            // Not whitespace and not comment; rewind 1 byte
            _source.Seek(-1, SeekOrigin.Current);
            break;
        }
    }

    public PdfToken NextToken()
    {
        SkipWhitespaceAndComments();

        long tokenOffset = _source.Position;
        int b = _source.ReadByte();
        if (b < 0)
            return PdfToken.Eof;

        byte ch = (byte)b;

        // Arrays: [ and ]
        if (ch == '[')
            return new PdfToken(PdfTokenType.BeginArray, "[", FileOffset: tokenOffset);
        if (ch == ']')
            return new PdfToken(PdfTokenType.EndArray, "]", FileOffset: tokenOffset);

        // Dictionaries or Hex Strings: << or <
        if (ch == '<')
        {
            int next = _source.ReadByte();
            if (next == '<')
            {
                return new PdfToken(PdfTokenType.BeginDictionary, "<<", FileOffset: tokenOffset);
            }
            if (next >= 0)
            {
                _source.Seek(-1, SeekOrigin.Current);
            }
            return ReadHexString(tokenOffset);
        }

        // Dictionary end: >>
        if (ch == '>')
        {
            int next = _source.ReadByte();
            if (next == '>')
            {
                return new PdfToken(PdfTokenType.EndDictionary, ">>", FileOffset: tokenOffset);
            }
            if (next >= 0)
            {
                _source.Seek(-1, SeekOrigin.Current);
            }
            return new PdfToken(PdfTokenType.Keyword, ">", FileOffset: tokenOffset);
        }

        // Literal String: (...)
        if (ch == '(')
        {
            return ReadLiteralString(tokenOffset);
        }

        // Name: /...
        if (ch == '/')
        {
            return ReadName(tokenOffset);
        }

        // Numbers or Keywords
        return ReadNumberOrKeyword(ch, tokenOffset);
    }

    private PdfToken ReadName(long offset)
    {
        _charCount = 0;
        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0 || IsWhitespace((byte)b) || IsDelimiter((byte)b))
            {
                if (b >= 0)
                    _source.Seek(-1, SeekOrigin.Current);
                break;
            }

            if (b == '#' && _charCount < _limits.MaxTokenLength)
            {
                // Hex-encoded character #XX
                int h1 = _source.ReadByte();
                int h2 = _source.ReadByte();
                if (h1 >= 0 && h2 >= 0 && TryParseHexNibble((byte)h1, out int n1) && TryParseHexNibble((byte)h2, out int n2))
                {
                    AppendChar((char)((n1 << 4) | n2));
                }
                else
                {
                    if (h2 >= 0) _source.Seek(-1, SeekOrigin.Current);
                    if (h1 >= 0) _source.Seek(-1, SeekOrigin.Current);
                    AppendChar('#');
                }
            }
            else
            {
                if (_charCount < _limits.MaxTokenLength)
                    AppendChar((char)b);
            }
        }

        return new PdfToken(PdfTokenType.Name, NameString(_chars.AsSpan(0, _charCount)), FileOffset: offset);
    }

    private PdfToken ReadLiteralString(long offset)
    {
        var ms = new ByteBuffer(this);
        int nesting = 1;

        while (nesting > 0)
        {
            int b = _source.ReadByte();
            if (b < 0)
                break; // Premature EOF recovery

            if (ms.Length > _limits.MaxTokenLength)
                throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxTokenLength), "PDF literal string exceeded maximum allowed token length.");

            if (b == '(')
            {
                nesting++;
                ms.WriteByte((byte)b);
            }
            else if (b == ')')
            {
                nesting--;
                if (nesting > 0)
                {
                    ms.WriteByte((byte)b);
                }
            }
            else if (b == '\\')
            {
                int esc = _source.ReadByte();
                if (esc < 0) break;

                switch (esc)
                {
                    case 'n': ms.WriteByte((byte)'\n'); break;
                    case 'r': ms.WriteByte((byte)'\r'); break;
                    case 't': ms.WriteByte((byte)'\t'); break;
                    case 'b': ms.WriteByte((byte)'\b'); break;
                    case 'f': ms.WriteByte((byte)'\f'); break;
                    case '(': ms.WriteByte((byte)'('); break;
                    case ')': ms.WriteByte((byte)')'); break;
                    case '\\': ms.WriteByte((byte)'\\'); break;
                    case '\r':
                    {
                        int next = _source.ReadByte();
                        if (next >= 0 && next != '\n')
                            _source.Seek(-1, SeekOrigin.Current);
                        break; // Line continuation
                    }
                    case '\n':
                        break; // Line continuation
                    default:
                        if (esc >= '0' && esc <= '7')
                        {
                            int octalVal = esc - '0';
                            int d2 = _source.ReadByte();
                            if (d2 >= '0' && d2 <= '7')
                            {
                                octalVal = (octalVal << 3) + (d2 - '0');
                                int d3 = _source.ReadByte();
                                if (d3 >= '0' && d3 <= '7')
                                {
                                    octalVal = (octalVal << 3) + (d3 - '0');
                                }
                                else if (d3 >= 0)
                                {
                                    _source.Seek(-1, SeekOrigin.Current);
                                }
                            }
                            else if (d2 >= 0)
                            {
                                _source.Seek(-1, SeekOrigin.Current);
                            }
                            ms.WriteByte((byte)(octalVal & 0xFF));
                        }
                        else
                        {
                            ms.WriteByte((byte)esc);
                        }
                        break;
                }
            }
            else
            {
                ms.WriteByte((byte)b);
            }
        }

        byte[] raw = ms.ToArray();
        return new PdfToken(PdfTokenType.String, Encoding.ASCII.GetString(raw), BinaryValue: raw, FileOffset: offset);
    }

    /// <summary>The lexer's byte buffer with the part of MemoryStream the string readers use.</summary>
    private readonly ref struct ByteBuffer
    {
        private readonly PdfLexer _lexer;

        public ByteBuffer(PdfLexer lexer)
        {
            _lexer = lexer;
            lexer._byteCount = 0;
        }

        public long Length => _lexer._byteCount;
        public void WriteByte(byte b) => _lexer.AppendByte(b);
        public byte[] ToArray() => _lexer._bytes.AsSpan(0, _lexer._byteCount).ToArray();
    }

    private PdfToken ReadHexString(long offset)
    {
        var ms = new ByteBuffer(this);
        int firstNibble = -1;

        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0 || b == '>')
                break;

            if (IsWhitespace((byte)b))
                continue;

            if (TryParseHexNibble((byte)b, out int nibble))
            {
                if (firstNibble < 0)
                {
                    firstNibble = nibble;
                }
                else
                {
                    ms.WriteByte((byte)((firstNibble << 4) | nibble));
                    firstNibble = -1;
                }
            }
        }

        // PDF 32000-2 7.3.4.3: if odd number of hex digits, final digit padded with 0
        if (firstNibble >= 0)
        {
            ms.WriteByte((byte)(firstNibble << 4));
        }

        byte[] raw = ms.ToArray();
        return new PdfToken(PdfTokenType.HexString, Convert.ToHexString(raw), BinaryValue: raw, FileOffset: offset);
    }

    private PdfToken ReadNumberOrKeyword(byte firstByte, long offset)
    {
        _charCount = 0;
        AppendChar((char)firstByte);

        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0 || IsWhitespace((byte)b) || IsDelimiter((byte)b))
            {
                if (b >= 0)
                    _source.Seek(-1, SeekOrigin.Current);
                break;
            }
            if (_charCount < _limits.MaxTokenLength)
                AppendChar((char)b);
        }

        ReadOnlySpan<char> text = _chars.AsSpan(0, _charCount);

        // Check for integer
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intVal))
        {
            return new PdfToken(PdfTokenType.Integer, new string(text), IntValue: intVal, RealValue: intVal, FileOffset: offset);
        }

        // Check for real
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double realVal))
        {
            return new PdfToken(PdfTokenType.Real, new string(text), RealValue: realVal, IntValue: (long)Math.Round(realVal), FileOffset: offset);
        }

        // Otherwise keyword/operator
        return new PdfToken(PdfTokenType.Keyword, KeywordLookup.TryGetValue(text, out var keyword) ? keyword : new string(text), FileOffset: offset);
    }

    private static bool TryParseHexNibble(byte b, out int nibble)
    {
        if (b >= '0' && b <= '9')
        {
            nibble = b - '0';
            return true;
        }
        if (b >= 'a' && b <= 'f')
        {
            nibble = b - 'a' + 10;
            return true;
        }
        if (b >= 'A' && b <= 'F')
        {
            nibble = b - 'A' + 10;
            return true;
        }
        nibble = 0;
        return false;
    }
}
