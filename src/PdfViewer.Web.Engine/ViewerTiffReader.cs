using System.Buffers.Binary;
using System.IO.Compression;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Streams;

namespace PdfViewer.Web.Engine;

/// <summary>
/// A TIFF file this reader refuses, or one page of it, with a message written for the reader
/// (it never quotes the file's content). <see cref="WholeFile"/> is true when no further page
/// can be read either: the file is not a TIFF, is damaged, or goes past a limit.
/// </summary>
public sealed class ViewerTiffException(string message, bool wholeFile = false) : Exception(message)
{
    public bool WholeFile { get; } = wholeFile;
}

/// <summary>
/// One page of a TIFF file, ready for <see cref="PdfDocumentWriter.AddImagePage"/>: its pixels as
/// samples (or a JPEG carried as it is), its resolution and its orientation (the writer turns the
/// page by it).
/// </summary>
public sealed record ViewerTiffPage(int Width, int Height, double DpiX, double DpiY, int Orientation, PdfCreateImage Image);

/// <summary>A page that was left out, 1-based, and why.</summary>
public sealed record ViewerTiffRefusal(int Page, string Reason);

/// <summary>Every page of a TIFF file that could be read, and the pages that could not.</summary>
public sealed record ViewerTiffResult(int PageCount, IReadOnlyList<ViewerTiffPage> Pages, IReadOnlyList<ViewerTiffRefusal> Refused);

/// <summary>
/// Reads every page (IFD) of a TIFF file without any platform codec, for hosts whose own decoder
/// has no TIFF (Android's BitmapFactory and Skia): the pages Windows' Create PDF from images takes
/// through WIC. Little- and big-endian files, strips and tiles; compression none, PackBits, LZW and
/// Deflate (with the horizontal predictor), CCITT Group 3 and Group 4 (decoded with the engine's
/// fax decoder, stored as 1-bit samples), and new-style JPEG, carried into the PDF as it is when it
/// is one complete JPEG. Bilevel, grey (1 to 16 bits), RGB (8 or 16 bits, with alpha), palette
/// (1 to 8 bits) and CMYK (8 bits) pictures.
/// <para>
/// Hostile files are bounded: at most <see cref="MaxPages"/> pages, <see cref="MaxPagePixels"/>
/// pixels a page and <see cref="MaxDecodedBytes"/> decoded in all; every offset and count is checked
/// against the file, and an IFD chain that loops ends where it loops.
/// </para>
/// </summary>
public sealed class ViewerTiffReader
{
    public const int MaxPages = 500;
    public const long MaxPagePixels = 50_000_000;
    public const long MaxDecodedBytes = 512L * 1024 * 1024;

    /// <summary>The resolution assumed when a file gives none (TIFF's customary 72 dpi).</summary>
    public const double DefaultDpi = 72;

    private const int MaxEntriesPerIfd = 4096;
    private const long MaxBlockBytes = 256L * 1024 * 1024;

    private readonly byte[] _file;
    private readonly bool _little;
    private readonly List<Ifd> _pages;
    private long _decoded;

    private ViewerTiffReader(byte[] file, bool little, List<Ifd> pages)
    {
        _file = file;
        _little = little;
        _pages = pages;
    }

    /// <summary>The pages the file has (reduced-resolution copies and masks are not pages).</summary>
    public int PageCount => _pages.Count;

    /// <summary>The file starts as a TIFF does: "II*\0" or "MM\0*".</summary>
    public static bool IsTiff(ReadOnlySpan<byte> file) =>
        file.Length >= 4 && (file[0] == 'I' && file[1] == 'I' && file[2] == 42 && file[3] == 0
                          || file[0] == 'M' && file[1] == 'M' && file[2] == 0 && file[3] == 42);

