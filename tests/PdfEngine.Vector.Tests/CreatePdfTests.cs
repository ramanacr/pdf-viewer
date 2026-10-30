using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Streams;
using PdfEngine.Vector.Text;
using PdfEngine.Vector.Windows.Create;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Create PDF: images (encoded here with Windows Imaging Component) become pages of the right
/// size and orientation, a JPEG keeps its bytes, lossless images keep their pixels, plain text
/// is laid out with an embedded font and stays extractable, and combined documents get an
/// outline item per source.
/// </summary>
public class CreatePdfTests
{
    // ------------------------------------------------------------------ synthetic images

    private static BitmapSource Pattern(int width, int height, PixelFormat format, double dpi = 96, int seed = 1)
    {
        int bpp = (format.BitsPerPixel + 7) / 8;
        int stride = width * bpp;
        var pixels = new byte[stride * height];
        var rnd = new Random(seed);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < bpp; c++)
                    pixels[y * stride + x * bpp + c] = (byte)(c == 3 ? 40 + (x * 7 + y) % 216 : (x * 255 / Math.Max(1, width - 1) + y * 3 + c * 60 + rnd.Next(8)) & 0xFF);
        var bmp = BitmapSource.Create(width, height, dpi, dpi, format, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Encode(BitmapEncoder encoder, params BitmapFrame[] frames)
    {
        foreach (var f in frames) encoder.Frames.Add(f);
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Jpeg(BitmapSource source, ushort orientation = 0)
    {
        BitmapMetadata? metadata = null;
        if (orientation != 0)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        }
        return Encode(new JpegBitmapEncoder { QualityLevel = 90 }, BitmapFrame.Create(source, null, metadata, null));
    }

    private static byte[] Png(BitmapSource source) => Encode(new PngBitmapEncoder(), BitmapFrame.Create(source));

    private static (PdfVectorDocument Doc, byte[] Bytes) Create(ImagesToPdfOptions? options, params (string Name, byte[] Bytes)[] images)
    {
        byte[] pdf = ImagesToPdf.ToBytes(images.Select(i => ImageInput.FromBytes(i.Name, i.Bytes)).ToList(), options);
        return (PdfVectorDocument.OpenAsync(pdf).AsTask().GetAwaiter().GetResult(), pdf);
    }

    private static PdfStream Image(PdfVectorDocument doc, int page)
    {
        var node = doc.PageTree.Pages[page - 1];
        var xobjects = (PdfDictionary)doc.Resolver.Resolve(node.Resources["XObject"])!;
        return (PdfStream)doc.Resolver.Resolve(xobjects["Im0"])!;
    }

    private static byte[] Samples(PdfVectorDocument doc, PdfStream stream) =>
        new PdfStreamDecoder(resolve: o => doc.Resolver.Resolve(o)).DecodeStream(stream);

    private static string Content(PdfVectorDocument doc, int page) =>
        Encoding.ASCII.GetString(new PdfStreamDecoder().DecodeStream(doc.PageTree.Pages[page - 1].Contents[0]));

    // ------------------------------------------------------------------ images

    [Fact]
    public void AJpeg_PassesThrough_WithItsBytesIdentical_AndThePageAtItsResolution()
    {
        byte[] jpeg = Jpeg(Pattern(64, 48, PixelFormats.Bgr24, dpi: 72));
        var header = JpegHeader.TryRead(jpeg)!;
        Assert.True(header.CanPassThrough);
        var (doc, pdf) = Create(null, ("photo.jpg", jpeg));
        using (doc)
        {
            Assert.Equal(1, doc.PageCount);
            var image = Image(doc, 1);
            Assert.Equal("DCTDecode", image.Dictionary.GetName("Filter"));
            Assert.Equal(jpeg, image.GetRawBytes().ToArray());
            Assert.Equal("DeviceRGB", image.Dictionary.GetName("ColorSpace"));
            double dpi = header.DpiX > 1 ? header.DpiX : 96;
            var box = doc.PageTree.Pages[0].MediaBox;
            Assert.Equal(64 * 72 / dpi, box.Width, 2);
            Assert.Equal(48 * 72 / dpi, box.Height, 2);
        }
        Assert.True(pdf.Length < jpeg.Length + 2000, $"{pdf.Length} bytes for a {jpeg.Length}-byte JPEG");
        Assert.Contains("/Producer", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void ExifOrientation_TurnsThePage_WithoutReencoding()
    {
        byte[] jpeg = Jpeg(Pattern(80, 40, PixelFormats.Bgr24, dpi: 72), orientation: 6);
        var header = JpegHeader.TryRead(jpeg)!;
        Assert.Equal(6, header.Orientation);
        double k = 72 / (header.DpiX > 1 ? header.DpiX : 96);
        var (doc, _) = Create(null, ("turned.jpg", jpeg));
        using (doc)
        {
            var box = doc.PageTree.Pages[0].MediaBox;
            Assert.Equal(40 * k, box.Width, 2);  // shown a quarter turn clockwise: portrait
            Assert.Equal(80 * k, box.Height, 2);
            Assert.Equal(jpeg, Image(doc, 1).GetRawBytes().ToArray());
            string w = PdfObjectWriter.FormatReal(Math.Round(40 * k, 4)), h = PdfObjectWriter.FormatReal(Math.Round(80 * k, 4));
            Assert.Contains($"0 -{h} {w} 0 0 {h} cm", Content(doc, 1));
        }
    }

    [Theory]
    [InlineData(1, 1, 0, 0, 1)]
    [InlineData(3, -1, 0, 0, -1)]
    [InlineData(6, 0, -1, 1, 0)]
    [InlineData(8, 0, 1, -1, 0)]
    public void PlacementMatrix_DrawsTheStoredPixelsUpright(int orientation, double a, double b, double c, double d)
    {
        var p = new PdfImagePlacement(1, 1, 0, 0, 1, 1, false);
        var m = p.Matrix(orientation);
        Assert.Equal((a, b, c, d), (m.A, m.B, m.C, m.D));
        // The stored top-left pixel (image space 0,1) lands where the orientation says it is seen.
        double x = m.C + m.E, y = m.D + m.F;
        var expected = orientation switch { 1 => (0.0, 1.0), 3 => (1.0, 0.0), 6 => (1.0, 1.0), _ => (0.0, 0.0) };
        Assert.Equal(expected, (x, y));
    }

    [Fact]
    public void APng_RoundTripsItsPixels_Losslessly_WithItsAlphaAsASoftMask()
    {
        var opaque = Pattern(37, 23, PixelFormats.Bgr24, seed: 5);
        var withAlpha = Pattern(19, 11, PixelFormats.Bgra32, seed: 9);
        var (doc, _) = Create(null, ("a.png", Png(opaque)), ("b.png", Png(withAlpha)));
        using (doc)
        {
            Assert.Equal(2, doc.PageCount);
            var first = Image(doc, 1);
            Assert.Equal("FlateDecode", first.Dictionary.GetName("Filter"));
            var bgr = new byte[37 * 23 * 3];
            opaque.CopyPixels(bgr, 37 * 3, 0);
            var rgb = new byte[bgr.Length];
            for (int i = 0; i < bgr.Length; i += 3) { rgb[i] = bgr[i + 2]; rgb[i + 1] = bgr[i + 1]; rgb[i + 2] = bgr[i]; }
            Assert.Equal(rgb, Samples(doc, first));
            Assert.Null(first.Dictionary["SMask"]);

            var second = Image(doc, 2);
            var bgra = new byte[19 * 11 * 4];
            withAlpha.CopyPixels(bgra, 19 * 4, 0);
            var smask = (PdfStream)doc.Resolver.Resolve(second.Dictionary["SMask"])!;
            var alpha = Samples(doc, smask);
            Assert.Equal(Enumerable.Range(0, 19 * 11).Select(i => bgra[i * 4 + 3]).ToArray(), alpha);
            var colour = Samples(doc, second);
            Assert.Equal(19 * 11 * 3, colour.Length);
        }
    }

    [Fact]
    public void AMultiPageTiff_GivesAPageAFrame_EachAtItsOwnSize()
    {
        byte[] tiff = Encode(new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            BitmapFrame.Create(Pattern(30, 20, PixelFormats.Bgr24, 72, 1)),
            BitmapFrame.Create(Pattern(20, 30, PixelFormats.Gray8, 72, 2)),
            BitmapFrame.Create(Pattern(16, 16, PixelFormats.BlackWhite, 72, 3)));
        var (doc, _) = Create(null, ("scan.tif", tiff));
        using (doc)
        {
            Assert.Equal(3, doc.PageCount);
            Assert.Equal((30.0, 20.0), (doc.PageTree.Pages[0].MediaBox.Width, doc.PageTree.Pages[0].MediaBox.Height));
            Assert.Equal((20.0, 30.0), (doc.PageTree.Pages[1].MediaBox.Width, doc.PageTree.Pages[1].MediaBox.Height));
            Assert.Equal("DeviceGray", Image(doc, 2).Dictionary.GetName("ColorSpace"));
            var bilevel = Image(doc, 3);
            Assert.Equal(1, bilevel.Dictionary.GetInteger("BitsPerComponent"));
            Assert.Equal(16 * 2, Samples(doc, bilevel).Length); // two bytes a row
        }
    }

    [Fact]
    public void APaletteImage_KeepsItsPalette()
    {
        var palette = new BitmapPalette(new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.White });
        var indices = Enumerable.Range(0, 8 * 8).Select(i => (byte)(i % 4)).ToArray();
        var bmp = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Indexed8, palette, indices, 8);
        var (doc, _) = Create(null, ("icon.png", Png(bmp)));
        using (doc)
        {
            var image = Image(doc, 1);
            var space = Assert.IsType<PdfArray>(image.Dictionary["ColorSpace"]);
            Assert.Equal("Indexed", ((PdfName)space[0]).Value);
            Assert.Equal(indices.Length, Samples(doc, image).Length * 8 / image.Dictionary.GetInteger("BitsPerComponent")!.Value);
        }
    }

    [Fact]
    public void Greyscale_AndJpegRecompression_AreHonoured()
    {
        var png = Png(Pattern(40, 30, PixelFormats.Bgr24));
        var (grey, _) = Create(new ImagesToPdfOptions { Image = new ImageConversionOptions { Color = ImageColorMode.Grayscale } }, ("a.png", png));
        using (grey)
        {
            Assert.Equal("DeviceGray", Image(grey, 1).Dictionary.GetName("ColorSpace"));
            Assert.Equal(40 * 30, Samples(grey, Image(grey, 1)).Length);
        }
        var (jpeg, _) = Create(new ImagesToPdfOptions { Image = new ImageConversionOptions { Compression = ImageCompression.Jpeg, JpegQuality = 60 } }, ("a.png", png));
        using (jpeg)
        {
            var image = Image(jpeg, 1);
            Assert.Equal("DCTDecode", image.Dictionary.GetName("Filter"));
            Assert.NotNull(JpegHeader.TryRead(image.GetRawBytes().Span));
        }
    }

    [Fact]
    public void PaperLayout_TurnsToLandscape_AndFitsInsideTheMargins()
    {
        var options = new ImagesToPdfOptions { Page = new PdfImagePageOptions { Size = PdfImagePageSize.Paper, Margin = 36 } };
        var (doc, _) = Create(options, ("wide.png", Png(Pattern(300, 100, PixelFormats.Bgr24))));
        using (doc)
        {
            var box = doc.PageTree.Pages[0].MediaBox;
            Assert.Equal(841.89, box.Width, 2);
            Assert.Equal(595.276, box.Height, 2);
            // 300 x 100 fits 769.89 wide: scale 2.5663, drawn 769.89 x 256.63, centred.
            Assert.Contains("769.89 0 0 256.63 36 169.323 cm", Content(doc, 1));
        }
        var place = PdfImagePlacement.For(new PdfCreateImage { Width = 100, Height = 100, Color = PdfImageColor.Gray, Kind = PdfImageData.Samples, Data = new byte[10000] },
            new PdfImagePageOptions { Size = PdfImagePageSize.Paper, Fill = true, PaperWidth = 200, PaperHeight = 100, Margin = 0, AutoOrientation = false });
        Assert.True(place.Clip);
        Assert.Equal((200.0, 200.0), (place.Width, place.Height));
    }

    [Fact]
    public void AJpegsIccProfile_BecomesAnIccBasedColourSpace()
    {
        byte[] jpeg = Jpeg(Pattern(16, 16, PixelFormats.Bgr24));
        byte[] icc = SrgbProfile.Bytes;
        // APP2 "ICC_PROFILE" chunk 1 of 1, right after SOI.
        var app2 = new List<byte> { 0xFF, 0xE2 };
        int length = 2 + 12 + 2 + icc.Length;
        app2.Add((byte)(length >> 8)); app2.Add((byte)length);
        app2.AddRange("ICC_PROFILE\0"u8.ToArray());
        app2.Add(1); app2.Add(1);
        app2.AddRange(icc);
        byte[] withProfile = jpeg.Take(2).Concat(app2).Concat(jpeg.Skip(2)).ToArray();
        Assert.Equal(icc, JpegHeader.TryRead(withProfile)!.IccProfile);

        var (doc, _) = Create(null, ("profiled.jpg", withProfile), ("again.jpg", withProfile));
        using (doc)
        {
            var space = Assert.IsType<PdfArray>(Image(doc, 1).Dictionary["ColorSpace"]);
            Assert.Equal("ICCBased", ((PdfName)space[0]).Value);
            var profile = (PdfStream)doc.Resolver.Resolve(space[1])!;
            Assert.Equal(3, profile.Dictionary.GetInteger("N"));
            Assert.Equal(icc, Samples(doc, profile));
            // The same profile is written once.
            Assert.Equal(space[1], Assert.IsType<PdfArray>(Image(doc, 2).Dictionary["ColorSpace"])[1]);
        }
    }

    [Fact]
    public void JpegHeader_RefusesWhatDctDecodeCannotRead()
    {
        // SOI, SOF9 (arithmetic coding) 8-bit 4x4 with 3 components.
        byte[] arithmetic = { 0xFF, 0xD8, 0xFF, 0xC9, 0, 17, 8, 0, 4, 0, 4, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0 };
        var header = JpegHeader.TryRead(arithmetic)!;
        Assert.Equal(3, header.Components);
        Assert.False(header.CanPassThrough);
        Assert.Null(JpegHeader.TryRead(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
    }

    [Fact]
    public void AnUnreadableImage_IsReportedByName()
    {
        var ex = Assert.Throws<ImageInputException>(() => ImagesToPdf.ToBytes(new[] { ImageInput.FromBytes("notes.webp", Encoding.ASCII.GetBytes("not an image")) }));
        Assert.Equal("notes.webp", ex.Name);
    }

    [Fact]
    public void AnImageDocument_ConvertsToPdfA2b()
    {
        var (doc, pdf) = Create(new ImagesToPdfOptions { Info = new PdfCreateInfo { Title = "Scans", Producer = "Test" } },
            ("a.jpg", Jpeg(Pattern(32, 32, PixelFormats.Bgr24))), ("b.png", Png(Pattern(16, 16, PixelFormats.Bgra32))));
        using (doc)
        {
            Assert.Equal("Scans", PdfDocumentInfo.ReadProperties(doc).Title);
            Assert.Equal("Test", PdfDocumentInfo.ReadProperties(doc).Producer);
            var result = PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavor = PdfAFlavor.A2b });
            Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
        }
    }

    // ------------------------------------------------------------------ text

    private static string PageText(byte[] pdf, int page)
    {
        using var doc = PdfVectorDocument.OpenAsync(pdf).AsTask().GetAwaiter().GetResult();
        return PdfTextExtractor.LoadPage(doc, page).Text;
    }

    [Fact]
    public async Task PlainText_IsLaidOutWithAnEmbeddedFont_AndExtractable()
    {
        using var ms = new MemoryStream();
        var result = PdfTextDocument.Write("Hello world from a text file\nSecond line\tafter a tab\fPage two: café ünïcödé", ms,
            info: new PdfCreateInfo { Title = "notes.txt" });
        byte[] pdf = ms.ToArray();
        Assert.Equal(2, result.Pages);
        string first = PageText(pdf, 1), second = PageText(pdf, 2);
        Assert.Contains("Hello world from a text file", first);
        Assert.Contains("Second line", first);
        Assert.Contains("after a tab", first);
        Assert.Contains("café ünïcödé", second);
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/FontFile2", raw);
        Assert.Contains("/ToUnicode", raw);

        // The viewer's native text service reads it too.
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var segments = await new PdfVectorTextService().ExtractTextSegmentsAsync(doc, 1);
        Assert.Contains("Hello", string.Join(" ", segments.Select(s => s.Text)));
    }

    [Fact]
    public void LongText_WrapsAtSpaces_AndFillsPages()
    {
        string paragraph = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"word{i}"));
        string text = string.Join("\n", Enumerable.Range(0, 80).Select(_ => paragraph));
        using var ms = new MemoryStream();
        var result = PdfTextDocument.Write(text, ms);
        Assert.True(result.Lines > 80 * 3, $"{result.Lines} lines");
        Assert.True(result.Pages >= 4, $"{result.Pages} pages");
        string page = PageText(ms.ToArray(), 1);
        Assert.Contains("word59 word60", page);
        Assert.DoesNotContain("wor d", page);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41, 0xC3, 0xA9 }, "Aé")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0, 0xE9, 0 }, "Aé")]
    [InlineData(new byte[] { 0x41, 0xC3, 0xA9 }, "Aé")]
    [InlineData(new byte[] { 0x41, 0xE9, 0x80 }, "Aé€")]
    public void TextFiles_AreDecodedByTheirMarkOrContent(byte[] bytes, string expected) => Assert.Equal(expected, PdfTextDocument.Decode(bytes));

    // ------------------------------------------------------------------ combine

    [Fact]
    public async Task CombinedSources_GetAnOutlineItemEach_AndTheCreatorsInformation()
    {
        byte[] images = ImagesToPdf.ToBytes(new[]
        {
            ImageInput.FromBytes("a.png", Png(Pattern(20, 20, PixelFormats.Bgr24))),
            ImageInput.FromBytes("b.png", Png(Pattern(20, 30, PixelFormats.Bgr24))),
        });
        using var text = new MemoryStream();
        PdfTextDocument.Write("Minutes of the meeting", text);
        using var first = await PdfVectorDocument.OpenAsync(images);
        using var second = await PdfVectorDocument.OpenAsync(text.ToArray());
        byte[] combined = PdfPageAssembler.Merge(new[] { first, second }, new PdfAssemblyOptions
        {
            SourceTitles = new[] { "Photos", "Minutes" },
            Info = new PdfCreateInfo { Title = "Binder", Producer = "Create PDF test" },
        });
        using var doc = await PdfVectorDocument.OpenAsync(combined);
        Assert.Equal(3, doc.PageCount);
        var outline = PdfDocumentInfo.ReadOutline(doc);
        Assert.Equal(new[] { "Photos", "Minutes" }, outline.Select(o => o.Title));
        var props = PdfDocumentInfo.ReadProperties(doc);
        Assert.Equal("Binder", props.Title);
        Assert.Equal("Create PDF test", props.Producer);
        Assert.Contains("Minutes of the meeting", PdfTextExtractor.LoadPage(doc, 3).Text);
    }

    /// <summary>The oracle reads what we write: PDFium opens it without repair, with the pages, text and bookmarks we put there.</summary>
    [Fact]
    public async Task Pdfium_ReadsTheCreatedDocuments()
    {
        byte[] images = ImagesToPdf.ToBytes(new[]
        {
            ImageInput.FromBytes("a.jpg", Jpeg(Pattern(64, 48, PixelFormats.Bgr24, dpi: 72), orientation: 8)),
            ImageInput.FromBytes("b.png", Png(Pattern(20, 30, PixelFormats.Bgra32))),
        });
        using var text = new MemoryStream();
        PdfTextDocument.Write("Read by the oracle", text);
        using var first = await PdfVectorDocument.OpenAsync(images);
        using var second = await PdfVectorDocument.OpenAsync(text.ToArray());
        byte[] combined = PdfPageAssembler.Merge(new[] { first, second }, new PdfAssemblyOptions { SourceTitles = new[] { "Pictures", "Text" } });

        using var engine = new PdfEngine.Pdfium.PdfiumEngine();
        using var doc = await engine.OpenDocumentAsync(combined);
        Assert.Equal(3, doc.PageCount);
        var page1 = await doc.GetPageInfoAsync(1);
        Assert.True(page1.HeightPoints > page1.WidthPoints, "orientation 8 shows the landscape JPEG as portrait");
        var segments = await engine.TextService.ExtractTextSegmentsAsync(doc, 3);
        Assert.Contains("oracle", string.Concat(segments.Select(s => s.Text)));
        Assert.Equal(new[] { "Pictures", "Text" }, (await doc.GetBookmarksAsync()).Select(b => b.Title));
    }

    /// <summary>
    /// Page and catalog entries of the caller's (an annotation, an outline) are written with the
    /// writer's own, and a fixed date writes the same bytes every time.
    /// </summary>
    [Fact]
    public async Task TheWriter_AddsPageAndCatalogEntries_AndAFixedDateIsReproducible()
    {
        static byte[] Write()
        {
            using var ms = new MemoryStream();
            using (var w = new PdfDocumentWriter(ms))
            {
                int annot = w.Reserve(), outline = w.Reserve(), item = w.Reserve();
                int page = w.AddPage(200, 100, Encoding.ASCII.GetBytes("0 0 1 rg 10 10 50 50 re f"),
                    new PdfDictionary(new Dictionary<string, PdfObject>()),
                    new Dictionary<string, PdfObject>
                    {
                        ["Annots"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(annot) }),
                        ["MediaBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(9), new PdfInteger(9) }),
                    });
                PdfArray Rect(params int[] v) => new(v.Select(n => (PdfObject)new PdfInteger(n)).ToList());
                w.Write(annot, new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Square"), ["Rect"] = Rect(5, 5, 65, 65),
                    ["C"] = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(0), new PdfInteger(0) }), ["P"] = new PdfIndirectRef(page),
                }));
                w.Write(item, new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Title"] = PdfObjectWriter.TextString("Start"), ["Parent"] = new PdfIndirectRef(outline),
                    ["Dest"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(page), new PdfName("Fit") }),
                }));
                w.Write(outline, new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("Outlines"), ["First"] = new PdfIndirectRef(item), ["Last"] = new PdfIndirectRef(item), ["Count"] = new PdfInteger(1),
                }));
                w.Finish(new PdfCreateInfo { Title = "Entries", Date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
                    new Dictionary<string, PdfObject> { ["Outlines"] = new PdfIndirectRef(outline), ["Type"] = new PdfName("NotACatalog") });
            }
            return ms.ToArray();
        }

        byte[] first = Write();
        Assert.Equal(first, Write());
        string written = Encoding.Latin1.GetString(first);
        Assert.True(written.Contains("D:20260102030405+00'00'", StringComparison.Ordinal)
            || written.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes("D:20260102030405")), StringComparison.OrdinalIgnoreCase),
            "the fixed date is written as /CreationDate");

        using var doc = await PdfVectorDocument.OpenAsync(first);
        var info = await doc.GetPageInfoAsync(1);
        Assert.Equal(200, info.WidthPoints, 3); // the writer's /MediaBox wins over the caller's
        Assert.Equal("Start", Assert.Single(await doc.GetBookmarksAsync()).Title);
        Assert.Equal("Square", Assert.Single(PdfEngine.Vector.Annotations.PdfAnnotationReader.Read(doc, 1)).Subtype);
    }

    [Fact]
    public async Task TheWriter_StreamsToANonSeekableOutput_AndRefusesAnEmptyDocument()
    {
        var sink = new NonSeekable();
        using (var writer = new PdfDocumentWriter(sink))
        {
            writer.AddImagePage(new PdfCreateImage { Width = 2, Height = 2, Color = PdfImageColor.Gray, Kind = PdfImageData.Samples, Data = new byte[] { 0, 255, 255, 0 } }, new PdfImagePageOptions());
            writer.Finish();
        }
        using var doc = await PdfVectorDocument.OpenAsync(sink.Inner.ToArray());
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(new byte[] { 0, 255, 255, 0 }, Samples(doc, Image(doc, 1)));
        using var empty = new PdfDocumentWriter(new MemoryStream());
        Assert.Throws<InvalidOperationException>(() => empty.Finish());
    }

    private sealed class NonSeekable : Stream
    {
        public MemoryStream Inner { get; } = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
    }
}
