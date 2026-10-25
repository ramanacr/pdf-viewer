using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Geometry;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PrintProduction;

namespace PdfViewer.ViewModels;

/// <summary>One plate in the Output Preview checklist: shown or hidden, its colour and its ink under the mouse.</summary>
public sealed partial class ColorantItem : ObservableObject
{
    private readonly Action _changed;

    public ColorantItem(int channel, PdfColorant colorant, Action changed)
    {
        Channel = channel;
        Name = colorant.Name;
        IsProcess = colorant.IsProcess;
        Appearance = colorant.Appearance;
        var c = colorant.Appearance;
        Swatch = new SolidColorBrush(Color.FromRgb((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255)));
        Swatch.Freeze();
        _changed = changed;
    }

    public int Channel { get; }
    public string Name { get; }
    public bool IsProcess { get; }
    public string Kind => IsProcess ? "Process" : "Spot";
    public PdfEngine.Vector.PdfColor Appearance { get; }
    public SolidColorBrush Swatch { get; }

    [ObservableProperty]
    private bool _isVisible = true;

    partial void OnIsVisibleChanged(bool value) => _changed();

    /// <summary>The plate's ink under the mouse, as "45 %", or empty.</summary>
    [ObservableProperty]
    private string _readout = string.Empty;

    public string AutomationName => $"{Name}, {Kind.ToLowerInvariant()} plate{(Readout.Length > 0 ? ", " + Readout : string.Empty)}";

    partial void OnReadoutChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}

/// <summary>
/// Output Preview (Acrobat Pro's Print Production tool): the page separated into its process and spot plates
/// by the engine, with plates shown or hidden, overprint simulated or not, areas over a total ink limit
/// highlighted, and each plate's ink under the mouse. Renders its own copy of the document; the main view
/// is untouched.
/// </summary>
public sealed partial class OutputPreviewViewModel : ObservableObject, IDisposable
{
    /// <summary>Largest side of the preview, in pixels.</summary>
    public const int PreviewPixels = 1100;

    private readonly byte[] _bytes;
    private readonly string _path;
    private readonly string? _password;
    private readonly Func<IPdfDisplayListRasterizer>? _rasterizerFactory;
    private PdfVectorDocument? _doc;
    private IPdfDisplayListRasterizer? _rasterizer;
    private PdfSeparation? _separation;
    private CancellationTokenSource? _render;
    private int _renderedPage;

    public OutputPreviewViewModel(byte[] bytes, string path, string? password, int page, Func<IPdfDisplayListRasterizer>? rasterizerFactory = null)
    {
        _bytes = bytes;
        _path = path;
        _password = password;
        _pageNumber = Math.Max(1, page);
        _rasterizerFactory = rasterizerFactory;
    }

    public string FileName => Path.GetFileName(_path);

    public ObservableCollection<ColorantItem> Colorants { get; } = new();

    [ObservableProperty]
    private int _pageNumber;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _simulateOverprint = true;

    [ObservableProperty]
    private bool _showInkWarning;

    [ObservableProperty]
    private double _inkLimit = 300;

    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _readout = "Point at the page to read the ink under the mouse.";

    [ObservableProperty]
    private bool _isBusy;

    partial void OnSimulateOverprintChanged(bool value) => _ = RenderAsync();
    partial void OnShowInkWarningChanged(bool value) => Compose();
    partial void OnInkLimitChanged(double value)
    {
        if (value is < 1 or > 6400 || double.IsNaN(value)) { InkLimit = double.IsNaN(value) ? 300 : Math.Clamp(value, 1, 6400); return; }
        if (ShowInkWarning) Compose();
        else UpdateSummary();
    }

    partial void OnPageNumberChanged(int value)
    {
        if (PageCount > 0 && (value < 1 || value > PageCount)) { PageNumber = Math.Clamp(value, 1, PageCount); return; }
        if (value != _renderedPage && _doc != null) _ = RenderAsync();
    }

    public int PreviewWidth => _separation?.Width ?? 0;
    public int PreviewHeight => _separation?.Height ?? 0;

    /// <summary>The plates shown, as a mask of the model's channels.</summary>
    public ulong VisibleMask => Colorants.Where(c => c.IsVisible).Aggregate(0UL, (m, c) => m | 1UL << c.Channel);

    /// <summary>The current separation (for tests and the readout).</summary>
    public PdfSeparation? Separation => _separation;

