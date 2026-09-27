using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Security;

namespace PdfViewer.ViewModels;

/// <summary>
/// What the Protect with Password dialog collected. The passwords live only until the protected
/// copy is written: <see cref="ClearPasswords"/> drops them, and they are never logged or stored.
/// </summary>
public sealed class ProtectDocumentOptions
{
    public string? OpenPassword { get; set; }
    public string? PermissionsPassword { get; set; }
    public PdfPermissionSet Permissions { get; init; } = PdfPermissionSet.All;
    public PdfEncryptionStrength Strength { get; init; } = PdfEncryptionStrength.Aes256;
    public bool EncryptMetadata { get; init; } = true;

    public void ClearPasswords()
    {
        OpenPassword = null;
        PermissionsPassword = null;
    }
}

/// <summary>The checks the Protect dialog makes before it lets the user continue.</summary>
public static class ProtectPasswordRules
{
    /// <summary>Why the choices cannot be used, or null when they can.</summary>
    public static string? Validate(bool requireOpen, string open, string openConfirm, bool restrict, string owner, string ownerConfirm,
        PdfEncryptionStrength strength)
    {
        if (!requireOpen && !restrict) return "Require an open password, restrict what readers may do, or both.";
        if (requireOpen && open.Length == 0) return "Type the open password.";
        if (requireOpen && open != openConfirm) return "The open passwords do not match.";
        if (restrict && owner.Length == 0) return "Type a permissions password: it is needed to change the restrictions later.";
        if (restrict && owner != ownerConfirm) return "The permissions passwords do not match.";
        if (requireOpen && restrict && open == owner) return "The permissions password must differ from the open password.";
        foreach (string password in new[] { requireOpen ? open : string.Empty, restrict ? owner : string.Empty })
        {
            if (password.Length == 0) continue;
            if (strength == PdfEncryptionStrength.Aes128)
            {
                if (!password.All(c => c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ'))
                    return "AES-128 passwords are limited to Latin letters, digits and symbols. Choose AES-256 for other characters.";
                continue;
            }
            try { System.Security.Cryptography.CryptographicOperations.ZeroMemory(PdfPasswordPreparation.PrepareForEncryption(password)); }
            catch (ArgumentException ex) { return ex.Message.Split(" (Parameter")[0]; }
        }
        return null;
    }
}

public partial class MainViewModel
{
    /// <summary>Shows the Protect with Password dialog for the named document; null when cancelled.</summary>
    public Func<string, ProtectDocumentOptions?>? ShowProtectDialogFunc { get; set; }

    /// <summary>Asks for the permissions (owner) password: file name in, password or null out.</summary>
    public Func<string, string?>? RequestOwnerPasswordFunc { get; set; }

    /// <summary>Picks where a copy goes (suggested path, dialog title); null when cancelled. Defaults to a Save As dialog.</summary>
    public Func<string, string, string?>? PickSavePathFunc { get; set; }

    /// <summary>"Password security, AES-256", "Certificate security, AES-128", or "No security".</summary>
    [ObservableProperty]
    private string _securityMethod = "No security";

    /// <summary>The document was opened with its owner password, or with one that doubles as it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveSecurity))]
    private bool _isOwnerPasswordKnown = true;

    /// <summary>
    /// Remove Security is offered for a password-encrypted document when the owner password is
    /// known, or when there is no open password (the owner password is asked for then). A reader who
    /// only knows the open password cannot strip the author's permissions.
    /// </summary>
    public bool CanRemoveSecurity => IsDocumentLoaded && DocumentPermissions.IsEncrypted && !_docService.IsDecryptedCopy
                                     && (IsOwnerPasswordKnown || string.IsNullOrEmpty(_openPassword));

    partial void OnDocumentPermissionsChanged(PdfEngine.Documents.PdfDocumentPermissions value) => OnPropertyChanged(nameof(CanRemoveSecurity));

    partial void OnIsDocumentLoadedChanged(bool value) => OnPropertyChanged(nameof(CanRemoveSecurity));

    /// <summary>Reads the security handler's method and whether the owner password is known (after opening).</summary>
    private async Task RefreshSecurityDetailsAsync()
    {
        SecurityMethod = "No security";
        IsOwnerPasswordKnown = true;
        if (!DocumentPermissions.IsEncrypted || _docService.CurrentBytes is not { } bytes)
        {
            OnPropertyChanged(nameof(CanRemoveSecurity));
            return;
        }
        string? password = _openPassword;
        try
        {
            (SecurityMethod, IsOwnerPasswordKnown) = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(bytes, _docService.CurrentFilePath, password: password);
                var security = doc.Security!;
                string kind = security is PdfStandardSecurityHandler ? "Password security" : "Certificate security";
                return ($"{kind}, {security.Algorithm}" + (security.IsMetadataEncrypted ? string.Empty : " (metadata not encrypted)"),
                        PdfEncryptor.IsOwnerKnown(doc, password));
            });
        }
        catch (Exception ex) when (ex is PdfEngine.Vector.Diagnostics.PdfVectorException or InvalidOperationException)
        {
            SecurityMethod = "Encrypted";
            IsOwnerPasswordKnown = DocumentPermissions.Unrestricted;
        }
        OnPropertyChanged(nameof(CanRemoveSecurity));
    }

