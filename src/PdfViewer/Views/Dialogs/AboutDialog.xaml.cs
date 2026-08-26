using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using PdfViewer.Services;

namespace PdfViewer.Views.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var asm = typeof(AboutDialog).Assembly;
        var version = asm.GetName().Version;
        var infoVersionAttr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        string versionStr = version != null ? version.ToString(3) : "1.0.0";
        VersionTextBlock.Text = versionStr;

        if (infoVersionAttr != null && !string.IsNullOrEmpty(infoVersionAttr.InformationalVersion))
        {
            string fullInfo = infoVersionAttr.InformationalVersion;
            int plusIdx = fullInfo.IndexOf('+');
            if (plusIdx >= 0 && plusIdx + 1 < fullInfo.Length)
            {
                string hash = fullInfo[(plusIdx + 1)..];
                CommitTextBlock.Text = $"({(hash.Length > 7 ? hash[..7] : hash)})";
            }
        }

        LicenseStatusTextBlock.Text = LicenseService.IsLicensed
            ? "Aspose.Total Active (Licensed)"
            : "Evaluation Mode";

        LicenseStatusTextBlock.Foreground = LicenseService.IsLicensed
            ? (Brush)new BrushConverter().ConvertFrom("#0D8A3A")!
            : (Brush)new BrushConverter().ConvertFrom("#D83B01")!;

        LicenseLocationTextBlock.Text = !string.IsNullOrEmpty(LicenseService.LicenseFilePath)
            ? LicenseService.LicenseFilePath
            : (LicenseService.IsLicensed ? "Inbuilt Embedded Resource" : "None");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
