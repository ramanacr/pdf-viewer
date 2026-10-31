using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfEngine.Documents;
using PdfEngine.Rendering;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
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

/// <summary>
/// What a host may show about a document: the information dictionary, the file header's version,
/// security and structure. Strings are empty when the document does not say.
/// </summary>
public sealed record ViewerMetadata(
    int PageCount, string Title, string Author, string Subject, string Keywords, string Creator, string Producer,
    DateTime? CreationDate, DateTime? ModificationDate, string PdfVersion, bool IsEncrypted, bool IsTagged,
    bool CanCopy, bool CanPrint, bool WasRepaired, long FileSizeBytes, bool IsSigned = false, int? CertificationLevel = null);

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
        Fallbacks = unsupported;
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

    /// <summary>Regions of the page this renderer cannot draw faithfully (for example a font it cannot load).</summary>
    public int UnsupportedRegions { get; }

    internal IReadOnlyList<PdfEngine.Vector.PdfFallbackToken> Fallbacks { get; }

    /// <summary>Of those, text whose font is neither embedded nor available as a substitute (no installed fonts in a browser).</summary>
    public int TextRegionsWithoutFont { get; }

    /// <summary>Of those, images that could not be decoded (damaged data, arithmetic-coded JPEG).</summary>
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
    // The parse of the bytes as opened, and of the current revision (the same until the first edit).
    private PdfVectorDocument _original;
    private PdfVectorDocument _document;
    private byte[] _originalBytes;
    private byte[] _bytes;
    // Opened by ranges (OpenRangedAsync) and not complete yet: pages are drawn from it, everything
    // else waits for the whole file (EnsureCompleteAsync). Null once the file is in memory.
    private PdfRangedDocument? _ranged;
    private Task? _completion;
    private readonly CancellationTokenSource _closing = new();
    private long _loadedBytes;
    private readonly string? _password;
    // A damaged file is written whole once before its first edit, since it cannot take an incremental update.
    private (PdfVectorDocument Document, byte[] Bytes)? _editBase;
    private readonly ViewerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SkiaVectorRenderer _renderer;
    private SkiaFontSource? _fonts;
    private bool _disposed;
    private readonly long _fileSize;

    /// <summary>
    /// Most times a page is drawn again after fetching the fonts it asked for (each time fetches at
    /// least one file). Three are needed at most: the Liberation face, the Noto index, the Noto faces.
    /// </summary>
    private const int MaxFontRounds = 5;

    private ViewerDocument(PdfVectorDocument document, byte[] bytes, string? password, ViewerOptions options, IReadOnlyList<ViewerPage> pages, long fileSize)
    {
        _original = _document = document;
        _originalBytes = _bytes = bytes;
        _password = password;
        _geometry = document.PageTree.Pages.Select(p => new Annotations.ViewerPageGeometry(p.MediaBox, p.CropBox, p.RotationDegrees)).ToArray();
        _fileSize = fileSize;
        _options = options;
        _pages = pages.ToArray();
        _fonts = options.Fonts;
        _renderer = new SkiaVectorRenderer(options.ImageCacheBytes, _fonts);
        _signed = PdfEngine.Vector.Signatures.PdfSignatureValidator.ReadSignedState(document);
        _incremental = PdfEngine.Vector.Services.PdfUpdate.TakesIncrementalUpdate(document, bytes);
    }

    /// <summary>A document opened by ranges, before the whole file is in memory: only its pages can be drawn.</summary>
    private ViewerDocument(PdfRangedDocument ranged, string? password, ViewerOptions options)
    {
        _ranged = ranged;
        _original = _document = ranged.Document;
        _originalBytes = _bytes = [];
        _password = password;
        (_pages, _geometry) = LayoutOf(ranged.Document.PageTree);
        _fileSize = ranged.Length;
        _options = options;
        _fonts = options.Fonts;
        _renderer = new SkiaVectorRenderer(options.ImageCacheBytes, _fonts);
        // Known once the file is complete (they read the form fields and the whole file).
        _signed = new PdfEngine.Vector.Signatures.PdfSignedState(0, null);
        _incremental = false;
    }

    // Signatures of the file as opened, and whether it takes an incremental update (else an edit rewrites it whole).
    private PdfEngine.Vector.Signatures.PdfSignedState _signed;
    private bool _incremental;

    /// <summary>
    /// True when the whole file is in memory. A document from <see cref="OpenRangedAsync"/> is not
    /// complete at first: its pages can be drawn and their text read for selection, and every
    /// other call waits for the rest of the file (<see cref="EnsureCompleteAsync"/>).
    /// <see cref="Bytes"/> and <see cref="OriginalBytes"/> throw until then, and
    /// <see cref="CanAnnotate"/> and <see cref="CanFillForms"/> are false.
    /// </summary>
    public bool IsComplete => _ranged == null;

    /// <summary>Raised when a ranged document becomes complete.</summary>
    public event Action? Completed;

    /// <summary>Bytes of the file read so far (the file size once complete).</summary>
    public long LoadedBytes => _ranged is { } r ? Math.Max(Interlocked.Read(ref _loadedBytes), r.Statistics.BytesFetched) : _fileSize;

    /// <summary>The file size in bytes.</summary>
    public long FileSize => _fileSize;

    /// <summary>For a document opened by ranges: what was fetched so far (kept after it is complete); null for one opened from memory.</summary>
    public PdfRangeStatistics? RangeStatistics { get => _ranged?.Statistics ?? _lastRangeStatistics; }
    private PdfRangeStatistics? _lastRangeStatistics;

    /// <summary>Why a ranged open read the whole file at once (for example a damaged cross-reference table), or null.</summary>
    public string? RangeFallbackReason { get; private set; }

    /// <summary>The document now: the ranged one's current parse until it is complete.</summary>
    private PdfVectorDocument Doc => _ranged?.Document ?? _document;

    public int PageCount => _pages.Length;

    /// <summary>
    /// Each page's size as shown. For a document opened by ranges, a page not read yet has the
    /// size of the first page until it is read (when it is drawn, or when the file is complete);
    /// <see cref="PagesChanged"/> tells when a size (or, for a damaged page tree, the page count) changes.
    /// </summary>
    public IReadOnlyList<ViewerPage> Pages => _pages;
    private ViewerPage[] _pages;

    /// <summary>Each page's boxes and rotation: how its user space maps to the page as shown (for a ranged document, as <see cref="Pages"/>).</summary>
    public IReadOnlyList<Annotations.ViewerPageGeometry> Geometry => _geometry;
    private Annotations.ViewerPageGeometry[] _geometry;

    /// <summary>
    /// Raised when <see cref="Pages"/> changed for a document opened by ranges: a page read for
    /// the first time is not the size of the first page, or the whole file showed other sizes or
    /// another page count, or a page drawn before showed another page's dictionary (a page tree
    /// whose counts did not match its kids). The host lays the pages out again and draws again
    /// the pages it has drawn.
    /// </summary>
    public event Action? PagesChanged;

    /// <summary>
    /// The layout of a ranged document's pages: each page read so far as it is, the others the
    /// size of the first page (or US Letter when no page is read), so that the layout moves only
    /// where a page differs from the first.
    /// </summary>
    private static (ViewerPage[] Pages, Annotations.ViewerPageGeometry[] Geometry) LayoutOf(PdfPageTree tree)
    {
        var pages = new ViewerPage[tree.Count];
        var geometry = new Annotations.ViewerPageGeometry[tree.Count];
        var first = tree.LoadedPage(0);
        var letter = new PdfEngine.Geometry.PdfRect(0, 0, 612, 792);
        for (int i = 0; i < pages.Length; i++)
        {
            var node = tree.LoadedPage(i) ?? first;
            pages[i] = node != null ? new ViewerPage(i + 1, node.PageSize.Width, node.PageSize.Height) : new ViewerPage(i + 1, 612, 792);
            geometry[i] = node != null ? new Annotations.ViewerPageGeometry(node.MediaBox, node.CropBox, node.RotationDegrees) : new Annotations.ViewerPageGeometry(letter, letter, 0);
        }
        return (pages, geometry);
    }

    /// <summary>After a ranged operation on <paramref name="page"/>: its size as read. True when the layout changed.</summary>
    private bool SyncRangedPage(PdfRangedDocument ranged, int page)
    {
        var tree = ranged.Document.PageTree;
        if (tree.Count != _pages.Length)
        {
            // The lazy tree did not match the file and was read whole: another page count.
            if (tree.Count == 0) return false;
            (_pages, _geometry) = LayoutOf(tree);
            _shownObjects.Clear();
            return true;
        }
        bool changed = false;
        // Read whole after a mismatch: a page shown before may have been another page's dictionary.
        if (!tree.IsLazy) changed |= ForgetStalePages(tree);
        if (page >= 1 && page <= tree.Count && tree.LoadedPage(page - 1) is { } node)
        {
            changed |= SetPage(page - 1, node);
            _shownObjects[page] = tree.PageObjectNumbers[page - 1];
        }
        return changed;
    }

    // The page dictionary (object number) each page was read from while the file was read by
    // ranges: a lazy tree that does not match its counts can show another page's dictionary
    // until the tree is read whole.
    private readonly Dictionary<int, int> _shownObjects = new();

    /// <summary>True when a page read so far is another page's dictionary by <paramref name="tree"/> (read whole); those pages are forgotten.</summary>
    private bool ForgetStalePages(PdfPageTree tree)
    {
        var stale = _shownObjects.Where(e => e.Key > tree.Count || tree.PageObjectNumbers[e.Key - 1] != e.Value).Select(e => e.Key).ToList();
        foreach (int p in stale) _shownObjects.Remove(p);
        return stale.Count > 0;
    }

    private bool SetPage(int index, PdfPageNode node)
    {
        var shown = new ViewerPage(index + 1, node.PageSize.Width, node.PageSize.Height);
        var geometry = new Annotations.ViewerPageGeometry(node.MediaBox, node.CropBox, node.RotationDegrees);
        if (_pages[index] == shown && _geometry[index] == geometry) return false;
        _pages[index] = shown;
        _geometry[index] = geometry;
        return true;
    }

    /// <summary>
    /// For a document opened by ranges: reads page <paramref name="page"/>'s size (the page-tree
    /// nodes above it, not its contents) so that it is drawn at its own size. Raises
    /// <see cref="PagesChanged"/> when it differs from the size shown so far.
    /// </summary>
    public async Task EnsurePageSizeAsync(int page, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (_ranged == null) return;
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ranged is not { } ranged) return;
            if (ranged.Document.PageTree.LoadedPage(page - 1) == null) await ranged.FindPageAsync(page, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also after a failure: a page the lazy tree could not place may have changed the page count.
            if (_ranged is { } synced && !_disposed) changed |= SyncRangedPage(synced, page);
            _gate.Release();
            if (changed) PagesChanged?.Invoke();
        }
    }

    /// <summary>The bytes the document was opened from. Throws until the document <see cref="IsComplete"/>.</summary>
    public byte[] OriginalBytes => IsComplete ? _originalBytes : throw new InvalidOperationException("The file is still loading.");

    /// <summary>The current revision: the bytes as opened plus the reader's annotations and form data (<see cref="ApplyEditsAsync"/>). Throws until the document <see cref="IsComplete"/>.</summary>
    public byte[] Bytes => IsComplete ? _bytes : throw new InvalidOperationException("The file is still loading.");

    /// <summary>True when <see cref="Bytes"/> differ from the bytes as opened.</summary>
    public bool IsEdited => !ReferenceEquals(_bytes, _originalBytes);

    /// <summary>The document's own title (/Title in its information dictionary), if it has one.</summary>
    public string? Title => string.IsNullOrWhiteSpace(Doc.Metadata.Title) ? null : Doc.Metadata.Title;

    /// <summary>"1.7" and the like, from the file header.</summary>
    public string? PdfVersion => Doc.HeaderVersion;

    public bool IsEncrypted => Doc.IsEncrypted;

    /// <summary>True when the cross-reference table was rebuilt because the file is damaged.</summary>
    public bool WasRepaired => Doc.WasRepaired;

    /// <summary>
    /// The document lets the reader copy its text (ISO 32000-2 Table 22, bit 5): always for an
    /// unencrypted file or one opened with the owner password.
    /// </summary>
    public bool CanCopy => Doc.Security is not { } s
                           || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanCopy;

    /// <summary>
    /// The document lets the reader add and change annotations: its permissions (ISO 32000-2
    /// Table 22, bit 6, as <see cref="CanCopy"/>) and its signatures (<see cref="EditRestriction"/>)
    /// allow it. A certification signature allows annotations only at level 3.
    /// </summary>
    public bool CanAnnotate => IsComplete && SignaturesAllowEdits && _signed.CertificationLevel is null or 3
                               && (_document.Security is not { } s
                                   || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanAnnotate);

    /// <summary>
    /// The document lets the reader fill its form fields: its permissions (bits 6 or 9, as
    /// <see cref="CanCopy"/>) and its signatures allow it. A certification signature allows form
    /// filling at levels 2 and 3.
    /// </summary>
    public bool CanFillForms => IsComplete && SignaturesAllowEdits && _signed.CertificationLevel is not 1
                                && (_document.Security is not { } s
                                    || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanFillForms);

    /// <summary>True when the file as opened has a signed signature field or a certification signature.</summary>
    public bool IsSigned => _signed.IsSigned;

    /// <summary>The certification signature's DocMDP level (1 no changes, 2 form filling, 3 also annotations); null when the file is not certified.</summary>
    public int? CertificationLevel => _signed.CertificationLevel;

    /// <summary>
    /// A signed file that cannot take an incremental update (it was repaired, or has no
    /// startxref) would be written whole, which breaks every signature: it is not edited at all.
    /// </summary>
    private bool SignaturesAllowEdits => !_signed.IsSigned || _incremental;

    /// <summary>Why the signatures stop or limit editing (Acrobat's rules), or null when they do not.</summary>
    public string? EditRestriction =>
        !_signed.IsSigned ? null
        : !_incremental ? "This signed document is damaged: saving changes would rewrite the whole file and break its signatures, so it cannot be changed here."
        : _signed.CertificationLevel switch
        {
            1 => "This document is certified, and its certification does not allow changes.",
            2 => "This document is certified for form filling only: annotations cannot be added or changed.",
            _ => null,
        };

    /// <summary>What a reader of a signed document is told before the first change, or null for an unsigned one or one that cannot be changed.</summary>
    public string? EditNotice =>
        _signed.IsSigned && SignaturesAllowEdits && _signed.CertificationLevel is not 1
            ? "Changes will be added as a new revision; existing signatures stay valid but will show that the document was modified after signing."
            : null;

    /// <summary>The document lets the reader print it (ISO 32000-2 Table 22, bit 3), as <see cref="CanCopy"/>.</summary>
    public bool CanPrint => Doc.Security is not { } s
                            || PdfDocumentPermissions.FromFlags(s.Permissions, s.Revision, s.IsOwner).CanPrint;

    /// <summary>The document's information, security and whether it is tagged (has a structure tree).</summary>
    public async Task<ViewerMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var m = _document.Metadata;
            bool tagged;
            try
            {
                tagged = PdfEngine.Vector.Accessibility.PdfAutoTagger.IsTagged(_document);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tagged = false; // a damaged catalog entry: not usable as a structure tree
            }
            // The engine's metadata has the common strings; keywords and dates come from /Info here.
            string? Info(string key)
            {
                try
                {
                    return _document.Resolver.Resolve(_document.XrefTable.Trailer?["Info"]) is PdfEngine.Vector.Objects.PdfDictionary info
                        ? PdfEngine.Vector.Objects.PdfTextStrings.Decode(_document.Resolver.Resolve(info[key]))
                        : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return null; // a damaged entry reads as absent
                }
            }
            return new ViewerMetadata(PageCount, m.Title ?? "", m.Author ?? "", m.Subject ?? "", Info("Keywords") ?? "", m.Creator ?? "", m.Producer ?? "",
                PdfEngine.Vector.Objects.PdfTextStrings.ParseDate(Info("CreationDate")), PdfEngine.Vector.Objects.PdfTextStrings.ParseDate(Info("ModDate")),
                _document.HeaderVersion ?? m.PdfVersion, _document.IsEncrypted, tagged, CanCopy, CanPrint, WasRepaired, _fileSize,
                _signed.IsSigned, _signed.CertificationLevel);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a document held in memory. Throws <see cref="ViewerOpenException"/> with the reason when it cannot.</summary>
    public static async Task<ViewerDocument> OpenAsync(byte[] bytes, string? password = null, ViewerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new ViewerOptions();
        var document = await ParseAsync(bytes, password, options, cancellationToken).ConfigureAwait(false);
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
            return new ViewerDocument(document, bytes, password, options, pages, bytes.LongLength);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>The parse of a whole file, with the size cap of <paramref name="options"/>; failures as <see cref="ViewerOpenException"/>.</summary>
    private static async Task<PdfVectorDocument> ParseAsync(byte[] bytes, string? password, ViewerOptions options, CancellationToken cancellationToken)
    {
        if (bytes.LongLength > options.MaxFileBytes)
            throw new ViewerOpenException(ViewerOpenFailure.TooLarge,
                $"This file is larger than {options.MaxFileBytes / (1024 * 1024)} MB, the most this viewer opens.");
        try
        {
            return await PdfVectorDocument.OpenAsync(bytes, "Document.pdf", password: password, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (OpenFailure(ex, password) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Opens a document from a range reader (a file on a web server, read by HTTP range requests
    /// through the host page) without reading the whole file first: the end of the file, the root
    /// of the page tree and the path to page 1, then each page's objects when it is drawn
    /// (<see cref="PdfRangedDocument"/>). Pages not read yet have the first page's size in
    /// <see cref="Pages"/> until they are read (<see cref="PagesChanged"/>). The
    /// document is not <see cref="IsComplete"/> at first; call <see cref="EnsureCompleteAsync"/>
    /// (for example after the first page is drawn) to read the rest in the background. A file whose
    /// cross-reference data is damaged is read whole at once and opened as by <see cref="OpenAsync"/>
    /// (<see cref="RangeFallbackReason"/> says so). Throws <see cref="ViewerOpenException"/> as <see cref="OpenAsync"/>
    /// does, and with <see cref="ViewerOpenFailure.NetworkError"/> when the reader fails.
    /// </summary>
    public static async Task<ViewerDocument> OpenRangedAsync(IPdfRangeReader reader, string? password = null, ViewerOptions? options = null,
        PdfRangedOptions? rangeOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new ViewerOptions();
        if (reader.Length > options.MaxFileBytes)
            throw new ViewerOpenException(ViewerOpenFailure.TooLarge,
                $"This file is larger than {options.MaxFileBytes / (1024 * 1024)} MB, the most this viewer opens.");
        if (reader.Length <= 0)
            throw new ViewerOpenException(ViewerOpenFailure.NotReadable, "The file is empty.");

        PdfRangedDocument ranged;
        try
        {
            ranged = await PdfRangedDocument.OpenAsync(reader, rangeOptions, password, null, "Document.pdf", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (OpenFailure(ex, password) is { } failure)
        {
            throw failure;
        }
        if (ranged.IsComplete)
        {
            // Read whole (a damaged file): from here on it is an ordinary document in memory.
            var bytes = ranged.Bytes!;
            string? reason = ranged.FallbackReason;
            var stats = ranged.Statistics;
            ranged.Dispose();
            var whole = await OpenAsync(bytes, password, options, cancellationToken).ConfigureAwait(false);
            whole.RangeFallbackReason = reason;
            whole._lastRangeStatistics = stats;
            return whole;
        }
        try
        {
            // The page count is the page tree's /Count; pages not read yet are laid out at the first page's size.
            if (ranged.Document.PageCount == 0)
                throw new ViewerOpenException(ViewerOpenFailure.NotReadable, "This PDF has no pages.");
            return new ViewerDocument(ranged, password, options);
        }
        catch
        {
            ranged.Dispose();
            throw;
        }
    }

    /// <summary>
    /// For a document from <see cref="OpenRangedAsync"/>: reads the rest of the file (one request at
    /// a time, so that pages drawn meanwhile go first), then opens it from memory; from then on the
    /// document <see cref="IsComplete"/> and behaves as one from <see cref="OpenAsync"/>. Every call
    /// waits for the same read; <paramref name="cancellationToken"/> stops this wait, not the read.
    /// Returns at once for a complete document.
    /// </summary>
    public Task EnsureCompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_ranged == null) return Task.CompletedTask;
        Task completion;
        lock (_closing)
        {
            if (_completion is { IsFaulted: true } or { IsCanceled: true }) _completion = null;
            completion = _completion ??= CompleteAsync();
        }
        return completion.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the rest and parses the whole file as <see cref="OpenAsync"/> does (the same options
    /// and failures): a failure is a <see cref="ViewerOpenException"/>, never a raw engine exception.
    /// </summary>
    private async Task CompleteAsync()
    {
        var ranged = _ranged!;
        var token = _closing.Token;
        byte[] bytes;
        try
        {
            bytes = await ranged.ReadAllAsync(new SyncProgress(n => Interlocked.Exchange(ref _loadedBytes, n)), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ObjectDisposedException && OpenFailure(ex, _password) is { } failure)
        {
            throw failure;
        }
        var document = await ParseAsync(bytes, _password, _options, token).ConfigureAwait(false);
        bool changed = false;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                document.Dispose();
                return;
            }
            // Every page's own size now (pages not drawn were laid out at the first page's size).
            if (document.PageCount != _pages.Length)
            {
                (_pages, _geometry) = LayoutOf(document.PageTree);
                changed = true;
            }
            else
            {
                for (int i = 0; i < _pages.Length; i++) changed |= SetPage(i, document.PageTree.Pages[i]);
                // A page drawn from a lazy tree that did not match its counts showed another page.
                changed |= ForgetStalePages(document.PageTree);
            }
            _shownObjects.Clear();
            _lastRangeStatistics = ranged.Statistics;
            _original = _document = document;
            _originalBytes = _bytes = bytes;
            _signed = PdfEngine.Vector.Signatures.PdfSignatureValidator.ReadSignedState(document);
            _incremental = PdfEngine.Vector.Services.PdfUpdate.TakesIncrementalUpdate(document, bytes);
            _ranged = null;
            PdfTextExtractor.Invalidate(ranged.Document);
            ranged.Dispose();
        }
        finally
        {
            _gate.Release();
        }
        if (changed) PagesChanged?.Invoke();
        Completed?.Invoke();
    }

    /// <summary>Reports on the thread that reads (Progress&lt;T&gt; would post to a context).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>The <see cref="ViewerOpenException"/> for a failure to open, or null to let the exception through (cancellation, out of memory).</summary>
    private static ViewerOpenException? OpenFailure(Exception ex, string? password)
    {
        switch (ex)
        {
            case ViewerOpenException open:
                return open;
            case PdfEncryptedDocumentException e when e.CertificateRequired:
                return new ViewerOpenException(ViewerOpenFailure.CertificateRequired,
                    "This PDF is encrypted for specific recipients' certificates. This viewer cannot use certificates, so it cannot be opened here.", ex);
            case PdfEncryptedDocumentException e when e.PasswordRequired:
                return string.IsNullOrEmpty(password)
                    ? new ViewerOpenException(ViewerOpenFailure.PasswordRequired, "This PDF is protected by a password.", ex)
                    : new ViewerOpenException(ViewerOpenFailure.WrongPassword, "That password does not open this PDF.", ex);
        }
        if (Unwrap(ex).Any(e => e is PlatformNotSupportedException
                                || e is System.Security.Cryptography.CryptographicException && !PlatformCrypto.CanDecrypt))
        {
            // The only platform-dependent step of opening is decryption. The engine brings its own
            // MD5 and AES where the runtime has none; what remains is certificate (public-key)
            // encryption, which needs RSA and a certificate store.
            return new ViewerOpenException(ViewerOpenFailure.EncryptionNotSupportedHere,
                "This PDF is encrypted with a method this platform's .NET runtime cannot decrypt. The desktop viewer opens it.", ex);
        }
        if (ex is PdfEncryptedDocumentException)
            return new ViewerOpenException(ViewerOpenFailure.NotReadable, "This PDF uses an encryption method the engine does not support.", ex);
        if (Unwrap(ex).Any(e => e is ViewerRangeReadException))
            return new ViewerOpenException(ViewerOpenFailure.NetworkError, "The file could not be read: the network failed or the server refused a range request.", ex);
        if (ex is OperationCanceledException or OutOfMemoryException)
            return null;
        // Hostile or broken input: whatever the parser threw, the file is simply not readable.
        return new ViewerOpenException(ViewerOpenFailure.NotReadable, "This file is not a PDF the engine can read, or it is damaged.", ex);

        static IEnumerable<Exception> Unwrap(Exception? e)
        {
            for (; e != null; e = e.InnerException) yield return e;
        }
    }

    /// <summary>
    /// Renders a page at <paramref name="scale"/> pixels per point (1 is 72 dpi). <paramref name="night"/>
    /// inverts the finished page, as the desktop viewer's night mode does.
    /// </summary>
    public Task<ViewerBitmap> RenderPageAsync(int page, double scale, bool night = false, CancellationToken cancellationToken = default) =>
        RenderPageAsync(page, scale, night, asOpened: false, cancellationToken);

    /// <summary>
    /// The same; with <paramref name="asOpened"/>, the page as the file was opened, without the
    /// reader's annotations and form data (to print without them).
    /// </summary>
    public async Task<ViewerBitmap> RenderPageAsync(int page, double scale, bool night, bool asOpened, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (!(scale > 0) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ranged is { } ranged)
            {
                var bitmap = await ranged.RunAsync(page, (d, ct) => new ValueTask<ViewerBitmap>(RenderCoreAsync(d, page, scale, night, ct)), cancellationToken).ConfigureAwait(false);
                return bitmap;
            }
            return await RenderCoreAsync(asOpened ? _original : _document, page, scale, night, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also after a failure: a page the lazy tree could not place may have changed the page count.
            if (_ranged is { } synced && !_disposed) changed |= SyncRangedPage(synced, page);
            _gate.Release();
            if (changed) PagesChanged?.Invoke();
        }
    }

    private async Task<ViewerBitmap> RenderCoreAsync(PdfVectorDocument document, int page, double scale, bool night, CancellationToken cancellationToken)
    {
        {
            var clock = Stopwatch.StartNew();
            var list = await document.GetPageDisplayListAsync(page, cancellationToken).ConfigureAwait(false);
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
    }

    /// <summary>
    /// Renders the pixels <paramref name="region"/> of the page at <paramref name="scale"/> (a tile
    /// of a page too large for one bitmap). The region is in pixels of the whole page at that scale
    /// and is clipped to it; <see cref="ViewerOptions.MaxBitmapPixels"/> limits the region, not the page.
    /// </summary>
    public async Task<ViewerBitmap> RenderRegionAsync(int page, double scale, PageTile region, bool night = false, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (!(scale > 0) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > _options.MaxBitmapPixels)
            throw new ArgumentOutOfRangeException(nameof(region));
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ranged is { } ranged)
            {
                var bitmap = await ranged.RunAsync(page, (d, ct) => new ValueTask<ViewerBitmap>(RenderRegionCoreAsync(d, page, scale, region, night, ct)), cancellationToken).ConfigureAwait(false);
                return bitmap;
            }
            return await RenderRegionCoreAsync(_document, page, scale, region, night, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Also after a failure: a page the lazy tree could not place may have changed the page count.
            if (_ranged is { } synced && !_disposed) changed |= SyncRangedPage(synced, page);
            _gate.Release();
            if (changed) PagesChanged?.Invoke();
        }
    }

    private async Task<ViewerBitmap> RenderRegionCoreAsync(PdfVectorDocument document, int page, double scale, PageTile region, bool night, CancellationToken cancellationToken)
    {
        {
            var clock = Stopwatch.StartNew();
            var list = await document.GetPageDisplayListAsync(page, cancellationToken).ConfigureAwait(false);
            var build = clock.Elapsed;
            var request = new RenderRequest { PageNumber = page, Dpi = scale * 72 };
            var pixelRegion = new SkiaPixelRegion(region.X, region.Y, region.Width, region.Height);
            var result = await _renderer.RenderAsync(list, request, null, pixelRegion, night, cancellationToken).ConfigureAwait(false);
            using var rendered = result.Page;
            var pixels = MemoryMarshal.TryGetArray(rendered.Pixels, out var segment) && segment.Count == rendered.WidthPixels * 4 * rendered.HeightPixels
                ? segment
                : new ArraySegment<byte>(rendered.Pixels.ToArray());
            return new ViewerBitmap(page, rendered.WidthPixels, rendered.HeightPixels, scale, pixels, build, result.Elapsed, result.Fallbacks);
        }
    }

    /// <summary>Pixels per strip of <see cref="RenderPageInStripsAsync"/> by default: 256 K pixels (1 MB of BGRA), a few milliseconds of drawing.</summary>
    public const int DefaultStripPixels = 256 * 1024;

    /// <summary>
    /// The same bitmap as <see cref="RenderPageAsync(int, double, bool, CancellationToken)"/>, drawn as horizontal
    /// strips of about <paramref name="stripPixels"/> pixels, one call each, so that a single-threaded
    /// host can let the browser handle input between them. <paramref name="between"/> runs after the
    /// display list is built and after each strip (await a timer in it to yield to the browser);
    /// when it returns false (the page left the view), the rest is not drawn and the result is null.
    /// The display list stays cached, so a page stopped this way costs only its drawing when it comes back.
    /// </summary>
    public async Task<ViewerBitmap?> RenderPageInStripsAsync(int page, double scale, bool night, Func<Task<bool>> between,
        int stripPixels = DefaultStripPixels, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (!(scale > 0) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        ArgumentNullException.ThrowIfNull(between);
        stripPixels = Math.Max(16 * 1024, stripPixels);
        // A ranged page not read yet has the first page's size: read its own before the strips are cut.
        if (_ranged != null) await EnsurePageSizeAsync(page, cancellationToken).ConfigureAwait(false);
        if (page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        var (width, height) = PageTiles.PixelSize(Pages[page - 1], scale);
        if ((long)width * height > _options.MaxBitmapPixels)
        {
            scale *= Math.Sqrt((double)_options.MaxBitmapPixels / ((long)width * height)) * 0.999;
            (width, height) = PageTiles.PixelSize(Pages[page - 1], scale);
        }
        if ((long)width * height <= stripPixels)
            return await RenderPageAsync(page, scale, night, cancellationToken).ConfigureAwait(false);

        int stripHeight = Math.Max(8, stripPixels / width);
        for (int round = 0; ; round++)
        {
            var pixels = new byte[(long)width * height * 4];
            var fallbacks = new List<PdfEngine.Vector.PdfFallbackToken>();
            TimeSpan build = TimeSpan.Zero, raster = TimeSpan.Zero;
            for (int y = 0, row = 0; y < height; y += stripHeight, row++)
            {
                var region = new PageTile(0, row, 0, y, width, Math.Min(stripHeight, height - y));
                var strip = await RenderRegionAsync(page, scale, region, night, cancellationToken).ConfigureAwait(false);
                if (strip.Width != width) throw new InvalidOperationException("A strip has another width than its page.");
                strip.Pixels.AsSpan().CopyTo(pixels.AsSpan((int)((long)y * width * 4)));
                build += strip.BuildTime;
                raster += strip.RasterTime;
                foreach (var t in strip.Fallbacks) if (!fallbacks.Contains(t)) fallbacks.Add(t);
                if (!await between().ConfigureAwait(false)) return null;
            }
            // Fonts fetched on demand: the page asked for faces that are not here yet; fetch them and draw again (as RenderPageAsync does).
            if (round < MaxFontRounds && _fonts is OnDemandFontSource lazy && lazy.HasPendingRequests)
            {
                await lazy.LoadRequestedAsync(cancellationToken).ConfigureAwait(false);
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _renderer.Dispose();
                    _renderer = new SkiaVectorRenderer(_options.ImageCacheBytes, _fonts);
                }
                finally
                {
                    _gate.Release();
                }
                continue;
            }
            return new ViewerBitmap(page, width, height, scale, new ArraySegment<byte>(pixels), build, raster, fallbacks);
        }
    }

    /// <summary>The page's text for selection: characters, lines and words on the shown page (empty for a page without text).</summary>
    public async Task<ViewerTextLayout> GetTextLayoutAsync(int page, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ranged is { } ranged)
            {
                var layout = await ranged.RunAsync(page, (d, ct) => ValueTask.FromResult(ViewerTextLayout.FromPage(PdfTextExtractor.LoadPage(d, page, ct))), cancellationToken).ConfigureAwait(false);
                return layout;
            }
            return ViewerTextLayout.FromPage(PdfTextExtractor.LoadPage(_document, page, cancellationToken));
        }
        finally
        {
            // Also after a failure: a page the lazy tree could not place may have changed the page count.
            if (_ranged is { } synced && !_disposed) changed |= SyncRangedPage(synced, page);
            _gate.Release();
            if (changed) PagesChanged?.Invoke();
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
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
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

    // Destinations and actions of the current revision, built on first use.
    private PdfEngine.Vector.Services.PdfNavigation? _navigation;

    /// <summary>
    /// The page's link annotations in /Annots order, with their areas on the shown page and where
    /// they go (empty for a page without links). A document loading by ranges is not completed for
    /// this: only the bytes the page's links need are fetched, as for the page's text.
    /// </summary>
    public async Task<IReadOnlyList<ViewerLink>> GetLinksAsync(int page, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        bool changed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ranged is { } ranged)
            {
                // Each run may be on a fresh parse: its own navigation (named destinations) and geometry.
                return await ranged.RunAsync(page, (d, ct) => ValueTask.FromResult(ReadLinks(d, page, new PdfEngine.Vector.Services.PdfNavigation(d))),
                    cancellationToken).ConfigureAwait(false);
            }
            if (page > _document.PageCount) return [];
            if (_navigation == null || !ReferenceEquals(_navigation.Document, _document))
                _navigation = new PdfEngine.Vector.Services.PdfNavigation(_document);
            return ReadLinks(_document, page, _navigation);
        }
        finally
        {
            // The page is read now: its size and turn are known (as after reading its text).
            if (_ranged is { } synced && !_disposed) changed = SyncRangedPage(synced, page);
            _gate.Release();
            if (changed) PagesChanged?.Invoke();
        }
    }

    private static IReadOnlyList<ViewerLink> ReadLinks(PdfVectorDocument document, int page, PdfEngine.Vector.Services.PdfNavigation navigation)
    {
        if (page > document.PageCount) return [];
        var node = document.PageTree.Pages[page - 1];
        var geometry = new Annotations.ViewerPageGeometry(node.MediaBox, node.CropBox, node.RotationDegrees);
        var links = PdfEngine.Vector.Services.PdfDocumentInfo.ReadLinks(document, page, navigation);
        var result = new List<ViewerLink>(links.Count);
        foreach (var link in links)
        {
            var areas = PdfEngine.Vector.Services.PdfLinkTargets.Areas(link)
                .Select(a => geometry.ToShown(new Annotations.ViewerBox(a.X, a.Y, a.Right, a.Bottom)))
                .ToList();
            if (areas.Count == 0) continue;
            result.Add(new ViewerLink(page, link.Index, areas, PdfEngine.Vector.Services.PdfLinkTargets.Resolve(link, document.PageCount)) { Source = link });
        }
        return result;
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
    internal static ViewerRect ToShown(PdfTextPage text, PdfEngine.Vector.Text.PdfTextRect box)
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

    // ---------------------------------------------------------------- annotations and forms

    /// <summary>The annotations of the file as opened that the viewer manages (see <see cref="Annotations.ViewerAnnotationPdf.FromPdf"/>).</summary>
    public async Task<IReadOnlyList<Annotations.ViewerAnnotation>> ReadAnnotationsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Annotations.ViewerAnnotationPdf.ReadAll(_original);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The widgets of the fillable form fields of the current revision, with their values.</summary>
    public async Task<IReadOnlyList<Annotations.ViewerFormWidget>> GetFormWidgetsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Annotations.ViewerFormWidget.ReadAll(_document, Geometry);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes the current revision the file as opened plus <paramref name="annotations"/> (their
    /// difference from <paramref name="fromFile"/>, the file's own) and <paramref name="formValues"/>,
    /// written by the engine as incremental updates with real annotation dictionaries and appearance
    /// streams, and reads it again: pages, text and search then show the edits. Every call starts
    /// from the bytes as opened, so the file does not grow with the number of edits. Throws
    /// <see cref="PdfEngine.Vector.Forms.PdfFieldValidationException"/> (or an argument, key or
    /// invalid-operation exception) when a form value is refused; nothing changes then. Changes
    /// that the document's signatures or permissions do not allow (<see cref="CanAnnotate"/>,
    /// <see cref="CanFillForms"/>) throw <see cref="InvalidOperationException"/>. A signed file
    /// is only ever changed by an incremental update, so its bytes as opened stay a prefix of
    /// <see cref="Bytes"/>. Returns the form fields written (the values given and the fields
    /// calculated from them).
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplyEditsAsync(IReadOnlyList<Annotations.ViewerAnnotation> fromFile, IReadOnlyList<Annotations.ViewerAnnotation> annotations,
        IReadOnlyList<(string Name, IReadOnlyList<string> Values)> formValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fromFile);
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(formValues);
        await EnsureCompleteAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var opened = new List<PdfVectorDocument>();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!SignaturesAllowEdits) throw new InvalidOperationException(EditRestriction);
            if (formValues.Count > 0 && !CanFillForms) throw new InvalidOperationException(EditRestriction ?? "This document does not allow form filling.");
            var (baseDoc, baseBytes) = await EditBaseAsync(cancellationToken).ConfigureAwait(false);
            byte[] bytes = Annotations.ViewerAnnotationPdf.Write(baseDoc, baseBytes, fromFile, annotations) ?? baseBytes;
            if (!ReferenceEquals(bytes, baseBytes) && !CanAnnotate)
                throw new InvalidOperationException(EditRestriction ?? "This document does not allow annotations.");
            PdfVectorDocument? doc = ReferenceEquals(bytes, baseBytes) ? baseDoc : null;
            IReadOnlyList<string> filled = [];
            if (formValues.Count > 0)
            {
                if (doc == null) opened.Add(doc = await OpenRevisionAsync(bytes, cancellationToken).ConfigureAwait(false));
                var (withValues, changed) = PdfEngine.Vector.Services.PdfFormService.Fill(doc, bytes, formValues, validate: true);
                filled = changed;
                if (!ReferenceEquals(withValues, bytes))
                {
                    bytes = withValues;
                    doc = null;
                }
            }
            if (doc == null) opened.Add(doc = await OpenRevisionAsync(bytes, cancellationToken).ConfigureAwait(false));

            var old = _document;
            _document = doc;
            _bytes = bytes;
            opened.Remove(doc);
            if (!ReferenceEquals(old, doc) && !ReferenceEquals(old, _original) && !ReferenceEquals(old, _editBase?.Document))
            {
                PdfTextExtractor.Invalidate(old);
                old.Dispose();
            }
            return filled;
        }
        finally
        {
            foreach (var d in opened) d.Dispose();
            _gate.Release();
        }
    }

    private async Task<(PdfVectorDocument Document, byte[] Bytes)> EditBaseAsync(CancellationToken cancellationToken)
    {
        if (_editBase is { } known) return known;
        if (!_original.WasRepaired) return (_original, _originalBytes);
        byte[] whole = PdfEngine.Vector.Services.PdfFullWriter.Rewrite(_original);
        var doc = await OpenRevisionAsync(whole, cancellationToken).ConfigureAwait(false);
        _editBase = (doc, whole);
        return (doc, whole);
    }

    private async Task<PdfVectorDocument> OpenRevisionAsync(byte[] bytes, CancellationToken cancellationToken) =>
        await PdfVectorDocument.OpenAsync(bytes, "Document.pdf", password: _password, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _renderer.Dispose();
            if (_ranged is { } ranged)
            {
                PdfTextExtractor.Invalidate(ranged.Document);
                ranged.Dispose();
            }
            foreach (var d in new[] { _document, _editBase?.Document, _original }.Distinct())
            {
                if (d == null) continue;
                PdfTextExtractor.Invalidate(d);
                d.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
