using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// CCITTFaxDecode (T.4 / T.6): codestreams produced by the Windows TIFF encoder (G4 = MMR,
/// G3 = Modified Huffman) decode to the source bitmap exactly and render like PDFium.
/// </summary>
public class CcittFaxTests
{
    private readonly ITestOutputHelper _output;
    public CcittFaxTests(ITestOutputHelper output) => _output = output;

    internal const int W = 301, H = 157;

    /// <summary>A bilevel test image: text, diagonal strokes, dots and a checkerboard (hard for run coding).</summary>
    internal static BitmapSource SourceImage()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            var tf = new Typeface("Arial");
            dc.DrawText(new FormattedText("CCITT fax 0123 ÄÖÜ", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 26, Brushes.Black, 1.0), new Point(8, 6));
            dc.DrawLine(new Pen(Brushes.Black, 3), new Point(5, 150), new Point(290, 50));
            dc.DrawEllipse(null, new Pen(Brushes.Black, 2), new Point(230, 110), 40, 30);
            for (int y = 0; y < 24; y++)
                for (int x = 0; x < 48; x++)
                    if (((x + y) & 1) == 0) dc.DrawRectangle(Brushes.Black, null, new Rect(10 + x, 110 + y, 1, 1));
            for (int i = 0; i < 40; i++) dc.DrawRectangle(Brushes.Black, null, new Rect(80 + i * 5, 70 + (i % 3), 2, 2));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return new FormatConvertedBitmap(rtb, PixelFormats.BlackWhite, null, 0.5);
    }

    /// <summary>1 = black per pixel.</summary>
    internal static byte[] BlackMask(BitmapSource bw)
    {
        int stride = (W + 7) / 8;
        var packed = new byte[stride * H];
        bw.CopyPixels(packed, stride, 0);
        var mask = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                mask[y * W + x] = (byte)(((packed[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1) == 0 ? 1 : 0); // BlackWhite: 0 = black
        return mask;
    }

    /// <summary>The single strip of a TIFF written with the given compression.</summary>
    internal static (byte[] Data, int Photometric) EncodeTiff(BitmapSource bw, TiffCompressOption compression)
    {
        var enc = new TiffBitmapEncoder { Compression = compression };
        enc.Frames.Add(BitmapFrame.Create(bw));
        using var ms = new MemoryStream();
        enc.Save(ms);
        byte[] tiff = ms.ToArray();
        bool le = tiff[0] == 'I';
        int U16(int o) => le ? tiff[o] | tiff[o + 1] << 8 : tiff[o] << 8 | tiff[o + 1];
        int U32(int o) => le ? tiff[o] | tiff[o + 1] << 8 | tiff[o + 2] << 16 | tiff[o + 3] << 24 : tiff[o] << 24 | tiff[o + 1] << 16 | tiff[o + 2] << 8 | tiff[o + 3];
        int ifd = U32(4), n = U16(ifd);
        int offsets = -1, counts = -1, stripCount = 0, photometric = 0;
        for (int i = 0; i < n; i++)
        {
            int e = ifd + 2 + i * 12, tag = U16(e), type = U16(e + 2), cnt = U32(e + 4);
            int Value() => type == 3 ? U16(e + 8) : U32(e + 8);
            if (tag == 273) { offsets = cnt == 1 ? Value() : U32(e + 8); stripCount = cnt; }
            if (tag == 279) counts = cnt == 1 ? Value() : U32(e + 8);
            if (tag == 262) photometric = Value();
        }
        Assert.Equal(1, stripCount); // one strip: the codestream is self-contained
        return (tiff.AsSpan(offsets, counts).ToArray(), photometric);
    }

    private static byte[] Decoded(byte[] packed, bool blackIs1)
    {
        int stride = (W + 7) / 8;
        var mask = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int bit = (packed[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
                mask[y * W + x] = (byte)(blackIs1 ? bit : 1 - bit);
            }
        return mask;
    }

    private async Task AssertPdfMatches(string name, byte[] data, string parms)
    {
        var b = new VectorPdfBuilder();
        int image = b.AddStream($"/Type /XObject /Subtype /Image /Width {W} /Height {H} /ColorSpace /DeviceGray /BitsPerComponent 1 " +
                                $"/Filter /CCITTFaxDecode /DecodeParms << {parms} >>", data);
        b.AddPage($"q {W} 0 0 {H} 0 0 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>", mediaBox: $"[0 0 {W} {H}]");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        Assert.False(list.HasFallback);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None);
        using var v = result.Page;
        Assert.Empty(result.Fallbacks);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"{name}: mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 1.0 && bad <= 0.005, $"{name}: mean {mean:F2} bad {bad:P2}");
    }

    [Fact]
    public async Task Group4_DecodesExactly_AndMatchesPdfium()
    {
        var src = SourceImage();
        var expected = BlackMask(src);
        var (g4, photometric) = EncodeTiff(src, TiffCompressOption.Ccitt4);
        _output.WriteLine($"G4: {g4.Length} bytes, photometric {photometric}");
        // WhiteIsZero (0): codec "white" runs are white pixels. BlackIsZero (1): they are black pixels.
        bool inverted = photometric == 1;
        var packed = CcittFaxDecoder.Decode(g4, new CcittFaxDecoder.Parameters(K: -1, Columns: W, Rows: H, BlackIs1: false), H);
        var mask = Decoded(packed, blackIs1: false);
        if (inverted) for (int i = 0; i < mask.Length; i++) mask[i] ^= 1;
        int diff = mask.Zip(expected, (a, e) => a != e ? 1 : 0).Sum();
        Assert.Equal(0, diff);
        await AssertPdfMatches("g4", g4, $"/K -1 /Columns {W} /Rows {H} /BlackIs1 {(inverted ? "true" : "false")}");
    }

    [Fact]
    public async Task Group3ModifiedHuffman_DecodesExactly_AndMatchesPdfium()
    {
        var src = SourceImage();
        var expected = BlackMask(src);
        var (g3, photometric) = EncodeTiff(src, TiffCompressOption.Ccitt3);
        _output.WriteLine($"G3: {g3.Length} bytes, photometric {photometric}");
        bool inverted = photometric == 1;
        // TIFF compression 3 without T4Options: one-dimensional T.4 rows, each preceded by an EOL.
        var parms = new CcittFaxDecoder.Parameters(K: 0, EndOfLine: true, Columns: W, Rows: H);
        var mask = Decoded(CcittFaxDecoder.Decode(g3, parms, H), blackIs1: false);
        if (inverted) for (int i = 0; i < mask.Length; i++) mask[i] ^= 1;
        Assert.Equal(0, mask.Zip(expected, (a, e) => a != e ? 1 : 0).Sum());
        await AssertPdfMatches("g3", g3, $"/K 0 /EndOfLine true /Columns {W} /Rows {H} /BlackIs1 {(inverted ? "true" : "false")}");
    }

    /// <summary>Packs a string of '0'/'1' (spaces ignored) into bytes, zero-padded.</summary>
    private static byte[] Bits(string bits)
    {
        bits = bits.Replace(" ", "");
        var bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i] == '1') bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    [Fact]
    public async Task MixedOneAndTwoDimensional_K1_WithEolsAndRtc()
    {
        const string Eol = "000000000001 ";
        // 16 × 3: row 0 white (1D), row 1 black 4..7 (2D: H + W4 + B4, V0), row 2 black 5..8 (2D: VR1, VR1, V0), RTC.
        var data = Bits(Eol + "1 101010 " + Eol + "0 001 1011 011 1 " + Eol + "0 011 011 1 " +
                        string.Concat(Enumerable.Repeat(Eol + "1 ", 6)));
        var packed = CcittFaxDecoder.Decode(data, new CcittFaxDecoder.Parameters(K: 1, EndOfLine: true, Columns: 16, Rows: 3), 3);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xF0, 0xFF, 0xF8, 0x7F }, packed);

        var b = new VectorPdfBuilder();
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 1 " +
                                "/Filter /CCITTFaxDecode /DecodeParms << /K 1 /EndOfLine true /Columns 16 /Rows 3 >>", data);
        b.AddPage("q 160 0 0 30 20 20 cm /Im1 Do Q", $"<< /XObject << /Im1 {image} 0 R >> >>");
        var pdf = b.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        using var v = await renderer.RenderDisplayListAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 });
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(pdf);
        using var p = await engine.Renderer.RenderPageAsync(pdoc, new RenderRequest { PageNumber = 1, Dpi = 72 });
        var (mean, bad) = DifferentialRenderingTests.Compare(v, p);
        _output.WriteLine($"k1: mean={mean:F2} bad={bad:P2}");
        Assert.True(mean <= 1.0 && bad <= 0.005, $"k1: mean {mean:F2} bad {bad:P2}");
    }

    [Fact]
    public void TruncatedAndCorruptStreams_FailSoft()
    {
        var src = SourceImage();
        var (g4, _) = EncodeTiff(src, TiffCompressOption.Ccitt4);
        var rng = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            var bytes = (byte[])g4.Clone();
            for (int f = 0; f < 1 + rng.Next(6); f++) bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
            if (rng.Next(3) == 0) Array.Resize(ref bytes, rng.Next(1, bytes.Length));
            var packed = CcittFaxDecoder.Decode(bytes, new CcittFaxDecoder.Parameters(K: -1, Columns: W, Rows: H), H);
            Assert.Equal(((W + 7) / 8) * H, packed.Length);
        }
    }
}
