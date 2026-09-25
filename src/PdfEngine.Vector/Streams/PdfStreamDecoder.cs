using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Streams;

/// <summary>
/// Result of <see cref="PdfStreamDecoder.DecodeImageStream"/>: the stream data after every general-purpose
/// filter has been applied, plus the first image codec filter (if any) that was left for an image decoder.
/// </summary>
/// <param name="Data">Decoded bytes; still encoded by <paramref name="ImageFilter"/> when that is non-null.</param>
/// <param name="ImageFilter">Normalised image filter name ("DCTDecode", "JPXDecode", "JBIG2Decode", "CCITTFaxDecode"), or null.</param>
/// <param name="ImageFilterParms">The /DecodeParms entry belonging to <paramref name="ImageFilter"/>, if any.</param>
public sealed record PdfDecodedStream(byte[] Data, string? ImageFilter, PdfDictionary? ImageFilterParms);

/// <summary>
/// Stream filter pipeline decoder (ISO 32000-2 Clause 7.4) with decompression ratio and memory bomb guards.
/// Every filter writes into a <see cref="BoundedOutputBuffer"/>, so the ceilings in
/// <see cref="PdfSecurityLimits"/> are enforced while decoding rather than after the fact.
/// </summary>
public sealed class PdfStreamDecoder
{
    private const int FlateChunkSize = 64 * 1024;

    private readonly PdfSecurityLimits _limits;
    private readonly Func<PdfObject?, PdfObject?> _resolve;

    /// <summary>Creates a decoder.</summary>
    /// <param name="limits">Security ceilings; defaults to <see cref="PdfSecurityLimits.Default"/>.</param>
    /// <param name="resolve">
    /// Optional indirect-object resolver used for /Filter, /DecodeParms and their array elements
    /// (typically <c>PdfObjectResolver.Resolve</c>). Defaults to identity.
    /// </param>
    public PdfStreamDecoder(PdfSecurityLimits? limits = null, Func<PdfObject?, PdfObject?>? resolve = null)
    {
        _limits = limits ?? PdfSecurityLimits.Default;
        _resolve = resolve ?? (static o => o);
    }

    /// <summary>
    /// Fully decodes a stream. Throws <see cref="PdfUnsupportedFeatureException"/> with
    /// <see cref="PdfFallbackReason.UnsupportedImageFilter"/> if the chain contains an image codec
    /// filter or an unknown filter; use <see cref="DecodeImageStream"/> for image XObjects.
    /// </summary>
    public byte[] DecodeStream(PdfStream stream)
    {
        var result = Decode(stream, stopAtImageFilter: false);
        return result.Data;
    }

    /// <summary>
    /// Applies all general-purpose filters in order and stops at the first image codec filter
    /// (DCTDecode, JPXDecode, JBIG2Decode, CCITTFaxDecode), returning the still-encoded bytes and that
    /// filter's normalised name and parameters.
    /// </summary>
    public PdfDecodedStream DecodeImageStream(PdfStream stream) => Decode(stream, stopAtImageFilter: true);

    private PdfDecodedStream Decode(PdfStream stream, bool stopAtImageFilter)
    {
        ReadOnlyMemory<byte> rawData = stream.GetRawBytes();
        var filters = GetFilters(stream.Dictionary);

        if (filters.Count == 0)
            return new PdfDecodedStream(rawData.ToArray(), null, null);

        byte[] current = rawData.ToArray();
        for (int i = 0; i < filters.Count; i++)
        {
            var (name, parms) = filters[i];
            string? imageFilter = NormalizeImageFilter(name);
            if (imageFilter != null)
            {
                if (stopAtImageFilter)
                    return new PdfDecodedStream(current, imageFilter, parms);

                throw new PdfUnsupportedFeatureException(
                    PdfFallbackReason.UnsupportedImageFilter,
                    $"Image codec filter /{name} requires an image decoder; use DecodeImageStream.");
            }

            current = DecodeFilter(current, name, parms);
        }

        return new PdfDecodedStream(current, null, null);
    }

    private static string? NormalizeImageFilter(string name) => name switch
    {
        "DCTDecode" or "DCT" => "DCTDecode",
        "JPXDecode" => "JPXDecode",
        "JBIG2Decode" => "JBIG2Decode",
        "CCITTFaxDecode" or "CCF" => "CCITTFaxDecode",
        _ => null,
    };

