using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace PdfViewer.Installer;

public partial class InstallerWindow : Window
{
    private bool _isComplete;

    public InstallerWindow()
    {
        InitializeComponent();
        TargetDirectoryBox.Text = InstallService.DefaultInstallPath;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Installation Directory",
            InitialDirectory = TargetDirectoryBox.Text
        };

        if (dialog.ShowDialog() == true)
        {
            TargetDirectoryBox.Text = dialog.FolderName;
        }
    }

    private async void ActionNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isComplete)
        {
            if (LaunchAppCheck.IsChecked == true)
            {
                string exePath = Path.Combine(TargetDirectoryBox.Text, "PdfViewer.exe");
                if (File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });
                }
            }
            Close();
            return;
        }

        string targetDir = TargetDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            MessageBox.Show("Please enter a valid destination folder.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Switch to progress screen
        ConfigStepPanel.Visibility = Visibility.Collapsed;
        ProgressStepPanel.Visibility = Visibility.Visible;
        ActionNextButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        bool desktop = DesktopShortcutCheck.IsChecked == true;
        bool startMenu = StartMenuShortcutCheck.IsChecked == true;
        bool associate = AssociatePdfCheck.IsChecked == true;

        try
        {
            var progress = new Progress<int>(pct =>
            {
                InstallProgressBar.Value = pct;
                ProgressStatusText.Text = pct switch
                {
                    < 30 => "Preparing installation package...",
                    < 75 => $"Extracting application binaries... ({pct}%)",
                    < 85 => "Configuring Windows shortcuts and associations...",
                    _ => "Finalizing setup registration..."
                };
            });

            await Task.Run(() =>
            {
                InstallService.Install(targetDir, desktop, startMenu, associate, progress);
            });

            // Switch to complete screen
            ProgressStepPanel.Visibility = Visibility.Collapsed;
            CompleteStepPanel.Visibility = Visibility.Visible;
            ActionNextButton.Content = "Finish";
            ActionNextButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            _isComplete = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed:\n{ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ConfigStepPanel.Visibility = Visibility.Visible;
            ProgressStepPanel.Visibility = Visibility.Collapsed;
            ActionNextButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
