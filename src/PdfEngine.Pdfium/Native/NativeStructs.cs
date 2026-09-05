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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int FPDF_WriteBlockCallback(IntPtr pThis, IntPtr pData, uint size);

[StructLayout(LayoutKind.Sequential)]
public struct FPDF_FILEWRITE
{
    public int version;
    [MarshalAs(UnmanagedType.FunctionPtr)]
    public FPDF_WriteBlockCallback WriteBlock;
}