    private List<(string Name, PdfDictionary? Parms)> GetFilters(PdfDictionary dict)
    {
        var result = new List<(string, PdfDictionary?)>();

        // /F and /DP are the inline-image abbreviations (ISO 32000-2 Table 91). In a regular stream
        // dictionary /F is a file specification (a string or dictionary), so it is only honoured as a
        // filter when it is shaped like one.
        PdfObject? filterObj = _resolve(dict["Filter"]);
        if (filterObj == null && _resolve(dict["F"]) is PdfName or PdfArray)
            filterObj = _resolve(dict["F"]);
        PdfObject? parmsObj = _resolve(dict["DecodeParms"] ?? dict["DP"]);

        var names = new List<string>();
        switch (filterObj)
        {
            case null or PdfNull:
                return result;
            case PdfName single:
                names.Add(single.Value);
                break;
            case PdfArray array:
                foreach (var item in array)
                {
                    if (_resolve(item) is PdfName n)
                        names.Add(n.Value);
                    else
                        throw new PdfSyntaxException("Stream /Filter array contains a non-name entry.");
                }
                break;
            default:
                throw new PdfSyntaxException("Stream /Filter must be a name or an array of names.");
        }

        for (int i = 0; i < names.Count; i++)
        {
            PdfDictionary? parms = parmsObj switch
            {
                // A single dictionary is paired with the (first) filter; being lenient, every filter sees it
                // and only reads the keys it understands.
                PdfDictionary d => d,
                PdfArray arr when i < arr.Count => _resolve(arr[i]) as PdfDictionary,
                _ => null,
            };
            result.Add((names[i], parms));
        }

        return result;
    }

    private byte[] DecodeFilter(byte[] input, string filterName, PdfDictionary? parms)
    {
        var output = BoundedOutputBuffer.For(_limits, input.Length);

        switch (filterName)
        {
            case "FlateDecode" or "Fl":
                DecodeFlate(input, output);
                return ApplyPredictor(output.ToArray(), parms);

            case "LZWDecode" or "LZW":
                int earlyChange = (int)(parms?.GetInteger("EarlyChange") ?? 1);
                LzwDecoder.Decode(input, earlyChange != 0, output);
                return ApplyPredictor(output.ToArray(), parms);

            case "ASCIIHexDecode" or "AHx":
                DecodeAsciiHex(input, output);
                return output.ToArray();

            case "ASCII85Decode" or "A85":
                DecodeAscii85(input, output);
                return output.ToArray();

            case "RunLengthDecode" or "RL":
                DecodeRunLength(input, output);
                return output.ToArray();

            case "Crypt":
                // ISO 32000-2 7.4.10: /Identity (the default when /Name is absent) passes data through.
                string cryptName = parms?.GetName("Name") ?? "Identity";
                if (cryptName == "Identity")
                    return input;
                throw new PdfUnsupportedFeatureException(
                    PdfFallbackReason.EncryptedContent,
                    $"Stream uses crypt filter /{cryptName}; no security handler is available.");

            default:
                throw new PdfUnsupportedFeatureException(
                    PdfFallbackReason.UnsupportedImageFilter,
                    $"Unsupported stream filter /{filterName}.");
        }
    }

    private byte[] ApplyPredictor(byte[] data, PdfDictionary? parms)
    {
        if (parms == null)
            return data;

        int predictor = ClampToInt(parms.GetInteger("Predictor") ?? 1);
        if (predictor <= 1)
            return data;

        int columns = ClampToInt(parms.GetInteger("Columns") ?? 1);
        int colors = ClampToInt(parms.GetInteger("Colors") ?? 1);
        int bpc = ClampToInt(parms.GetInteger("BitsPerComponent") ?? 8);
        return PredictorDecoder.DecodePredictor(data, predictor, columns, colors, bpc);
    }

    private static int ClampToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    /// <summary>
    /// Inflates zlib (RFC 1950) data, falling back to raw deflate (RFC 1951) when the zlib header is
    /// missing or corrupt. Output is streamed in chunks into the bounded buffer. Truncated or corrupt
    /// trailing data is tolerated once some output has been produced (common in real-world files).
    /// </summary>
    private void DecodeFlate(byte[] input, BoundedOutputBuffer output)
    {
        if (input.Length == 0)
            return;

        if (TryInflate(input, 0, zlib: true, output))
            return;

        // Zlib failed without producing output: retry as raw deflate, skipping a plausible zlib header.
        int offset = input.Length > 2 && (input[0] & 0x0F) == 8 ? 2 : 0;
        if (TryInflate(input, offset, zlib: false, output))
            return;

        throw new PdfSyntaxException("FlateDecode stream data is corrupt.");
    }