    /// <summary>One line per permission, as Document Properties shows them.</summary>
    public string PermissionsDescription
    {
        get
        {
            var p = DocumentPermissions;
            if (!p.IsEncrypted) return "Everything is allowed.";
            static string A(bool allowed) => allowed ? "Allowed" : "Not allowed";
            string print = !p.CanPrint ? "Not allowed" : p.CanPrintHighQuality ? "High resolution" : "Low resolution";
            string lines = string.Join("\n",
                $"Printing: {print}",
                $"Changing the document: {A(p.CanModify)}",
                $"Content copying: {A(p.CanCopy)}",
                $"Copying for accessibility: {A(p.CanExtractForAccessibility)}",
                $"Commenting: {A(p.CanAnnotate)}",
                $"Filling in forms: {A(p.CanFillForms)}",
                $"Page assembly: {A(p.CanAssemble)}");
            return p.Unrestricted ? "Opened with the permissions password: everything is allowed.\n" + lines : lines;
        }
    }

    private string? WhyCannotChangeSecurity()
    {
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return "Open a document first.";
        if (_docService.IsDecryptedCopy) return "This document is encrypted for specific recipients; its security cannot be changed here.";
        return null;
    }

    /// <summary>The copy is made from the document as saved; unsaved comments or form entries are not in it.</summary>
    private bool ProceedWithoutUnsavedChanges(string caption) =>
        !HasUnsavedChanges || Confirm("The copy is made from the saved document, so your unsaved changes will not be in it. " +
                                      "Save them first to include them.\n\nContinue without them?", caption);

