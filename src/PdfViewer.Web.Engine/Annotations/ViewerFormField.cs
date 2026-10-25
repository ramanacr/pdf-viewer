using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>The form field kinds the viewer lets the reader fill.</summary>
public enum ViewerFormKind { Text, CheckBox, Radio, ComboBox, ListBox }

/// <summary>
/// One widget of a fillable form field, where the viewer puts its input: <see cref="Rect"/> on the
/// shown page (points from its top-left corner). <see cref="OnState"/> is the value that turns a
/// check box or radio button on; <see cref="IsOn"/> whether this widget shows it now.
/// </summary>
public sealed record ViewerFormWidget(
    string Name, ViewerFormKind Kind, int Page, ViewerRect Rect, string Value, IReadOnlyList<string> Values,
    IReadOnlyList<(string Export, string Display)> Options, string? OnState, bool IsOn, bool ReadOnly,
    bool Multiline, bool Password, int MaxLength, bool MultiSelect, bool Editable, string Tooltip)
{
    /// <summary>The value a click on a check box or radio button sets.</summary>
    public string ToggleValue => Kind == ViewerFormKind.Radio ? OnState ?? "Off" : IsOn ? "Off" : OnState ?? "Yes";

    /// <summary>The widgets of every fillable field (not push buttons, signatures or hidden widgets), page by page.</summary>
    public static List<ViewerFormWidget> ReadAll(PdfVectorDocument document, IReadOnlyList<ViewerPageGeometry> pages)
    {
        var result = new List<ViewerFormWidget>();
        PdfAcroForm? form;
        try
        {
            form = PdfAcroForm.Read(document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return result; // a damaged form is shown as drawn, not filled
        }
        if (form == null) return result;
        foreach (var field in form.Fields)
        {
            ViewerFormKind? kind = field.Kind switch
            {
                PdfFormFieldKind.Text => ViewerFormKind.Text,
                PdfFormFieldKind.CheckBox => ViewerFormKind.CheckBox,
                PdfFormFieldKind.RadioGroup => ViewerFormKind.Radio,
                PdfFormFieldKind.ComboBox => ViewerFormKind.ComboBox,
                PdfFormFieldKind.ListBox => ViewerFormKind.ListBox,
                _ => null,
            };
            if (kind == null) continue;
            foreach (var w in field.Widgets)
            {
                if (w.Hidden || w.PageNumber < 1 || w.PageNumber > pages.Count || w.Rect.IsEmpty) continue;
                var geometry = pages[w.PageNumber - 1];
                var rect = geometry.ToShown(new ViewerBox(w.Rect.Left, w.Rect.Top, w.Rect.Right, w.Rect.Bottom));
                string state = string.IsNullOrEmpty(w.AppearanceState)
                    ? w.OnState != null && field.Value == w.OnState ? w.OnState : "Off"
                    : w.AppearanceState;
                result.Add(new ViewerFormWidget(field.FullName, kind.Value, w.PageNumber, rect, field.Value,
                    field.Kind == PdfFormFieldKind.ListBox ? field.Values : [field.Value], field.Options, w.OnState,
                    !string.Equals(state, "Off", StringComparison.Ordinal), field.IsReadOnly, field.IsMultiline, field.IsPassword,
                    field.MaxLength, field.IsMultiSelect, field.IsEditableCombo, field.Tooltip ?? ""));
            }
        }
        return result;
    }
}
