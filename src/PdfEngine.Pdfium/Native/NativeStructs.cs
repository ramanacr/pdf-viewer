using System.Runtime.InteropServices;

namespace PdfEngine.Pdfium.Native;

[StructLayout(LayoutKind.Sequential)]
public struct FPDF_LIBRARY_CONFIG
{
    public int version;
    public IntPtr m_pUserFontPaths;
    public IntPtr m_pIsolate;
    public uint m_v8EmbedderSlot;
    public IntPtr m_pPlatform;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_SIZEF
{
    public float width;
    public float height;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_RECTF
{
    public float left;
    public float top;
    public float right;
    public float bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_POINTF
{
    public float x;
    public float y;
}

[StructLayout(LayoutKind.Sequential)]
public struct FS_QUADPOINTSF
{
    public float x1;
    public float y1;
    public float x2;
    public float y2;
    public float x3;
    public float y3;
    public float x4;
    public float y4;
}

/// <summary>
/// FPDF_SYSTEMTIME, returned by the FFI_GetLocalTime callback.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FPDF_SYSTEMTIME
{
    public ushort wYear;
    public ushort wMonth;
    public ushort wDayOfWeek;
    public ushort wDay;
    public ushort wHour;
    public ushort wMinute;
    public ushort wSecond;
    public ushort wMilliseconds;
}

/// <summary>
/// FPDF_FORMFILLINFO, version 1 (fpdf_formfill.h).
///
/// This host does not present interactive widgets, run document JavaScript or service
/// timers, but the callbacks must still be NON-NULL: PDFium invokes several of them without
/// a null check - notably Release from the environment's destructor - so leaving them null
/// crashes inside FPDFDOC_ExitFormFillEnvironment. PdfiumFormEnvironment therefore installs
/// no-op stubs and keeps them alive for the environment's lifetime.
///
/// Deliberately version 1, NOT 2: version 2 enables the XFA members and their code paths,
/// which this build neither ships nor wants.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FPDF_FORMFILLINFO
{
    public int version;

    public IntPtr Release;
    public IntPtr FFI_Invalidate;
    public IntPtr FFI_OutputSelectedRect;
    public IntPtr FFI_SetCursor;
    public IntPtr FFI_SetTimer;
    public IntPtr FFI_KillTimer;
    public IntPtr FFI_GetLocalTime;
    public IntPtr FFI_OnChange;
    public IntPtr FFI_GetPage;
    public IntPtr FFI_GetCurrentPage;
    public IntPtr FFI_GetRotation;
    public IntPtr FFI_ExecuteNamedAction;
    public IntPtr FFI_SetTextFieldFocus;
    public IntPtr FFI_DoURIAction;
    public IntPtr FFI_DoGoToAction;

    /// <summary>FPDF_JSPLATFORM*. Null keeps document JavaScript unavailable.</summary>
    public IntPtr m_pJsPlatform;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int FPDF_WriteBlockCallback(IntPtr pThis, IntPtr pData, uint size);

[StructLayout(LayoutKind.Sequential)]
public struct FPDF_FILEWRITE
{
    public int version;
    [MarshalAs(UnmanagedType.FunctionPtr)]
    public FPDF_WriteBlockCallback WriteBlock;
}
