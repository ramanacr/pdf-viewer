using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Skia;
using PdfEngine.Vector.Text;

namespace PdfViewer.Web.Engine;

/// <summary>A page's size as shown (its /Rotate applied), in PDF points.</summary>
public readonly record struct ViewerPage(int Number, double Width, double Height);

/// <summary>A rectangle on a page as shown: PDF points from the top-left corner of the rotated page.</summary>
public readonly record struct ViewerRect(double X, double Y, double Width, double Height);

/// <summary>
/// One search hit: its page, its order on the page, one rectangle per line it covers, and the
/// matched text with a few words around it (for a results list).
/// </summary>
public sealed record ViewerSearchHit(int Page, int IndexOnPage, IReadOnlyList<ViewerRect> Rects, string Before, string Match, string After);

/// <summary>An outline (bookmark) entry; <see cref="Page"/> is null when it goes nowhere in this document.</summary>
public sealed record ViewerOutlineItem(string Title, int? Page, IReadOnlyList<ViewerOutlineItem> Children);

/// <summary>Limits and resources of a viewer session.</summary>
public sealed class ViewerOptions
{
    /// <summary>Largest file accepted, in bytes.</summary>
    public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Decoded images the renderer keeps across renders.</summary>
    public long ImageCacheBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Largest bitmap a render produces (pixels); a larger request is rendered at the largest scale that fits.</summary>
    public long MaxBitmapPixels { get; init; } = 4096L * 4096;

    /// <summary>
    /// Substitutes for fonts a document does not embed; null uses the platform's installed fonts (none
    /// in a browser). An <see cref="OnDemandFontSource"/> is fetched from as pages need its faces.
    /// </summary>
    public SkiaFontSource? Fonts { get; init; }
}

/// <summary>A rendered page: BGRA, premultiplied, opaque, <see cref="Width"/> × 4 bytes per row.</summary>
public sealed class ViewerBitmap
{
    internal ViewerBitmap(int page, int width, int height, double scale, ArraySegment<byte> pixels, TimeSpan build, TimeSpan raster,
        IReadOnlyList<PdfEngine.Vector.PdfFallbackToken> unsupported)
    {
        Page = page;
        Width = width;
        Height = height;
        Scale = scale;
        Pixels = pixels;
        BuildTime = build;
        RasterTime = raster;
        UnsupportedRegions = unsupported.Count;
        TextRegionsWithoutFont = unsupported.Count(t => t.Reason is PdfEngine.Vector.PdfFallbackReason.UnsupportedFontType or PdfEngine.Vector.PdfFallbackReason.TextClipping);
        ImagesNotDecoded = unsupported.Count(t => t.Reason is PdfEngine.Vector.PdfFallbackReason.ImageDecode or PdfEngine.Vector.PdfFallbackReason.UnsupportedImageFilter);
    }

    public int Page { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Pixels per point actually used (lower than asked when the bitmap would exceed <see cref="ViewerOptions.MaxBitmapPixels"/>).</summary>
    public double Scale { get; }

    public ArraySegment<byte> Pixels { get; }
    public long ByteLength => Pixels.Count;

    /// <summary>Time spent interpreting the page into a display list (zero when it was cached).</summary>
    public TimeSpan BuildTime { get; }

    /// <summary>Time Skia spent drawing the display list.</summary>
    public TimeSpan RasterTime { get; }

    /// <summary>Regions of the page this renderer cannot draw faithfully (for example a CMYK JPEG).</summary>
    public int UnsupportedRegions { get; }

    /// <summary>Of those, text whose font is neither embedded nor available as a substitute (no installed fonts in a browser).</summary>
    public int TextRegionsWithoutFont { get; }

    /// <summary>Of those, images that could not be decoded (four-component JPEG, damaged data).</summary>
    public int ImagesNotDecoded { get; }
}

/// <summary>
/// One open document in a viewer: the engine-facing layer of the web demo, free of any UI or
/// browser API so that other hosts (an embeddable SDK, tests) can use it as it is. It opens a
/// document from bytes, renders pages with the portable Skia backend, searches the engine's text
/// and reads the outline. Calls are serialized, one engine operation at a time, which is what
/// single-threaded WebAssembly needs (a blocking wait there throws) and costs nothing elsewhere.
/// </summary>
public sealed class ViewerDocument : IAsyncDisposable
{
    private readonly PdfVectorDocument _document;
    private readonly ViewerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SkiaVectorRenderer _renderer;
    private SkiaFontSource? _fonts;
    private bool _disposed;

