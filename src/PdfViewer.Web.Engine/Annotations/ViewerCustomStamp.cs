using System.Security.Cryptography;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Services;
using SkiaSharp;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>What a custom stamp was made from: a picture (PNG or JPEG) or a page of a PDF.</summary>
public enum ViewerCustomStampKind { Image, Page }

/// <summary>
/// What a custom stamp draws: a picture or a page of a PDF, held as a one-page unencrypted PDF
/// whose page the stamp's appearance draws as a form XObject (the engine's
/// <see cref="PdfEngine.Vector.Annotations.PdfStampAppearance.SourcePdf"/>, as the desktop app
/// writes its custom stamps). A page stays vector content; a picture is embedded once, a JPEG as
/// it is. Everything given is untrusted: sizes are capped, a picture is decoded in full before it
/// is taken (a damaged one is refused), an encrypted PDF is refused (a page of the document already
/// open is taken through <see cref="FromDocumentAsync"/>), and the appearance copies no action,
/// script or link to another file. Immutable; several stamps may share one.
/// </summary>
public sealed class ViewerCustomStamp
{
    /// <summary>The largest picture file taken.</summary>
    public const int MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>The most pixels a picture may have (it is decoded in full, four bytes a pixel).</summary>
    public const long MaxImagePixels = 12_000_000;

    /// <summary>The longest side of a picture, in pixels.</summary>
    public const int MaxImageSide = 10_000;

    /// <summary>The largest PDF a page is taken from.</summary>
    public const int MaxPdfBytes = 20 * 1024 * 1024;

    /// <summary>The largest stamp kept: the one-page PDF that draws it.</summary>
    public const int MaxStampBytes = 12 * 1024 * 1024;

    /// <summary>The /Name of a custom stamp, as the desktop app writes it.</summary>
    public const string StampName = "Custom";

    /// <summary>The usual size of a placed custom stamp: its longer side, in points (2 inches, as the desktop app).</summary>
    public const double PlacedSize = 144;

    private ViewerCustomStamp(ViewerCustomStampKind kind, byte[] pdf, double width, double height)
    {
        Kind = kind;
        Pdf = pdf;
        Width = width;
        Height = height;
        Hash = Convert.ToHexStringLower(SHA256.HashData(pdf));
    }

    public ViewerCustomStampKind Kind { get; }

    /// <summary>A one-page unencrypted PDF: its page is the stamp. Not to be changed.</summary>
    public byte[] Pdf { get; }

    /// <summary>The stamp's size upright (the page as shown, or the picture as seen), in points.</summary>
    public double Width { get; }
    public double Height { get; }

    /// <summary>The SHA-256 of <see cref="Pdf"/> as lower-case hex: the same stamp has the same hash.</summary>
    public string Hash { get; }

    /// <summary>Width over height.</summary>
    public double Aspect => Width / Height;

