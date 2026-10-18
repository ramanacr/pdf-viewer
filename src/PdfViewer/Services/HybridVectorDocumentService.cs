using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Diagnostics;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Windows;
using PdfViewer.Core.Security;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// The document service the viewer ships with. Every page, tile, print and export image is drawn
/// by the vector engine (Direct2D, or the WPF renderer when no Direct3D device exists); content
/// the engine cannot reproduce exactly is drawn by its best effort, never by another renderer
/// (M9, 06_PDFIUM_FALLBACK "Removal sequence"). In Auto and Vector modes the engine also opens the
/// document (passwords, repair, permissions, certificate encryption) and serves every document
/// service (M10). The published app has no PDFium at all. In developer and test builds
/// (<see cref="PdfDocumentServiceFactory.IsPdfiumRendererAvailable"/>) <see cref="PdfEngineMode.Pdfium"/>
/// mode still opens and renders whole pages with PDFium, as the oracle, and Auto mode reads the
/// text of a document the engine cannot parse with PDFium.
/// </summary>
public sealed class HybridVectorDocumentService : IPdfDocumentService
{
    private readonly object _pdfiumGate = new();
    private string _filePath = string.Empty;
    // The current revision: the file as read, the latest form fill, or the decrypted copy of a
    // certificate-encrypted file.
    private byte[]? _bytes;

#if PDFIUM_RENDERER
    // PDFium (developer and test builds only): created and opened on first use, from the current
    // revision (_bytes). PDFium mode opens it with the document; in Auto mode only text extraction
    // reaches it, and only when the engine could not parse the document.
    private PdfiumDocumentService? _pdfium;
    // _pdfium holds the current revision (cleared by every reload).
    private bool _pdfiumCurrent;

    /// <summary>
    /// PDFium with the current revision open, opened now if it is not: with the form-fill
    /// environment when it renders pages (PDFium mode), without it for text. Every remaining use
    /// in Auto and Vector modes is text extraction and search.
    /// </summary>
    private PdfiumDocumentService _pdfiumService
    {
        get
        {
            lock (_pdfiumGate)
            {
                _pdfium ??= new PdfiumDocumentService(_securityPolicy);
                bool forms = UsePdfiumPages;
                if (_bytes != null && (!_pdfiumCurrent || (forms && !_pdfium.DrawsFormFields)))
                {
                    _pdfium.OpenFromMemory(_bytes, _filePath, IsDecryptedCopy ? null : _password, drawFormFields: forms);
                    _pdfiumCurrent = true;
                }
                return _pdfium;
            }
        }
    }

    /// <summary>True when PDFium has a document open (it is only opened when something needs it).</summary>
    public bool IsPdfiumDocumentOpen => _pdfium?.IsDocumentLoaded == true;
#else
    /// <summary>Always false: this build has no PDFium.</summary>
    public bool IsPdfiumDocumentOpen => false;
#endif
    private readonly PdfSecurityPolicy _securityPolicy;
    private readonly WindowsVectorRenderer _vectorRenderer;

    /// <summary>
    /// Direct2D/DirectWrite rasterizer (ADR-005): GPU (or WARP) rendering of pages and viewport
    /// regions. Null only if no Direct3D device can be created; the WPF renderer then stands in.
    /// </summary>
    private readonly Direct2DVectorRenderer? _d2d;

    /// <summary>How vector pages reach the screen.</summary>
    public enum VectorHostMode
    {
        /// <summary>Direct2D bitmaps: page bitmap plus a viewport detail tile at device resolution (default).</summary>
        Direct2DTiles,
        /// <summary>Frozen WPF drawings stretched by WPF (the earlier live-surface host).</summary>
        WpfDrawing,
    }

    /// <summary>Host mode; PDF_VECTOR_HOST=wpf selects the WPF drawing host.</summary>
    public VectorHostMode HostMode { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("PDF_VECTOR_HOST"), "wpf", StringComparison.OrdinalIgnoreCase)
            ? VectorHostMode.WpfDrawing
            : VectorHostMode.Direct2DTiles;

    /// <summary>True when the Direct2D rasterizer is active (false: WPF fallback renderer).</summary>
    public bool IsDirect2DActive => _d2d != null;
    private readonly ConcurrentDictionary<int, PageEngineReport> _pageEngineReports = new();
    private PdfEngineMetrics _metrics = new();
    private PdfVectorDocument? _vectorDoc;
    private bool _vectorOpenAttempted;
    // The document password, kept for the lazy vector open (engine switch) and cleared on close.
    private string? _password;
    private readonly SemaphoreSlim _vectorOpenGate = new(1, 1);
    private PdfEngineMode _mode;
    private bool _disposed;

    /// <summary>Document information, bookmarks and annotations without PDFium (M9).</summary>
    private readonly NativeDocumentServices _native = new();

    /// <summary>
    /// The vector parse that serves the document-level services in Auto and Vector modes; null in
    /// PDFium mode or when the vector core could not open the file (PDFium then serves them).
    /// </summary>
    private PdfVectorDocument? NativeDocument => _mode != PdfEngineMode.Pdfium ? _vectorDoc : null;

    public PdfEngineMode EngineMode
    {
        get => _mode;
        set
        {
            value = PdfDocumentServiceFactory.Normalize(value);
            if (_mode != value)
            {
                ClearSurfaces();
                _pageEngineReports.Clear();
            }
            _mode = value;
            ApplyModeToRenderers();
        }
    }

    /// <summary>Whole pages come from PDFium: <see cref="PdfEngineMode.Pdfium"/> in a build that has the PDFium renderer.</summary>
#if PDFIUM_RENDERER
    private bool UsePdfiumPages => _mode == PdfEngineMode.Pdfium;
#else
    private const bool UsePdfiumPages = false;
#endif

    /// <summary>Strict vector mode outlines what the engine cannot reproduce; the other modes draw it by best effort.</summary>
    private void ApplyModeToRenderers()
    {
        bool strict = _mode == PdfEngineMode.Vector;
        _vectorRenderer.OutlineUnsupportedRegions = strict;
        if (_d2d != null)
            _d2d.OutlineUnsupportedRegions = strict;
    }

    /// <summary>Resolution of fallback regions on a live WPF surface (the engine draws them; no provider is used).</summary>
    internal const double SurfaceFallbackDpi = 200;

    /// <summary>
    /// Pages with more commands than this are not given a live WPF drawing (WPF re-renders a live
    /// drawing on every frame, which stops being cheaper than one raster for very dense pages):
    /// they are shown as Direct2D bitmaps with detail tiles, like every page in the default host.
    /// </summary>
    internal const int MaxLiveSurfaceCommands = 40_000;

