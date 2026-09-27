using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using Microsoft.Win32;
using PdfEngine.Vector.Signatures;
using PdfViewer.Services;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Chooses who signs (a certificate from the personal store, which covers smart cards and
/// tokens, or a .pfx/.p12 file), what the signature records, how it looks (the details, the
/// name, or a handwritten signature chosen from a picture or drawn here), an optional
/// timestamp and document timestamp, and where the signed file is saved.
/// </summary>
public partial class SignDocumentDialog : Window
{
    private sealed record CertificateItem(string Name, string Detail, X509Certificate2? Certificate, string? FilePath);

    private readonly List<X509Certificate2> _storeCertificates;
    private readonly SigningSettings _settings = SigningSettings.Load();
    private CertificateItem? _fileItem;
    /// <summary>The signature picture (PNG), chosen or drawn; remembered only if the user asks.</summary>
    private byte[]? _imagePng;
    private bool _drawing;

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
        DocTimestampCheck.IsEnabled = TimestampCheck.IsChecked == true;
        OutputBox.Text = documentPath;

        // The appearance, as last used.
        foreach (ComboBoxItem option in LayoutCombo.Items)
            if ((string)option.Tag == _settings.AppearanceLayout) LayoutCombo.SelectedItem = option;
        ShowDateCheck.IsChecked = _settings.ShowDate;
        ShowReasonCheck.IsChecked = _settings.ShowReason;
        ShowLocationCheck.IsChecked = _settings.ShowLocation;
        ShowLabelsCheck.IsChecked = _settings.ShowLabels;
        RememberImageCheck.IsChecked = _settings.RememberSignatureImage;
        if (_settings.RememberSignatureImage) SetImage(SignatureImageStore.LoadRemembered());
        DrawCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromRgb(0x10, 0x20, 0x70), Width = 2.4, Height = 2.4, FitToCurve = true, StylusTip = StylusTip.Ellipse,
        };
        UpdateAppearancePanel();

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
        DocTimestampCheck.IsEnabled = TimestampBox.IsEnabled;
        if (!DocTimestampCheck.IsEnabled) DocTimestampCheck.IsChecked = false;
        if (TimestampBox.IsEnabled) TimestampBox.Focus();
    }

    // ------------------------------------------------------------------ appearance

    private PdfSignatureLayout SelectedLayout =>
        LayoutCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<PdfSignatureLayout>(tag, out var layout) ? layout : PdfSignatureLayout.TextOnly;

    private bool UsesImage => SelectedLayout is PdfSignatureLayout.ImageAndText or PdfSignatureLayout.ImageOnly;

    private void LayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAppearancePanel();

    private void UpdateAppearancePanel()
    {
        if (ImagePanel == null) return; // during InitializeComponent
        ImagePanel.Visibility = UsesImage ? Visibility.Visible : Visibility.Collapsed;
        bool details = SelectedLayout != PdfSignatureLayout.ImageOnly;
        foreach (var check in new[] { ShowDateCheck, ShowReasonCheck, ShowLocationCheck, ShowLabelsCheck }) check.IsEnabled = details;
    }

    private void SetImage(byte[]? png)
    {
        _imagePng = png;
        try
        {
            ImagePreview.Source = png != null ? SignatureImageStore.Preview(png) : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or ArgumentException)
        {
            _imagePng = null;
            ImagePreview.Source = null;
        }
        ImageHint.Visibility = _imagePng == null && !_drawing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a picture of your signature",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        StopDrawing(keep: false);
        try
        {
            SetImage(SignatureImageStore.PngFromFile(dialog.FileName));
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException or ArgumentException
                                       or System.IO.FileFormatException or InvalidOperationException)
        {
            ShowError($"The picture could not be read: {ex.Message}");
        }
    }

    private void Draw_Click(object sender, RoutedEventArgs e)
    {
        if (_drawing)
        {
            StopDrawing(keep: true);
            return;
        }
        _drawing = true;
        DrawCanvas.Strokes.Clear();
        DrawCanvas.Visibility = Visibility.Visible;
        ImagePreview.Visibility = Visibility.Collapsed;
        ImageHint.Visibility = Visibility.Collapsed;
        DrawButton.Content = "Done";
        DrawCanvas.Focus();
    }

    /// <summary>Ends drawing; the ink becomes the signature picture when <paramref name="keep"/> and there is any.</summary>
    private void StopDrawing(bool keep)
    {
        if (!_drawing) return;
        _drawing = false;
        byte[]? png = keep ? SignatureImageStore.PngFromStrokes(DrawCanvas.Strokes) : null;
        DrawCanvas.Visibility = Visibility.Collapsed;
        ImagePreview.Visibility = Visibility.Visible;
        DrawButton.Content = "Draw";
        SetImage(png ?? _imagePng);
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        DrawCanvas.Strokes.Clear();
        if (_drawing) return;
        SetImage(null);
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
        StopDrawing(keep: true);
        PdfSignatureAppearance? appearance = null;
        if (UsesImage && _imagePng == null)
        {
            if (item.FilePath != null) certificate.Dispose();
            ShowError("Choose a picture of your signature, or draw it, for this appearance.");
            return;
        }
        try
        {
            appearance = new PdfSignatureAppearance
            {
                Layout = SelectedLayout,
                Image = UsesImage ? SignatureImageStore.ToImage(_imagePng!) : null,
                ShowDate = ShowDateCheck.IsChecked == true,
                ShowReason = ShowReasonCheck.IsChecked == true,
                ShowLocation = ShowLocationCheck.IsChecked == true,
                ShowLabels = ShowLabelsCheck.IsChecked == true,
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.IO.IOException or System.IO.FileFormatException)
        {
            if (item.FilePath != null) certificate.Dispose();
            ShowError($"The signature picture could not be used: {ex.Message}");
            return;
        }

        _settings.TimestampServer = Blank(TimestampBox.Text);
        _settings.UseTimestamp = timestamp != null;
        _settings.AppearanceLayout = SelectedLayout.ToString();
        _settings.ShowDate = appearance.ShowDate;
        _settings.ShowReason = appearance.ShowReason;
        _settings.ShowLocation = appearance.ShowLocation;
        _settings.ShowLabels = appearance.ShowLabels;
        // The picture is kept only when the user asks, and deleted as soon as they stop asking.
        _settings.RememberSignatureImage = RememberImageCheck.IsChecked == true;
        if (_settings.RememberSignatureImage && _imagePng != null) SignatureImageStore.Remember(_imagePng);
        else if (!_settings.RememberSignatureImage) SignatureImageStore.Forget();
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
            AddLongTermValidation: LtvCheck.IsChecked == true, Appearance: appearance,
            AddDocumentTimestamp: timestamp != null && DocTimestampCheck.IsChecked == true);
        DialogResult = true;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
