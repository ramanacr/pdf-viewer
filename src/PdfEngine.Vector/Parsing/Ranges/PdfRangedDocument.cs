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
/// cross-reference data, then the objects of the document's structure: the catalog, the page tree
/// and each page's dictionary (<see cref="PdfClosureKind.Structure"/>); content streams are
/// resolved when their page is drawn.</item>
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
    private long _bytesFetched;
    private int _requestCount;
    private bool _disposed;

    private PdfRangedDocument(IPdfRangeReader reader, PdfRangedOptions options, string? password, PdfSecurityLimits? limits, string name)
    {
        _reader = reader;
        _options = options;
        _password = password;
        _limits = limits;
        _name = name;
        _cache = new PdfChunkCache(reader.Length, options.BlockSize, options.CacheBytes);
        _requests = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests));
        foreach (var (offset, data) in options.InitialData) Seed(offset, data.Span);
    }

    /// <summary>The parsed document. It is replaced when the document is parsed again (after a miss, or when the file is complete): do not keep it across calls.</summary>
    public PdfVectorDocument Document { get; private set; } = null!;

    /// <summary>The fetched blocks.</summary>
    public PdfChunkCache Cache => _cache;

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
            await doc.OpenCoreAsync(cancellationToken).ConfigureAwait(false);
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        long length = Length;
        long tail = Math.Min(length, _options.TailBytes);
        await FetchAsync([new PdfByteRange(0, Math.Min(length, 4096)), new PdfByteRange(length - tail, length)], cancellationToken).ConfigureAwait(false);

        for (int round = 0; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long misses = _cache.MissCount;
            PdfVectorDocument? opened = null;
            PdfXrefTable? xref = null;
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
                await PrefetchStructureAsync(xref, cancellationToken).ConfigureAwait(false);

                misses = _cache.MissCount;
                opened = PdfVectorDocument.OpenDeferred(new PdfChunkedByteSource(_cache), _name, _limits, _password);
                if (_cache.MissCount == misses && !opened.WasRepaired)
                {
                    Document = opened;
                    _planner = new PdfRangePlanner(opened.XrefTable, length);
                    return;
                }
                if (opened.WasRepaired)
                {
                    opened.Dispose();
                    await FallBackAsync("The file's cross-reference data does not match its objects; the file was read whole to rebuild it.", cancellationToken).ConfigureAwait(false);
                    return;
                }
                opened.Dispose();
            }
            catch (Exception ex) when (IsMiss(ex, misses))
            {
                opened?.Dispose();
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
            if (!await FetchMissesAsync(cancellationToken).ConfigureAwait(false))
            {
                await FallBackAsync(DamagedXref, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private const string DamagedXref = "The cross-reference data is damaged: the file was read whole to rebuild it.";

    private bool IsMiss(Exception ex, long missesBefore) =>
        ex is PdfDataNotLoadedException || ex.InnerException is PdfDataNotLoadedException || _cache.MissCount != missesBefore;

    /// <summary>The trailer, the page tree, the pages' boxes and the ends of their content streams, a level at a time.</summary>
    private async Task PrefetchStructureAsync(PdfXrefTable xref, CancellationToken cancellationToken)
    {
        var resolver = new PdfObjectResolver(new PdfChunkedByteSource(_cache), xref, _limits);
        var closure = new PdfObjectClosure(_planner!, PdfClosureKind.Structure);
        if (xref.Trailer != null) closure.AddDirectRoot(xref.Trailer);
        await WalkAsync(closure, resolver, cancellationToken).ConfigureAwait(false);
    }

    private async Task WalkAsync(PdfObjectClosure closure, PdfObjectResolver resolver, CancellationToken cancellationToken)
    {
        for (int level = 0; !closure.IsDone && level < 64; level++)
        {
            await FetchAsync(closure.PendingRanges(), cancellationToken).ConfigureAwait(false);
            closure.Advance(n => resolver.Resolve(n));
        }
        // The walk's own misses are expected (it fetches what it finds); they are not the operation's.
        _cache.TakeMisses();
    }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completeDocument || _planner == null) return;
        var doc = Document;
        if (pageNumber < 1 || pageNumber > doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var closure = new PdfObjectClosure(_planner, PdfClosureKind.Page);
        int number = doc.PageTree.PageObjectNumbers[pageNumber - 1];
        if (number > 0) closure.AddRoot(number);
        else closure.AddDirectRoot(doc.PageTree.Pages[pageNumber - 1].Dictionary);
        _cache.BeginOperation();
        await WalkAsync(closure, doc.Resolver, cancellationToken).ConfigureAwait(false);
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
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_completeDocument) return await operation(Document, cancellationToken).ConfigureAwait(false);
                await PrefetchPageCoreAsync(pageNumber, cancellationToken).ConfigureAwait(false);
                long misses = _cache.MissCount;
                T result;
                try
                {
                    result = await operation(Document, cancellationToken).ConfigureAwait(false);
                    if (_cache.MissCount == misses) return result;
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
                if (!await FetchMissesAsync(cancellationToken).ConfigureAwait(false))
                {
                    await FallBackAsync(DamagedXref, cancellationToken).ConfigureAwait(false);
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
        var old = Document;
        Document = PdfVectorDocument.OpenDeferred(new PdfChunkedByteSource(_cache), _name, _limits, _password);
        _planner = new PdfRangePlanner(Document.XrefTable, Length);
        old.Dispose();
    }

    /// <summary>
    /// Fetches each recorded miss with the object around it (or <see cref="PdfRangedOptions.ReadAheadBytes"/>).
    /// False when a read asked for the whole file: only a rebuild of the cross-reference data does that.
    /// </summary>
    private async Task<bool> FetchMissesAsync(CancellationToken cancellationToken)
    {
        var misses = _cache.TakeMisses();
        var ranges = new List<PdfByteRange>();
        foreach (var miss in misses)
        {
            if (miss.Start == 0 && miss.End >= Length && Length > _options.ReadAheadBytes) return false;
            var extent = _planner?.ExtentAt(miss.Start) ?? miss;
            if (extent.Length > _options.MaxRequestBytes * 4L || extent.End < miss.End) extent = miss;
            long start = Math.Min(extent.Start, miss.Start);
            long end = Math.Max(Math.Max(extent.End, miss.End), Math.Min(Length, miss.Start + _options.ReadAheadBytes));
            ranges.Add(new PdfByteRange(start, end));
        }
        if (ranges.Count == 0)
        {
            // A miss swallowed before it was recorded is impossible; read ahead from the start as a last resort.
            ranges.Add(new PdfByteRange(0, Math.Min(Length, _options.ReadAheadBytes)));
        }
        await FetchAsync(ranges, cancellationToken).ConfigureAwait(false);
        return true;
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
            _cache.Store(range.Start, buffer);
        }
        finally
        {
            _requests.Release();
        }
    }

    private async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        await _reader.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
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
    /// first. Calls after the first return the same task. <paramref name="progress"/> gets the bytes read so far.
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
        var all = new byte[length];
        long step = Math.Max(_cache.BlockSize, _options.CompletionRequestBytes / _cache.BlockSize * _cache.BlockSize);
        for (long pos = 0; pos < length; pos += step)
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
            progress?.Report(range.End);
        }
        lock (_readAllSync) _bytes = all;
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
        FallbackReason ??= reason;
        var bytes = await ReadAllAsync(null, cancellationToken).ConfigureAwait(false);
        SwitchToMemory(bytes);
    }

    private void SwitchToMemory(byte[] bytes)
    {
        if (_completeDocument) return;
        var old = Document;
        Document = PdfVectorDocument.Open(new MemoryByteSource(bytes), _name, _limits, _password);
        _completeDocument = true;
        _planner = null;
        _cache.Clear();
        old?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Document?.Dispose();
        _cache.Clear();
    }
}