    private const int SurfaceCacheCapacity = 24;
    private readonly object _surfaceSync = new();
    private readonly LinkedList<((int Page, int Rotation, bool Night) Key, ImageSource Surface)> _surfaceLru = new();
    private readonly Dictionary<(int Page, int Rotation, bool Night), LinkedListNode<((int Page, int Rotation, bool Night) Key, ImageSource Surface)>> _surfaces = new();

    private void ClearSurfaces()
    {
        lock (_surfaceSync)
        {
            _surfaceLru.Clear();
            _surfaces.Clear();
        }
    }

    public async Task<ImageSource?> GetVectorPageSurfaceAsync(int pageNumber, int rotationAngle = 0, CancellationToken ct = default, bool nightMode = false)
    {
        if (UsePdfiumPages || HostMode != VectorHostMode.WpfDrawing)
            return null;

        int rotation = (((rotationAngle % 360) + 360) % 360) / 90 * 90;
        var key = (pageNumber, rotation, nightMode);
        lock (_surfaceSync)
        {
            if (_surfaces.TryGetValue(key, out var node))
            {
                _surfaceLru.Remove(node);
                _surfaceLru.AddFirst(node);
                return node.Value.Surface;
            }
        }

        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        var doc = _vectorDoc;
        if (doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
            return null;

        var buildClock = System.Diagnostics.Stopwatch.StartNew();
        IPdfDisplayList displayList;
        try
        {
            displayList = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
            PrefetchText(doc, pageNumber);
        }
        catch (Exception ex) when (_mode != PdfEngineMode.Vector && ex is not OperationCanceledException)
        {
            return null; // the bitmap path classifies and reports it
        }
        buildClock.Stop();

        bool strict = _mode == PdfEngineMode.Vector;
        if (displayList.Commands.Count > MaxLiveSurfaceCommands)
            return null;

        PdfVectorSurfaceResult result;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // No fallback provider: what the engine cannot reproduce exactly is drawn by its best effort.
            result = await _vectorRenderer.BuildPageSurfaceAsync(displayList, (PageRotation)rotation,
                null, SurfaceFallbackDpi, ct, invertColors: nightMode).ConfigureAwait(false);
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException)
        {
            return null; // the bitmap path draws the page
        }
        clock.Stop();

        var metrics = new PdfPageMetrics
        {
            PageNumber = pageNumber,
            IsFullyVector = result.Fallbacks.Count == 0,
            DrawCommandsCount = displayList.Commands.Count,
            FallbackCommandsCount = result.Fallbacks.Count,
            FallbackAreaRatio = AreaRatio(displayList, result.Fallbacks),
            ParseDurationMs = buildClock.Elapsed.TotalMilliseconds,
            RenderDurationMs = clock.Elapsed.TotalMilliseconds,
        };
        metrics.FallbackReasons.AddRange(result.Fallbacks.Select(t => t.Reason));
        _metrics.RecordPage(metrics);
        _pageEngineReports[pageNumber] = VectorReport(pageNumber, displayList, result.Fallbacks, strict, live: true);

        lock (_surfaceSync)
        {
            if (!_surfaces.ContainsKey(key))
            {
                _surfaces[key] = _surfaceLru.AddFirst((key, result.Surface));
                while (_surfaceLru.Count > SurfaceCacheCapacity)
                {
                    var last = _surfaceLru.Last!;
                    _surfaceLru.RemoveLast();
                    _surfaces.Remove(last.Value.Key);
                }
            }
        }
        return result.Surface;
    }

    public PageEngineReport? GetPageEngineReport(int pageNumber)
    {
        if (_pageEngineReports.TryGetValue(pageNumber, out var report))
        {
            return report;
        }

#if PDFIUM_RENDERER
        if (UsePdfiumPages)
        {
            return new PageEngineReport(
                pageNumber,
                PdfEngineMode.Pdfium,
                IsFallback: false,
                FallbackReasons: Array.Empty<string>(),
                BadgeText: "🖼️ PDFium",
                Tooltip: "Rendered via Google PDFium software rasterizer [Mode: PDFium, developer build]");
        }
#endif

        return new PageEngineReport(
            pageNumber,
            _mode,
            IsFallback: false,
            FallbackReasons: Array.Empty<string>(),
            BadgeText: _mode == PdfEngineMode.Vector ? "⚡ Vector" : "⚡ Vector (Auto)",
            Tooltip: "Vector engine (page not rendered yet)");
    }

    public bool IsDocumentLoaded => _vectorDoc != null || _bytes != null;
    public string CurrentFilePath => _filePath;
#if PDFIUM_RENDERER
    public int PageCount => _vectorDoc?.PageCount ?? (_bytes != null && UsePdfiumPages ? _pdfiumService.PageCount : 0);
#else
    public int PageCount => _vectorDoc?.PageCount ?? 0;
#endif

    public HybridVectorDocumentService(
        PdfSecurityPolicy securityPolicy,
        PdfEngineMode mode = PdfEngineMode.Auto)
    {
        _securityPolicy = securityPolicy ?? PdfSecurityPolicy.DefaultStrict;
        _vectorRenderer = new WindowsVectorRenderer();
        try
        {
            _d2d = new Direct2DVectorRenderer();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _d2d = null; // no Direct3D device at all: the WPF renderer rasterizes instead
        }
        _mode = PdfDocumentServiceFactory.Normalize(mode);
        ApplyModeToRenderers();
    }

    /// <summary>Local engine diagnostics for the open document (never transmitted).</summary>
    public PdfEngineMetrics EngineMetrics => _metrics;

    /// <summary>True when the vector core parsed the open document.</summary>
    public bool IsVectorDocumentOpen => _vectorDoc != null;

    public async Task<DocumentMetadata> OpenDocumentAsync(
        string filePath,
        string? password = null,
        CancellationToken ct = default)
    {
#if PDFIUM_RENDERER
        if (UsePdfiumPages)
        {
            _metrics = new PdfEngineMetrics();
            ClearSurfaces();
            _pageEngineReports.Clear();
            IsDecryptedCopy = false;
            return await OpenInPdfiumModeAsync(filePath, password, ct).ConfigureAwait(false);
        }
#endif

        // Auto and Vector: the engine opens the file; PDFium is not involved (M10).
        if (!System.IO.File.Exists(filePath))
            throw new System.IO.FileNotFoundException($"File not found: {filePath}", filePath);
        _securityPolicy.EnsureDocumentSizeAllowed(new System.IO.FileInfo(filePath).Length, filePath);
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var vector = await OpenNativeAsync(bytes, filePath, password, ct).ConfigureAwait(false);

        byte[] revision = bytes;
        bool decryptedCopy = false;
        if (vector.Security is PdfEngine.Vector.Security.PdfPublicKeySecurityHandler)
        {
            // Certificate (public-key) encryption: the engine opened it with the user's
            // certificates. The services work on an in-memory decrypted copy, which never
            // replaces the original.
            revision = PdfDecryptedCopyWriter.Write(vector);
            decryptedCopy = true;
        }

        // The document that was open stays open until this one has been read.
        ResetDocument();
        _metrics = new PdfEngineMetrics();
        ClearSurfaces();
        _pageEngineReports.Clear();
        _password = password;
        _filePath = filePath;
        _bytes = revision;
        _vectorDoc = vector;
        _vectorOpenAttempted = true;
        IsDecryptedCopy = decryptedCopy;
        if (vector.WasRepaired)
            _metrics.RecordRecovery();
        return NativeDocumentServices.Metadata(vector, filePath);
    }

