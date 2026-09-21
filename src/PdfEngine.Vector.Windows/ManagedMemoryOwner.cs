using System;
using System.Buffers;

namespace PdfEngine.Vector.Windows;

/// <summary>
/// Managed IMemoryOwner implementation backed by ArrayPool or heap allocation.
/// </summary>
public sealed class ManagedMemoryOwner : IMemoryOwner<byte>
{
    private byte[]? _buffer;
    private readonly int _length;

    public Memory<byte> Memory
    {
        get
        {
            if (_buffer == null)
                throw new ObjectDisposedException(nameof(ManagedMemoryOwner));
            return new Memory<byte>(_buffer, 0, _length);
        }
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(ManagedMemoryOwner));

    public ManagedMemoryOwner(int length)
    {
        _length = length;
        _buffer = new byte[length];
    }

    public void Dispose()
    {
        _buffer = null;
    }
}
