using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PdfViewer.Services;
using PdfViewer.Services.Scanning;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the Create PDF from Images or Combine Files window (set by the main window).</summary>
    public Action<CreatePdfViewModel>? ShowCreatePdfAction { get; set; }

    /// <summary>Shows the scan window (set by the main window).</summary>
    public Action<ScanViewModel>? ShowScanAction { get; set; }

    /// <summary>Chooses the file for Create PDF from File (tests set it; null: the open dialog).</summary>
    public Func<string?>? PickCreateSourceFunc { get; set; }

    /// <summary>Chooses where a new PDF goes, from a suggested path (tests set it; null: the save dialog).</summary>
    public Func<string, string?>? ChooseCreateTargetFunc { get; set; }

    /// <summary>The scanners to use instead of Windows Image Acquisition (tests).</summary>
    public IScannerService? ScannerServiceOverride { get; set; }

    /// <summary>The Office conversion to use instead of Microsoft Office (tests).</summary>
    public IOfficeConverter? OfficeConverterOverride { get; set; }

    /// <summary>Create PDF from File is running.</summary>
    [ObservableProperty]
    private bool _isCreatingPdf;

    private CancellationTokenSource? _createCts;

    private static string ProducerName()
    {
        var v = typeof(MainViewModel).Assembly.GetName().Version;
        return v == null ? "PDF Viewer Native" : $"PDF Viewer Native {v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>The Create PDF pipeline with this tab's recognizer, Office and password prompt.</summary>
    public CreatePdfService NewCreatePdfService() => new()
    {
        Ocr = OcrEngineOverride ?? _opticalEngine,
        Office = OfficeConverterOverride ?? new OfficeConverter(),
        RequestPassword = RequestPasswordFunc,
        Producer = ProducerName(),
    };

    private bool CreateOcrAvailable => (OcrEngineOverride ?? _opticalEngine) != null;

    public CreatePdfViewModel CreateCreatePdfViewModel(CreatePdfMode mode) => new(mode, NewCreatePdfService(), CreateOcrAvailable)
    {
        ChooseTarget = ChooseCreateTargetFunc ?? (suggested => ChooseNewPdf(suggested, mode == CreatePdfMode.Images ? "Create PDF from Images" : "Combine Files")),
        OpenCreated = OpenRequestedDocumentAsync,
    };

    public ScanViewModel CreateScanViewModel() => new(ScannerServiceOverride ?? new WiaScannerService(), NewCreatePdfService(), CreateOcrAvailable)
    {
        ChooseTarget = ChooseCreateTargetFunc ?? (suggested => ChooseNewPdf(suggested, "Create PDF from Scanner")),
        OpenCreated = OpenRequestedDocumentAsync,
    };

    /// <summary>File &gt; Create PDF &gt; From Images: pictures to pages, a new document opened in a tab.</summary>
    [RelayCommand]
    public void CreatePdfFromImages() => ShowCreatePdfAction?.Invoke(CreateCreatePdfViewModel(CreatePdfMode.Images));

    /// <summary>File &gt; Create PDF &gt; Combine Files: PDFs, pictures and Office files into one, a bookmark each.</summary>
    [RelayCommand]
    public void CombineFiles() => ShowCreatePdfAction?.Invoke(CreateCreatePdfViewModel(CreatePdfMode.Combine));

    /// <summary>File &gt; Create PDF &gt; From Scanner: Windows Image Acquisition, flatbed or feeder.</summary>
    [RelayCommand]
    public void CreatePdfFromScanner()
    {
        if (ScannerServiceOverride == null && !WiaScannerService.IsAvailable)
        {
            ShowAlert("Windows Image Acquisition is not available on this computer, so scanners cannot be used. " +
                      "On Windows Server, add the \"Windows Image Acquisition\" feature first.",
                "Create PDF from Scanner", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowScanAction?.Invoke(CreateScanViewModel());
    }

    /// <summary>
    /// File &gt; Create PDF &gt; From File: a picture, a text file, or anything Office opens
    /// (Word, Excel and PowerPoint files, RTF, HTML) converted on this machine and opened in a new
    /// tab. Running it again while it runs stops it.
    /// </summary>
    [RelayCommand]
    public async Task CreatePdfFromFileAsync()
    {
        if (_createCts != null)
        {
            _createCts.Cancel();
            return;
        }
        var service = NewCreatePdfService();
        string? source = PickCreateSourceFunc != null ? PickCreateSourceFunc() : PickCreateSource();
        if (string.IsNullOrEmpty(source)) return;
        if (CreatePdfService.IsPdf(source))
        {
            await OpenRequestedDocumentAsync(source); // already a PDF
            return;
        }
        if (!service.CanTake(source))
        {
            ShowAlert($"'{Path.GetFileName(source)}' is not a kind of file Create PDF can convert. Pictures, text files and files Microsoft Office opens can be.",
                "Create PDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string suggested = Path.ChangeExtension(source, ".pdf");
        string? target = ChooseCreateTargetFunc != null ? ChooseCreateTargetFunc(suggested) : ChooseNewPdf(suggested, "Create PDF from File");
        if (string.IsNullOrEmpty(target)) return;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return;

        IsCreatingPdf = true;
        _createCts = new CancellationTokenSource();
        var progress = new Progress<CreatePdfProgress>(p => StatusText = p.Message + " (run again to stop)");
        try
        {
            var settings = new CreatePdfOptionsViewModel { IsOcrAvailable = CreateOcrAvailable }.ToSettings();
            var report = await service.FromFileAsync(source, target, settings, progress, _createCts.Token);
            StatusText = CreatePdfViewModel.Describe(report, Path.GetFileName(target));
            await OpenRequestedDocumentAsync(target);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Create PDF stopped; no file was written.";
        }
        catch (Exception ex) when (ex is OfficeConversionException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or NotSupportedException or ArgumentException)
        {
            StatusText = "Create PDF failed.";
            ShowAlert($"The PDF could not be created:\n\n{ex.Message}", "Create PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            IsCreatingPdf = false;
            _createCts?.Dispose();
            _createCts = null;
        }
    }

    private string? PickCreateSource()
    {
        string office = string.Join(";", OfficeConverter.SupportedExtensions.Select(e => "*" + e));
        const string images = "*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif;*.webp;*.heic";
        var dialog = new OpenFileDialog
        {
            Title = "Create PDF from File",
            Filter = $"Supported files|{office};{images}|Office and text files|{office}|Pictures|{images}|All files|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? ChooseNewPdf(string suggested, string title)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "PDF files (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested) ?? string.Empty,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
