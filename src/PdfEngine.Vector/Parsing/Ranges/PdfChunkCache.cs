using System;
using System.Collections.Generic;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// The fetched parts of a file that is read by ranges: fixed-size blocks, the least recently used
/// dropped first when the cache holds more than <see cref="MaxBytes"/>. Blocks used since the last
/// <see cref="BeginOperation"/> are never dropped, so one page's bytes stay while that page is
/// parsed even when it alone is larger than the cap. Thread-safe.
/// </summary>
public sealed class PdfChunkCache
{
    /// <summary>The default block size: 4 KB, small enough that a page object costs little more than its own bytes.</summary>
    public const int DefaultBlockSize = 4096;

    /// <summary>The default cap: 64 MB.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private sealed class Block(long index, byte[] data)
    {
        public long Index { get; } = index;
        public byte[] Data { get; } = data;
        public int Generation { get; set; }
    }

    private readonly Dictionary<long, LinkedListNode<Block>> _blocks = new();
    private readonly LinkedList<Block> _lru = new(); // most recently used first
    private readonly List<PdfByteRange> _misses = new();
    private readonly object _sync = new();
    private int _generation;
    private long _cachedBytes;
    private long _missCount;
    private long _evictedBytes;

    /// <param name="fileLength">The length of the file.</param>
    /// <param name="blockSize">The block size in bytes (512 to 1 MB).</param>
    /// <param name="maxBytes">The most bytes kept, at least one block.</param>
    public PdfChunkCache(long fileLength, int blockSize = DefaultBlockSize, long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 512);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, 1024 * 1024);
        FileLength = fileLength;
        BlockSize = blockSize;
        MaxBytes = Math.Max(maxBytes, blockSize);
    }

    public long FileLength { get; }
    public int BlockSize { get; }
    public long MaxBytes { get; }

    /// <summary>The bytes held now.</summary>
    public long CachedBytes { get { lock (_sync) return _cachedBytes; } }

    /// <summary>The bytes dropped so far to stay under the cap.</summary>
    public long EvictedBytes { get { lock (_sync) return _evictedBytes; } }

    /// <summary>How many reads found bytes missing (see <see cref="RecordMiss"/>).</summary>
    public long MissCount { get { lock (_sync) return _missCount; } }

    /// <summary>The number of blocks of the file.</summary>
    public long BlockCount => (FileLength + BlockSize - 1) / BlockSize;

    /// <summary>
    /// Starts an operation (a page parse): the blocks it reads or stores are kept until the next
    /// call, whatever the cap.
    /// </summary>
    public void BeginOperation()
    {
        lock (_sync)
        {
            _generation++;
            Evict();
        }
    }

    /// <summary>The range grown to whole blocks and clipped to the file.</summary>
    public PdfByteRange Align(PdfByteRange range)
    {
        long start = Math.Clamp(range.Start, 0, FileLength) / BlockSize * BlockSize;
        long end = Math.Clamp(range.End, 0, FileLength);
        end = Math.Min(FileLength, (end + BlockSize - 1) / BlockSize * BlockSize);
        return new PdfByteRange(start, Math.Max(start, end));
    }

    /// <summary>True when every byte of the range is held.</summary>
    public bool Contains(PdfByteRange range)
    {
        if (range.IsEmpty) return true;
        lock (_sync)
        {
            for (long b = Math.Max(0, range.Start) / BlockSize, last = (Math.Min(range.End, FileLength) - 1) / BlockSize; b <= last; b++)
            {
                if (!_blocks.ContainsKey(b)) return false;
            }
            return true;
        }
    }

    /// <summary>The parts of the range that are not held, as block-aligned ranges in file order.</summary>
    public IReadOnlyList<PdfByteRange> Missing(PdfByteRange range)
    {
        var result = new List<PdfByteRange>();
        range = Align(range);
        if (range.IsEmpty) return result;
        lock (_sync)
        {
            long runStart = -1;
            for (long b = range.Start / BlockSize, last = (range.End - 1) / BlockSize; b <= last; b++)
            {
                bool held = _blocks.ContainsKey(b);
                if (!held && runStart < 0) runStart = b;
                if (held && runStart >= 0)
                {
                    result.Add(BlockRange(runStart, b));
                    runStart = -1;
                }
            }
            if (runStart >= 0) result.Add(BlockRange(runStart, (range.End - 1) / BlockSize + 1));
        }
        return result;
    }

    private PdfByteRange BlockRange(long firstBlock, long endBlock) =>
        new(firstBlock * BlockSize, Math.Min(FileLength, endBlock * BlockSize));

    /// <summary>
    /// Stores fetched bytes. <paramref name="offset"/> must be at a block boundary; only whole
    /// blocks are kept (the last block of the file may be shorter). Blocks already held are kept as they are.
    /// </summary>
    public void Store(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset % BlockSize != 0 || offset + data.Length > FileLength)
            throw new ArgumentOutOfRangeException(nameof(offset));
        lock (_sync)
        {
            for (int at = 0; at < data.Length; at += BlockSize)
            {
                long index = (offset + at) / BlockSize;
                int size = (int)Math.Min(BlockSize, FileLength - index * BlockSize);
                if (data.Length - at < size) break; // a partial block is not kept
                if (_blocks.TryGetValue(index, out var existing))
                {
                    Touch(existing);
                    continue;
                }
                var block = new Block(index, data.Slice(at, size).ToArray()) { Generation = _generation };
                _blocks[index] = _lru.AddFirst(block);
                _cachedBytes += size;
            }
            Evict();
        }
    }

    /// <summary>Copies held bytes; false (and nothing copied) when any of them is missing.</summary>
    public bool TryCopy(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > FileLength) return false;
        if (destination.IsEmpty) return true;
        lock (_sync)
        {
            if (!Contains(new PdfByteRange(offset, offset + destination.Length))) return false;
            int done = 0;
            while (done < destination.Length)
            {
                long at = offset + done;
                var node = _blocks[at / BlockSize];
                Touch(node);
                int inBlock = (int)(at % BlockSize);
                int n = Math.Min(destination.Length - done, node.Value.Data.Length - inBlock);
                node.Value.Data.AsSpan(inBlock, n).CopyTo(destination.Slice(done));
                done += n;
            }
            return true;
        }
    }

    /// <summary>The block that holds <paramref name="offset"/>, or null when it is not held. The array must not be changed.</summary>
    public byte[]? TryGetBlock(long offset, out long blockStart)
    {
        blockStart = offset / BlockSize * BlockSize;
        lock (_sync)
        {
            if (!_blocks.TryGetValue(offset / BlockSize, out var node)) return null;
            Touch(node);
            return node.Value.Data;
        }
    }

    /// <summary>Notes that a read needed bytes that are not held (the parser then throws <see cref="PdfDataNotLoadedException"/>).</summary>
    public void RecordMiss(long offset, long length)
    {
        lock (_sync)
        {
            _missCount++;
            if (_misses.Count < 256) _misses.Add(new PdfByteRange(offset, Math.Min(FileLength, offset + Math.Max(1, length))));
        }
    }

    /// <summary>The misses recorded since the last call.</summary>
    public IReadOnlyList<PdfByteRange> TakeMisses()
    {
        lock (_sync)
        {
            var list = _misses.ToArray();
            _misses.Clear();
            return list;
        }
    }

    /// <summary>Drops every block.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _blocks.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }

    private void Touch(LinkedListNode<Block> node)
    {
        node.Value.Generation = _generation;
        if (node != _lru.First)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private void Evict()
    {
        while (_cachedBytes > MaxBytes && _lru.Last is { } last && last.Value.Generation != _generation)
        {
            _lru.RemoveLast();
            _blocks.Remove(last.Value.Index);
            _cachedBytes -= last.Value.Data.Length;
            _evictedBytes += last.Value.Data.Length;
        }
    }
}
