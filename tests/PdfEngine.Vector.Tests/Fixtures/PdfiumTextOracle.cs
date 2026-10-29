using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PdfEngine.Pdfium;
using PdfEngine.Pdfium.Adapters;
using PdfEngine.Pdfium.Native;
using PdfEngine.Vector.Text;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// PDFium's text page for one page of a document (the oracle the native text page is measured
/// against): characters, their types and boxes, the page text, and search hits.
/// </summary>
internal sealed class PdfiumTextOracle
{
    public string Text = string.Empty;
    public List<char> Unicode = new();
    public List<bool> Generated = new();
    public List<bool> Hyphen = new();
    public List<PdfTextRect> Boxes = new();
    public List<PdfTextRect> Loose = new();
    private readonly Dictionary<(string, uint), List<(int, int)>> _searches = new();

    public int Count => Unicode.Count;

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFText_IsGenerated(IntPtr textPage, int index);

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDFText_IsHyphen(IntPtr textPage, int index);

    /// <summary>Reads the page and runs each query with no options, match case and match whole word.</summary>
    public static PdfiumTextOracle Read(byte[] pdf, int pageNumber = 1, params string[] queries)
    {
        var o = new PdfiumTextOracle();
        using var engine = new PdfiumEngine();
        using var doc = (PdfiumDocument)engine.OpenDocumentAsync(pdf).AsTask().GetAwaiter().GetResult();
        lock (doc.SyncLock)
        {
            using var page = PdfiumNativeBridge.FPDF_LoadPage(doc.Handle, pageNumber - 1);
            using var text = PdfiumNativeBridge.FPDFText_LoadPage(page);
            int n = PdfiumNativeBridge.FPDFText_CountChars(text);
            if (n > 0)
            {
                var buf = new byte[(n + 1) * 2];
                int written = PdfiumNativeBridge.FPDFText_GetText(text, 0, n, buf);
                o.Text = written > 0 ? PdfiumNativeBridge.Utf16BytesToString(buf, written * 2) : string.Empty;
            }
            IntPtr raw = text.DangerousGetHandle();
            for (int i = 0; i < n; i++)
            {
                o.Unicode.Add((char)PdfiumNativeBridge.FPDFText_GetUnicode(text, i));
                o.Generated.Add(FPDFText_IsGenerated(raw, i) == 1);
                o.Hyphen.Add(FPDFText_IsHyphen(raw, i) == 1);
                PdfiumNativeBridge.FPDFText_GetCharBox(text, i, out double l, out double r, out double b, out double t);
                o.Boxes.Add(new PdfTextRect(l, b, r, t));
                PdfiumNativeBridge.FPDFText_GetLooseCharBox(text, i, out var rect);
                o.Loose.Add(new PdfTextRect(rect.left, rect.bottom, rect.right, rect.top));
            }
            foreach (string q in queries)
            {
                foreach (uint flags in new[] { 0u, PdfiumNativeBridge.FPDF_MATCHCASE, PdfiumNativeBridge.FPDF_MATCHWHOLEWORD })
                {
                    var hits = new List<(int, int)>();
                    using var h = PdfiumNativeBridge.FPDFText_FindStart(text, PdfiumNativeBridge.StringToUtf16NullTerminated(q), flags, 0);
                    while (h != null && !h.IsInvalid && PdfiumNativeBridge.FPDFText_FindNext(h) != 0 && hits.Count < 1000)
                        hits.Add((PdfiumNativeBridge.FPDFText_GetSchResultIndex(h), PdfiumNativeBridge.FPDFText_GetSchCount(h)));
                    o._searches[(q, flags)] = hits;
                }
            }
        }
        return o;
    }

    public List<(int, int)> Hits(string query, bool matchCase = false, bool wholeWord = false) =>
        _searches[(query, matchCase ? PdfiumNativeBridge.FPDF_MATCHCASE : wholeWord ? PdfiumNativeBridge.FPDF_MATCHWHOLEWORD : 0u)];
}
