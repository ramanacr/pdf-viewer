using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Signatures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfEngine.Vector.Signatures;

namespace PdfViewer.ViewModels;

/// <summary>Where a signature goes: a box drawn on a page, or an existing empty signature field.</summary>
/// <param name="Bounds">Normalized to the unrotated page, top-left origin (like annotations); null when <paramref name="FieldName"/> is set.</param>
public sealed record SignaturePlacement(int PageNumber, Rect? Bounds, string? FieldName = null);

/// <summary>What the signing dialog collected.</summary>
public sealed record SignatureOptions(
    X509Certificate2 Certificate,
    string? Reason,
    string? Location,
    string? ContactInfo,
    Uri? TimestampServer,
    string OutputPath,
    int? CertificationLevel = null);

public partial class MainViewModel
{
    /// <summary>Shows the signing dialog for a placement (and whether the document can still be certified); null when the user cancels.</summary>
    public Func<SignaturePlacement, bool, SignatureOptions?>? ShowSignDialogFunc { get; set; }

    /// <summary>Only an unsigned document can be certified.</summary>
    public bool CanCertify => !HasSignatures;

    /// <summary>Waiting for the user to drag the signature's box on a page.</summary>
    [ObservableProperty]
    private bool _isPlacingSignature;

    // Placing a signature, marking for redaction and drawing annotations exclude each other.
    partial void OnIsPlacingSignatureChanged(bool value)
    {
        if (value) { ActiveAnnotationTool = null; IsMarkingRedaction = false; }
    }

    partial void OnActiveAnnotationToolChanged(AnnotationType? value)
    {
        if (value != null) { IsPlacingSignature = false; IsMarkingRedaction = false; }
    }

    /// <summary>Why the document cannot be signed right now, or null.</summary>
    public string? WhyCannotSign()
    {
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return "Open a document first.";
        if (_docService.IsDecryptedCopy) return "This document is encrypted for specific recipients; save an unencrypted copy to sign it.";
        if (_docService.Permissions is { CanFillForms: false, CanModify: false })
            return "This document's security settings do not allow signing.";
        if (HasUnsavedChanges && !OnlyFormChanges)
            return "Save your changes before signing: a signature covers the document as it is saved.";
        return null;
    }

