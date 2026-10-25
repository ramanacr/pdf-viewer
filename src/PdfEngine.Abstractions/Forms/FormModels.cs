using PdfEngine.Geometry;

namespace PdfEngine.Forms;

public enum FormFieldType
{
    Unknown,
    PushButton,
    CheckBox,
    RadioButton,
    ComboBox,
    ListBox,
    TextField,
    Signature
}

public class FormFieldModel
{
    public string Name { get; set; } = string.Empty;
    public string AlternateName { get; set; } = string.Empty;
    public FormFieldType Type { get; set; } = FormFieldType.TextField;
    public int PageNumber { get; set; } = 1;
    public PdfRect Bounds { get; set; }
    public string Value { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }
    public bool IsRequired { get; set; }
    public bool IsChecked { get; set; }
    public List<string> Options { get; set; } = new();
    public int SelectedIndex { get; set; } = -1;
    public int MaxLength { get; set; }
    public bool IsMultiLine { get; set; }
    public bool IsPassword { get; set; }
}

/// <summary>Reads, fills, exports, imports, resets and flattens interactive form fields (AcroForm).</summary>
public interface IPdfFormService
{
    /// <summary>Gets the form fields that have a widget on a page.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="pageNumber">The page number, starting at 1.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask<IReadOnlyList<FormFieldModel>> GetFormFieldsAsync(
        Documents.IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the value of a field in the open document.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="fieldName">The fully qualified field name.</param>
    /// <param name="value">The new value.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask SetFieldValueAsync(
        Documents.IPdfDocument document,
        string fieldName,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>Writes the field values to an XFDF file.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="targetXfdfPath">The XFDF file to write.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ExportFormDataXfdfAsync(
        Documents.IPdfDocument document,
        string targetXfdfPath,
        CancellationToken cancellationToken = default);

    /// <summary>Reads field values from an XFDF file into the open document.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="sourceXfdfPath">The XFDF file to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ImportFormDataXfdfAsync(
        Documents.IPdfDocument document,
        string sourceXfdfPath,
        CancellationToken cancellationToken = default);

    /// <summary>Sets every field back to its default value.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask ResetFormAsync(
        Documents.IPdfDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a copy of the document with the fields drawn into the page content, so that they are no longer editable.</summary>
    /// <param name="document">An open document of this engine.</param>
    /// <param name="targetPath">The file to write.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    ValueTask FlattenFormFieldsAsync(
        Documents.IPdfDocument document,
        string targetPath,
        CancellationToken cancellationToken = default);
}
