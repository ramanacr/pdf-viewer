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
                    model.Opacity = a > 0 ? a / 255.0 : 1.0;
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
            return ExportXfdfInternalAsync(targetPath, annotations, cancellationToken);
        }

        lock (pdfiumDoc.SyncLock)
        {
            // Create a duplicate document in memory
            byte[] fileBytes = File.ReadAllBytes(pdfiumDoc.FilePath);
            IntPtr unmanagedBuf = Marshal.AllocHGlobal(fileBytes.Length);
            Marshal.Copy(fileBytes, 0, unmanagedBuf, fileBytes.Length);

            using var editDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, null);
            if (editDoc == null || editDoc.IsInvalid)
            {
                Marshal.FreeHGlobal(unmanagedBuf);
                throw new PdfSaveException("Failed to open working copy for annotation saving.", targetPath);
            }

            try
            {
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
                var fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        byte[] buffer = new byte[size];
                        Marshal.Copy(pData, buffer, 0, (int)size);
                        outStream.Write(buffer, 0, (int)size);
                        return 1;
                    }
                };

                int saveResult = PdfiumNativeBridge.FPDF_SaveAsCopy(editDoc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
                if (saveResult == 0)
                {
                    throw new PdfSaveException("Native FPDF_SaveAsCopy failed.", targetPath);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedBuf);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static async ValueTask ExportXfdfInternalAsync(string targetPath, IReadOnlyList<AnnotationModel> annotations, CancellationToken ct)
    {
        var xfdf = new XElement("xfdf",
            new XAttribute(XNamespace.Xmlns + "xfdf", "http://ns.adobe.com/xfdf/"),
            new XElement("annots",
                annotations.Select(a => new XElement(a.Type.ToString().ToLowerInvariant(),
                    new XAttribute("page", a.PageNumber - 1),
                    new XAttribute("rect", $"{a.X},{a.Y},{a.X + a.Width},{a.Y + a.Height}"),
                    new XAttribute("color", a.Color),
                    new XAttribute("opacity", a.Opacity),
                    new XAttribute("title", a.Author),
                    new XElement("contents", a.Contents)
                ))
            )
        );

        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await xfdf.SaveAsync(fs, SaveOptions.None, ct);
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
