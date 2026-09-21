using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// Stream filter pipeline decoder with decompression ratio and memory bomb guards.
/// </summary>
public sealed class PdfStreamDecoder
{
    private readonly PdfSecurityLimits _limits;

    public PdfStreamDecoder(PdfSecurityLimits? limits = null)
    {
        _limits = limits ?? PdfSecurityLimits.Default;
    }

    public byte[] DecodeStream(PdfStream stream)
    {
        ReadOnlyMemory<byte> rawData = stream.GetRawBytes();
        if (rawData.IsEmpty)
            return Array.Empty<byte>();

        var dict = stream.Dictionary;
        if (!dict.TryGetValue("Filter", out var filterObj))
        {
            // Uncompressed stream
            return rawData.ToArray();
        }

        // Filters can be a single name or an array of filter names applied sequentially
        var filters = new List<string>();
        if (filterObj is PdfName singleFilter)
        {
            filters.Add(singleFilter.Value);
        }
        else if (filterObj is PdfArray filterArray)
        {
            foreach (var item in filterArray)
            {
                if (item is PdfName name)
                {
                    filters.Add(name.Value);
                }
            }
        }

        byte[] current = rawData.ToArray();
        for (int i = 0; i < filters.Count; i++)
        {
            string filter = filters[i];
            PdfDictionary? decodeParms = GetDecodeParms(dict, i);
            current = DecodeFilter(current, filter, decodeParms);
        }

        return current;
    }

    private byte[] DecodeFilter(byte[] input, string filterName, PdfDictionary? parms)
    {
        byte[] decoded = filterName switch
        {
            "FlateDecode" or "Fl" => DecodeFlate(input, parms),
            "ASCIIHexDecode" or "AHx" => DecodeAsciiHex(input),
            "ASCII85Decode" or "A85" => DecodeAscii85(input),
            "RunLengthDecode" or "RL" => DecodeRunLength(input),
            _ => input // Unsupported filter: return raw to allow fallback/diagnostics
        };

        // Decompression expansion check
        if (decoded.Length > _limits.MaxDecodedStreamBytes)
        {
            throw new InvalidDataException(
                $"Decoded stream size ({decoded.Length} bytes) exceeded security ceiling ({_limits.MaxDecodedStreamBytes} bytes).");
        }

        if (input.Length > 0 && (double)decoded.Length / input.Length > _limits.MaxDecompressionRatio)
        {
            throw new InvalidDataException(
                $"Decompression ratio ({(double)decoded.Length / input.Length:F1}x) exceeded safety threshold ({_limits.MaxDecompressionRatio}x).");
        }

        return decoded;
    }

    private byte[] DecodeFlate(byte[] input, PdfDictionary? parms)
    {
        byte[] uncompressed;
        using (var inStream = new MemoryStream(input))
        {
            using var outStream = new MemoryStream();
            try
            {
                // .NET 9 ZLibStream automatically handles zlib header/checksum
                using (var zlib = new ZLibStream(inStream, CompressionMode.Decompress))
                {
                    zlib.CopyTo(outStream);
                }
                uncompressed = outStream.ToArray();
            }
            catch
            {
                // Fallback: try raw DeflateStream if zlib header was absent or malformed
                inStream.Position = 0;
                outStream.SetLength(0);
                // If standard 2-byte zlib header (0x78 0x9C etc) is present, skip it
                if (input.Length > 2 && (input[0] & 0x0F) == 8)
                {
                    inStream.Position = 2;
                }
                using (var deflate = new DeflateStream(inStream, CompressionMode.Decompress))
                {
                    deflate.CopyTo(outStream);
                }
                uncompressed = outStream.ToArray();
            }
        }

        // Apply predictor post-processing if specified
        if (parms != null)
        {
            int predictor = (int)(parms.GetInteger("Predictor") ?? 1);
            int columns = (int)(parms.GetInteger("Columns") ?? 1);
            int colors = (int)(parms.GetInteger("Colors") ?? 1);
            int bpc = (int)(parms.GetInteger("BitsPerComponent") ?? 8);

            if (predictor > 1)
            {
                return PredictorDecoder.DecodePredictor(uncompressed, predictor, columns, colors, bpc);
            }
        }

        return uncompressed;
    }

