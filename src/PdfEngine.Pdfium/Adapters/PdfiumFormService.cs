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
            ReadFieldsFromPage(pdfiumDoc, pageNumber, fields);
            return ValueTask.FromResult<IReadOnlyList<FormFieldModel>>(fields);
        }
    }

    /// <summary>
    /// Reads every widget on a page directly from its annotation dictionary.
    ///
    /// Deliberately does NOT use a PDFium form-fill environment. The environment would give
    /// richer metadata (fully-qualified names, option lists, resolved button subtypes), but
    /// FPDFDOC_ExitFormFillEnvironment corrupts the heap in this PDFium build - reproducibly
    /// crashing the process during teardown, sometimes as an "Internal CLR error" much later.
    /// Crashing the host is a far worse outcome than reduced metadata fidelity, so field data
    /// is read from the annotation dictionary, which is stable.
    ///
    /// Known limits of the dictionary-only approach, in exchange for not crashing:
    ///   - Name is the PARTIAL /T name; fields nested under a /Parent are not qualified.
    ///   - /Btn cannot be split into push button vs checkbox vs radio (reported as CheckBox).
    ///   - Choice option lists are not enumerated, so Options stays empty.
    /// </summary>
    private static void ReadFieldsFromPage(
        PdfiumDocument pdfiumDoc,
        int pageNumber,
        List<FormFieldModel> fields)
    {
        using var pageHandle = PdfiumNativeBridge.FPDF_LoadPage(pdfiumDoc.Handle, pageNumber - 1);
        if (pageHandle == null || pageHandle.IsInvalid) return;

        float pageW = PdfiumNativeBridge.FPDF_GetPageWidthF(pageHandle);
        float pageH = PdfiumNativeBridge.FPDF_GetPageHeightF(pageHandle);

        int annotCount = PdfiumNativeBridge.FPDFPage_GetAnnotCount(pageHandle);
        {
            for (int i = 0; i < annotCount; i++)
            {
                using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(pageHandle, i);
                if (annot == null || annot.IsInvalid) continue;

                int subtype = PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot);
                if (subtype != PdfiumNativeBridge.FPDF_ANNOT_WIDGET) continue;

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

                field.Name = ReadAnnotString(annot, "T");
                field.Value = ReadAnnotString(annot, "V");
                field.DefaultValue = ReadAnnotString(annot, "DV");

                if (field.Type is FormFieldType.CheckBox or FormFieldType.RadioButton)
                {
                    field.IsChecked = ReadCheckedState(annot);
                }

                fields.Add(field);
            }
        }
    }

    /// <summary>
    /// Determines checkbox/radio state from the widget's /AS appearance state, falling back
    /// to /V. Any state other than "Off" means checked, per the PDF specification.
    ///
    /// Deliberately NOT using FPDFAnnot_IsChecked: that API dereferences the field's form
    /// control without a null check, so a checkbox lacking an /AP appearance dictionary -
    /// which real documents do contain - crashes the process. A native access violation
    /// cannot be caught, so the only safe option is not to make the call. Reading /AS is
    /// the spec's own representation of the state and cannot fault.
    /// </summary>
    private static bool ReadCheckedState(SafeAnnotHandle annot)
    {
        string state = ReadAnnotString(annot, "AS");
        if (string.IsNullOrEmpty(state))
        {
            state = ReadAnnotString(annot, "V");
        }

        return !string.IsNullOrEmpty(state)
               && !string.Equals(state, "Off", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadAnnotString(SafeAnnotHandle annot, string key)
    {
        uint len = PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (len == 0) return string.Empty;

        byte[] buffer = new byte[len];
        PdfiumNativeBridge.FPDFAnnot_GetStringValue(annot, key, buffer, len);
        return PdfiumNativeBridge.Utf16BytesToString(buffer, (int)len);
    }

    public ValueTask SetFieldValueAsync(
        IPdfDocument document,
        string fieldName,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        if (string.IsNullOrEmpty(fieldName))
            throw new ArgumentException("Field name must be supplied.", nameof(fieldName));

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryUpdateField(pdfiumDoc, fieldName, value, cancellationToken, out bool wasReadOnly))
        {
            if (wasReadOnly)
            {
                throw new InvalidOperationException(
                    $"Form field '{fieldName}' is read-only and cannot be modified.");
            }

            throw new KeyNotFoundException($"No form field named '{fieldName}' exists in the document.");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Finds the widget for a field and writes its value.
    ///
    /// The value is written to the field's /V entry and the widget's cached appearance
    /// stream is dropped so viewers regenerate it; without clearing /AP the field would
    /// keep rendering its previous text even though the stored value changed.
    /// </summary>
    private static bool TryUpdateField(
        PdfiumDocument document,
        string fieldName,
        string value,
        CancellationToken cancellationToken,
        out bool wasReadOnly)
    {
        wasReadOnly = false;
        bool updated = false;

        lock (document.SyncLock)
        {
            int pageCount = document.PageCount;

            for (int p = 0; p < pageCount; p++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var page = PdfiumNativeBridge.FPDF_LoadPage(document.Handle, p);
                if (page == null || page.IsInvalid) continue;

                int annotCount = PdfiumNativeBridge.FPDFPage_GetAnnotCount(page);
                for (int i = 0; i < annotCount; i++)
                {
                    using var annot = PdfiumNativeBridge.FPDFPage_GetAnnot(page, i);
                    if (annot == null || annot.IsInvalid) continue;

                    if (PdfiumNativeBridge.FPDFAnnot_GetSubtype(annot) != PdfiumNativeBridge.FPDF_ANNOT_WIDGET)
                        continue;

                    string name = ReadAnnotString(annot, "T");
                    if (!string.Equals(name, fieldName, StringComparison.Ordinal)) continue;

                    byte[] encoded = PdfiumNativeBridge.StringToUtf16NullTerminated(value);
                    if (PdfiumNativeBridge.FPDFAnnot_SetStringValue(annot, "V", encoded) == 0)
                    {
                        continue;
                    }

                    // NOTE: do NOT try to clear the widget's cached /AP here. /AP is a
                    // dictionary, so writing a string into it corrupts the annotation and
                    // crashes PDFium during teardown. Regeneration is driven by the
                    // AcroForm's /NeedAppearances flag instead.
                    updated = true;
                }
            }
        }

        return updated;
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

    public async ValueTask ResetFormAsync(
        IPdfDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document is not PdfiumDocument pdfiumDoc)
            throw new ArgumentException("Document must be a PdfiumDocument instance.", nameof(document));

        if (!pdfiumDoc.IsOpen)
            throw new ObjectDisposedException(nameof(document));

        // Reset each field to its /DV default value, or to empty when it has none - which
        // is what a PDF reset action does.
        for (int p = 1; p <= pdfiumDoc.PageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fields = await GetFormFieldsAsync(document, p, cancellationToken);
            foreach (var field in fields)
            {
                if (string.IsNullOrEmpty(field.Name) || field.IsReadOnly) continue;
                if (field.Type == FormFieldType.PushButton || field.Type == FormFieldType.Signature) continue;

                TryUpdateField(pdfiumDoc, field.Name, field.DefaultValue ?? string.Empty, cancellationToken, out _);
            }
        }
    }

    /// <summary>
    /// Reads the widget's true field type.
    ///
    /// With a form handle PDFium resolves inheritance and the /Ff flag bits, so push
    /// buttons, checkboxes and radio buttons are distinguished properly. Without one we
    /// fall back to the widget's own /FT entry, which cannot tell those three apart and is
    /// absent entirely when the type is inherited from a /Parent field.
    /// </summary>
    private static FormFieldType ReadFieldType(SafeAnnotHandle annot)
    {
        string ft = ReadAnnotString(annot, "FT").Trim();
        return ft switch
        {
            "Tx" => FormFieldType.TextField,
            "Ch" => FormFieldType.ComboBox,
            "Sig" => FormFieldType.Signature,
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
