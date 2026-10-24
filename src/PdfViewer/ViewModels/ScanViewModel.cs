using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Windows.Create;
using PdfViewer.Services;
using PdfViewer.Services.Scanning;

namespace PdfViewer.ViewModels;

/// <summary>
/// Create PDF from a scanner: choose the scanner, colour, resolution, flatbed or feeder (both
/// sides when it can), scan as many times as needed (each scan adds its pages), then create the
/// PDF, made searchable when asked. Scanned pages wait in a private temporary folder, not in
/// memory, and are deleted when the PDF is made or the window closes.
/// </summary>
public sealed partial class ScanViewModel : ObservableObject, IDisposable
{
    private readonly IScannerService _scanner;
    private readonly CreatePdfService _service;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PdfViewerScan_" + Guid.NewGuid().ToString("N"));
    private CancellationTokenSource? _cts;

    public ScanViewModel(IScannerService scanner, CreatePdfService service, bool ocrAvailable)
    {
        _scanner = scanner;
        _service = service;
        // Scans are photographs of paper: JPEG keeps them small. Black and white stays one bit a pixel, losslessly.
        Options = new CreatePdfOptionsViewModel { IsOcrAvailable = ocrAvailable, UseJpeg = true, JpegQuality = 80 };
        Pages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(PagesText));
        };
    }

    public CreatePdfOptionsViewModel Options { get; }

    public ObservableCollection<ScannerDevice> Devices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan), nameof(CanUseFeeder), nameof(CanDuplex))]
    private ScannerDevice? _selectedDevice;

    public IReadOnlyList<string> ColorModes { get; } = new[] { "Colour", "Greyscale", "Black and white" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorModeIndex))]
    private ScanColorMode _colorMode = ScanColorMode.Color;

    /// <summary><see cref="ColorMode"/> as the index of <see cref="ColorModes"/>, for the list.</summary>
    public int ColorModeIndex
    {
        get => (int)ColorMode;
        set => ColorMode = (ScanColorMode)Math.Clamp(value, 0, 2);
    }

    public IReadOnlyList<int> Resolutions { get; } = new[] { 100, 150, 200, 300, 400, 600 };

    [ObservableProperty]
    private int _dpi = 300;

    /// <summary>Scan from the document feeder (every page in it) rather than the glass.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDuplex))]
    private bool _useFeeder;

    [ObservableProperty]
    private bool _duplex;

    public bool CanUseFeeder => SelectedDevice?.HasFeeder == true;
    public bool CanDuplex => UseFeeder && SelectedDevice?.HasDuplex == true;

    partial void OnSelectedDeviceChanged(ScannerDevice? value)
    {
        UseFeeder = value is { HasFeeder: true, HasFlatbed: false };
        if (value?.HasDuplex != true) Duplex = false;
        if (value is { CapabilitiesKnown: false }) _ = ReadCapabilitiesAsync(value);
    }

    /// <summary>Asks the scanner what it has (feeder, duplex) in the background; scanning does not wait for it.</summary>
    private async Task ReadCapabilitiesAsync(ScannerDevice device)
    {
        string before = Status;
        Status = $"Connecting to {device.Name}...";
        try
        {
            var known = await _scanner.GetCapabilitiesAsync(device);
            int index = Devices.IndexOf(device);
            if (index < 0) return;
            bool selected = SelectedDevice == device;
            Devices[index] = known;
            if (selected) SelectedDevice = known;
            Status = before;
        }
        catch (ScannerUnavailableException ex)
        {
            if (SelectedDevice == device) Status = ex.Message;
        }
    }

    /// <summary>The scanned pages, as files in the private folder, in order.</summary>
    public ObservableCollection<string> Pages { get; } = new();

    public int PageCount => Pages.Count;
    public string PagesText => Pages.Count == 0 ? "No pages scanned yet." : Pages.Count == 1 ? "1 page scanned." : $"{Pages.Count} pages scanned.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan), nameof(CanCreate))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _progress;

    public bool CanScan => SelectedDevice != null && !IsBusy;
    public bool CanCreate => Pages.Count > 0 && !IsBusy;

    public Func<string, string?>? ChooseTarget { get; set; }
    public Func<string, Task>? OpenCreated { get; set; }

    public async Task LoadDevicesAsync()
    {
        Devices.Clear();
        IsBusy = true;
        Status = "Looking for scanners...";
        try
        {
            foreach (var d in await _scanner.GetDevicesAsync()) Devices.Add(d);
            SelectedDevice = Devices.FirstOrDefault();
            Status = Devices.Count == 0
                ? "No scanner was found. Connect and switch on a scanner (its driver comes from its maker or Windows Update), then choose Refresh."
                : Devices.Count == 1 ? $"Ready to scan with {Devices[0].Name}." : $"{Devices.Count} scanners found.";
        }
        catch (ScannerUnavailableException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Scans with the current settings; the pages are added after those already scanned.</summary>
    public async Task<int> ScanAsync()
    {
        if (!CanScan) return 0;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = UseFeeder ? "Scanning the pages in the feeder..." : "Scanning...";
        Directory.CreateDirectory(_folder);
        int before = Pages.Count;
        var arrived = new List<string>();
        try
        {
            var settings = new ScanSettings(SelectedDevice!.Id, ColorMode, Dpi, UseFeeder ? ScanSource.Feeder : ScanSource.Flatbed, CanDuplex && Duplex);
            bool abandoned = false;
            var scan = _scanner.ScanAsync(settings, page =>
            {
                // Called on the scanner's thread: the page goes to disk at once.
                lock (arrived)
                {
                    if (abandoned) return;
                    string path = Path.Combine(_folder, $"page{before + arrived.Count + 1:D4}.img");
                    File.WriteAllBytes(path, page.ImageFile);
                    arrived.Add(path);
                }
            }, _cts.Token);
            // A driver can sit in one call for minutes (a network scanner that is off): Stop does not wait for it.
            await Task.WhenAny(scan, Task.Delay(Timeout.Infinite, _cts.Token));
            if (!scan.IsCompleted)
            {
                lock (arrived) abandoned = true;
                _ = scan.ContinueWith(t => t.Exception, TaskScheduler.Default);
                throw new OperationCanceledException();
            }
            int added = await scan;
            Status = added == 0 ? "Nothing was scanned." : $"Scanned {added} page(s). Scan more, or create the PDF.";
            return added;
        }
        catch (OperationCanceledException)
        {
            Status = "Scanning stopped. The pages scanned so far are kept.";
            return arrived.Count;
        }
        catch (ScannerUnavailableException ex)
        {
            Status = ex.Message;
            return arrived.Count;
        }
        catch (IOException ex)
        {
            Status = $"A scanned page could not be kept: {ex.Message}";
            return arrived.Count;
        }
        finally
        {
            foreach (var p in arrived) Pages.Add(p);
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    public void RemoveLastPage()
    {
        if (Pages.Count == 0 || IsBusy) return;
        string last = Pages[^1];
        Pages.RemoveAt(Pages.Count - 1);
        TryDelete(last);
    }

    public string SuggestedPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Scan {DateTime.Now:yyyy-MM-dd HHmm}.pdf");

    public async Task<bool> CreateAndOpenAsync()
    {
        if (!CanCreate) return false;
        string? target = ChooseTarget?.Invoke(SuggestedPath);
        if (string.IsNullOrEmpty(target)) return false;
        if (!await CreateAsync(target)) return false;
        if (OpenCreated != null) await OpenCreated(target);
        return true;
    }

    public async Task<bool> CreateAsync(string target)
    {
        if (!CanCreate) return false;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<CreatePdfProgress>(p =>
        {
            Status = p.Message;
            if (p.Fraction >= 0) Progress = 100 * p.Fraction;
        });
        try
        {
            var inputs = Pages.Select((p, i) => ImageInput.FromFile(p) with { Name = $"Page {i + 1}" }).ToList();
            var report = await _service.FromImagesAsync(inputs, target, Options.ToSettings(), progress, _cts.Token);
            Progress = 100;
            Status = CreatePdfViewModel.Describe(report, Path.GetFileName(target));
            foreach (var p in Pages) TryDelete(p);
            Pages.Clear();
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped: no file was written. The scanned pages are kept.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Status = $"The PDF could not be created: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
