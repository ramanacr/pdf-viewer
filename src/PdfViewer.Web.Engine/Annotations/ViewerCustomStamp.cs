using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using SkiaSharp;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>What a custom stamp was made from: a picture (PNG or JPEG) or a page of a PDF.</summary>
public enum ViewerCustomStampKind { Image, Page }

/// <summary>
/// What a custom stamp draws: a picture or a page of a PDF, held as a one-page unencrypted PDF
/// made here that holds that and nothing else (the engine's <c>PdfStampForms.Isolate</c>: a page
/// whose content draws the picture or the page's content and resources as a form XObject; no
/// annotations, page actions, piece information, metadata or document information). The stamp's
/// appearance draws that page (<see cref="PdfStampAppearance.SourcePdf"/>, as the desktop app
/// writes its custom stamps). A page stays vector content; a picture is embedded once, a plain
/// JPEG as it is (without its metadata). Everything given is untrusted: sizes are capped, a
/// picture is decoded before it is taken (a damaged one is refused; a large one is decoded at
/// most <see cref="DecodePixels"/>), an encrypted PDF is refused, and nothing copied carries an
/// action, a script or a link to another file. Immutable; several stamps may share one.
/// </summary>
public sealed class ViewerCustomStamp
{
    /// <summary>The largest picture file taken.</summary>
    public const int MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>The most pixels a picture may have (from its header).</summary>
    public const long MaxImagePixels = 12_000_000;

    /// <summary>The longest side of a picture, in pixels.</summary>
    public const int MaxImageSide = 10_000;

    /// <summary>The most pixels decoded: a larger picture is decoded smaller (a phone's memory), keeping its shape.</summary>
    public const long DecodePixels = 4_000_000;

    /// <summary>The largest PDF a page is taken from.</summary>
    public const int MaxPdfBytes = 20 * 1024 * 1024;

    /// <summary>The largest stamp kept: the one-page PDF that draws it.</summary>
    public const int MaxStampBytes = 12 * 1024 * 1024;

    /// <summary>The /Name of a custom stamp, as the desktop app writes it.</summary>
    public const string StampName = "Custom";

    /// <summary>The usual size of a placed custom stamp: its longer side, in points (2 inches, as the desktop app).</summary>
    public const double PlacedSize = 144;

    /// <summary>The longer side of a picture's page in the stamp PDF, in points: only its shape matters.</summary>
    private const double PictureSide = 1000;

    private ViewerCustomStamp(ViewerCustomStampKind kind, byte[] pdf, double width, double height)
    {
        Kind = kind;
        Pdf = pdf;
        Width = width;
        Height = height;
        Hash = Convert.ToHexStringLower(SHA256.HashData(pdf));
    }

    public ViewerCustomStampKind Kind { get; }

    /// <summary>A one-page unencrypted PDF made here: its page is the stamp. Not to be changed.</summary>
    public byte[] Pdf { get; }

    /// <summary>The stamp's size upright (the page as shown, or the picture's shape), in points.</summary>
    public double Width { get; }
    public double Height { get; }

    /// <summary>The SHA-256 of <see cref="Pdf"/> as lower-case hex: the same stamp has the same hash.</summary>
    public string Hash { get; }

    /// <summary>Width over height.</summary>
    public double Aspect => Width / Height;

    /// <summary>The size a stamp is placed at with a click: <see cref="PlacedSize"/> across its longer side, its shape kept.</summary>
    public (double Width, double Height) DefaultSize
    {
        get
        {
            double k = PlacedSize / Math.Max(Width, Height);
            return (Width * k, Height * k);
        }
    }