    /// <summary>
    /// The engine's parse of a file, with its errors in the viewer's terms: a password prompt for
    /// a wrong or missing password, the certificate message for a recipient-encrypted file, and a
    /// damaged-file error for anything that cannot be read (damaged cross-reference data is
    /// rebuilt first, as PDFium does).
    /// </summary>
    private static async Task<PdfVectorDocument> OpenNativeAsync(byte[] bytes, string filePath, string? password, CancellationToken ct)
    {
        try
        {
            return await PdfVectorDocument.OpenAsync(bytes, filePath, cancellationToken: ct, password: password).ConfigureAwait(false);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
        {
            throw new PdfEngine.Exceptions.PdfPasswordRequiredException(ex.Message, filePath);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.CertificateRequired)
        {
            throw new InvalidOperationException(
                "This document is encrypted for specific recipients, and none of the certificates installed for your account can open it.", ex);
        }
        catch (PdfEncryptedDocumentException ex)
        {
            throw new InvalidOperationException($"This document's encryption is not supported: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is PdfVectorException or System.IO.InvalidDataException or FormatException)
        {
            throw new PdfEngine.Exceptions.PdfCorruptDocumentException($"The file is not a readable PDF document ({ex.Message}).", ex, filePath);
        }
    }

#if PDFIUM_RENDERER
    /// <summary>
    /// PDFium mode (developer and test builds): PDFium opens the file and renders the pages; the
    /// vector document is opened lazily if the engine changes. Certificate-encrypted files, which
    /// PDFium cannot open, are opened by the engine and given to PDFium as a decrypted copy.
    /// </summary>
    private async Task<DocumentMetadata> OpenInPdfiumModeAsync(string filePath, string? password, CancellationToken ct)
    {
        ResetDocument();
        _pdfium ??= new PdfiumDocumentService(_securityPolicy);
        DocumentMetadata meta;
        try
        {
            meta = await _pdfium.OpenDocumentAsync(filePath, password, ct).ConfigureAwait(false);
        }
        catch (PdfUnsupportedSecurityException)
        {
            return await OpenWithVectorSecurityAsync(filePath, password, ct).ConfigureAwait(false);
        }
        _password = password;
        _filePath = filePath;
        _bytes = _pdfium.CurrentBytes;
        _pdfiumCurrent = true;
        return meta;
    }
#endif

    /// <summary>Forgets the open document (both engines) before another one is opened.</summary>
    private void ResetDocument()
    {
        var old = _vectorDoc;
        _vectorDoc = null;
        old?.Dispose();
        _vectorOpenAttempted = false;
        _password = null;
        _bytes = null;
        _filePath = string.Empty;
#if PDFIUM_RENDERER
        lock (_pdfiumGate)
        {
            _pdfiumCurrent = false;
            _pdfium?.CloseDocument();
        }
#endif
    }

    public bool IsDecryptedCopy { get; private set; }

    public byte[]? CurrentBytes => _bytes;

    /// <summary>A new revision of the open document (a form fill): the engine re-reads it, PDFium on its next use; caches are dropped.</summary>
    public async Task ReloadFromBytesAsync(byte[] bytes, CancellationToken ct = default)
    {
        _securityPolicy.EnsureDocumentSizeAllowed(bytes.Length, _filePath);
        lock (_pdfiumGate)
        {
            _bytes = bytes;
#if PDFIUM_RENDERER
            _pdfiumCurrent = false;
#endif
        }
#if PDFIUM_RENDERER
        if (UsePdfiumPages)
            _ = _pdfiumService; // PDFium renders the pages: it reads the new revision now
#endif
        _metrics = new PdfEngineMetrics();
        ClearSurfaces();
        _pageEngineReports.Clear();
        var old = _vectorDoc;
        _vectorDoc = null;
        _vectorOpenAttempted = false;
        old?.Dispose();
        if (!UsePdfiumPages)
        {
            try
            {
                _vectorDoc = await PdfVectorDocument.OpenAsync(bytes, CurrentFilePath, cancellationToken: ct, password: IsDecryptedCopy ? null : _password).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _vectorDoc = null; // its pages are reported as unreadable
                _metrics.DocumentFallbackReason = ex is PdfVectorException pve ? pve.Kind.ToString() : nameof(PdfFallbackReason.MalformedContentRecovery);
            }
            _vectorOpenAttempted = true;
        }
    }

    /// <summary>The engine's security handler (the open document's permissions); PDFium's view only in PDFium mode.</summary>
    public PdfEngine.Documents.PdfDocumentPermissions Permissions =>
        _vectorDoc?.Security is { } security
            ? PdfEngine.Documents.PdfDocumentPermissions.FromFlags(security.Permissions, security.Revision, security.IsOwner)
            : IsDecryptedCopy ? PdfEngine.Documents.PdfDocumentPermissions.Owner
            : _vectorDoc != null ? PdfEngine.Documents.PdfDocumentPermissions.Unencrypted
#if PDFIUM_RENDERER
            : IsPdfiumDocumentOpen ? _pdfium!.Permissions
#endif
            : PdfEngine.Documents.PdfDocumentPermissions.Unencrypted;

#if PDFIUM_RENDERER
    private async Task<DocumentMetadata> OpenWithVectorSecurityAsync(string filePath, string? password, CancellationToken ct)
    {
        _metrics = new PdfEngineMetrics();
        ClearSurfaces();
        _pageEngineReports.Clear();
        var vector = await OpenNativeAsync(await System.IO.File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false), filePath, password, ct).ConfigureAwait(false);
        byte[] copy = PdfEngine.Vector.Document.PdfDecryptedCopyWriter.Write(vector);
        DocumentMetadata meta;
        try
        {
            meta = await _pdfium!.OpenDocumentFromBytesAsync(copy, filePath, ct).ConfigureAwait(false);
        }
        catch
        {
            vector.Dispose();
            throw;
        }
        _password = password;
        _filePath = filePath;
        _bytes = copy;
        _pdfiumCurrent = true;
        _vectorDoc?.Dispose();
        _vectorDoc = vector;
        _vectorOpenAttempted = true;
        IsDecryptedCopy = true;
        return meta;
    }
#endif

    /// <summary>Opens the vector document lazily when the file was opened in PDFium mode and the engine changed since.</summary>
    private async Task EnsureVectorDocumentAsync(CancellationToken ct)
    {
        if (UsePdfiumPages || _vectorDoc != null || _vectorOpenAttempted || _bytes == null)
            return;

        await _vectorOpenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_vectorDoc == null && !_vectorOpenAttempted)
                await OpenVectorDocumentAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _vectorOpenGate.Release();
        }
    }

