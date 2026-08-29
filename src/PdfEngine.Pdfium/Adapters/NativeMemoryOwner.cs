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
        var manager = new PointerMemoryManager((byte*)_pointer.ToPointer(), byteLength);
        Memory = manager.Memory;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pointer);
                _pointer = IntPtr.Zero;
            }
        }
    }

    private sealed unsafe class PointerMemoryManager : MemoryManager<byte>
    {
        private readonly byte* _ptr;
        private readonly int _length;

        public PointerMemoryManager(byte* ptr, int length)
        {
            _ptr = ptr;
            _length = length;
        }

        public override Span<byte> GetSpan() => new(_ptr, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex >= _length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle(_ptr + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