    /// <summary>The size a stamp is placed at with a click: <see cref="PlacedSize"/> across its longer side.</summary>
    public (double Width, double Height) DefaultSize
    {
        get
        {
            double k = PlacedSize / Math.Max(Width, Height);
            return (Math.Max(4, Width * k), Math.Max(4, Height * k));
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
    /// A stamp from a PNG or JPEG picture file. The picture is decoded in full (a damaged one is
    /// refused), turned upright by its EXIF orientation, and embedded losslessly with its
    /// transparency; a plain JPEG is embedded as it is. Refused when it is not a PNG or JPEG, too
    /// large, or cannot be decoded.
    /// </summary>
    public static ViewerCustomStamp FromImage(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length == 0) throw new ViewerAnnotationException("The picture is empty.");
        if (file.Length > MaxImageBytes) throw new ViewerAnnotationException($"The picture is larger than {MaxImageBytes / (1024 * 1024)} MB.");
        if (Sniff(file) != ViewerCustomStampKind.Image) throw new ViewerAnnotationException("Choose a PNG or JPEG picture.");
        PdfCreateImage image;
        try
        {
            image = Decode(file);
        }
        catch (Exception ex) when (ex is not ViewerAnnotationException and not OutOfMemoryException and not OperationCanceledException)
        {
            // The exception's type only: nothing of the picture is written anywhere.
            throw new ViewerAnnotationException($"The picture cannot be read ({ex.GetType().Name}).");
        }
        byte[] pdf;
        using (var ms = new MemoryStream())
        {
            using (var writer = new PdfDocumentWriter(ms))
            {
                writer.AddImagePage(image, new PdfImagePageOptions { Size = PdfImagePageSize.FitImage });
                writer.Finish(new PdfCreateInfo { Producer = "PdfViewer custom stamp" });
            }
            pdf = ms.ToArray();
        }
        return Load(ViewerCustomStampKind.Image, pdf);
    }

    /// <summary>The picture as samples (or a JPEG to carry as it is), after checking its size and decoding it in full.</summary>
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
        // Decoded in full even for a JPEG carried as it is: a damaged file is refused here, not drawn broken.
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var result = codec.GetPixels(info, out byte[] pixels);
        if (result != SKCodecResult.Success || pixels == null || pixels.Length < (long)w * h * 4)
            throw new ViewerAnnotationException("The picture cannot be read: it is damaged or incomplete.");

        if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg && JpegHeader.TryRead(file) is { CanPassThrough: true } header
            && header.Width == w && header.Height == h)
        {
            return new PdfCreateImage
            {
                Width = w, Height = h, Kind = PdfImageData.Jpeg, Data = file, Unchanged = true,
                Color = header.Components switch { 1 => PdfImageColor.Gray, 4 => PdfImageColor.Cmyk, _ => PdfImageColor.Rgb },
                InvertedCmyk = header.Components == 4 && header.Adobe,
                IccProfile = header.IccProfile, Orientation = header.Orientation, DpiX = header.DpiX, DpiY = header.DpiY,
            };
        }
        var rgb = new byte[(long)w * h * 3];
        var alpha = new byte[(long)w * h];
        bool opaque = true;
        for (long i = 0, n = (long)w * h; i < n; i++)
        {
            rgb[i * 3] = pixels[i * 4];
            rgb[i * 3 + 1] = pixels[i * 4 + 1];
            rgb[i * 3 + 2] = pixels[i * 4 + 2];
            byte a = pixels[i * 4 + 3];
            alpha[i] = a;
            opaque &= a == 255;
        }
        return new PdfCreateImage
        {
            Width = w, Height = h, Kind = PdfImageData.Samples, Color = PdfImageColor.Rgb, Data = rgb,
            // Typed: a bare null would become an empty mask (through byte[]), not none.
            Alpha = opaque ? (ReadOnlyMemory<byte>?)null : alpha, Orientation = orientation,
        };
    }

    // ------------------------------------------------------------------ a page of a PDF

    /// <summary>
    /// A stamp from page <paramref name="page"/> (from 1) of a PDF file: the page alone (its
    /// content and resources, no outline or form) is kept and drawn as vector content. Refused
    /// when the file is too large, is not a PDF that can be read, is encrypted (open it first and
    /// use <see cref="FromDocumentAsync"/>), or has no such page.
    /// </summary>
    public static ViewerCustomStamp FromPdf(byte[] pdf, int page)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.Length == 0) throw new ViewerAnnotationException("The PDF is empty.");
        if (pdf.Length > MaxPdfBytes) throw new ViewerAnnotationException($"The PDF is larger than {MaxPdfBytes / (1024 * 1024)} MB.");
        byte[] one;
        using (var doc = Open(pdf, "The PDF"))
        {
            if (doc.IsEncrypted) throw new ViewerAnnotationException("The PDF is encrypted. Open it in the viewer first, or use an unencrypted copy.");
            if (page < 1 || page > doc.PageCount)
                throw new ViewerAnnotationException(doc.PageCount == 1 ? "The PDF has 1 page." : $"The PDF has {doc.PageCount} pages; choose a page from 1 to {doc.PageCount}.");
            try
            {
                one = PdfPageAssembler.Extract(doc, [page], new PdfAssemblyOptions { KeepOutlines = false, KeepForms = false });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
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
        if (doc.IsEncrypted) throw new ViewerAnnotationException("The PDF is encrypted. Open it in the viewer first, or use an unencrypted copy.");
        return doc.PageCount;
    }

    /// <summary>
    /// A stamp from page <paramref name="page"/> (from 1) of a document open in the viewer: an
    /// encrypted one too, as it is open already (the stamp keeps the page unencrypted).
    /// </summary>
    public static async Task<ViewerCustomStamp> FromDocumentAsync(ViewerDocument document, int page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (page < 1 || page > document.PageCount)
            throw new ViewerAnnotationException($"The document has {document.PageCount} pages; choose a page from 1 to {document.PageCount}.");
        byte[] one;
        try
        {
            one = await document.ExtractPageAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException and not ObjectDisposedException)
        {
            throw new ViewerAnnotationException($"That page cannot be made into a stamp ({ex.GetType().Name}).");
        }
        return Load(ViewerCustomStampKind.Page, one);
    }

    /// <summary>
    /// A stamp kept earlier (<see cref="Pdf"/> of a stamp, read back from the reader's stamp library
    /// or kept edits): checked as a new one is.
    /// </summary>
    public static ViewerCustomStamp FromKept(ViewerCustomStampKind kind, byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!Enum.IsDefined(kind)) throw new ViewerAnnotationException("That is not a kind of custom stamp.");
        return Load(kind, pdf);
    }

    /// <summary>Checks a one-page PDF made or kept for a stamp, and reads its size as shown.</summary>
    private static ViewerCustomStamp Load(ViewerCustomStampKind kind, byte[] one)
    {
        if (one.Length == 0 || one.Length > MaxStampBytes)
            throw new ViewerAnnotationException($"The stamp would be larger than {MaxStampBytes / (1024 * 1024)} MB; choose a smaller picture or a simpler page.");
        using var doc = Open(one, "The stamp");
        if (doc.IsEncrypted || doc.PageCount != 1) throw new ViewerAnnotationException("The stamp is not a single unencrypted page.");
        var node = doc.PageTree.Pages[0];
        var crop = node.CropBox;
        bool turned = node.RotationDegrees is 90 or 270;
        double w = turned ? crop.Height : crop.Width, h = turned ? crop.Width : crop.Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w < 1 || h < 1 || w > 14_400 || h > 14_400)
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
            throw new ViewerAnnotationException($"{what} is encrypted. Open it in the viewer first, or use an unencrypted copy.");
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
