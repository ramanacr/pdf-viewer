using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfEngine.Vector.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// Form data on the engine's form service (M10): the Fill Form Fields dialog, XFDF and FDF import
/// and export, reset and flatten. Every change is a new revision of the open document in memory,
/// like an on-page fill; nothing touches the file until it is saved. No document script runs.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The current revision, opened for a form edit (a decrypted copy needs no password).</summary>
    private async Task<PdfEditableDocument> OpenCurrentRevisionAsync() =>
        await PdfEditableDocument.OpenAsync(_docService.CurrentBytes ?? throw new InvalidOperationException("No document is open."),
            _docService.CurrentFilePath, _docService.IsDecryptedCopy ? null : _openPassword);

    /// <summary>A form edit is allowed: the document has a form, permits filling, and can take an update.</summary>
    private bool CanEditForm(string action)
    {
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return false;
        if (!Permit(DocumentPermissions.CanFillForms, action)) return false;
        if (FormReadOnlyReason != null)
        {
            StatusText = FormReadOnlyReason;
            ShowAlert(FormReadOnlyReason, "Form", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    /// <summary>Makes <paramref name="next"/> the open revision: the services reload it, the form and pages are read again.</summary>
    private async Task AdoptFormRevisionAsync(byte[] next)
    {
        await _docService.ReloadFromBytesAsync(next);
        await LoadFormAsync();
        _formChangedSinceSave = true;
        HasUnsavedChanges = true;
        foreach (var page in Pages) page.UnloadImage();
        foreach (var thumb in Thumbnails) thumb.UnloadThumbnail();
        _cache.Clear();
        _ = RenderVisiblePagesAsync();
        _ = RenderThumbnailsAsync();
        if (HasSignatures) _ = ValidateSignaturesAsync();
    }

    /// <summary>The Fill Form Fields dialog on the engine: fields of the current revision, edited values applied in memory.</summary>
    private async Task EditFormFieldsNativeAsync()
    {
        StatusText = "Reading form fields...";
        try
        {
            await using var doc = await OpenCurrentRevisionAsync();
            var allFields = PdfFormService.ReadAllFields(doc.Vector);
            if (allFields.Count == 0)
            {
                StatusText = "This document has no form fields.";
                ShowAlert("This document contains no form fields.", "Form Fields", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = Path.GetFileName(_docService.CurrentFilePath);
            var edited = ShowFormFieldsFunc?.Invoke(allFields, name);
            if (edited == null)
            {
                StatusText = $"{allFields.Count} form field(s).";
                return;
            }
            if (!CanEditForm("filling in forms")) return;

            // One value per field (a field with several widgets is listed once per widget).
            var before = allFields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value);
            var changes = edited
                .Where(f => !f.IsReadOnly && !string.IsNullOrEmpty(f.Name)
                            && f.Type is not (PdfEngine.Forms.FormFieldType.PushButton or PdfEngine.Forms.FormFieldType.Signature or PdfEngine.Forms.FormFieldType.Unknown))
                .GroupBy(f => f.Name)
                .Select(g => g.First())
                .Where(f => !before.TryGetValue(f.Name, out var old) || old != (f.Value ?? string.Empty))
                .Select(f => (f.Name, (IReadOnlyList<string>)new[] { f.Value ?? string.Empty }))
                .ToList();
            if (changes.Count == 0)
            {
                StatusText = "No form values changed.";
                return;
            }
            PdfFormService.SetValues(doc, changes, validate: true);
            await AdoptFormRevisionAsync(doc.Bytes);
            StatusText = $"Filled {changes.Count} field(s). Save to keep the values.";
        }
        catch (PdfEngine.Vector.Forms.PdfFieldValidationException ex)
        {
            StatusText = ex.Message;
            ShowAlert(ex.Message, "Form Fields", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException or IOException)
        {
            StatusText = $"Form editing failed: {ex.Message}";
            ShowAlert($"Could not edit the form fields:\n\n{ex.Message}", "Form Fields", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ import and export

    [RelayCommand]
    public async Task ImportFormDataAsync()
    {
        if (!CanEditForm("filling in forms")) return;
        var picker = new OpenFileDialog
        {
            Filter = "Form data (*.xfdf;*.fdf)|*.xfdf;*.fdf|XFDF (*.xfdf)|*.xfdf|FDF (*.fdf)|*.fdf|All Files (*.*)|*.*",
            Title = "Import form data",
        };
        if (picker.ShowDialog() != true) return;
        await ImportFormDataFromAsync(picker.FileName);
    }

    /// <summary>The work of <see cref="ImportFormDataAsync"/> without the dialog (tests call this). Null when nothing was imported.</summary>
    public async Task<PdfFormImportResult?> ImportFormDataFromAsync(string path)
    {
        if (!CanEditForm("filling in forms")) return null;
        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);
            await using var doc = await OpenCurrentRevisionAsync();
            var result = PdfFormService.Import(doc, data);
            if (result.Applied > 0) await AdoptFormRevisionAsync(doc.Bytes);
            string skipped = result.UnknownFields.Count + result.ReadOnlyFields.Count > 0
                ? $" {result.UnknownFields.Count} name(s) not in this form and {result.ReadOnlyFields.Count} read-only field(s) were skipped."
                : string.Empty;
            StatusText = $"Imported {result.Applied} field value(s) from {Path.GetFileName(path)}.{skipped}";
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            StatusText = $"Form data import failed: {ex.Message}";
            ShowAlert($"The form data was not imported:\n\n{ex.Message}", "Import Form Data", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    [RelayCommand]
    public async Task ExportFormDataAsync()
    {
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return;
        var target = new SaveFileDialog
        {
            Filter = "XFDF (*.xfdf)|*.xfdf|FDF (*.fdf)|*.fdf",
            Title = "Export form data",
            FileName = $"{Path.GetFileNameWithoutExtension(_docService.CurrentFilePath)}.xfdf",
        };
        if (target.ShowDialog() != true) return;
        await ExportFormDataToAsync(target.FileName);
    }

    /// <summary>The work of <see cref="ExportFormDataAsync"/> without the dialog: FDF for a .fdf path, else XFDF.</summary>
    public async Task<bool> ExportFormDataToAsync(string path)
    {
        try
        {
            await using var doc = await OpenCurrentRevisionAsync();
            string href = Path.GetFileName(_docService.CurrentFilePath);
            byte[] data = string.Equals(Path.GetExtension(path), ".fdf", StringComparison.OrdinalIgnoreCase)
                ? PdfFormService.ExportFdf(doc.Vector, href)
                : System.Text.Encoding.UTF8.GetBytes(PdfFormService.ExportXfdf(doc.Vector, href));
            await File.WriteAllBytesAsync(path, data);
            StatusText = $"Form data exported to {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"Form data export failed: {ex.Message}";
            ShowAlert($"The form data was not exported:\n\n{ex.Message}", "Export Form Data", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ------------------------------------------------------------------ reset and flatten

    /// <summary>Every fillable field back to its default value (a reset action, without its script).</summary>
    [RelayCommand]
    public async Task ResetFormAsync()
    {
        if (!CanEditForm("filling in forms")) return;
        if (ConfirmFunc != null && !Confirm("Clear every field back to its default value?", "Reset Form")) return;
        try
        {
            await using var doc = await OpenCurrentRevisionAsync();
            var changed = PdfFormService.Reset(doc);
            if (doc.IsModified) await AdoptFormRevisionAsync(doc.Bytes);
            StatusText = $"Reset {changed.Count} field(s) to their default values.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            StatusText = $"Reset failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task FlattenFormAsync()
    {
        if (!IsDocumentLoaded || !Permit(DocumentPermissions.CanModify || DocumentPermissions.CanFillForms, "flattening the form")) return;
        var target = new SaveFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Save flattened form as",
            FileName = $"{Path.GetFileNameWithoutExtension(_docService.CurrentFilePath)}_flattened.pdf",
        };
        if (target.ShowDialog() != true) return;
        if (await FlattenFormToAsync(target.FileName))
            ShowAlert($"The form was flattened into:\n{target.FileName}\n\nYour document is unchanged.", "Flatten Form", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Writes the current revision with every field drawn into its page to a new file (never the original).</summary>
    public async Task<bool> FlattenFormToAsync(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_docService.CurrentFilePath), StringComparison.OrdinalIgnoreCase))
        {
            ShowAlert("Save the flattened form under a new name: the original stays as it is.", "Flatten Form", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try
        {
            await using var doc = await OpenCurrentRevisionAsync();
            byte[] flat = await Task.Run(() => PdfFormService.Flatten(doc.Vector, doc.Bytes));
            await File.WriteAllBytesAsync(path, flat);
            StatusText = $"Flattened form saved to {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StatusText = $"Flatten failed: {ex.Message}";
            ShowAlert($"The form was not flattened:\n\n{ex.Message}", "Flatten Form", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
