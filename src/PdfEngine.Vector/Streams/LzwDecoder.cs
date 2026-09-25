using System;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// LZWDecode filter (ISO 32000-2 Clause 7.4.4.2): variable-width codes of 9 to 12 bits, MSB first,
/// clear-table code 256, EOD code 257, first table entry 258.
/// </summary>
internal static class LzwDecoder
{
    private const int ClearTable = 256;
    private const int EndOfData = 257;
    private const int FirstEntry = 258;
    private const int MaxEntries = 4096;

    /// <summary>
    /// Decodes <paramref name="input"/> into <paramref name="output"/>. Malformed codes end decoding
    /// (output is truncated) instead of throwing.
    /// </summary>
    /// <param name="earlyChange">
    /// /EarlyChange (default 1): when true the code width grows one code early (ISO 32000-2 Table 10).
    /// </param>
    public static void Decode(ReadOnlySpan<byte> input, bool earlyChange, BoundedOutputBuffer output)
    {
        int early = earlyChange ? 1 : 0;

        // Table entry i = prefix entry + suffix byte; lengths make output a single backwards walk.
        var prefix = new short[MaxEntries];
        var suffix = new byte[MaxEntries];
        var length = new short[MaxEntries];
        var firstByte = new byte[MaxEntries];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            length[i] = 1;
            firstByte[i] = (byte)i;
        }

        Span<byte> scratch = stackalloc byte[MaxEntries];
        int next = FirstEntry;
        int codeLength = 9;
        int previous = -1;

        int bitBuffer = 0;
        int bitCount = 0;
        int position = 0;

        while (true)
        {
            while (bitCount < codeLength)
            {
                if (position >= input.Length)
                    return; // Missing EOD: accept what was decoded.
                bitBuffer = (bitBuffer << 8) | input[position++];
                bitCount += 8;
            }

            bitCount -= codeLength;
            int code = (bitBuffer >> bitCount) & ((1 << codeLength) - 1);
            bitBuffer &= (1 << bitCount) - 1;

            if (code == ClearTable)
            {
                next = FirstEntry;
                codeLength = 9;
                previous = -1;
                continue;
            }

            if (code == EndOfData)
                return;

            if (previous < 0)
            {
                if (code >= 256)
                    return; // First code after a clear must be a literal.
                output.WriteByte((byte)code);
                previous = code;
                continue;
            }

            byte first;
            if (code < next)
            {
                first = firstByte[code];
                WriteEntry(code, prefix, suffix, length, scratch, output);
            }
            else if (code == next)
            {
                // KwKwK case: the entry being defined is previous + first byte of previous.
                first = firstByte[previous];
                WriteEntry(previous, prefix, suffix, length, scratch, output);
                output.WriteByte(first);
            }
            else
            {
                return; // Code references an undefined entry: malformed, truncate.
            }

            if (next < MaxEntries)
            {
                prefix[next] = (short)previous;
                suffix[next] = first;
                length[next] = (short)Math.Min(length[previous] + 1, MaxEntries);
                firstByte[next] = firstByte[previous];
                next++;
            }

            if (next + early >= (1 << codeLength) && codeLength < 12)
                codeLength++;

            previous = code;
        }
    }

    private static void WriteEntry(int code, short[] prefix, byte[] suffix, short[] length, Span<byte> scratch, BoundedOutputBuffer output)
    {
        int len = length[code];
        int index = len;
        int current = code;
        while (current >= 0 && index > 0)
        {
            scratch[--index] = suffix[current];
            current = prefix[current];
        }
        output.Write(scratch.Slice(index, len - index));
    }
}
