using System.Xml.Linq;
using PdfEngine.Documents;
using PdfEngine.Forms;
using PdfEngine.Pdfium.Native;

namespace PdfEngine.Pdfium.Adapters;

/// <summary>
/// Native AcroForm field discovery, inspection, and XFDF import/export service.
/// </summary>
public sealed class PdfiumFormService : IPdfFormService
{
    public ValueTask<IReadOnlyList<FormFieldModel>> GetFormFieldsAsync(
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
            var fields = new List<FormFieldModel>();
            using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, pageNumber - 1);
            if (pageHandle == null || pageHandle.IsInvalid) return ValueTask.FromResult<IReadOnlyList<FormFieldModel>>(fields);

            float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
            float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

            int annotCount = PdfiumNativeBridge.FPDFPage_GetAnnotCount(pageHandle);
            for (int i = 0; i < annotCount; i++)
            {
                using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(pageHandle, i);
                if (annot == null || annot.IsInvalid) continue;

                int subtype = PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot);
                if (subtype == PdfiumNativeBridge.FPDF_ANNOT_WIDGET)
                {
                    var field = new FormFieldModel
                    {
                        PageNumber = pageNumber,
                        Type = FormFieldType.TextField
                    };

                    if (PdfiumNativeBridge.FPDFAnnot_GetRect(annot, out var rect) != 0 && pageW > 0 && pageH > 0)
                    {
                        field.Bounds = new Geometry.PdfRect(
                            Math.Max(0.0, Math.Min(1.0, rect.left / pageW)),
                            Math.Max(0.0, Math.Min(1.0, 1.0 - (rect.top / pageH))),
                            Math.Max(0.0, Math.Min(1.0, (rect.right - rect.left) / pageW)),
                            Math.Max(0.0, Math.Min(1.0, (rect.top - rect.bottom) / pageH))
                        );
                    }

                    uint nameLen = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "T", null, 0);
                    if (nameLen > 0)
                    {
                        byte[] nBuf = new byte[nameLen];
                        PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "T", nBuf, nameLen);
                        field.Name = PdfiumNativeBridge.Utf16BytesToString(nBuf, (int)nameLen);
                    }

                    uint valLen = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "V", null, 0);
                    if (valLen > 0)
                    {
                        byte[] vBuf = new byte[valLen];
                        PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "V", vBuf, valLen);
                        field.Value = PdfiumNativeBridge.Utf16BytesToString(vBuf, (int)valLen);
                    }

                    fields.Add(field);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<FormFieldModel>>(fields);
        }
    }

    public ValueTask SetFieldValueAsync(
        IPdfDocument document,
        string fieldName,
        string value,
        CancellationToken cancellationToken = default)
    {
        // Field setting will be enhanced with form fill environment in Phase 3
        return ValueTask.CompletedTask;
    }

    public async ValueTask ExportFormDataXfdfAsync(
        IPdfDocument document,
        string targetXfdfPath,
        CancellationToken cancellationToken = default)
    {
        var allFields = new List<FormFieldModel>();
        for (int p = 1; p <= document.PageCount; p++)
        {
            var fields = await GetFormFieldsAsync(document, p, cancellationToken);
            allFields.AddRange(fields);
        }

        var xfdf = new XElement("xfdf",
            new XAttribute(XNamespace.Xmlns + "xfdf", "http://ns.adobe.com/xfdf/"),
            new XElement("fields",
                allFields.Where(f => !string.IsNullOrEmpty(f.Name)).Select(f => new XElement("field",
                    new XAttribute("name", f.Name),
                    new XElement("value", f.Value)
                ))
            )
        );

        using var fs = new FileStream(targetXfdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await xfdf.SaveAsync(fs, SaveOptions.None, cancellationToken);
    }

    public async ValueTask ImportFormDataXfdfAsync(
        IPdfDocument document,
        string sourceXfdfPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceXfdfPath))
            throw new FileNotFoundException($"XFDF file not found: {sourceXfdfPath}", sourceXfdfPath);

        cancellationToken.ThrowIfCancellationRequested();

        using var stream = File.OpenRead(sourceXfdfPath);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var fields = doc.Descendants().Where(e => e.Name.LocalName == "field");

        foreach (var field in fields)
        {
            string? name = field.Attribute("name")?.Value;
            string? val = field.Element(field.Name.Namespace + "value")?.Value;
            if (!string.IsNullOrEmpty(name) && val != null)
            {
                await SetFieldValueAsync(document, name, val, cancellationToken);
            }
        }
    }

    public ValueTask ResetFormAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask FlattenFormFieldsAsync(
        IPdfDocument document,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        lock (pdfiumDoc.SyncLock)
        {
            byte[] fileBytes = File.ReadAllBytes(pdfiumDoc.FilePath);
            IntPtr unmanagedBuf = System.Runtime.InteropServices.Marshal.AllocHGlobal(fileBytes.Length);
            System.Runtime.InteropServices.Marshal.Copy(fileBytes, 0, unmanagedBuf, fileBytes.Length);

            using var editDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, null);
            try
            {
                for (int p = 0; p < pdfiumDoc.PageCount; p++)
                {
                    using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(editDoc, p);
                    if (pageHandle != null && !pageHandle.IsInvalid)
                    {
                        PdfiumNativeBridge.FPDFPage_Flatten(pageHandle, PdfiumNativeBridge.FLAT_NORMALDISPLAY);
                    }
                }

                using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        byte[] buffer = new byte[size];
                        System.Runtime.InteropServices.Marshal.Copy(pData, buffer, 0, (int)size);
                        outStream.Write(buffer, 0, (int)size);
                        return 1;
                    }
                };

                int res = PdfiumNativeBridge.FPDF_SaveAsCopy(editDoc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
                if (res == 0)
                {
                    throw new PdfEngine.Exceptions.PdfSaveException("Failed to flatten form fields.", targetPath);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedBuf);
            }
        }

        return ValueTask.CompletedTask;
    }
}
