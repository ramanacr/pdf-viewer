using System;
using System.IO;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// An <see cref="IPdfByteSource"/> over the fetched blocks of a <see cref="PdfChunkCache"/>. Reads
/// are synchronous, as the parser needs; a read of bytes that are not held records the miss in the
/// cache and throws <see cref="PdfDataNotLoadedException"/>. <see cref="PdfRangedDocument"/> fetches
/// what a page needs before it parses the page, so misses are the exception. Disposing it leaves the cache alone.
/// </summary>
internal sealed class PdfChunkedByteSource : IPdfByteSource
{
    private readonly PdfChunkCache _cache;
    private readonly long _base;
    private readonly long _length;
    private long _position;

    // The block of the last ReadByte: the lexer reads byte by byte.
    private byte[]? _block;
    private long _blockStart = -1;

    public PdfChunkedByteSource(PdfChunkCache cache) : this(cache, 0, cache.FileLength) { }

    private PdfChunkedByteSource(PdfChunkCache cache, long baseOffset, long length)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _base = baseOffset;
        _length = length;
    }

    /// <summary>The cache the bytes come from.</summary>
    public PdfChunkCache Cache => _cache;

    public long Length => _length;

    public long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public int ReadByte()
    {
        if (_position >= _length) return -1;
        long at = _base + _position;
        if (_block == null || at < _blockStart || at >= _blockStart + _block.Length)
        {
            _block = _cache.TryGetBlock(at, out _blockStart);
            if (_block == null)
            {
                _blockStart = -1;
                throw Miss(at, 1);
            }
        }
        _position++;
        return _block[at - _blockStart];
    }

    public int Read(Span<byte> destination)
    {
        if (_position >= _length) return 0;
        int n = (int)Math.Min(destination.Length, _length - _position);
        if (!_cache.TryCopy(_base + _position, destination.Slice(0, n)))
            throw Miss(_base + _position, n);
        _position += n;
        return n;
    }

    public ReadOnlyMemory<byte> ReadMemory(long offset, int length)
    {
        if (offset < 0 || offset > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        int n = (int)Math.Min(Math.Max(0, length), _length - offset);
        if (n == 0) return ReadOnlyMemory<byte>.Empty;
        // A miss is recorded before anything is allocated: a crafted /Length of gigabytes costs nothing.
        if (!_cache.Contains(new PdfByteRange(_base + offset, _base + offset + n))) throw Miss(_base + offset, n);
        var buffer = new byte[n];
        if (!_cache.TryCopy(_base + offset, buffer)) throw Miss(_base + offset, n); // dropped meanwhile
        return buffer;
    }

    public IPdfByteSource Slice(long offset, long length)
    {
        if (offset < 0 || offset > _length) throw new ArgumentOutOfRangeException(nameof(offset));
        return new PdfChunkedByteSource(_cache, _base + offset, Math.Max(0, Math.Min(length, _length - offset)));
    }

    public void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
    }

    private PdfDataNotLoadedException Miss(long fileOffset, long length)
    {
        _cache.RecordMiss(fileOffset, length);
        return new PdfDataNotLoadedException(fileOffset, length);
    }

    public void Dispose() { }
}
