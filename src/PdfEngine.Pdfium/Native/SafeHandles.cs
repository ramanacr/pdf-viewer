using Microsoft.Win32.SafeHandles;

namespace PdfEngine.Pdfium.Native;

public sealed class SafeDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeDocumentHandle() : base(true) { }

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            PdfiumNativeBridge.FPDF_CloseDocument(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

public sealed class SafePageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePageHandle() : base(true) { }

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            PdfiumNativeBridge.FPDF_ClosePage(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

public sealed class SafeTextPageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeTextPageHandle() : base(true) { }

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            PdfiumNativeBridge.FPDFText_ClosePage(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

public sealed class SafeSearchHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSearchHandle() : base(true) { }

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            PdfiumNativeBridge.FPDFText_FindClose(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}

public sealed class SafeAnnotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeAnnotHandle() : base(true) { }

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            PdfiumNativeBridge.FPDFPage_CloseAnnot(handle);
            handle = IntPtr.Zero;
        }
        return true;
    }
}
