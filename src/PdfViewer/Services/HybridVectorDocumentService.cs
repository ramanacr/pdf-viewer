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
/// Hybrid PDF document service orchestrating vector-first parsing and rendering
/// with transparent, safe fallback to PDFium.
/// </summary>
public sealed class HybridVectorDocumentService : IPdfDocumentService
{
    /// <summary>
    /// Above this display-list fallback area the whole page is delegated to PDFium: compositing
    /// most of a page from fallback pixels costs more than it saves.
    /// </summary>
    internal const double FullPageFallbackAreaThreshold = 0.5;

    private readonly PdfiumDocumentService _pdfiumService;
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
    private readonly PdfiumRegionFallbackProvider _fallbackProvider;
    private readonly ConcurrentDictionary<int, PageEngineReport> _pageEngineReports = new();
    private PdfEngineMetrics _metrics = new();
    private PdfVectorDocument? _vectorDoc;
    private bool _vectorOpenAttempted;
    // The document password, kept for the lazy vector open (engine switch) and cleared on close.
    private string? _password;
    private readonly SemaphoreSlim _vectorOpenGate = new(1, 1);
    private PdfEngineMode _mode;
    private bool _disposed;

    public PdfEngineMode EngineMode
    {
        get => _mode;
        set
        {
            if (_mode != value)
                ClearSurfaces();
            _mode = value;
        }
    }

    /// <summary>
    /// Fallback regions on a live vector surface are PDFium pixels; this is their resolution.
    /// 200 dpi stays crisp to roughly 275 % zoom, and only the (small) regions pay for it.
    /// </summary>
    internal const double SurfaceFallbackDpi = 200;

