using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using PdfEngine.Pdfium.Native;

namespace PdfEngine.Vector.Oracle;

/// <summary>
/// PDFium as the reference for the native document services: page geometry, metadata, outline,
/// links, attachments and annotations as PDFium's public API reports them, and page renders.
/// Test and developer tooling only (06 "Removal sequence" step 4); shipped code never uses it.
/// All calls hold the process-wide PDFium lock.
/// </summary>
public sealed class PdfiumOracle : IDisposable
{
    public static readonly string[] AnnotSubtypes =
    {
        "Unknown", "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline",
        "Squiggly", "StrikeOut", "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Sound", "Movie", "Widget", "Screen",
        "PrinterMark", "TrapNet", "Watermark", "3D", "RichMedia", "XFAWidget", "Redact",
    };

    private readonly IntPtr _buffer;
    private readonly IntPtr _doc;

    public PdfiumOracle(byte[] bytes, string? password = null)
    {
        EnsureLoaded();
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            _buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _buffer, bytes.Length);
            _doc = N.FPDF_LoadMemDocument(_buffer, bytes.Length, password);
            if (_doc == IntPtr.Zero)
            {
                uint error = N.FPDF_GetLastError();
                Marshal.FreeHGlobal(_buffer);
                throw new InvalidOperationException($"PDFium cannot open the document (error {error}).");
            }
        }
    }

    public void Dispose()
    {
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            N.FPDF_CloseDocument(_doc);
            Marshal.FreeHGlobal(_buffer);
        }
    }

    // ------------------------------------------------------------------ document

    public int PageCount { get { lock (PdfiumNativeBridge.PdfiumLock) return N.FPDF_GetPageCount(_doc); } }

    public (double Width, double Height) PageSize(int index)
    {
        lock (PdfiumNativeBridge.PdfiumLock)
            return N.FPDF_GetPageSizeByIndexF(_doc, index, out var s) != 0 ? (s.width, s.height) : (0, 0);
    }

    public int Rotation(int index) => WithPage(index, p => (N.FPDFPage_GetRotation(p) % 4 + 4) % 4 * 90);

    /// <summary>The page box (crop within media), normalized: left, bottom, right, top.</summary>
    public (double L, double B, double R, double T) BoundingBox(int index) => WithPage(index, p =>
        N.FPDF_GetPageBoundingBox(p, out var r) != 0 ? (Math.Min(r.left, r.right), Math.Min(r.top, r.bottom), Math.Max(r.left, r.right), Math.Max(r.top, r.bottom)) : (0d, 0d, 0d, 0d));

    public string MetaText(string tag)
    {
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            uint len = N.FPDF_GetMetaText(_doc, tag, null, 0);
            if (len <= 2) return string.Empty;
            var buf = new byte[len];
            N.FPDF_GetMetaText(_doc, tag, buf, len);
            return Utf16(buf);
        }
    }

    public int SecurityRevision { get { lock (PdfiumNativeBridge.PdfiumLock) return N.FPDF_GetSecurityHandlerRevision(_doc); } }
    public uint Permissions { get { lock (PdfiumNativeBridge.PdfiumLock) return N.FPDF_GetDocPermissions(_doc); } }
    public int FileVersion { get { lock (PdfiumNativeBridge.PdfiumLock) return N.FPDF_GetFileVersion(_doc, out int v) != 0 ? v : 0; } }

    // ------------------------------------------------------------------ outline

    public sealed record Bookmark(int Depth, string Title, int PageIndex);

    /// <summary>The outline depth first, as FPDFBookmark_* walks it (cycles cut).</summary>
    public List<Bookmark> Outline()
    {
        var list = new List<Bookmark>();
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            var seen = new HashSet<IntPtr>();
            void Walk(IntPtr item, int depth)
            {
                for (; item != IntPtr.Zero && depth < 64 && seen.Add(item) && list.Count < 100_000; item = N.FPDFBookmark_GetNextSibling(_doc, item))
                {
                    uint len = N.FPDFBookmark_GetTitle(item, null, 0);
                    string title = string.Empty;
                    if (len > 2)
                    {
                        var buf = new byte[len];
                        N.FPDFBookmark_GetTitle(item, buf, len);
                        title = Utf16(buf);
                    }
                    IntPtr dest = N.FPDFBookmark_GetDest(_doc, item);
                    if (dest == IntPtr.Zero && N.FPDFBookmark_GetAction(item) is var action && action != IntPtr.Zero && N.FPDFAction_GetType(action) == 1)
                        dest = N.FPDFAction_GetDest(_doc, action);
                    list.Add(new Bookmark(depth, title, dest != IntPtr.Zero ? N.FPDFDest_GetDestPageIndex(_doc, dest) : -1));
                    Walk(N.FPDFBookmark_GetFirstChild(_doc, item), depth + 1);
                }
            }
            Walk(N.FPDFBookmark_GetFirstChild(_doc, IntPtr.Zero), 0);
        }
        return list;
    }

    // ------------------------------------------------------------------ links

    /// <summary>A link: rectangle (normalized), action type (FPDFAction_GetType; -1 when it has no action), destination page, URI.</summary>
    public sealed record Link(double L, double B, double R, double T, int ActionType, int PageIndex, string? Uri);

    public List<Link> Links(int index) => WithPage(index, page =>
    {
        var links = new List<Link>();
        int pos = 0;
        while (N.FPDFLink_Enumerate(page, ref pos, out IntPtr link) != 0 && links.Count < 100_000)
        {
            N.FPDFLink_GetAnnotRect(link, out var r);
            // FPDFLink_GetDest falls back to the action's destination (go-to, remote go-to).
            IntPtr dest = N.FPDFLink_GetDest(_doc, link);
            IntPtr action = N.FPDFLink_GetAction(link);
            int type = action != IntPtr.Zero ? (int)N.FPDFAction_GetType(action) : -1;
            string? uri = null;
            if (type == 3)
            {
                uint len = N.FPDFAction_GetURIPath(_doc, action, null, 0);
                if (len > 0)
                {
                    var buf = new byte[len];
                    N.FPDFAction_GetURIPath(_doc, action, buf, len);
                    uri = Encoding.Latin1.GetString(buf, 0, (int)len - 1);
                }
            }
            links.Add(new Link(Math.Min(r.left, r.right), Math.Min(r.top, r.bottom), Math.Max(r.left, r.right), Math.Max(r.top, r.bottom),
                type, dest != IntPtr.Zero ? N.FPDFDest_GetDestPageIndex(_doc, dest) : -1, uri));
        }
        return links;
    });

    // ------------------------------------------------------------------ attachments

    public List<(string Name, long Size)> Attachments()
    {
        var list = new List<(string, long)>();
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            int count = N.FPDFDoc_GetAttachmentCount(_doc);
            for (int i = 0; i < count; i++)
            {
                IntPtr a = N.FPDFDoc_GetAttachment(_doc, i);
                if (a == IntPtr.Zero) { list.Add((string.Empty, -1)); continue; }
                uint len = N.FPDFAttachment_GetName(a, null, 0);
                string name = string.Empty;
                if (len > 2)
                {
                    var buf = new byte[len];
                    N.FPDFAttachment_GetName(a, buf, len);
                    name = Utf16(buf);
                }
                long size = N.FPDFAttachment_GetFile(a, null, 0, out uint fileLength) != 0 ? fileLength : -1;
                list.Add((name, size));
            }
        }
        return list;
    }

    public byte[]? AttachmentBytes(int index)
    {
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            IntPtr a = N.FPDFDoc_GetAttachment(_doc, index);
            if (a == IntPtr.Zero || N.FPDFAttachment_GetFile(a, null, 0, out uint length) == 0) return null;
            var buf = new byte[length];
            if (length > 0) N.FPDFAttachment_GetFile(a, buf, length, out _);
            return buf;
        }
    }

    // ------------------------------------------------------------------ annotations

    public sealed record Annot(string Subtype, double L, double B, double R, double T, string Contents, string Author, int Quads, int InkPaths);

    /// <summary>The page's annotations in /Annots order (entries that are not dictionaries are skipped).</summary>
    public List<Annot> Annots(int index) => WithPage(index, page =>
    {
        var list = new List<Annot>();
        int count = N.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count && list.Count < 100_000; i++)
        {
            IntPtr a = N.FPDFPage_GetAnnot(page, i);
            if (a == IntPtr.Zero) continue;
            try
            {
                int subtype = N.FPDFAnnot_GetSubtype(a);
                N.FPDFAnnot_GetRect(a, out var r);
                list.Add(new Annot(subtype >= 0 && subtype < AnnotSubtypes.Length ? AnnotSubtypes[subtype] : "Unknown",
                    Math.Min(r.left, r.right), Math.Min(r.top, r.bottom), Math.Max(r.left, r.right), Math.Max(r.top, r.bottom),
                    AnnotString(a, "Contents"), AnnotString(a, "T"), (int)N.FPDFAnnot_CountAttachmentPoints(a), N.FPDFAnnot_GetInkListCount(a)));
            }
            finally
            {
                N.FPDFPage_CloseAnnot(a);
            }
        }
        return list;
    });

    private static string AnnotString(IntPtr annot, string key)
    {
        uint len = N.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (len <= 2) return string.Empty;
        var buf = new byte[len];
        N.FPDFAnnot_GetStringValue(annot, key, buf, len);
        return Utf16(buf);
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>
    /// BGRA pixels of the page at <paramref name="dpi"/>, upright (with /Rotate applied) and then
    /// turned a further <paramref name="quarterTurns"/> clockwise by PDFium itself, annotations drawn.
    /// </summary>
    public (int Width, int Height, byte[] Bgra) Render(int index, double dpi = 72, int quarterTurns = 0)
    {
        var (w, h) = PageSize(index);
        if (quarterTurns % 2 != 0) (w, h) = (h, w);
        int pw = Math.Max(1, (int)Math.Round(w * dpi / 72)), ph = Math.Max(1, (int)Math.Round(h * dpi / 72));
        return WithPage(index, page =>
        {
            IntPtr bitmap = N.FPDFBitmap_CreateEx(pw, ph, 4, IntPtr.Zero, 0);
            try
            {
                N.FPDFBitmap_FillRect(bitmap, 0, 0, pw, ph, 0xFFFFFFFF);
                N.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pw, ph, ((quarterTurns % 4) + 4) % 4, 0x01);
                int stride = N.FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[pw * ph * 4];
                IntPtr buffer = N.FPDFBitmap_GetBuffer(bitmap);
                for (int y = 0; y < ph; y++) Marshal.Copy(buffer + y * stride, pixels, y * pw * 4, pw * 4);
                return (pw, ph, pixels);
            }
            finally
            {
                N.FPDFBitmap_Destroy(bitmap);
            }
        });
    }

    /// <summary>Mean absolute channel difference (0..255) of two renders of the same size; 255 when the sizes differ.</summary>
    public static double MeanDifference((int Width, int Height, byte[] Bgra) a, (int Width, int Height, byte[] Bgra) b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255;
        long total = 0;
        for (int i = 0; i < a.Bgra.Length; i += 4)
            total += Math.Abs(a.Bgra[i] - b.Bgra[i]) + Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) + Math.Abs(a.Bgra[i + 2] - b.Bgra[i + 2]);
        return total / (3.0 * a.Width * a.Height);
    }

    // ------------------------------------------------------------------ plumbing

    private T WithPage<T>(int index, Func<IntPtr, T> body)
    {
        lock (PdfiumNativeBridge.PdfiumLock)
        {
            IntPtr page = N.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new InvalidOperationException($"PDFium cannot load page {index + 1}.");
            try { return body(page); }
            finally { N.FPDF_ClosePage(page); }
        }
    }

    private static string Utf16(byte[] buf)
    {
        int length = buf.Length;
        while (length >= 2 && buf[length - 1] == 0 && buf[length - 2] == 0) length -= 2;
        return Encoding.Unicode.GetString(buf, 0, length);
    }

    private static bool _loaded;

    /// <summary>Loads PDFium through the engine's own bridge (which finds the DLL), then binds these imports to the same module.</summary>
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (typeof(PdfiumOracle))
        {
            if (_loaded) return;
            PdfiumNativeBridge.EnsureInitialized();
            string? path = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => string.Equals(m.ModuleName, "pdfium.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
            NativeLibrary.SetDllImportResolver(typeof(PdfiumOracle).Assembly, (name, assembly, search) =>
                name == "pdfium_oracle" && path != null ? NativeLibrary.Load(path) : IntPtr.Zero);
            _loaded = true;
        }
    }

    private static class N
    {
        private const string Dll = "pdfium_oracle";
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Dll)] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Dll)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr doc, int index, out FS_SIZEF size);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Dll)] public static extern int FPDF_GetPageBoundingBox(IntPtr page, out FS_RECTF rect);
        [DllImport(Dll)] public static extern uint FPDF_GetMetaText(IntPtr doc, [MarshalAs(UnmanagedType.LPStr)] string tag, byte[]? buffer, uint length);
        [DllImport(Dll)] public static extern int FPDF_GetSecurityHandlerRevision(IntPtr doc);
        [DllImport(Dll)] public static extern uint FPDF_GetDocPermissions(IntPtr doc);
        [DllImport(Dll)] public static extern int FPDF_GetFileVersion(IntPtr doc, out int version);
        [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr doc, IntPtr bookmark);
        [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr doc, IntPtr bookmark);
        [DllImport(Dll)] public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint length);
        [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetDest(IntPtr doc, IntPtr bookmark);
        [DllImport(Dll)] public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);
        [DllImport(Dll)] public static extern uint FPDFAction_GetType(IntPtr action);
        [DllImport(Dll)] public static extern IntPtr FPDFAction_GetDest(IntPtr doc, IntPtr action);
        [DllImport(Dll)] public static extern uint FPDFAction_GetURIPath(IntPtr doc, IntPtr action, byte[]? buffer, uint length);
        [DllImport(Dll)] public static extern int FPDFDest_GetDestPageIndex(IntPtr doc, IntPtr dest);
        [DllImport(Dll)] public static extern int FPDFLink_Enumerate(IntPtr page, ref int start, out IntPtr link);
        [DllImport(Dll)] public static extern IntPtr FPDFLink_GetDest(IntPtr doc, IntPtr link);
        [DllImport(Dll)] public static extern IntPtr FPDFLink_GetAction(IntPtr link);
        [DllImport(Dll)] public static extern int FPDFLink_GetAnnotRect(IntPtr link, out FS_RECTF rect);
        [DllImport(Dll)] public static extern int FPDFDoc_GetAttachmentCount(IntPtr doc);
        [DllImport(Dll)] public static extern IntPtr FPDFDoc_GetAttachment(IntPtr doc, int index);
        [DllImport(Dll)] public static extern uint FPDFAttachment_GetName(IntPtr attachment, byte[]? buffer, uint length);
        [DllImport(Dll)] public static extern int FPDFAttachment_GetFile(IntPtr attachment, byte[]? buffer, uint length, out uint written);
        [DllImport(Dll)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
        [DllImport(Dll)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
        [DllImport(Dll)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
        [DllImport(Dll)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
        [DllImport(Dll)] public static extern int FPDFAnnot_GetRect(IntPtr annot, out FS_RECTF rect);
        [DllImport(Dll)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, byte[]? buffer, uint length);
        [DllImport(Dll)] public static extern UIntPtr FPDFAnnot_CountAttachmentPoints(IntPtr annot);
        [DllImport(Dll)] public static extern int FPDFAnnot_GetInkListCount(IntPtr annot);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr first, int stride);
        [DllImport(Dll)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
        [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }
}
