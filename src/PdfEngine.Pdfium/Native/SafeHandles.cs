using Microsoft.Win32.SafeHandles;

namespace PdfEngine.Pdfium.Native;

/// <summary>
/// Base for PDFium handles.
///
/// ReleaseHandle runs on the GC's FINALIZER THREAD whenever a handle is not disposed
/// explicitly. PDFium is not thread-safe, so a finalizer closing a page or annotation while
/// the main thread is inside any other PDFium call corrupts shared state - which surfaces
/// later as an access violation in an unrelated call, typically wherever the next teardown
/// happens. Every release therefore takes the same process-wide lock as every other native
/// entry point.
/// </summary>
public abstract class SafePdfiumHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    protected SafePdfiumHandle() : base(true) { }

    /// <summary>Closes the native resource. Always invoked under the global PDFium lock.</summary>
    protected abstract void CloseNativeHandle(IntPtr nativeHandle);

    protected override bool ReleaseHandle()
    {
        if (IsInvalid) return true;

        IntPtr toClose = handle;
        handle = IntPtr.Zero;

        lock (PdfiumNativeBridge.PdfiumLock)
        {
            CloseNativeHandle(toClose);
        }

        return true;
    }
}

public sealed class SafeDocumentHandle : SafePdfiumHandle
{
    protected override void CloseNativeHandle(IntPtr nativeHandle)
        => PdfiumNativeBridge.FPDF_CloseDocument(nativeHandle);
}

public sealed class SafePageHandle : SafePdfiumHandle
{
    protected override void CloseNativeHandle(IntPtr nativeHandle)
        => PdfiumNativeBridge.FPDF_ClosePage(nativeHandle);
}

public sealed class SafeTextPageHandle : SafePdfiumHandle
{
    protected override void CloseNativeHandle(IntPtr nativeHandle)
        => PdfiumNativeBridge.FPDFText_ClosePage(nativeHandle);
}

public sealed class SafeSearchHandle : SafePdfiumHandle
{
    protected override void CloseNativeHandle(IntPtr nativeHandle)
        => PdfiumNativeBridge.FPDFText_FindClose(nativeHandle);
}

public sealed class SafeAnnotHandle : SafePdfiumHandle
{
    protected override void CloseNativeHandle(IntPtr nativeHandle)
        => PdfiumNativeBridge.FPDFPage_CloseAnnot(nativeHandle);
}