    /// <summary>Most times a page is drawn again after fetching the fonts it asked for (each time fetches at least one face).</summary>
    private const int MaxFontRounds = 4;

    private ViewerDocument(PdfVectorDocument document, ViewerOptions options, IReadOnlyList<ViewerPage> pages)
    {
        _document = document;
        _options = options;
        Pages = pages;
        _fonts = options.Fonts;
        _renderer = new SkiaVectorRenderer(options.ImageCacheBytes, _fonts);
    }

    public int PageCount => Pages.Count;

    public IReadOnlyList<ViewerPage> Pages { get; }

    /// <summary>The document's own title (/Title in its information dictionary), if it has one.</summary>
    public string? Title => string.IsNullOrWhiteSpace(_document.Metadata.Title) ? null : _document.Metadata.Title;

    /// <summary>"1.7" and the like, from the file header.</summary>
    public string? PdfVersion => _document.HeaderVersion;

    public bool IsEncrypted => _document.IsEncrypted;

    /// <summary>True when the cross-reference table was rebuilt because the file is damaged.</summary>
    public bool WasRepaired => _document.WasRepaired;

    /// <summary>Opens a document held in memory. Throws <see cref="ViewerOpenException"/> with the reason when it cannot.</summary>
    public static async Task<ViewerDocument> OpenAsync(byte[] bytes, string? password = null, ViewerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new ViewerOptions();
        if (bytes.LongLength > options.MaxFileBytes)
            throw new ViewerOpenException(ViewerOpenFailure.TooLarge,
                $"This file is larger than {options.MaxFileBytes / (1024 * 1024)} MB, the most this viewer opens.");

        PdfVectorDocument document;
        try
        {
            document = await PdfVectorDocument.OpenAsync(bytes, "Document.pdf", password: password, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.CertificateRequired)
        {
            throw new ViewerOpenException(ViewerOpenFailure.CertificateRequired,
                "This PDF is encrypted for specific recipients' certificates. This viewer cannot use certificates, so it cannot be opened here.", ex);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
        {
            throw string.IsNullOrEmpty(password)
                ? new ViewerOpenException(ViewerOpenFailure.PasswordRequired, "This PDF is protected by a password.", ex)
                : new ViewerOpenException(ViewerOpenFailure.WrongPassword, "That password does not open this PDF.", ex);
        }
        catch (Exception ex) when (Unwrap(ex).Any(e => e is PlatformNotSupportedException
                                       || e is System.Security.Cryptography.CryptographicException && !PlatformCrypto.CanDecrypt))
        {
            // The only platform-dependent step of opening is decryption. The engine brings its own
            // MD5 and AES where the runtime has none; what remains is certificate (public-key)
            // encryption, which needs RSA and a certificate store.
            throw new ViewerOpenException(ViewerOpenFailure.EncryptionNotSupportedHere,
                "This PDF is encrypted with a method this platform's .NET runtime cannot decrypt. The desktop viewer opens it.", ex);
        }
        catch (PdfEncryptedDocumentException ex)
        {
            throw new ViewerOpenException(ViewerOpenFailure.NotReadable, "This PDF uses an encryption method the engine does not support.", ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Hostile or broken input: whatever the parser threw, the file is simply not readable.
            throw new ViewerOpenException(ViewerOpenFailure.NotReadable, "This file is not a PDF the engine can read, or it is damaged.", ex);
        }

        static IEnumerable<Exception> Unwrap(Exception? e)
        {
            for (; e != null; e = e.InnerException) yield return e;
        }

        try
        {
            var pages = new ViewerPage[document.PageCount];
            for (int i = 0; i < pages.Length; i++)
            {
                var info = await document.GetPageInfoAsync(i + 1, cancellationToken).ConfigureAwait(false);
                pages[i] = new ViewerPage(i + 1, info.WidthPoints, info.HeightPoints);
            }
            if (pages.Length == 0)
                throw new ViewerOpenException(ViewerOpenFailure.NotReadable, "This PDF has no pages.");
            return new ViewerDocument(document, options, pages);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Renders a page at <paramref name="scale"/> pixels per point (1 is 72 dpi). <paramref name="night"/>
    /// inverts the finished page, as the desktop viewer's night mode does.
    /// </summary>
    public async Task<ViewerBitmap> RenderPageAsync(int page, double scale, bool night = false, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (!(scale > 0) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var clock = Stopwatch.StartNew();
            var list = await _document.GetPageDisplayListAsync(page, cancellationToken).ConfigureAwait(false);
            var build = clock.Elapsed;

            var request = new RenderRequest { PageNumber = page, Dpi = scale * 72 };
            var (w, h) = SkiaVectorRenderer.OutputSize(list, request);
            if ((long)w * h > _options.MaxBitmapPixels)
            {
                scale *= Math.Sqrt((double)_options.MaxBitmapPixels / ((long)w * h)) * 0.999;
                request = new RenderRequest { PageNumber = page, Dpi = scale * 72 };
            }

            var result = await _renderer.RenderAsync(list, request, null, null, night, cancellationToken).ConfigureAwait(false);
            // Fonts fetched on demand: the page asked for faces that are not here yet. Fetch them and
            // draw it again with a fresh renderer (the old one remembers those faces as missing).
            // Each round settles every face asked for (loaded, or failed and then skipped), so a
            // failed face lets the text fall through to the next substitute on the next round.
            for (int round = 0; round < MaxFontRounds && _fonts is OnDemandFontSource lazy && lazy.HasPendingRequests; round++)
            {
                await lazy.LoadRequestedAsync(cancellationToken).ConfigureAwait(false);
                result.Page.Dispose();
                _renderer.Dispose();
                _renderer = new SkiaVectorRenderer(_options.ImageCacheBytes, _fonts);
                result = await _renderer.RenderAsync(list, request, null, null, night, cancellationToken).ConfigureAwait(false);
            }
            // The Skia backend's pages own a plain array (disposing them frees nothing): keep it, no copy.
            using var rendered = result.Page;
            var pixels = MemoryMarshal.TryGetArray(rendered.Pixels, out var segment) && segment.Count == rendered.WidthPixels * 4 * rendered.HeightPixels
                ? segment
                : new ArraySegment<byte>(rendered.Pixels.ToArray());
            return new ViewerBitmap(page, rendered.WidthPixels, rendered.HeightPixels, scale, pixels, build, result.Elapsed, result.Fallbacks);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Every match of <paramref name="query"/>, page by page, with the engine's search rules (those
    /// of the desktop viewer). Pages are read one at a time, so a host stays responsive between them.
    /// </summary>
    public async IAsyncEnumerable<ViewerSearchHit> SearchAsync(string query, bool matchCase = false, bool wholeWord = false,
        IProgress<int>? pagesSearched = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;
        for (int p = 1; p <= PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hits = await SearchPageAsync(p, query, matchCase, wholeWord, cancellationToken).ConfigureAwait(false);
            foreach (var hit in hits) yield return hit;
            pagesSearched?.Report(p);
            // Let the host draw between pages (on a single thread nothing else runs otherwise).
            await Task.Yield();
        }
    }

    /// <summary>The matches of <paramref name="query"/> on one page.</summary>
    public async Task<IReadOnlyList<ViewerSearchHit>> SearchPageAsync(int page, string query, bool matchCase = false, bool wholeWord = false,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var text = PdfTextExtractor.LoadPage(_document, page, cancellationToken);
            var found = text.Find(query, matchCase, wholeWord);
            var hits = new List<ViewerSearchHit>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                var (start, count) = found[i];
                int before = Math.Max(0, start - ContextChars);
                hits.Add(new ViewerSearchHit(page, i, HighlightRects(text, start, count),
                    Context(text, before, start - before, leading: true),
                    Flatten(text.GetText(start, count)),
                    Context(text, start + count, ContextChars, leading: false)));
            }
            return hits;
        }
        finally
        {
            _gate.Release();
        }
    }

    private const int ContextChars = 32;

    /// <summary>Text around a match, cut at a word boundary on the far side.</summary>
    private static string Context(PdfTextPage text, int start, int count, bool leading)
    {
        if (count <= 0 || start >= text.CharCount) return "";
        string s = Flatten(text.GetText(start, count));
        // At the start or the end of the page there is no word to cut.
        if (leading ? start == 0 : start + count >= text.CharCount) return s;
        int cut = leading ? s.IndexOf(' ') : s.LastIndexOf(' ');
        return cut < 0 ? s : leading ? "…" + s[(cut + 1)..] : s[..cut] + "…";
    }

    /// <summary>Line breaks and tabs as single spaces.</summary>
    private static string Flatten(string s) => string.Join(' ', s.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The page's text in reading order, as the engine extracts it.</summary>
    public async Task<string> GetPageTextAsync(int page, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return PdfTextExtractor.LoadPage(_document, page, cancellationToken).GetAllText();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The document outline, empty when it has none.</summary>
    public async Task<IReadOnlyList<ViewerOutlineItem>> GetOutlineAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bookmarks = await _document.GetBookmarksAsync(cancellationToken).ConfigureAwait(false);
            return bookmarks.Select(Convert).ToList();
        }
        finally
        {
            _gate.Release();
        }

        ViewerOutlineItem Convert(BookmarkItem b) =>
            new(b.Title, b.TargetPageNumber >= 1 && b.TargetPageNumber <= PageCount ? b.TargetPageNumber : null, b.Children.Select(Convert).ToList());
    }

    /// <summary>Uses other font substitutes from now on (for example fonts the user allowed the page to read).</summary>
    public async Task UseFontsAsync(SkiaFontSource fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _renderer.Dispose();
            _fonts = fonts;
            _renderer = new SkiaVectorRenderer(_options.ImageCacheBytes, fonts);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One rectangle per line of the characters [start, start + count): the characters' font boxes
    /// (ascent to descent) united along each line, in shown-page points from the top-left corner.
    /// </summary>
    internal static IReadOnlyList<ViewerRect> HighlightRects(PdfTextPage text, int start, int count)
    {
        var rects = new List<ViewerRect>();
        ViewerRect? line = null;
        for (int i = start; i < start + count && i < text.CharCount; i++)
        {
            if (!text.HasTextObject(i)) continue;
            var box = text.GetLooseCharBox(i);
            box.Normalize();
            if (box.IsEmpty) continue;
            var r = ToShown(text, box);
            if (line is { } l && Overlaps(l, r))
            {
                double x0 = Math.Min(l.X, r.X), y0 = Math.Min(l.Y, r.Y);
                line = new ViewerRect(x0, y0, Math.Max(l.X + l.Width, r.X + r.Width) - x0, Math.Max(l.Y + l.Height, r.Y + r.Height) - y0);
            }
            else
            {
                if (line != null) rects.Add(line.Value);
                line = r;
            }
        }
        if (line != null) rects.Add(line.Value);
        return rects;

        // Same line: the boxes overlap across the line direction by more than half the smaller one.
        // Lines run across the shown page unless /Rotate turned it sideways (horizontal writing).
        bool Overlaps(ViewerRect a, ViewerRect b)
        {
            bool horizontal = text.Rotation is 0 or 180;
            double overlap = horizontal
                ? Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y)
                : Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
            double smaller = horizontal ? Math.Min(a.Height, b.Height) : Math.Min(a.Width, b.Width);
            return overlap > smaller / 2;
        }
    }

    /// <summary>A user-space box on the shown page: crop box origin, y down, then the page's /Rotate (clockwise).</summary>
    internal static ViewerRect ToShown(PdfTextPage text, PdfEngine.Vector.Text.TextRect box)
    {
        var crop = text.PageBox;
        double w = crop.Width, h = crop.Height;
        double u0 = box.Left - crop.X, u1 = box.Right - crop.X;
        double v0 = crop.Y + crop.Height - box.Top, v1 = crop.Y + crop.Height - box.Bottom;
        return text.Rotation switch
        {
            90 => new ViewerRect(h - v1, u0, v1 - v0, u1 - u0),
            180 => new ViewerRect(w - u1, h - v1, u1 - u0, v1 - v0),
            270 => new ViewerRect(v0, w - u1, v1 - v0, u1 - u0),
            _ => new ViewerRect(u0, v0, u1 - u0, v1 - v0),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _renderer.Dispose();
            PdfTextExtractor.Invalidate(_document);
            _document.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
