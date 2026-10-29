using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Xref;

namespace PdfEngine.Vector.Parsing;

/// <summary>Settings of a <see cref="PdfRangedDocument"/>.</summary>
public sealed class PdfRangedOptions
{
    /// <summary>The block size of the cache (default 4 KB).</summary>
    public int BlockSize { get; init; } = PdfChunkCache.DefaultBlockSize;

    /// <summary>The most fetched bytes kept in memory (default 64 MB). A page's own bytes are kept while it is parsed, whatever the cap.</summary>
    public long CacheBytes { get; init; } = PdfChunkCache.DefaultMaxBytes;

    /// <summary>The end of the file fetched first, for the cross-reference data and the trailer (default 1 MB).</summary>
    public int TailBytes { get; init; } = 1024 * 1024;

    /// <summary>The most bytes in one read (default 4 MB).</summary>
    public int MaxRequestBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The most reads at the same time (default 4).</summary>
    public int MaxConcurrentRequests { get; init; } = 4;

    /// <summary>A miss is fetched with at least this many bytes around it (default 64 KB).</summary>
    public int ReadAheadBytes { get; init; } = 64 * 1024;

    /// <summary>Reads closer than this are joined into one (default 16 KB).</summary>
    public long MergeGapBytes { get; init; } = PdfRangePlanner.DefaultMergeGap;

    /// <summary>How many times an operation is tried again after fetching what it missed, before the whole file is downloaded (default 12).</summary>
    public int MaxRounds { get; init; } = 12;

    /// <summary>The size of each read of <see cref="PdfRangedDocument.ReadAllAsync"/> (default 1 MB).</summary>
    public int CompletionRequestBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// The most bytes that the objects of one walk (the document's structure, or one page) may
    /// need (default 256 MB). A walk that would ask for more stops, and the file is read whole
    /// (<see cref="PdfRangedDocument.FallbackReason"/> says so).
    /// </summary>
    public long MaxWalkBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// The most bytes fetched in one round after a miss, and for the document's structure when it
    /// opens (default 32 MB). Past it, the file is read whole. The cache holds at most
    /// <see cref="CacheBytes"/> plus this while a document opens.
    /// </summary>
    public long MaxRoundBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>
    /// Find each page in the page tree when it is first needed (default true): opening reads the
    /// catalog and the root of the page tree only, and a page needs only the tree nodes on its
    /// path. False reads every page's dictionary while the document opens. A tree whose /Count
    /// values do not match its kids is read whole either way (<see cref="PdfRangedDocument.PageTreeFallbackReason"/>).
    /// </summary>
    public bool LazyPageTree { get; init; } = true;

    /// <summary>Bytes the host already has (for example the start of the file, or its end), as (offset, bytes) pairs.</summary>
    public IReadOnlyList<(long Offset, ReadOnlyMemory<byte> Data)> InitialData { get; init; } = [];
}

/// <summary>How much a <see cref="PdfRangedDocument"/> has fetched.</summary>
public readonly record struct PdfRangeStatistics(long BytesFetched, int Requests, long CachedBytes, long FileLength, bool IsComplete);