    private async Task OpenVectorDocumentAsync(CancellationToken ct)
    {
        _vectorOpenAttempted = true;
        try
        {
            // The revision in memory, with the password it was opened with (a decrypted copy needs none).
            _vectorDoc = await PdfVectorDocument.OpenAsync(_bytes!, _filePath, cancellationToken: ct, password: IsDecryptedCopy ? null : _password).ConfigureAwait(false);
            if (_vectorDoc.WasRepaired)
                _metrics.RecordRecovery();
        }
        catch (PdfEncryptedDocumentException)
        {
            // A security handler the engine does not have: its pages are reported as unreadable.
            _vectorDoc = null;
            _metrics.DocumentFallbackReason = nameof(PdfFallbackReason.EncryptedContent);
        }
        catch (Exception ex) when (_mode != PdfEngineMode.Vector && ex is not OperationCanceledException)
        {
            // Document-level parse failure: classified; its pages are reported as unreadable.
            _vectorDoc = null;
            _metrics.DocumentFallbackReason = ex is PdfVectorException pve ? pve.Kind.ToString() : nameof(PdfFallbackReason.MalformedContentRecovery);
        }
    }

    public void CloseDocument()
    {
        ClearSurfaces();
        IsDecryptedCopy = false;
        _pageEngineReports.Clear();
        ResetDocument();
    }

    public DocumentMetadata? GetMetadata() =>
        NativeDocument is { } native ? NativeDocumentServices.Metadata(native, CurrentFilePath)
#if PDFIUM_RENDERER
        : _bytes != null && UsePdfiumPages ? _pdfiumService.GetMetadata()
#endif
        : null;

    public (double Width, double Height) GetPageDimensions(int pageNumber)
    {
        if (_vectorDoc != null && pageNumber >= 1 && pageNumber <= _vectorDoc.PageCount)
        {
            var pageNode = _vectorDoc.PageTree.Pages[pageNumber - 1];
            return (pageNode.PageSize.Width, pageNode.PageSize.Height);
        }
#if PDFIUM_RENDERER
        if (_bytes != null && UsePdfiumPages)
            return _pdfiumService.GetPageDimensions(pageNumber);
#endif
        return (0, 0);
    }

    public int GetPageIntrinsicRotation(int pageNumber)
    {
        if (_vectorDoc != null && pageNumber >= 1 && pageNumber <= _vectorDoc.PageCount)
        {
            var pageNode = _vectorDoc.PageTree.Pages[pageNumber - 1];
            return pageNode.RotationDegrees;
        }
#if PDFIUM_RENDERER
        if (_bytes != null && UsePdfiumPages)
            return _pdfiumService.GetPageIntrinsicRotation(pageNumber);
#endif
        return 0;
    }

    public BitmapSource? RenderPage(int pageNumber, int dpi = 150, int rotationAngle = 0)
    {
        return RenderPageAsync(pageNumber, dpi, rotationAngle).GetAwaiter().GetResult();
    }

    public Task<BitmapSource?> RenderPageAsync(
        int pageNumber,
        int dpi = 150,
        int rotationAngle = 0,
        CancellationToken ct = default) =>
        RenderPageCoreAsync(pageNumber, dpi, rotationAngle, invert: false, ct);

    /// <summary>
    /// The night-mode page: drawn as by day and inverted on the GPU by a Direct2D Invert effect
    /// (the same pass detail tiles use), so night mode needs no CPU pass over the bitmap.
    /// </summary>
    public Task<BitmapSource?> RenderPageInvertedAsync(int pageNumber, int dpi, int rotationAngle, CancellationToken ct = default) =>
        RenderPageCoreAsync(pageNumber, dpi, rotationAngle, invert: true, ct);

    /// <summary>
    /// A page being drawn has its text built in the background (low priority, bounded, after the
    /// drawing), so that selecting, copying or searching on it does not wait for the text.
    /// </summary>
    private static void PrefetchText(PdfVectorDocument doc, int pageNumber) =>
        PdfEngine.Vector.Text.PdfTextExtractor.Prefetch(doc, pageNumber);

    private async Task<BitmapSource?> RenderPageCoreAsync(int pageNumber, int dpi, int rotationAngle, bool invert, CancellationToken ct)
    {
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);

#if PDFIUM_RENDERER
        if (UsePdfiumPages)
        {
            _pageEngineReports[pageNumber] = PdfiumReport(pageNumber);
            var pdfiumPage = await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
            return invert && pdfiumPage != null ? NightModeImage.Invert(pdfiumPage) : pdfiumPage;
        }
#endif

        var doc = _vectorDoc;
        if (doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
        {
            _pageEngineReports[pageNumber] = UnreadableReport(pageNumber,
                $"• Document: {_metrics.DocumentFallbackReason ?? "not parsed by the vector engine"}", "the document could not be read");
            return BlankPage(pageNumber, dpi, rotationAngle, invert);
        }

        var buildClock = System.Diagnostics.Stopwatch.StartNew();
        IPdfDisplayList displayList;
        try
        {
            displayList = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
            PrefetchText(doc, pageNumber);
        }
        catch (Exception ex) when (_mode != PdfEngineMode.Vector && ex is not OperationCanceledException)
        {
            RecordUnreadable(pageNumber, 0, PdfFallbackReason.InternalCompatibilityGuard);
            _pageEngineReports[pageNumber] = UnreadableReport(pageNumber,
                $"• {PdfFallbackReason.InternalCompatibilityGuard}: display list build failed ({ex.GetType().Name})", "the page could not be read");
            return BlankPage(pageNumber, dpi, rotationAngle, invert);
        }
        buildClock.Stop();

        // Same raster ceiling for every page: a hostile MediaBox at high zoom must not allocate an
        // unbounded bitmap.
        var (pixelW, pixelH) = PixelSize(displayList, dpi, rotationAngle);
        _securityPolicy.EnsureRenderDimensionsAllowed(pixelW, pixelH);

        bool strict = _mode == PdfEngineMode.Vector;
        var request = new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = dpi,
            Rotation = (PageRotation)((((rotationAngle % 360) + 360) % 360) / 90 * 90),
        };

