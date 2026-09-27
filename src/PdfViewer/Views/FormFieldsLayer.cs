using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PdfEngine.Vector.Forms;
using PdfViewer.ViewModels;

namespace PdfViewer.Views;

/// <summary>
/// A page's form fields as real WPF editors laid over the page (in its unrotated space): text
/// boxes, password boxes, check boxes, radio buttons, combo and list boxes — native caret, IME,
/// clipboard, undo and UI Automation, and Tab through the document's fields. An editor shows its
/// own text only while it has focus; otherwise it is transparent and the page shows the field's
/// appearance, which each commit regenerates in the document, so what is saved is what is seen.
/// </summary>
internal sealed class FormFieldsLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(FormFieldsLayer), new PropertyMetadata(null, (d, e) => ((FormFieldsLayer)d).OnPageChanged(e)));

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static readonly Brush FieldTint = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0x3C, 0x8D, 0xFF)));
    private static readonly Brush RequiredBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25)));
    private static readonly Brush FocusBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)));
    private static readonly Brush EditingBackground = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));

    private readonly Dictionary<FormFieldViewModel, (Border Frame, FrameworkElement Editor)> _elements = new();
    private MainViewModel? _owner;

    public FormFieldsLayer()
    {
        Background = null; // clicks between fields go to the page beneath
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private void OnPageChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PageViewModel old)
        {
            old.FormFields.CollectionChanged -= OnFieldsChanged;
            old.PropertyChanged -= OnPagePropertyChanged;
        }
        if (_owner != null) _owner.PropertyChanged -= OnOwnerPropertyChanged;
        _owner = null;
        if (e.NewValue is PageViewModel page)
        {
            page.FormFields.CollectionChanged += OnFieldsChanged;
            page.PropertyChanged += OnPagePropertyChanged;
            _owner = page.Owner;
            if (_owner != null) _owner.PropertyChanged += OnOwnerPropertyChanged;
        }
        Rebuild();
    }

    private void OnFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.UnrotatedDisplayWidth) or nameof(PageViewModel.UnrotatedDisplayHeight) or nameof(PageViewModel.DisplayScale))
            Layout();
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HighlightFormFields))
            foreach (var (field, (frame, _)) in _elements) StyleFrame(frame, field, focused: false);
    }

    private void Rebuild()
    {
        Children.Clear();
        _elements.Clear();
        if (Page is not { } page) return;
        foreach (var field in page.FormFields)
        {
            var editor = CreateEditor(field);
            var frame = new Border { Child = editor, SnapsToDevicePixels = true };
            StyleFrame(frame, field, focused: false);
            editor.GotKeyboardFocus += (_, _) => StyleFrame(frame, field, focused: true);
            editor.LostKeyboardFocus += (_, _) => { if (!editor.IsKeyboardFocusWithin) StyleFrame(frame, field, focused: false); };
            AutomationProperties.SetName(editor, field.AccessibleName);
            AutomationProperties.SetHelpText(editor, string.Join(", ", new[]
            {
                field.IsRequired ? "required" : null,
                field.IsReadOnly ? "read-only" : null,
            }.Where(s => s != null)));
            if (!string.IsNullOrWhiteSpace(field.Tooltip)) editor.ToolTip = field.Tooltip;
            KeyboardNavigation.SetTabIndex(editor, field.TabIndex);
            Children.Add(frame);
            _elements[field] = (frame, editor);
        }
        Layout();
    }

    private void StyleFrame(Border frame, FormFieldViewModel field, bool focused)
    {
        bool highlight = _owner?.HighlightFormFields ?? true;
        frame.Background = highlight && !field.IsReadOnly ? FieldTint : Brushes.Transparent;
        frame.BorderBrush = focused ? FocusBorder : field.IsRequired && highlight ? RequiredBorder : null;
        frame.BorderThickness = focused || (field.IsRequired && highlight) ? new Thickness(1) : new Thickness(0);
    }

    private void Layout()
    {
        if (Page is not { } page) return;
        double w = page.UnrotatedDisplayWidth, h = page.UnrotatedDisplayHeight, scale = page.DisplayScale;
        foreach (var (field, (frame, editor)) in _elements)
        {
            SetLeft(frame, field.Bounds.X * w);
            SetTop(frame, field.Bounds.Y * h);
            frame.Width = Math.Max(4, field.Bounds.Width * w);
            frame.Height = Math.Max(4, field.Bounds.Height * h);
            double size = Math.Max(4, field.FontSizePoints * scale);
            switch (editor)
            {
                case Control c: c.FontSize = size; break;
            }
        }
    }

    // ------------------------------------------------------------------ editors

    private FrameworkElement CreateEditor(FormFieldViewModel field) => field.Kind switch
    {
        PdfFormFieldKind.Text when field.IsPassword => PasswordEditor(field),
        PdfFormFieldKind.Text => TextEditor(field),
        PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioGroup => ButtonEditor(field),
        PdfFormFieldKind.ComboBox => ComboEditor(field),
        PdfFormFieldKind.ListBox => ListEditor(field),
        _ => new Border(),
    };

    private void Commit(FormFieldViewModel field, PdfFieldChange change)
    {
        if (_owner is { } owner)
            _ = owner.CommitFormFieldAsync(field, change);
    }

    private FrameworkElement TextEditor(FormFieldViewModel field)
    {
        var box = new TextBox
        {
            Text = field.Value,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent, // the page's appearance shows the value
            CaretBrush = Brushes.Black,
            Padding = new Thickness(2, 0, 2, 0),
            IsReadOnly = field.IsReadOnly,
            AcceptsReturn = field.IsMultiline,
            AcceptsTab = false,
            TextWrapping = field.IsMultiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.IsMultiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            TextAlignment = field.Quadding switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
            FontFamily = new FontFamily("Arial"),
            MaxLength = field.MaxLength > 0 ? field.MaxLength : 0,
            FocusVisualStyle = null,
        };
        if (field.IsComb && field.MaxLength > 0)
            box.CharacterCasing = CharacterCasing.Normal;
        box.GotKeyboardFocus += (_, _) =>
        {
            box.Text = field.Value;
            box.Background = EditingBackground;
            box.Foreground = Brushes.Black;
            box.SelectAll();
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.Background = Brushes.Transparent;
            box.Foreground = Brushes.Transparent;
            if (!field.IsReadOnly && box.Text != field.Value)
                Commit(field, new PdfFieldChange(field.FullName, Text: box.Text));
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                box.Text = field.Value; // abandon the edit
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !field.IsMultiline)
            {
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        };
        return box;
    }

    private FrameworkElement PasswordEditor(FormFieldViewModel field)
    {
        var box = new PasswordBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            Padding = new Thickness(2, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = field.MaxLength > 0 ? field.MaxLength : 0,
            IsEnabled = !field.IsReadOnly,
            FocusVisualStyle = null,
        };
        string original = field.Value;
        box.GotKeyboardFocus += (_, _) => { box.Background = EditingBackground; box.Foreground = Brushes.Black; };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.Background = Brushes.Transparent;
            box.Foreground = Brushes.Transparent;
            if (box.Password.Length > 0 && box.Password != original)
                Commit(field, new PdfFieldChange(field.FullName, Text: box.Password));
        };
        return box;
    }

    private FrameworkElement ButtonEditor(FormFieldViewModel field)
    {
        // A transparent toggle: the page draws the box and its check; this takes the click,
        // Space, focus and the automation role.
        var toggle = new ToggleButton
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsChecked = field.IsOn,
            IsEnabled = !field.IsReadOnly,
            Cursor = Cursors.Hand,
            Template = TransparentTemplate(),
        };
        field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FormFieldViewModel.IsOn)) toggle.IsChecked = field.IsOn; };
        toggle.Click += (_, _) =>
        {
            if (field.Kind == PdfFormFieldKind.CheckBox)
                Commit(field, new PdfFieldChange(field.FullName, Checked: !field.IsOn));
            else if (!field.IsOn && field.OnState != null)
                Commit(field, new PdfFieldChange(field.FullName, Choice: field.OnState));
            toggle.IsChecked = field.IsOn; // until the commit updates it
        };
        return toggle;
    }

    private static ControlTemplate TransparentTemplate()
    {
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = border;
        return template;
    }

    private FrameworkElement ComboEditor(FormFieldViewModel field)
    {
        var combo = new ComboBox
        {
            IsEditable = field.IsEditableCombo,
            IsEnabled = !field.IsReadOnly,
            Opacity = 0, // shown on hover and focus; the page draws the chosen value otherwise
            FontFamily = new FontFamily("Arial"),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var (_, display) in field.Options) combo.Items.Add(display);
        combo.SelectedIndex = field.Options.ToList().FindIndex(o => o.Export == field.Value);
        if (field.IsEditableCombo && combo.SelectedIndex < 0) combo.Text = field.Value;
        void Show(bool visible) => combo.Opacity = visible || combo.IsDropDownOpen || combo.IsKeyboardFocusWithin ? 1 : 0;
        combo.MouseEnter += (_, _) => Show(true);
        combo.MouseLeave += (_, _) => Show(false);
        combo.GotKeyboardFocus += (_, _) => Show(true);
        combo.LostKeyboardFocus += (_, _) =>
        {
            Show(false);
            if (field.IsEditableCombo && !combo.IsKeyboardFocusWithin)
            {
                string text = combo.Text ?? string.Empty;
                string export = field.Options.FirstOrDefault(o => o.Display == text).Export ?? text;
                if (export != field.Value) Commit(field, new PdfFieldChange(field.FullName, Choice: export));
            }
        };
        combo.DropDownClosed += (_, _) =>
        {
            Show(false);
            int i = combo.SelectedIndex;
            if (i >= 0 && i < field.Options.Count && field.Options[i].Export != field.Value)
                Commit(field, new PdfFieldChange(field.FullName, Choice: field.Options[i].Export));
        };
        return combo;
    }

    private FrameworkElement ListEditor(FormFieldViewModel field)
    {
        var list = new ListBox
        {
            SelectionMode = field.IsMultiSelect ? SelectionMode.Extended : SelectionMode.Single,
            IsEnabled = !field.IsReadOnly,
            Opacity = 0,
            FontFamily = new FontFamily("Arial"),
            BorderThickness = new Thickness(0),
        };
        foreach (var (_, display) in field.Options) list.Items.Add(display);
        foreach (var s in field.Selections)
        {
            int i = field.Options.ToList().FindIndex(o => o.Export == s);
            if (i >= 0) list.SelectedItems.Add(list.Items[i]);
        }
        list.MouseEnter += (_, _) => list.Opacity = 1;
        list.MouseLeave += (_, _) => list.Opacity = list.IsKeyboardFocusWithin ? 1 : 0;
        list.GotKeyboardFocus += (_, _) => list.Opacity = 1;
        list.LostKeyboardFocus += (_, _) =>
        {
            if (list.IsKeyboardFocusWithin) return;
            list.Opacity = 0;
            var chosen = list.SelectedItems.Cast<string>()
                .Select(d => field.Options.First(o => o.Display == d).Export).ToList();
            if (!chosen.SequenceEqual(field.Selections))
                Commit(field, new PdfFieldChange(field.FullName, Selections: chosen));
        };
        return list;
    }
}
