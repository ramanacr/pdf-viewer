using System.Runtime.InteropServices;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PdfEngine.Documents;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Services;
using SkiaSharp;
using Uri = Android.Net.Uri;

namespace PdfViewer.Android;

/// <summary>
/// Create: a PDF from pictures (a page each), from a plain text file, or from PDFs and pictures
/// combined, with a bookmark for each file. None of them needs an open document. Files come from
/// the system picker (read access to the files picked, no permission); the new PDF goes through
/// the save dialog and can be opened at once.
/// <para>
/// A JPEG is carried into the PDF as it is (no quality lost), less its metadata (EXIF with place
/// and camera, XMP, comments), and turned upright by its EXIF orientation through the page's
/// image matrix. PNG, GIF and BMP pictures are decoded and stored losslessly; other photos (WebP,
/// HEIC where the device decodes it) are stored as JPEG at quality 90.
/// </para>
/// </summary>
public sealed partial class MainActivity
{
    /// <summary>The most one picture file may have.</summary>
    private const long MaxPictureFileBytes = 64L * 1024 * 1024;

    /// <summary>The most pixels a picture is decoded at; a larger one is decoded smaller (a JPEG carried as it is is not decoded).</summary>
    private const long MaxPicturePixels = 50_000_000;

    /// <summary>The most a combined set of files may have in all (they are open together while the result is made).</summary>
    private const long MaxCombineBytes = 256L * 1024 * 1024;

    private sealed record PickedFile(Uri Uri, string Name, bool IsPdf);

    /// <summary>The paper the reader most likely uses: Letter in the United States and Canada, A4 elsewhere.</summary>
    private static bool LetterPaper => Java.Util.Locale.Default.Country is "US" or "CA" or "MX" or "PH";

    // ------------------------------------------------------------------ PDF from pictures

    /// <summary>
    /// PDF from pictures: pick pictures (several at once, and more after), put them in order, choose
    /// the page size (A4 or Letter with the page turned to the picture, or the picture's own size),
    /// then save. Each picture is a page.
    /// </summary>
    private void CreateFromImages()
    {
        PickFiles(["image/*"], true, uris =>
            ArrangeFiles("PDF from pictures", ToPicked(uris), ["image/*"], "Next", files =>
            {
                string[] sizes = [LetterPaper ? "Letter" : "A4", LetterPaper ? "A4" : "Letter", "Same size as each picture"];
                int selected = 0;
                new AlertDialog.Builder(this)
                    .SetTitle("Page size")!
                    .SetSingleChoiceItems(sizes, selected, (_, e) => selected = e.Which)!
                    .SetPositiveButton("Make PDF", (_, _) =>
                    {
                        var page = sizes[selected] switch
                        {
                            "A4" => Paper(PdfImagePageOptions.A4),
                            "Letter" => Paper(PdfImagePageOptions.Letter),
                            _ => new PdfImagePageOptions { Size = PdfImagePageSize.FitImage },
                        };
                        WritePicturesPdf(files, page);
                    })!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show();
            }));
    }

    /// <summary>A paper size with half-inch margins, turned to landscape for a landscape picture.</summary>
    private static PdfImagePageOptions Paper((double Width, double Height) paper) => new()
    {
        Size = PdfImagePageSize.Paper,
        PaperWidth = paper.Width,
        PaperHeight = paper.Height,
        AutoOrientation = true,
    };