        RasterResult result;
        try
        {
            // Without Direct2D (the WPF renderer) the night page is the day page inverted after.
            result = await RasterizeAsync(displayList, request, null, invert && _d2d != null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException and not PdfEngine.Exceptions.PdfSecurityPolicyException)
        {
            RecordUnreadable(pageNumber, buildClock.Elapsed.TotalMilliseconds, PdfFallbackReason.InternalCompatibilityGuard);
            _pageEngineReports[pageNumber] = UnreadableReport(pageNumber,
                $"• {PdfFallbackReason.InternalCompatibilityGuard}: vector renderer failed ({ex.GetType().Name})", "the page could not be drawn");
            return BlankPage(pageNumber, dpi, rotationAngle, invert);
        }

        using var renderedPage = result.Page;
        var metrics = new PdfPageMetrics
        {
            PageNumber = pageNumber,
            IsFullyVector = result.Fallbacks.Count == 0,
            DrawCommandsCount = displayList.Commands.Count,
            FallbackCommandsCount = result.Fallbacks.Count,
            FallbackAreaRatio = AreaRatio(displayList, result.Fallbacks),
            ParseDurationMs = buildClock.Elapsed.TotalMilliseconds,
            RenderDurationMs = result.Elapsed.TotalMilliseconds,
        };
        metrics.FallbackReasons.AddRange(result.Fallbacks.Select(t => t.Reason));
        _metrics.RecordPage(metrics);

        _pageEngineReports[pageNumber] = VectorReport(pageNumber, displayList, result.Fallbacks, strict, live: false);
        var bitmap = CreateBitmapSource(renderedPage);
        return invert && _d2d == null ? NightModeImage.Invert(bitmap) : bitmap;
    }

    /// <summary>
    /// A white page of the page's size, for a page the engine cannot read at all (the report says
    /// why). The page is never left as an empty rectangle and nothing is handed to another renderer.
    /// </summary>
    private BitmapSource BlankPage(int pageNumber, int dpi, int rotationAngle, bool invert = false)
    {
        var (w, h) = GetPageDimensions(pageNumber);
        if (w <= 0 || h <= 0) (w, h) = (612, 792);
        int total = ((GetPageIntrinsicRotation(pageNumber) + rotationAngle) % 360 + 360) % 360;
        if (total is 90 or 270) (w, h) = (h, w);
        int pw = (int)Math.Max(1, Math.Round(w * dpi / 72.0)), ph = (int)Math.Max(1, Math.Round(h * dpi / 72.0));
        _securityPolicy.EnsureRenderDimensionsAllowed(pw, ph);
        var pixels = new byte[pw * ph * 4];
        Array.Fill(pixels, (byte)0xFF);
        if (invert)
            for (int i = 0; i < pixels.Length; i += 4) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        var bitmap = BitmapSource.Create(pw, ph, dpi, dpi, PixelFormats.Pbgra32, null, pixels, pw * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private readonly record struct RasterResult(RenderedPage Page, IReadOnlyList<PdfFallbackToken> Fallbacks, int FallbackRegionsComposited, TimeSpan Elapsed);

    /// <summary>
    /// Direct2D when available; the WPF renderer otherwise (full page only). No fallback provider
    /// is passed: what the engine cannot reproduce exactly is drawn by its best effort.
    /// </summary>
    private async Task<RasterResult> RasterizeAsync(IPdfDisplayList list, RenderRequest request,
        PixelRegion? region, bool invert, CancellationToken ct)
    {
        if (_d2d != null)
        {
            var r = await _d2d.RenderAsync(list, request, null, region, invert, ct).ConfigureAwait(false);
            return new RasterResult(r.Page, r.Fallbacks, r.FallbackRegionsComposited, r.Elapsed);
        }
        if (region != null || invert)
            throw new NotSupportedException("Region and inverted rasterization need the Direct2D renderer.");
        var w = await _vectorRenderer.RenderAsync(list, request, null, ct).ConfigureAwait(false);
        return new RasterResult(w.Page, w.Fallbacks, w.FallbackRegionsComposited, w.CompileTime + w.RasterTime);
    }

    /// <summary>
    /// Renders a pixel window of the page at <paramref name="pixelsPerPoint"/> — the visible part of
    /// a zoomed page at exact device resolution (Direct2D host). Null when the vector path is not
    /// used for this page (the caller keeps the page bitmap).
    /// </summary>
    public async Task<BitmapSource?> RenderPageRegionAsync(int pageNumber, int rotationAngle, double pixelsPerPoint,
        int x, int y, int width, int height, bool nightMode, CancellationToken ct = default)
    {
        const int MaxSide = 8192;
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || pixelsPerPoint <= 0)
            return null;

        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
#if PDFIUM_RENDERER
        if (UsePdfiumPages)
        {
            // PDFium pages (developer builds) get the same viewport detail from PDFium itself.
            var bmp = await Task.Run(() => _pdfiumService.RenderPageRegion(pageNumber, pixelsPerPoint * 72.0, rotationAngle, x, y, width, height), ct).ConfigureAwait(false);
            return bmp != null && nightMode ? NightModeImage.Invert(bmp) : bmp;
        }
#endif
        var doc = _vectorDoc;
        if (doc == null || _d2d == null || pageNumber < 1 || pageNumber > doc.PageCount)
            return null; // the page bitmap stays: no detail tiles without Direct2D or a readable page

        var list = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
        PrefetchText(doc, pageNumber);
        var request = new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = pixelsPerPoint * 72.0,
            Rotation = (PageRotation)((((rotationAngle % 360) + 360) % 360) / 90 * 90),
        };
        var result = await RasterizeAsync(list, request, new PixelRegion(x, y, width, height), nightMode, ct).ConfigureAwait(false);
        using var page = result.Page;
        return CreateBitmapSource(page);
    }

    public async Task<PdfEngine.Vector.Direct2D.SharedTexture?> RenderPageRegionToGpuAsync(int pageNumber, int rotationAngle, double pixelsPerPoint,
        int x, int y, int width, int height, bool nightMode, CancellationToken ct = default)
    {
        if (width <= 0 || height <= 0 || pixelsPerPoint <= 0 || _d2d == null || UsePdfiumPages)
            return null;
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        var doc = _vectorDoc;
        if (doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
            return null;
        var list = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
        PrefetchText(doc, pageNumber);
        _securityPolicy.EnsureRenderDimensionsAllowed(width, height);
        var request = new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = pixelsPerPoint * 72.0,
            Rotation = (PageRotation)((((rotationAngle % 360) + 360) % 360) / 90 * 90),
        };
        return await _d2d.RenderToSharedTextureAsync(list, request, null,
            new PixelRegion(x, y, width, height), nightMode, ct).ConfigureAwait(false);
    }

