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

public interface IPdfFormService
{
    ValueTask<IReadOnlyList<FormFieldModel>> GetFormFieldsAsync(
        Documents.IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    ValueTask SetFieldValueAsync(
        Documents.IPdfDocument document,
        string fieldName,
        string value,
        CancellationToken cancellationToken = default);

    ValueTask ExportFormDataXfdfAsync(
        Documents.IPdfDocument document,
        string targetXfdfPath,
        CancellationToken cancellationToken = default);

    ValueTask ImportFormDataXfdfAsync(
        Documents.IPdfDocument document,
        string sourceXfdfPath,
        CancellationToken cancellationToken = default);

    ValueTask ResetFormAsync(
        Documents.IPdfDocument document,
        CancellationToken cancellationToken = default);

    ValueTask FlattenFormFieldsAsync(
        Documents.IPdfDocument document,
        string targetPath,
        CancellationToken cancellationToken = default);
}
