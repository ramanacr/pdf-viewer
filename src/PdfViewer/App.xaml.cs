using System;
using System.IO;
using System.Windows;
using PdfViewer.Services;
using PdfViewer.Views;

namespace PdfViewer;

public partial class App : Application
{
    public static string? StartupPdfPath { get; private set; }

    /// <summary>Set only for a <c>--startup-probe</c> launch (the cold-start release gate).</summary>
    internal static StartupProbe? Probe { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // First, so the milestone measures what the runtime and host cost before any of ours.
        Probe = StartupProbe.TryCreate(e.Args);
        Probe?.Mark("appStartup");

        base.OnStartup(e);

        if (Probe != null)
        {
            // A measurement must not add to the user's recent files or read their answers.
            RecentFilesService.SetSettingsDirectoryForTests(Probe.SettingsDirectory);
            PrivacySettings.SetSettingsDirectoryForTests(Probe.SettingsDirectory);
            SigningSettings.SetDirectoryForTests(Probe.SettingsDirectory);
        }

        // Initialize PDFium engine
        PdfiumNativeBridge.EnsureInitialized();

        if (Probe != null)
        {
            StartupPdfPath = Probe.DocumentPath;
            return;
        }

        // Process command line args for opening files directly or CLI shell actions
        if (e.Args.Length > 0)
        {
            string firstArg = e.Args[0];

            if (string.Equals(firstArg, "--register", StringComparison.OrdinalIgnoreCase))
            {
                ShellIntegrationService.RegisterShellAssociation();
                Shutdown(0);
                return;
            }

            if (string.Equals(firstArg, "--unregister", StringComparison.OrdinalIgnoreCase))
            {
                ShellIntegrationService.UnregisterShellAssociation();
                Shutdown(0);
                return;
            }

            if (string.Equals(firstArg, "--thumbnail", StringComparison.OrdinalIgnoreCase) && e.Args.Length >= 3)
            {
                string pdfPath = e.Args[1];
                string outImg = e.Args[2];
                int dpi = e.Args.Length >= 4 && int.TryParse(e.Args[3], out int d) ? d : 150;

                try
                {
                    var docService = PdfDocumentServiceFactory.CreateService();
                    var metaTask = docService.OpenDocumentAsync(pdfPath);
                    metaTask.Wait(5000);
                    if (docService.IsDocumentLoaded)
                    {
                        var bmp = docService.RenderPage(1, dpi);
                        if (bmp != null)
                        {
                            using var fs = File.Create(outImg);
                            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                            encoder.Save(fs);
                        }
                    }
                }
                catch { }

                Shutdown(0);
                return;
            }

            if (string.Equals(firstArg, "/p", StringComparison.OrdinalIgnoreCase) && e.Args.Length >= 2 && File.Exists(e.Args[1]))
            {
                StartupPdfPath = Path.GetFullPath(e.Args[1]);
            }
            else if (File.Exists(firstArg))
            {
                StartupPdfPath = Path.GetFullPath(firstArg);
            }
        }
    }
}