    private static bool TryInflate(byte[] input, int offset, bool zlib, BoundedOutputBuffer output)
    {
        int startLength = output.Length;
        byte[] chunk = ArrayPool<byte>.Shared.Rent(FlateChunkSize);
        try
        {
            using var inStream = new MemoryStream(input, offset, input.Length - offset, writable: false);
            using Stream inflater = zlib
                ? new ZLibStream(inStream, CompressionMode.Decompress)
                : new DeflateStream(inStream, CompressionMode.Decompress);

            int read;
            while ((read = inflater.Read(chunk, 0, FlateChunkSize)) > 0)
            {
                output.Write(chunk.AsSpan(0, read));
            }
            return true;
        }
        catch (InvalidDataException)
        {
            // Keep partial output of a truncated/corrupt stream; retry only when nothing was produced.
            return output.Length > startLength;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Decodes ASCIIHexDecode data (ISO 32000-2 7.4.2). Non-hex, non-whitespace bytes are skipped.</summary>
    public static byte[] DecodeAsciiHex(ReadOnlySpan<byte> input)
    {
        var output = BoundedOutputBuffer.Unbounded(input.Length / 2 + 1);
        DecodeAsciiHex(input, output);
        return output.ToArray();
    }

    private static void DecodeAsciiHex(ReadOnlySpan<byte> input, BoundedOutputBuffer output)
    {
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
                output.WriteByte((byte)((firstNibble << 4) | nibble));
                firstNibble = -1;
            }
        }

        // An odd final digit behaves as if followed by 0 (ISO 32000-2 7.4.2).
        if (firstNibble >= 0)
            output.WriteByte((byte)(firstNibble << 4));
    }

    /// <summary>
    /// Decodes ASCII85Decode data (ISO 32000-2 7.4.3). Accepts an optional leading "&lt;~", treats "~" as EOD,
    /// expands "z" only at a group boundary, and decodes a final partial group of 2-4 characters to 1-3 bytes.
    /// </summary>
    /// <exception cref="PdfSyntaxException">"z" inside a group, or a group whose value exceeds 2^32 - 1.</exception>
    public static byte[] DecodeAscii85(ReadOnlySpan<byte> input)
    {
        var output = BoundedOutputBuffer.Unbounded(input.Length);
        DecodeAscii85(input, output);
        return output.ToArray();
    }

    private static void DecodeAscii85(ReadOnlySpan<byte> input, BoundedOutputBuffer output)
    {
        int start = 0;
        while (start < input.Length && PdfLexer.IsWhitespace(input[start])) start++;
        if (start + 1 < input.Length && input[start] == '<' && input[start + 1] == '~')
            start += 2;

        Span<byte> tuple = stackalloc byte[5];
        int count = 0;

        for (int i = start; i < input.Length; i++)
        {
            byte b = input[i];
            if (b == '~') break; // "~>" EOD; a lone '~' is also treated as end of data.
            if (PdfLexer.IsWhitespace(b)) continue;

            if (b == 'z')
            {
                if (count != 0)
                    throw new PdfSyntaxException("ASCII85 'z' appears inside a 5-character group.");
                output.Fill(0, 4);
                continue;
            }

            if (b < '!' || b > 'u')
                continue; // Invalid characters are skipped rather than aborting the stream.

            tuple[count++] = (byte)(b - '!');
            if (count == 5)
            {
                WriteAscii85Group(tuple, 4, output);
                count = 0;
            }
        }

        // A final partial group of n (2..4) characters is padded with 'u' and yields n-1 bytes.
        // A single leftover character carries no complete byte and is ignored.
        if (count > 1)
        {
            for (int i = count; i < 5; i++) tuple[i] = 84;
            WriteAscii85Group(tuple, count - 1, output);
        }
    }

    private static void WriteAscii85Group(ReadOnlySpan<byte> tuple, int byteCount, BoundedOutputBuffer output)
    {
        ulong value = 0;
        for (int i = 0; i < 5; i++) value = value * 85 + tuple[i];
        if (value > uint.MaxValue)
            throw new PdfSyntaxException("ASCII85 group value exceeds 2^32 - 1.");

        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(value >> 24);
        bytes[1] = (byte)(value >> 16);
        bytes[2] = (byte)(value >> 8);
        bytes[3] = (byte)value;
        output.Write(bytes[..byteCount]);
    }

    /// <summary>Decodes RunLengthDecode data (ISO 32000-2 7.4.5). Truncated runs are dropped.</summary>
    public static byte[] DecodeRunLength(ReadOnlySpan<byte> input)
    {
        var output = BoundedOutputBuffer.Unbounded(input.Length);
        DecodeRunLength(input, output);
        return output.ToArray();
    }

    private static void DecodeRunLength(ReadOnlySpan<byte> input, BoundedOutputBuffer output)
    {
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
                if (i + count > input.Length) break;
                output.Write(input.Slice(i, count));
                i += count;
            }
            else
            {
                // Replicated byte (257 - len) times
                if (i >= input.Length) break;
                output.Fill(input[i++], 257 - len);
            }
        }
    }
}
