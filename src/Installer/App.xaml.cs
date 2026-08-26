using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace PdfViewer.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = Environment.GetCommandLineArgs();

        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("-uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            var res = MessageBox.Show("Are you sure you want to uninstall PDF Viewer Native?", "Uninstall PDF Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                InstallService.Uninstall();
                MessageBox.Show("PDF Viewer has been successfully uninstalled.", "Uninstall Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        if (args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase) || a.Equals("-silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/s", StringComparison.OrdinalIgnoreCase) || a.Equals("-s", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                InstallService.Install(InstallService.DefaultInstallPath, true, true, true);

                if (args.Any(a => a.Equals("-launch", StringComparison.OrdinalIgnoreCase) || a.Equals("/launch", StringComparison.OrdinalIgnoreCase) || a.Equals("-run", StringComparison.OrdinalIgnoreCase)))
                {
                    string targetExe = Path.Combine(InstallService.DefaultInstallPath, "PdfViewer.exe");
                    if (File.Exists(targetExe))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = targetExe,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer_error.log"), ex.ToString());
            }
            Shutdown();
            return;
        }

        var window = new InstallerWindow();
        window.Show();
    }
}
