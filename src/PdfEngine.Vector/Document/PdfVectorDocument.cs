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

    public static async ValueTask<PdfVectorDocument> OpenAsync(
        string filePath,
        PdfSecurityLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var memSource = new MemoryByteSource(bytes);
        return OpenCore(memSource, filePath, limits);
    }

    public static async ValueTask<PdfVectorDocument> OpenAsync(
        byte[] pdfBytes,
        string filePath = "Memory.pdf",
        PdfSecurityLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var memSource = new MemoryByteSource(pdfBytes);
        return await Task.Run(() => OpenCore(memSource, filePath, limits), cancellationToken).ConfigureAwait(false);
    }

    public static PdfVectorDocument Open(
        IPdfByteSource source,
        string filePath = "Source.pdf",
        PdfSecurityLimits? limits = null)
    {
        return OpenCore(source, filePath, limits);
    }

    private static PdfVectorDocument OpenCore(
        IPdfByteSource source,
        string filePath,
        PdfSecurityLimits? limits)
    {
        limits ??= PdfSecurityLimits.Default;
        var xref = new PdfXrefTable(limits);
        xref.Load(source);

        var resolver = new PdfObjectResolver(source, xref, limits);

        // No security handler yet (05_PDF_CORE "Encryption"): strings and streams would decode to
        // ciphertext and render as garbage, so refuse with a typed error and let the host fall back.
        if (xref.Trailer != null && xref.Trailer.ContainsKey("Encrypt"))
        {
            throw new PdfEncryptedDocumentException();
        }

        if (xref.Trailer == null || !xref.Trailer.TryGetValue("Root", out var rootRef))
        {
            throw new InvalidDataException("PDF document contains no valid trailer or /Root catalog.");
        }

        var catalogObj = resolver.Resolve(rootRef);
        if (catalogObj is not PdfDictionary catalog)
        {
            throw new InvalidDataException("PDF /Root catalog could not be resolved.");
        }

        var pageTree = new PdfPageTree(resolver, limits);
        pageTree.Load(catalog);

        // Resolve basic metadata
        var meta = ExtractMetadata(xref.Trailer, resolver, filePath, source.Length, pageTree.Count);

        return new PdfVectorDocument(source, filePath, meta, xref, resolver, pageTree, catalog, limits);
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
        _interpreter = new PdfContentInterpreter(_resolver, null, _limits);
        _displayListCache = new DisplayListCache(limits.MaxCachedDisplayListCommands);

        for (int i = 0; i < pageTree.Pages.Count; i++)
        {
            _pageDictToNumber[pageTree.Pages[i].Dictionary] = i + 1;
        }
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

    public ValueTask<IReadOnlyList<BookmarkItem>> GetBookmarksAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_catalog.TryGetValue("Outlines", out var outlinesObj))
        {
            return ValueTask.FromResult<IReadOnlyList<BookmarkItem>>(Array.Empty<BookmarkItem>());
        }

        var outlines = _resolver.Resolve(outlinesObj) as PdfDictionary;
        if (outlines == null || !outlines.TryGetValue("First", out var firstRef))
        {
            return ValueTask.FromResult<IReadOnlyList<BookmarkItem>>(Array.Empty<BookmarkItem>());
        }

        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var result = TraverseOutlineList(firstRef, visited, depth: 0);
        return ValueTask.FromResult<IReadOnlyList<BookmarkItem>>(result);
    }

    private List<BookmarkItem> TraverseOutlineList(PdfObject? firstItemRef, HashSet<PdfDictionary> visited, int depth)
    {
        var list = new List<BookmarkItem>();
        if (depth > 50 || firstItemRef == null)
            return list;

        var currentRef = firstItemRef;
        while (currentRef != null)
        {
            var itemObj = _resolver.Resolve(currentRef) as PdfDictionary;
            if (itemObj == null || !visited.Add(itemObj))
                break;

            string title = ExtractString(itemObj["Title"]);
            int targetPage = 1;
            double? targetX = null;
            double? targetY = null;
            double? targetZoom = null;

            // Check /Dest or /A
            PdfObject? destObj = _resolver.Resolve(itemObj["Dest"]);
            if (destObj == null && itemObj.TryGetValue("A", out var aObj))
            {
                var actionDict = _resolver.Resolve(aObj) as PdfDictionary;
                if (actionDict != null && actionDict.GetName("S") == "GoTo")
                {
                    destObj = _resolver.Resolve(actionDict["D"]);
                }
            }

            if (destObj is PdfArray destArr && destArr.Count > 0)
            {
                var targetPageObj = _resolver.Resolve(destArr[0]);
                if (targetPageObj is PdfDictionary targetDict && _pageDictToNumber.TryGetValue(targetDict, out int pNum))
                {
                    targetPage = pNum;
                }
                else if (destArr[0].TryGetInteger(out long pIdx))
                {
                    targetPage = Math.Clamp((int)pIdx + 1, 1, Math.Max(1, _pageTree.Count));
                }

                if (destArr.Count >= 4 && destArr[1].TryGetName(out string mode) && mode == "XYZ")
                {
                    if (destArr[2].TryGetNumber(out double x)) targetX = x;
                    if (destArr[3].TryGetNumber(out double y)) targetY = y;
                    if (destArr.Count >= 5 && destArr[4].TryGetNumber(out double z) && z > 0) targetZoom = z;
                }
            }

            var bookmark = new BookmarkItem
            {
                Title = title,
                TargetPageNumber = targetPage,
                TargetX = targetX,
                TargetY = targetY,
                TargetZoom = targetZoom
            };

            // Recursively resolve children if any
            if (itemObj.TryGetValue("First", out var childFirst))
            {
                bookmark.Children = TraverseOutlineList(childFirst, visited, depth + 1);
            }

            list.Add(bookmark);

            // Move to next sibling
            if (itemObj.TryGetValue("Next", out var nextRef))
            {
                currentRef = nextRef;
            }
            else
            {
                break;
            }
        }

        return list;
    }

    private string ExtractString(PdfObject? obj)
    {
        var resolved = _resolver.Resolve(obj);
        if (resolved is not PdfString str)
            return string.Empty;

        var span = str.RawBytes.Span;
        if (span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode.GetString(span.Slice(2));
        }

        if (span.Length >= 2 && span[0] == 0xFF && span[1] == 0xFE)
        {
            return System.Text.Encoding.Unicode.GetString(span.Slice(2));
        }

        return System.Text.Encoding.Latin1.GetString(span);
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
