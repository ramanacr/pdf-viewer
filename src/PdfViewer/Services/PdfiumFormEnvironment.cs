using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PdfViewer.Services;

/// <summary>
/// PDFium's form-fill environment, used for one thing: drawing form fields. FPDF_RenderPageBitmap
/// with FPDF_ANNOT renders every annotation "except widget and popup annotations" (fpdfview.h);
/// widgets are drawn by FPDF_FFLDraw after the page, and that needs this environment. Without it
/// every page PDFium drew (thumbnails, fallback pages, printing) showed forms empty.
///
/// The environment was removed once as "heap-corrupting". The cause was the binding, not PDFium:
/// the FPDF_FORMFILLINFO was passed as <c>ref</c> to a managed struct, which the marshaller pins
/// only for the call, while PDFium keeps the pointer — fpdf_formfill.h: "must remain valid until
/// the returned FPDF_FORMHANDLE is closed". Here the struct lives in native memory for the
/// handle's whole life, allocated at its full size (version-2 members zeroed), and the callbacks
/// are static UnmanagedCallersOnly methods, so there is nothing for the collector to move or free.
/// Version 1: no XFA. No JavaScript platform: document scripts never run. The mandatory
/// callbacks (Invalidate, SetCursor, SetTimer, KillTimer, GetLocalTime, GetPage, GetRotation,
/// ExecuteNamedAction) are implemented as no-ops; nothing here is interactive.
/// </summary>
internal sealed unsafe class PdfiumFormEnvironment : IDisposable
{
    // 16 v1 callbacks + JS platform + v2 members; generously sized so every read PDFium makes
    // lands in zeroed memory we own.
    private const int StructBytes = 64 * 8;

    private IntPtr _info;
    public IntPtr Handle { get; private set; }

    private PdfiumFormEnvironment(IntPtr info, IntPtr handle)
    {
        _info = info;
        Handle = handle;
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFDOC_InitFormFillEnvironment(SafeDocumentHandle document, IntPtr formInfo);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FORM_OnAfterLoadPage(SafePageHandle page, IntPtr formHandle);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FORM_OnBeforeClosePage(SafePageHandle page, IntPtr formHandle);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_FFLDraw(IntPtr formHandle, IntPtr bitmap, SafePageHandle page,
        int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>The environment for a document, or null when PDFium declines (no form handle).</summary>
    public static PdfiumFormEnvironment? Create(SafeDocumentHandle document)
    {
        IntPtr info = Marshal.AllocHGlobal(StructBytes);
        new Span<byte>((void*)info, StructBytes).Clear();
        var p = (IntPtr*)((byte*)info + 8); // after int version (padded to 8)
        *(int*)info = 1;
        p[0] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&Release;
        p[1] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, double, double, double, double, void>)&Invalidate;
        p[2] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, double, double, double, double, void>)&OutputSelectedRect;
        p[3] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, void>)&SetCursor;
        p[4] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int>)&SetTimer;
        p[5] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, void>)&KillTimer;
        p[6] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, SystemTime>)&GetLocalTime;
        p[7] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnChange;
        p[8] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr>)&GetPage;
        p[9] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&GetCurrentPage;
        p[10] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&GetRotation;
        p[11] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&ExecuteNamedAction;
        p[12] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, void>)&SetTextFieldFocus;
        p[13] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DoUriAction;
        p[14] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, int, void>)&DoGoToAction;
        // p[15] = m_pJsPlatform: null (no JavaScript).

        IntPtr handle;
        try
        {
            handle = FPDFDOC_InitFormFillEnvironment(document, info);
        }
        catch (EntryPointNotFoundException)
        {
            handle = IntPtr.Zero;
        }
        if (handle == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(info);
            return null;
        }
        return new PdfiumFormEnvironment(info, handle);
    }

    /// <summary>Tells the environment a page was loaded (required before drawing its fields).</summary>
    public void PageLoaded(SafePageHandle page) => FORM_OnAfterLoadPage(page, Handle);

    /// <summary>Tells the environment a page is about to close.</summary>
    public void PageClosing(SafePageHandle page) => FORM_OnBeforeClosePage(page, Handle);

    /// <summary>Draws the page's form fields over what FPDF_RenderPageBitmap drew.</summary>
    public void DrawFields(IntPtr bitmap, SafePageHandle page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags) =>
        FPDF_FFLDraw(Handle, bitmap, page, startX, startY, sizeX, sizeY, rotate, flags);

    /// <summary>Must run before the document closes.</summary>
    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            FPDFDOC_ExitFormFillEnvironment(Handle);
            Handle = IntPtr.Zero;
        }
        if (_info != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_info);
            _info = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Release(IntPtr self) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Invalidate(IntPtr self, IntPtr page, double l, double t, double r, double b) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OutputSelectedRect(IntPtr self, IntPtr page, double l, double t, double r, double b) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetCursor(IntPtr self, int cursor) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetTimer(IntPtr self, int elapse, IntPtr callback) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void KillTimer(IntPtr self, int id) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static SystemTime GetLocalTime(IntPtr self)
    {
        var now = DateTime.Now;
        return new SystemTime
        {
            Year = (ushort)now.Year, Month = (ushort)now.Month, DayOfWeek = (ushort)now.DayOfWeek, Day = (ushort)now.Day,
            Hour = (ushort)now.Hour, Minute = (ushort)now.Minute, Second = (ushort)now.Second, Milliseconds = (ushort)now.Millisecond,
        };
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnChange(IntPtr self) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetPage(IntPtr self, IntPtr document, int index) => IntPtr.Zero;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetCurrentPage(IntPtr self, IntPtr document) => IntPtr.Zero;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetRotation(IntPtr self, IntPtr page) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ExecuteNamedAction(IntPtr self, IntPtr name) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetTextFieldFocus(IntPtr self, IntPtr value, int length, int focused) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DoUriAction(IntPtr self, IntPtr uri) { }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void DoGoToAction(IntPtr self, int page, int zoomMode, IntPtr position, int count) { }
}
