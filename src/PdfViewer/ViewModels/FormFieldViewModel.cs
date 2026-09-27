using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Forms;

namespace PdfViewer.ViewModels;

/// <summary>
/// One widget of a form field on a page, as the page's form layer presents it: where it is
/// (normalized to the unrotated page, top-left origin), what kind of editor it needs, and the
/// field's current value. Radio buttons and multi-widget fields have one view model per widget,
/// all sharing the field's name.
/// </summary>
public partial class FormFieldViewModel : ObservableObject
{
    public required string FullName { get; init; }
    public required PdfFormFieldKind Kind { get; init; }
    public required int PageNumber { get; init; }
    /// <summary>Normalized widget rectangle on the unrotated page (0-1, top-left origin).</summary>
    public required Rect Bounds { get; init; }
    /// <summary>The widget's height in points (for the editor's font size).</summary>
    public double HeightPoints { get; init; }
    public double FontSizePoints { get; init; }
    public int Quadding { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsRequired { get; init; }
    public bool IsMultiline { get; init; }
    public bool IsPassword { get; init; }
    public bool IsComb { get; init; }
    public bool IsEditableCombo { get; init; }
    public bool IsMultiSelect { get; init; }
    public int MaxLength { get; init; }
    public string? Tooltip { get; init; }
    /// <summary>This widget's on-state (check box: its "checked" name; radio: its choice).</summary>
    public string? OnState { get; init; }
    public IReadOnlyList<(string Export, string Display)> Options { get; init; } = Array.Empty<(string, string)>();
    /// <summary>Position in the document's tab order.</summary>
    public int TabIndex { get; init; }

    /// <summary>Text, the chosen export value, or the button state name.</summary>
    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _selections = Array.Empty<string>();

    public bool IsOn => Kind switch
    {
        PdfFormFieldKind.CheckBox => Value.Length > 0 && Value != "Off",
        PdfFormFieldKind.RadioGroup => OnState != null && Value == OnState,
        _ => false,
    };

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(IsOn));

    /// <summary>What a screen reader announces for the field.</summary>
    public string AccessibleName => string.IsNullOrWhiteSpace(Tooltip) ? FullName : Tooltip!;

    public static IEnumerable<FormFieldViewModel> FromField(PdfFormField field, Func<int, (double X, double Y, double W, double H)?> pageBox, int tabStart)
    {
        int tab = tabStart;
        foreach (var w in field.Widgets)
        {
            if (w.Hidden || w.PageNumber < 1 || pageBox(w.PageNumber) is not { } box || box.W <= 0 || box.H <= 0)
                continue;
            var da = PdfFormFiller.ParseDA(field.DefaultAppearance);
            yield return new FormFieldViewModel
            {
                FullName = field.FullName,
                Kind = field.Kind,
                PageNumber = w.PageNumber,
                Bounds = new Rect((w.Rect.X - box.X) / box.W, 1 - (w.Rect.Y + w.Rect.Height - box.Y) / box.H, w.Rect.Width / box.W, w.Rect.Height / box.H),
                HeightPoints = w.Rect.Height,
                FontSizePoints = da.Size > 0 ? da.Size : Math.Clamp(w.Rect.Height * 0.65, 6, 14),
                Quadding = field.Quadding,
                IsReadOnly = field.IsReadOnly,
                IsRequired = field.IsRequired,
                IsMultiline = field.IsMultiline,
                IsPassword = field.IsPassword,
                IsComb = field.IsComb,
                IsEditableCombo = field.IsEditableCombo,
                IsMultiSelect = field.IsMultiSelect,
                MaxLength = field.MaxLength,
                Tooltip = field.Tooltip,
                OnState = w.OnState,
                Options = field.Options,
                TabIndex = tab++,
                Value = field.Value,
                Selections = field.Values,
            };
        }
    }
}
