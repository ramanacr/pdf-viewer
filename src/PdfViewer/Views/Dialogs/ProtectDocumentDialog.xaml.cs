using System.Windows;
using PdfEngine.Vector.Security;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Protect with Password: an open password (typed twice), restrictions guarded by a permissions
/// password, the cipher and whether metadata is encrypted. The password boxes are cleared as soon
/// as the choices are handed over, and nothing typed here is logged or remembered.
/// </summary>
public partial class ProtectDocumentDialog : Window
{
    public ProtectDocumentOptions? Result { get; private set; }

    public ProtectDocumentDialog(string documentName)
    {
        InitializeComponent();
        SummaryText.Text = $"Encrypts a copy of \"{documentName}\". Readers need the open password to see it; the permissions password lifts the restrictions and allows changing the security.";
        RequireOpenCheck.IsChecked = true;
        StrengthCombo.SelectionChanged += (_, _) => Revalidate();
        Loaded += (_, _) => OpenPasswordBox.Focus();
        Closed += (_, _) => ClearBoxes();
    }

    private PdfEncryptionStrength Strength => StrengthCombo.SelectedIndex == 1 ? PdfEncryptionStrength.Aes128 : PdfEncryptionStrength.Aes256;

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (OpenPasswordGrid == null || PermissionsPanel == null) return; // during InitializeComponent
        OpenPasswordGrid.IsEnabled = RequireOpenCheck.IsChecked == true;
        PermissionsPanel.IsEnabled = RestrictCheck.IsChecked == true;
        Revalidate();
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => Revalidate();

    private string? Validate() => ProtectPasswordRules.Validate(
        RequireOpenCheck.IsChecked == true, OpenPasswordBox.Password, OpenConfirmBox.Password,
        RestrictCheck.IsChecked == true, OwnerPasswordBox.Password, OwnerConfirmBox.Password, Strength);

    private void Revalidate()
    {
        if (ProtectButton == null || ErrorText == null) return;
        string? error = Validate();
        ProtectButton.IsEnabled = error == null;
        // Say what is wrong once something has been typed, not while the dialog is still empty.
        bool typed = OpenPasswordBox.Password.Length + OpenConfirmBox.Password.Length + OwnerPasswordBox.Password.Length + OwnerConfirmBox.Password.Length > 0;
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = error != null && typed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Protect_Click(object sender, RoutedEventArgs e)
    {
        if (Validate() != null) return;
        bool restrict = RestrictCheck.IsChecked == true;
        var permissions = !restrict ? PdfPermissionSet.All : new PdfPermissionSet
        {
            Printing = PrintingCombo.SelectedIndex switch { 0 => PdfPrintPermission.None, 1 => PdfPrintPermission.LowResolution, _ => PdfPrintPermission.HighResolution },
            ChangeDocument = ChangeCheck.IsChecked == true,
            CopyContent = CopyCheck.IsChecked == true,
            CopyForAccessibility = AccessibilityCheck.IsChecked == true,
            Comment = CommentCheck.IsChecked == true,
            FillForms = FormsCheck.IsChecked == true,
            AssemblePages = AssembleCheck.IsChecked == true,
        };
        Result = new ProtectDocumentOptions
        {
            OpenPassword = RequireOpenCheck.IsChecked == true ? OpenPasswordBox.Password : null,
            PermissionsPassword = restrict ? OwnerPasswordBox.Password : null,
            Permissions = permissions,
            Strength = Strength,
            EncryptMetadata = EncryptMetadataCheck.IsChecked == true,
        };
        ClearBoxes();
        DialogResult = true;
    }

    private void ClearBoxes()
    {
        OpenPasswordBox.Clear();
        OpenConfirmBox.Clear();
        OwnerPasswordBox.Clear();
        OwnerConfirmBox.Clear();
    }
}
