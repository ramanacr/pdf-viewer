using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfViewer.Models;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>
/// ViewModel representing a single page in the PDF document viewport.
/// </summary>
public partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private int _pageNumber;

    [ObservableProperty]
    private double _widthPt = 612;

    [ObservableProperty]
    private double _heightPt = 792;

    [ObservableProperty]
    private BitmapSource? _renderedImage;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _displayScale = 1.0;

    [ObservableProperty]
    private int _rotationAngle = 0;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<SearchMatch> MatchesOnPage { get; } = new();
    public ObservableCollection<AnnotationModel> AnnotationsOnPage { get; } = new();

    public double DisplayWidth => (RotationAngle == 90 || RotationAngle == 270 ? HeightPt : WidthPt) * DisplayScale;
    public double DisplayHeight => (RotationAngle == 90 || RotationAngle == 270 ? WidthPt : HeightPt) * DisplayScale;

    public PageViewModel(int pageNumber, double widthPt, double heightPt)
    {
        PageNumber = pageNumber;
        WidthPt = widthPt > 0 ? widthPt : 612;
        HeightPt = heightPt > 0 ? heightPt : 792;
    }

    public void UpdateScale(double scale)
    {
        DisplayScale = scale;
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public void UpdateRotation(int angle)
    {
        RotationAngle = angle;
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public async Task LoadImageAsync(AsyncPageRenderer renderer, int dpi, int rotation, CancellationToken ct = default)
    {
        if (RenderedImage != null && RotationAngle == rotation) return;

        IsLoading = true;
        try
        {
            var bitmap = await renderer.GetOrRenderPageAsync(PageNumber, dpi, rotation, ct);
            if (!ct.IsCancellationRequested && bitmap != null)
            {
                RenderedImage = bitmap;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            IsLoading = false;
        }
    }

    public void UnloadImage()
    {
        RenderedImage = null;
        IsLoading = false;
    }
}
