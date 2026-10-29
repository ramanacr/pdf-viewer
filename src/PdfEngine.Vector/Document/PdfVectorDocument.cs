using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Documents;
using PdfEngine.Geometry;
using PdfEngine.Vector.Content;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Xref;

namespace PdfEngine.Vector.Document;

/// <summary>
/// Vector-first PDF document implementation conforming to IPdfVectorDocument.
/// </summary>
public sealed class PdfVectorDocument : IPdfVectorDocument
{
    private readonly IPdfByteSource _source;
    private readonly PdfSecurityLimits _limits;
    private readonly PdfXrefTable _xrefTable;
    private readonly PdfObjectResolver _resolver;
    private readonly PdfPageTree _pageTree;
    private readonly PdfContentInterpreter _interpreter;
    private readonly PdfDictionary _catalog;
    private readonly Dictionary<PdfDictionary, int> _pageDictToNumber = new(ReferenceEqualityComparer.Instance);
    private readonly DisplayListCache _displayListCache;
    // Resolver, font cache and byte source are not re-entrant across threads: one page builds at a time.
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    /// <summary>Held while the resolver and byte source are in use by a page build (display list or text page).</summary>
    internal SemaphoreSlim BuildGate => _buildGate;
    private bool _disposed;

    /// <inheritdoc/>
    public string FilePath { get; }
    /// <inheritdoc/>
    public DocumentMetadata Metadata { get; }
    /// <inheritdoc/>
    public int PageCount => _pageTree.Count;
    /// <inheritdoc/>
    public bool IsOpen => !_disposed;

    /// <summary>Resolves indirect objects of the file (low-level access).</summary>
    public PdfObjectResolver Resolver => _resolver;
    /// <summary>The cross-reference table of the file (low-level access).</summary>
    public PdfXrefTable XrefTable => _xrefTable;

    /// <summary>True when the xref was rebuilt by scanning a damaged file.</summary>
    public bool WasRepaired => _xrefTable.WasReconstructed;
    /// <summary>The flattened page tree (low-level access).</summary>
    public PdfPageTree PageTree => _pageTree;

    /// <summary>True when the file is encrypted (and was opened with a valid password).</summary>
    public bool IsEncrypted => Security != null;

    /// <summary>The Standard security handler of an encrypted document (permissions, revision), else null.</summary>
    public PdfEngine.Vector.Security.PdfSecurityHandler? Security { get; private init; }

    /// <summary>The version in the file header ("%PDF-1.7" gives "1.7"), or null when the header has none.</summary>
    public string? HeaderVersion { get; private init; }

    /// <summary>The document catalog (/Root).</summary>
    public PdfDictionary Catalog => _catalog;

    /// <summary>Reads a PDF file into memory and opens it.</summary>
    /// <param name="filePath">The path of the PDF file.</param>
    /// <param name="limits">Ceilings for hostile input; null uses <see cref="PdfSecurityLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the file read.</param>
    /// <param name="password">The user or owner password of an encrypted document, or null.</param>
    /// <param name="certificates">For public-key encryption: certificates with private keys to try; null uses <see cref="PdfEngine.Vector.Security.PdfPublicKeySecurityHandler.CertificateSource"/> (the personal certificate stores).</param>
    /// <returns>The open document. Dispose it when you no longer need it.</returns>
    /// <exception cref="PdfSyntaxException">The file is not a PDF, or it is damaged beyond repair.</exception>
    /// <exception cref="PdfEncryptedDocumentException">The password or certificate is missing or wrong, or the security handler is not supported.</exception>
    /// <exception cref="PdfResourceLimitException">The file exceeds a limit of <paramref name="limits"/>.</exception>
    public static async ValueTask<PdfVectorDocument> OpenAsync(
        string filePath,
        PdfSecurityLimits? limits = null,
        CancellationToken cancellationToken = default,
        string? password = null,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var memSource = new PdfMemoryByteSource(bytes);
        return OpenCore(memSource, filePath, limits, password, certificates);
    }

