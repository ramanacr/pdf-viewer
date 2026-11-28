using System.Buffers.Binary;
using PdfEngine.Vector.Create;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// TIFF pages for Create PDF on hosts without a TIFF codec (Android): small files built here, one
/// for each compression, colour kind and layout the reader takes, read back to the same pixels,
/// sizes, resolutions and page counts, and the limits that keep hostile files bounded.
/// </summary>
public class ViewerTiffReaderTests
{
    // ------------------------------------------------------------------ a small TIFF writer

    private const ushort Byte = 1, Short = 3, Long = 4, Rational = 5, Undefined = 7;

    private sealed class Page
    {
        public List<byte[]> Blocks { get; } = [];
        public SortedDictionary<ushort, (ushort Type, long[] Values)> Tags { get; } = new();
        public bool Tiled { get; init; }

        public Page Tag(ushort tag, ushort type, params long[] values)
        {
            Tags[tag] = (type, values);
            return this;
        }

        public Page Size(int width, int height) => Tag(256, Long, width).Tag(257, Long, height);
    }

    /// <summary>A TIFF of the pages: each page's blocks, then its IFD with out-of-line values after it. <paramref name="nextOfLast"/> lets a test point the chain anywhere.</summary>
    private static byte[] Build(bool little, IReadOnlyList<Page> pages, long? nextOfLast = null)
    {
        var o = new List<byte>();
        void U16(int at, long v)
        {
            var b = new byte[2];
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
            for (int i = 0; i < 2; i++) o[at + i] = b[i];
        }
        void U32(int at, long v)
        {
            var b = new byte[4];
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
            for (int i = 0; i < 4; i++) o[at + i] = b[i];
        }
        int Grow(int n)
        {
            int at = o.Count;
            o.AddRange(new byte[n]);
            return at;
        }
        void Align()
        {
            if (o.Count % 2 == 1) o.Add(0);
        }
        o.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        Grow(6);
        U16(2, 42);
        int link = 4;
        foreach (var page in pages)
        {
            var offsets = new List<long>();
            foreach (var block in page.Blocks)
            {
                Align();
                offsets.Add(o.Count);
                o.AddRange(block);
            }
            var tags = new SortedDictionary<ushort, (ushort Type, long[] Values)>(page.Tags);
            if (page.Blocks.Count > 0)
            {
                tags.TryAdd(page.Tiled ? (ushort)324 : (ushort)273, (Long, offsets.ToArray()));
                tags.TryAdd(page.Tiled ? (ushort)325 : (ushort)279, (Long, page.Blocks.Select(b => (long)b.Length).ToArray()));
            }
            Align();
            int ifd = Grow(2 + 12 * tags.Count + 4);
            U32(link, ifd);
            U16(ifd, tags.Count);
            int e = ifd + 2;
            foreach (var (tag, (type, values)) in tags)
            {
                int size = type switch { Byte or Undefined => 1, Short => 2, Rational => 4, _ => 4 };
                long count = type == Rational ? values.Length / 2 : values.Length;
                U16(e, tag);
                U16(e + 2, type);
                U32(e + 4, count);
                int at = e + 8;
                if (size * values.Length > 4)
                {
                    Align();
                    at = Grow(size * values.Length);
                    U32(e + 8, at);
                }
                for (int i = 0; i < values.Length; i++)
                {
                    if (size == 1) o[at + i] = (byte)values[i];
                    else if (size == 2) U16(at + 2 * i, values[i]);
                    else U32(at + 4 * i, values[i]);
                }
                e += 12;
            }
            link = ifd + 2 + 12 * tags.Count;
        }
        if (nextOfLast is long next) U32(link, next);
        return o.ToArray();
    }

    private static Page Gray8(int width, int height, byte[] pixels, int photometric = 1)
    {
        var page = new Page().Size(width, height).Tag(258, Short, 8).Tag(259, Short, 1).Tag(262, Short, photometric);
        page.Blocks.Add(pixels);
        return page;
    }