    public static byte[] DecodeAsciiHex(ReadOnlySpan<byte> input)
    {
        using var ms = new MemoryStream();
        int firstNibble = -1;

        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == '>') break; // EOD marker
            if (PdfLexer.IsWhitespace(b)) continue;

            int nibble;
            if (b >= '0' && b <= '9') nibble = b - '0';
            else if (b >= 'a' && b <= 'f') nibble = b - 'a' + 10;
            else if (b >= 'A' && b <= 'F') nibble = b - 'A' + 10;
            else continue;

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

        if (firstNibble >= 0)
        {
            ms.WriteByte((byte)(firstNibble << 4));
        }

        return ms.ToArray();
    }

    public static byte[] DecodeAscii85(ReadOnlySpan<byte> input)
    {
        using var ms = new MemoryStream();
        Span<byte> tuple = stackalloc byte[5];
        int count = 0;

        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == '~')
            {
                // Check for ~> EOD
                if (i + 1 < input.Length && input[i + 1] == '>')
                    break;
            }
            if (PdfLexer.IsWhitespace(b)) continue;

            if (b == 'z' && count == 0)
            {
                // 'z' represents 4 zero bytes
                ms.Write([0, 0, 0, 0]);
                continue;
            }

            if (b >= '!' && b <= 'u')
            {
                tuple[count++] = (byte)(b - '!');
                if (count == 5)
                {
                    uint val = (uint)(
                        tuple[0] * 85 * 85 * 85 * 85 +
                        tuple[1] * 85 * 85 * 85 +
                        tuple[2] * 85 * 85 +
                        tuple[3] * 85 +
                        tuple[4]);

                    ms.WriteByte((byte)((val >> 24) & 0xFF));
                    ms.WriteByte((byte)((val >> 16) & 0xFF));
                    ms.WriteByte((byte)((val >> 8) & 0xFF));
                    ms.WriteByte((byte)(val & 0xFF));
                    count = 0;
                }
            }
        }

        if (count > 1)
        {
            // Pad remaining tuple elements with 'u' (84)
            for (int i = count; i < 5; i++)
            {
                tuple[i] = 84;
            }

            uint val = (uint)(
                tuple[0] * 85 * 85 * 85 * 85 +
                tuple[1] * 85 * 85 * 85 +
                tuple[2] * 85 * 85 +
                tuple[3] * 85 +
                tuple[4]);

            for (int i = 0; i < count - 1; i++)
            {
                ms.WriteByte((byte)((val >> (24 - i * 8)) & 0xFF));
            }
        }

        return ms.ToArray();
    }

    public static byte[] DecodeRunLength(ReadOnlySpan<byte> input)
    {
        using var ms = new MemoryStream();
        int i = 0;

        while (i < input.Length)
        {
            byte len = input[i++];
            if (len == 128)
                break; // EOD

            if (len < 128)
            {
                // Literal run of len + 1 bytes
                int count = len + 1;
                if (i + count <= input.Length)
                {
                    ms.Write(input.Slice(i, count));
                    i += count;
                }
                else break;
            }
            else
            {
                // Replicated byte (257 - len) times
                int count = 257 - len;
                if (i < input.Length)
                {
                    byte repeatByte = input[i++];
                    for (int k = 0; k < count; k++)
                    {
                        ms.WriteByte(repeatByte);
                    }
                }
                else break;
            }
        }

        return ms.ToArray();
    }

    private static PdfDictionary? GetDecodeParms(PdfDictionary dict, int index)
    {
        if (!dict.TryGetValue("DecodeParms", out var parmsObj))
            return null;

        if (parmsObj is PdfDictionary d)
            return d;

        if (parmsObj is PdfArray arr && index < arr.Count && arr[index] is PdfDictionary itemDict)
            return itemDict;

        return null;
    }
}
