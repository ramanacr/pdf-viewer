using Microsoft.AspNetCore.Components;
using PdfViewer.Web.Engine.Annotations;

namespace PdfViewer.Web.Components;

/// <summary>
/// Form filling: an input over each widget of a fillable field (text, check box, radio button,
/// combo box, list box). A value goes to the engine, which writes it with the field's appearance
/// and recalculates the calculated fields (formats and simple calculations as the desktop does,
/// without running the document's JavaScript); the page is then drawn again from the document.
/// </summary>
public sealed partial class Viewer
{
    private IReadOnlyList<ViewerFormWidget> _formWidgets = [];
    private ILookup<int, ViewerFormWidget> _formWidgetsByPage = Array.Empty<ViewerFormWidget>().ToLookup(w => w.Page);

    /// <summary>The values the reader set, by full field name (each list box selection is one value).</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _formValues = new(StringComparer.Ordinal);
    private string? _lastFormField;

    /// <summary>Longest value taken from one input.</summary>
    private const int MaxFieldValueLength = 10000;

    private bool CanFillForms => _config.EnableFormFilling && !_readOnly && _doc?.CanFillForms == true;

    private void SetFieldValue(ViewerFormWidget w, IReadOnlyList<string> values)
    {
        if (!CanFillForms || w.ReadOnly) return;
        values = values.Select(v => v.Length > MaxFieldValueLength ? v[..MaxFieldValueLength] : v).ToList();
        if (_formValues.TryGetValue(w.Name, out var known) ? known.SequenceEqual(values) : values.SequenceEqual(w.Values)) return;
        _formValues[w.Name] = values;
        _lastFormField = w.Name;
        _ = CommitEditsAsync();
    }

    private void OnFieldChange(ViewerFormWidget w, ChangeEventArgs e)
    {
        switch (e.Value)
        {
            case string[] many: SetFieldValue(w, many); break;
            case string one: SetFieldValue(w, [one]); break;
            case bool on: SetFieldValue(w, [on ? w.OnState ?? "Yes" : "Off"]); break;
        }
    }

    private void ToggleField(ViewerFormWidget w) => SetFieldValue(w, [w.ToggleValue]);

    /// <summary>The input's text size in CSS pixels: about the field's height, as Acrobat's auto size.</summary>
    private string FieldFontSize(ViewerFormWidget w) =>
        Px(Math.Clamp((w.Multiline ? 12 : w.Rect.Height * 0.62) * _zoom * CssPerPoint, 6, 40));

    private static string FieldLabel(ViewerFormWidget w) => w.Tooltip.Length > 0 ? w.Tooltip : w.Name;
}