    /// <summary>Separates the current page and shows it.</summary>
    public async Task RenderAsync()
    {
        _render?.Cancel();
        var cts = _render = new CancellationTokenSource();
        IsBusy = true;
        Summary = "Separating the page...";
        try
        {
            _doc ??= await PdfVectorDocument.OpenAsync(_bytes, _path, password: _password);
            _rasterizer ??= _rasterizerFactory?.Invoke() ?? new Direct2DDisplayListRasterizer();
            PageCount = _doc.PageCount;
            int page = Math.Clamp(PageNumber, 1, PageCount);
            var doc = _doc;
            var rasterizer = _rasterizer;
            bool overprint = SimulateOverprint;
            var hidden = Colorants.Where(c => !c.IsVisible).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            var (inventory, separation) = await Task.Run(async () =>
            {
                var inv = await PdfColorantInventory.ReadAsync(doc, [page], cts.Token);
                var node = doc.PageTree.Pages[page - 1];
                var crop = node.CropBox.IsEmpty ? new PdfRect(0, 0, node.PageSize.Width, node.PageSize.Height) : node.CropBox;
                bool sideways = ((node.RotationDegrees % 360) + 360) % 360 is 90 or 270;
                double w = sideways ? crop.Height : crop.Width, h = sideways ? crop.Width : crop.Height;
                double scale = PreviewPixels / Math.Max(1, Math.Max(w, h));
                int pw = Math.Max(1, (int)Math.Round(w * scale)), ph = Math.Max(1, (int)Math.Round(h * scale));
                var sep = await PdfSeparationRenderer.RenderAsync(doc, page, pw, ph, rasterizer,
                    new PdfSeparationOptions { Model = inv.CreateModel(), SimulateOverprint = overprint }, cts.Token);
                return (inv, sep);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            _separation = separation;
            _renderedPage = page;
            Colorants.Clear();
            int channel = 0;
            foreach (var colorant in inventory.All)
            {
                var item = new ColorantItem(channel++, colorant, Compose) { IsVisible = !hidden.Contains(colorant.Name) };
                Colorants.Add(item);
            }
            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
            Compose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is PdfVectorException or InvalidOperationException or IOException or ArgumentException)
        {
            Summary = $"The page could not be separated: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_render, cts)) IsBusy = false;
        }
    }

    /// <summary>Mixes the visible plates into the preview (no new render).</summary>
    public void Compose()
    {
        if (_separation is not { } s) return;
        var spots = Colorants.Where(c => !c.IsProcess).Select(c => c.Appearance).ToList();
        var bgra = s.CompositeToBgra(VisibleMask, spots, ShowInkWarning ? InkLimit : null);
        var bitmap = BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgr32, null, bgra, s.Width * 4);
        bitmap.Freeze();
        Preview = bitmap;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_separation is not { } s) return;
        var coverage = s.Coverage(InkLimit, VisibleMask);
        string spots = Colorants.Count(c => !c.IsProcess) switch { 0 => "no spot colours", 1 => "1 spot colour", var n => $"{n} spot colours" };
        string over = coverage.PixelsOver == 0
            ? $"nothing over {InkLimit:0} %"
            : $"{coverage.FractionOver * 100:0.##} % of the page over {InkLimit:0} %";
        Summary = $"Page {_renderedPage}: {spots}; total ink up to {coverage.MaxPercent:0} % ({over}). "
                  + (s.SimulatedOverprint ? "Overprinting simulated." : "Overprinting not simulated: every object knocks out what is under it.")
                  + (s.HasFallback ? " Parts of the page are drawn approximately." : string.Empty);
    }

    /// <summary>Shows the ink of every plate at a preview pixel (from its top-left).</summary>
    public void ReadAt(int x, int y)
    {
        if (_separation is not { } s || x < 0 || y < 0 || x >= s.Width || y >= s.Height)
        {
            ClearReadout();
            return;
        }
        foreach (var item in Colorants)
            item.Readout = $"{s.InkAt(item.Channel, x, y) * 100:0} %";
        double total = s.TotalInkAt(x, y, VisibleMask);
        Readout = string.Join("  ", Colorants.Select(c => $"{Abbreviation(c)} {c.Readout}")) + $"  Total {total:0} %";
    }

    public void ClearReadout()
    {
        foreach (var item in Colorants) item.Readout = string.Empty;
        Readout = "Point at the page to read the ink under the mouse.";
    }

    private static string Abbreviation(ColorantItem item) => item.Name switch
    {
        "Cyan" => "C",
        "Magenta" => "M",
        "Yellow" => "Y",
        "Black" => "K",
        _ => item.Name,
    };

    public void Dispose()
    {
        _render?.Cancel();
        (_rasterizer as IDisposable)?.Dispose();
        _rasterizer = null;
        _doc?.Dispose();
        _doc = null;
    }
}
