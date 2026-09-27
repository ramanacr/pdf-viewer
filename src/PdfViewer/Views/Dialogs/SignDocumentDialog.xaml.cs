using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PdfViewer.Services;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Chooses who signs (a certificate from the personal store, which covers smart cards and
/// tokens, or a .pfx/.p12 file), what the signature records, an optional timestamp, and where
/// the signed file is saved.
/// </summary>
public partial class SignDocumentDialog : Window
{
    private sealed record CertificateItem(string Name, string Detail, X509Certificate2? Certificate, string? FilePath);

    private readonly List<X509Certificate2> _storeCertificates;
    private readonly SigningSettings _settings = SigningSettings.Load();
    private CertificateItem? _fileItem;

    public SignatureOptions? Result { get; private set; }

    public SignDocumentDialog(SignaturePlacement placement, string documentPath, bool canCertify = true)
    {
        InitializeComponent();
        // Certification is for the author's first signature: a signed document can only be approved.
        if (!canCertify) CertifyRow.Visibility = Visibility.Collapsed;
        PlacementText.Text = placement.FieldName != null
            ? $"Signs the field \"{placement.FieldName}\". The signature covers the document as it is now; later changes show as changes made after signing."
            : $"Adds a visible signature to page {placement.PageNumber}. The signature covers the document as it is now; later changes show as changes made after signing.";

        _storeCertificates = LoadStore();
        foreach (var c in _storeCertificates)
            CertificateCombo.Items.Add(new CertificateItem(SigningCertificates.SignerName(c), SigningCertificates.Describe(c), c, null));
        if (CertificateCombo.Items.Count > 0) CertificateCombo.SelectedIndex = 0;
        else NoCertificateText.Visibility = Visibility.Visible;

        ReasonBox.Text = _settings.Reason ?? string.Empty;
        LocationBox.Text = _settings.Location ?? string.Empty;
        ContactBox.Text = _settings.ContactInfo ?? string.Empty;
        TimestampBox.Text = _settings.TimestampServer ?? string.Empty;
        TimestampCheck.IsChecked = _settings.UseTimestamp && !string.IsNullOrWhiteSpace(_settings.TimestampServer);
        TimestampBox.IsEnabled = TimestampCheck.IsChecked == true;
        OutputBox.Text = documentPath;

        Closed += (_, _) =>
        {
            // Store certificates not chosen are released; the chosen one belongs to the caller.
            foreach (var c in _storeCertificates)
                if (!ReferenceEquals(c, Result?.Certificate)) c.Dispose();
        };
    }

    private static List<X509Certificate2> LoadStore()
    {
        try { return SigningCertificates.FromStore(); }
        catch (CryptographicException) { return new List<X509Certificate2>(); }
    }

    private void CertificateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PfxPasswordRow.Visibility = CertificateCombo.SelectedItem is CertificateItem { FilePath: not null } ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseCertificate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a digital ID file",
            Filter = "Digital ID files (*.pfx;*.p12)|*.pfx;*.p12|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_fileItem != null) CertificateCombo.Items.Remove(_fileItem);
        _fileItem = new CertificateItem(Path.GetFileName(dialog.FileName), "Certificate file - enter its password below", null, dialog.FileName);
        CertificateCombo.Items.Add(_fileItem);
        CertificateCombo.SelectedItem = _fileItem;
        NoCertificateText.Visibility = Visibility.Collapsed;
        PfxPasswordBox.Focus();
    }

    private void TimestampCheck_Changed(object sender, RoutedEventArgs e)
    {
        TimestampBox.IsEnabled = TimestampCheck.IsChecked == true;
        if (TimestampBox.IsEnabled) TimestampBox.Focus();
    }

    private void ChangeOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the signed document",
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Path.GetFileName(OutputBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutputBox.Text),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FileName;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SignButton_Click(object sender, RoutedEventArgs e)
    {
        if (CertificateCombo.SelectedItem is not CertificateItem item)
        {
            ShowError("Choose a certificate to sign with.");
            return;
        }

        X509Certificate2 certificate;
        if (item.FilePath != null)
        {
            try
            {
                certificate = SigningCertificates.FromFile(item.FilePath, PfxPasswordBox.Password);
            }
            catch (CryptographicException ex)
            {
                ShowError($"The certificate file could not be opened: {ex.Message}");
                return;
            }
            if (SigningCertificates.WhyNotUsable(certificate, DateTimeOffset.Now) is { } fileReason)
            {
                certificate.Dispose();
                ShowError(fileReason);
                return;
            }
        }
        else certificate = item.Certificate!;

        Uri? timestamp = null;
        if (TimestampCheck.IsChecked == true)
        {
            if (!Uri.TryCreate(TimestampBox.Text.Trim(), UriKind.Absolute, out timestamp) || timestamp.Scheme is not ("http" or "https"))
            {
                if (item.FilePath != null) certificate.Dispose();
                ShowError("Enter the timestamp server's address, starting with http:// or https://.");
                return;
            }
        }

        _settings.Reason = Blank(ReasonBox.Text);
        _settings.Location = Blank(LocationBox.Text);
        _settings.ContactInfo = Blank(ContactBox.Text);
        _settings.TimestampServer = Blank(TimestampBox.Text);
        _settings.UseTimestamp = timestamp != null;
        _settings.Save();

        int? certification = CertifyRow.Visibility == Visibility.Visible ? CertifyCombo.SelectedIndex switch
        {
            1 => 2,
            2 => 3,
            3 => 1,
            _ => (int?)null,
        } : null;
        // Long-term validation is asked for each time and not remembered: it contacts the certificate authority.
        Result = new SignatureOptions(certificate, _settings.Reason, _settings.Location, _settings.ContactInfo, timestamp, OutputBox.Text, certification,
            AddLongTermValidation: LtvCheck.IsChecked == true);
        DialogResult = true;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