    [RelayCommand]
    private void BeginSignature()
    {
        if (IsPlacingSignature)
        {
            IsPlacingSignature = false;
            StatusText = "Signing cancelled.";
            return;
        }
        if (WhyCannotSign() is { } reason)
        {
            ShowAlert(reason, "Sign Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IsPlacingSignature = true;
        StatusText = "Drag a box where the signature should appear (Esc cancels).";
    }

    /// <summary>The user placed the signature (dragged a box, or clicked an empty signature field).</summary>
    public async Task<bool> PlaceSignatureAsync(SignaturePlacement placement)
    {
        IsPlacingSignature = false;
        if (WhyCannotSign() is { } reason)
        {
            ShowAlert(reason, "Sign Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (ShowSignDialogFunc?.Invoke(placement, CanCertify) is not { } options)
        {
            StatusText = "Signing cancelled.";
            return false;
        }
        return await SignAsync(placement, options);
    }

    /// <summary>
    /// Signs the current revision (including filled form fields) and writes it to
    /// <see cref="SignatureOptions.OutputPath"/>, then opens the signed file. The signature is an
    /// incremental update, so everything signed before stays valid.
    /// </summary>
    public async Task<bool> SignAsync(SignaturePlacement placement, SignatureOptions options)
    {
        if (WhyCannotSign() is { } reason)
        {
            ShowAlert(reason, "Sign Document", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        byte[] current = _docService.CurrentBytes!;
        int pageToShow = CurrentPageNumber;
        StatusText = options.TimestampServer != null ? "Signing and requesting a timestamp..." : "Signing...";
        byte[] signed;
        try
        {
            using var doc = await PdfVectorDocument.OpenAsync(current, _docService.CurrentFilePath, password: _openPassword);
            var request = new PdfSignatureRequest
            {
                FieldName = placement.FieldName,
                PageNumber = placement.PageNumber,
                Rect = placement.Bounds is { } b ? ToUserSpace(doc, placement.PageNumber, b) : null,
                SignerName = SigningCertificates.SignerName(options.Certificate),
                Reason = options.Reason,
                Location = options.Location,
                ContactInfo = options.ContactInfo,
                SigningTime = DateTimeOffset.Now,
                ContentsSize = options.TimestampServer != null ? 32768 : 16384,
                CertificationLevel = options.CertificationLevel,
            };
            var timestamp = options.TimestampServer is { } server ? TimestampClient.For(server) : null;
            signed = await SignWithRoomAsync(doc, current, request, options.Certificate, timestamp);
        }
        catch (Exception ex) when (ex is CryptographicException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or System.Net.Http.HttpRequestException or TaskCanceledException
                                       or System.Collections.Generic.KeyNotFoundException)
        {
            StatusText = "The document was not signed.";
            ShowAlert($"The document was not signed:\n\n{ex.Message}", "Sign Document", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        string output = Path.GetFullPath(options.OutputPath);
        string directory = Path.GetDirectoryName(output) ?? string.Empty;
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(output)}.signing");
        string backupPath = temporaryPath + ".bak";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, signed);
            if (File.Exists(output)) ReplaceOriginal(temporaryPath, output, backupPath);
            else File.Move(temporaryPath, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "The signed document was not saved.";
            ShowAlert($"The signed document could not be saved:\n\n{ex.Message}", "Sign Document", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            TryDeleteQuietly(temporaryPath);
            TryDeleteQuietly(backupPath);
        }

        // Show the signed file, as saved: its signature is what the reader now sees.
        HasUnsavedChanges = false;
        _formChangedSinceSave = false;
        await LoadDocumentAsync(output, _openPassword);
        if (pageToShow >= 1 && pageToShow <= PageCount) CurrentPageNumber = pageToShow;
        StatusText = $"Signed by {SigningCertificates.SignerName(options.Certificate)} and saved to {Path.GetFileName(output)}.";
        return true;
    }

    /// <summary>Signs; when the signature outgrows the space reserved for it (a long chain, a large timestamp), reserves more and signs again.</summary>
    private static async Task<byte[]> SignWithRoomAsync(PdfVectorDocument doc, byte[] current, PdfSignatureRequest request,
        X509Certificate2 certificate, PdfTimestampClient? timestamp)
    {
        for (int attempt = 0; ; attempt++)
        {
            var prepared = PdfSigner.Prepare(doc, current, request);
            byte[] cms = await PdfCmsSigner.SignAsync(prepared.SignedBytes(), certificate, timestamp);
            if (cms.Length <= request.ContentsSize || attempt >= 3)
                return prepared.Complete(cms);
            request = request with { ContentsSize = Math.Max(request.ContentsSize * 2, cms.Length + 4096) };
        }
    }

    /// <summary>A box normalized to the unrotated page (top-left origin) in the page's default user space.</summary>
    internal static PdfRect ToUserSpace(PdfVectorDocument doc, int pageNumber, Rect normalized)
    {
        var crop = doc.PageTree.Pages[pageNumber - 1].CropBox;
        double x = crop.X + normalized.X * crop.Width;
        double w = normalized.Width * crop.Width;
        double h = normalized.Height * crop.Height;
        double y = crop.Y + (1 - normalized.Y - normalized.Height) * crop.Height;
        return new PdfRect(x, y, w, h);
    }

    // ------------------------------------------------------------------ validation

    /// <summary>The document's signatures, oldest first, as validated.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SignatureItemViewModel> Signatures { get; } = new();

    [ObservableProperty]
    private bool _hasSignatures;

    /// <summary>The one-line verdict over all signatures (the banner above the page).</summary>
    [ObservableProperty]
    private string? _signatureBanner;

    /// <summary>"ok", "warn" or "bad".</summary>
    [ObservableProperty]
    private string _signatureBannerLevel = "ok";

    [ObservableProperty]
    private bool _isSignatureBannerDismissed;

    private int _signatureValidation;

    /// <summary>
    /// Validates every signature against the document as it is now (unsaved form changes
    /// included, so the panel shows what they would do to each signature).
    /// </summary>
    public async Task ValidateSignaturesAsync()
    {
        int run = ++_signatureValidation;
        byte[]? bytes = _docService.CurrentBytes;
        System.Collections.Generic.IReadOnlyList<PdfSignatureCheck> checks;
        int revisions = 1;
        try
        {
            if (bytes == null || _docService.IsDecryptedCopy) checks = Array.Empty<PdfSignatureCheck>();
            else
            {
                string? password = _openPassword;
                checks = await Task.Run(() => PdfSignatureValidator.ValidateAsync(bytes, password));
                revisions = PdfSignatureValidator.RevisionEnds(bytes).Count;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            checks = Array.Empty<PdfSignatureCheck>();
        }
        if (run != _signatureValidation) return; // a newer validation is under way

        Signatures.Clear();
        foreach (var c in checks) Signatures.Add(SignatureItemViewModel.From(c, revisions));
        HasSignatures = Signatures.Count > 0;
        if (!HasSignatures)
        {
            SignatureBanner = null;
            return;
        }
        bool anyBad = Signatures.Any(s => s.Level == "bad");
        bool anyWarn = Signatures.Any(s => s.Level == "warn");
        var people = checks.Where(c => !c.IsDocumentTimestamp).ToList();
        var certifier = people.FirstOrDefault(c => c.CertificationLevel != null);
        string who = certifier != null ? $"Certified by {certifier.SignerName}"
            : people.Count == 1 ? $"Signed by {people[0].SignerName}"
            : people.Count > 1 ? $"Signed by {people.Count} people" : "Timestamped";
        bool several = checks.Count > 1;
        SignatureBannerLevel = anyBad ? "bad" : anyWarn ? "warn" : "ok";
        SignatureBanner = anyBad
            ? $"{who}. At least one signature is invalid, or the document was changed in a way its signatures do not allow."
            : anyWarn
                ? $"{who}. The document is intact, but {(several ? "at least one signer's" : "the signer's")} identity could not be verified."
                : $"{who}. {(several ? "All signatures are" : "The signature is")} valid.";
    }

    [RelayCommand]
    private void ShowSignaturePanel()
    {
        IsSidebarOpen = true;
        SelectedSidebarTab = SignaturesTabIndex;
    }

    [RelayCommand]
    private void DismissSignatureBanner() => IsSignatureBannerDismissed = true;

    /// <summary>The sidebar tab that lists signatures.</summary>
    public const int SignaturesTabIndex = 4;

    /// <summary>Signature fields in the document that are waiting to be signed.</summary>
    public System.Collections.Generic.IReadOnlyList<FormFieldViewModel> EmptySignatureFields =>
        AllFormFields.Where(f => f.Kind == PdfEngine.Vector.Forms.PdfFormFieldKind.Signature).ToList();
}