    /// <summary>TIFF LZW, as libtiff writes it: clear first, codes growing one early, a clear before the table fills.</summary>
    private static byte[] LzwEncode(byte[] data)
    {
        var bytes = new List<byte>();
        int acc = 0, accBits = 0, width = 9, next = 258;
        void Put(int code)
        {
            acc = acc << width | code;
            accBits += width;
            while (accBits >= 8)
            {
                bytes.Add((byte)(acc >> (accBits - 8)));
                accBits -= 8;
            }
            acc &= (1 << accBits) - 1;
        }
        void Grew()
        {
            next++;
            if (next >= 1 << width && width < 12) width++;
        }
        var table = new Dictionary<(int, byte), int>();
        Put(256);
        int w = -1;
        foreach (byte b in data)
        {
            if (w < 0) { w = b; continue; }
            if (table.TryGetValue((w, b), out int code)) { w = code; continue; }
            Put(w);
            table[(w, b)] = next;
            Grew();
            w = b;
            if (next >= 4093)
            {
                Put(256);
                table.Clear();
                next = 258;
                width = 9;
            }
        }
        if (w >= 0)
        {
            Put(w);
            Grew();
        }
        Put(257);
        if (accBits > 0) bytes.Add((byte)(acc << (8 - accBits)));
        return bytes.ToArray();
    }

    // ------------------------------------------------------------------ compressions and colours