    public async Task PreparePageRegionAsync(int pageNumber, int rotationAngle, double pixelsPerPoint,
        int x, int y, int width, int height, bool nightMode, CancellationToken ct = default)
    {
        if (width <= 0 || height <= 0 || pixelsPerPoint <= 0 || _d2d == null || UsePdfiumPages)
            return;
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        var doc = _vectorDoc;
        if (doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
            return;
        var list = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
        PrefetchText(doc, pageNumber);
        _securityPolicy.EnsureRenderDimensionsAllowed(width, height);
        var request = new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = pixelsPerPoint * 72.0,
            Rotation = (PageRotation)((((rotationAngle % 360) + 360) % 360) / 90 * 90),
        };
        await _d2d.PrepareAsync(list, request, new PixelRegion(x, y, width, height), nightMode, ct).ConfigureAwait(false);
    }

    private static (int Width, int Height) PixelSize(IPdfDisplayList list, int dpi, int rotationAngle)
    {
        int total = (((list.RotationDegrees + rotationAngle) % 360) + 360) % 360;
        var crop = list.CropBox;
        bool sideways = total is 90 or 270;
        double w = sideways ? crop.Height : crop.Width;
        double h = sideways ? crop.Width : crop.Height;
        return ((int)Math.Max(1, Math.Round(w * dpi / 72.0)), (int)Math.Max(1, Math.Round(h * dpi / 72.0)));
    }

    private static double AreaRatio(IPdfDisplayList list, IReadOnlyList<PdfFallbackToken> tokens)
    {
        if (tokens.Count == 0) return 0;
        var merged = new PdfDisplayList(list.PageNumber, list.PageSize, list.MediaBox, list.CropBox, list.RotationDegrees,
            Array.Empty<PdfDrawCommand>(), list.Features, tokens);
        return merged.ComputeFallbackAreaRatio();
    }

    /// <summary>A page the engine could not read or draw at all (it is shown as a white page).</summary>
    private void RecordUnreadable(int pageNumber, double buildMs, params PdfFallbackReason[] reasons)
    {
        var metrics = new PdfPageMetrics
        {
            PageNumber = pageNumber,
            IsFullyVector = false,
            IsFullPageFallback = true,
            FallbackAreaRatio = 1.0,
            ParseDurationMs = buildMs,
        };
        metrics.FallbackReasons.AddRange(reasons);
        _metrics.RecordPage(metrics);
    }

    private static IReadOnlyList<string> DescribeReasons(IEnumerable<PdfFallbackToken> tokens) =>
        tokens.GroupBy(t => t.Reason)
              .Select(g => $"• {g.Key} ×{g.Count()}: {g.First().Description}")
              .ToList();

#if PDFIUM_RENDERER
    private static PageEngineReport PdfiumReport(int pageNumber) => new(
        pageNumber,
        PdfEngineMode.Pdfium,
        IsFallback: false,
        FallbackReasons: Array.Empty<string>(),
        BadgeText: "🖼️ PDFium",
        Tooltip: "Rendered by PDFium [Mode: PDFium, developer build]");
#endif

    private static PageEngineReport UnreadableReport(int pageNumber, string reason, string why) => new(
        pageNumber,
        PdfEngineMode.Vector,
        IsFallback: true,
        FallbackReasons: new[] { reason },
        BadgeText: "⚠ Unreadable page",
        Tooltip: $"Shown blank because {why}:\n{reason}");

    private static PageEngineReport VectorReport(int pageNumber, IPdfDisplayList list, IReadOnlyList<PdfFallbackToken> fallbacks, bool strict, bool live)
    {
        string how = live ? "live vector surface (zoom re-renders vectors, no page bitmap)" : "vector engine";
        int commands = list.Commands.Count;
        bool recovered = list.Features.HasFlag(PdfFeatureSet.Recovered);
        if (fallbacks.Count == 0)
        {
            return new PageEngineReport(pageNumber, PdfEngineMode.Vector, IsFallback: false, Array.Empty<string>(),
                BadgeText: "⚡ Vector",
                Tooltip: $"Rendered by the {how}\nCommands: {commands}" +
                         (recovered ? "\nDamaged or unsupported content was skipped or drawn by best effort, as PDFium does." : string.Empty));
        }

        var reasons = DescribeReasons(fallbacks);
        if (strict)
        {
            return new PageEngineReport(pageNumber, PdfEngineMode.Vector, IsFallback: false, reasons,
                BadgeText: $"⚡ Vector ({fallbacks.Count} unsupported)",
                Tooltip: $"Strict vector mode ({how}): unsupported regions are outlined:\n{string.Join("\n", reasons)}");
        }

        // No other renderer is asked: the engine draws these regions as well as it can.
        return new PageEngineReport(pageNumber, PdfEngineMode.Vector, IsFallback: false, reasons,
            BadgeText: $"⚡ Vector ({fallbacks.Count} approximated)",
            Tooltip: $"Rendered by the {how}; {fallbacks.Count} region(s) drawn by the engine's best effort:\n{string.Join("\n", reasons)}");
    }

    private static BitmapSource CreateBitmapSource(RenderedPage renderedPage)
    {
        var bitmap = BitmapSource.Create(
            renderedPage.WidthPixels,
            renderedPage.HeightPixels,
            renderedPage.Dpi,
            renderedPage.Dpi,
            PixelFormats.Pbgra32,
            null,
            renderedPage.Pixels.ToArray(),
            renderedPage.Stride);

        bitmap.Freeze();
        return bitmap;
    }

    public ObservableCollection<BookmarkItem> ExtractBookmarks()
    {
        if (NativeDocument is { } native)
        {
            try
            {
                return NativeDocumentServices.Bookmarks(native);
            }
            catch (Exception ex) when (ex is PdfVectorException or InvalidOperationException)
            {
                // A damaged outline: no bookmarks rather than an error.
                return new ObservableCollection<BookmarkItem>();
            }
        }
#if PDFIUM_RENDERER
        if (_bytes != null && UsePdfiumPages)
            return _pdfiumService.ExtractBookmarks();
#endif
        return new ObservableCollection<BookmarkItem>();
    }

    // ------------------------------------------------------------------ text (native text page, M9)

    /// <summary>
    /// The document text comes from: the vector engine's text page in Auto and Vector modes (no
    /// PDFium call), PDFium's in PDFium mode. Null when the vector document could not be opened;
    /// Auto mode in a developer build then reads text with PDFium, and the published app (no
    /// PDFium) has no text for that document.
    /// </summary>
    private PdfVectorDocument? TextDocument => _mode == PdfEngineMode.Pdfium ? null : _vectorDoc;

#if PDFIUM_RENDERER
    private bool TextFromPdfium => _mode == PdfEngineMode.Pdfium || (_vectorDoc == null && _mode != PdfEngineMode.Vector);
#endif

    public async Task<List<SearchMatch>> SearchTextAsync(string query, bool matchCase = false, CancellationToken ct = default)
    {
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        if (TextDocument is { } doc)
            return await Task.Run(() => NativePageText.Search(doc, query, matchCase, ct), ct).ConfigureAwait(false);
#if PDFIUM_RENDERER
        if (TextFromPdfium)
            return await _pdfiumService.SearchTextAsync(query, matchCase, ct).ConfigureAwait(false);
#endif
        return new List<SearchMatch>();
    }