    /// <summary>Opens a PDF from bytes in memory. The document keeps a reference to <paramref name="pdfBytes"/>; do not change the array.</summary>
    /// <param name="pdfBytes">The complete PDF file.</param>
    /// <param name="filePath">A name for the document, reported by <see cref="FilePath"/>.</param>
    /// <param name="limits">Ceilings for hostile input; null uses <see cref="PdfSecurityLimits.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the operation before parsing starts.</param>
    /// <param name="password">The user or owner password of an encrypted document, or null.</param>
    /// <param name="certificates">For public-key encryption: certificates with private keys to try; null uses <see cref="PdfEngine.Vector.Security.PdfPublicKeySecurityHandler.CertificateSource"/> (the personal certificate stores).</param>
    /// <returns>The open document. Dispose it when you no longer need it.</returns>
    /// <exception cref="PdfSyntaxException">The data is not a PDF, or it is damaged beyond repair.</exception>
    /// <exception cref="PdfEncryptedDocumentException">The password or certificate is missing or wrong, or the security handler is not supported.</exception>
    /// <exception cref="PdfResourceLimitException">The file exceeds a limit of <paramref name="limits"/>.</exception>
    public static async ValueTask<PdfVectorDocument> OpenAsync(
        byte[] pdfBytes,
        string filePath = "Memory.pdf",
        PdfSecurityLimits? limits = null,
        CancellationToken cancellationToken = default,
        string? password = null,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null)
    {
        var memSource = new PdfMemoryByteSource(pdfBytes);
        return await Task.Run(() => OpenCore(memSource, filePath, limits, password, certificates), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a PDF from a byte source, synchronously. The document owns <paramref name="source"/> and disposes it.</summary>
    /// <param name="source">The PDF data, for example a <see cref="PdfMemoryByteSource"/> or a <see cref="PdfFileByteSource"/>.</param>
    /// <param name="filePath">A name for the document, reported by <see cref="FilePath"/>.</param>
    /// <param name="limits">Ceilings for hostile input; null uses <see cref="PdfSecurityLimits.Default"/>.</param>
    /// <param name="password">The user or owner password of an encrypted document, or null.</param>
    /// <param name="certificates">For public-key encryption: certificates with private keys to try; null uses <see cref="PdfEngine.Vector.Security.PdfPublicKeySecurityHandler.CertificateSource"/> (the personal certificate stores).</param>
    /// <returns>The open document. Dispose it when you no longer need it.</returns>
    /// <exception cref="PdfSyntaxException">The data is not a PDF, or it is damaged beyond repair.</exception>
    /// <exception cref="PdfEncryptedDocumentException">The password or certificate is missing or wrong, or the security handler is not supported.</exception>
    /// <exception cref="PdfResourceLimitException">The file exceeds a limit of <paramref name="limits"/>.</exception>
    public static PdfVectorDocument Open(
        IPdfByteSource source,
        string filePath = "Source.pdf",
        PdfSecurityLimits? limits = null,
        string? password = null,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null)
    {
        return OpenCore(source, filePath, limits, password, certificates);
    }

    /// <summary>
    /// <see cref="Open"/> for a document read by ranges (<see cref="PdfRangedDocument"/>): pages'
    /// content streams are resolved when first used, so opening reads only the page dictionaries.
    /// With <paramref name="lazyPages"/>, the page tree is lazy (<see cref="PdfPageTree.IsLazy"/>):
    /// opening reads only its root, and each page is found when it is first asked for.
    /// </summary>
    internal static PdfVectorDocument OpenDeferred(IPdfByteSource source, string filePath, PdfSecurityLimits? limits, string? password, bool lazyPages = false) =>
        OpenCore(source, filePath, limits, password, null, deferContents: true, lazyPages);

    private static PdfVectorDocument OpenCore(
        IPdfByteSource source,
        string filePath,
        PdfSecurityLimits? limits,
        string? password,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates,
        bool deferContents = false,
        bool lazyPages = false)
    {
        try
        {
            return OpenCoreUnchecked(source, filePath, limits, password, certificates, deferContents, lazyPages);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Hostile-input boundary: data-shape failures surface as one typed syntax error.
            throw new PdfSyntaxException($"Malformed PDF structure ({ex.GetType().Name}).", ex);
        }
    }

    private static PdfVectorDocument OpenCoreUnchecked(
        IPdfByteSource source,
        string filePath,
        PdfSecurityLimits? limits,
        string? password,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates,
        bool deferContents,
        bool lazyPages)
    {
        limits ??= PdfSecurityLimits.Default;
        var xref = new PdfXrefTable(limits);
        xref.Load(source);

        var resolver = new PdfObjectResolver(source, xref, limits);

        // Standard security handler (7.6.4): authenticate, then every object is decrypted as it is read.
        PdfEngine.Vector.Security.PdfSecurityHandler? security = null;
        if (xref.Trailer != null && xref.Trailer.TryGetValue("Encrypt", out var encryptObj) && encryptObj != null)
        {
            if (resolver.Resolve(encryptObj) is not PdfDictionary encrypt)
                throw new PdfEncryptedDocumentException("The /Encrypt dictionary cannot be read.");
            security = PdfEngine.Vector.Security.PdfSecurityHandler.Create(encrypt, resolver.Resolve(xref.Trailer["ID"]) as PdfArray, resolver.Resolve);
            switch (security)
            {
                case PdfEngine.Vector.Security.PdfStandardSecurityHandler standard when !standard.Authenticate(password):
                    throw new PdfEncryptedDocumentException(
                        string.IsNullOrEmpty(password) ? "A password is required to open this document." : "The password is incorrect.",
                        passwordRequired: true);
                case PdfEngine.Vector.Security.PdfPublicKeySecurityHandler publicKey
                    when !publicKey.Authenticate(certificates ?? PdfEngine.Vector.Security.PdfPublicKeySecurityHandler.CertificateSource()):
                    throw new PdfEncryptedDocumentException(
                        "This document is encrypted for specific recipients; no installed certificate can open it.",
                        certificateRequired: true);
            }
            resolver.SetSecurityHandler(security, encryptObj is PdfIndirectRef er ? er.ObjectNumber : -1);
        }

        PdfDictionary? catalog = ResolveCatalog(xref, resolver);
        if (catalog == null && !xref.WasReconstructed)
        {
            // The xref chain parsed but its /Root is missing or dangling: rebuild by scanning once.
            xref.Rebuild(source);
            catalog = ResolveCatalog(xref, resolver);
        }
        if (catalog == null)
        {
            throw new PdfSyntaxException("PDF document has no resolvable /Root catalog.");
        }

        var pageTree = new PdfPageTree(resolver, limits) { DeferContents = deferContents, IsLazy = lazyPages, MaxLazyPages = xref.Entries.Count };
        pageTree.Load(catalog);

        // Resolve basic metadata
        var meta = ExtractMetadata(xref.Trailer!, resolver, filePath, source.Length, pageTree.Count);

        return new PdfVectorDocument(source, filePath, meta, xref, resolver, pageTree, catalog, limits)
        {
            Security = security,
            HeaderVersion = ReadHeaderVersion(source),
        };
    }

    /// <summary>"%PDF-M.m" within the first kilobyte (7.5.2 allows leading junk, as readers do).</summary>
    private static string? ReadHeaderVersion(IPdfByteSource source)
    {
        var head = source.ReadMemory(0, (int)Math.Min(1024, source.Length)).Span;
        ReadOnlySpan<byte> marker = "%PDF-"u8;
        int at = head.IndexOf(marker);
        if (at < 0 || at + marker.Length + 3 > head.Length) return null;
        var v = head.Slice(at + marker.Length, 3);
        return char.IsAsciiDigit((char)v[0]) && v[1] == '.' && char.IsAsciiDigit((char)v[2])
            ? $"{(char)v[0]}.{(char)v[2]}"
            : null;
    }

    private static PdfDictionary? ResolveCatalog(PdfXrefTable xref, PdfObjectResolver resolver)
    {
        if (xref.Trailer == null || !xref.Trailer.TryGetValue("Root", out var rootRef))
            return null;
        return resolver.Resolve(rootRef) as PdfDictionary;
    }

    private PdfVectorDocument(
        IPdfByteSource source,
        string filePath,
        DocumentMetadata metadata,
        PdfXrefTable xrefTable,
        PdfObjectResolver resolver,
        PdfPageTree pageTree,
        PdfDictionary catalog,
        PdfSecurityLimits limits)
    {
        _source = source;
        FilePath = filePath;
        Metadata = metadata;
        _xrefTable = xrefTable;
        _resolver = resolver;
        _pageTree = pageTree;
        _catalog = catalog;
        _limits = limits;
        _interpreter = new PdfContentInterpreter(_resolver, new PdfEngine.Vector.Fonts.PdfFontResolver(_resolver, _limits), _limits)
        {
            HiddenOptionalContentGroups = ResolveHiddenOptionalContent(catalog, resolver),
            WidgetAppearance = GeneratedWidgetAppearance,
            HiddenAnnotation = n => _hiddenAnnotations.Contains(n),
        };
        _displayListCache = new DisplayListCache(limits.MaxCachedDisplayListCommands);

        // A lazy tree finds its pages when they are asked for: none are read here.
        for (int i = 0; !pageTree.IsLazy && i < pageTree.Pages.Count; i++)
        {
            _pageDictToNumber[pageTree.Pages[i].Dictionary] = i + 1;
        }
    }

    /// <summary>
    /// Optional-content groups that are OFF in the default viewing configuration /OCProperties /D
    /// (ISO 32000-2 8.11.4.3): /BaseState, then /ON and /OFF overrides.
    /// </summary>
    private static IReadOnlySet<PdfDictionary>? ResolveHiddenOptionalContent(PdfDictionary catalog, PdfObjectResolver resolver)
    {
        if (resolver.Resolve(catalog["OCProperties"]) is not PdfDictionary ocProps)
            return null;

        var all = new List<PdfDictionary>();
        if (resolver.Resolve(ocProps["OCGs"]) is PdfArray ocgs)
        {
            foreach (var item in ocgs)
                if (resolver.Resolve(item) is PdfDictionary g) all.Add(g);
        }

        if (resolver.Resolve(ocProps["D"]) is not PdfDictionary config)
            return null;

        var hidden = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (config.GetName("BaseState") == "OFF")
        {
            foreach (var g in all) hidden.Add(g);
        }

        if (resolver.Resolve(config["ON"]) is PdfArray on)
        {
            foreach (var item in on)
                if (resolver.Resolve(item) is PdfDictionary g) hidden.Remove(g);
        }

        if (resolver.Resolve(config["OFF"]) is PdfArray off)
        {
            foreach (var item in off)
                if (resolver.Resolve(item) is PdfDictionary g) hidden.Add(g);
        }

        return hidden.Count > 0 ? hidden : null;
    }

    /// <inheritdoc/>
    public ValueTask<PageInfo> GetPageInfoAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        int idx = pageNumber - 1;
        if (idx < 0 || idx >= _pageTree.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} is out of bounds (1..{_pageTree.Count}).");
        }

        var pageNode = _pageTree.Pages[idx];
        var info = new PageInfo
        {
            PageNumber = pageNumber,
            WidthPoints = pageNode.PageSize.Width,
            HeightPoints = pageNode.PageSize.Height,
            RotationDegrees = pageNode.RotationDegrees
        };
        return ValueTask.FromResult(info);
    }

