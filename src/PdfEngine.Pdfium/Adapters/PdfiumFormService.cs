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
                    // Derive the real field type from the widget's /FT entry instead of
                    // hardcoding TextField, which made every checkbox, radio, combo and
                    // signature field surface as a text box and drove the wrong UI editor.
                    var field = new FormFieldModel
                    {
                        PageNumber = pageNumber,
                        Type = ReadFieldType(annot)
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
        // Writing field values requires a PDFium form-fill environment
        // (FPDFDOC_InitFormFillEnvironment), which this adapter does not create. Throw
        // rather than returning CompletedTask: silently discarding the value made callers
        // - notably ImportFormDataXfdfAsync - report a successful import that wrote nothing.
        throw new NotSupportedException(
            "Setting form field values is not supported by the PDFium adapter yet: it requires a " +
            "form-fill environment. Field values can currently be read and exported, not written.");
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
        // Same limitation as SetFieldValueAsync - resetting requires a form-fill
        // environment. Reporting success while doing nothing is worse than failing.
        throw new NotSupportedException(
            "Resetting form fields is not supported by the PDFium adapter yet: it requires a " +
            "form-fill environment.");
    }

    /// <summary>
    /// Reads the widget's field type from its /FT entry ("Tx", "Btn", "Ch", "Sig").
    /// Note /FT may be inherited from a /Parent field, in which case it is absent on the
    /// widget itself and we fall back to Unknown.
    /// </summary>
    private static FormFieldType ReadFieldType(SafeAnnotHandle annot)
    {
        uint len = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "FT", null, 0);
        if (len == 0) return FormFieldType.Unknown;

        byte[] buf = new byte[len];
        PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, "FT", buf, len);
        string ft = PdfiumNativeBridge.Utf16BytesToString(buf, (int)len).Trim();

        return ft switch
        {
            "Tx" => FormFieldType.TextField,
            "Ch" => FormFieldType.ComboBox,
            "Sig" => FormFieldType.Signature,
            // /Btn covers push buttons, checkboxes and radio buttons; distinguishing them
            // needs the /Ff flag bits, which are not exposed as a string value.
            "Btn" => FormFieldType.CheckBox,
            _ => FormFieldType.Unknown
        };
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

            // editDoc lives INSIDE the try so FPDF_CloseDocument runs before the finally
            // frees unmanagedBuf: FPDF_LoadMemDocument parses lazily out of that buffer for
            // the document's whole lifetime, so freeing first corrupts the heap on close.
            SafeDocumentHandle? editDoc = null;
            try
            {
                editDoc = PdfiumNativeBridge.FPDF_LoadMemDocument(unmanagedBuf, fileBytes.Length, null);
                // Unlike its sibling services this had no validity check at all, so a
                // truncated/replaced/encrypted file meant FPDF_LoadPage and FPDF_SaveAsCopy
                // ran against a null document and the real PDFium error was lost.
                if (editDoc == null || editDoc.IsInvalid)
                {
                    throw new PdfEngine.Exceptions.PdfSaveException(
                        $"Failed to open working copy for form flattening (PDFium error {PdfiumNativeBridge.FPDF_GetLastError()}).",
                        targetPath);
                }

                for (int p = 0; p < pdfiumDoc.PageCount; p++)
                {
                    using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(editDoc, p);
                    if (pageHandle != null && !pageHandle.IsInvalid)
                    {
                        PdfiumNativeBridge.FPDFPage_Flatten(pageHandle, PdfiumNativeBridge.FLAT_NORMALDISPLAY);
                    }
                }

                using var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
                Exception? writeFailure = null;
                var fileWrite = new FPDF_FILEWRITE
                {
                    version = 1,
                    WriteBlock = (pThis, pData, size) =>
                    {
                        // Never unwind a managed exception through PDFium's C++ frames.
                        try
                        {
                            byte[] buffer = new byte[size];
                            System.Runtime.InteropServices.Marshal.Copy(pData, buffer, 0, (int)size);
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

                int res = PdfiumNativeBridge.FPDF_SaveAsCopy(editDoc, ref fileWrite, PdfiumNativeBridge.FPDF_NO_INCREMENTAL);
                GC.KeepAlive(fileWrite);

                if (writeFailure != null)
                    throw new PdfEngine.Exceptions.PdfSaveException("Failed writing flattened document to disk.", writeFailure, targetPath);

                if (res == 0)
                {
                    throw new PdfEngine.Exceptions.PdfSaveException("Failed to flatten form fields.", targetPath);
                }
            }
            finally
            {
                editDoc?.Dispose();
                System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanagedBuf);
            }
        }

        return ValueTask.CompletedTask;
    }
}
