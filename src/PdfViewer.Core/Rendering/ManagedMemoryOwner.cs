using System.Buffers;

namespace PdfViewer.Core.Rendering;

/// <summary>
/// Managed heap-allocated IMemoryOwner for UI-neutral image operations.
/// </summary>
public sealed class ManagedMemoryOwner : IMemoryOwner<byte>
{
    private byte[]? _buffer;
    private readonly int _length;

    /// <summary>
    /// Throws once disposed rather than silently returning an empty Memory, which turned a
    /// use-after-dispose into a zero-length read/write that succeeded and corrupted results
    /// instead of surfacing the bug.
    /// </summary>
    public Memory<byte> Memory => _buffer != null
        ? new Memory<byte>(_buffer, 0, _length)
        : throw new ObjectDisposedException(nameof(ManagedMemoryOwner));

    public ManagedMemoryOwner(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _length = length;
        _buffer = new byte[length];
    }

    public void Dispose()
    {
        _buffer = null;
    }
}
