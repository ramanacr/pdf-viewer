using System.Runtime.InteropServices;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// The spooler's XPS print API (prntvpt / Windows.Graphics.Printing): a document package target
/// for one print job, which Direct2D's print control writes pages into. Called through the
/// vtable, so no built-in COM interop is involved and every pointer stays explicit.
/// </summary>
internal static unsafe class PrintInterop
{
    private static readonly Guid ClsidPrintDocumentPackageTargetFactory = new("348EF17D-6C81-4982-92B4-EE188A43867A");
    private static readonly Guid IidPrintDocumentPackageTargetFactory = new("D2959BF7-B31B-4A3D-9600-712EB1335BA4");

    private const uint ClsctxInprocServer = 0x1, ClsctxLocalServer = 0x4;
    private const uint StgmReadWrite = 0x2, StgmShareExclusive = 0x10, StgmCreate = 0x1000;
    private const uint CoinitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHCreateMemStream(byte* data, uint size);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IntPtr stream);

    /// <summary>
    /// Joins the multithreaded apartment for the calling thread; true when the caller must
    /// <see cref="LeaveApartment"/> afterwards. A thread already in an apartment keeps it.
    /// </summary>
    public static bool EnterApartment()
    {
        int hr = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        return hr >= 0; // S_OK or S_FALSE both take a reference; RPC_E_CHANGED_MODE does not
    }

    public static void LeaveApartment() => CoUninitialize();

    /// <summary>A native IStream over a copy of <paramref name="data"/>; release it with <see cref="Marshal.Release"/>.</summary>
    public static IntPtr CreateMemoryStream(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            var stream = SHCreateMemStream(p, (uint)data.Length);
            if (stream == IntPtr.Zero) throw new OutOfMemoryException("Could not create a stream for the print ticket.");
            return stream;
        }
    }

    /// <summary>A native IStream writing to a new file (the job's output instead of the printer port).</summary>
    public static IntPtr CreateFileStream(string path)
    {
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, StgmCreate | StgmReadWrite | StgmShareExclusive, 0x80 /* normal */, true, IntPtr.Zero, out var stream));
        return stream;
    }

    /// <summary>
    /// IPrintDocumentPackageTargetFactory::CreateDocumentPackageTargetForPrintJob: an
    /// IPrintDocumentPackageTarget (returned AddRef'd) for a new job on <paramref name="printerName"/>.
    /// </summary>
    public static IntPtr CreateDocumentPackageTarget(string printerName, string jobName, IntPtr outputStream, IntPtr printTicketStream)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidPrintDocumentPackageTargetFactory, IntPtr.Zero, ClsctxInprocServer | ClsctxLocalServer,
            IidPrintDocumentPackageTargetFactory, out var factory));
        try
        {
            var vtable = *(IntPtr**)factory;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, char*, char*, IntPtr, IntPtr, IntPtr*, int>)vtable[3];
            IntPtr target;
            int hr;
            fixed (char* printer = printerName)
            fixed (char* job = jobName)
                hr = create(factory, printer, job, outputStream, printTicketStream, &target);
            Marshal.ThrowExceptionForHR(hr);
            return target;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>IPrintDocumentPackageTarget::Cancel: stops the job and drops what was spooled.</summary>
    public static void CancelTarget(IntPtr target)
    {
        var vtable = *(IntPtr**)target;
        var cancel = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[5];
        _ = cancel(target);
    }
}
