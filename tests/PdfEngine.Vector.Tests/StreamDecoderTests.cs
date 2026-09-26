using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Streams;
using Xunit;

namespace PdfEngine.Vector.Tests;

public class StreamDecoderTests
{
    internal static PdfStream MakeStream(byte[] bytes, params (string Key, PdfObject Value)[] entries)
    {
        var dict = new Dictionary<string, PdfObject>();
        foreach (var (key, value) in entries)
            dict[key] = value;
        dict["Length"] = new PdfInteger(bytes.Length);
        return new PdfStream(new PdfDictionary(dict), 0, bytes.Length, null, bytes);
    }

    internal static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static PdfArray Arr(params PdfObject[] items) => new(items);

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new Dictionary<string, PdfObject>();
        foreach (var (k, v) in entries) d[k] = v;
        return new PdfDictionary(d);
    }

    // ---------------- Flate ----------------

    [Fact]
    public void Flate_RoundTrips()
    {
        byte[] payload = Encoding.ASCII.GetBytes("BT /F1 12 Tf (Hello) Tj ET");
        var stream = MakeStream(Zlib(payload), ("Filter", new PdfName("FlateDecode")));
        Assert.Equal(payload, new PdfStreamDecoder().DecodeStream(stream));
    }

    [Fact]
    public void Flate_DecompressionBomb_ThrowsResourceLimitWithoutFullAllocation()
    {
        // 200 MB of zeros compresses to ~200 KB.
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var chunk = new byte[1024 * 1024];
                for (int i = 0; i < 200; i++) z.Write(chunk);
            }
            compressed = ms.ToArray();
        }

        var limits = new PdfSecurityLimits { MaxDecodedStreamBytes = 1024 * 1024, MaxDecompressionRatio = double.MaxValue };
        var stream = MakeStream(compressed, ("Filter", new PdfName("FlateDecode")));
        var decoder = new PdfStreamDecoder(limits);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<PdfResourceLimitException>(() => decoder.DecodeStream(stream));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(nameof(PdfSecurityLimits.MaxDecodedStreamBytes), ex.LimitName);
        Assert.True(allocated < 32L * 1024 * 1024, $"Allocated {allocated} bytes while decoding a bomb.");
    }

    [Fact]
    public void Flate_RatioLimit_EnforcedOnlyAboveOneMiB()
    {
        var limits = new PdfSecurityLimits { MaxDecompressionRatio = 10 };
        var decoder = new PdfStreamDecoder(limits);

        // Tiny stream with a huge ratio is legitimate.
        var small = MakeStream(Zlib(new byte[100_000]), ("Filter", new PdfName("FlateDecode")));
        Assert.Equal(100_000, decoder.DecodeStream(small).Length);

        var big = MakeStream(Zlib(new byte[8 * 1024 * 1024]), ("Filter", new PdfName("FlateDecode")));
        var ex = Assert.Throws<PdfResourceLimitException>(() => decoder.DecodeStream(big));
        Assert.Equal(nameof(PdfSecurityLimits.MaxDecompressionRatio), ex.LimitName);
    }

    [Fact]
    public void RunLength_OutputIsBounded()
    {
        // Each 2-byte run expands to 128 bytes.
        var input = new List<byte>();
        for (int i = 0; i < 20_000; i++) { input.Add(129); input.Add(0xAA); }
        input.Add(128);
        var limits = new PdfSecurityLimits { MaxDecodedStreamBytes = 1024 * 1024, MaxDecompressionRatio = double.MaxValue };
        var stream = MakeStream(input.ToArray(), ("Filter", new PdfName("RunLengthDecode")));
        Assert.Throws<PdfResourceLimitException>(() => new PdfStreamDecoder(limits).DecodeStream(stream));
    }

    [Fact]
    public void Flate_WithPngPredictor_Decodes()
    {
        byte[] rows = [2, 1, 2, 3, 2, 1, 1, 1]; // Up predictor, 3 columns, 2 rows
        var stream = MakeStream(Zlib(rows),
            ("Filter", new PdfName("FlateDecode")),
            ("DecodeParms", Dict(("Predictor", new PdfInteger(12)), ("Columns", new PdfInteger(3)))));
        Assert.Equal(new byte[] { 1, 2, 3, 2, 3, 4 }, new PdfStreamDecoder().DecodeStream(stream));
    }

    // ---------------- LZW ----------------

    private static readonly byte[] SpecLzwInput = [0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01];
    private static readonly byte[] SpecLzwOutput = [0x2D, 0x2D, 0x2D, 0x2D, 0x2D, 0x41, 0x2D, 0x2D, 0x2D, 0x42];

    [Fact]
    public void Lzw_SpecExample_Decodes()
    {
        var stream = MakeStream(SpecLzwInput, ("Filter", new PdfName("LZWDecode")));
        Assert.Equal(SpecLzwOutput, new PdfStreamDecoder().DecodeStream(stream));

        var abbreviated = MakeStream(SpecLzwInput, ("Filter", new PdfName("LZW")));
        Assert.Equal(SpecLzwOutput, new PdfStreamDecoder().DecodeStream(abbreviated));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Lzw_LongInput_RoundTripsWithEarlyChange(int earlyChange)
    {
        var rng = new Random(1234);
        var data = new byte[20_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)rng.Next(0, 6); // compressible, forces code width growth and table reset
        byte[] encoded = LzwEncode(data, earlyChange);
        var stream = MakeStream(encoded,
            ("Filter", new PdfName("LZWDecode")),
            ("DecodeParms", Dict(("EarlyChange", new PdfInteger(earlyChange)))));
        Assert.Equal(data, new PdfStreamDecoder().DecodeStream(stream));
    }

    [Fact]
    public void Lzw_WithTiffPredictor_Decodes()
    {
        byte[] deltas = [10, 5, 5, 20, 1, 1];
        var stream = MakeStream(LzwEncode(deltas, 1),
            ("Filter", new PdfName("LZWDecode")),
            ("DecodeParms", Dict(("Predictor", new PdfInteger(2)), ("Columns", new PdfInteger(3)))));
        Assert.Equal(new byte[] { 10, 15, 20, 20, 21, 22 }, new PdfStreamDecoder().DecodeStream(stream));
    }

    [Fact]
    public void Lzw_Garbage_DoesNotThrowIndexOutOfRange()
    {
        var rng = new Random(7);
        for (int trial = 0; trial < 50; trial++)
        {
            var bytes = new byte[rng.Next(1, 300)];
            rng.NextBytes(bytes);
            var stream = MakeStream(bytes, ("Filter", new PdfName("LZWDecode")));
            try { new PdfStreamDecoder().DecodeStream(stream); }
            catch (PdfVectorException) { }
        }
    }

    /// <summary>Reference LZW encoder (ISO 32000-2 7.4.4.2) used to produce test vectors.</summary>
    private static byte[] LzwEncode(byte[] data, int earlyChange)
    {
        var output = new List<byte>();
        int bitBuf = 0, bitCount = 0;
        int codeLen = 9;
        void Emit(int code)
        {
            bitBuf = (bitBuf << codeLen) | code;
            bitCount += codeLen;
            while (bitCount >= 8)
            {
                output.Add((byte)(bitBuf >> (bitCount - 8)));
                bitCount -= 8;
                bitBuf &= (1 << bitCount) - 1;
            }
        }

        var table = new Dictionary<string, int>();
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++) table[((char)i).ToString()] = i;
        }

        Reset();
        int next = 258;
        Emit(256);
        string w = string.Empty;
        foreach (byte b in data)
        {
            string wc = w + (char)b;
            if (table.ContainsKey(wc)) { w = wc; continue; }
            Emit(table[w]);
            table[wc] = next++;
            if (next + earlyChange > (1 << codeLen) && codeLen < 12) codeLen++;
            if (next >= 4094)
            {
                Emit(256);
                Reset();
                next = 258;
                codeLen = 9;
            }
            w = ((char)b).ToString();
        }
        if (w.Length > 0)
        {
            Emit(table[w]);
            next++;
            if (next + earlyChange > (1 << codeLen) && codeLen < 12) codeLen++;
        }
        Emit(257);
        if (bitCount > 0) output.Add((byte)(bitBuf << (8 - bitCount)));
        return output.ToArray();
    }

    // ---------------- Filter chain / typed failures ----------------

    [Fact]
    public void UnknownFilter_ThrowsTypedUnsupported()
    {
        var stream = MakeStream([1, 2, 3], ("Filter", new PdfName("BogusDecode")));
        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() => new PdfStreamDecoder().DecodeStream(stream));
        Assert.Equal(PdfFallbackReason.UnsupportedImageFilter, ex.Reason);
    }

    [Fact]
    public void ImageFilter_InDecodeStream_ThrowsTypedUnsupported()
    {
        var stream = MakeStream([0xFF, 0xD8, 0xFF], ("Filter", new PdfName("DCTDecode")));
        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() => new PdfStreamDecoder().DecodeStream(stream));
        Assert.Equal(PdfFallbackReason.UnsupportedImageFilter, ex.Reason);
    }

    [Fact]
    public void DecodeImageStream_StopsAtDct_AfterApplyingPrecedingFilters()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(jpeg) + ">");
        var dctParms = Dict(("ColorTransform", new PdfInteger(0)));
        var stream = MakeStream(hex,
            ("Filter", Arr(new PdfName("AHx"), new PdfName("DCT"))),
            ("DecodeParms", Arr(PdfNull.Instance, dctParms)));

        var result = new PdfStreamDecoder().DecodeImageStream(stream);
        Assert.Equal(jpeg, result.Data);
        Assert.Equal("DCTDecode", result.ImageFilter);
        Assert.Same(dctParms, result.ImageFilterParms);
    }

    [Theory]
    [InlineData("JPXDecode", "JPXDecode")]
    [InlineData("JBIG2Decode", "JBIG2Decode")]
    [InlineData("CCITTFaxDecode", "CCITTFaxDecode")]
    [InlineData("CCF", "CCITTFaxDecode")]
    public void DecodeImageStream_NormalizesImageFilterNames(string filter, string expected)
    {
        var stream = MakeStream([1, 2, 3], ("Filter", new PdfName(filter)));
        var result = new PdfStreamDecoder().DecodeImageStream(stream);
        Assert.Equal(expected, result.ImageFilter);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Data);
    }

    [Fact]
    public void DecodeImageStream_NoImageFilter_ReturnsFullyDecoded()
    {
        byte[] payload = [9, 8, 7];
        var stream = MakeStream(Zlib(payload), ("Filter", new PdfName("FlateDecode")));
        var result = new PdfStreamDecoder().DecodeImageStream(stream);
        Assert.Equal(payload, result.Data);
        Assert.Null(result.ImageFilter);
        Assert.Null(result.ImageFilterParms);
    }

    [Fact]
    public void Crypt_Identity_IsNoOp_OtherCryptThrowsEncrypted()
    {
        byte[] payload = [1, 2, 3];
        var identity = MakeStream(payload,
            ("Filter", new PdfName("Crypt")),
            ("DecodeParms", Dict(("Name", new PdfName("Identity")))));
        Assert.Equal(payload, new PdfStreamDecoder().DecodeStream(identity));

        var noParms = MakeStream(payload, ("Filter", new PdfName("Crypt")));
        Assert.Equal(payload, new PdfStreamDecoder().DecodeStream(noParms));

        var named = MakeStream(payload,
            ("Filter", new PdfName("Crypt")),
            ("DecodeParms", Dict(("Name", new PdfName("StdCF")))));
        var ex = Assert.Throws<PdfUnsupportedFeatureException>(() => new PdfStreamDecoder().DecodeStream(named));
        Assert.Equal(PdfFallbackReason.EncryptedContent, ex.Reason);
    }

    [Fact]
    public void ResolveCallback_IsUsedForFilterAndDecodeParms()
    {
        byte[] rows = [2, 1, 2, 3, 2, 1, 1, 1];
        var filterRef = new PdfIndirectRef(10);
        var parmsRef = new PdfIndirectRef(11);
        var nameRef = new PdfIndirectRef(12);
        var objects = new Dictionary<PdfIndirectRef, PdfObject>
        {
            [filterRef] = Arr(nameRef),
            [nameRef] = new PdfName("FlateDecode"),
            [parmsRef] = Dict(("Predictor", new PdfInteger(12)), ("Columns", new PdfInteger(3))),
        };
        PdfObject? Resolve(PdfObject? o) => o is PdfIndirectRef r && objects.TryGetValue(r, out var v) ? v : o;

        var stream = MakeStream(Zlib(rows), ("Filter", filterRef), ("DecodeParms", parmsRef));
        Assert.Equal(new byte[] { 1, 2, 3, 2, 3, 4 }, new PdfStreamDecoder(null, Resolve).DecodeStream(stream));
    }

    [Fact]
    public void ChainedFilters_ApplyInOrder()
    {
        byte[] payload = Encoding.ASCII.GetBytes("chained filters work");
        byte[] hex = Encoding.ASCII.GetBytes(Convert.ToHexString(Zlib(payload)) + ">");
        var stream = MakeStream(hex, ("Filter", Arr(new PdfName("ASCIIHexDecode"), new PdfName("FlateDecode"))));
        Assert.Equal(payload, new PdfStreamDecoder().DecodeStream(stream));
    }

    // ---------------- ASCII85 ----------------

    [Fact]
    public void Ascii85_HelloWorld()
    {
        Assert.Equal("Hello World", Encoding.ASCII.GetString(PdfStreamDecoder.DecodeAscii85("87cURD]i,\"Ebo7~>"u8)));
    }

    [Fact]
    public void Ascii85_AcceptsLeadingDelimiterAndWhitespace()
    {
        Assert.Equal("Hello World", Encoding.ASCII.GetString(PdfStreamDecoder.DecodeAscii85("  <~87cUR\nD]i,\"Ebo7~>"u8)));
    }

    [Fact]
    public void Ascii85_ZAtGroupStart_IsFourZeros()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, PdfStreamDecoder.DecodeAscii85("zz~>"u8));
    }

    [Fact]
    public void Ascii85_ZInsideGroup_IsSyntaxError()
    {
        Assert.Throws<PdfSyntaxException>(() => PdfStreamDecoder.DecodeAscii85("87z~>"u8));
    }

    [Theory]
    [InlineData(1)] // 2 chars -> 1 byte
    [InlineData(2)] // 3 chars -> 2 bytes
    [InlineData(3)] // 4 chars -> 3 bytes
    [InlineData(5)] // full group + 2-char partial
    public void Ascii85_FinalPartialGroup(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(0xF0 + i);
        byte[] encoded = Ascii85Encode(data);
        Assert.Equal(length % 4 + 1 + (length / 4) * 5 + 2, encoded.Length);
        Assert.Equal(data, PdfStreamDecoder.DecodeAscii85(encoded));
    }

    [Fact]
    public void Ascii85_SingleTrailingChar_IsIgnored()
    {
        Assert.Equal("Hello Wo", Encoding.ASCII.GetString(PdfStreamDecoder.DecodeAscii85("87cURD]i,\"!~>"u8)));
    }

    [Fact]
    public void Ascii85_MissingEod_DecodesAvailableData()
    {
        Assert.Equal("Hello World", Encoding.ASCII.GetString(PdfStreamDecoder.DecodeAscii85("87cURD]i,\"Ebo7"u8)));
    }

    [Fact]
    public void Ascii85_GroupOverflow_IsSyntaxError()
    {
        Assert.Throws<PdfSyntaxException>(() => PdfStreamDecoder.DecodeAscii85("uuuuu~>"u8));
    }

    [Fact]
    public void Ascii85_EncodesAllByteValuesRoundTrip()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;
        Assert.Equal(data, PdfStreamDecoder.DecodeAscii85(Ascii85Encode(data)));
    }

    private static byte[] Ascii85Encode(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int j = 0; j < 4; j++) v = (v << 8) | (j < n ? data[i + j] : 0u);
            if (n == 4 && v == 0) { sb.Append('z'); continue; }
            var chars = new char[5];
            for (int j = 4; j >= 0; j--) { chars[j] = (char)('!' + v % 85); v /= 85; }
            sb.Append(chars, 0, n + 1);
        }
        sb.Append("~>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---------------- ASCIIHex ----------------

    [Fact]
    public void AsciiHex_OddDigitCount_PadsWithZero()
    {
        Assert.Equal(new byte[] { 0xAB, 0xC0 }, PdfStreamDecoder.DecodeAsciiHex("a b c>"u8));
    }

    // ---------------- Predictor robustness ----------------

    [Fact]
    public void Predictor_ShortFinalRow_IsTruncatedNotThrown()
    {
        // Two full PNG rows (3 columns) + a partial row with 2 of 3 data bytes.
        byte[] input = [0, 1, 2, 3, 2, 1, 1, 1, 2, 5, 5];
        byte[] decoded = PredictorDecoder.DecodePredictor(input, predictor: 12, columns: 3);
        Assert.Equal(new byte[] { 1, 2, 3, 2, 3, 4, 7, 8 }, decoded);
    }

    [Fact]
    public void Predictor_HugeColumns_DoesNotOverflowOrAllocate()
    {
        byte[] input = [0, 1, 2, 3];
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = PredictorDecoder.DecodePredictor(input, predictor: 15, columns: int.MaxValue, colors: 32, bitsPerComponent: 16);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded);

        byte[] tiff = PredictorDecoder.DecodePredictor(input, predictor: 2, columns: int.MaxValue, colors: 32, bitsPerComponent: 16);
        Assert.Equal(4, tiff.Length);
    }

    [Fact]
    public void Predictor_Tiff16Bit_AddsBigEndianSamples()
    {
        // 2 columns, 1 color, 16 bpc: samples 0x0102, delta 0x0001 -> 0x0102, 0x0103
        byte[] input = [0x01, 0x02, 0x00, 0x01];
        Assert.Equal(new byte[] { 0x01, 0x02, 0x01, 0x03 },
            PredictorDecoder.DecodePredictor(input, predictor: 2, columns: 2, colors: 1, bitsPerComponent: 16));
    }

    [Fact]
    public void Predictor_Tiff4Bit_AddsNibbles()
    {
        // 4 columns, 4 bpc: samples 1, +1, +1, +1 -> 1,2,3,4
        byte[] input = [0x11, 0x11];
        Assert.Equal(new byte[] { 0x12, 0x34 },
            PredictorDecoder.DecodePredictor(input, predictor: 2, columns: 4, colors: 1, bitsPerComponent: 4));
    }

    [Fact]
    public void Predictor_RandomInput_NeverThrows()
    {
        var rng = new Random(99);
        int[] predictors = [2, 10, 11, 12, 13, 14, 15];
        int[] bpcs = [1, 2, 4, 8, 16, 0, -3, 7];
        for (int trial = 0; trial < 300; trial++)
        {
            var input = new byte[rng.Next(0, 64)];
            rng.NextBytes(input);
            PredictorDecoder.DecodePredictor(input,
                predictors[rng.Next(predictors.Length)],
                rng.Next(-5, 40),
                rng.Next(-2, 40),
                bpcs[rng.Next(bpcs.Length)]);
        }
    }
}
