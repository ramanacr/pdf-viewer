using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.Views.Dialogs;

public partial class UpdateDialog : Window
{
    private UpdateInfo? _updateInfo;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;

    public UpdateDialog(UpdateInfo? preloadedInfo = null)
    {
        InitializeComponent();
        _updateInfo = preloadedInfo;

        Loaded += async (s, e) =>
        {
            if (_updateInfo != null)
            {
                DisplayUpdateInfo(_updateInfo);
            }
            else
            {
                await RunCheckAsync();
            }
        };

        Closing += (s, e) =>
        {
            // Closing via the title-bar X (or Alt+F4) while a download is in flight
            // must cancel it, otherwise it silently finishes and force-shuts the app
            // down to launch the installer even though the user dismissed the dialog.
            if (_isDownloading)
            {
                _downloadCts?.Cancel();
            }
        };
    }

    private async Task RunCheckAsync()
    {
        ShowPanel(CheckingPanel);
        PrimaryActionButton.IsEnabled = false;
        PrimaryActionButton.Content = "Checking...";
        CancelActionButton.Content = "Cancel";

        try
        {
            _updateInfo = await UpdateService.CheckForUpdatesAsync();
            DisplayUpdateInfo(_updateInfo);
        }
        catch (Exception ex)
        {
            ShowPanel(ErrorPanel);
            ErrorMessageText.Text = $"Error contacting GitHub: {ex.Message}";
            PrimaryActionButton.IsEnabled = true;
            PrimaryActionButton.Content = "Retry";
            CancelActionButton.Content = "Close";
        }
    }

    private void DisplayUpdateInfo(UpdateInfo info)
    {
        if (info.IsUpdateAvailable)
        {
            ShowPanel(UpdateAvailablePanel);
            ReleaseTitleText.Text = info.ReleaseTitle;
            PublishedDateText.Text = info.PublishedAt.HasValue
                ? $"Published: {info.PublishedAt.Value:MMMM dd, yyyy}"
                : string.Empty;

            OldVersionBadge.Text = $"v{info.CurrentVersion}";
            NewVersionBadge.Text = $"v{info.LatestVersion}";
            ReleaseNotesBox.Text = !string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? info.ReleaseNotes
                : "No changelog provided for this release.";

            DownloadSizeText.Text = !string.IsNullOrEmpty(info.FormattedInstallerSize)
                ? $"Setup installer size: ~{info.FormattedInstallerSize}"
                : string.Empty;

            PrimaryActionButton.IsEnabled = !string.IsNullOrEmpty(info.InstallerDownloadUrl);
            PrimaryActionButton.Content = "Update Now";
            CancelActionButton.Content = "Later";
        }
        else
        {
            ShowPanel(UpToDatePanel);
            CurrentVersionInfoText.Text = $"PDF Viewer v{info.CurrentVersion} is currently the newest release available.";
            PrimaryActionButton.IsEnabled = true;
            PrimaryActionButton.Content = "OK";
            CancelActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ErrorPanel.Visibility == Visibility.Visible)
        {
            await RunCheckAsync();
            return;
        }

        if (UpToDatePanel.Visibility == Visibility.Visible)
        {
            Close();
            return;
        }

        if (_updateInfo != null && _updateInfo.IsUpdateAvailable && !string.IsNullOrEmpty(_updateInfo.InstallerDownloadUrl))
        {
            await StartDownloadAsync(_updateInfo.InstallerDownloadUrl);
        }
        else if (_updateInfo != null && !string.IsNullOrEmpty(_updateInfo.ReleaseUrl))
        {
            UpdateService.OpenBrowser(_updateInfo.ReleaseUrl);
            Close();
        }
    }

    private async Task StartDownloadAsync(string downloadUrl)
    {
        ShowPanel(DownloadingPanel);
        _isDownloading = true;
        PrimaryActionButton.IsEnabled = false;
        PrimaryActionButton.Content = "Installing...";
        CancelActionButton.Content = "Cancel";

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        var progress = new Progress<(long BytesRead, long TotalBytes, double Percent)>(report =>
        {
            DownloadProgressBar.Value = report.Percent;
            DownloadPercentText.Text = $"{report.Percent:F0}%";

            double mbRead = report.BytesRead / (1024.0 * 1024.0);
            double mbTotal = report.TotalBytes > 0 ? report.TotalBytes / (1024.0 * 1024.0) : 0;

            DownloadBytesText.Text = mbTotal > 0
                ? $"{mbRead:F1} MB / {mbTotal:F1} MB"
                : $"{mbRead:F1} MB downloaded";
        });

        try
        {
            string downloadedInstaller = await UpdateService.DownloadInstallerAsync(downloadUrl, progress, ct);

            // Trigger silent upgrade and restart
            UpdateService.LaunchInstallerAndExit(downloadedInstaller, silent: true);
        }
        catch (OperationCanceledException)
        {
            _isDownloading = false;
            if (_updateInfo != null) DisplayUpdateInfo(_updateInfo);
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            ShowPanel(ErrorPanel);
            ErrorMessageText.Text = $"Download failed: {ex.Message}";
            PrimaryActionButton.IsEnabled = true;
            PrimaryActionButton.Content = "Retry";
            CancelActionButton.Content = "Close";
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading && _downloadCts != null)
        {
            _downloadCts.Cancel();
            return;
        }

        Close();
    }

    private void ViewOnGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        string url = _updateInfo?.ReleaseUrl ?? UpdateService.GitHubReleasesUrl;
        UpdateService.OpenBrowser(url);
    }

    private void ShowPanel(UIElement activePanel)
    {
        CheckingPanel.Visibility = activePanel == CheckingPanel ? Visibility.Visible : Visibility.Collapsed;
        UpToDatePanel.Visibility = activePanel == UpToDatePanel ? Visibility.Visible : Visibility.Collapsed;
        UpdateAvailablePanel.Visibility = activePanel == UpdateAvailablePanel ? Visibility.Visible : Visibility.Collapsed;
        DownloadingPanel.Visibility = activePanel == DownloadingPanel ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = activePanel == ErrorPanel ? Visibility.Visible : Visibility.Collapsed;
    }
}