    public List<PageTextSegment> ExtractPageTextSegments(int pageNumber)
    {
        if (TextDocument is { } doc)
        {
            if (IsPageVectorRendered(pageNumber) && DisplayListSegments(doc, pageNumber, CancellationToken.None) is { Count: > 0 } glyphs)
                return glyphs;
            return NativePageText.Segments(doc, pageNumber);
        }
#if PDFIUM_RENDERER
        if (TextFromPdfium)
            return _pdfiumService.ExtractPageTextSegments(pageNumber);
#endif
        return new List<PageTextSegment>();
    }

    /// <summary>
    /// Selection text comes from the text page: it has the reading order, the inferred spaces and
    /// line breaks, and line-end hyphens. It reads the same file (or the decrypted copy of a
    /// certificate-encrypted one) as the renderer, so boxes match the page.
    /// </summary>
    public async Task<PdfViewer.Text.PageTextLayout> ExtractPageTextLayoutAsync(int pageNumber, CancellationToken ct = default)
    {
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        if (TextDocument is { } doc)
            return await Task.Run(() => NativePageText.Layout(doc, pageNumber, ct), ct).ConfigureAwait(false);
#if PDFIUM_RENDERER
        if (TextFromPdfium)
            return await _pdfiumService.ExtractPageTextLayoutAsync(pageNumber, ct).ConfigureAwait(false);
#endif
        return PdfViewer.Text.PageTextLayout.Empty;
    }

    public async Task<PdfViewer.Text.PageAccessibleContent> ExtractAccessibleContentAsync(int pageNumber, CancellationToken ct = default)
    {
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        if (TextDocument is { } doc)
        {
            var tagged = await Task.Run(() => NativePageText.Tagged(doc, pageNumber, ct), ct).ConfigureAwait(false);
            if (tagged != null && tagged.Blocks.Count > 0)
                return tagged;
            var layout = await ExtractPageTextLayoutAsync(pageNumber, ct).ConfigureAwait(false);
            return PdfViewer.Text.PageAccessibleContent.FromLayout(pageNumber, layout);
        }
#if PDFIUM_RENDERER
        if (TextFromPdfium)
            return await _pdfiumService.ExtractAccessibleContentAsync(pageNumber, ct).ConfigureAwait(false);
#endif
        return PdfViewer.Text.PageAccessibleContent.FromLayout(pageNumber, PdfViewer.Text.PageTextLayout.Empty);
    }

    public async Task<List<PageTextSegment>> ExtractPageTextSegmentsAsync(int pageNumber, CancellationToken ct = default)
    {
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        if (TextDocument is { } doc)
        {
            return await Task.Run(() =>
            {
                if (IsPageVectorRendered(pageNumber) && DisplayListSegments(doc, pageNumber, ct) is { Count: > 0 } glyphs)
                    return glyphs;
                return NativePageText.Segments(doc, pageNumber, ct);
            }, ct).ConfigureAwait(false);
        }
#if PDFIUM_RENDERER
        if (TextFromPdfium)
            return await _pdfiumService.ExtractPageTextSegmentsAsync(pageNumber, ct).ConfigureAwait(false);
#endif
        return new List<PageTextSegment>();
    }

