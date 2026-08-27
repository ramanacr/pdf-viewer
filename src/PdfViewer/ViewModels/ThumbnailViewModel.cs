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

    public async Task LoadThumbnailAsync(AsyncPageRenderer renderer, int rotation, CancellationToken ct)
    {
        if (ThumbnailImage != null) return;

        IsLoading = true;
        try
        {
            // Render at lower DPI for fast thumbnail generation (e.g. 50 DPI)
            var thumb = await renderer.GetOrRenderPageAsync(PageNumber, 50, rotation, ct);
            if (!ct.IsCancellationRequested && thumb != null)
            {
                ThumbnailImage = thumb;
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
        IsLoading = false;
    }
}