    /// <summary>What the bytes look like: a picture (PNG or JPEG), a PDF, or null for neither.</summary>
    public static ViewerCustomStampKind? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return ViewerCustomStampKind.Image;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ViewerCustomStampKind.Image;
        // A PDF's header may follow some bytes of junk (ISO 32000-2 7.5.2; readers look in the first 1024).
        var head = bytes[..Math.Min(bytes.Length, 1024)];
        return head.IndexOf("%PDF-"u8) >= 0 ? ViewerCustomStampKind.Page : null;
    }

    // ------------------------------------------------------------------ a picture

    /// <summary>
    /// A stamp from a PNG or JPEG picture file. The picture is decoded (a damaged one is refused;
    /// one larger than <see cref="DecodePixels"/> is decoded smaller), turned upright by its EXIF
    /// orientation, and embedded losslessly with its transparency; a plain upright JPEG is embedded
    /// as it is, without its metadata (EXIF, comments). Refused when it is not a PNG or JPEG, too
    /// large, cannot be decoded, or the device has not the memory to decode it.
    /// </summary>
    public static ViewerCustomStamp FromImage(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length == 0) throw new ViewerAnnotationException("The picture is empty.");
        if (file.Length > MaxImageBytes) throw new ViewerAnnotationException($"The picture is larger than {MaxImageBytes / (1024 * 1024)} MB.");
        if (Sniff(file) != ViewerCustomStampKind.Image) throw new ViewerAnnotationException("Choose a PNG or JPEG picture.");
        byte[] picturePdf;
        try
        {
            var image = Decode(file);
            picturePdf = PicturePage(image);
        }
        catch (OutOfMemoryException)
        {
            throw new ViewerAnnotationException("This picture is too large to make a stamp on this device. Choose a smaller one.");
        }
        catch (Exception ex) when (ex is not ViewerAnnotationException and not OperationCanceledException)
        {
            // The exception's type only: nothing of the picture is written anywhere.
            throw new ViewerAnnotationException($"The picture cannot be read ({ex.GetType().Name}).");
        }
        using var doc = Open(picturePdf, "The picture");
        return Load(ViewerCustomStampKind.Image, PdfStampForms.Isolate(doc, 1));
    }

    /// <summary>
    /// A PDF whose one page shows the picture upright, its shape from its pixels and resolution
    /// (<see cref="PictureSide"/> across the longer side, so a thin picture keeps its shape).
    /// </summary>
    private static byte[] PicturePage(PdfCreateImage image)
    {
        double dx = image.DpiX is > 1 and < 100_000 ? image.DpiX : 96, dy = image.DpiY is > 1 and < 100_000 ? image.DpiY : dx;
        double w = image.Width / dx, h = image.Height / dy;
        if (image.Orientation is >= 5 and <= 8) (w, h) = (h, w);
        double k = PictureSide / Math.Max(w, h);
        w *= k;
        h *= k;
        using var ms = new MemoryStream();
        using (var writer = new PdfDocumentWriter(ms))
        {
            int xobject = writer.AddImage(image);
            var (a, b, c, d, e, f) = new PdfImagePlacement(w, h, 0, 0, w, h, false).Matrix(image.Orientation);
            string content = FormattableString.Invariant($"q {a:0.######} {b:0.######} {c:0.######} {d:0.######} {e:0.######} {f:0.######} cm /Im0 Do Q\n");
            var resources = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Im0"] = new PdfIndirectRef(xobject) }),
            });
            writer.AddPage(w, h, Encoding.ASCII.GetBytes(content), resources);
            writer.Finish();
        }
        return ms.ToArray();
    }

    /// <summary>The picture as samples (or an upright JPEG to carry as it is), after checking its size and decoding it.</summary>
    private static PdfCreateImage Decode(byte[] file)
    {
        using var data = SKData.CreateCopy(file);
        using var codec = SKCodec.Create(data) ?? throw new ViewerAnnotationException("The picture cannot be read: it is not a PNG or JPEG that can be decoded.");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            throw new ViewerAnnotationException("Choose a PNG or JPEG picture.");
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new ViewerAnnotationException("The picture has no pixels.");
        if (w > MaxImageSide || h > MaxImageSide || (long)w * h > MaxImagePixels)
            throw new ViewerAnnotationException($"The picture is {w} x {h} pixels; a stamp can have at most {MaxImagePixels / 1_000_000} million pixels and {MaxImageSide} pixels a side.");
        int orientation = (int)codec.EncodedOrigin is >= 1 and <= 8 ? (int)codec.EncodedOrigin : 1;
        double scale = (long)w * h > DecodePixels ? Math.Sqrt((double)DecodePixels / ((long)w * h)) : 1;

        // A JPEG's DCT scaling decodes it smaller at once; a PNG is decoded whole, then made smaller.
        var size = scale < 1 ? codec.GetScaledDimensions((float)scale) : new SKSizeI(w, h);
        if (size.Width <= 0 || size.Height <= 0) size = new SKSizeI(w, h);
        using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        // Decoded even for a JPEG carried as it is: a damaged file is refused here, not drawn broken.
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new ViewerAnnotationException("The picture cannot be read: it is damaged or incomplete.");

        var header = codec.EncodedFormat == SKEncodedImageFormat.Jpeg ? JpegHeader.TryRead(file) : null;
        if (header is { CanPassThrough: true } && header.Width == w && header.Height == h && orientation == 1)
        {
            // Upright: carried as it is, less its metadata (lossless). A turned JPEG is decoded below and turned by its matrix.
            return new PdfCreateImage
            {
                Width = w, Height = h, Kind = PdfImageData.Jpeg, Data = JpegClean.Strip(file), Unchanged = true,
                Color = header.Components switch { 1 => PdfImageColor.Gray, 4 => PdfImageColor.Cmyk, _ => PdfImageColor.Rgb },
                InvertedCmyk = header.Components == 4 && header.Adobe,
                IccProfile = header.IccProfile, DpiX = header.DpiX, DpiY = header.DpiY,
            };
        }

        SKBitmap pixels = bitmap;
        SKBitmap? smaller = null;
        try
        {
            if ((long)size.Width * size.Height > DecodePixels)
            {
                int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
                smaller = bitmap.Resize(new SKImageInfo(sw, sh, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.Mitchell))
                    ?? throw new ViewerAnnotationException("The picture cannot be made smaller.");
                pixels = smaller;
            }
            int pw = pixels.Width, ph = pixels.Height;
            var span = pixels.GetPixelSpan();
            var rgb = new byte[(long)pw * ph * 3];
            var alpha = new byte[(long)pw * ph];
            bool opaque = true;
            int stride = pixels.RowBytes;
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int s = y * stride + x * 4;
                    long i = (long)y * pw + x;
                    rgb[i * 3] = span[s];
                    rgb[i * 3 + 1] = span[s + 1];
                    rgb[i * 3 + 2] = span[s + 2];
                    alpha[i] = span[s + 3];
                    opaque &= span[s + 3] == 255;
                }
            }
            // The resolution is per pixel of the picture as it was: a picture decoded smaller keeps its shape and its size.
            double k = (double)pw / w;
            return new PdfCreateImage
            {
                Width = pw, Height = ph, Kind = PdfImageData.Samples, Color = PdfImageColor.Rgb, Data = rgb,
                // Typed: a bare null would become an empty mask (through byte[]), not none.
                Alpha = opaque ? (ReadOnlyMemory<byte>?)null : alpha, Orientation = orientation,
                DpiX = header?.DpiX > 0 ? header.DpiX * k : 0, DpiY = header?.DpiY > 0 ? header.DpiY * (double)ph / h : 0,
            };
        }
        finally
        {
            smaller?.Dispose();
        }
    }

    // ------------------------------------------------------------------ a page of a PDF

    /// <summary>
    /// A stamp from page <paramref name="page"/> (from 1) of a PDF file: a new one-page PDF that
    /// draws that page's content and resources as vector content, and holds nothing else of the
    /// file. Refused when the file is too large, is not a PDF that can be read, is encrypted, or
    /// has no such page.
    /// </summary>
    public static ViewerCustomStamp FromPdf(byte[] pdf, int page)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length == 0) throw new ViewerAnnotationException("The PDF is empty.");
        if (pdf.Length > MaxPdfBytes) throw new ViewerAnnotationException($"The PDF is larger than {MaxPdfBytes / (1024 * 1024)} MB.");
        byte[] one;
        using (var doc = Open(pdf, "The PDF"))
        {
            if (doc.IsEncrypted) throw new ViewerAnnotationException("The PDF is encrypted. Use an unencrypted copy.");
            if (page < 1 || page > doc.PageCount)
                throw new ViewerAnnotationException(doc.PageCount == 1 ? "The PDF has 1 page." : $"The PDF has {doc.PageCount} pages; choose a page from 1 to {doc.PageCount}.");
            try
            {
                one = PdfStampForms.Isolate(doc, page);
            }
            catch (OutOfMemoryException)
            {
                throw new ViewerAnnotationException("That page is too large to make a stamp on this device.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ViewerAnnotationException($"That page cannot be made into a stamp ({ex.GetType().Name}).");
            }
        }
        return Load(ViewerCustomStampKind.Page, one);
    }

    /// <summary>
    /// The number of pages of a PDF a stamp may be made from, for choosing the page; refused as
    /// <see cref="FromPdf"/> refuses (too large, not a PDF that can be read, encrypted).
    /// </summary>
    public static int PageCount(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length == 0) throw new ViewerAnnotationException("The PDF is empty.");
        if (pdf.Length > MaxPdfBytes) throw new ViewerAnnotationException($"The PDF is larger than {MaxPdfBytes / (1024 * 1024)} MB.");
        using var doc = Open(pdf, "The PDF");
        if (doc.IsEncrypted) throw new ViewerAnnotationException("The PDF is encrypted. Use an unencrypted copy.");
        return doc.PageCount;
    }

    /// <summary>
    /// A stamp kept earlier (<see cref="Pdf"/> of a stamp, read back from the reader's stamp library
    /// or kept edits): checked as a new one is, and refused unless it is a stamp PDF as this viewer
    /// makes it (one page that draws one form, nothing else).
    /// </summary>
    public static ViewerCustomStamp FromKept(ViewerCustomStampKind kind, byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!Enum.IsDefined(kind)) throw new ViewerAnnotationException("That is not a kind of custom stamp.");
        return Load(kind, pdf);
    }

    private static readonly HashSet<string> PageKeys = new(StringComparer.Ordinal) { "Type", "Parent", "MediaBox", "Resources", "Contents" };

    /// <summary>Checks a one-page PDF made or kept for a stamp (made here: nothing but the page), and reads its size.</summary>
    private static ViewerCustomStamp Load(ViewerCustomStampKind kind, byte[] one)
    {
        if (one.Length == 0 || one.Length > MaxStampBytes)
            throw new ViewerAnnotationException($"The stamp would be larger than {MaxStampBytes / (1024 * 1024)} MB; choose a smaller picture or a simpler page.");
        using var doc = Open(one, "The stamp");
        if (doc.IsEncrypted || doc.PageCount != 1) throw new ViewerAnnotationException("The stamp is not a single unencrypted page.");
        var trailer = doc.XrefTable.Trailer;
        var catalog = doc.Resolver.Resolve(trailer?["Root"]) as PdfDictionary;
        var node = doc.PageTree.Pages[0];
        if (trailer == null || trailer.Entries.ContainsKey("Info") || catalog == null || catalog.Entries.Keys.Any(k => k is not ("Type" or "Pages"))
            || node.Dictionary.Entries.Keys.Any(k => !PageKeys.Contains(k)) || node.RotationDegrees != 0
            // Its resources: one form, Fm0, which draws the picture or the page.
            || doc.Resolver.Resolve(node.Dictionary["Resources"]) is not PdfDictionary resources || resources.Entries.Count != 1
            || doc.Resolver.Resolve(resources["XObject"]) is not PdfDictionary xobjects || xobjects.Entries.Count != 1
            || doc.Resolver.Resolve(xobjects["Fm0"]) is not PdfStream form || doc.Resolver.Resolve(form.Dictionary["Subtype"]) is not PdfName { Value: "Form" })
            throw new ViewerAnnotationException("This is not a stamp made by this viewer.");
        var media = node.MediaBox;
        double w = media.Width, h = media.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w < 0.01 || h < 0.01 || w > 14_400 || h > 14_400)
            throw new ViewerAnnotationException("The page has no size that can be stamped.");
        return new ViewerCustomStamp(kind, one, w, h);
    }

    private static PdfVectorDocument Open(byte[] bytes, string what)
    {
        try
        {
            return PdfVectorDocument.Open(new PdfMemoryByteSource(bytes), "Stamp.pdf");
        }
        catch (Exception ex) when (ex is PdfEngine.Vector.Diagnostics.PdfEncryptedDocumentException)
        {
            throw new ViewerAnnotationException($"{what} is encrypted. Use an unencrypted copy.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new ViewerAnnotationException($"{what} is not a PDF that can be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>The largest box of this stamp's shape inside a <paramref name="width"/> by <paramref name="height"/> box.</summary>
    public (double Width, double Height) FitInside(double width, double height)
    {
        double k = Math.Min(width / Width, height / Height);
        return (Width * k, Height * k);
    }
}

/// <summary>
/// A JPEG file without its metadata, losslessly: application segments (EXIF, XMP, JFIF and the
/// like) and comments are left out, except the ICC profile (APP2) and the Adobe marker (APP14)
/// that the colours need, and nothing after the end of the image is kept. The coded data is copied as it is.
/// </summary>
internal static class JpegClean
{
    public static byte[] Strip(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        using var output = new MemoryStream(jpeg.Length);
        output.Write([0xFF, 0xD8]);
        int pos = 2;
        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return jpeg; // not where a marker should be: left as it is (it was decoded)
            int marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                return output.ToArray();
            }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                output.Write(jpeg, pos, 2);
                pos += 2;
                continue;
            }
            if (pos + 4 > jpeg.Length) return jpeg;
            int length = jpeg[pos + 2] << 8 | jpeg[pos + 3];
            if (length < 2 || pos + 2 + length > jpeg.Length) return jpeg;
            var segment = jpeg.AsSpan(pos + 4, length - 2);
            bool keep = marker switch
            {
                0xE2 => segment.StartsWith("ICC_PROFILE\0"u8),
                0xEE => segment.StartsWith("Adobe"u8),
                >= 0xE0 and <= 0xEF or 0xFE => false,
                _ => true,
            };
            if (keep) output.Write(jpeg, pos, 2 + length);
            pos += 2 + length;
            if (marker == 0xDA)
            {
                // The coded data of the scan, to the next marker (0xFF 0x00 is a byte of data, 0xFF 0xD0-D7 a restart).
                int start = pos;
                while (pos + 1 < jpeg.Length && !(jpeg[pos] == 0xFF && jpeg[pos + 1] is not 0x00 and not (>= 0xD0 and <= 0xD7) and not 0xFF)) pos++;
                output.Write(jpeg, start, pos - start);
            }
        }
        return jpeg;
    }
}