    /// <summary>Per-glyph segments of a page the vector engine drew (its display list), or null.</summary>
    private static List<PageTextSegment>? DisplayListSegments(PdfVectorDocument doc, int pageNumber, CancellationToken ct)
    {
        try
        {
            var displayList = doc.GetPageDisplayListAsync(pageNumber, ct).AsTask().GetAwaiter().GetResult();
            return displayList.Features.HasFlag(PdfFeatureSet.Text) ? ExtractCharSegmentsFromDisplayList(displayList) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true when the given page has an existing engine report confirming it was
    /// rendered natively by the vector pipeline (not a PDFium fallback).
    /// In Vector-only mode we always trust the display list; in Auto/Hybrid mode we require
    /// an explicit render to have happened so we know the page is vector-clean.
    /// </summary>
    private bool IsPageVectorRendered(int pageNumber)
    {
        if (_mode == PdfEngineMode.Vector)
            return true; // always use vector char extraction in strict vector mode

        if (_pageEngineReports.TryGetValue(pageNumber, out var report))
            return report.Engine is PdfEngineMode.Vector or PdfEngineMode.Hybrid;

        return false; // page not yet rendered — use PDFium word-level segments for safety
    }

    /// <summary>
    /// Extracts one <see cref="PageTextSegment"/> per glyph from the vector display list,
    /// giving true per-character selection precision in vector/hybrid mode.
    ///
    /// Coordinate conventions:
    ///   - <see cref="PdfGlyph.OffsetX"/>  is the cumulative glyph-left position in text space,
    ///     already fully scaled by (width/1000 * fontSize * hScale) inside PdfContentInterpreter.
    ///   - <see cref="PdfGlyph.AdvanceX"/> is also in text space, already fully scaled.
    ///   - <see cref="PdfGlyphRun.TextMatrix"/> transforms text-space coordinates into PDF
    ///     page space (y-up, origin at bottom-left of the page MediaBox).
    ///   - We normalize to [0, 1] screen space by dividing by page size and flipping y.
    /// </summary>
    private static List<PageTextSegment> ExtractCharSegmentsFromDisplayList(IPdfDisplayList displayList)
    {
        var list = new List<PageTextSegment>();
        // Segments are normalized to the UNROTATED crop box, like the PDFium path.
        var crop = displayList.CropBox;
        double pageW = crop.Width;
        double pageH = crop.Height;
        if (pageW <= 0 || pageH <= 0) return list;

        int segIndex = 0;

        foreach (var cmd in displayList.Commands)
        {
            if (cmd is not DrawGlyphRun dgr) continue;

            var run = dgr.Run;
            if (run.Glyphs.Count == 0) continue;

            // Text space → default user space, CTM included (invisible OCR text too).
            var m = run.TextToPage;
            double fontSize = Math.Abs(run.FontSize);
            double ascent = (run.Face?.Ascent ?? 0.8) * fontSize;
            double descent = (run.Face?.Descent ?? -0.2) * fontSize;
            if (ascent <= 0) ascent = 0.8 * fontSize;
            if (descent >= 0) descent = -0.2 * fontSize;

            foreach (var glyph in run.Glyphs)
            {
                if (string.IsNullOrEmpty(glyph.Unicode) || glyph.Unicode == " ")
                    continue;

                double left = glyph.OffsetX;
                double right = glyph.OffsetX + glyph.AdvanceX;
                if (glyph.AdvanceY != 0) right = left + Math.Abs(glyph.AdvanceY); // vertical: one em-ish column
                if (Math.Abs(right - left) < 1e-6) right = left + fontSize * 0.25;
                double bottom = run.TextRise + glyph.OffsetY + descent;
                double top = run.TextRise + glyph.OffsetY + ascent;

                var bounds = m.Transform(new PdfRect(Math.Min(left, right), bottom, Math.Abs(right - left), top - bottom));

                double normX = Math.Max(0.0, (bounds.X - crop.X) / pageW);
                double normY = Math.Max(0.0, 1.0 - (bounds.Y + bounds.Height - crop.Y) / pageH);
                double normW = Math.Max(0.001, bounds.Width / pageW);
                double normH = Math.Max(0.001, bounds.Height / pageH);

                if (normX + normW > 1.0) normX = Math.Max(0.0, 1.0 - normW);
                if (normY + normH > 1.0) normY = Math.Max(0.0, 1.0 - normH);

                list.Add(new PageTextSegment
                {
                    PageNumber = displayList.PageNumber,
                    Text = glyph.Unicode!,
                    X = normX,
                    Y = normY,
                    Width = normW,
                    Height = normH,
                    SegmentIndex = segIndex++
                });
            }
        }

        return list;
    }

    /// <summary>Pages as PNG or JPEG files, drawn by the same renderer as the screen (PDFium only in PDFium mode).</summary>
    public async Task ExportPagesToImagesAsync(
        string outputDirectory,
        string fileNamePrefix,
        int startPage,
        int endPage,
        string format = "PNG",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsDocumentLoaded)
            throw new InvalidOperationException("No document is open.");
        System.IO.Directory.CreateDirectory(outputDirectory);
        startPage = Math.Max(1, startPage);
        endPage = Math.Min(PageCount, endPage);
        int total = Math.Max(1, endPage - startPage + 1);
        bool jpeg = format.Equals("JPEG", StringComparison.OrdinalIgnoreCase) || format.Equals("JPG", StringComparison.OrdinalIgnoreCase);
        string ext = jpeg ? "jpg" : "png";
        for (int p = startPage; p <= endPage; p++)
        {
            ct.ThrowIfCancellationRequested();
            var bitmap = await RenderPageAsync(p, dpi, 0, ct).ConfigureAwait(false);
            if (bitmap != null)
            {
                string outPath = System.IO.Path.Combine(outputDirectory, $"{fileNamePrefix}_page_{p:D3}.{ext}");
                await Task.Run(() =>
                {
                    BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = new System.IO.FileStream(outPath, System.IO.FileMode.Create, System.IO.FileAccess.Write);
                    encoder.Save(file);
                }, ct).ConfigureAwait(false);
            }
            progress?.Report((double)(p - startPage + 1) / total * 100.0);
        }
    }

    public void PrintDocument(PrintDialog printDialog, int fromPage = 1, int toPage = -1, int rotationAngle = 0)
    {
        if (!IsDocumentLoaded) return;
        int start = Math.Max(1, fromPage);
        int end = toPage < 1 ? PageCount : Math.Min(PageCount, toPage);
        var paginator = new PageImagePaginator(this, start, end, printDialog.PrintableAreaWidth, printDialog.PrintableAreaHeight, rotationAngle);
        printDialog.PrintDocument(paginator, $"Printing {System.IO.Path.GetFileName(CurrentFilePath)}");
    }

    // ------------------------------------------------------------------ printing (DocumentPrinter)

    /// <summary>Strict vector mode: a page that fails to print as vectors is an error, not an image.</summary>
    internal bool IsStrictVector => _mode == PdfEngineMode.Vector;

    /// <summary>
    /// The display list to print a page from, or null when the page is printed as an image
    /// (<see cref="RenderPage"/>): PDFium mode, or a page the engine cannot read.
    /// </summary>
    internal async Task<IPdfDisplayList?> GetPrintableDisplayListAsync(int pageNumber, CancellationToken ct = default)
    {
#if PDFIUM_RENDERER
        if (UsePdfiumPages)
            return null;
#endif
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);
        var doc = _vectorDoc;
        if (doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
            return null;
        IPdfDisplayList list;
        try
        {
            // Annotations as printed: the Print flag decides, not NoView.
            list = await doc.GetPagePrintDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsStrictVector && ex is not OperationCanceledException)
        {
            return null;
        }
        return list;
    }

    public List<AnnotationModel> LoadExistingAnnotations() =>
        NativeDocument is { } native ? _native.Load(native)
#if PDFIUM_RENDERER
        : _bytes != null && UsePdfiumPages ? _pdfiumService.LoadExistingAnnotations()
#endif
        : new List<AnnotationModel>();

    /// <summary>
    /// Saves with the native annotation services in Auto and Vector modes: the latest revision
    /// in memory (form fills included) with exactly <paramref name="annotations"/>, written to a
    /// new file. Never over the original.
    /// </summary>
    public async Task SaveAnnotatedDocumentAsync(
        string targetPath,
        AnnotationSaveMode mode,
        IEnumerable<AnnotationModel> annotations,
        string? originalPath = null,
        CancellationToken ct = default)
    {
        if (NativeDocument is not { } native || CurrentBytes is not { } bytes)
        {
#if PDFIUM_RENDERER
            if (UsePdfiumPages)
            {
                await _pdfiumService.SaveAnnotatedDocumentAsync(targetPath, mode, annotations, originalPath, ct).ConfigureAwait(false);
                return;
            }
#endif
            throw new InvalidOperationException("The document could not be read by the engine, so it cannot be saved.");
        }
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target file path cannot be empty.", nameof(targetPath));
        if (!string.IsNullOrEmpty(originalPath) &&
            string.Equals(System.IO.Path.GetFullPath(targetPath), System.IO.Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Saving directly over the original source file is prohibited. Please specify a new file name or directory.");

        var list = annotations.ToList();
        if (mode == AnnotationSaveMode.ExportXfdf)
        {
            var (sizes, origins) = NativeDocumentServices.PageFrames(native, list.Select(a => a.PageNumber));
            await Task.Run(() => AnnotationExchange.ExportAnnotationsToXfdf(targetPath, list, sizes, origins), ct).ConfigureAwait(false);
            return;
        }

        byte[] output = await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            // The revision the bytes hold (a decrypted copy has no password); its object numbers are the loaded ones.
            using var editing = await PdfVectorDocument.OpenAsync(bytes, CurrentFilePath, cancellationToken: ct,
                password: IsDecryptedCopy ? null : _password).ConfigureAwait(false);
            return _native.Save(editing, bytes, list, flatten: mode == AnnotationSaveMode.Flattened);
        }, ct).ConfigureAwait(false);

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
        await System.IO.File.WriteAllBytesAsync(targetPath, output, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _vectorDoc?.Dispose();
            _vectorRenderer.Dispose();
            _d2d?.Dispose();
#if PDFIUM_RENDERER
            _pdfium?.Dispose();
#endif
        }
    }
}
