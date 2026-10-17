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
    private bool _disposed;

    public string FilePath { get; }
    public DocumentMetadata Metadata { get; }
    public int PageCount => _pageTree.Count;
    public bool IsOpen => !_disposed;

    public PdfObjectResolver Resolver => _resolver;
    public PdfXrefTable XrefTable => _xrefTable;

    /// <summary>True when the xref was rebuilt by scanning a damaged file.</summary>
    public bool WasRepaired => _xrefTable.WasReconstructed;
    public PdfPageTree PageTree => _pageTree;

    /// <summary>True when the file is encrypted (and was opened with a valid password).</summary>
    public bool IsEncrypted => Security != null;

    /// <summary>The Standard security handler of an encrypted document (permissions, revision), else null.</summary>
    public PdfEngine.Vector.Security.PdfSecurityHandler? Security { get; private init; }

    /// <summary>The version in the file header ("%PDF-1.7" gives "1.7"), or null when the header has none.</summary>
    public string? HeaderVersion { get; private init; }

    /// <summary>The document catalog (/Root).</summary>
    public PdfDictionary Catalog => _catalog;

    public static async ValueTask<PdfVectorDocument> OpenAsync(
        string filePath,
        PdfSecurityLimits? limits = null,
        CancellationToken cancellationToken = default,
        string? password = null,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var memSource = new MemoryByteSource(bytes);
        return OpenCore(memSource, filePath, limits, password, certificates);
    }

    public static async ValueTask<PdfVectorDocument> OpenAsync(
        byte[] pdfBytes,
        string filePath = "Memory.pdf",
        PdfSecurityLimits? limits = null,
        CancellationToken cancellationToken = default,
        string? password = null,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null)
    {
        var memSource = new MemoryByteSource(pdfBytes);
        return await Task.Run(() => OpenCore(memSource, filePath, limits, password, certificates), cancellationToken).ConfigureAwait(false);
    }

    public static PdfVectorDocument Open(
        IPdfByteSource source,
        string filePath = "Source.pdf",
        PdfSecurityLimits? limits = null,
        string? password = null,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates = null)
    {
        return OpenCore(source, filePath, limits, password, certificates);
    }

    private static PdfVectorDocument OpenCore(
        IPdfByteSource source,
        string filePath,
        PdfSecurityLimits? limits,
        string? password,
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates)
    {
        try
        {
            return OpenCoreUnchecked(source, filePath, limits, password, certificates);
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
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2>? certificates)
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

        var pageTree = new PdfPageTree(resolver, limits);
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
        };
        _displayListCache = new DisplayListCache(limits.MaxCachedDisplayListCommands);

        for (int i = 0; i < pageTree.Pages.Count; i++)
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
            var displayList = await Task.Run(() => _interpreter.BuildDisplayList(pageNode, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            _displayListCache.Add(pageNumber, displayList);
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _displayListCache.Clear();
            _source.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Single page representation for IPdfVectorDocument.
/// </summary>
public sealed class PdfVectorPage : IPdfPage
{
    public int PageNumber { get; }
    public PageInfo Info { get; }
    public PdfVectorDocument Document { get; }

    public PdfVectorPage(int pageNumber, PageInfo info, PdfVectorDocument document)
    {
        PageNumber = pageNumber;
        Info = info;
        Document = document;
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
