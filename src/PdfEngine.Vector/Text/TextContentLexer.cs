using System;
using System.Collections.Generic;
using System.Globalization;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Text;

/// <summary>The content operators the text walk acts on; every other operator is <see cref="Other"/>.</summary>
internal enum TextOperator
{
    Other, q, Q, cm, BT, Tf, gs, Tc, Tw, Tz, TL, Ts, Tr, Td, TD, Tm, TStar, Tj, Quote, DoubleQuote, TJ, BI, Do, BDC, BMC, EMC,
}

/// <summary>
/// Reads a content stream for the text walk: operands up to the next operator, straight from the
/// decoded bytes. It gives the same operands as <see cref="PdfLexer"/> and <see cref="PdfParser"/>
/// read one object at a time (numbers, names, strings, indirect references, true, false and null),
/// without a string per token or the parser's look-ahead lexing each number twice more. Numbers stay
/// numbers, strings without escapes are slices of the content, and an array of strings and numbers
/// (a TJ array) is kept as a run of items. Anything else (dictionaries, other arrays) is left to
/// the general parser at that offset, so unusual content still reads as it did.
/// </summary>
internal sealed class TextContentLexer
{
    public enum Kind : byte { Number, Name, String, Array, Object }

    public struct Operand
    {
        public Kind Kind;
        public double Number;
        /// <summary>The name (string), the string (PdfString) or the parsed object (PdfObject).</summary>
        public object? Value;
        /// <summary>For an array: its items in <see cref="ArrayItems"/>.</summary>
        public int Start, Count;
    }

    private const int MaxOperands = 64;

    private readonly byte[] _buf;
    private readonly int _end;
    private int _pos;
    private readonly int _maxToken;
    private readonly MemoryByteSource _src;
    private readonly PdfLexer _lexer;
    private readonly PdfParser _parser;

    public readonly List<Operand> Operands = new(8);
    /// <summary>The items of the arrays among the operands (numbers and strings only).</summary>
    public readonly List<Operand> ArrayItems = new();

    public TextContentLexer(byte[] content)
    {
        _buf = content;
        _end = content.Length;
        _maxToken = PdfSecurityLimits.Default.MaxTokenLength;
        _src = new MemoryByteSource(content);
        _lexer = new PdfLexer(_src, PdfSecurityLimits.Default);
        _parser = new PdfParser(PdfSecurityLimits.Default);
    }

    public int Count => Operands.Count;

    /// <summary>
    /// Reads operands up to the next operator. False at the end of the content, or where the
    /// content cannot be read any further (a string over the token limit), as the walk then stops.
    /// </summary>
    public bool Next(out TextOperator op)
    {
        op = TextOperator.Other;
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _end) return false;
            int tokenPos = _pos;
            byte c = _buf[_pos];
            switch (c)
            {
                case (byte)'(':
                {
                    if (!ReadLiteralString(out var s)) return false;
                    Add(new Operand { Kind = Kind.String, Value = s });
                    continue;
                }
                case (byte)'<':
                    if (_pos + 1 < _end && _buf[_pos + 1] == '<') { Fallback(tokenPos); continue; }
                    Add(new Operand { Kind = Kind.String, Value = ReadHexString() });
                    continue;
                case (byte)'>':
                    if (_pos + 1 < _end && _buf[_pos + 1] == '>') { _pos += 2; continue; } // a stray >> reads as nothing
                    _pos++;
                    return true; // the keyword ">"
                case (byte)']':
                    _pos++; // a stray ] reads as nothing
                    continue;
                case (byte)'[':
                    if (!TryReadArray()) Fallback(tokenPos);
                    continue;
                case (byte)'/':
                    Add(new Operand { Kind = Kind.Name, Value = ReadName() });
                    continue;
            }