    /// <summary>Reads the file's structure (its pages' tags); the pixels are read by <see cref="ReadPage"/>.</summary>
    /// <exception cref="ViewerTiffException">Not a TIFF, a BigTIFF, damaged, or more than <see cref="MaxPages"/> pages.</exception>
    public static ViewerTiffReader Open(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length >= 4 && (file[0] == 'I' && file[1] == 'I' && file[2] == 43 || file[0] == 'M' && file[1] == 'M' && file[3] == 43))
            throw new ViewerTiffException("This is a BigTIFF file, which this app does not read.", true);
        if (!IsTiff(file) || file.Length < 8) throw new ViewerTiffException("This is not a TIFF file.", true);
        bool little = file[0] == 'I';
        var pages = new List<Ifd>();
        var seen = new HashSet<long>();
        long offset = U32(file, 4, little);
        while (offset != 0)
        {
            // A chain that points back at an IFD already read, or out of the file, ends here.
            if (!seen.Add(offset) || offset < 8 || offset + 2 > file.Length) break;
            int count = U16(file, (int)offset, little);
            long end = offset + 2 + 12L * count;
            if (count == 0 || count > MaxEntriesPerIfd || end > file.Length) break;
            var ifd = new Ifd();
            for (int i = 0; i < count; i++)
            {
                int e = (int)offset + 2 + 12 * i;
                ifd.Entries[U16(file, e, little)] = new Entry(U16(file, e + 2, little), U32(file, e + 4, little), e + 8);
            }
            if (ifd.Entries.ContainsKey(256))
            {
                var reader = new ViewerTiffReader(file, little, pages);
                long subfile = reader.Value(ifd, 254, 0);
                // NewSubfileType bit 0: a reduced-resolution copy; bit 2: a transparency mask. Neither is a page.
                if ((subfile & 0b101) == 0)
                {
                    if (pages.Count == MaxPages)
                        throw new ViewerTiffException($"This TIFF file has more than {MaxPages} pages, the most this app reads from one file.", true);
                    pages.Add(ifd);
                }
            }
            offset = end + 4 <= file.Length ? U32(file, (int)end, little) : 0;
        }
        if (pages.Count == 0) throw new ViewerTiffException("The TIFF file is damaged: it has no page that can be read.", true);
        return new ViewerTiffReader(file, little, pages);
    }

    /// <summary>
    /// Reads every page; a page that cannot be read is left out with its reason. Throws when the
    /// file itself cannot be read or no page can.
    /// </summary>
    public static ViewerTiffResult ReadAll(byte[] file, CancellationToken cancellationToken = default)
    {
        var reader = Open(file);
        var pages = new List<ViewerTiffPage>();
        var refused = new List<ViewerTiffRefusal>();
        for (int i = 0; i < reader.PageCount; i++)
        {
            try
            {
                pages.Add(reader.ReadPage(i, cancellationToken));
            }
            catch (ViewerTiffException ex) when (!ex.WholeFile)
            {
                refused.Add(new ViewerTiffRefusal(i + 1, ex.Message));
            }
        }
        if (pages.Count == 0)
            throw new ViewerTiffException(reader.PageCount == 1 ? refused[0].Reason : $"None of its {reader.PageCount} pages can be read. {refused[0].Reason}", true);
        return new ViewerTiffResult(reader.PageCount, pages, refused);
    }

    /// <summary>Reads page <paramref name="index"/> (0-based).</summary>
    /// <exception cref="ViewerTiffException">The page cannot be read (or, with <see cref="ViewerTiffException.WholeFile"/>, the total decoded size went past the limit).</exception>
    public ViewerTiffPage ReadPage(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _pages.Count);
        var ifd = _pages[index];

        long w = Value(ifd, 256, 0), h = Value(ifd, 257, 0);
        if (w <= 0 || h <= 0 || w > 1 << 24 || h > 1 << 24) throw Damaged("its size is missing or impossible");
        if (w * h > MaxPagePixels)
            throw new ViewerTiffException($"Its picture has {w} × {h} pixels, more than the {MaxPagePixels / 1_000_000} million this app reads.");
        int width = (int)w, height = (int)h;
        int compression = (int)Value(ifd, 259, 1);
        int spp = (int)Value(ifd, 277, 1);
        if (spp is < 1 or > 16) throw Damaged("its samples per pixel are impossible");
        var bitsArray = Values(ifd, 258);
        int bps = bitsArray is { Length: > 0 } ? (int)bitsArray[0] : 1;
        if (bitsArray != null && bitsArray.Any(b => b != bps))
            throw new ViewerTiffException("Its colour channels have different bit depths, which this app does not read.");
        int photometric = (int)Value(ifd, 262, -1);
        if (photometric < 0) photometric = spp >= 3 ? 2 : 1;
        int orientation = (int)Value(ifd, 274, 1);
        if (orientation is < 1 or > 8) orientation = 1;
        var (dpiX, dpiY) = Resolution(ifd);

        PdfCreateImage image;
        if (compression == 7)
            image = Jpeg(ifd, width, height, photometric);
        else if (compression == 6)
            throw new ViewerTiffException("It uses old-style JPEG compression, which this app does not read.");
        else
            image = Samples(ifd, width, height, compression, spp, bps, photometric, cancellationToken);

        image = new PdfCreateImage
        {
            Width = image.Width, Height = image.Height, Color = image.Color, BitsPerComponent = image.BitsPerComponent, Kind = image.Kind,
            Data = image.Data, Palette = image.Palette, Alpha = image.Alpha, InvertedCmyk = image.InvertedCmyk,
            DpiX = dpiX, DpiY = dpiY, Orientation = orientation,
        };
        return new ViewerTiffPage(width, height, dpiX, dpiY, orientation, image);
    }

    // ------------------------------------------------------------------ samples

    private PdfCreateImage Samples(Ifd ifd, int width, int height, int compression, int spp, int bps, int photometric, CancellationToken ct)
    {
        if (compression is not (1 or 2 or 3 or 4 or 5 or 8 or 32946 or 32773))
            throw new ViewerTiffException($"It is compressed in a way this app does not read (TIFF compression {compression}).");
        bool fax = compression is 2 or 3 or 4;
        if (fax && (spp != 1 || bps != 1)) throw Damaged("its fax compression is given for a picture that is not black and white");
        if (Value(ifd, 284, 1) == 2 && spp > 1)
            throw new ViewerTiffException("Its colour channels are stored in separate planes, which this app does not read.");
        int predictor = (int)Value(ifd, 317, 1);
        if (predictor is not (1 or 2))
            throw new ViewerTiffException("It uses a floating-point predictor, which this app does not read.");
        if (predictor == 2 && bps is not (8 or 16))
            throw new ViewerTiffException("It uses the horizontal predictor on samples smaller than a byte, which this app does not read.");
        var extra = Values(ifd, 338) ?? [];

        // What the samples are, checked before anything is decoded.
        int colors;
        switch (photometric)
        {
            case 0 or 1:
                colors = 1;
                if (bps is not (1 or 2 or 4 or 8 or 16) || spp > 1 && bps < 8) throw Unsupported($"{bps}-bit grey");
                break;
            case 2:
                colors = 3;
                if (spp < 3 || bps is not (8 or 16)) throw Unsupported($"{bps}-bit RGB");
                break;
            case 3:
                colors = 1;
                if (spp != 1 || bps is not (1 or 2 or 4 or 8)) throw Unsupported($"{bps}-bit palette");
                break;
            case 5:
                colors = 4;
                if (spp < 4 || bps != 8 || Value(ifd, 332, 1) != 1) throw Unsupported("this kind of CMYK");
                break;
            case 6:
                throw new ViewerTiffException("Its colours are stored as YCbCr without JPEG compression, which this app does not read.");
            default:
                throw new ViewerTiffException($"Its colours are stored in a way this app does not read (TIFF photometric {photometric}).");
        }
        // The first extra sample is opacity when it says so (1 premultiplied, 2 straight).
        int alphaIndex = spp > colors && extra.Length > 0 && extra[0] is 1 or 2 ? colors : -1;
        bool associated = alphaIndex >= 0 && extra[0] == 1;

        long rowBytes = ((long)width * spp * bps + 7) / 8;
        long size = rowBytes * height;
        Budget(size * 2);
        var raw = new byte[size];

        // Strips are blocks as wide as the picture; tiles are blocks of their own size.
        bool tiled = ifd.Entries.ContainsKey(322);
        int blockW, blockH;
        long[] offsets, counts;
        if (tiled)
        {
            long tw = Value(ifd, 322, 0), th = Value(ifd, 323, 0);
            if (tw <= 0 || th <= 0 || tw > 1 << 16 || th > 1 << 16) throw Damaged("its tile size is impossible");
            blockW = (int)tw;
            blockH = (int)th;
            offsets = Values(ifd, 324) ?? throw Damaged("its tiles cannot be found");
            counts = Values(ifd, 325) ?? throw Damaged("its tile sizes are missing");
        }
        else
        {
            blockW = width;
            long rps = Value(ifd, 278, height);
            blockH = (int)Math.Clamp(rps <= 0 ? height : rps, 1, height);
            offsets = Values(ifd, 273) ?? throw Damaged("its strips cannot be found");
            counts = Values(ifd, 279)
                     ?? (compression == 1 && offsets.Length == 1 ? [Math.Max(0, _file.Length - offsets[0])] : throw Damaged("its strip sizes are missing"));
        }
        int across = (width + blockW - 1) / blockW, down = (height + blockH - 1) / blockH;
        long blockRowBytes = ((long)blockW * spp * bps + 7) / 8;
        long blockBytes = blockRowBytes * blockH;
        if (blockBytes > MaxBlockBytes) throw Damaged("its strips or tiles are impossibly large");
        var block = new byte[blockBytes];
        int fillOrder = (int)Value(ifd, 266, 1);
        int t4 = (int)Value(ifd, 292, 0);
        bool readable = false;

        for (int b = 0; b < (long)across * down && b < offsets.Length && b < counts.Length; b++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = offsets[b], count = counts[b];
            if (offset <= 0 || offset >= _file.Length || count <= 0) continue; // a missing block stays blank
            count = Math.Min(count, _file.Length - offset); // a file cut short is read as far as it goes
            int bx = b % across, by = b / across;
            int rows = tiled ? blockH : (int)Math.Min(blockH, height - (long)by * blockH);
            Array.Clear(block);
            byte[] source = _file;
            int start = (int)offset, length = (int)count;
            if (fillOrder == 2)
            {
                source = new byte[length];
                for (int i = 0; i < length; i++) source[i] = Reverse(_file[start + i]);
                start = 0;
            }
            var target = block.AsSpan(0, (int)(blockRowBytes * rows));
            switch (compression)
            {
                case 1: source.AsSpan(start, Math.Min(length, target.Length)).CopyTo(target); break;
                case 32773: PackBits(source.AsSpan(start, length), target); break;
                case 5: Lzw(source.AsSpan(start, length), target); break;
                case 8 or 32946: Inflate(source, start, length, target); break;
                default:
                    // Kept as stored: 0 is a code's white run, as uncompressed bilevel data has it; the photometric reads it.
                    var p = new CcittFaxDecoder.Parameters(
                        K: compression == 4 ? -1 : compression == 3 && (t4 & 1) != 0 ? 1 : 0,
                        EndOfLine: compression == 3, EncodedByteAlign: compression == 2 || compression == 3 && (t4 & 4) != 0,
                        Columns: blockW, Rows: rows, EndOfBlock: compression != 2, BlackIs1: true);
                    try
                    {
                        CcittFaxDecoder.Decode(source, start, start + length, p, rows, out _, ct).AsSpan(0, target.Length).CopyTo(target);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;
                    }
                    break;
            }
            readable = true;
            if (predictor == 2) Unpredict(target, blockRowBytes, blockW, spp, bps);

            // Into the picture: whole bytes (a tile's width is a multiple of 16, so its rows start on a byte).
            long xByte = (long)bx * blockRowBytes;
            for (int r = 0; r < rows; r++)
            {
                long y = (long)by * blockH + r;
                if (y >= height) break;
                long n = Math.Min(blockRowBytes, rowBytes - xByte);
                if (n <= 0) break;
                block.AsSpan((int)(r * blockRowBytes), (int)n).CopyTo(raw.AsSpan((int)(y * rowBytes + xByte), (int)n));
            }
        }
        if (!readable) throw Damaged("none of its picture data can be found");

        // As PDF wants them: 16-bit samples big-endian, 0 = black.
        if (bps == 16 && _little)
            for (long i = 0; i + 1 < raw.LongLength; i += 2) (raw[i], raw[i + 1]) = (raw[i + 1], raw[i]);

        if (photometric == 3)
        {
            var map = Values(ifd, 320);
            int entries = 1 << bps;
            if (map == null || map.Length < 3 * entries) throw Damaged("its palette is missing");
            // 16-bit colour map values; some writers store 8-bit values in them, which are then all below 256.
            bool eightBit = map.Take(3 * entries).All(v => v < 256);
            var palette = new byte[3 * entries];
            for (int i = 0; i < entries; i++)
                for (int c = 0; c < 3; c++)
                    palette[3 * i + c] = (byte)(eightBit ? map[c * entries + i] : map[c * entries + i] >> 8);
            return new PdfCreateImage { Width = width, Height = height, Color = PdfImageColor.Rgb, Kind = PdfImageData.Samples, BitsPerComponent = bps, Data = raw, Palette = palette };
        }

        var (data, alpha) = spp == colors ? (raw, null) : Split(raw, width, height, spp, bps / 8, colors, alphaIndex, associated);
        if (photometric == 0)
            for (long i = 0; i < data.LongLength; i++) data[i] = (byte)~data[i];
        return new PdfCreateImage
        {
            Width = width, Height = height, Kind = PdfImageData.Samples, BitsPerComponent = bps, Data = data,
            Color = colors switch { 1 => PdfImageColor.Gray, 3 => PdfImageColor.Rgb, _ => PdfImageColor.Cmyk },
            Alpha = alpha == null ? (ReadOnlyMemory<byte>?)null : alpha,
        };
    }

    /// <summary>
    /// The colour samples of each pixel, without its extra samples, and its opacity (8 bits, straight;
    /// null when every pixel is opaque). Samples are 1 or 2 bytes, big-endian.
    /// </summary>
    private static (byte[] Data, byte[]? Alpha) Split(byte[] raw, int width, int height, int spp, int bytes, int colors, int alphaIndex, bool associated)
    {
        long pixels = (long)width * height;
        var data = new byte[pixels * colors * bytes];
        var alpha = alphaIndex >= 0 ? new byte[pixels] : null;
        bool opaque = true;
        int max = bytes == 1 ? 255 : 65535;
        for (long p = 0; p < pixels; p++)
        {
            long s = p * spp * bytes;
            int a = max;
            if (alpha != null)
            {
                long at = s + alphaIndex * bytes;
                a = bytes == 1 ? raw[at] : raw[at] << 8 | raw[at + 1];
                alpha[p] = (byte)(bytes == 1 ? a : a >> 8);
                if (alpha[p] != 255) opaque = false;
            }
            for (int c = 0; c < colors; c++)
            {
                long from = s + c * bytes, to = (p * colors + c) * bytes;
                int v = bytes == 1 ? raw[from] : raw[from] << 8 | raw[from + 1];
                if (associated && a > 0 && a < max) v = (int)Math.Min(max, (long)v * max / a);
                if (bytes == 1) data[to] = (byte)v;
                else
                {
                    data[to] = (byte)(v >> 8);
                    data[to + 1] = (byte)v;
                }
            }
        }
        return (data, opaque ? null : alpha);
    }

    /// <summary>Undoes the horizontal predictor (TIFF 6.0 section 14) on each row, in the file's byte order.</summary>
    private void Unpredict(Span<byte> block, long rowBytes, int width, int spp, int bps)
    {
        int rows = (int)(block.Length / rowBytes);
        for (int r = 0; r < rows; r++)
        {
            var row = block.Slice((int)(r * rowBytes), (int)rowBytes);
            if (bps == 8)
            {
                for (int i = spp; i < width * spp; i++) row[i] += row[i - spp];
                continue;
            }
            for (int i = spp; i < width * spp; i++)
            {
                var cur = row.Slice(2 * i, 2);
                var prev = row.Slice(2 * (i - spp), 2);
                if (_little) BinaryPrimitives.WriteUInt16LittleEndian(cur, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(cur) + BinaryPrimitives.ReadUInt16LittleEndian(prev)));
                else BinaryPrimitives.WriteUInt16BigEndian(cur, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(cur) + BinaryPrimitives.ReadUInt16BigEndian(prev)));
            }
        }
    }

    // ------------------------------------------------------------------ JPEG

    /// <summary>
    /// New-style JPEG (compression 7) carried as it is: one strip or tile holding the whole picture,
    /// joined to the shared JPEGTables when the file has them, and only when the result is a complete
    /// JPEG (quantisation and Huffman tables, a frame of the picture's size) a PDF reader decodes.
    /// </summary>
    private PdfCreateImage Jpeg(Ifd ifd, int width, int height, int photometric)
    {
        var offsets = Values(ifd, 324) ?? Values(ifd, 273);
        var counts = Values(ifd, 325) ?? Values(ifd, 279);
        if (offsets == null || counts == null || offsets.Length == 0 || counts.Length == 0) throw Damaged("its picture data cannot be found");
        if (offsets.Length > 1)
            throw new ViewerTiffException("Its JPEG picture is stored in several parts, which this app cannot carry into a PDF.");
        long offset = offsets[0], count = counts[0];
        if (offset <= 0 || count <= 4 || offset + count > _file.Length) throw Damaged("its picture data lies outside the file");
        var data = _file.AsSpan((int)offset, (int)count);
        if (data[0] != 0xFF || data[1] != 0xD8) throw Damaged("its JPEG data does not start as a JPEG does");

        byte[] jpeg;
        if (Bytes(ifd, 347) is { Length: > 4 } tables && tables[0] == 0xFF && tables[1] == 0xD8)
        {
            // The tables' stream without its end marker, then the picture's without its start marker.
            int tablesEnd = tables.Length;
            if (tables[^2] == 0xFF && tables[^1] == 0xD9) tablesEnd -= 2;
            jpeg = new byte[tablesEnd + data.Length - 2];
            tables.AsSpan(0, tablesEnd).CopyTo(jpeg);
            data[2..].CopyTo(jpeg.AsSpan(tablesEnd));
        }
        else jpeg = data.ToArray();

        var header = JpegHeader.TryRead(jpeg);
        if (header == null || !HasTables(jpeg))
            throw new ViewerTiffException("Its JPEG picture is incomplete (its tables are missing), so this app cannot carry it into a PDF.");
        if (!header.CanPassThrough || header.Width != width || header.Height != height)
            throw new ViewerTiffException("Its JPEG picture is of a kind this app cannot carry into a PDF.");
        if (header.Components == 3 && photometric == 2 && !header.Adobe)
        {
            // RGB stored without the YCbCr transform: an Adobe marker with transform 0 says so to PDF readers.
            byte[] adobe = [0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0x00, 0x64, 0, 0, 0, 0, 0];
            var marked = new byte[jpeg.Length + adobe.Length];
            jpeg.AsSpan(0, 2).CopyTo(marked);
            adobe.CopyTo(marked, 2);
            jpeg.AsSpan(2).CopyTo(marked.AsSpan(2 + adobe.Length));
            jpeg = marked;
        }
        Budget(jpeg.Length);
        return new PdfCreateImage
        {
            Width = width, Height = height, Kind = PdfImageData.Jpeg, Data = jpeg,
            Color = header.Components switch { 1 => PdfImageColor.Gray, 4 => PdfImageColor.Cmyk, _ => PdfImageColor.Rgb },
            InvertedCmyk = header.Components == 4 && header.Adobe,
        };
    }

    /// <summary>A quantisation table (DQT) and, for Huffman coding, a Huffman table (DHT) come before the first scan.</summary>
    private static bool HasTables(byte[] jpeg)
    {
        bool dqt = false, dht = false;
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return false;
            int marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            if (marker == 0xDA) return dqt && dht;
            if (marker == 0xD9) return false;
            if (marker == 0xDB) dqt = true;
            if (marker == 0xC4) dht = true;
            pos += 2 + (jpeg[pos + 2] << 8 | jpeg[pos + 3]);
        }
        return false;
    }

    // ------------------------------------------------------------------ decompression

    /// <summary>PackBits (Apple, TIFF compression 32773) into <paramref name="output"/>, as far as it goes.</summary>
    private static void PackBits(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int i = 0, o = 0;
        while (i < input.Length && o < output.Length)
        {
            int n = (sbyte)input[i++];
            if (n >= 0)
            {
                int k = Math.Min(Math.Min(n + 1, input.Length - i), output.Length - o);
                input.Slice(i, k).CopyTo(output[o..]);
                i += n + 1;
                o += k;
            }
            else if (n != -128 && i < input.Length)
            {
                int k = Math.Min(1 - n, output.Length - o);
                output.Slice(o, k).Fill(input[i++]);
                o += k;
            }
        }
    }

    /// <summary>
    /// TIFF LZW (compression 5): codes of 9 to 12 bits, most significant bit first, the width growing
    /// one code early; clear 256, end 257. Stops when <paramref name="output"/> is full or the data is damaged.
    /// </summary>
    internal static int Lzw(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length >= 2 && input[0] == 0 && (input[1] & 1) == 1)
            throw new ViewerTiffException("It uses an old form of LZW compression, which this app does not read.");
        const int Clear = 256, End = 257, Max = 4096;
        var prefix = new short[Max];
        var length = new short[Max];
        var first = new byte[Max];
        var last = new byte[Max];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            length[i] = 1;
            first[i] = last[i] = (byte)i;
        }
        int next = 258, width = 9, previous = -1, o = 0;
        long bit = 0, bits = input.Length * 8L;
        while (bit + width <= bits && o < output.Length)
        {
            int code = 0;
            for (int k = 0; k < width; k++, bit++)
                code = code << 1 | (input[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1;
            if (code == Clear)
            {
                next = 258;
                width = 9;
                previous = -1;
                continue;
            }
            if (code == End) break;
            if (previous < 0)
            {
                if (code > 255) break;
                output[o++] = (byte)code;
                previous = code;
                continue;
            }
            if (code > next || code == next && next >= Max) break; // damaged
            if (next < Max)
            {
                prefix[next] = (short)previous;
                length[next] = (short)(length[previous] + 1);
                first[next] = first[previous];
                last[next] = code < next ? first[code] : first[previous];
                next++;
            }
            // The code's string, written backwards from its end.
            int n = length[code];
            for (int c = code, k = n - 1; c >= 0; c = prefix[c], k--)
                if (o + k < output.Length) output[o + k] = last[c];
            o += Math.Min(n, output.Length - o);
            previous = code;
            if (next + 1 >= 1 << width && width < 12) width++;
        }
        return o;
    }

    /// <summary>Deflate in a zlib wrapper (compression 8 and 32946), as far as it goes.</summary>
    private static void Inflate(byte[] source, int start, int length, Span<byte> output)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(source, start, length, writable: false), CompressionMode.Decompress);
            int o = 0;
            while (o < output.Length)
            {
                int n = z.Read(output[o..]);
                if (n == 0) break;
                o += n;
            }
        }
        catch (InvalidDataException)
        {
            // Damaged: what was decoded before stays.
        }
    }

    private static byte Reverse(byte b)
    {
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
    }

    // ------------------------------------------------------------------ tags

    private sealed record Entry(int Type, long Count, int ValueAt);

    private sealed class Ifd
    {
        public Dictionary<int, Entry> Entries { get; } = new();
    }

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    /// <summary>Where a tag's values are, or -1 when they are missing or outside the file.</summary>
    private long ValuesAt(Entry e)
    {
        long size = TypeSize(e.Type) * e.Count;
        if (size <= 0) return -1;
        long at = size <= 4 ? e.ValueAt : U32(_file, e.ValueAt, _little);
        return at < 0 || at + size > _file.Length ? -1 : at;
    }

    /// <summary>A tag's integer values (BYTE, SHORT, LONG and their signed forms), or null.</summary>
    private long[]? Values(Ifd ifd, int tag)
    {
        if (!ifd.Entries.TryGetValue(tag, out var e) || e.Type is not (1 or 3 or 4 or 6 or 8 or 9 or 13)) return null;
        long at = ValuesAt(e);
        if (at < 0) return null;
        var values = new long[e.Count];
        int size = TypeSize(e.Type);
        for (long i = 0; i < e.Count; i++)
        {
            int p = (int)(at + i * size);
            values[i] = e.Type switch
            {
                1 => _file[p],
                6 => (sbyte)_file[p],
                3 => U16(_file, p, _little),
                8 => (short)U16(_file, p, _little),
                9 => (int)U32(_file, p, _little),
                _ => U32(_file, p, _little),
            };
        }
        return values;
    }

    private long Value(Ifd ifd, int tag, long fallback) => Values(ifd, tag) is { Length: > 0 } v ? v[0] : fallback;

    private byte[]? Bytes(Ifd ifd, int tag)
    {
        if (!ifd.Entries.TryGetValue(tag, out var e) || TypeSize(e.Type) != 1) return null;
        long at = ValuesAt(e);
        return at < 0 ? null : _file.AsSpan((int)at, (int)e.Count).ToArray();
    }

    private double Rational(Ifd ifd, int tag)
    {
        if (!ifd.Entries.TryGetValue(tag, out var e) || e.Type != 5) return 0;
        long at = ValuesAt(e);
        if (at < 0) return 0;
        double num = U32(_file, (int)at, _little), den = U32(_file, (int)at + 4, _little);
        return den == 0 ? 0 : num / den;
    }

    /// <summary>Dots per inch from XResolution, YResolution and ResolutionUnit (2 inch, 3 centimetre, 1 none: an aspect ratio only).</summary>
    private (double X, double Y) Resolution(Ifd ifd)
    {
        double x = Rational(ifd, 282), y = Rational(ifd, 283);
        if (x is <= 0 or > 100_000) x = 0;
        if (y is <= 0 or > 100_000) y = x;
        if (x == 0) x = y;
        if (x == 0) return (DefaultDpi, DefaultDpi);
        switch (Value(ifd, 296, 2))
        {
            case 3: return (x * 2.54, y * 2.54);
            case 1: return (DefaultDpi, DefaultDpi * y / x);
            default: return (x, y);
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Budget(long bytes)
    {
        _decoded += bytes;
        if (_decoded > MaxDecodedBytes)
            throw new ViewerTiffException($"The TIFF file's pictures take more than {MaxDecodedBytes / (1024 * 1024)} MB once decoded, more than this app reads from one file.", true);
    }

    private static ViewerTiffException Damaged(string what) => new($"It is damaged: {what}.");

    private static ViewerTiffException Unsupported(string what) => new($"Its pixels are {what}, which this app does not read.");

    private static int U16(byte[] b, int at, bool little) => at + 2 > b.Length ? 0 :
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

    private static long U32(byte[] b, int at, bool little) => at + 4 > b.Length ? 0 :
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
}