/// <summary>
/// A PDF opened from an <see cref="IPdfRangeReader"/> without reading the whole file first, as
/// Adobe's and pdf.js's viewers open a large file from a web server that answers range requests.
/// <list type="number">
/// <item>Opening fetches the end of the file (<see cref="PdfRangedOptions.TailBytes"/>) for the
/// cross-reference data, then the objects of the document's structure: the catalog and the root
/// of the page tree (<see cref="PdfClosureKind.Structure"/>), and the tree nodes on the path to
/// page 1. The page tree is lazy (<see cref="PdfPageTree.IsLazy"/>, <see cref="PdfRangedOptions.LazyPageTree"/>):
/// the page count is the root's /Count, and each page is found when it is first needed, by its
/// path from the root. A tree whose /Count values do not match its kids is read whole instead
/// (<see cref="PageTreeFallbackReason"/>). Content streams are resolved when their page is drawn.</item>
/// <item><see cref="RunAsync"/> fetches the objects one page needs, level by level from the page
/// dictionary (<see cref="PdfClosureKind.Page"/>), then runs the operation (draw, read text) on the
/// parsed document synchronously, over the fetched blocks (<see cref="PdfChunkedByteSource"/>).</item>
/// <item>A read that still finds bytes missing is recorded; the bytes are fetched, the document is
/// parsed again over the cache (cheap: nothing is fetched twice) and the operation runs again.</item>
/// <item>When the cross-reference data is damaged (the parser had to rebuild it by scanning the whole
/// file) or an operation still misses bytes after <see cref="PdfRangedOptions.MaxRounds"/> tries,
/// the whole file is read and the document is opened from memory, as without range loading
/// (<see cref="FallbackReason"/> says why).</item>
/// </list>
/// The in-memory path (<see cref="PdfVectorDocument.OpenAsync(byte[], string, PdfSecurityLimits?, CancellationToken, string?, IEnumerable{System.Security.Cryptography.X509Certificates.X509Certificate2}?)"/>)
/// is not changed by any of this. Operations are serialized.
/// </summary>
public sealed class PdfRangedDocument : IDisposable
{
    private readonly IPdfRangeReader _reader;
    private readonly PdfRangedOptions _options;
    private readonly PdfChunkCache _cache;
    private readonly PdfSecurityLimits? _limits;
    private readonly string? _password;
    private readonly string _name;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _requests;
    private PdfRangePlanner? _planner;
    private byte[]? _bytes;
    private Task<byte[]>? _readAll;
    private byte[]? _partial;   // the whole read so far, kept when it fails
    private long _partialEnd;   // the bytes of _partial that are read (from 0)
    private long _bytesFetched;
    private int _requestCount;
    private volatile bool _disposed;
    // Dispose and the swaps of Document take it, so that no document is made after Dispose.
    private readonly object _stateSync = new();
    // Canceled by Dispose: reads in progress end.
    private readonly CancellationTokenSource _lifetime = new();

    private PdfRangedDocument(IPdfRangeReader reader, PdfRangedOptions options, string? password, PdfSecurityLimits? limits, string name)
    {
        _reader = reader;
        _options = options;
        _password = password;
        _limits = limits;
        _name = name;
        _cache = new PdfChunkCache(reader.Length, options.BlockSize, options.CacheBytes);
        _requests = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
        _lazyTree = options.LazyPageTree;
        foreach (var (offset, data) in options.InitialData) Seed(offset, data.Span);
    }

    /// <summary>The parsed document. It is replaced when the document is parsed again (after a miss, or when the file is complete): do not keep it across calls.</summary>
    public PdfVectorDocument Document { get; private set; } = null!;

    /// <summary>The fetched blocks.</summary>
    internal PdfChunkCache Cache => _cache;

    /// <summary>The length of the file.</summary>
    public long Length => _reader.Length;

    /// <summary>True when the whole file is in memory and <see cref="Document"/> was opened from it.</summary>
    public bool IsComplete => _completeDocument;
    private bool _completeDocument;
    private readonly object _readAllSync = new();

    /// <summary>The whole file, once it was read (<see cref="ReadAllAsync"/>, <see cref="CompleteAsync"/> or a fallback).</summary>
    public byte[]? Bytes => _bytes;

    /// <summary>Why the document was opened from the whole file instead of by ranges, or null.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>
    /// Why the page tree was read whole instead of page by page (its /Count values do not match
    /// its kids, a loop, a tree too deep), or null. The document stays read by ranges.
    /// </summary>
    public string? PageTreeFallbackReason { get; private set; }

    // The page tree is found page by page (PdfPageTree.IsLazy) until a mismatch turns it off.
    private bool _lazyTree;

    /// <summary>How many times an operation was tried again after a miss.</summary>
    public int Retries { get; private set; }

    public PdfRangeStatistics Statistics => new(Interlocked.Read(ref _bytesFetched), Volatile.Read(ref _requestCount), _cache.CachedBytes, Length, IsComplete);