            int start = _pos, end = ReadRun();
            var token = Classify(start, end, out double number);
            if (token == RunKind.Integer)
            {
                if (TryIndirectRef(number, out var reference)) Add(new Operand { Kind = Kind.Object, Value = reference });
                else Add(new Operand { Kind = Kind.Number, Number = number });
                continue;
            }
            if (token == RunKind.Real)
            {
                Add(new Operand { Kind = Kind.Number, Number = number });
                continue;
            }
            var span = _buf.AsSpan(start, Math.Min(end - start, _maxToken));
            if (span.SequenceEqual("true"u8)) { Add(new Operand { Kind = Kind.Object, Value = PdfBoolean.True }); continue; }
            if (span.SequenceEqual("false"u8)) { Add(new Operand { Kind = Kind.Object, Value = PdfBoolean.False }); continue; }
            if (span.SequenceEqual("null"u8)) { Add(new Operand { Kind = Kind.Object, Value = PdfNull.Instance }); continue; }
            op = Lookup(span);
            return true;
        }
    }

    /// <summary>Drops the operands of the operator just handled.</summary>
    public void Clear()
    {
        Operands.Clear();
        ArrayItems.Clear();
    }

    /// <summary>Inline image data is skipped by the general reader, from where the lexer stands.</summary>
    public void SkipInlineImage()
    {
        _src.Position = _pos;
        PdfEngine.Vector.Editing.PageContentWalker.InlineImage(_src, _lexer, _parser);
        _pos = (int)_src.Position;
    }

    // ------------------------------------------------------------------ operand access (from the end, 1 = last)

    public bool TryNumber(int fromEnd, out double value)
    {
        var o = Operands[Operands.Count - fromEnd];
        if (o.Kind == Kind.Number) { value = o.Number; return true; }
        if (o.Kind == Kind.Object && o.Value is PdfObject obj) return obj.TryGetNumber(out value);
        value = 0;
        return false;
    }

    public PdfString? StringAt(int fromEnd)
    {
        var o = Operands[Operands.Count - fromEnd];
        return o.Value as PdfString;
    }

    public string? NameAt(int fromEnd)
    {
        var o = Operands[Operands.Count - fromEnd];
        return o.Kind == Kind.Name ? (string)o.Value! : o.Value is PdfName n ? n.Value : null;
    }

    public object? ValueAt(int fromEnd) => Operands[Operands.Count - fromEnd].Value;

    public Operand At(int fromEnd) => Operands[Operands.Count - fromEnd];

    /// <summary>The last <paramref name="n"/> operands as finite numbers into <paramref name="values"/>; false when one is not.</summary>
    public bool Numbers(int n, Span<double> values)
    {
        if (Operands.Count < n) return false;
        for (int i = 0; i < n; i++)
            if (!TryNumber(n - i, out values[i]) || !double.IsFinite(values[i])) return false;
        return true;
    }

    // ------------------------------------------------------------------ tokens

    private void Add(Operand o)
    {
        Operands.Add(o);
        if (Operands.Count > MaxOperands) Operands.RemoveAt(0);
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _end)
        {
            byte b = _buf[_pos];
            if (IsWhitespace(b)) { _pos++; continue; }
            if (b != '%') return;
            while (_pos < _end && _buf[_pos] is not (0x0A or 0x0D)) _pos++;
        }
    }

    /// <summary>A run of regular characters (the first byte is taken whatever it is, as <see cref="PdfLexer"/> does); returns its end.</summary>
    private int ReadRun()
    {
        _pos++;
        while (_pos < _end && !IsWhitespace(_buf[_pos]) && !IsDelimiter(_buf[_pos])) _pos++;
        return _pos;
    }

    private enum RunKind { Integer, Real, Keyword }

    /// <summary>Reads a run as <see cref="PdfLexer"/> does: an integer (Int64), else a real (invariant float), else a keyword.</summary>
    private RunKind Classify(int start, int end, out double number)
    {
        number = 0;
        var span = _buf.AsSpan(start, Math.Min(end - start, _maxToken));
        if (span.Length == 0) return RunKind.Keyword;
        // Plain digits with an optional sign: the common case, read without the general parser.
        int i = span[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        if (i < span.Length && span.Length - i <= 18)
        {
            long v = 0;
            int k = i;
            for (; k < span.Length; k++)
            {
                uint d = (uint)(span[k] - '0');
                if (d > 9) break;
                v = v * 10 + d;
            }
            if (k == span.Length)
            {
                number = span[0] == '-' ? -v : v;
                return RunKind.Integer;
            }
        }
        // Digits with one decimal point, 15 digits at most: the mantissa and the power of ten are
        // exact doubles, so one division gives the correctly rounded value the parser gives.
        if (i < span.Length && span.Length - i <= 16)
        {
            long m = 0;
            int digits = 0, frac = 0, k = i;
            bool dot = false;
            for (; k < span.Length; k++)
            {
                byte ch = span[k];
                if (ch == '.')
                {
                    if (dot) break;
                    dot = true;
                    continue;
                }
                uint d = (uint)(ch - '0');
                if (d > 9) break;
                m = m * 10 + d;
                digits++;
                if (dot) frac++;
            }
            if (k == span.Length && dot && digits is > 0 and <= 15)
            {
                double value = m / Pow10[frac];
                number = span[0] == '-' ? -value : value;
                return RunKind.Real;
            }
        }
        // PdfLexer reads the run as Latin-1 text; no byte over 0x7F can be part of a number then.
        foreach (byte b in span)
            if (b > 0x7F) return RunKind.Keyword;
        if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { number = l; return RunKind.Integer; }
        if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double r)) { number = r; return RunKind.Real; }
        return RunKind.Keyword;
    }

    private static readonly double[] Pow10 = { 1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15 };

    /// <summary>"num gen R" after an integer, as <see cref="PdfParser"/> looks ahead for it; consumes it when found.</summary>
    private bool TryIndirectRef(double number, out PdfIndirectRef? reference)
    {
        reference = null;
        int save = _pos;
        SkipWhitespaceAndComments();
        if (_pos < _end && !IsDelimiter(_buf[_pos]))
        {
            int s1 = _pos, e1 = ReadRun();
            if (Classify(s1, e1, out double gen) == RunKind.Integer)
            {
                SkipWhitespaceAndComments();
                if (_pos < _end && !IsDelimiter(_buf[_pos]))
                {
                    int s2 = _pos, e2 = ReadRun();
                    if (e2 - s2 == 1 && _buf[s2] == 'R')
                    {
                        reference = new PdfIndirectRef((int)(long)number, (int)(long)gen);
                        return true;
                    }
                }
            }
        }
        _pos = save;
        return false;
    }

    /// <summary>The general parser reads the object at <paramref name="tokenPos"/>, as the walk did before this lexer.</summary>
    private void Fallback(int tokenPos)
    {
        _src.Position = tokenPos;
        PdfObject? obj;
        try { obj = _parser.ParseObject(_lexer); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { obj = null; _src.Position = tokenPos + 1; }
        if (obj != null) Add(new Operand { Kind = Kind.Object, Value = obj });
        else if (_src.Position == tokenPos) _src.Position = tokenPos + 1;
        _pos = (int)_src.Position;
    }

    /// <summary>An array of numbers and strings; false (nothing consumed) for anything else, which the general parser then reads.</summary>
    private bool TryReadArray()
    {
        int arrayStart = _pos, itemsStart = ArrayItems.Count;
        _pos++;
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _end) break;
            byte c = _buf[_pos];
            if (c == ']')
            {
                _pos++;
                Add(new Operand { Kind = Kind.Array, Start = itemsStart, Count = ArrayItems.Count - itemsStart });
                return true;
            }
            if (c == '(')
            {
                if (!ReadLiteralString(out var s)) break;
                ArrayItems.Add(new Operand { Kind = Kind.String, Value = s });
                continue;
            }
            if (c == '<')
            {
                if (_pos + 1 < _end && _buf[_pos + 1] == '<') break;
                ArrayItems.Add(new Operand { Kind = Kind.String, Value = ReadHexString() });
                continue;
            }
            if (IsDelimiter(c)) break;
            int start = _pos, end = ReadRun();
            var kind = Classify(start, end, out double number);
            if (kind == RunKind.Keyword) break;
            if (kind == RunKind.Integer && TryIndirectRef(number, out _)) break;
            ArrayItems.Add(new Operand { Kind = Kind.Number, Number = number });
        }
        ArrayItems.RemoveRange(itemsStart, ArrayItems.Count - itemsStart);
        _pos = arrayStart;
        return false;
    }

    /// <summary>A literal string as <see cref="PdfLexer"/> reads it; false when it is over the token limit (the lexer throws there).</summary>
    private bool ReadLiteralString(out PdfString value)
    {
        int start = ++_pos;
        int nesting = 1;
        int i = start;
        bool escaped = false;
        for (; i < _end; i++)
        {
            byte b = _buf[i];
            if (b == '\\') { escaped = true; break; }
            if (b == '(') nesting++;
            else if (b == ')' && --nesting == 0) break;
        }
        if (!escaped)
        {
            int length = i - start;
            if (length > _maxToken + 1) { value = null!; return false; }
            _pos = i < _end ? i + 1 : _end;
            value = new PdfString(_buf.AsMemory(start, length));
            return true;
        }

        // With escapes: decoded byte by byte, exactly as the lexer does.
        _pos = start;
        var bytes = new List<byte>(Math.Min(256, _end - start));
        nesting = 1;
        while (nesting > 0)
        {
            if (_pos >= _end) break;
            int b = _buf[_pos++];
            if (bytes.Count > _maxToken) { value = null!; return false; }
            if (b == '(') { nesting++; bytes.Add((byte)b); }
            else if (b == ')') { nesting--; if (nesting > 0) bytes.Add((byte)b); }
            else if (b == '\\')
            {
                if (_pos >= _end) break;
                int esc = _buf[_pos++];
                switch (esc)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '(': bytes.Add((byte)'('); break;
                    case ')': bytes.Add((byte)')'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    case '\r':
                        if (_pos < _end && _buf[_pos] == '\n') _pos++;
                        break;
                    case '\n':
                        break;
                    default:
                        if (esc is >= '0' and <= '7')
                        {
                            int octal = esc - '0';
                            if (_pos < _end && _buf[_pos] is >= (byte)'0' and <= (byte)'7')
                            {
                                octal = (octal << 3) + (_buf[_pos++] - '0');
                                if (_pos < _end && _buf[_pos] is >= (byte)'0' and <= (byte)'7')
                                    octal = (octal << 3) + (_buf[_pos++] - '0');
                            }
                            bytes.Add((byte)(octal & 0xFF));
                        }
                        else
                        {
                            bytes.Add((byte)esc);
                        }
                        break;
                }
            }
            else
            {
                bytes.Add((byte)b);
            }
        }
        value = new PdfString(bytes.ToArray());
        return true;
    }

    private PdfString ReadHexString()
    {
        _pos++;
        int start = _pos;
        int close = Array.IndexOf(_buf, (byte)'>', start, _end - start);
        int stop = close < 0 ? _end : close;
        var bytes = new byte[(stop - start + 1) / 2];
        int n = 0, first = -1;
        for (int i = start; i < stop; i++)
        {
            int nibble = HexNibble(_buf[i]);
            if (nibble < 0) continue; // white space and stray characters are skipped
            if (first < 0) first = nibble;
            else { bytes[n++] = (byte)((first << 4) | nibble); first = -1; }
        }
        if (first >= 0) bytes[n++] = (byte)(first << 4);
        _pos = close < 0 ? _end : close + 1;
        return new PdfString(n == bytes.Length ? bytes : bytes.AsSpan(0, n).ToArray(), IsHex: true);
    }

    private static int HexNibble(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary>A name as <see cref="PdfLexer"/> reads it: #XX escapes, each other byte as one character.</summary>
    private string ReadName()
    {
        int start = ++_pos;
        int i = start;
        bool plain = true;
        while (i < _end && !IsWhitespace(_buf[i]) && !IsDelimiter(_buf[i]))
        {
            if (_buf[i] == '#') plain = false;
            i++;
        }
        if (plain && i - start <= _maxToken)
        {
            _pos = i;
            return Latin1(_buf.AsSpan(start, i - start));
        }

        var sb = new System.Text.StringBuilder(i - start);
        while (_pos < _end)
        {
            byte b = _buf[_pos];
            if (IsWhitespace(b) || IsDelimiter(b)) break;
            _pos++;
            if (b == '#' && sb.Length < _maxToken)
            {
                int h1 = _pos < _end ? HexNibble(_buf[_pos]) : -1;
                int h2 = _pos + 1 < _end ? HexNibble(_buf[_pos + 1]) : -1;
                if (h1 >= 0 && h2 >= 0)
                {
                    sb.Append((char)((h1 << 4) | h2));
                    _pos += 2;
                }
                else
                {
                    sb.Append('#');
                }
            }
            else if (sb.Length < _maxToken)
            {
                sb.Append((char)b);
            }
        }
        return sb.ToString();
    }

    private static string Latin1(ReadOnlySpan<byte> bytes) => bytes.Length == 0 ? string.Empty : System.Text.Encoding.Latin1.GetString(bytes);

    private static TextOperator Lookup(ReadOnlySpan<byte> s)
    {
        switch (s.Length)
        {
            case 1:
                return s[0] switch
                {
                    (byte)'q' => TextOperator.q,
                    (byte)'Q' => TextOperator.Q,
                    (byte)'\'' => TextOperator.Quote,
                    (byte)'"' => TextOperator.DoubleQuote,
                    _ => TextOperator.Other,
                };
            case 2:
                byte a = s[0], b = s[1];
                if (a == 'T')
                {
                    return b switch
                    {
                        (byte)'f' => TextOperator.Tf,
                        (byte)'c' => TextOperator.Tc,
                        (byte)'w' => TextOperator.Tw,
                        (byte)'z' => TextOperator.Tz,
                        (byte)'L' => TextOperator.TL,
                        (byte)'s' => TextOperator.Ts,
                        (byte)'r' => TextOperator.Tr,
                        (byte)'d' => TextOperator.Td,
                        (byte)'D' => TextOperator.TD,
                        (byte)'m' => TextOperator.Tm,
                        (byte)'*' => TextOperator.TStar,
                        (byte)'j' => TextOperator.Tj,
                        (byte)'J' => TextOperator.TJ,
                        _ => TextOperator.Other,
                    };
                }
                if (a == 'c' && b == 'm') return TextOperator.cm;
                if (a == 'B' && b == 'T') return TextOperator.BT;
                if (a == 'g' && b == 's') return TextOperator.gs;
                if (a == 'B' && b == 'I') return TextOperator.BI;
                if (a == 'D' && b == 'o') return TextOperator.Do;
                return TextOperator.Other;
            case 3:
                if (s.SequenceEqual("BDC"u8)) return TextOperator.BDC;
                if (s.SequenceEqual("BMC"u8)) return TextOperator.BMC;
                if (s.SequenceEqual("EMC"u8)) return TextOperator.EMC;
                return TextOperator.Other;
            default:
                return TextOperator.Other;
        }
    }
}
