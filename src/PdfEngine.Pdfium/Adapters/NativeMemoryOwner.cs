using System.Buffers;
using System.Runtime.InteropServices;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Manages unmanaged memory buffer allocation and disposal as an IMemoryOwner for high-performance zero-copy pixel rendering.
/// </summary>
public sealed class NativeMemoryOwner : IMemoryOwner<byte>
{
    private IntPtr _pointer;
    private readonly int _length;
    private readonly PointerMemoryManager _manager;
    private bool _disposed;

    public Memory<byte> Memory { get; }
    public IntPtr Pointer => _pointer;
    public int Length => _length;

    public unsafe NativeMemoryOwner(int byteLength)
    {
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        _length = byteLength;
        _pointer = Marshal.AllocHGlobal(byteLength);

        // Wrap pointer into unmanaged memory manager
        _manager = new PointerMemoryManager((byte*)_pointer.ToPointer(), byteLength);
        Memory = _manager.Memory;
    }

    /// <summary>
    /// Safety net for callers that fail to dispose. Without this, an undisposed
    /// RenderedPage leaked its entire pixel buffer (tens of MB per page) for the life of
    /// the process, invisible to the GC because it is unmanaged memory.
    /// </summary>
    ~NativeMemoryOwner()
    {
        ReleaseBuffer();
    }

    public void Dispose()
    {
        ReleaseBuffer();
        GC.SuppressFinalize(this);
    }

    private void ReleaseBuffer()
    {
        if (_disposed) return;
        _disposed = true;

        // Invalidate the manager BEFORE freeing, so any read that races or follows disposal
        // throws ObjectDisposedException instead of silently returning a Span over freed
        // heap (which read garbage pixels or crashed the process uncatchably).
        _manager?.Invalidate();

        IntPtr buffer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private sealed unsafe class PointerMemoryManager : MemoryManager<byte>
    {
        private byte* _ptr;
        private readonly int _length;
        private volatile bool _invalidated;

        public PointerMemoryManager(byte* ptr, int length)
        {
            _ptr = ptr;
            _length = length;
        }

        public void Invalidate()
        {
            _invalidated = true;
            _ptr = null;
        }

        public override Span<byte> GetSpan()
        {
            ThrowIfInvalidated();
            return new(_ptr, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ThrowIfInvalidated();
            if (elementIndex < 0 || elementIndex >= _length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle(_ptr + elementIndex);
        }

        private void ThrowIfInvalidated()
        {
            if (_invalidated || _ptr == null)
                throw new ObjectDisposedException(nameof(NativeMemoryOwner),
                    "The rendered page buffer has already been released.");
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
