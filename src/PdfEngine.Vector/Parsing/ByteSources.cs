using System;
using System.IO;

namespace PdfEngine.Vector.Parsing;

/// <summary>
/// Memory-backed IPdfByteSource over a byte array or ReadOnlyMemory.
/// </summary>
public sealed class PdfMemoryByteSource : IPdfByteSource
{
    private readonly ReadOnlyMemory<byte> _memory;
    private long _position;
    // The array behind _memory, when there is one: ReadByte indexes it directly instead of
    // taking the memory's span for every byte (the lexer reads content streams byte by byte).
    private readonly byte[]? _array;
    private readonly int _arrayOffset;

    public long Length => _memory.Length;

    public long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public PdfMemoryByteSource(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
        _position = 0;
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array != null)
        {
            _array = segment.Array;
            _arrayOffset = segment.Offset;
        }
    }

    public PdfMemoryByteSource(byte[] bytes) : this(bytes.AsMemory()) { }

    public int ReadByte()
    {
        if (_position >= _memory.Length)
            return -1;
        return _array != null ? _array[_arrayOffset + (int)_position++] : _memory.Span[(int)_position++];
    }

    public int Read(Span<byte> destination)
    {
        if (_position >= _memory.Length)
            return 0;

        int remaining = (int)(_memory.Length - _position);
        int toRead = Math.Min(destination.Length, remaining);
        _memory.Span.Slice((int)_position, toRead).CopyTo(destination);
        _position += toRead;
        return toRead;
    }

    public ReadOnlyMemory<byte> ReadMemory(long offset, int length)
    {
        if (offset < 0 || offset > _memory.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int available = (int)(_memory.Length - offset);
        int actual = Math.Min(length, available);
        return actual > 0 ? _memory.Slice((int)offset, actual) : ReadOnlyMemory<byte>.Empty;
    }

    public IPdfByteSource Slice(long offset, long length)
    {
        if (offset < 0 || offset > _memory.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        int available = (int)(_memory.Length - offset);
        int actual = (int)Math.Min(length, available);
        return new PdfMemoryByteSource(_memory.Slice((int)offset, actual));
    }

    public void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin)
    {
        long newPos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _memory.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = newPos;
    }

    public void Dispose() { }
}

/// <summary>
/// File-backed IPdfByteSource with random access over a FileStream.
/// </summary>
public sealed class PdfFileByteSource : IPdfByteSource
{
    private readonly FileStream _stream;
    private readonly long _baseOffset;
    private readonly long _length;
    private readonly bool _ownsStream;

    public long Length => _length;

    public long Position
    {
        get => Math.Max(0, _stream.Position - _baseOffset);
        set => _stream.Position = _baseOffset + Math.Clamp(value, 0, _length);
    }

    public PdfFileByteSource(string filePath)
    {
        _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        _baseOffset = 0;
        _length = _stream.Length;
        _ownsStream = true;
    }

    private PdfFileByteSource(FileStream stream, long baseOffset, long length, bool ownsStream)
    {
        _stream = stream;
        _baseOffset = baseOffset;
        _length = length;
        _ownsStream = ownsStream;
    }

    public int ReadByte()
    {
        if (Position >= _length)
            return -1;
        return _stream.ReadByte();
    }

    public int Read(Span<byte> destination)
    {
        long current = Position;
        if (current >= _length)
            return 0;

        int toRead = (int)Math.Min(destination.Length, _length - current);
        return _stream.Read(destination.Slice(0, toRead));
    }

    public ReadOnlyMemory<byte> ReadMemory(long offset, int length)
    {
        long actualOffset = _baseOffset + offset;
        if (actualOffset < 0 || actualOffset > _baseOffset + _length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int available = (int)(_length - offset);
        int toRead = Math.Min(length, available);
        if (toRead <= 0)
            return ReadOnlyMemory<byte>.Empty;

        byte[] buffer = new byte[toRead];
        long oldPos = _stream.Position;
        try
        {
            _stream.Position = actualOffset;
            int read = _stream.Read(buffer, 0, toRead);
            return read == toRead ? buffer : buffer.AsMemory(0, read);
        }
        finally
        {
            _stream.Position = oldPos;
        }
    }

    public IPdfByteSource Slice(long offset, long length)
    {
        long sliceBase = _baseOffset + offset;
        long sliceLen = Math.Min(length, _length - offset);
        return new PdfFileByteSource(_stream, sliceBase, Math.Max(0, sliceLen), ownsStream: false);
    }

    public void Seek(long offset, SeekOrigin origin = SeekOrigin.Begin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        Position = target;
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
