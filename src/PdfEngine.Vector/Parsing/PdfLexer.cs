using System;
using System.Globalization;
using System.IO;
using System.Text;
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
        var sb = new StringBuilder();
        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0 || IsWhitespace((byte)b) || IsDelimiter((byte)b))
            {
                if (b >= 0)
                    _source.Seek(-1, SeekOrigin.Current);
                break;
            }

            if (b == '#' && sb.Length < _limits.MaxTokenLength)
            {
                // Hex-encoded character #XX
                int h1 = _source.ReadByte();
                int h2 = _source.ReadByte();
                if (h1 >= 0 && h2 >= 0 && TryParseHexNibble((byte)h1, out int n1) && TryParseHexNibble((byte)h2, out int n2))
                {
                    sb.Append((char)((n1 << 4) | n2));
                }
                else
                {
                    if (h2 >= 0) _source.Seek(-1, SeekOrigin.Current);
                    if (h1 >= 0) _source.Seek(-1, SeekOrigin.Current);
                    sb.Append('#');
                }
            }
            else
            {
                if (sb.Length < _limits.MaxTokenLength)
                    sb.Append((char)b);
            }
        }

        return new PdfToken(PdfTokenType.Name, sb.ToString(), FileOffset: offset);
    }

    private PdfToken ReadLiteralString(long offset)
    {
        using var ms = new MemoryStream();
        int nesting = 1;

        while (nesting > 0)
        {
            int b = _source.ReadByte();
            if (b < 0)
                break; // Premature EOF recovery

            if (ms.Length > _limits.MaxTokenLength)
                throw new InvalidDataException("PDF literal string exceeded maximum allowed token length.");

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

    private PdfToken ReadHexString(long offset)
    {
        using var ms = new MemoryStream();
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
        var sb = new StringBuilder();
        sb.Append((char)firstByte);

        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0 || IsWhitespace((byte)b) || IsDelimiter((byte)b))
            {
                if (b >= 0)
                    _source.Seek(-1, SeekOrigin.Current);
                break;
            }
            if (sb.Length < _limits.MaxTokenLength)
                sb.Append((char)b);
        }

        string text = sb.ToString();

        // Check for integer
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intVal))
        {
            return new PdfToken(PdfTokenType.Integer, text, IntValue: intVal, RealValue: intVal, FileOffset: offset);
        }

        // Check for real
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double realVal))
        {
            return new PdfToken(PdfTokenType.Real, text, RealValue: realVal, IntValue: (long)Math.Round(realVal), FileOffset: offset);
        }

        // Otherwise keyword/operator
        return new PdfToken(PdfTokenType.Keyword, text, FileOffset: offset);
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