    /// <summary>
    /// Opens the document. Throws what <see cref="PdfVectorDocument.Open"/> throws for a file that
    /// is not a PDF or needs a password, and what the reader throws when it cannot read.
    /// </summary>
    public static async Task<PdfRangedDocument> OpenAsync(IPdfRangeReader reader, PdfRangedOptions? options = null, string? password = null,
        PdfSecurityLimits? limits = null, string name = "Ranged.pdf", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.Length <= 0) throw new PdfSyntaxException("The file is empty.");
        var doc = new PdfRangedDocument(reader, options ?? new PdfRangedOptions(), password, limits, name);
        try
        {
            await doc.OpenCoreAsync(doc._lazyTree, cancellationToken).ConfigureAwait(false);
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens (or opens again) the document over the cache, with a lazy page tree when
    /// <paramref name="lazy"/>. <see cref="_lazyTree"/> follows the document put in place, so a
    /// reopen that fails leaves both as they were.
    /// </summary>
    private async Task OpenCoreAsync(bool lazy, CancellationToken cancellationToken)
    {
        long length = Length;
        long tail = Math.Min(length, _options.TailBytes);
        // Opening is one cache operation: what it reads is kept until it ends (it needs all of it
        // at once), what the host sent and opening does not read can be dropped. So that the cache
        // passes its cap by at most MaxRoundBytes, opening fetches at most that much after the tail.
        _cache.BeginOperation();
        await FetchAsync([new PdfByteRange(0, Math.Min(length, 4096)), new PdfByteRange(length - tail, length)], cancellationToken).ConfigureAwait(false);
        long budget = Interlocked.Read(ref _bytesFetched) + _options.MaxRoundBytes;

        for (int round = 0; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (Interlocked.Read(ref _bytesFetched) > budget)
            {
                await FallBackAsync($"Opening needed more than {Megabytes(_options.MaxRoundBytes)} of range requests; the file was read whole.", cancellationToken).ConfigureAwait(false);
                return;
            }
            long misses = _cache.MissCount;
            PdfVectorDocument? opened = null;
            PdfXrefTable? xref = null;
            bool ready = false;
            try
            {
                xref = new PdfXrefTable(_limits);
                xref.Load(new PdfChunkedByteSource(_cache));
                if (xref.WasReconstructed)
                {
                    await FallBackAsync(DamagedXref, cancellationToken).ConfigureAwait(false);
                    return;
                }
                _planner = new PdfRangePlanner(xref, length);
                if (await PrefetchStructureAsync(xref, lazy, cancellationToken).ConfigureAwait(false) is { } limit)
                {
                    await FallBackAsync(limit, cancellationToken).ConfigureAwait(false);
                    return;
                }

                misses = _cache.MissCount;
                opened = PdfVectorDocument.OpenDeferred(new PdfChunkedByteSource(_cache), _name, _limits, _password, lazyPages: lazy);
                if (_cache.MissCount == misses && !opened.WasRepaired)
                {
                    var document = opened;
                    opened = null; // Replace owns it now, also when it throws
                    Replace(document);
                    _planner = new PdfRangePlanner(document.XrefTable, length);
                    _lazyTree = lazy;
                    ready = true;
                }
                else if (opened.WasRepaired)
                {
                    opened.Dispose();
                    await FallBackAsync("The file's cross-reference data does not match its objects; the file was read whole to rebuild it.", cancellationToken).ConfigureAwait(false);
                    return;
                }
                else
                {
                    opened.Dispose();
                }
            }
            catch (PdfPageTreeMismatchException ex) when (lazy)
            {
                // The root's /Count cannot be trusted: the tree is read whole, as without a lazy tree.
                opened?.Dispose();
                await UseFullPageTreeAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsMiss(ex, misses))
            {
                opened?.Dispose();
            }
            if (ready)
            {
                // A lazy tree: the path to page 1 now, so that its size is known (the viewer lays the
                // other pages out with it). Outside the try above: its own failures (a read that
                // fails) are not misses of opening.
                if (lazy) await EnsurePageFoundAsync(1, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (xref?.WasReconstructed == true)
            {
                await FallBackAsync(DamagedXref, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (round >= _options.MaxRounds)
            {
                await FallBackAsync($"Opening still needed more bytes after {round + 1} rounds of range requests; the file was read whole.", cancellationToken).ConfigureAwait(false);
                return;
            }
            Retries++;
            if (await FetchMissesAsync(Math.Max(0, budget - Interlocked.Read(ref _bytesFetched)), cancellationToken).ConfigureAwait(false) is { } reason)
            {
                await FallBackAsync(reason, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private const string DamagedXref = "The cross-reference data is damaged: the file was read whole to rebuild it.";

    private bool IsMiss(Exception ex, long missesBefore) =>
        ex is PdfDataNotLoadedException || ex.InnerException is PdfDataNotLoadedException || _cache.MissCount != missesBefore;

    /// <summary>
    /// The trailer, the page tree, the pages' boxes and the ends of their content streams, a level
    /// at a time. Opening needs all of them at once, so they count as one round
    /// (<see cref="PdfRangedOptions.MaxRoundBytes"/>). Returns why the file must be read whole, or null.
    /// </summary>
    private async Task<string?> PrefetchStructureAsync(PdfXrefTable xref, bool lazy, CancellationToken cancellationToken)
    {
        var resolver = new PdfObjectResolver(new PdfChunkedByteSource(_cache), xref, _limits);
        var closure = new PdfObjectClosure(_planner!, PdfClosureKind.Structure)
        {
            MaxBytes = Math.Min(_options.MaxWalkBytes, _options.MaxRoundBytes),
            // A lazy tree reads its nodes when a page is asked for (EnsurePageFoundAsync).
            FollowKids = !lazy,
        };
        if (xref.Trailer != null) closure.AddDirectRoot(xref.Trailer);
        await WalkAsync(closure, resolver, cancellationToken).ConfigureAwait(false);
        return closure.LimitReached
            ? $"The document's structure needs more than {Megabytes(closure.MaxBytes)} of range requests; the file was read whole."
            : null;
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0.#} MB";

    private async Task WalkAsync(PdfObjectClosure closure, PdfObjectResolver resolver, CancellationToken cancellationToken)
    {
        for (int level = 0; !closure.IsDone && level < 64; level++)
        {
            var ranges = closure.PendingRanges();
            if (closure.LimitReached) break;
            await FetchAsync(ranges, cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();
            closure.Advance(n => resolver.Resolve(n));
        }
        // The walk's own misses are expected (it fetches what it finds); they are not the operation's.
        _cache.TakeMisses();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Fetches the bytes that page <paramref name="pageNumber"/> needs (nothing when the file is complete).</summary>
    public async Task PrefetchPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrefetchPageCoreAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PrefetchPageCoreAsync(int pageNumber, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_completeDocument || _planner == null) return;
        if (pageNumber < 1 || pageNumber > Document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        // The page's own bytes (and the tree nodes above it) are kept while it is parsed (see PdfRangedOptions.CacheBytes).
        _cache.BeginOperation();
        await EnsurePageFoundAsync(pageNumber, cancellationToken).ConfigureAwait(false);
        if (_completeDocument) return;
        var doc = Document; // a mismatch in the lazy tree replaced it
        if (pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var closure = new PdfObjectClosure(_planner!, PdfClosureKind.Page) { MaxBytes = _options.MaxWalkBytes };
        int number = doc.PageTree.PageObjectNumbers[pageNumber - 1];
        if (number > 0) closure.AddRoot(number);
        else closure.AddDirectRoot(doc.PageTree.Pages[pageNumber - 1].Dictionary);
        await WalkAsync(closure, doc.Resolver, cancellationToken).ConfigureAwait(false);
        if (closure.LimitReached)
        {
            await FallBackAsync($"Page {pageNumber} needs more than {Megabytes(_options.MaxWalkBytes)} of range requests; the file was read whole.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Page <paramref name="pageNumber"/>'s node (its boxes, rotation and dictionary), fetching the
    /// page-tree nodes on its path if the tree is lazy, but not its contents. Use it for a page's
    /// size before it is drawn. The node belongs to the current <see cref="Document"/>.
    /// </summary>
    public async Task<PdfPageNode> FindPageAsync(int pageNumber, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (pageNumber < 1 || pageNumber > Document.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            _cache.BeginOperation();
            await EnsurePageFoundAsync(pageNumber, cancellationToken).ConfigureAwait(false);
            var tree = Document.PageTree;
            if (pageNumber > tree.Count) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            return tree.Pages[pageNumber - 1];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// For a lazy page tree: reads the tree nodes on the path to page <paramref name="pageNumber"/>,
    /// a level per round (the kids that a node's /Count does not settle are fetched together). A
    /// tree that does not match its /Count values, or a page not found after
    /// <see cref="PdfRangedOptions.MaxRounds"/> rounds, turns the lazy tree off: the tree is read
    /// whole (<see cref="PageTreeFallbackReason"/>), as when the document opens without it.
    /// </summary>
    private async Task EnsurePageFoundAsync(int pageNumber, CancellationToken cancellationToken)
    {
        for (int round = 0; ; round++)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var tree = Document.PageTree;
            if (_completeDocument || !tree.IsLazy || pageNumber < 1 || pageNumber > tree.Count || tree.LoadedPage(pageNumber - 1) != null) return;
            long misses = _cache.MissCount;
            try
            {
                _ = tree.Pages[pageNumber - 1];
                _cache.TakeMisses(); // what the kids read ahead recorded is not this operation's
                return;
            }
            catch (PdfPageTreeMismatchException ex)
            {
                await UseFullPageTreeAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsMiss(ex, misses))
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not ObjectDisposedException)
            {
                // A damaged tree node: the whole walk reads it as a document opened from memory does.
                await UseFullPageTreeAsync($"The page tree could not be read lazily ({ex.GetType().Name}).", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (round >= _options.MaxRounds)
            {
                await UseFullPageTreeAsync($"Page {pageNumber} was not found in the page tree after {round + 1} rounds of range requests.", cancellationToken).ConfigureAwait(false);
                return;
            }
            // A tree node is a small dictionary, placed by the cross-reference table: its extent and
            // a few blocks around it, not the read-ahead of a page's objects.
            if (await FetchMissesAsync(_options.MaxRoundBytes, cancellationToken, Math.Min(_options.ReadAheadBytes, 4L * _cache.BlockSize)).ConfigureAwait(false) is { } reason)
            {
                await FallBackAsync(reason, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Opens the document again with the whole tree (its bytes are fetched by ranges, as when it
    /// opens), which turns the lazy page tree off. When that fails (a read fails, the operation
    /// is canceled), the lazy document and <see cref="_lazyTree"/> stay as they were, so the next
    /// operation finds the mismatch again and tries again.
    /// </summary>
    private async Task UseFullPageTreeAsync(string reason, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_completeDocument) return;
        await OpenCoreAsync(lazy: false, cancellationToken).ConfigureAwait(false);
        PageTreeFallbackReason ??= reason;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on page <paramref name="pageNumber"/> once its bytes are
    /// fetched. When the operation still reads bytes that are not there, they are fetched and it
    /// runs again on a fresh parse (the earlier run may have kept a partial result in its caches).
    /// </summary>
    public async Task<T> RunAsync<T>(int pageNumber, Func<PdfVectorDocument, CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int round = 0; ; round++)
            {
                ThrowIfDisposed();
                if (_completeDocument) return await operation(Document, cancellationToken).ConfigureAwait(false);
                await PrefetchPageCoreAsync(pageNumber, cancellationToken).ConfigureAwait(false);
                long misses = _cache.MissCount;
                T result;
                try
                {
                    result = await operation(Document, cancellationToken).ConfigureAwait(false);
                    if (_cache.MissCount == misses) return result;
                }
                catch (PdfPageTreeMismatchException ex) when (_lazyTree && !_completeDocument)
                {
                    // The operation read another page, and the lazy tree could not place it.
                    await UseFullPageTreeAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (IsMiss(ex, misses))
                {
                }
                if (round >= _options.MaxRounds)
                {
                    await FallBackAsync($"Page {pageNumber} still needed more bytes after {round + 1} rounds of range requests; the file was read whole.", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                Retries++;
                if (await FetchMissesAsync(_options.MaxRoundBytes, cancellationToken).ConfigureAwait(false) is { } reason)
                {
                    await FallBackAsync(reason, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                Reparse();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Parses the document again over the cache: what an interrupted operation cached is dropped.</summary>
    private void Reparse()
    {
        ThrowIfDisposed();
        var parsed = PdfVectorDocument.OpenDeferred(new PdfChunkedByteSource(_cache), _name, _limits, _password, lazyPages: _lazyTree);
        Replace(parsed);
        _planner = new PdfRangePlanner(parsed.XrefTable, Length);
    }

    /// <summary>Puts <paramref name="document"/> in place of <see cref="Document"/>; after Dispose, disposes it and throws.</summary>
    private void Replace(PdfVectorDocument document)
    {
        PdfVectorDocument? old;
        lock (_stateSync)
        {
            if (_disposed)
            {
                document.Dispose();
                throw new ObjectDisposedException(nameof(PdfRangedDocument));
            }
            old = Document;
            Document = document;
        }
        old?.Dispose();
    }

    /// <summary>
    /// Fetches each recorded miss with the object around it (or <see cref="PdfRangedOptions.ReadAheadBytes"/>).
    /// Returns why the file must be read whole instead, or null: a read asked for the whole file
    /// (only a rebuild of the cross-reference data does that), or the round needs more than
    /// <paramref name="maxBytes"/> (<see cref="PdfRangedOptions.MaxRoundBytes"/>, or what is left of the budget of opening).
    /// </summary>
    private async Task<string?> FetchMissesAsync(long maxBytes, CancellationToken cancellationToken, long? readAhead = null)
    {
        var misses = _cache.TakeMisses();
        var ranges = new List<PdfByteRange>();
        foreach (var miss in misses)
        {
            if (miss.Start == 0 && miss.End >= Length && Length > _options.ReadAheadBytes) return DamagedXref;
            var extent = _planner?.ExtentAt(miss.Start) ?? miss;
            if (extent.Length > _options.MaxRequestBytes * 4L || extent.End < miss.End) extent = miss;
            long start = Math.Min(extent.Start, miss.Start);
            long end = Math.Max(Math.Max(extent.End, miss.End), Math.Min(Length, miss.Start + (readAhead ?? _options.ReadAheadBytes)));
            ranges.Add(new PdfByteRange(start, end));
        }
        if (ranges.Count == 0)
        {
            // A miss swallowed before it was recorded is impossible; read ahead from the start as a last resort.
            ranges.Add(new PdfByteRange(0, Math.Min(Length, _options.ReadAheadBytes)));
        }
        long missing = PdfRangePlanner.Merge(ranges).SelectMany(r => _cache.Missing(r)).Sum(r => r.Length);
        if (missing > maxBytes)
        {
            return $"A read needed {Megabytes(missing)} more, more than the {Megabytes(maxBytes)} this round may fetch; the file was read whole.";
        }
        await FetchAsync(ranges, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Fetches the parts of <paramref name="ranges"/> that are not in the cache.</summary>
    public async Task FetchAsync(IEnumerable<PdfByteRange> ranges, CancellationToken cancellationToken = default)
    {
        var missing = ranges.SelectMany(r => _cache.Missing(r)).ToList();
        if (missing.Count == 0) return;
        var requests = PdfRangePlanner.Split(PdfRangePlanner.Merge(missing, _options.MergeGapBytes), _options.MaxRequestBytes, _cache.BlockSize);
        await Task.WhenAll(requests.Select(r => FetchOneAsync(r, cancellationToken))).ConfigureAwait(false);
    }

    private async Task FetchOneAsync(PdfByteRange range, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var buffer = new byte[range.Length];
            await ReadAsync(range.Start, buffer, cancellationToken).ConfigureAwait(false);
            lock (_stateSync)
            {
                ThrowIfDisposed(); // a disposed document keeps nothing
                _cache.Store(range.Start, buffer);
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    private async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        ThrowIfDisposed();
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
        {
            await _reader.ReadAsync(offset, destination, linked.Token).ConfigureAwait(false);
        }
        ThrowIfDisposed();
        Interlocked.Add(ref _bytesFetched, destination.Length);
        Interlocked.Increment(ref _requestCount);
    }

    private void Seed(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset >= Length || data.IsEmpty) return;
        data = data.Slice(0, (int)Math.Min(data.Length, Length - offset));
        // Only whole blocks are kept: skip to the first block boundary.
        long first = (offset + _cache.BlockSize - 1) / _cache.BlockSize * _cache.BlockSize;
        int skip = (int)(first - offset);
        if (skip >= data.Length) return;
        var rest = data.Slice(skip);
        long end = first + rest.Length;
        int whole = end == Length ? rest.Length : rest.Length / _cache.BlockSize * _cache.BlockSize;
        if (whole > 0) _cache.Store(first, rest.Slice(0, whole));
    }

    /// <summary>
    /// Reads the whole file (the blocks already fetched are copied, not fetched again), one
    /// <see cref="PdfRangedOptions.CompletionRequestBytes"/> read at a time so that page reads go
    /// first. Calls after the first return the same task. After a failure, the next call resumes
    /// where the failed one stopped: what it read is kept. <paramref name="progress"/> gets the bytes read so far.
    /// </summary>
    public Task<byte[]> ReadAllAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_readAllSync)
        {
            if (_bytes != null) return Task.FromResult(_bytes);
            if (_readAll is { IsFaulted: true } or { IsCanceled: true }) _readAll = null;
            return _readAll ??= ReadAllCoreAsync(progress, cancellationToken);
        }
    }

    private async Task<byte[]> ReadAllCoreAsync(IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await Task.Yield();
        long length = Length;
        if (length > Array.MaxLength) throw new PdfResourceLimitException("FileLength", "The file is too large to hold in memory.");
        // A read that failed before left its bytes here: this one goes on from where it stopped.
        var all = _partial ??= new byte[length];
        long step = Math.Max(_cache.BlockSize, _options.CompletionRequestBytes / _cache.BlockSize * _cache.BlockSize);
        if (_partialEnd > 0) progress?.Report(_partialEnd);
        for (long pos = _partialEnd; pos < length; pos += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = new PdfByteRange(pos, Math.Min(length, pos + step));
            // Held blocks are copied; runs of the others are fetched.
            var missing = new List<PdfByteRange>();
            for (long b = range.Start; b < range.End; b += _cache.BlockSize)
            {
                int n = (int)Math.Min(_cache.BlockSize, range.End - b);
                if (_cache.TryCopy(b, all.AsSpan((int)b, n))) continue;
                if (missing.Count > 0 && missing[^1].End == b) missing[^1] = new PdfByteRange(missing[^1].Start, b + n);
                else missing.Add(new PdfByteRange(b, b + n));
            }
            foreach (var gap in missing)
            {
                await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ReadAsync(gap.Start, all.AsMemory((int)gap.Start, (int)gap.Length), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _requests.Release();
                }
            }
            _partialEnd = range.End;
            progress?.Report(range.End);
        }
        lock (_readAllSync)
        {
            _bytes = all;
            _partial = null;
        }
        return all;
    }

    /// <summary>Reads the whole file and parses the document from memory: from then on nothing is fetched. Returns the file.</summary>
    public async Task<byte[]> CompleteAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllAsync(null, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SwitchToMemory(bytes);
        }
        finally
        {
            _gate.Release();
        }
        return bytes;
    }

    private async Task FallBackAsync(string reason, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        FallbackReason ??= reason;
        var bytes = await ReadAllAsync(null, cancellationToken).ConfigureAwait(false);
        SwitchToMemory(bytes);
    }

    private void SwitchToMemory(byte[] bytes)
    {
        ThrowIfDisposed();
        if (_completeDocument) return;
        Replace(PdfVectorDocument.Open(new PdfMemoryByteSource(bytes), _name, _limits, _password));
        _completeDocument = true;
        _planner = null;
        _cache.Clear();
    }

    /// <summary>
    /// Disposes the document and drops the cache. Reads in progress are canceled; an operation
    /// still running ends with <see cref="ObjectDisposedException"/> and makes no new document.
    /// </summary>
    public void Dispose()
    {
        PdfVectorDocument? document;
        lock (_stateSync)
        {
            if (_disposed) return;
            _disposed = true;
            document = Document;
            _partial = null;
            _cache.Clear();
        }
        _lifetime.Cancel();
        document?.Dispose();
    }
}
