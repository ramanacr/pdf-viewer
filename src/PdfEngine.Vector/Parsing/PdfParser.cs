using System;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// Recursive descent parser that converts PDF token streams into high-level PdfObject graphs.
/// </summary>
public sealed class PdfParser
{
    private readonly PdfSecurityLimits _limits;

    public PdfParser(PdfSecurityLimits? limits = null)
    {
        _limits = limits ?? PdfSecurityLimits.Default;
    }

    public PdfObject? ParseObject(PdfLexer lexer, int depth = 0)
    {
        if (depth > _limits.MaxNestingDepth)
            throw new InvalidDataException($"PDF object nesting depth exceeded limit ({_limits.MaxNestingDepth}).");

        var token = lexer.NextToken();
        if (token.Type == PdfTokenType.EndOfFile)
            return null;

        switch (token.Type)
        {
            case PdfTokenType.Integer:
            {
                // Lookahead to check if this is an indirect reference: num gen R
                long savedPos = lexer.Position;
                var next1 = lexer.NextToken();
                if (next1.Type == PdfTokenType.Integer)
                {
                    var next2 = lexer.NextToken();
                    if (next2.Type == PdfTokenType.Keyword && next2.TextValue == "R")
                    {
                        return new PdfIndirectRef((int)token.IntValue, (int)next1.IntValue);
                    }
                }

                // Not an indirect ref; rewind to after the first integer
                lexer.Position = savedPos;
                return new PdfInteger(token.IntValue);
            }

            case PdfTokenType.Real:
                return new PdfReal(token.RealValue);

            case PdfTokenType.Name:
                return new PdfName(token.TextValue);

            case PdfTokenType.String:
                return new PdfString(token.BinaryValue, IsHex: false);

            case PdfTokenType.HexString:
                return new PdfString(token.BinaryValue, IsHex: true);

            case PdfTokenType.Keyword:
                if (string.Equals(token.TextValue, "true", StringComparison.Ordinal))
                    return PdfBoolean.True;
                if (string.Equals(token.TextValue, "false", StringComparison.Ordinal))
                    return PdfBoolean.False;
                if (string.Equals(token.TextValue, "null", StringComparison.Ordinal))
                    return PdfNull.Instance;
                return null;

            case PdfTokenType.BeginArray:
            {
                var list = new List<PdfObject>();
                while (true)
                {
                    long itemPos = lexer.Position;
                    var peek = lexer.NextToken();
                    if (peek.Type == PdfTokenType.EndArray || peek.Type == PdfTokenType.EndOfFile)
                        break;

                    lexer.Position = itemPos;
                    var item = ParseObject(lexer, depth + 1);
                    if (item != null)
                    {
                        list.Add(item);
                    }
                }
                return new PdfArray(list);
            }

            case PdfTokenType.BeginDictionary:
            {
                var dict = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                while (true)
                {
                    var keyToken = lexer.NextToken();
                    if (keyToken.Type == PdfTokenType.EndDictionary || keyToken.Type == PdfTokenType.EndOfFile)
                        break;

                    if (keyToken.Type != PdfTokenType.Name)
                    {
                        // Resilient recovery: skip unexpected token
                        continue;
                    }

                    var value = ParseObject(lexer, depth + 1);
                    if (value != null)
                    {
                        dict[keyToken.TextValue] = value;
                    }
                }

                var pdfDict = new PdfDictionary(dict);

                // Check if followed by stream keyword
                long afterDictPos = lexer.Position;
                var streamToken = lexer.NextToken();
                if (streamToken.Type == PdfTokenType.Keyword && streamToken.TextValue == "stream")
                {
                    return ParseStream(lexer, pdfDict);
                }

                lexer.Position = afterDictPos;
                return pdfDict;
            }

            default:
                return null;
        }
    }

    private PdfStream ParseStream(PdfLexer lexer, PdfDictionary dict)
    {
        // Skip single EOL after 'stream' keyword (either CRLF or LF)
        var source = lexer.Source;
        int b = source.ReadByte();
        if (b == 0x0D)
        {
            int next = source.ReadByte();
            if (next >= 0 && next != 0x0A)
            {
                source.Seek(-1, SeekOrigin.Current);
            }
        }
        else if (b != 0x0A && b >= 0)
        {
            source.Seek(-1, SeekOrigin.Current);
        }

        long streamStart = source.Position;
        long streamLength = -1;

        if (dict.TryGetValue("Length", out var lenObj) && lenObj is PdfInteger lenInt)
        {
            streamLength = lenInt.Value;
        }

        if (streamLength >= 0 && streamStart + streamLength <= source.Length)
        {
            // Direct seek to end of stream
            source.Seek(streamStart + streamLength);
            // Verify endstream
            var endToken = lexer.NextToken();
            if (endToken.Type == PdfTokenType.Keyword && endToken.TextValue == "endstream")
            {
                return new PdfStream(dict, streamStart, streamLength, source);
            }
        }

        // Fallback: search for "endstream" marker
        source.Position = streamStart;
        long foundLength = ScanForEndstream(source);
        return new PdfStream(dict, streamStart, Math.Max(0, foundLength), source);
    }

    private static long ScanForEndstream(IPdfByteSource source)
    {
        long start = source.Position;
        ReadOnlySpan<byte> target = "endstream"u8;
        Span<byte> window = stackalloc byte[9];

        while (true)
        {
            long current = source.Position;
            int read = source.Read(window);
            if (read < target.Length)
                break;

            if (window.SequenceEqual(target))
            {
                // Found endstream
                source.Position = current;
                long len = current - start;
                // Trim trailing CR or LF if present
                if (len > 0)
                {
                    source.Position = current - 1;
                    int prev = source.ReadByte();
                    if (prev == 0x0A) len--;
                    if (len > 0)
                    {
                        source.Position = start + len - 1;
                        prev = source.ReadByte();
                        if (prev == 0x0D) len--;
                    }
                }
                return len;
            }

            source.Position = current + 1;
        }

        return source.Length - start;
    }
}
