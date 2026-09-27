using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfViewer.Core.Comparison;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>A highlighted word range on a compared page.</summary>
public sealed record CompareHighlight(Rect Bounds, ChangeKind Kind, int ChangeIndex);

/// <summary>One page of one side of the comparison: its image (rendered on demand) and its highlights.</summary>
public sealed partial class ComparePageViewModel : ObservableObject
{
    private readonly PdfiumDocumentService _service;
    private int _rendering;

    public int PageNumber { get; }
    /// <summary>Display width and height in points (after /Rotate).</summary>
    public double WidthPoints { get; }
    public double HeightPoints { get; }
    public List<CompareHighlight> Highlights { get; } = new();

    /// <summary>A box normalized to the unrotated page (top-left origin), on the page as displayed.</summary>
    public Rect ToDisplay(Rect r) => Rotation switch
    {
        90 => new Rect(1 - r.Y - r.Height, r.X, r.Height, r.Width),
        180 => new Rect(1 - r.X - r.Width, 1 - r.Y - r.Height, r.Width, r.Height),
        270 => new Rect(r.Y, 1 - r.X - r.Width, r.Height, r.Width),
        _ => r,
    };

    [ObservableProperty]
    private BitmapSource? _image;

    /// <summary>The page's /Rotate: highlights come normalized to the unrotated page and are shown on the displayed one.</summary>
    public int Rotation { get; }

    public ComparePageViewModel(PdfiumDocumentService service, int pageNumber, double widthPoints, double heightPoints, int rotation = 0)
    {
        _service = service;
        Rotation = ((rotation % 360) + 360) % 360;
        PageNumber = pageNumber;
        WidthPoints = widthPoints;
        HeightPoints = heightPoints;
    }

    /// <summary>Renders the page once, off the UI thread, when it first comes into view.</summary>
    public async Task EnsureRenderedAsync(int dpi)
    {
        if (Image != null || Interlocked.Exchange(ref _rendering, 1) == 1) return;
        try
        {
            var bmp = await Task.Run(() => _service.RenderPage(PageNumber, dpi));
            if (bmp != null && bmp.CanFreeze && !bmp.IsFrozen) bmp.Freeze();
            Image = bmp;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window closed while it rendered.
        }
    }
}

/// <summary>A change as the list shows it.</summary>
public sealed record CompareChangeItem(int Index, TextChange Change)
{
    public string Title => Change.Kind switch
    {
        ChangeKind.Inserted => "Added",
        ChangeKind.Deleted => "Removed",
        _ => "Changed",
    };
    public string Where => Change.Kind switch
    {
        ChangeKind.Inserted => $"page {Change.NewPage} (new)",
        ChangeKind.Deleted => $"page {Change.OldPage} (old)",
        _ => Change.OldPage == Change.NewPage ? $"page {Change.OldPage}" : $"page {Change.OldPage} → {Change.NewPage}",
    };
    public string Detail => Change.Kind switch
    {
        ChangeKind.Inserted => Shorten(Change.NewText),
        ChangeKind.Deleted => Shorten(Change.OldText),
        _ => $"{Shorten(Change.OldText)}  →  {Shorten(Change.NewText)}",
    };
    private static string Shorten(string s) => s.Length > 120 ? s[..117] + "..." : s;
}

/// <summary>
/// Two documents side by side, their text compared word by word: removed words highlighted on
/// the old side, added words on the new side, replacements on both, and a list to step through.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject, IDisposable
{
    private PdfiumDocumentService? _old, _new;

    public string OldName { get; private set; } = string.Empty;
    public string NewName { get; private set; } = string.Empty;
    public ObservableCollection<ComparePageViewModel> OldPages { get; } = new();
    public ObservableCollection<ComparePageViewModel> NewPages { get; } = new();
    public ObservableCollection<CompareChangeItem> Changes { get; } = new();

    [ObservableProperty]
    private string _summary = "Comparing...";

    /// <summary>How wide each page is drawn, in device-independent pixels.</summary>
    [ObservableProperty]
    private double _pageWidth = 520;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _ignoreCase;

    [ObservableProperty]
    private CompareChangeItem? _selectedChange;

    private string? _oldPath, _newPath, _oldPassword, _newPassword;

    public async Task LoadAsync(string oldPath, string newPath, string? oldPassword = null, string? newPassword = null, CancellationToken ct = default)
    {
        _oldPath = oldPath; _newPath = newPath; _oldPassword = oldPassword; _newPassword = newPassword;
        OldName = Path.GetFileName(oldPath);
        NewName = Path.GetFileName(newPath);
        OnPropertyChanged(nameof(OldName));
        OnPropertyChanged(nameof(NewName));
        IsBusy = true;
        try
        {
            _old?.Dispose(); _new?.Dispose();
            _old = new PdfiumDocumentService();
            _new = new PdfiumDocumentService();
            await _old.OpenDocumentAsync(oldPath, oldPassword, ct);
            await _new.OpenDocumentAsync(newPath, newPassword, ct);
            await CompareAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIgnoreCaseChanged(bool value)
    {
        if (_old != null && _new != null) _ = RecompareAsync();
    }

    private async Task RecompareAsync()
    {
        IsBusy = true;
        try { await CompareAsync(CancellationToken.None); }
        finally { IsBusy = false; }
    }

    private async Task CompareAsync(CancellationToken ct)
    {
        using var engine = new PdfEngine.Pdfium.PdfiumEngine();
        var comparer = new PdfViewer.Core.Comparison.PdfComparisonService(engine.Renderer, engine.TextService);
        IReadOnlyList<TextChange> changes;
        await using (var a = await engine.OpenDocumentAsync(_oldPath!, _oldPassword, ct))
        await using (var b = await engine.OpenDocumentAsync(_newPath!, _newPassword, ct))
            changes = await comparer.CompareTextAsync(a, b, IgnoreCase, ct);

        BuildPages(_old!, OldPages);
        BuildPages(_new!, NewPages);
        Changes.Clear();
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            Changes.Add(new CompareChangeItem(i, c));
            foreach (var w in c.Old)
                if (w.PageNumber >= 1 && w.PageNumber <= OldPages.Count)
                    OldPages[w.PageNumber - 1].Highlights.Add(new CompareHighlight(new Rect(w.X, w.Y, w.Width, w.Height), c.Kind, i));
            foreach (var w in c.New)
                if (w.PageNumber >= 1 && w.PageNumber <= NewPages.Count)
                    NewPages[w.PageNumber - 1].Highlights.Add(new CompareHighlight(new Rect(w.X, w.Y, w.Width, w.Height), c.Kind, i));
        }
        int added = changes.Count(c => c.Kind == ChangeKind.Inserted), removed = changes.Count(c => c.Kind == ChangeKind.Deleted);
        int replaced = changes.Count - added - removed;
        Summary = changes.Count == 0
            ? $"The text is the same ({OldPages.Count} and {NewPages.Count} pages)."
            : $"{changes.Count} difference(s): {replaced} changed, {added} added, {removed} removed.";
    }

    private static void BuildPages(PdfiumDocumentService service, ObservableCollection<ComparePageViewModel> into)
    {
        into.Clear();
        for (int p = 1; p <= service.PageCount; p++)
        {
            var (w, h) = service.GetPageDimensions(p);
            into.Add(new ComparePageViewModel(service, p, w, h, service.GetPageIntrinsicRotation(p)));
        }
    }

    public void Dispose()
    {
        _old?.Dispose();
        _new?.Dispose();
        _old = _new = null;
    }
}
