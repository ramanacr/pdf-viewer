using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PdfViewer.Services;

#region Safe Handles

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

#endregion

#region Native Structs

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

#endregion

/// <summary>
/// Version-pinned C ABI / P/Invoke bridge for Google PDFium.
/// </summary>
public static class PdfiumNativeBridge
{
    private const string DllName = "pdfium";
    private static bool _isInitialized;

    /// <summary>
    /// The process-wide PDFium serialization lock. Deliberately the SAME object instance
    /// used by PdfEngine.Pdfium's bridge: PDFium's global font/codec/render-device state
    /// means both copies must serialize against each other, not just against themselves.
    /// </summary>
    public static object PdfiumLock => PdfEngine.Pdfium.Native.PdfiumNativeBridge.PdfiumLock;

    static PdfiumNativeBridge()
    {
        NativeLibrary.SetDllImportResolver(typeof(PdfiumNativeBridge).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (libraryName.Equals("pdfium", StringComparison.OrdinalIgnoreCase) ||
                libraryName.Equals("pdfium.dll", StringComparison.OrdinalIgnoreCase))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string arch = "win-x64";
                string[] candidatePaths =
                {
                    Path.Combine(baseDir, "pdfium.dll"),
                    Path.Combine(baseDir, "runtimes", arch, "native", "pdfium.dll"),
                    Path.Combine(baseDir, "..", "..", "..", "runtimes", arch, "native", "pdfium.dll"),
                    Path.Combine(baseDir, "..", "..", "..", "..", "src", "PdfViewer", "runtimes", arch, "native", "pdfium.dll"),
                    Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "PdfViewer", "runtimes", arch, "native", "pdfium.dll")
                };

                foreach (var p in candidatePaths)
                {
                    if (File.Exists(p))
                    {
                        if (NativeLibrary.TryLoad(p, out IntPtr hLib))
                        {
                            return hLib;
                        }
                    }
                }
            }
            return IntPtr.Zero;
        });

        EnsureInitialized();
    }

    public static void EnsureInitialized()
    {
        // Locks on the SAME object as PdfEngine.Pdfium's bridge copy, so the
        // check-then-set of PDFIUM_INITIALIZED is atomic across both copies (two
        // separate locks previously let both call FPDF_InitLibraryWithConfig).
        lock (PdfiumLock)
        {
            if (_isInitialized) return;

            if (AppDomain.CurrentDomain.GetData("PDFIUM_INITIALIZED") == null)
            {
                try
                {
                    var config = new FPDF_LIBRARY_CONFIG { version = 2 };
                    FPDF_InitLibraryWithConfig(ref config);
                }
                catch (Exception ex)
                {
                    // Do NOT latch _isInitialized on failure — that made every later call
                    // run against an uninitialized PDFium and surface as an unrelated
                    // "Failed to open PDF document" far from the real cause.
                    throw new InvalidOperationException(
                        "Failed to initialize the native PDFium library. Ensure pdfium.dll is deployed " +
                        "next to the application or under runtimes/win-x64/native/.", ex);
                }

                AppDomain.CurrentDomain.SetData("PDFIUM_INITIALIZED", true);
            }

            _isInitialized = true;
        }

        // Deliberately NOT calling FPDF_DestroyLibrary on ProcessExit: that handler runs on
        // an arbitrary thread without suspending others, so it could tear down PDFium while
        // a render was still in flight and race SafeHandle finalizers calling
        // FPDF_CloseDocument. The OS reclaims everything at exit anyway.
    }

    #region Constants

    // Bitmap formats
    public const int FPDFBitmap_Gray = 1;
    public const int FPDFBitmap_BGR = 2;
    public const int FPDFBitmap_BGRx = 3;
    public const int FPDFBitmap_BGRA = 4;

    // Render flags
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;
    public const int FPDF_NO_NATIVETEXT = 0x04;
    public const int FPDF_GRAYSCALE = 0x08;
    public const int FPDF_REVERSE_BYTE_ORDER = 0x10;
    public const int FPDF_PRINTING = 0x800;

    // Error codes
    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    // Search flags
    public const uint FPDF_MATCHCASE = 0x00000001;
    public const uint FPDF_MATCHWHOLEWORD = 0x00000002;
    public const uint FPDF_CONSECUTIVE = 0x00000004;

    // Flatten flags
    public const int FLAT_NORMALDISPLAY = 0;
    public const int FLAT_PRINT = 1;

    // Save flags
    public const uint FPDF_INCREMENTAL = 1;
    public const uint FPDF_NO_INCREMENTAL = 2;
    public const uint FPDF_REMOVE_SECURITY = 4;

    // Annotation subtypes
    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_LINK = 2;
    public const int FPDF_ANNOT_FREETEXT = 3;
    public const int FPDF_ANNOT_LINE = 4;
    public const int FPDF_ANNOT_SQUARE = 5;
    public const int FPDF_ANNOT_CIRCLE = 6;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_UNDERLINE = 10;
    public const int FPDF_ANNOT_STRIKEOUT = 12;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_POPUP = 16;
    public const int FPDF_ANNOT_WIDGET = 20;

    // Annotation color types
    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDFANNOT_COLORTYPE_InteriorColor = 1;

    #endregion

    #region P/Invoke Methods

    // Initialization & Library
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibraryWithConfig(ref FPDF_LIBRARY_CONFIG config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_DestroyLibrary();

    // Document Management
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern SafeDocumentHandle FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string file_path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafeDocumentHandle FPDF_LoadMemDocument(IntPtr data_buf, int size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetLastError();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(SafeDocumentHandle document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetFileVersion(SafeDocumentHandle document, out int fileVersion);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetSecurityHandlerRevision(SafeDocumentHandle document);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint FPDF_GetMetaText(SafeDocumentHandle document, [MarshalAs(UnmanagedType.LPStr)] string tag, byte[]? buffer, uint buflen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafeDocumentHandle FPDF_CreateNewDocument();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_SaveAsCopy(SafeDocumentHandle document, ref FPDF_FILEWRITE file_write, uint flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_SaveWithVersion(SafeDocumentHandle document, ref FPDF_FILEWRITE file_write, uint flags, int file_version);

    // Page Management & Geometry
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafePageHandle FPDF_LoadPage(SafeDocumentHandle document, int page_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafePageHandle FPDF_LoadPage(IntPtr document, int page_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float FPDF_GetPageWidthF(SafePageHandle page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float FPDF_GetPageHeightF(SafePageHandle page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageSizeByIndexF(SafeDocumentHandle document, int page_index, out FS_SIZEF size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int page_index, out FS_SIZEF size);

    // NOTE: the real PDFium export is FPDFPage_GetRotation. This was previously declared
    // as "FPDF_GetPageRotation", which is not exported by pdfium.dll at all and would have
    // thrown EntryPointNotFoundException on first call.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_GetRotation(SafePageHandle page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_SetRotation(SafePageHandle page, int rotate);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafePageHandle FPDFPage_New(SafeDocumentHandle document, int page_index, double width, double height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_Delete(SafeDocumentHandle document, int page_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_Flatten(SafePageHandle page, int nFlag);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_GenerateContent(SafePageHandle page);

    // Page Import (PPO)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_ImportPages(SafeDocumentHandle dest_doc, SafeDocumentHandle src_doc, [MarshalAs(UnmanagedType.LPStr)] string? pagerange, int index);

    // Bitmap & Rendering
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr first_scan, int stride);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, SafePageHandle page, int start_x, int start_y, int size_x, int size_y, int rotate, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int start_x, int start_y, int size_x, int size_y, int rotate, int flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    // Bookmarks / Outline
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetFirstChild(SafeDocumentHandle document, IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetNextSibling(SafeDocumentHandle document, IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint buflen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetDest(SafeDocumentHandle document, IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFAction_GetDest(SafeDocumentHandle document, IntPtr action);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFDest_GetDestPageIndex(SafeDocumentHandle document, IntPtr dest);

    // Links & Actions
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFLink_Enumerate(SafePageHandle page, ref int pos, out IntPtr link_annot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFLink_GetDest(SafeDocumentHandle document, IntPtr link_annot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFLink_GetAction(IntPtr link_annot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFAction_GetURIPath(SafeDocumentHandle document, IntPtr action, byte[]? buffer, uint buflen);

    // Text & Search
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafeTextPageHandle FPDFText_LoadPage(SafePageHandle page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFText_ClosePage(IntPtr text_page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountChars(SafeTextPageHandle text_page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDFText_GetUnicode(SafeTextPageHandle text_page, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetCharBox(SafeTextPageHandle text_page, int index, out double left, out double right, out double bottom, out double top);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetLooseCharBox(SafeTextPageHandle text_page, int index, out FS_RECTF rect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetText(SafeTextPageHandle text_page, int start_index, int count, byte[] result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_CountRects(SafeTextPageHandle text_page, int start_index, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetRect(SafeTextPageHandle text_page, int rect_index, out double left, out double top, out double right, out double bottom);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetBoundedText(SafeTextPageHandle text_page, double left, double top, double right, double bottom, byte[]? buffer, int buflen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafeSearchHandle FPDFText_FindStart(SafeTextPageHandle text_page, byte[] findwhat, uint flags, int start_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_FindNext(SafeSearchHandle handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_FindPrev(SafeSearchHandle handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetSchResultIndex(SafeSearchHandle handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFText_GetSchCount(SafeSearchHandle handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFText_FindClose(IntPtr handle);

    // Annotations
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_GetAnnotCount(SafePageHandle page);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafeAnnotHandle FPDFPage_GetAnnot(SafePageHandle page, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFPage_CloseAnnot(IntPtr annot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern SafeAnnotHandle FPDFPage_CreateAnnot(SafePageHandle page, int subtype);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFPage_RemoveAnnot(SafePageHandle page, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetSubtype(SafeAnnotHandle annot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetRect(SafeAnnotHandle annot, ref FS_RECTF rect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetRect(SafeAnnotHandle annot, out FS_RECTF rect);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_SetColor(SafeAnnotHandle annot, int type, uint R, uint G, uint B, uint A);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetColor(SafeAnnotHandle annot, int type, out uint R, out uint G, out uint B, out uint A);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int FPDFAnnot_SetStringValue(SafeAnnotHandle annot, [MarshalAs(UnmanagedType.LPStr)] string key, byte[] value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern uint FPDFAnnot_GetStringValue(SafeAnnotHandle annot, [MarshalAs(UnmanagedType.LPStr)] string key, byte[]? buffer, uint buflen);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_AppendAttachmentPoints(SafeAnnotHandle annot, ref FS_QUADPOINTSF quad_points);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_AddInkStroke(SafeAnnotHandle annot, [In] FS_POINTF[] points, int point_count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetInkListCount(SafeAnnotHandle annot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFAnnot_GetInkListPath(SafeAnnotHandle annot, int path_index, [Out] FS_POINTF[]? buffer, int length);

    #endregion

    #region Helper String Conversions

    public static string Utf16BytesToString(byte[] buffer, int byteLength)
    {
        if (buffer == null || byteLength <= 0) return string.Empty;
        // Trim trailing null characters
        int actualLen = byteLength;
        if (actualLen >= 2 && buffer[actualLen - 2] == 0 && buffer[actualLen - 1] == 0)
        {
            actualLen -= 2;
        }
        return Encoding.Unicode.GetString(buffer, 0, actualLen).TrimEnd('\0');
    }

    public static byte[] StringToUtf16NullTerminated(string text)
    {
        if (text == null) text = string.Empty;
        var bytes = Encoding.Unicode.GetBytes(text);
        var result = new byte[bytes.Length + 2];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        result[^2] = 0;
        result[^1] = 0;
        return result;
    }

    #endregion
}
