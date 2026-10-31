using System.Text;
using System.Text.Json;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Annotations;
using PdfViewer.Web.Engine.Embed;
using SkiaSharp;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Custom stamps in the shared editing core (<see cref="ViewerEditSession.AddCustomStamp"/>, used
/// by the web viewer, its embed SDK and the Android app): a picture or a PDF page placed as a
/// /Stamp whose appearance draws it (a page as vector content), its shape kept, undo, the limits
/// on untrusted pictures and PDFs, and kept edits. Every picture and PDF is made here.
/// </summary>
public class CustomStampTests
{
    private static readonly SKColor Red = new(255, 0, 0), Blue = new(0, 0, 255);

    // ------------------------------------------------------------------ pictures and PDFs made here

    /// <summary>A picture whose left half is red and right half blue (or transparent).</summary>
    private static byte[] Picture(int w, int h, SKEncodedImageFormat format = SKEncodedImageFormat.Png, bool transparentRight = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bitmap.SetPixel(x, y, x < w / 2 ? Red : transparentRight ? SKColors.Transparent : Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>A PDF whose page 1 (300 x 150) is green on the left and blue on the right, as paths; more plain pages after it.</summary>
    private static byte[] SourcePdf(int pages = 1, string extraPage = "", PdfTestEncryption? encryption = null)
    {
        var b = new VectorPdfBuilder();
        b.AddPage("0 1 0 rg 0 0 150 150 re f 0 0 1 rg 150 0 150 150 re f", mediaBox: "[0 0 300 150]", extra: extraPage);
        for (int i = 1; i < pages; i++) b.AddPage("", mediaBox: "[0 0 300 150]");
        return b.Build(encryption);
    }

    private static byte[] Pages(int count = 1, int rotate = 0)
    {
        var b = new VectorPdfBuilder();
        for (int i = 0; i < count; i++) b.AddPage("", mediaBox: "[0 0 612 792]", extra: rotate != 0 ? $"/Rotate {rotate}" : "");
        return b.Build();
    }

    private static async Task<(ViewerDocument Doc, ViewerEditSession Session)> Start(byte[] pdf)
    {
        var doc = await ViewerDocument.OpenAsync(pdf);
        var session = await ViewerEditSession.StartAsync(doc, author: "Reader");
        return (doc, session);
    }

    /// <summary>The colour drawn at a point of the shown page (scale 1), as (R, G, B).</summary>
    private static async Task<(int R, int G, int B)> PixelAt(ViewerDocument doc, int page, double x, double y)
    {
        var bitmap = await doc.RenderPageAsync(page, 1);
        int px = (int)Math.Round(x * bitmap.Scale), py = (int)Math.Round(y * bitmap.Scale);
        int i = bitmap.Pixels.Offset + (py * bitmap.Width + px) * 4;
        var p = bitmap.Pixels.Array!;
        return (p[i + 2], p[i + 1], p[i]);
    }

    private static void Is(Func<(int R, int G, int B), bool> test, (int R, int G, int B) c) => Assert.True(test(c), $"The pixel is {c}.");

    private static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 60 && c.B < 60;
    private static bool IsBlue((int R, int G, int B) c) => c.B > 200 && c.R < 60 && c.G < 60;
    private static bool IsGreen((int R, int G, int B) c) => c.G > 200 && c.R < 60 && c.B < 60;
    private static bool IsWhite((int R, int G, int B) c) => c.R > 235 && c.G > 235 && c.B > 235;

    private static async Task<IReadOnlyList<PdfAnnotation>> ReadPdf(byte[] bytes, int page)
    {
        using var vector = await PdfVectorDocument.OpenAsync(bytes);
        return PdfAnnotationReader.Read(vector, page);
    }

    // ------------------------------------------------------------------ a picture

    [Fact]
    public async Task APicture_IsPlacedInTheDraggedBox_KeepingItsShape_AndDrawnThere()
    {
        var stamp = ViewerCustomStamp.FromImage(Picture(40, 20));
        Assert.Equal(ViewerCustomStampKind.Image, stamp.Kind);
        Assert.Equal(2.0, stamp.Aspect, 6);
        var (doc, session) = await Start(Pages());
        await using var _ = doc;

        // A square box: the 2:1 picture fills its width and is centred down it.
        var a = session.AddCustomStamp(1, new ViewerRect(100, 100, 200, 200), stamp);
        var shown = doc.Geometry[0].ToShown(a.Box);
        Assert.Equal((100.0, 150.0, 200.0, 100.0), (Math.Round(shown.X, 6), Math.Round(shown.Y, 6), Math.Round(shown.Width, 6), Math.Round(shown.Height, 6)));
        Assert.True(a.IsCustomStamp);
        Assert.Equal((ViewerAnnotationKind.Stamp, ViewerCustomStamp.StampName), (a.Kind, a.StampName));
        Assert.Same(stamp, a.CustomStamp);
        Assert.Equal(("Stamp", "Custom stamp"), (session.List()[0].Label, session.List()[0].Summary));

        Assert.True((await session.ApplyAsync()).Written);
        Is(IsRed, await PixelAt(doc, 1, 140, 200));
        Is(IsBlue, await PixelAt(doc, 1, 260, 200));
        Is(IsWhite, await PixelAt(doc, 1, 200, 140));
        Is(IsWhite, await PixelAt(doc, 1, 200, 260));

        // Saved as a /Stamp named Custom with an appearance that draws the picture; read back as a custom stamp.
        var saved = doc.Bytes;
        var read = Assert.Single(await ReadPdf(saved, 1));
        Assert.Equal(("Stamp", "Custom"), (read.Subtype, read.Icon));
        Assert.True(read.HasAppearance);
        using (var vector = await PdfVectorDocument.OpenAsync(saved))
        {
            var list = await vector.GetPageDisplayListAsync(1);
            Assert.Single(list.Commands.OfType<DrawImage>());
        }
        await using var again = await ViewerDocument.OpenAsync(saved);
        var back = Assert.Single(await again.ReadAnnotationsAsync());
        Assert.True(back.IsCustomStamp);
        Assert.Null(back.CustomStamp);
        Is(IsRed, await PixelAt(again, 1, 140, 200));
    }

    [Fact]
    public async Task APicture_PlacedWithAClick_IsTwoInchesAcross_CentredThere_AndItsTransparencyShows()
    {
        var stamp = ViewerCustomStamp.FromImage(Picture(30, 60, transparentRight: true));
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        var a = session.AddCustomStamp(1, new ViewerRect(300, 400, 0, 0), stamp);
        var shown = doc.Geometry[0].ToShown(a.Box);
        Assert.Equal(72, shown.Width, 6);
        Assert.Equal(144, shown.Height, 6);
        Assert.Equal(300, shown.X + shown.Width / 2, 6);
        Assert.Equal(400, shown.Y + shown.Height / 2, 6);
        await session.ApplyAsync();
        Is(IsRed, await PixelAt(doc, 1, 280, 400));
        Is(IsWhite, await PixelAt(doc, 1, 320, 400));
    }

    [Fact]
    public async Task AJpeg_IsEmbeddedAsItIs()
    {
        byte[] jpeg = Picture(64, 32, SKEncodedImageFormat.Jpeg);
        var stamp = ViewerCustomStamp.FromImage(jpeg);
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        session.AddCustomStamp(1, new ViewerRect(100, 100, 128, 64), stamp);
        await session.ApplyAsync();
        // The JPEG's own bytes are in the file (DCTDecode, not decoded and written again).
        Assert.True(doc.Bytes.AsSpan().IndexOf(jpeg) > 0);
        Is(IsRed, await PixelAt(doc, 1, 120, 132));
        Is(IsBlue, await PixelAt(doc, 1, 210, 132));
    }

    [Fact]
    public async Task OnATurnedPage_TheStampReadsUprightAsShown()
    {
        var stamp = ViewerCustomStamp.FromImage(Picture(40, 20));
        var (doc, session) = await Start(Pages(1, rotate: 90));
        await using var _ = doc;
        var g = doc.Geometry[0];
        Assert.Equal(792, g.ShownWidth);
        var a = session.AddCustomStamp(1, new ViewerRect(200, 200, 200, 100), stamp);
        Assert.Equal(90, a.StampTurn);
        var shown = g.ToShown(a.Box);
        Assert.Equal((200.0, 100.0), (Math.Round(shown.Width, 6), Math.Round(shown.Height, 6)));
        await session.ApplyAsync();
        // Red still on the left, blue on the right, as the page is shown.
        Is(IsRed, await PixelAt(doc, 1, 240, 250));
        Is(IsBlue, await PixelAt(doc, 1, 360, 250));
    }

    // ------------------------------------------------------------------ a PDF page

    [Fact]
    public async Task APdfPage_IsDrawnAsVectorContent()
    {
        var stamp = ViewerCustomStamp.FromPdf(SourcePdf(pages: 3), 1);
        Assert.Equal(ViewerCustomStampKind.Page, stamp.Kind);
        Assert.Equal((300.0, 150.0), (stamp.Width, stamp.Height));
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        var a = session.AddCustomStamp(1, new ViewerRect(100, 300, 300, 300), stamp);
        var shown = doc.Geometry[0].ToShown(a.Box);
        Assert.Equal(2.0, shown.Width / shown.Height, 6);
        await session.ApplyAsync();
        Is(IsGreen, await PixelAt(doc, 1, 150, 450));
        Is(IsBlue, await PixelAt(doc, 1, 350, 450));
        using var vector = await PdfVectorDocument.OpenAsync(doc.Bytes);
        var list = await vector.GetPageDisplayListAsync(1);
        Assert.Empty(list.Commands.OfType<DrawImage>());
        Assert.Equal(2, list.Commands.OfType<FillPath>().Count());
    }

    [Fact]
    public async Task ATurnedSourcePage_IsStampedAsItIsShown()
    {
        var stamp = ViewerCustomStamp.FromPdf(SourcePdf(extraPage: "/Rotate 90"), 1);
        Assert.Equal((150.0, 300.0), (stamp.Width, stamp.Height));
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        session.AddCustomStamp(1, new ViewerRect(100, 100, 100, 200), stamp);
        await session.ApplyAsync();
        // Turned a quarter clockwise: green (the page's left) is at the top, blue at the bottom.
        Is(IsGreen, await PixelAt(doc, 1, 150, 140));
        Is(IsBlue, await PixelAt(doc, 1, 150, 260));
    }

    [Fact]
    public async Task AnEncryptedPdf_IsRefused_UnlessItIsTheOpenDocument()
    {
        byte[] encrypted = SourcePdf(encryption: new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner"));
        var ex = Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf(encrypted, 1));
        Assert.Contains("encrypted", ex.Message);
        byte[] withPassword = SourcePdf(encryption: new PdfTestEncryption(TestEncryptionKind.Aes256_R6, "user", "owner"));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf(withPassword, 1));

        await using var open = await ViewerDocument.OpenAsync(withPassword, "user");
        var stamp = await ViewerCustomStamp.FromDocumentAsync(open, 1);
        Assert.Equal(300, stamp.Width);
        using (var one = PdfVectorDocument.Open(new PdfEngine.Vector.Parsing.PdfMemoryByteSource(stamp.Pdf)))
            Assert.False(one.IsEncrypted);
        await Assert.ThrowsAsync<ViewerAnnotationException>(() => ViewerCustomStamp.FromDocumentAsync(open, 2));

        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        session.AddCustomStamp(1, new ViewerRect(100, 100, 300, 150), stamp);
        await session.ApplyAsync();
        Is(IsGreen, await PixelAt(doc, 1, 150, 175));
    }

    [Fact]
    public async Task TheStamp_CarriesNoScriptAndNoLinkToAnotherFile()
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 300 150] /AA << /PO << /S /JavaScript /JS (app.alert\\(1\\)) >> >> /Ref << /F (elsewhere.pdf) /Page 0 >>",
            "1 0 0 rg 0 0 300 150 re f");
        b.AddPage("/Fm0 Do", resources: $"<< /XObject << /Fm0 {form} 0 R >> >>", mediaBox: "[0 0 300 150]",
            extra: "/AA << /O << /S /JavaScript /JS (app.alert\\(2\\)) >> >>");
        b.WithCatalog("/OpenAction << /S /JavaScript /JS (app.alert\\(3\\)) >>");
        var stamp = ViewerCustomStamp.FromPdf(b.Build(), 1);

        byte[] pdf = Pages();
        var (doc, session) = await Start(pdf);
        await using var _ = doc;
        session.AddCustomStamp(1, new ViewerRect(100, 100, 300, 150), stamp);
        await session.ApplyAsync();
        Is(IsRed, await PixelAt(doc, 1, 250, 175));
        string update = Encoding.Latin1.GetString(doc.Bytes, pdf.Length, doc.Bytes.Length - pdf.Length);
        Assert.DoesNotContain("JavaScript", update);
        Assert.DoesNotContain("app.alert", update);
        Assert.DoesNotContain("elsewhere.pdf", update);
    }

    // ------------------------------------------------------------------ editing

    [Fact]
    public async Task ACustomStamp_IsOneUndoStep_AndMoves_AndIsDeleted()
    {
        var stamp = ViewerCustomStamp.FromImage(Picture(40, 20));
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        var a = session.AddCustomStamp(1, new ViewerRect(100, 100, 200, 100), stamp);
        Assert.True(session.CanUndo);
        Assert.True(session.Undo());
        Assert.Empty(session.Store.Current);
        Assert.True(session.Redo());
        Assert.Same(stamp, Assert.Single(session.Store.Current).CustomStamp);

        Assert.True(session.Move(a.Id, 50, 60));
        var moved = session.Store.Find(a.Id)!;
        Assert.Same(stamp, moved.CustomStamp);
        var shown = doc.Geometry[0].ToShown(moved.Box);
        Assert.Equal((150.0, 160.0), (Math.Round(shown.X, 6), Math.Round(shown.Y, 6)));
        await session.ApplyAsync();
        Is(IsRed, await PixelAt(doc, 1, 190, 210));
        Is(IsWhite, await PixelAt(doc, 1, 140, 150));

        Assert.True(session.Delete(a.Id));
        Assert.Empty(session.Store.Current);
        await session.ApplyAsync();
        Is(IsWhite, await PixelAt(doc, 1, 190, 210));
        Assert.True(session.Undo());
        Assert.Single(session.Store.Current);
    }

    [Fact]
    public async Task AStampLargerThanThePage_IsMadeSmaller_AndKeptOnIt()
    {
        var stamp = ViewerCustomStamp.FromPdf(SourcePdf(), 1);
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        var a = session.AddCustomStamp(1, new ViewerRect(-500, -500, 3000, 3000), stamp);
        var shown = doc.Geometry[0].ToShown(a.Box);
        Assert.True(shown.X >= 0 && shown.Y >= 0 && shown.X + shown.Width <= 612.0001 && shown.Y + shown.Height <= 792.0001);
        Assert.Equal(2.0, shown.Width / shown.Height, 6);
        Assert.Throws<ViewerAnnotationException>(() => session.AddCustomStamp(1, new ViewerRect(double.NaN, 0, 10, 10), stamp));
        Assert.Throws<ViewerAnnotationException>(() => session.AddCustomStamp(2, new ViewerRect(0, 0, 10, 10), stamp));
    }

    [Fact]
    public async Task AHostPageUpdate_KeepsWhatTheStampDraws_AndItsShape()
    {
        var stamp = ViewerCustomStamp.FromImage(Picture(40, 20));
        var (doc, session) = await Start(Pages());
        await using var _ = doc;
        var a = session.AddCustomStamp(1, new ViewerRect(100, 100, 200, 100), stamp);

        // As getAnnotations gives it: subtype stamp, flagged custom, without the picture.
        var json = Json(w => EmbedAnnotationJson.Write(w, a, "file"));
        var selector = json.RootElement.GetProperty("target").GetProperty("selector");
        Assert.Equal("stamp", selector.GetProperty("subtype").GetString());
        Assert.True(selector.GetProperty("customStamp").GetBoolean());
        Assert.False(selector.TryGetProperty("customStampData", out var none));
        var withData = Json(w => EmbedAnnotationJson.Write(w, a, "file", stampData: true));
        var data = withData.RootElement.GetProperty("target").GetProperty("selector").GetProperty("customStampData");
        Assert.Equal("image", data.GetProperty("kind").GetString());
        Assert.Equal(stamp.Pdf, data.GetProperty("data").GetBytesFromBase64());

        // Given back with a square box: the stamp is fitted in it, and still draws the picture.
        var parsed = EmbedAnnotationJson.Parse(Grow(json.RootElement, 50), 1, "", default, requireId: true);
        var updated = session.UpdateAnnotation(parsed);
        Assert.Same(stamp, updated.CustomStamp);
        Assert.Equal(2.0, updated.Box.Width / updated.Box.Height, 6);

        // A new stamp named Custom without a picture is refused.
        var fake = parsed with { Id = "new" };
        Assert.Throws<ViewerAnnotationException>(() => session.AddAnnotations([fake]));
    }

    private static JsonDocument Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) write(w);
        return JsonDocument.Parse(ms.ToArray());
    }

    /// <summary>The annotation's JSON with its boundingBox grown upwards by <paramref name="by"/> points.</summary>
    private static JsonElement Grow(JsonElement annotation, double by)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(annotation.GetRawText())!;
        var bb = node["target"]!["selector"]!["boundingBox"]!.AsArray();
        bb[3] = bb[3]!.GetValue<double>() + by;
        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    // ------------------------------------------------------------------ limits on untrusted input

    [Fact]
    public void Pictures_TooLargeDamagedOrOfAnotherKind_AreRefused()
    {
        byte[] png = Picture(20, 10);
        // Too many bytes.
        var big = new byte[ViewerCustomStamp.MaxImageBytes + 1];
        png.CopyTo(big, 0);
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage(big));
        // Too many pixels, or a side too long (the header says so; nothing is decoded).
        Assert.Contains("pixels", Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage(PngHeader(5000, 5000))).Message);
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage(PngHeader(ViewerCustomStamp.MaxImageSide + 1, 1)));
        // Damaged: a PNG cut short, a JPEG cut short, a PNG signature with nothing after it.
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage(png[..(png.Length / 2)]));
        byte[] jpeg = Picture(64, 64, SKEncodedImageFormat.Jpeg);
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage(jpeg[..(jpeg.Length / 2)]));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage([.. png[..8], .. new byte[100]]));
        // Not a PNG or JPEG.
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage([.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0]));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage(SourcePdf()));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromImage([]));
    }

    [Fact]
    public void Pdfs_TooLargeDamagedOrWithoutThePage_AreRefused()
    {
        byte[] pdf = SourcePdf(pages: 2);
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf(pdf, 0));
        Assert.Contains("2 pages", Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf(pdf, 3)).Message);
        Assert.Equal(300, ViewerCustomStamp.FromPdf(pdf, 2).Width);
        // A huge file is refused before it is read.
        var huge = new byte[ViewerCustomStamp.MaxPdfBytes + 1];
        pdf.CopyTo(huge, 0);
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf(huge, 1));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf(Encoding.ASCII.GetBytes("%PDF-1.7\nnot really"), 1));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromPdf([], 1));
        // A kept stamp is checked again: two pages, or not a PDF, is not a stamp.
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromKept(ViewerCustomStampKind.Page, pdf));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.FromKept(ViewerCustomStampKind.Image, Picture(4, 4)));
        Assert.Equal(ViewerCustomStampKind.Page, ViewerCustomStamp.Sniff(pdf));
        Assert.Equal(ViewerCustomStampKind.Image, ViewerCustomStamp.Sniff(Picture(4, 4)));
        Assert.Null(ViewerCustomStamp.Sniff("GIF89a"u8));
    }

    /// <summary>A PNG that says it is a w x h RGBA picture (IHDR), with a few bytes of pixel data and its end: the size is read, the pixels are not there.</summary>
    private static byte[] PngHeader(int w, int h)
    {
        var file = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        void Chunk(string type, byte[] data)
        {
            file.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
            file.AddRange(body);
            uint crc = Crc32(body);
            file.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        }
        Chunk("IHDR", [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0]);
        Chunk("IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]);
        Chunk("IEND", []);
        return file.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    // ------------------------------------------------------------------ kept edits (Android)

    [Fact]
    public async Task KeptEdits_HoldCustomStamps_EachOnce_AndGiveThemBack()
    {
        var picture = ViewerCustomStamp.FromImage(Picture(40, 20));
        var page = ViewerCustomStamp.FromPdf(SourcePdf(), 1);
        byte[] pdf = Pages(2);
        var (doc, session) = await Start(pdf);
        await using var _ = doc;
        session.AddCustomStamp(1, new ViewerRect(100, 100, 200, 100), picture);
        session.AddCustomStamp(2, new ViewerRect(100, 100, 200, 100), picture);
        session.AddCustomStamp(1, new ViewerRect(100, 400, 300, 150), page);
        var snapshot = session.Snapshot("content://doc", ViewerEditSnapshot.Hash(pdf));
        byte[] utf8 = snapshot.ToUtf8()!;
        Assert.NotNull(utf8);
        string json = Encoding.UTF8.GetString(utf8);
        // The picture placed twice is kept once.
        Assert.Equal(2, JsonDocument.Parse(json).RootElement.GetProperty("customStamps").GetArrayLength());

        var read = ViewerEditSnapshot.FromJson(json);
        await using var again = await ViewerDocument.OpenAsync(pdf);
        var restored = await ViewerEditSession.StartAsync(again, author: "Reader");
        Assert.True(restored.Restore(read));
        Assert.Equal(3, restored.Store.Current.Count);
        Assert.Equal(2, restored.Store.Current.Count(a => a.CustomStamp!.Hash == picture.Hash));
        Assert.Equal(1, restored.Store.Current.Count(a => a.CustomStamp!.Hash == page.Hash));
        Assert.Same(restored.Store.Current.First(a => a.Page == 2).CustomStamp, restored.Store.Current.First(a => a.Page == 1 && a.CustomStamp!.Kind == ViewerCustomStampKind.Image).CustomStamp);
        await restored.ApplyAsync();
        Is(IsRed, await PixelAt(again, 1, 140, 150));
        Is(IsGreen, await PixelAt(again, 1, 150, 475));
        Is(IsBlue, await PixelAt(again, 2, 260, 150));

        // A stamp changed on the way is refused, as is one that names a stamp the edits do not have.
        string damaged = json.Replace(picture.Hash, new string('0', 64));
        Assert.Throws<FormatException>(() => ViewerEditSnapshot.FromJson(damaged));
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["customStamps"]!.AsArray().Clear();
        Assert.Throws<FormatException>(() => ViewerEditSnapshot.FromJson(node.ToJsonString()));
    }

    [Fact]
    public async Task KeptEdits_TooLargeForTheCap_AreNotKept()
    {
        var picture = ViewerCustomStamp.FromImage(Picture(40, 20));
        byte[] pdf = Pages();
        var (doc, session) = await Start(pdf);
        await using var _ = doc;
        session.AddCustomStamp(1, new ViewerRect(100, 100, 200, 100), picture);
        var snapshot = session.Snapshot("content://doc", ViewerEditSnapshot.Hash(pdf));
        Assert.Null(snapshot.ToUtf8(picture.Pdf.Length / 2));
        Assert.NotNull(snapshot.ToUtf8(picture.Pdf.Length * 2 + 4096));
        Assert.NotNull(snapshot.ToUtf8());
    }

    // ------------------------------------------------------------------ the stamp library (Android: app-private files)

    [Fact]
    public void TheStampLibrary_KeepsStamps_GivesThemBackChecked_AndDeletesThem()
    {
        string folder = Path.Combine(Path.GetTempPath(), "custom-stamps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new ViewerStampLibrary(folder);
            Assert.Empty(library.List());
            var picture = ViewerCustomStamp.FromImage(Picture(40, 20));
            var page = ViewerCustomStamp.FromPdf(SourcePdf(pages: 2), 2);
            Assert.Null(library.Save(picture, "Signature\n"));
            Assert.Null(library.Save(page, "Letterhead, page 2"));
            Assert.Null(library.Save(picture, "Again"));   // kept once
            var list = library.List();
            Assert.Equal([("Signature", ViewerCustomStampKind.Image), ("Letterhead, page 2", ViewerCustomStampKind.Page)], list.Select(s => (s.Name, s.Kind)));
            Assert.Equal(picture.Hash, list[0].Id);

            var back = library.Load(list[1].Id)!;
            Assert.Equal((page.Hash, ViewerCustomStampKind.Page, 300.0), (back.Hash, back.Kind, back.Width));
            Assert.Null(library.Load("../../escape"));
            Assert.Null(library.Load(new string('a', 64)));

            // A file changed on the device is not trusted: it is not given back.
            File.WriteAllBytes(Path.Combine(folder, picture.Hash + ".pdf"), Picture(4, 4));
            Assert.Null(library.Load(picture.Hash));
            // A damaged index is read as empty, not as an error.
            File.WriteAllText(Path.Combine(folder, "stamps.json"), "{ not json");
            Assert.Empty(library.List());

            Assert.Null(library.Save(page, "Letterhead"));
            Assert.True(library.Delete(page.Hash));
            Assert.Empty(library.List());
            Assert.False(File.Exists(Path.Combine(folder, page.Hash + ".pdf")));
            Assert.False(library.Delete(page.Hash));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheStampLibrary_HasACountCap()
    {
        string folder = Path.Combine(Path.GetTempPath(), "custom-stamps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new ViewerStampLibrary(folder);
            for (int i = 0; i < ViewerStampLibrary.MaxStamps; i++)
                Assert.Null(library.Save(ViewerCustomStamp.FromImage(Picture(10 + i, 10)), $"Stamp {i}"));
            Assert.Contains("at most", library.Save(ViewerCustomStamp.FromImage(Picture(9, 9)), "One more"));
            Assert.Equal(ViewerStampLibrary.MaxStamps, library.List().Count);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void APdfsPages_AreCounted_ForChoosingOne()
    {
        Assert.Equal(3, ViewerCustomStamp.PageCount(SourcePdf(pages: 3)));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.PageCount(SourcePdf(encryption: new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner"))));
        Assert.Throws<ViewerAnnotationException>(() => ViewerCustomStamp.PageCount(Picture(4, 4)));
    }
}