    [Fact]
    public void DeflateRgb_FromTheAppsOwnEncoder_ReadsBackTheSamePixels()
    {
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 30, 20, 10, 255, 50, 100, 200, 255, 255, 255, 255, 255];
        var tiff = ViewerImageEncoders.Tiff(bgra, 3, 2, 300);
        Assert.True(ViewerTiffReader.IsTiff(tiff));
        var result = ViewerTiffReader.ReadAll(tiff);
        var page = Assert.Single(result.Pages);
        Assert.Equal((3, 2, 300.0, 300.0), (page.Width, page.Height, page.DpiX, page.DpiY));
        Assert.Equal(PdfImageColor.Rgb, page.Image.Color);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30, 200, 100, 50, 255, 255, 255 }, page.Image.Data.ToArray());
        Assert.Null(page.Image.Alpha);
    }

    [Fact]
    public void UncompressedGray_KeepsPixels_AndResolutionInCentimetres()
    {
        byte[] pixels = [0, 50, 100, 150, 200, 250, 1, 2];
        var page = Gray8(4, 2, pixels).Tag(282, Rational, 100, 1).Tag(283, Rational, 50, 1).Tag(296, Short, 3);
        var read = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0];
        Assert.Equal(pixels, read.Image.Data.ToArray());
        Assert.Equal(PdfImageColor.Gray, read.Image.Color);
        Assert.Equal(254, read.DpiX, 3);
        Assert.Equal(127, read.DpiY, 3);
        Assert.Equal(read.DpiX, read.Image.DpiX);
    }

    [Fact]
    public void NoResolution_IsTakenAs72Dpi()
    {
        var read = ViewerTiffReader.ReadAll(Build(true, [Gray8(2, 1, [1, 2])])).Pages[0];
        Assert.Equal((72.0, 72.0), (read.DpiX, read.DpiY));
    }

    [Fact]
    public void WhiteIsZeroBilevel_IsInvertedToPdfGrey()
    {
        // 10 × 2, 1 bit: row 0 the first 4 pixels black, row 1 all black (WhiteIsZero: 1 = black).
        var page = new Page().Size(10, 2).Tag(258, Short, 1).Tag(259, Short, 1).Tag(262, Short, 0);
        page.Blocks.Add([0xF0, 0x00, 0xFF, 0xC0]);
        var read = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0];
        Assert.Equal(1, read.Image.BitsPerComponent);
        var data = read.Image.Data.ToArray();
        Assert.Equal(0x0F, data[0]);
        Assert.Equal(0xC0, data[1] & 0xC0);
        Assert.Equal(0x00, data[2]);
        Assert.Equal(0x00, data[3] & 0xC0);
    }

    [Fact]
    public void PackBits_RunsAndLiterals()
    {
        // 6 × 2 grey: a run of five 7s and a literal 9, then literals 1..6.
        var page = new Page().Size(6, 2).Tag(258, Short, 8).Tag(259, Short, 32773).Tag(262, Short, 1);
        page.Blocks.Add([unchecked((byte)-4), 7, 0, 9, 5, 1, 2, 3, 4, 5, 6]);
        var read = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0];
        Assert.Equal(new byte[] { 7, 7, 7, 7, 7, 9, 1, 2, 3, 4, 5, 6 }, read.Image.Data.ToArray());
    }

    [Fact]
    public void LzwWithHorizontalPredictor_Rgb_InSeveralStrips()
    {
        const int W = 40, H = 30;
        var rgb = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                rgb[i] = (byte)(x * 6);
                rgb[i + 1] = (byte)(y * 8);
                rgb[i + 2] = (byte)((x * y) & 0xFF);
            }
        var page = new Page().Size(W, H).Tag(258, Short, 8, 8, 8).Tag(259, Short, 5).Tag(262, Short, 2).Tag(277, Short, 3)
            .Tag(278, Long, 16).Tag(317, Short, 2);
        for (int s = 0; s < 2; s++)
        {
            int rows = Math.Min(16, H - s * 16);
            var strip = rgb.AsSpan(s * 16 * W * 3, rows * W * 3).ToArray();
            for (int r = 0; r < rows; r++)
                for (int i = W * 3 - 1; i >= 3; i--)
                    strip[r * W * 3 + i] -= strip[r * W * 3 + i - 3];
            page.Blocks.Add(LzwEncode(strip));
        }
        var read = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0];
        Assert.Equal(rgb, read.Image.Data.ToArray());
    }

    [Fact]
    public void Lzw_LongRepetitiveData_RoundTrips()
    {
        var data = new byte[200_000];
        var random = new Random(7);
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 3000 < 1500 ? 0 : random.Next(4));
        var output = new byte[data.Length];
        Assert.Equal(data.Length, ViewerTiffReader.Lzw(LzwEncode(data), output));
        Assert.Equal(data, output);
    }

    [Fact]
    public void Palette4Bit_UsesTheColourMap()
    {
        var map = new long[3 * 16];
        for (int i = 0; i < 16; i++)
        {
            map[i] = i * 4096;           // red
            map[16 + i] = 65535 - i * 4096; // green
            map[32 + i] = 0x8000;        // blue
        }
        var page = new Page().Size(3, 1).Tag(258, Short, 4).Tag(259, Short, 1).Tag(262, Short, 3).Tag(320, Short, map);
        page.Blocks.Add([0x01, 0xF0]);
        var image = ViewerTiffReader.ReadAll(Build(false, [page])).Pages[0].Image;
        Assert.Equal(4, image.BitsPerComponent);
        Assert.NotNull(image.Palette);
        Assert.Equal(new byte[] { 0, 255, 128 }, image.Palette![..3]);
        Assert.Equal(new byte[] { 16, 239, 128 }, image.Palette[3..6]);
        Assert.Equal(new byte[] { 0x01, 0xF0 }, image.Data.ToArray());
    }

    [Fact]
    public void BigEndian16BitGray_IsBigEndianForPdf()
    {
        var page = new Page().Size(2, 1).Tag(258, Short, 16).Tag(259, Short, 1).Tag(262, Short, 1);
        page.Blocks.Add([0x12, 0x34, 0xAB, 0xCD]);
        var image = ViewerTiffReader.ReadAll(Build(false, [page])).Pages[0].Image;
        Assert.Equal(16, image.BitsPerComponent);
        Assert.Equal(new byte[] { 0x12, 0x34, 0xAB, 0xCD }, image.Data.ToArray());

        var little = new Page().Size(2, 1).Tag(258, Short, 16).Tag(259, Short, 1).Tag(262, Short, 1);
        little.Blocks.Add([0x34, 0x12, 0xCD, 0xAB]);
        Assert.Equal(new byte[] { 0x12, 0x34, 0xAB, 0xCD }, ViewerTiffReader.ReadAll(Build(true, [little])).Pages[0].Image.Data.ToArray());
    }

    [Fact]
    public void RgbaPremultiplied_IsSplitIntoStraightColourAndAlpha()
    {
        var page = new Page().Size(2, 1).Tag(258, Short, 8, 8, 8, 8).Tag(259, Short, 1).Tag(262, Short, 2).Tag(277, Short, 4).Tag(338, Short, 1);
        page.Blocks.Add([100, 50, 0, 200, 10, 20, 30, 255]);
        var image = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image;
        Assert.Equal(new byte[] { 127, 63, 0, 10, 20, 30 }, image.Data.ToArray());
        Assert.Equal(new byte[] { 200, 255 }, image.Alpha!.Value.ToArray());
    }

    [Fact]
    public void Cmyk8_IsCmyk()
    {
        var page = new Page().Size(1, 1).Tag(258, Short, 8, 8, 8, 8).Tag(259, Short, 1).Tag(262, Short, 5).Tag(277, Short, 4);
        page.Blocks.Add([1, 2, 3, 4]);
        var image = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image;
        Assert.Equal(PdfImageColor.Cmyk, image.Color);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.Data.ToArray());
    }

    [Fact]
    public void Tiles_ArePutTogether()
    {
        const int W = 20, H = 18;
        var expected = new byte[W * H];
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i * 7);
        var page = new Page { Tiled = true }.Size(W, H).Tag(258, Short, 8).Tag(259, Short, 8).Tag(262, Short, 1).Tag(322, Long, 16).Tag(323, Long, 16);
        for (int ty = 0; ty < 2; ty++)
            for (int tx = 0; tx < 2; tx++)
            {
                var tile = new byte[16 * 16];
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        int px = tx * 16 + x, py = ty * 16 + y;
                        if (px < W && py < H) tile[y * 16 + x] = expected[py * W + px];
                    }
                using var packed = new MemoryStream();
                using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(tile);
                page.Blocks.Add(packed.ToArray());
            }
        Assert.Equal(expected, ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image.Data.ToArray());
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
    public void CcittGroup4_DecodesToOneBitGrey()
    {
        // 16 × 3: row 0 white (V0), row 1 black 4..7 (H + W4 + B4, V0), row 2 the same (V0 V0 V0), EOFB.
        var g4 = Bits("1 " + "001 1011 011 1 " + "1 1 1 " + "000000000001 000000000001");
        var page = new Page().Size(16, 3).Tag(258, Short, 1).Tag(259, Short, 4).Tag(262, Short, 0).Tag(282, Rational, 200, 1).Tag(283, Rational, 100, 1);
        page.Blocks.Add(g4);
        var read = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0];
        Assert.Equal(1, read.Image.BitsPerComponent);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xF0, 0xFF, 0xF0, 0xFF }, read.Image.Data.ToArray());
        Assert.Equal((200.0, 100.0), (read.DpiX, read.DpiY));

        // BlackIsZero: the code's white runs are black pixels.
        page.Tag(262, Short, 1);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x0F, 0x00, 0x0F, 0x00 }, ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image.Data.ToArray());

        // FillOrder 2: the same codes with each byte's bits reversed.
        var reversed = new Page().Size(16, 3).Tag(258, Short, 1).Tag(259, Short, 4).Tag(262, Short, 0).Tag(266, Short, 2);
        reversed.Blocks.Add(g4.Select(b => (byte)((b * 0x0202020202UL & 0x010884422010UL) % 1023)).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xF0, 0xFF, 0xF0, 0xFF }, ViewerTiffReader.ReadAll(Build(true, [reversed])).Pages[0].Image.Data.ToArray());
    }

    [Fact]
    public void CcittGroup3ModifiedHuffman_ByteAlignedRows()
    {
        // Compression 2: each row one-dimensional, starting on a byte. 16 wide: W16 = "101010"; W4 B4 W8 = "1011 011 10011".
        var data = Bits("101010 00").Concat(Bits("1011 011 10011 000")).ToArray();
        var page = new Page().Size(16, 2).Tag(258, Short, 1).Tag(259, Short, 2).Tag(262, Short, 0);
        page.Blocks.Add(data);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xF0, 0xFF }, ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image.Data.ToArray());
    }

    // ------------------------------------------------------------------ JPEG

    /// <summary>An 8 × 8 grey baseline JPEG, split as TIFF stores it: tables apart, and the frame and scan.</summary>
    private static (byte[] Tables, byte[] Strip) TinyJpeg()
    {
        var tables = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00 };
        tables.AddRange(Enumerable.Repeat((byte)1, 64));
        foreach (byte kind in new byte[] { 0x00, 0x10 })
        {
            tables.AddRange(new byte[] { 0xFF, 0xC4, 0x00, 0x14, kind, 1 });
            tables.AddRange(new byte[15]);
            tables.Add(0x00);
        }
        tables.AddRange(new byte[] { 0xFF, 0xD9 });
        byte[] strip = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08, 0x01, 0x01, 0x11, 0x00,
                        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x3F, 0xFF, 0xD9];
        return (tables.ToArray(), strip);
    }

    [Fact]
    public void JpegWithSharedTables_IsCarriedAsOneCompleteJpeg()
    {
        var (tables, strip) = TinyJpeg();
        var page = new Page().Size(8, 8).Tag(258, Short, 8).Tag(259, Short, 7).Tag(262, Short, 1).Tag(347, Undefined, tables.Select(b => (long)b).ToArray());
        page.Blocks.Add(strip);
        var image = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image;
        Assert.Equal(PdfImageData.Jpeg, image.Kind);
        Assert.Equal(PdfImageColor.Gray, image.Color);
        var jpeg = image.Data.ToArray();
        Assert.Equal(tables.Length - 2 + strip.Length - 2, jpeg.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB }, jpeg[..4]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
    }

    [Fact]
    public void JpegWithoutTables_IsRefusedWithAReason()
    {
        var (_, strip) = TinyJpeg();
        var jpegPage = new Page().Size(8, 8).Tag(258, Short, 8).Tag(259, Short, 7).Tag(262, Short, 1);
        jpegPage.Blocks.Add(strip);
        var result = ViewerTiffReader.ReadAll(Build(true, [Gray8(1, 1, [5]), jpegPage]));
        Assert.Single(result.Pages);
        var refused = Assert.Single(result.Refused);
        Assert.Equal(2, refused.Page);
        Assert.Contains("tables", refused.Reason);
    }

    [Fact]
    public void OldStyleJpeg_IsRefused()
    {
        var page = Gray8(1, 1, [0]).Tag(259, Short, 6);
        var ex = Assert.Throws<ViewerTiffException>(() => ViewerTiffReader.ReadAll(Build(true, [page])));
        Assert.Contains("old-style JPEG", ex.Message);
    }

    // ------------------------------------------------------------------ pages, orientation, PDF

    [Fact]
    public void MultiPage_ReadsEveryPage_AndSkipsReducedCopies()
    {
        var thumbnail = Gray8(1, 1, [9]).Tag(254, Long, 1);
        var file = Build(true, [Gray8(2, 1, [1, 2]), thumbnail, Gray8(3, 1, [3, 4, 5]), Gray8(1, 2, [6, 7])]);
        var result = ViewerTiffReader.ReadAll(file);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(new[] { (2, 1), (3, 1), (1, 2) }, result.Pages.Select(p => (p.Width, p.Height)));
        Assert.Equal(new byte[] { 3, 4, 5 }, result.Pages[1].Image.Data.ToArray());
    }

    [Fact]
    public void Orientation_TurnsThePage_InTheWrittenPdf()
    {
        var page = Gray8(4, 2, new byte[8]).Tag(274, Short, 6).Tag(282, Rational, 72, 1).Tag(283, Rational, 72, 1);
        var result = ViewerTiffReader.ReadAll(Build(true, [page, Gray8(2, 2, new byte[4])]));
        var read = result.Pages[0];
        Assert.Equal(6, read.Orientation);
        Assert.Equal(6, read.Image.Orientation);
        Assert.Equal((2.0, 4.0), read.Image.NaturalSize);

        using var output = new MemoryStream();
        using (var writer = new PdfDocumentWriter(output))
        {
            foreach (var p in result.Pages) writer.AddImagePage(p.Image, new PdfImagePageOptions { Size = PdfImagePageSize.FitImage });
            Assert.Equal(2, writer.PageCount);
            writer.Finish();
        }
        string pdf = System.Text.Encoding.Latin1.GetString(output.ToArray());
        Assert.Contains("/MediaBox [0 0 2 4]", pdf);
    }

    // ------------------------------------------------------------------ hostile and damaged files

    [Fact]
    public void NotATiff_AndBigTiff_AreRefusedForTheWholeFile()
    {
        var ex = Assert.Throws<ViewerTiffException>(() => ViewerTiffReader.Open("%PDF-1.7 not a tiff"u8.ToArray()));
        Assert.True(ex.WholeFile);
        Assert.False(ViewerTiffReader.IsTiff("%PDF"u8));
        var big = Assert.Throws<ViewerTiffException>(() => ViewerTiffReader.Open([(byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Contains("BigTIFF", big.Message);
    }

    [Fact]
    public void IfdChainLoop_EndsWhereItLoops()
    {
        var once = Build(true, [Gray8(1, 1, [1]), Gray8(1, 1, [2])]);
        int firstIfd = BinaryPrimitives.ReadInt32LittleEndian(once.AsSpan(4));
        var looped = Build(true, [Gray8(1, 1, [1]), Gray8(1, 1, [2])], nextOfLast: firstIfd);
        Assert.Equal(2, ViewerTiffReader.Open(looped).PageCount);
    }

    [Fact]
    public void MoreThan500Pages_IsRefused()
    {
        var pages = Enumerable.Range(0, ViewerTiffReader.MaxPages + 1).Select(_ => Gray8(1, 1, [0])).ToList();
        var ex = Assert.Throws<ViewerTiffException>(() => ViewerTiffReader.Open(Build(true, pages)));
        Assert.True(ex.WholeFile);
        Assert.Contains("500", ex.Message);
        Assert.Equal(ViewerTiffReader.MaxPages, ViewerTiffReader.Open(Build(true, pages.Take(ViewerTiffReader.MaxPages).ToList())).PageCount);
    }

    [Fact]
    public void TileFarLargerThanThePicture_IsRefused_NotAllocated()
    {
        // A 1 × 1 picture claiming 16384 × 16384 tiles would make the reader set aside 256 MB.
        var page = new Page { Tiled = true }.Size(1, 1).Tag(258, Short, 8).Tag(259, Short, 1).Tag(262, Short, 1).Tag(322, Long, 16384).Tag(323, Long, 16384);
        page.Blocks.Add([7]);
        var result = ViewerTiffReader.ReadAll(Build(true, [page, Gray8(1, 1, [1])]));
        Assert.Single(result.Pages);
        Assert.Contains("tile size", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void TagWithFarMoreValuesThanThePictureUses_IsRefused()
    {
        // A 1 × 1 picture whose BitsPerSample lists 100 000 values: refused before an array of that size is made.
        var page = Gray8(1, 1, [9]).Tag(258, Short, Enumerable.Repeat(8L, 100_000).ToArray());
        var result = ViewerTiffReader.ReadAll(Build(true, [page, Gray8(1, 1, [1])]));
        Assert.Single(result.Pages);
        Assert.Contains("values", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void DarkSixteenBitPalette_StaysDark()
    {
        // Every entry below 256 of 65535: near black. Read as 16-bit, as TIFF 6.0 has it, not as bright 8-bit values.
        var map = new long[3 * 16];
        for (int i = 0; i < map.Length; i++) map[i] = i % 16 * 4;
        var page = new Page().Size(2, 1).Tag(258, Short, 4).Tag(259, Short, 1).Tag(262, Short, 3).Tag(320, Short, map);
        page.Blocks.Add([0xF0]);
        var image = ViewerTiffReader.ReadAll(Build(true, [page])).Pages[0].Image;
        Assert.All(image.Palette!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void TooManyPixels_RefusesThatPageOnly()
    {
        var huge = new Page().Size(10_000, 6_000).Tag(258, Short, 1).Tag(259, Short, 4).Tag(262, Short, 0);
        huge.Blocks.Add([0]);
        var result = ViewerTiffReader.ReadAll(Build(true, [huge, Gray8(1, 1, [1])]));
        Assert.Equal(2, result.PageCount);
        Assert.Single(result.Pages);
        Assert.Contains("30 million", Assert.Single(result.Refused).Reason);
    }

    [Fact]
    public void DecodedTotal_IsBounded()
    {
        // Each page claims 5000 × 5000 grey pixels (25 MB, 50 MB counted with its output): five fit in 256 MB, the sixth does not.
        var pages = Enumerable.Range(0, 8).Select(_ =>
        {
            var p = new Page().Size(5000, 5000).Tag(258, Short, 8).Tag(259, Short, 32773).Tag(262, Short, 1);
            p.Blocks.Add([unchecked((byte)-127), 0]);
            return p;
        }).ToList();
        var reader = ViewerTiffReader.Open(Build(true, pages));
        var ex = Assert.Throws<ViewerTiffException>(() =>
        {
            for (int i = 0; i < reader.PageCount; i++) reader.ReadPage(i);
        });
        Assert.True(ex.WholeFile);
        Assert.Contains("256 MB", ex.Message);
    }

    [Fact]
    public void StripOutsideTheFile_IsDamaged()
    {
        var page = new Page().Size(2, 2).Tag(258, Short, 8).Tag(259, Short, 1).Tag(262, Short, 1).Tag(273, Long, 1_000_000).Tag(279, Long, 4);
        var ex = Assert.Throws<ViewerTiffException>(() => ViewerTiffReader.ReadAll(Build(true, [page])));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void UnsupportedCompression_SaysSo()
    {
        var page = Gray8(1, 1, [0]).Tag(259, Short, 34712);
        var ex = Assert.Throws<ViewerTiffException>(() => ViewerTiffReader.ReadAll(Build(true, [page])));
        Assert.Contains("34712", ex.Message);
    }
}