    /// <inheritdoc/>
    public ValueTask<IPdfPage> GetPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        int idx = pageNumber - 1;
        if (idx < 0 || idx >= _pageTree.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        var pageNode = _pageTree.Pages[idx];
        var info = new PageInfo
        {
            PageNumber = pageNumber,
            WidthPoints = pageNode.PageSize.Width,
            HeightPoints = pageNode.PageSize.Height,
            RotationDegrees = pageNode.RotationDegrees
        };

        IPdfPage page = new PdfVectorPage(pageNumber, info, this);
        return ValueTask.FromResult(page);
    }

    /// <summary>
    /// The outline as bookmarks: destinations resolved through named destinations (the /Dests
    /// name tree and the catalog's /Dests) and go-to actions, as PDFium resolves them.
    /// </summary>
    public ValueTask<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var outline = PdfEngine.Vector.Services.PdfDocumentInfo.ReadOutline(this);
        return ValueTask.FromResult<IReadOnlyList<BookmarkItem>>(outline.Select(ToBookmark).ToList());
    }

    private BookmarkItem ToBookmark(PdfEngine.Vector.Services.PdfOutlineItem item)
    {
        var dest = item.Destination ?? (item.Action?.Kind == PdfEngine.Vector.Services.PdfActionKind.GoTo ? item.Action.Destination : null);
        double? Param(int i) => dest != null && dest.Fit == PdfEngine.Vector.Services.PdfDestinationFit.XYZ && i < dest.Parameters.Count ? dest.Parameters[i] : null;
        double? zoom = Param(2);
        return new BookmarkItem
        {
            Title = item.Title,
            TargetPageNumber = item.PageIndex >= 0 ? Math.Clamp(item.PageIndex + 1, 1, Math.Max(1, _pageTree.Count)) : 1,
            TargetX = Param(0),
            TargetY = Param(1),
            TargetZoom = zoom > 0 ? zoom : null,
            Children = item.Children.Select(ToBookmark).ToList(),
        };
    }

    // ------------------------------------------------------------------ form field appearances

    private readonly object _formSync = new();
    private bool _formRead;
    private PdfEngine.Vector.Forms.PdfAcroForm? _form;
    private Dictionary<int, (PdfEngine.Vector.Forms.PdfFormField Field, PdfEngine.Vector.Forms.PdfFormWidget Widget)>? _widgets;
    private readonly Dictionary<int, PdfStream?> _generatedAppearances = new();

    /// <summary>
    /// The appearance drawn for a widget whose own cannot be used: generated from the field's value
    /// when the form has /NeedAppearances (text and choice fields) or the widget has no appearance,
    /// as PDFium and Acrobat generate it. Built once per widget, in memory only.
    /// </summary>
    private PdfStream? GeneratedWidgetAppearance(int widgetObject, bool hasAppearance)
    {
        lock (_formSync)
        {
            if (!_formRead)
            {
                _formRead = true;
                try
                {
                    _form = PdfEngine.Vector.Forms.PdfAcroForm.Read(this);
                }
                catch (Exception ex) when (ex is PdfVectorException and not PdfDataNotLoadedException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
                {
                    _form = null; // a form that cannot be read: widgets draw their own appearances
                }
                if (_form != null)
                {
                    _widgets = new();
                    foreach (var field in _form.Fields)
                        foreach (var widget in field.Widgets)
                            _widgets.TryAdd(widget.ObjectNumber, (field, widget));
                }
            }
            if (_form == null || _widgets == null || !_widgets.TryGetValue(widgetObject, out var entry))
                return null;
            if (hasAppearance && !_form.NeedAppearances)
                return null;
            if (!_generatedAppearances.TryGetValue(widgetObject, out var stream))
            {
                stream = PdfEngine.Vector.Forms.PdfFormFiller.GenerateAppearance(_form, entry.Field, entry.Widget, _resolver, hasAppearance);
                _generatedAppearances[widgetObject] = stream;
            }
            return stream;
        }
    }

    /// <summary>
    /// The page as a printer gets it: annotations with the Print flag only, NoView ones included
    /// (ISO 32000-2 12.5.3). The screen display list is returned when no annotation on the page
    /// prints differently from how it shows; otherwise a list is built for the job (not cached).
    /// </summary>
    public async ValueTask<IPdfDisplayList> GetPagePrintDisplayListAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        int idx = pageNumber - 1;
        if (idx < 0 || idx >= _pageTree.Count)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var pageNode = _pageTree.Pages[idx];
        if (!PrintsDifferently(pageNode) && _hiddenAnnotations.Count == 0)
            return await GetPageDisplayListAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _interpreter.BuildDisplayList(pageNode, cancellationToken, forPrinting: true), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// The page as it prints (see <see cref="GetPagePrintDisplayListAsync"/>), with its colours replaced by the
    /// values of the plates <paramref name="request"/> asks for (print production: separations). Not cached.
    /// </summary>
    public ValueTask<IPdfDisplayList> GetPlateDisplayListAsync(int pageNumber, PdfEngine.Vector.PrintProduction.PdfPlateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildForPrintProductionAsync(pageNumber, request, null, cancellationToken);
    }

    /// <summary>
    /// Interprets the page as it prints and reports every painting operation to <paramref name="observer"/>
    /// (print production: colorants, preflight). Returns the print display list that was built.
    /// </summary>
    internal ValueTask<IPdfDisplayList> ObservePageAsync(int pageNumber, PdfEngine.Vector.PrintProduction.IPdfPaintObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return BuildForPrintProductionAsync(pageNumber, null, observer, cancellationToken);
    }

    private async ValueTask<IPdfDisplayList> BuildForPrintProductionAsync(int pageNumber, PdfEngine.Vector.PrintProduction.PdfPlateRequest? plate,
        PdfEngine.Vector.PrintProduction.IPdfPaintObserver? observer, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        int idx = pageNumber - 1;
        if (idx < 0 || idx >= _pageTree.Count)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var pageNode = _pageTree.Pages[idx];
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _interpreter.BuildDisplayList(pageNode, cancellationToken, forPrinting: true, plate, observer), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    private bool PrintsDifferently(PdfPageNode page)
    {
        if (_resolver.Resolve(page.Dictionary["Annots"]) is not PdfArray annots)
            return false;
        foreach (var item in annots)
        {
            if (_resolver.Resolve(item) is not PdfDictionary annot || annot.GetName("Subtype") is "Popup" or "Link")
                continue;
            long flags = annot.GetInteger("F") ?? 0;
            if ((flags & 2) == 0 && ((flags & 4) == 0 || (flags & 32) != 0))
                return true;
        }
        return false;
    }

    private volatile IReadOnlySet<int> _hiddenAnnotations = new HashSet<int>();
    private int _hiddenVersion;

    /// <summary>The annotations (by object number) the screen display lists leave out.</summary>
    public IReadOnlySet<int> HiddenAnnotations => _hiddenAnnotations;

    /// <summary>
    /// Leaves these annotations (by object number) out of the screen display lists, for a viewer
    /// that draws them itself while they are being changed; printing still draws them. Returns
    /// the pages whose display lists were dropped because of the change.
    /// </summary>
    public IReadOnlyList<int> SetHiddenAnnotations(IEnumerable<int> objectNumbers)
    {
        ArgumentNullException.ThrowIfNull(objectNumbers);
        var next = new HashSet<int>(objectNumbers.Where(n => n > 0));
        var before = _hiddenAnnotations;
        if (next.SetEquals(before)) return Array.Empty<int>();
        var changed = new HashSet<int>(next);
        changed.SymmetricExceptWith(before);
        _hiddenAnnotations = next;
        Interlocked.Increment(ref _hiddenVersion);
        _displayListCache.Clear();
        var pages = new List<int>();
        for (int i = 0; i < _pageTree.Count; i++)
        {
            if (_resolver.Resolve(_pageTree.Pages[i].Dictionary["Annots"]) is not PdfArray annots) continue;
            if (annots.Any(a => a is PdfIndirectRef r && changed.Contains(r.ObjectNumber))) pages.Add(i + 1);
        }
        return pages;
    }

    /// <summary>
    /// Interprets the page's content stream, annotations and form fields into an immutable display list,
    /// on a worker thread. Recent lists are cached (see <see cref="PdfSecurityLimits.MaxCachedDisplayListCommands"/>),
    /// so a second call for the same page is cheap.
    /// </summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the interpretation.</param>
    /// <returns>
    /// The display list, ready for a renderer such as <c>SkiaVectorRenderer</c>. Content that is damaged or
    /// exceeds a per-page budget of <see cref="PdfSecurityLimits"/> does not throw: the list keeps what was
    /// drawn before it and has <see cref="PdfFeatureSet.Recovered"/> in its features.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> is less than 1 or greater than <see cref="PageCount"/>.</exception>
    public async ValueTask<IPdfDisplayList> GetPageDisplayListAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_displayListCache.TryGet(pageNumber, out var cached))
        {
            return cached;
        }

        int idx = pageNumber - 1;
        if (idx < 0 || idx >= _pageTree.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        // Interpretation is CPU-bound; never run it on the caller's (possibly UI) thread.
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_displayListCache.TryGet(pageNumber, out cached))
                return cached;

            var pageNode = _pageTree.Pages[idx];
            int version = Volatile.Read(ref _hiddenVersion);
            var displayList = await Task.Run(() => _interpreter.BuildDisplayList(pageNode, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            // A list built while the hidden annotations changed is used once, not kept.
            if (version == Volatile.Read(ref _hiddenVersion)) _displayListCache.Add(pageNumber, displayList);
            return displayList;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>
    /// LRU cache of page display lists bounded by total command count (08_PERFORMANCE: the
    /// display-list cache has its own complexity budget). The most recent page is always kept.
    /// </summary>
    private sealed class DisplayListCache
    {
        private readonly long _maxCommands;
        private readonly LinkedList<(int Page, IPdfDisplayList List)> _lru = new();
        private readonly Dictionary<int, LinkedListNode<(int Page, IPdfDisplayList List)>> _map = new();
        private long _commands;

        public DisplayListCache(long maxCommands) => _maxCommands = Math.Max(1, maxCommands);

        public bool TryGet(int page, out IPdfDisplayList list)
        {
            lock (_lru)
            {
                if (_map.TryGetValue(page, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    list = node.Value.List;
                    return true;
                }
            }
            list = null!;
            return false;
        }

        public void Add(int page, IPdfDisplayList list)
        {
            lock (_lru)
            {
                if (_map.ContainsKey(page))
                    return;
                _map[page] = _lru.AddFirst((page, list));
                _commands += list.Commands.Count;
                while (_commands > _maxCommands && _lru.Count > 1)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Page);
                    _commands -= last.Value.List.Commands.Count;
                }
            }
        }

        public void Clear()
        {
            lock (_lru)
            {
                _lru.Clear();
                _map.Clear();
                _commands = 0;
            }
        }
    }

    private static DocumentMetadata ExtractMetadata(
        PdfDictionary trailer,
        PdfObjectResolver resolver,
        string filePath,
        long fileSizeBytes,
        int pageCount)
    {
        string title = string.Empty;
        string author = string.Empty;
        string subject = string.Empty;
        string creator = string.Empty;
        string producer = string.Empty;

        if (trailer.TryGetValue("Info", out var infoRef))
        {
            var infoObj = resolver.Resolve(infoRef);
            if (infoObj is PdfDictionary infoDict)
            {
                title = infoDict.GetString("Title") ?? string.Empty;
                author = infoDict.GetString("Author") ?? string.Empty;
                subject = infoDict.GetString("Subject") ?? string.Empty;
                creator = infoDict.GetString("Creator") ?? string.Empty;
                producer = infoDict.GetString("Producer") ?? string.Empty;
            }
        }

        return new DocumentMetadata
        {
            Title = title,
            Author = author,
            Subject = subject,
            Creator = creator,
            Producer = producer,
            PageCount = pageCount,
            FileSizeBytes = fileSizeBytes,
            FilePath = filePath
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfVectorDocument));
    }

    /// <summary>Closes the document, clears its display-list cache and releases its byte source.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _displayListCache.Clear();
            _source.Dispose();
        }
    }

    /// <summary>Closes the document; the same as <see cref="Dispose"/>.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Single page representation for IPdfVectorDocument.
/// </summary>
internal sealed class PdfVectorPage : IPdfPage
{
    /// <inheritdoc/>
    public int PageNumber { get; }
    /// <inheritdoc/>
    public PageInfo Info { get; }
    /// <summary>The document that the page belongs to.</summary>
    public PdfVectorDocument Document { get; }

    /// <summary>Creates a page object. <see cref="PdfVectorDocument.GetPageAsync"/> creates pages; hosts rarely call this.</summary>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="info">The size and rotation of the page.</param>
    /// <param name="document">The document of the page.</param>
    public PdfVectorPage(int pageNumber, PageInfo info, PdfVectorDocument document)
    {
        PageNumber = pageNumber;
        Info = info;
        Document = document;
    }

    /// <summary>Does nothing: a page holds no resources of its own.</summary>
    public void Dispose() { }
    /// <summary>Does nothing: a page holds no resources of its own.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