    private void WritePicturesPdf(IReadOnlyList<PickedFile> files, PdfImagePageOptions page)
    {
        string name = files.Count == 1 ? System.IO.Path.GetFileNameWithoutExtension(files[0].Name) : "Pictures";
        SaveNewFileWithProgress("application/pdf", name + ".pdf", "Making the PDF…", (ct, report) =>
        {
            using var output = new MemoryStream();
            using (var writer = new PdfDocumentWriter(output))
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    report($"Adding picture {i + 1} of {files.Count}…");
                    writer.AddImagePage(ReadPicture(files[i]), page);
                }
                writer.Finish(CreateInfo(name));
                return output.ToArray();
            }
        }, target => OfferOpen(target, files.Count == 1 ? "The PDF was made." : $"The PDF was made, with {files.Count} pages."));
    }

    private static PdfCreateInfo CreateInfo(string? title) => new() { Title = title, Creator = "PDF Viewer (Android)", Producer = "PDF Viewer Native" };

    // ------------------------------------------------------------------ PDF from a text file

    /// <summary>
    /// PDF from a text file: the engine's own layout (lines wrapped at the margins, a form feed
    /// starts a page), in a monospaced installed font embedded as a subset, on A4 or Letter as
    /// the device's region suggests. The text stays searchable and selectable.
    /// </summary>
    private void CreateFromTextFile()
    {
        PickFiles(["text/plain"], false, uris =>
        {
            var file = ToPicked(uris)[0];
            string name = System.IO.Path.GetFileNameWithoutExtension(file.Name) is { Length: > 0 } n ? n : "Text";
            int pages = 0;
            SaveNewFileWithProgress("application/pdf", name + ".pdf", "Laying out the text…", (ct, report) =>
            {
                byte[] raw = ReadUri(file.Uri, 32L * 1024 * 1024);
                string text = PdfTextDocument.Decode(raw);
                var paper = LetterPaper ? PdfImagePageOptions.Letter : PdfImagePageOptions.A4;
                using var output = new MemoryStream();
                var result = PdfTextDocument.Write(text, output, new PdfTextLayoutOptions { PageWidth = paper.Width, PageHeight = paper.Height },
                    CreateInfo(name), progress: new CallbackProgress<int>(p => report($"Laying out the text: page {p}…")), cancellationToken: ct);
                pages = result.Pages;
                return output.ToArray();
            }, target => OfferOpen(target, $"The PDF was made, with {pages} page(s)."));
        });
    }

    // ------------------------------------------------------------------ combine files

    /// <summary>
    /// Combine files: PDFs and pictures, in the order the reader sets, into one PDF. Each picture is
    /// a page (on A4 or Letter as the device's region suggests); each PDF brings all its pages, its
    /// bookmarks and form fields. The result has a bookmark for each file, named after it, holding
    /// that file's own bookmarks. A protected PDF asks for its password; one whose permissions do
    /// not allow taking its pages out is refused (the result would not be encrypted).
    /// </summary>
    private void CombineFiles()
    {
        string[] types = ["application/pdf", "image/*"];
        PickFiles(types, true, uris =>
            ArrangeFiles("Combine files", ToPicked(uris), types, "Combine", files => PrepareCombine(files)));
    }

    /// <summary>Reads the files and finds the protected PDFs (on a worker), then asks for their passwords one by one.</summary>
    private async void PrepareCombine(IReadOnlyList<PickedFile> files)
    {
        var read = await RunBusyAsync("Reading the files…", ct =>
        {
            var bytes = new byte[files.Count][];
            var locked = new List<int>();
            long total = 0;
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                bytes[i] = ReadUri(files[i].Uri, files[i].IsPdf ? MaxCombineBytes : MaxPictureFileBytes);
                total += bytes[i].LongLength;
                if (total > MaxCombineBytes)
                    throw new UserMessageException($"These files have more than {MaxCombineBytes / (1024 * 1024)} MB in all, the most Combine takes on a phone. Combine fewer at a time.");
                if (!files[i].IsPdf) continue;
                try
                {
                    using var doc = PdfVectorDocument.Open(new PdfMemoryByteSource(bytes[i]), files[i].Name, certificates: []);
                }
                catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
                {
                    locked.Add(i);
                }
            }
            return (Bytes: bytes, Locked: locked);
        });
        if (read.Bytes == null) return;
        var passwords = new string?[files.Count];
        void AskNext(int k)
        {
            if (k == read.Locked.Count)
            {
                WriteCombined(files, read.Bytes, passwords);
                return;
            }
            int i = read.Locked[k];
            AskLine("Password", $"\"{files[i].Name}\" is protected. Type its password to include it.", "", "Password",
                global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword, "Continue", password =>
                {
                    passwords[i] = password;
                    AskNext(k + 1);
                });
        }
        AskNext(0);
    }

    private void WriteCombined(IReadOnlyList<PickedFile> files, byte[][] bytes, string?[] passwords)
    {
        var page = Paper(LetterPaper ? PdfImagePageOptions.Letter : PdfImagePageOptions.A4);
        int pageCount = 0;
        SaveNewFileWithProgress("application/pdf", "Combined.pdf", "Combining…", (ct, report) =>
        {
            var docs = new List<PdfVectorDocument>();
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    report($"Reading file {i + 1} of {files.Count}…");
                    byte[] pdf = bytes[i];
                    if (!files[i].IsPdf)
                    {
                        using var output = new MemoryStream();
                        using (var writer = new PdfDocumentWriter(output))
                        {
                            writer.AddImagePage(ReadPicture(files[i], pdf), page);
                            writer.Finish(CreateInfo(null));
                        }
                        pdf = output.ToArray();
                    }
                    PdfVectorDocument doc;
                    try
                    {
                        doc = PdfVectorDocument.Open(new PdfMemoryByteSource(pdf), files[i].Name, password: passwords[i], certificates: []);
                    }
                    catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
                    {
                        throw new UserMessageException($"The password for \"{files[i].Name}\" is not right. Nothing was combined.");
                    }
                    docs.Add(doc);
                    if (doc.Security is { } s && !PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanAssemble)
                        throw new UserMessageException($"\"{files[i].Name}\" does not allow taking its pages into another document. Nothing was combined.");
                }
                ct.ThrowIfCancellationRequested();
                report("Combining…");
                byte[] combined = PdfPageAssembler.Merge(docs, new PdfAssemblyOptions
                {
                    SourceTitles = files.Select(f => (string?)System.IO.Path.GetFileNameWithoutExtension(f.Name)).ToList(),
                    Info = CreateInfo("Combined"),
                });
                pageCount = docs.Sum(d => d.PageCount);
                return combined;
            }
            finally
            {
                foreach (var d in docs) d.Dispose();
            }
        }, target => OfferOpen(target, $"{files.Count} files were combined into one PDF of {pageCount} page(s), with a bookmark for each file."));
    }

    // ------------------------------------------------------------------ choosing and ordering files

    private List<PickedFile> ToPicked(IReadOnlyList<Uri> uris) => uris.Select(u =>
    {
        string name = DisplayName(u) ?? "File";
        string? type = ContentResolver?.GetType(u);
        bool pdf = type == "application/pdf" || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        return new PickedFile(u, name, pdf);
    }).ToList();

    /// <summary>
    /// The files picked, in order, in a dialog: move one up or down, remove one, add more, then
    /// <paramref name="next"/> with the list. Buttons are 48 dp, each with what it does for TalkBack.
    /// </summary>
    private void ArrangeFiles(string title, List<PickedFile> files, string[] mimeTypes, string nextText, Action<IReadOnlyList<PickedFile>> next)
    {
        var list = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        list.SetPadding((int)Dp(16), (int)Dp(8), (int)Dp(8), (int)Dp(8));
        AlertDialog? dialog = null;

        void Fill()
        {
            list.RemoveAllViews();
            var hint = new TextView(this) { Text = "Pages follow this order. Move a file with ↑ and ↓." };
            hint.SetTextColor(TextColor);
            hint.SetPadding((int)Dp(8), 0, 0, (int)Dp(8));
            list.AddView(hint);
            for (int i = 0; i < files.Count; i++)
            {
                int index = i;
                var row = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
                row.SetGravity(GravityFlags.CenterVertical);
                var label = new TextView(this) { Text = $"{i + 1}. {files[i].Name}" };
                label.SetTextColor(TextColor);
                label.SetSingleLine(true);
                label.Ellipsize = global::Android.Text.TextUtils.TruncateAt.Middle;
                label.SetPadding((int)Dp(8), 0, 0, 0);
                row.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
                void Move(int from, int to)
                {
                    (files[from], files[to]) = (files[to], files[from]);
                    Fill();
                }
                var up = ToolButton("↑", $"Move {files[i].Name} up", (_, _) => { if (index > 0) Move(index, index - 1); });
                var down = ToolButton("↓", $"Move {files[i].Name} down", (_, _) => { if (index < files.Count - 1) Move(index, index + 1); });
                var remove = ToolButton("✕", $"Remove {files[i].Name}", (_, _) =>
                {
                    files.RemoveAt(index);
                    Fill();
                });
                up.Enabled = i > 0;
                up.Alpha = i > 0 ? 1f : 0.35f;
                down.Enabled = i < files.Count - 1;
                down.Alpha = i < files.Count - 1 ? 1f : 0.35f;
                foreach (var b in new[] { up, down, remove })
                {
                    b.SetTextColor(AccentColor);
                    row.AddView(b);
                }
                list.AddView(row);
            }
            if (dialog?.GetButton((int)DialogButtonType.Positive) is { } go) go.Enabled = files.Count > 0;
        }

        Fill();
        var scroll = new ScrollView(this);
        scroll.AddView(list);
        dialog = new AlertDialog.Builder(this)
            .SetTitle(title)!
            .SetView(scroll)!
            .SetPositiveButton(nextText, (_, _) => next(files.ToList()))!
            .SetNeutralButton("Add more…", (_, _) => { })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        dialog.Show();
        if (dialog.GetButton((int)DialogButtonType.Positive) is { } next0) next0.Enabled = files.Count > 0;
        // Set after Show, so the dialog stays open while more files are picked.
        dialog.GetButton((int)DialogButtonType.Neutral)!.Click += (_, _) =>
            PickFiles(mimeTypes, true, more =>
            {
                files.AddRange(ToPicked(more));
                Fill();
            });
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>Reads a picked picture file and makes it ready for a page (on a worker thread).</summary>
    private PdfCreateImage ReadPicture(PickedFile file, byte[]? bytes = null)
    {
        bytes ??= ReadUri(file.Uri, MaxPictureFileBytes);
        try
        {
            return PictureForPage(bytes);
        }
        catch (UserMessageException ex)
        {
            throw new UserMessageException($"\"{file.Name}\": {ex.Message}");
        }
    }

    /// <summary>
    /// A picture for a page: a JPEG carried as it is (metadata removed), other pictures decoded with
    /// Skia (or by Android for formats Skia does not read, such as HEIC), at most
    /// <see cref="MaxPicturePixels"/>, and stored losslessly or as JPEG.
    /// </summary>
    private static PdfCreateImage PictureForPage(byte[] file)
    {
        using var data = SKData.CreateCopy(file);
        using var codec = SKCodec.Create(data);
        if (codec == null) return DecodeWithAndroid(file);
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new UserMessageException("The picture has no pixels.");
        int orientation = (int)codec.EncodedOrigin is >= 1 and <= 8 ? (int)codec.EncodedOrigin : 1;

        if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg && JpegForPdf.Read(file) is { CanPassThrough: true } jpeg && jpeg.Width == w && jpeg.Height == h)
        {
            return new PdfCreateImage
            {
                Width = w, Height = h, Kind = PdfImageData.Jpeg, Data = jpeg.Stripped, Unchanged = true,
                Color = jpeg.Components switch { 1 => PdfImageColor.Gray, 4 => PdfImageColor.Cmyk, _ => PdfImageColor.Rgb },
                InvertedCmyk = jpeg.Components == 4 && jpeg.Adobe,
                DpiX = jpeg.DpiX, DpiY = jpeg.DpiY, Orientation = orientation,
            };
        }

        // Decoded at most MaxPicturePixels: a JPEG or WebP decodes smaller at once; another format too large is refused.
        var size = new SKSizeI(w, h);
        if ((long)w * h > MaxPicturePixels)
        {
            size = codec.GetScaledDimensions((float)Math.Sqrt((double)MaxPicturePixels / ((long)w * h)));
            if ((long)size.Width * size.Height > MaxPicturePixels || size.Width <= 0 || size.Height <= 0)
                throw new UserMessageException($"The picture has {w} × {h} pixels, more than the {MaxPicturePixels / 1_000_000} million this app decodes.");
        }
        using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new UserMessageException("The picture cannot be read: it is damaged.");
        bool lossless = codec.EncodedFormat is SKEncodedImageFormat.Png or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Bmp
            or SKEncodedImageFormat.Ico or SKEncodedImageFormat.Wbmp;
        return FromRgba(bitmap.GetPixelSpan(), bitmap.Width, bitmap.Height, bitmap.RowBytes, orientation, lossless, premultiplied: false);
    }

    /// <summary>A picture Skia cannot read (HEIC, AVIF on newer Android), decoded by Android with its EXIF orientation.</summary>
    private static PdfCreateImage DecodeWithAndroid(byte[] file)
    {
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(file, 0, file.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            throw new UserMessageException("This is not a picture this device can read.");
        int sample = 1;
        while ((long)(bounds.OutWidth / sample) * (bounds.OutHeight / sample) > MaxPicturePixels) sample *= 2;
        using var bitmap = BitmapFactory.DecodeByteArray(file, 0, file.Length, new BitmapFactory.Options { InSampleSize = sample, InPreferredConfig = Bitmap.Config.Argb8888 })
                           ?? throw new UserMessageException("The picture cannot be read: it is damaged.");
        int orientation = 1;
        try
        {
            using var stream = new MemoryStream(file, writable: false);
            var exif = new global::Android.Media.ExifInterface(stream);
            orientation = exif.GetAttributeInt(global::Android.Media.ExifInterface.TagOrientation, 1) is int o and >= 1 and <= 8 ? o : 1;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            // No readable EXIF: shown as stored.
        }
        int w = bitmap.Width, h = bitmap.Height;
        // GetPixels gives unpremultiplied 0xAARRGGBB ints. (A ByteBuffer wrapping a managed array would not
        // do: .NET for Android copies the array into Java once, so the pixels would never come back.)
        var argb = new int[w * h];
        bitmap.GetPixels(argb, 0, w, 0, 0, w, h);
        bitmap.Recycle();
        var pixels = new byte[w * h * 4];
        for (int i = 0, o = 0; i < argb.Length; i++, o += 4)
        {
            int c = argb[i];
            pixels[o] = (byte)(c >> 16);
            pixels[o + 1] = (byte)(c >> 8);
            pixels[o + 2] = (byte)c;
            pixels[o + 3] = (byte)(c >>> 24);
        }
        return FromRgba(pixels, w, h, w * 4, orientation, lossless: false, premultiplied: false);
    }

    /// <summary>
    /// RGBA pixels as an image: losslessly (with its transparency) for drawings and pictures with
    /// transparency, else as a JPEG at quality 90 (photos).
    /// </summary>
    private static PdfCreateImage FromRgba(ReadOnlySpan<byte> rgba, int w, int h, int stride, int orientation, bool lossless, bool premultiplied)
    {
        bool opaque = true;
        for (int y = 0; y < h && opaque; y++)
        {
            var row = rgba.Slice(y * stride, w * 4);
            for (int x = 3; x < row.Length; x += 4)
                if (row[x] != 255) { opaque = false; break; }
        }
        if (!lossless && opaque)
        {
            var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
            var copy = rgba.ToArray();
            var handle = GCHandle.Alloc(copy, GCHandleType.Pinned);
            try
            {
                using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), stride);
                using var encoded = pixmap.Encode(SKEncodedImageFormat.Jpeg, 90)
                                    ?? throw new UserMessageException("The picture could not be compressed.");
                return new PdfCreateImage
                {
                    Width = w, Height = h, Kind = PdfImageData.Jpeg, Color = PdfImageColor.Rgb, Data = encoded.ToArray(), Orientation = orientation,
                };
            }
            finally
            {
                handle.Free();
            }
        }
        var rgb = new byte[(long)w * h * 3];
        var alpha = opaque ? null : new byte[(long)w * h];
        for (int y = 0; y < h; y++)
        {
            var row = rgba.Slice(y * stride, w * 4);
            long o = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                byte a = row[x * 4 + 3];
                byte r = row[x * 4], g = row[x * 4 + 1], b = row[x * 4 + 2];
                if (premultiplied && a is > 0 and < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }
                rgb[(o + x) * 3] = r;
                rgb[(o + x) * 3 + 1] = g;
                rgb[(o + x) * 3 + 2] = b;
                if (alpha != null) alpha[o + x] = a;
            }
        }
        return new PdfCreateImage
        {
            Width = w, Height = h, Kind = PdfImageData.Samples, Color = PdfImageColor.Rgb, Data = rgb, Orientation = orientation,
            // Typed: a bare null would become an empty mask (through byte[]), not none.
            Alpha = alpha == null ? (ReadOnlyMemory<byte>?)null : alpha,
        };
    }

    /// <summary>
    /// What a PDF needs from a JPEG file to carry it as it is (DCTDecode): its frame (size,
    /// components, precision, kind of coding), the Adobe marker (inverted CMYK) and the JFIF
    /// resolution; and the file without its metadata segments (EXIF, XMP, IPTC, comments), which a
    /// PDF does not use and which can hold the place a photo was taken.
    /// </summary>
    private sealed record JpegForPdf(int Width, int Height, int Components, int Precision, int FrameMarker, bool Adobe, double DpiX, double DpiY, byte[] Stripped)
    {
        /// <summary>Baseline, extended or progressive Huffman coding, 8 bits, 1, 3 or 4 components: what PDF readers decode.</summary>
        public bool CanPassThrough => FrameMarker is 0xC0 or 0xC1 or 0xC2 && Precision == 8 && Components is 1 or 3 or 4;

        public static JpegForPdf? Read(byte[] jpeg)
        {
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
            using var output = new MemoryStream(jpeg.Length);
            output.Write([0xFF, 0xD8]);
            int pos = 2, width = 0, height = 0, components = 0, precision = 0, frame = 0;
            bool adobe = false;
            double dpiX = 0, dpiY = 0;
            while (pos + 1 < jpeg.Length)
            {
                if (jpeg[pos] != 0xFF) return null;
                int marker = jpeg[pos + 1];
                if (marker == 0xFF) { pos++; continue; }
                if (marker == 0xD9)
                {
                    output.Write([0xFF, 0xD9]);
                    break;
                }
                if (marker is 0x01 or >= 0xD0 and <= 0xD7)
                {
                    output.Write(jpeg, pos, 2);
                    pos += 2;
                    continue;
                }
                if (pos + 4 > jpeg.Length) return null;
                int length = jpeg[pos + 2] << 8 | jpeg[pos + 3];
                if (length < 2 || pos + 2 + length > jpeg.Length) return null;
                var segment = jpeg.AsSpan(pos + 4, length - 2);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && frame == 0 && segment.Length >= 6)
                {
                    frame = marker;
                    precision = segment[0];
                    height = segment[1] << 8 | segment[2];
                    width = segment[3] << 8 | segment[4];
                    components = segment[5];
                }
                else if (marker == 0xE0 && segment.Length >= 12 && segment.StartsWith("JFIF\0"u8))
                {
                    int units = segment[7], x = segment[8] << 8 | segment[9], y = segment[10] << 8 | segment[11];
                    double k = units switch { 1 => 1, 2 => 2.54, _ => 0 };
                    dpiX = x * k;
                    dpiY = y * k;
                }
                else if (marker == 0xEE && segment.StartsWith("Adobe"u8))
                {
                    adobe = true;
                }
                bool keep = marker switch
                {
                    0xE0 => segment.StartsWith("JFIF\0"u8),
                    0xE2 => segment.StartsWith("ICC_PROFILE\0"u8),
                    0xEE => segment.StartsWith("Adobe"u8),
                    >= 0xE1 and <= 0xEF or 0xFE => false,
                    _ => true,
                };
                if (keep) output.Write(jpeg, pos, 2 + length);
                pos += 2 + length;
                if (marker == 0xDA)
                {
                    // The scan's coded data, to the next marker (0xFF 0x00 is a data byte, 0xFF 0xD0-D7 a restart).
                    int start = pos;
                    while (pos + 1 < jpeg.Length && !(jpeg[pos] == 0xFF && jpeg[pos + 1] is not 0x00 and not (>= 0xD0 and <= 0xD7) and not 0xFF)) pos++;
                    output.Write(jpeg, start, pos - start);
                }
            }
            if (frame == 0 || width <= 0 || height <= 0) return null;
            // A file cut short after its scan is still decoded as far as it goes; it ends properly here.
            if (pos + 1 >= jpeg.Length) output.Write([0xFF, 0xD9]);
            return new JpegForPdf(width, height, components, precision, frame, adobe, dpiX, dpiY, output.ToArray());
        }
    }
}