    /// <summary>
    /// Pages with more commands than this are shown as bitmaps: WPF re-renders a live drawing on
    /// every frame, which stops being cheaper than one raster for very dense pages.
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
        if (_mode == PdfEngineMode.Pdfium || HostMode != VectorHostMode.WpfDrawing)
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
        }
        catch (Exception ex) when (_mode != PdfEngineMode.Vector && ex is not OperationCanceledException)
        {
            return null; // the bitmap path classifies and reports it
        }
        buildClock.Stop();

        bool strict = _mode == PdfEngineMode.Vector;
        if (displayList.Commands.Count > MaxLiveSurfaceCommands)
            return null;
        if (!strict && (displayList.ComputeFallbackAreaRatio() >= FullPageFallbackAreaThreshold ||
                        displayList.Commands.Count == 0 && displayList.HasFallback))
            return null;

        if (!strict && displayList.HasFallback)
        {
            // Fallback pixels come from one PDFium page raster: same ceiling as any other raster.
            var (w, h) = PixelSize(displayList, (int)SurfaceFallbackDpi, 0);
            try
            {
                _securityPolicy.EnsureRenderDimensionsAllowed(w, h);
            }
            catch (PdfEngine.Exceptions.PdfSecurityPolicyException)
            {
                return null;
            }
        }

        PdfVectorSurfaceResult result;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            result = await _vectorRenderer.BuildPageSurfaceAsync(displayList, (PageRotation)rotation,
                strict ? null : _fallbackProvider, SurfaceFallbackDpi, ct, invertColors: nightMode).ConfigureAwait(false);
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException)
        {
            return null;
        }
        clock.Stop();

        if (!strict && AreaRatio(displayList, result.Fallbacks) >= FullPageFallbackAreaThreshold)
            return null; // backend fallbacks pushed the page over the threshold: show PDFium's raster

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
        _pageEngineReports[pageNumber] = VectorReport(pageNumber, displayList.Commands.Count, result.Fallbacks, result.FallbackRegionsComposited, strict, live: true);

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

        if (_mode == PdfEngineMode.Pdfium)
        {
            return new PageEngineReport(
                pageNumber,
                PdfEngineMode.Pdfium,
                IsFallback: false,
                FallbackReasons: Array.Empty<string>(),
                BadgeText: "🖼️ PDFium",
                Tooltip: "Rendered via Google PDFium software rasterizer [Mode: PDFium]");
        }

        return new PageEngineReport(
            pageNumber,
            _mode,
            IsFallback: false,
            FallbackReasons: Array.Empty<string>(),
            BadgeText: _mode == PdfEngineMode.Vector ? "⚡ Vector" : "⚡ Vector (Auto)",
            Tooltip: "Vector engine (page not rendered yet)");
    }

    public bool IsDocumentLoaded => _vectorDoc != null || _pdfiumService.IsDocumentLoaded;
    public string CurrentFilePath => _pdfiumService.CurrentFilePath;
    public int PageCount => _vectorDoc?.PageCount ?? _pdfiumService.PageCount;

    public HybridVectorDocumentService(
        PdfSecurityPolicy securityPolicy,
        PdfEngineMode mode = PdfEngineMode.Auto)
    {
        _securityPolicy = securityPolicy ?? PdfSecurityPolicy.DefaultStrict;
        _pdfiumService = new PdfiumDocumentService(_securityPolicy);
        _vectorRenderer = new WindowsVectorRenderer();
        try
        {
            _d2d = new Direct2DVectorRenderer();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _d2d = null; // no Direct3D device at all: the WPF renderer rasterizes instead
        }
        _fallbackProvider = new PdfiumRegionFallbackProvider(_pdfiumService, GetPageGeometry);
        _mode = mode;
    }

    /// <summary>Local engine diagnostics for the open document (never transmitted).</summary>
    public PdfEngineMetrics EngineMetrics => _metrics;

    /// <summary>True when the vector core parsed the open document.</summary>
    public bool IsVectorDocumentOpen => _vectorDoc != null;

    private (PdfRect CropBox, int Rotation)? GetPageGeometry(int pageNumber)
    {
        var doc = _vectorDoc;
        if (doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
            return null;
        var node = doc.PageTree.Pages[pageNumber - 1];
        return (node.CropBox, node.RotationDegrees);
    }

    public async Task<DocumentMetadata> OpenDocumentAsync(
        string filePath,
        string? password = null,
        CancellationToken ct = default)
    {
        // PDFium stays open for every non-rendering service (search, annotations, forms, save)
        // and as the fallback renderer.
        DocumentMetadata meta;
        IsDecryptedCopy = false;
        try
        {
            meta = await _pdfiumService.OpenDocumentAsync(filePath, password, ct).ConfigureAwait(false);
        }
        catch (PdfUnsupportedSecurityException)
        {
            // Certificate (public-key) encryption, which PDFium cannot open: the vector core opens
            // it with the user's certificates, and PDFium works on an in-memory decrypted copy so
            // search, printing and annotations keep working. The copy never replaces the original.
            meta = await OpenWithVectorSecurityAsync(filePath, password, ct).ConfigureAwait(false);
            return meta;
        }
        _metrics = new PdfEngineMetrics();
        _fallbackProvider.Reset();
        ClearSurfaces();
        _vectorOpenAttempted = false;
        _password = password;

        if (_mode == PdfEngineMode.Pdfium)
            return meta; // opened lazily if the user later switches to Auto/Vector

        await OpenVectorDocumentAsync(filePath, ct).ConfigureAwait(false);
        return meta;
    }

    public bool IsDecryptedCopy { get; private set; }

    /// <summary>The vector core's security handler when it opened the file, else PDFium's view.</summary>
    public PdfEngine.Documents.PdfDocumentPermissions Permissions =>
        _vectorDoc?.Security is { } security
            ? PdfEngine.Documents.PdfDocumentPermissions.FromFlags(security.Permissions, security.Revision, security.IsOwner)
            : IsDecryptedCopy ? PdfEngine.Documents.PdfDocumentPermissions.Owner : _pdfiumService.Permissions;

    private async Task<DocumentMetadata> OpenWithVectorSecurityAsync(string filePath, string? password, CancellationToken ct)
    {
        _metrics = new PdfEngineMetrics();
        _fallbackProvider.Reset();
        ClearSurfaces();
        _password = password;
        PdfVectorDocument vector;
        try
        {
            vector = await PdfVectorDocument.OpenAsync(filePath, cancellationToken: ct, password: password).ConfigureAwait(false);
        }
        catch (PdfEncryptedDocumentException ex) when (ex.CertificateRequired)
        {
            throw new InvalidOperationException(
                "This document is encrypted for specific recipients, and none of the certificates installed for your account can open it.", ex);
        }
        byte[] copy = PdfEngine.Vector.Document.PdfDecryptedCopyWriter.Write(vector);
        DocumentMetadata meta;
        try
        {
            meta = await _pdfiumService.OpenDocumentFromBytesAsync(copy, filePath, ct).ConfigureAwait(false);
        }
        catch
        {
            vector.Dispose();
            throw;
        }
        _vectorDoc?.Dispose();
        _vectorDoc = vector;
        _vectorOpenAttempted = true;
        IsDecryptedCopy = true;
        return meta;
    }

    /// <summary>Opens the vector document lazily when the file was opened in PDFium mode and the engine changed since.</summary>
    private async Task EnsureVectorDocumentAsync(CancellationToken ct)
    {
        if (_mode == PdfEngineMode.Pdfium || _vectorDoc != null || _vectorOpenAttempted || !_pdfiumService.IsDocumentLoaded)
            return;

        await _vectorOpenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_vectorDoc == null && !_vectorOpenAttempted)
                await OpenVectorDocumentAsync(_pdfiumService.CurrentFilePath, ct).ConfigureAwait(false);
        }
        finally
        {
            _vectorOpenGate.Release();
        }
    }

    private async Task OpenVectorDocumentAsync(string filePath, CancellationToken ct)
    {
        _vectorOpenAttempted = true;
        try
        {
            // Encrypted files open with the same password PDFium accepted (Standard security handler).
            _vectorDoc = await PdfVectorDocument.OpenAsync(filePath, cancellationToken: ct, password: _password).ConfigureAwait(false);
            if (_vectorDoc.WasRepaired)
                _metrics.RecordRecovery();
        }
        catch (PdfEncryptedDocumentException)
        {
            // An unsupported security handler (e.g. public-key): PDFium renders the document.
            _vectorDoc = null;
            _metrics.DocumentFallbackReason = nameof(PdfFallbackReason.EncryptedContent);
        }
        catch (Exception ex) when (_mode != PdfEngineMode.Vector && ex is not OperationCanceledException)
        {
            // Document-level parse failure in hybrid/auto: classified, then PDFium renders everything.
            _vectorDoc = null;
            _metrics.DocumentFallbackReason = ex is PdfVectorException pve ? pve.Kind.ToString() : nameof(PdfFallbackReason.MalformedContentRecovery);
        }
    }

    public void CloseDocument()
    {
        ClearSurfaces();
        _vectorOpenAttempted = false;
        _password = null;
        IsDecryptedCopy = false;
        _pageEngineReports.Clear();
        _fallbackProvider.Reset();
        _vectorDoc?.Dispose();
        _vectorDoc = null;
        _pdfiumService.CloseDocument();
    }

    public DocumentMetadata? GetMetadata() => _pdfiumService.GetMetadata();

    public (double Width, double Height) GetPageDimensions(int pageNumber)
    {
        if (_vectorDoc != null && pageNumber >= 1 && pageNumber <= _vectorDoc.PageCount)
        {
            var pageNode = _vectorDoc.PageTree.Pages[pageNumber - 1];
            return (pageNode.PageSize.Width, pageNode.PageSize.Height);
        }
        return _pdfiumService.GetPageDimensions(pageNumber);
    }

    public int GetPageIntrinsicRotation(int pageNumber)
    {
        if (_vectorDoc != null && pageNumber >= 1 && pageNumber <= _vectorDoc.PageCount)
        {
            var pageNode = _vectorDoc.PageTree.Pages[pageNumber - 1];
            return pageNode.RotationDegrees;
        }
        return _pdfiumService.GetPageIntrinsicRotation(pageNumber);
    }

    public BitmapSource? RenderPage(int pageNumber, int dpi = 150, int rotationAngle = 0)
    {
        return RenderPageAsync(pageNumber, dpi, rotationAngle).GetAwaiter().GetResult();
    }

    public async Task<BitmapSource?> RenderPageAsync(
        int pageNumber,
        int dpi = 150,
        int rotationAngle = 0,
        CancellationToken ct = default)
    {
        await EnsureVectorDocumentAsync(ct).ConfigureAwait(false);

        var doc = _vectorDoc;
        if (_mode == PdfEngineMode.Pdfium || doc == null || pageNumber < 1 || pageNumber > doc.PageCount)
        {
            _pageEngineReports[pageNumber] = _mode == PdfEngineMode.Pdfium
                ? PdfiumReport(pageNumber)
                : FallbackReport(pageNumber, new[] { $"• Document: {_metrics.DocumentFallbackReason ?? "not parsed by the vector core"}" }, "document could not be opened by the vector core");
            return await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
        }

        var buildClock = System.Diagnostics.Stopwatch.StartNew();
        IPdfDisplayList displayList;
        try
        {
            displayList = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_mode != PdfEngineMode.Vector && ex is not OperationCanceledException)
        {
            RecordFullFallback(pageNumber, 0, PdfFallbackReason.InternalCompatibilityGuard);
            _pageEngineReports[pageNumber] = FallbackReport(pageNumber, new[] { $"• {PdfFallbackReason.InternalCompatibilityGuard}: display list build failed ({ex.GetType().Name})" }, "vector interpretation failed");
            return await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
        }
        buildClock.Stop();

        // Same raster ceiling the PDFium path enforces: a hostile MediaBox at high zoom must not
        // allocate an unbounded bitmap through the vector path either.
        var (pixelW, pixelH) = PixelSize(displayList, dpi, rotationAngle);
        _securityPolicy.EnsureRenderDimensionsAllowed(pixelW, pixelH);

        double listFallbackArea = displayList.ComputeFallbackAreaRatio();
        bool strict = _mode == PdfEngineMode.Vector;

        if (!strict && (listFallbackArea >= FullPageFallbackAreaThreshold || displayList.Commands.Count == 0 && displayList.HasFallback))
        {
            RecordFullFallback(pageNumber, buildClock.Elapsed.TotalMilliseconds, displayList.FallbackTokens.Select(t => t.Reason).ToArray());
            _pageEngineReports[pageNumber] = FallbackReport(pageNumber, DescribeReasons(displayList.FallbackTokens),
                $"{listFallbackArea:P0} of the page needs constructs the vector path does not support yet");
            return await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
        }

        var request = new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = dpi,
            Rotation = (PageRotation)((((rotationAngle % 360) + 360) % 360) / 90 * 90),
        };

        RasterResult result;
        try
        {
            result = await RasterizeAsync(displayList, request, strict ? null : _fallbackProvider, null, false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!strict && ex is not OperationCanceledException)
        {
            RecordFullFallback(pageNumber, buildClock.Elapsed.TotalMilliseconds, PdfFallbackReason.InternalCompatibilityGuard);
            _pageEngineReports[pageNumber] = FallbackReport(pageNumber, new[] { $"• {PdfFallbackReason.InternalCompatibilityGuard}: vector renderer failed ({ex.GetType().Name})" }, "vector rendering failed");
            return await _pdfiumService.RenderPageAsync(pageNumber, dpi, rotationAngle, ct).ConfigureAwait(false);
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

        _pageEngineReports[pageNumber] = VectorReport(pageNumber, displayList.Commands.Count, result.Fallbacks, result.FallbackRegionsComposited, strict, live: false);
        return CreateBitmapSource(renderedPage);
    }

    private readonly record struct RasterResult(RenderedPage Page, IReadOnlyList<PdfFallbackToken> Fallbacks, int FallbackRegionsComposited, TimeSpan Elapsed);

    /// <summary>Direct2D when available; the WPF renderer otherwise (full page only, inversion applied after).</summary>
    private async Task<RasterResult> RasterizeAsync(IPdfDisplayList list, RenderRequest request, IPdfFallbackProvider? provider,
        PixelRegion? region, bool invert, CancellationToken ct)
    {
        if (_d2d != null)
        {
            var r = await _d2d.RenderAsync(list, request, provider, region, invert, ct).ConfigureAwait(false);
            return new RasterResult(r.Page, r.Fallbacks, r.FallbackRegionsComposited, r.Elapsed);
        }
        if (region != null || invert)
            throw new NotSupportedException("Region and inverted rasterization need the Direct2D renderer.");
        var w = await _vectorRenderer.RenderAsync(list, request, provider, ct).ConfigureAwait(false);
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
        var doc = _vectorDoc;
        if (_mode == PdfEngineMode.Pdfium || doc == null || _d2d == null || pageNumber < 1 || pageNumber > doc.PageCount)
        {
            // PDFium pages get the same viewport detail from PDFium itself.
            var bmp = await Task.Run(() => _pdfiumService.RenderPageRegion(pageNumber, pixelsPerPoint * 72.0, rotationAngle, x, y, width, height), ct).ConfigureAwait(false);
            return bmp != null && nightMode ? NightModeImage.Invert(bmp) : bmp;
        }

        var list = await doc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
        bool strict = _mode == PdfEngineMode.Vector;
        if (!strict && (list.ComputeFallbackAreaRatio() >= FullPageFallbackAreaThreshold || list.Commands.Count == 0 && list.HasFallback))
        {
            var bmp = await Task.Run(() => _pdfiumService.RenderPageRegion(pageNumber, pixelsPerPoint * 72.0, rotationAngle, x, y, width, height), ct).ConfigureAwait(false);
            return bmp != null && nightMode ? NightModeImage.Invert(bmp) : bmp;
        }

        var request = new RenderRequest
        {
            PageNumber = pageNumber,
            Dpi = pixelsPerPoint * 72.0,
            Rotation = (PageRotation)((((rotationAngle % 360) + 360) % 360) / 90 * 90),
        };
        var result = await RasterizeAsync(list, request, strict ? null : _fallbackProvider, new PixelRegion(x, y, width, height), nightMode, ct).ConfigureAwait(false);
        using var page = result.Page;
        return CreateBitmapSource(page);
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

    private void RecordFullFallback(int pageNumber, double buildMs, params PdfFallbackReason[] reasons)
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

    private static PageEngineReport PdfiumReport(int pageNumber) => new(
        pageNumber,
        PdfEngineMode.Pdfium,
        IsFallback: false,
        FallbackReasons: Array.Empty<string>(),
        BadgeText: "🖼️ PDFium",
        Tooltip: "Rendered by PDFium [Mode: PDFium]");

    private static PageEngineReport FallbackReport(int pageNumber, IReadOnlyList<string> reasons, string why) => new(
        pageNumber,
        PdfEngineMode.Pdfium,
        IsFallback: true,
        FallbackReasons: reasons,
        BadgeText: "🖼️ PDFium (Fallback)",
        Tooltip: $"Rendered by PDFium because {why}:\n{string.Join("\n", reasons)}");

    private static PageEngineReport VectorReport(int pageNumber, int commands, IReadOnlyList<PdfFallbackToken> fallbacks, int composited, bool strict, bool live)
    {
        string how = live ? "live vector surface (zoom re-renders vectors, no page bitmap)" : "vector engine (rasterized)";
        if (fallbacks.Count == 0)
        {
            return new PageEngineReport(pageNumber, PdfEngineMode.Vector, IsFallback: false, Array.Empty<string>(),
                BadgeText: "⚡ Vector",
                Tooltip: $"Rendered by the {how}\nCommands: {commands}");
        }

        var reasons = DescribeReasons(fallbacks);
        if (strict)
        {
            return new PageEngineReport(pageNumber, PdfEngineMode.Vector, IsFallback: false, reasons,
                BadgeText: $"⚡ Vector ({fallbacks.Count} unsupported)",
                Tooltip: $"Strict vector mode ({how}): unsupported regions are outlined, not rendered:\n{string.Join("\n", reasons)}");
        }

        return new PageEngineReport(pageNumber, PdfEngineMode.Hybrid, IsFallback: true, reasons,
            BadgeText: $"⚡ Hybrid ({composited} PDFium regions)",
            Tooltip: $"Rendered by the {how}; {composited} region(s) composited from PDFium:\n{string.Join("\n", reasons)}");
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
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium)
        {
            try
            {
                var vectorBookmarks = _vectorDoc.GetBookmarksAsync().GetAwaiter().GetResult();
                if (vectorBookmarks.Count > 0)
                {
                    var col = new ObservableCollection<BookmarkItem>();
                    foreach (var b in vectorBookmarks)
                    {
                        col.Add(ConvertBookmark(b));
                    }
                    return col;
                }
            }
            catch
            {
                // Fallback to PDFium
            }
        }
        return _pdfiumService.ExtractBookmarks();
    }

    private static BookmarkItem ConvertBookmark(PdfEngine.Documents.BookmarkItem item)
    {
        var model = new BookmarkItem
        {
            Title = item.Title,
            TargetPageNumber = item.TargetPageNumber
        };
        foreach (var child in item.Children)
        {
            model.Children.Add(ConvertBookmark(child));
        }
        return model;
    }

    public Task<List<SearchMatch>> SearchTextAsync(string query, bool matchCase = false, CancellationToken ct = default) =>
        _pdfiumService.SearchTextAsync(query, matchCase, ct);

    public List<PageTextSegment> ExtractPageTextSegments(int pageNumber)
    {
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium && IsPageVectorRendered(pageNumber))
        {
            try
            {
                var displayList = _vectorDoc.GetPageDisplayListAsync(pageNumber).GetAwaiter().GetResult();
                if (displayList.Features.HasFlag(PdfFeatureSet.Text))
                {
                    var segments = ExtractCharSegmentsFromDisplayList(displayList);
                    if (segments.Count > 0) return segments;
                }
            }
            catch { }
        }
        return _pdfiumService.ExtractPageTextSegments(pageNumber);
    }

    public async Task<List<PageTextSegment>> ExtractPageTextSegmentsAsync(int pageNumber, CancellationToken ct = default)
    {
        if (_vectorDoc != null && _mode != PdfEngineMode.Pdfium && IsPageVectorRendered(pageNumber))
        {
            try
            {
                var displayList = await _vectorDoc.GetPageDisplayListAsync(pageNumber, ct).ConfigureAwait(false);
                if (displayList.Features.HasFlag(PdfFeatureSet.Text))
                {
                    var segments = ExtractCharSegmentsFromDisplayList(displayList);
                    if (segments.Count > 0) return segments;
                }
            }
            catch { }
        }
        return await _pdfiumService.ExtractPageTextSegmentsAsync(pageNumber, ct).ConfigureAwait(false);
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

    public Task ExportPagesToImagesAsync(
        string outputDirectory,
        string fileNamePrefix,
        int startPage,
        int endPage,
        string format = "PNG",
        int dpi = 300,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        _pdfiumService.ExportPagesToImagesAsync(outputDirectory, fileNamePrefix, startPage, endPage, format, dpi, progress, ct);

    public void PrintDocument(PrintDialog printDialog, int fromPage = 1, int toPage = -1, int rotationAngle = 0) =>
        _pdfiumService.PrintDocument(printDialog, fromPage, toPage, rotationAngle);

    public List<AnnotationModel> LoadExistingAnnotations() =>
        _pdfiumService.LoadExistingAnnotations();

    public Task SaveAnnotatedDocumentAsync(
        string targetPath,
        AnnotationSaveMode mode,
        IEnumerable<AnnotationModel> annotations,
        string? originalPath = null,
        CancellationToken ct = default) =>
        _pdfiumService.SaveAnnotatedDocumentAsync(targetPath, mode, annotations, originalPath, ct);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _vectorDoc?.Dispose();
            _vectorRenderer.Dispose();
            _d2d?.Dispose();
            _pdfiumService.Dispose();
        }
    }
}
