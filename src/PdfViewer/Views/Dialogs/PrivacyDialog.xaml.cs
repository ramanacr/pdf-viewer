using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using PdfViewer.Services;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// States the product's privacy and safety guarantees and owns the single opt-in that
/// governs the only outbound network request in the application.
/// </summary>
public partial class PrivacyDialog : Window
{
    /// <summary>A claim the product makes, paired with what backs it up.</summary>
    public sealed record Guarantee(string Claim, string Evidence);

    public PrivacyDialog()
    {
        InitializeComponent();
        StatementText.Text = PrivacySettings.PrivacyStatement;
        GuaranteeList.ItemsSource = BuildGuarantees();
        UpdateCheckBox.IsChecked = PrivacySettings.AutomaticUpdateChecksEnabled ?? false;
    }

    internal static IReadOnlyList<Guarantee> BuildGuarantees() =>
        new List<Guarantee>
        {
            new("No script engine is present.",
                DescribeScriptEngine()),
            new("Embedded actions are never executed.",
                "JavaScript, launch actions and embedded files found in a document are reported to " +
                "you and left inert. Links are opened only when you click them."),
            new("You are told what is inside a document.",
                "Every file is inspected on open and anything active is named in the status bar and " +
                "under Tools → Document Safety."),
            new("No telemetry, no analytics, no account.",
                "Nothing about you or your documents is recorded or transmitted. There is no sign-in " +
                "and no cloud storage."),
            new("Documents stay on this machine.",
                "Files are read from and written to local paths you choose. Nothing is uploaded."),
            new("The bill of materials is published.",
                "Every release ships CycloneDX and SPDX SBOMs listing each component and version, so " +
                "the above can be checked rather than taken on trust."),
        };

    private static string DescribeScriptEngine()
    {
        // A JavaScript-enabled PDFium build cannot run without its V8 snapshot and ICU data
        // files sitting beside the binary. Their absence is a fact about this installation,
        // not a promise, so it is reported as observed rather than asserted.
        try
        {
            var dir = AppContext.BaseDirectory;
            var jsRuntimeFiles = new[] { "v8.dll", "v8_context_snapshot.bin", "snapshot_blob.bin", "icudtl.dat" };
            var present = jsRuntimeFiles.Where(f => File.Exists(Path.Combine(dir, f))).ToArray();

            return present.Length == 0
                ? "This build links PDFium without V8. None of the JavaScript runtime files a " +
                  "script-enabled build requires are installed, so document script simply cannot run."
                : "Unexpected JavaScript runtime files were found in the installation folder: " +
                  string.Join(", ", present) + ". Reinstall from an official release.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "This build links PDFium without V8, so document script cannot run. " +
                   "(The installation folder could not be inspected to confirm.)";
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        PrivacySettings.SetAutomaticUpdateChecks(UpdateCheckBox.IsChecked == true);
        DialogResult = true;
        Close();
    }
}