    private string? PickSavePath(string suggested, string title)
    {
        if (PickSavePathFunc != null) return PickSavePathFunc(suggested, title);
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested),
            OverwritePrompt = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Save As for a security change: a new name, or the original only when the user confirms it.</summary>
    private string? PickSecuredCopyPath(string suffix, string title)
    {
        string source = _docService.CurrentFilePath;
        string suggested = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, Path.GetFileNameWithoutExtension(source) + suffix + ".pdf");
        if (PickSavePath(suggested, title) is not { Length: > 0 } output) return null;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
            && !Confirm($"Replace the original document?\n\n{source}\n\nThe current version cannot be recovered afterwards.", title))
            return null;
        return output;
    }

    /// <summary>Protect with Password: the dialog, the signature warning, Save As, then the protected copy.</summary>
    [RelayCommand]
    public async Task ProtectWithPasswordAsync()
    {
        if (WhyCannotChangeSecurity() is { } reason)
        {
            ShowAlert(reason, "Protect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ProceedWithoutUnsavedChanges("Protect")) return;
        string? ownerPassword = null;
        if (!IsOwnerPasswordKnown && !TryProveOwner("Protect", out ownerPassword)) return;
        if (ShowProtectDialogFunc?.Invoke(Path.GetFileName(_docService.CurrentFilePath)) is not { } options) return;
        try
        {
            if (PickSecuredCopyPath("_protected", "Save the protected copy") is not { } output) return;
            await ProtectToAsync(options, output, ownerPassword);
        }
        finally
        {
            options.ClearPasswords();
        }
    }

    /// <summary>
    /// The work of <see cref="ProtectWithPasswordAsync"/> after the dialogs (tests call this). The
    /// original stays open; a copy saved over it is reopened, asking for its new password.
    /// </summary>
    public async Task<bool> ProtectToAsync(ProtectDocumentOptions options, string outputPath, string? currentOwnerPassword = null)
    {
        try
        {
            return await RewriteSecurityAsync(outputPath, "Protect", "protected", doc =>
            {
                var encryption = new PdfEncryptionOptions
                {
                    UserPassword = options.OpenPassword,
                    OwnerPassword = options.PermissionsPassword,
                    Permissions = options.Permissions,
                    Strength = options.Strength,
                    EncryptMetadata = options.EncryptMetadata,
                    CurrentOwnerPassword = currentOwnerPassword ?? _openPassword,
                    AllowInvalidatingSignatures = true, // confirmed by RewriteSecurityAsync
                };
                return PdfEncryptor.Encrypt(doc, encryption);
            });
        }
        finally
        {
            options.ClearPasswords();
        }
    }

    /// <summary>Remove Security: the owner password (asked for when not known), the signature warning, Save As.</summary>
    [RelayCommand]
    public async Task RemoveSecurityAsync()
    {
        if (WhyCannotChangeSecurity() is { } reason)
        {
            ShowAlert(reason, "Remove Security", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!DocumentPermissions.IsEncrypted)
        {
            ShowAlert("This document has no security to remove.", "Remove Security", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ProceedWithoutUnsavedChanges("Remove Security")) return;
        string? ownerPassword = null;
        if (!IsOwnerPasswordKnown && !TryProveOwner("Remove Security", out ownerPassword)) return;
        if (!Confirm("Remove the password and permissions from a copy of this document? Anyone with the copy can then open, print, copy and change it.",
                "Remove Security")) return;
        if (PickSecuredCopyPath("_unsecured", "Save the unsecured copy") is not { } output) return;
        await RemoveSecurityToAsync(output, ownerPassword);
    }

    /// <summary>The work of <see cref="RemoveSecurityAsync"/> after the dialogs (tests call this).</summary>
    public Task<bool> RemoveSecurityToAsync(string outputPath, string? ownerPassword = null) =>
        RewriteSecurityAsync(outputPath, "Remove Security", "unsecured",
            doc => PdfEncryptor.RemoveSecurity(doc, ownerPassword ?? _openPassword, allowInvalidatingSignatures: true));

    /// <summary>Asks for the permissions password and checks it against the document before anything is written.</summary>
    private bool TryProveOwner(string caption, out string? ownerPassword)
    {
        ownerPassword = RequestOwnerPasswordFunc?.Invoke(Path.GetFileName(_docService.CurrentFilePath));
        if (string.IsNullOrEmpty(ownerPassword))
        {
            ShowAlert("Changing this document's security needs its permissions password.", caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        bool proven;
        try
        {
            using var doc = PdfVectorDocument.Open(new PdfEngine.Vector.Parsing.MemoryByteSource(_docService.CurrentBytes!), _docService.CurrentFilePath, password: _openPassword);
            proven = PdfEncryptor.IsOwnerKnown(doc, ownerPassword);
        }
        catch (PdfEngine.Vector.Diagnostics.PdfVectorException)
        {
            proven = false;
        }
        if (proven) return true;
        ownerPassword = null;
        ShowAlert("The permissions password is incorrect.", caption, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task<bool> RewriteSecurityAsync(string outputPath, string caption, string adjective,
        Func<PdfVectorDocument, PdfSecurityRewriteResult> rewrite)
    {
        if (WhyCannotChangeSecurity() is { } reason)
        {
            ShowAlert(reason, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        byte[] bytes = _docService.CurrentBytes!;
        string source = _docService.CurrentFilePath;
        string output = Path.GetFullPath(outputPath);
        bool replacesOriginal = string.Equals(output, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        StatusText = caption == "Protect" ? "Encrypting..." : "Removing security...";
        PdfSecurityRewriteResult result;
        try
        {
            using var doc = await PdfVectorDocument.OpenAsync(bytes, source, password: _openPassword);
            int signatures = await Task.Run(() => PdfEncryptor.CountSignatures(doc));
            if (signatures > 0 && !Confirm(
                    $"This document has {signatures} digital signature(s). Saving a {adjective} copy rewrites the whole file, " +
                    "so the signatures in the copy will no longer be valid. The original keeps them.\n\nContinue?", caption))
            {
                StatusText = "Security not changed.";
                return false;
            }
            result = await Task.Run(() => rewrite(doc));
            string temporary = Path.Combine(Path.GetDirectoryName(output) ?? string.Empty, $".{Path.GetFileName(output)}.securing");
            await File.WriteAllBytesAsync(temporary, result.Bytes);
            File.Move(temporary, output, overwrite: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or PdfEngine.Vector.Diagnostics.PdfVectorException)
        {
            StatusText = "Security not changed.";
            ShowAlert($"The {adjective} copy was not saved:\n\n{ex.Message}", caption, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        string signatureNote = result.SignaturesInvalidated > 0 ? $"\n\n{result.SignaturesInvalidated} signature(s) in the copy are no longer valid." : string.Empty;
        if (replacesOriginal)
        {
            // The open document is now the rewritten file: reopen it (asking for a new open password).
            HasUnsavedChanges = false;
            await LoadDocumentAsync(output);
        }
        StatusText = $"Saved the {adjective} copy: {Path.GetFileName(output)}";
        ShowAlert($"The {adjective} copy is saved:\n{output}{signatureNote}", caption, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }
}
