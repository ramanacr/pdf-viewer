using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Pdfium.Native;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Native PDFium annotation loader and multi-mode persistence service.
/// </summary>
public sealed class PdfiumAnnotationService : IPdfAnnotationService
{
    public ValueTask<IReadOnlyList<AnnotationModel>> LoadAnnotationsAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        cancellationToken.ThrowIfCancellationRequested();

        lock (pdfiumDoc.SyncLock)
        {
            var annotations = new List<AnnotationModel>();
            using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, pageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid) return ValueTask.FromResult<IReadOnlyList<AnnotationModel>>(annotations);

            float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
            float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

            int count = PdfiumNativeBridge.FPDFPage_GetAnnotCount(pageHandle);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(pageHandle, i);
                if (annot == null || annot.IsInvalid) continue;

                int subtype = PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot);
                if (subtype == PdfiumNativeBridge.FPDF_ANNOT_POPUP || subtype == PdfiumNativeBridge.FPDF_ANNOT_LINK)
                    continue;

                var model = new AnnotationModel
                {
                    PageNumber = pageNumber,
                    Type = MapSubtypeToAnnotationType(subtype)
                };

                if (PdfiumNativeBridge.FPDFAnnot_GetRect(annot, out var rect) != 0 && pageW > 0 && pageH > 0)
                {
                    model.X = Math.Max(0.0, Math.Min(1.0, rect.left / pageW));
                    model.Y = Math.Max(0.0, Math.Min(1.0, 1.0 - (rect.top / pageH)));
                    model.Width = Math.Max(0.0, Math.Min(1.0, (rect.right - rect.left) / pageW));
                    model.Height = Math.Max(0.0, Math.Min(1.0, (rect.top - rect.bottom) / pageH));
                }

                if (PdfiumNativeBridge.FPDFAnnot_GetColor(annot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_Color, out uint r, out uint g, out uint b, out uint a) != 0)
                {
                    model.Color = $"#{r:X2}{g:X2}{b:X2}";
                    // Map alpha straight through: the old "a > 0 ? a/255 : 1.0" turned a
                    // fully transparent annotation into a fully OPAQUE one, and a
                    // save round-trip then wrote it back at alpha 255.
                    model.Opacity = a / 255.0;
                }

                uint contentsLen = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "Contents", null, 0);
                if (contentsLen > 0)
                {
                    byte[] cBuf = new byte[contentsLen];
                    PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "Contents", cBuf, contentsLen);
                    model.Contents = PdfiumNativeBridge.Utf16BytesToString(cBuf, (int)contentsLen);
                }

                uint authorLen = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "T", null, 0);
                if (authorLen > 0)
                {
                    byte[] aBuf = new byte[authorLen];
                    PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "T", aBuf, authorLen);
                    model.Author = PdfiumNativeBridge.Utf16BytesToString(aBuf, (int)authorLen);
                }

                if (subtype == PdfiumNativeBridge.FPDF_ANNOT_INK)
                {
                    int inkListCount = PdfiumNativeBridge.FPDFAnnot_GetInkListCount(annot);
                    for (int p = 0; p < inkListCount; p++)
                    {
                        int ptCount = PdfiumNativeBridge.FPDFAnnot_GetInkListPath(annot, p, null, 0);
                        if (ptCount > 0)
                        {
                            var ptBuf = new FS_POINTF[ptCount];
                            PdfiumNativeBridge.FPDFAnnot_GetInkListPath(annot, p, ptBuf, ptCount);
                            var stroke = new InkStroke { Color = model.Color, Thickness = model.StrokeThickness };
                            foreach (var pt in ptBuf)
                            {
                                stroke.Points.Add(new Geometry.PdfPoint(pt.x / pageW, 1.0 - (pt.y / pageH)));
                            }
                            model.InkStrokes.Add(stroke);
                        }
                    }
                }

                annotations.Add(model);
            }

            return ValueTask.FromResult<IReadOnlyList<AnnotationModel>>(annotations);
        }
    }

    public ValueTask SaveAnnotatedDocumentAsync(
        IPdfDocument document,
        string targetPath,
        IReadOnlyList<AnnotationModel> annotations,
        AnnotationSaveMode mode,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(pdfiumDoc.FilePath), StringComparison.OrdinalIgnoreCase))
            throw new PdfSaveException("Target path cannot overwrite currently open document directly.", targetPath);

        cancellationToken.ThrowIfCancellationRequested();

        if (mode == AnnotationSaveMode.ExportXfdf)
        {
            // Collect page dimensions up front (under the lock) so the writer can convert
            // the app's normalized coordinates into the PDF points XFDF requires.
            var pageSizes = new Dictionary<int, (double Width, double Height)>();
            lock (pdfiumDoc.SyncLock)
            {
                foreach (int pageNumber in annotations.Select(a => a.PageNumber).Distinct())
                {
                    int pageIndex = pageNumber - 1;
                    if (pageIndex < 0 || pageIndex >= pdfiumDoc.PageCount) continue;

                    if (PdfiumNativeBridge.FPDF_GetPageSizeByIndexF(pdfiumDoc.Handle, pageIndex, out var size) != 0)
                    {
                        pageSizes[pageNumber] = (size.width, size.height);
                    }
                }
            }

            return ExportXfdfInternalAsync(targetPath, annotations, pageSizes, cancellationToken);
        }

        lock (pdfiumDoc.SyncLock)
        {
            // Create a duplicate document in memory
            byte[] fileBytes = File.ReadAllBytes(pdfiumDoc.FilePath);
            IntPtr unmanagedBuf = Marshal.AllocHGlobal(fileBytes.Length);
            Marshal.Copy(fileBytes, 0, unmanagedBuf, fileBytes.Length);

            // editDoc is declared INSIDE the try so FPDF_CloseDocument runs before the
            // finally frees unmanagedBuf. FPDF_LoadMemDocument does not copy the buffer, so
            // freeing it first left PDFium tearing down a document over freed heap.
            SafeDocumentHandle? editDoc = null;
            try
            {
                editDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, null);
                if (editDoc == null || editDoc.IsInvalid)
                {
                    throw new PdfSaveException(
                        $"Failed to open working copy for annotation saving (PDFium error {PdfiumNativeBridge.FPDF_GetLastError()}).",
                        targetPath);
                }

                var grouped = annotations.GroupBy(a => a.PageNumber);
                foreach (var grp in grouped)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int pageIndex = grp.Key - 1;
                    if (pageIndex < 0 || pageIndex >= pdfiumDoc.PageCount) continue;

                    using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(editDoc, pageIndex);
                    if (pageHandle == null || pageHandle.IsInvalid) continue;

                    float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
                    float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

                    foreach (var annot in grp)
                    {
                        int fpdfType = MapAnnotationTypeToSubtype(annot.Type);
                        using var nativeAnnot = PdfiumNativeBridge.FPDFPage_CreateAnnot(pageHandle, fpdfType);
                        if (nativeAnnot == null || nativeAnnot.IsInvalid) continue;

                        var rect = new FS_RECTF
                        {
                            left = (float)(annot.X * pageW),
                            right = (float)((annot.X + annot.Width) * pageW),
                            bottom = (float)((1.0 - annot.Y - annot.Height) * pageH),
                            top = (float)((1.0 - annot.Y) * pageH)
                        };
                        PdfiumNativeBridge.FPDFAnnot_SetRect(nativeAnnot, ref rect);

                        if (ParseHexColor(annot.Color, out uint r, out uint g, out uint b))
                        {
                            uint alpha = (uint)Math.Clamp((int)(annot.Opacity * 255), 0, 255);
                            PdfiumNativeBridge.FPDFAnnot_SetColor(nativeAnnot, PdfiumNativeBridge.FPDFANNOT_COLORTYPE_Color, r, g, b, alpha);
                        }

                        if (!string.IsNullOrEmpty(annot.Contents))
                        {
                            byte[] contentsBytes = PdfiumNativeBridge.StringToUtf16NullTerminated(annot.Contents);
                            PdfiumNativeBridge.FPDFAnnot_SetStringValue(nativeAnnot, "Contents", contentsBytes);
                        }

                        if (!string.IsNullOrEmpty(annot.Author))
                        {
                            byte[] authorBytes = PdfiumNativeBridge.StringToUtf16NullTerminated(annot.Author);
                            PdfiumNativeBridge.FPDFAnnot_SetStringValue(nativeAnnot, "T", authorBytes);
                        }

                        if (annot.Type == AnnotationType.Ink && annot.InkStrokes.Count > 0)
                        {
                            foreach (var stroke in annot.InkStrokes)
                            {
                                if (stroke.Points.Count > 1)
                                {
                                    var ptArray = stroke.Points.Select(p => new FS_POINTF
                                    {
                                        x = (float)(p.X * pageW),
                                        y = (float)((1.0 - p.Y) * pageH)
                                    }).ToArray();
                                    PdfiumNativeBridge.FPDFAnnot_AddInkStroke(nativeAnnot, ptArray, ptArray.Length);
                                }
                            }
                        }

                        if (annot.Type == AnnotationType.Highlight || annot.Type == AnnotationType.Underline || annot.Type == AnnotationType.StrikeOut)
                        {
                            var quad = new FS_QUADPOINTSF
                            {
                                x1 = rect.left, y1 = rect.top,
                                x2 = rect.right, y2 = rect.top,
                                x3 = rect.left, y3 = rect.bottom,
                                x4 = rect.right, y4 = rect.bottom
                            };
                            PdfiumNativeBridge.FPDFAnnot_AppendAttachmentPoints(nativeAnnot, ref quad);
                        }
                    }

                    PdfiumNativeBridge.FPDFPage_GenerateContent(pageHandle);

                    if (mode == AnnotationSaveMode.Flattened)
                    {
                        PdfiumNativeBridge.FPDFPage_Flatten(pageHandle, PdfiumNativeBridge.FLAT_NORMALDISPLAY);
                    }
                }

                // Save out to file
                using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Exception? writeFailure = null;
                var fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        // Never let a managed exception unwind through PDFium's C++ frames -
                        // it is built without exception support, so destructors are skipped
                        // and native state is left inconsistent. Signal failure with 0.
                        try
                        {
                            byte[] buffer = new byte[size];
                            Marshal.Copy(pData, buffer, 0, (int)size);
                            outStream.Write(buffer, 0, (int)size);
                            return 1;
                        }
                        catch (Exception ex)
                        {
                            writeFailure ??= ex;
                            return 0;
                        }
                    }
                };

                int saveResult = PdfiumNativeBridge.FPDF_SaveAsCopy(editDoc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
                GC.KeepAlive(fileWrite);

                if (writeFailure != null)
                    throw new PdfSaveException("Failed writing annotated document to disk.", writeFailure, targetPath);

                if (saveResult == 0)
                {
                    throw new PdfSaveException("Native FPDF_SaveAsCopy failed.", targetPath);
                }
            }
            finally
            {
                editDoc?.Dispose();
                Marshal.FreeHGlobal(unmanagedBuf);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Writes a spec-conformant XFDF file.
    /// The rect attribute must be PDF user-space points with a BOTTOM-LEFT origin
    /// (left,bottom,right,top) - the previous implementation emitted the raw normalized
    /// 0..1 top-down values, which collapsed every annotation into a sub-point speck at the
    /// page origin when opened in Acrobat. Colors must be #RRGGBB, not the app's #AARRGGBB.
    /// </summary>
    private static async ValueTask ExportXfdfInternalAsync(
        string targetPath,
        IReadOnlyList<AnnotationModel> annotations,
        IReadOnlyDictionary<int, (double Width, double Height)> pageSizes,
        CancellationToken ct)
    {
        XNamespace xfdfNs = "http://ns.adobe.com/xfdf/";

        var annotElements = new List<XElement>();
        foreach (var a in annotations)
        {
            if (!pageSizes.TryGetValue(a.PageNumber, out var size))
            {
                size = (612.0, 792.0);
            }

            double left = a.X * size.Width;
            double right = (a.X + a.Width) * size.Width;
            double bottom = (1.0 - a.Y - a.Height) * size.Height;
            double top = (1.0 - a.Y) * size.Height;

            var element = new XElement(xfdfNs + a.Type.ToString().ToLowerInvariant(),
                new XAttribute("page", a.PageNumber - 1),
                new XAttribute("rect", FormatInvariant(left, bottom, right, top)),
                new XAttribute("color", ToRgbHex(a.Color)),
                new XAttribute("opacity", a.Opacity.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("title", a.Author ?? string.Empty),
                new XElement(xfdfNs + "contents", a.Contents ?? string.Empty));

            // Markup annotations are drawn from quadpoints, not rect; without coords most
            // viewers render nothing at all for highlight/underline/strikeout.
            if (a.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut)
            {
                element.Add(new XAttribute("coords", FormatInvariant(
                    left, top, right, top, left, bottom, right, bottom)));
            }

            annotElements.Add(element);
        }

        var xfdf = new XElement(xfdfNs + "xfdf", new XElement(xfdfNs + "annots", annotElements));

        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await xfdf.SaveAsync(fs, SaveOptions.None, ct);
    }

    private static string FormatInvariant(params double[] values) =>
        string.Join(",", values.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Normalizes an app color (#AARRGGBB or #RRGGBB) to the #RRGGBB form XFDF requires.
    /// </summary>
    private static string ToRgbHex(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return "#000000";
        string hex = color.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex.Substring(2);   // drop alpha
        if (hex.Length != 6) return "#000000";
        return "#" + hex.ToUpperInvariant();
    }

    private static AnnotationType MapSubtypeToAnnotationType(int subtype) => subtype switch
    {
        PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT => AnnotationType.Highlight,
        PdfiumNativeBridge.FPDF_ANNOT_UNDERLINE => AnnotationType.Underline,
        PdfiumNativeBridge.FPDF_ANNOT_STRIKEOUT => AnnotationType.StrikeOut,
        PdfiumNativeBridge.FPDF_ANNOT_TEXT => AnnotationType.Note,
        PdfiumNativeBridge.FPDF_ANNOT_FREETEXT => AnnotationType.FreeText,
        PdfiumNativeBridge.FPDF_ANNOT_SQUARE => AnnotationType.Rectangle,
        PdfiumNativeBridge.FPDF_ANNOT_CIRCLE => AnnotationType.Ellipse,
        PdfiumNativeBridge.FPDF_ANNOT_INK => AnnotationType.Ink,
        _ => AnnotationType.Highlight
    };

    private static int MapAnnotationTypeToSubtype(AnnotationType type) => type switch
    {
        AnnotationType.Highlight => PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT,
        AnnotationType.Underline => PdfiumNativeBridge.FPDF_ANNOT_UNDERLINE,
        AnnotationType.StrikeOut => PdfiumNativeBridge.FPDF_ANNOT_STRIKEOUT,
        AnnotationType.Note => PdfiumNativeBridge.FPDF_ANNOT_TEXT,
        AnnotationType.FreeText => PdfiumNativeBridge.FPDF_ANNOT_FREETEXT,
        AnnotationType.Rectangle => PdfiumNativeBridge.FPDF_ANNOT_SQUARE,
        AnnotationType.Ellipse => PdfiumNativeBridge.FPDF_ANNOT_CIRCLE,
        AnnotationType.Ink => PdfiumNativeBridge.FPDF_ANNOT_INK,
        _ => PdfiumNativeBridge.FPDF_ANNOT_HIGHLIGHT
    };

    private static bool ParseHexColor(string hex, out uint r, out uint g, out uint b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex)) return false;
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            uint.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r) &&
            uint.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g) &&
            uint.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b))
        {
            return true;
        }
        return false;
    }
}
