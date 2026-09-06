using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// ViewModel representing a page thumbnail in the left navigation sidebar.
/// </summary>
public partial class ThumbnailViewModel : ObservableObject
{
    [ObservableProperty]
    private int _pageNumber;

    [ObservableProperty]
    private BitmapSource? _thumbnailImage;

    [ObservableProperty]
    private bool _isCurrentPage;

    [ObservableProperty]
    private bool _isLoading;

    public ThumbnailViewModel(int pageNumber)
    {
        PageNumber = pageNumber;
    }

    private bool _renderedNightMode;

    public async Task LoadThumbnailAsync(
        AsyncPageRenderer renderer, int rotation, bool nightMode, CancellationToken ct)
    {
        if (ThumbnailImage != null && _renderedNightMode == nightMode) return;

        IsLoading = true;
        try
        {
            // Render at lower DPI for fast thumbnail generation (e.g. 50 DPI)
            var thumb = await renderer.GetOrRenderPageAsync(PageNumber, 50, rotation, ct);
            if (!ct.IsCancellationRequested && thumb != null)
            {
                // Thumbnails follow the page. A sidebar of white thumbnails beside an inverted
                // document is the same mismatch night mode exists to remove.
                ThumbnailImage = nightMode ? NightModeImage.Invert(thumb) : thumb;
                _renderedNightMode = nightMode;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            IsLoading = false;
        }
    }

    public void UnloadThumbnail()
    {
        ThumbnailImage = null;
        _renderedNightMode = false;
        IsLoading = false;
    }
}
