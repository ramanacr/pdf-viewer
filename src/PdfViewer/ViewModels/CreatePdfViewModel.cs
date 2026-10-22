using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Vector.Create;
using PdfEngine.Vector.Windows.Create;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>The choices every way of creating a PDF shares: page size, image quality and colour, recognition, PDF/A.</summary>
public sealed partial class CreatePdfOptionsViewModel : ObservableObject
{
    public const string FitToImage = "Fit to image";

    public IReadOnlyList<string> PageSizes { get; } = new[] { FitToImage, "A4", "Letter", "Legal" };

    /// <summary>"Fit to image" keeps each picture at its own size; a paper size puts it inside the margins.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPaper))]
    private string _pageSize = FitToImage;

    public bool UsesPaper => PageSize != FitToImage;

    /// <summary>The margin on each side of the paper, in millimetres.</summary>
    [ObservableProperty]
    private double _marginMillimetres = 10;

    /// <summary>Cover the paper inside the margins, cropping what overflows, rather than fit inside them.</summary>
    [ObservableProperty]
    private bool _fillPage;

    /// <summary>Compress pictures again as JPEG (smaller files) instead of keeping them as they are or lossless.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityIndex))]
    private bool _useJpeg;

    public IReadOnlyList<string> Qualities { get; } = new[] { "Keep JPEGs as they are, others lossless", "Compress as JPEG" };

    /// <summary><see cref="UseJpeg"/> as the index of <see cref="Qualities"/>, for the list.</summary>
    public int QualityIndex
    {
        get => UseJpeg ? 1 : 0;
        set => UseJpeg = value == 1;
    }

    [ObservableProperty]
    private int _jpegQuality = 85;

    [ObservableProperty]
    private bool _grayscale;

    [ObservableProperty]
    private bool _makeSearchable;

    [ObservableProperty]
    private bool _convertToPdfA;

    /// <summary>Windows OCR has a language installed, so Make Searchable can be offered.</summary>
    public bool IsOcrAvailable { get; init; }

    /// <summary>The paper plain text goes on when the page size says nothing (A4, or Letter where that is the custom).</summary>
    public static (double Width, double Height) DefaultPaper
    {
        get
        {
            try { return RegionInfo.CurrentRegion.IsMetric ? PdfImagePageOptions.A4 : PdfImagePageOptions.Letter; }
            catch (ArgumentException) { return PdfImagePageOptions.A4; }
        }
    }

    public CreatePdfSettings ToSettings()
    {
        var paper = PageSize switch
        {
            "Letter" => PdfImagePageOptions.Letter,
            "Legal" => PdfImagePageOptions.Legal,
            "A4" => PdfImagePageOptions.A4,
            _ => DefaultPaper,
        };
        double margin = Math.Clamp(MarginMillimetres, 0, 100) * 72 / 25.4;
        return new CreatePdfSettings
        {
            Page = new PdfImagePageOptions
            {
                Size = UsesPaper ? PdfImagePageSize.Paper : PdfImagePageSize.FitImage,
                PaperWidth = paper.Width, PaperHeight = paper.Height, Margin = margin, Fill = FillPage,
            },
            Image = new ImageConversionOptions
            {
                Compression = UseJpeg ? ImageCompression.Jpeg : ImageCompression.Automatic,
                JpegQuality = Math.Clamp(JpegQuality, 10, 100),
                Color = Grayscale ? ImageColorMode.Grayscale : ImageColorMode.Keep,
            },
            MakeSearchable = MakeSearchable && IsOcrAvailable,
            PdfA = ConvertToPdfA,
            Text = new PdfTextLayoutOptions { PageWidth = paper.Width, PageHeight = paper.Height },
        };
    }
}

/// <summary>A file in the list to create from (the same file can be listed twice: each entry is its own).</summary>
public sealed class CreatePdfItem(string path, string kind)
{
    public string Path { get; } = path;
    public string Kind { get; } = kind;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

public enum CreatePdfMode
{
    /// <summary>Images to pages.</summary>
    Images,
    /// <summary>PDFs, images and Office files into one document.</summary>
    Combine,
}

/// <summary>
/// Create PDF from images, and Combine Files: the list of files (added, reordered by dragging
/// or with the buttons, removed), the options, and the run with its progress and a Cancel.
/// </summary>
public sealed partial class CreatePdfViewModel : ObservableObject
{
    private readonly CreatePdfService _service;
    private CancellationTokenSource? _cts;

    public CreatePdfViewModel(CreatePdfMode mode, CreatePdfService service, bool ocrAvailable)
    {
        Mode = mode;
        _service = service;
        Options = new CreatePdfOptionsViewModel { IsOcrAvailable = ocrAvailable };
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(SummaryText));
        };
    }

    public CreatePdfMode Mode { get; }
    public CreatePdfOptionsViewModel Options { get; }
    public ObservableCollection<CreatePdfItem> Items { get; } = new();

    public string Title => Mode == CreatePdfMode.Images ? "Create PDF from Images" : "Combine Files";

    public string Intro => Mode == CreatePdfMode.Images
        ? "Each picture becomes a page (each page of a multi-page TIFF too), in the order below. Drag to reorder."
        : "PDFs, pictures and Office files become one PDF, in the order below, with a bookmark for each file. Drag to reorder.";

    /// <summary>The open-file filter for adding files.</summary>
    public string FileFilter
    {
        get
        {
            const string images = "*.jpg;*.jpeg;*.jfif;*.png;*.tif;*.tiff;*.bmp;*.gif;*.webp;*.heic;*.heif;*.jxr";
            if (Mode == CreatePdfMode.Images) return $"Pictures|{images}|All files|*.*";
            string office = string.Join(";", OfficeConverter.SupportedExtensions.Select(e => "*" + e));
            return $"All supported files|*.pdf;{images};{office}|PDF files|*.pdf|Pictures|{images}|Office and text files|{office}|All files|*.*";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The file written, once created.</summary>
    public string? CreatedPath { get; private set; }

    public bool CanCreate => Items.Count > 0 && !IsBusy;

    public string SummaryText => Items.Count == 0 ? "No files yet." : Items.Count == 1 ? "1 file." : $"{Items.Count} files.";

    /// <summary>Chooses where the new PDF goes from a suggested name (the window shows a save dialog; tests answer directly).</summary>
    public Func<string, string?>? ChooseTarget { get; set; }

    /// <summary>Opens the created document (in a new tab).</summary>
    public Func<string, Task>? OpenCreated { get; set; }

    public string SuggestedPath
    {
        get
        {
            var first = Items.FirstOrDefault();
            string name = first == null ? (Mode == CreatePdfMode.Images ? "Images" : "Combined")
                : Mode == CreatePdfMode.Combine && Items.Count > 1 ? "Combined" : System.IO.Path.GetFileNameWithoutExtension(first.Path);
            return System.IO.Path.Combine(first?.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), name + ".pdf");
        }
    }

    /// <summary>Adds the files this mode takes; returns the names of those it does not.</summary>
    public IReadOnlyList<string> AddFiles(IEnumerable<string> paths)
    {
        var refused = new List<string>();
        foreach (var path in paths)
        {
            string? kind = KindOf(path);
            if (kind == null) refused.Add(System.IO.Path.GetFileName(path));
            else Items.Add(new CreatePdfItem(path, kind));
        }
        Status = refused.Count == 0 ? string.Empty
            : $"Not added ({(Mode == CreatePdfMode.Images ? "not a picture" : "not a kind of file that can be combined")}): {string.Join(", ", refused)}";
        return refused;
    }

    private string? KindOf(string path)
    {
        if (CreatePdfService.IsImage(path)) return "Picture";
        if (Mode == CreatePdfMode.Images) return null;
        if (CreatePdfService.IsPdf(path)) return "PDF";
        if (CreatePdfService.IsText(path)) return "Text";
        return _service.Office.AppFor(System.IO.Path.GetExtension(path)) is { } app ? app.ToString() : null;
    }

    /// <summary>Moves an item to another place in the list (drag and drop, the arrow buttons).</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count || to < 0 || to >= Items.Count || from == to) return;
        Items.Move(from, to);
    }

    public void MoveUp(CreatePdfItem item) => Move(Items.IndexOf(item), Items.IndexOf(item) - 1);
    public void MoveDown(CreatePdfItem item) => Move(Items.IndexOf(item), Items.IndexOf(item) + 1);
    public void Remove(CreatePdfItem item) => Items.Remove(item);

    public void Cancel() => _cts?.Cancel();

    /// <summary>Asks where to save, creates the PDF, and opens it. Returns whether it was created.</summary>
    public async Task<bool> CreateAndOpenAsync()
    {
        if (!CanCreate) return false;
        string? target = ChooseTarget?.Invoke(SuggestedPath);
        if (string.IsNullOrEmpty(target)) return false;
        if (!await CreateAsync(target)) return false;
        if (OpenCreated != null) await OpenCreated(target);
        return true;
    }

    /// <summary>Creates the PDF at <paramref name="target"/>; a source in the list is never overwritten.</summary>
    public async Task<bool> CreateAsync(string target)
    {
        if (!CanCreate) return false;
        string full = System.IO.Path.GetFullPath(target);
        if (Items.Any(i => string.Equals(System.IO.Path.GetFullPath(i.Path), full, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Choose another file name: a file in the list would be overwritten.";
            return false;
        }
        IsBusy = true;
        Progress = 0;
        IsProgressIndeterminate = false;
        _cts = new CancellationTokenSource();
        bool finished = false;
        // Progress<T> posts each report, so one can arrive after the run ends; it must not
        // overwrite the final status.
        var progress = new Progress<CreatePdfProgress>(p =>
        {
            if (finished) return;
            Status = p.Message;
            IsProgressIndeterminate = p.Fraction < 0;
            if (p.Fraction >= 0) Progress = 100 * p.Fraction;
        });
        try
        {
            var settings = Options.ToSettings();
            var paths = Items.Select(i => i.Path).ToList();
            var report = Mode == CreatePdfMode.Images
                ? await _service.FromImagesAsync(paths.Select(ImageInput.FromFile).ToList(), target, settings, progress, _cts.Token)
                : await _service.CombineAsync(paths, target, settings, progress, _cts.Token);
            finished = true;
            CreatedPath = target;
            Progress = 100;
            IsProgressIndeterminate = false;
            Status = Describe(report, System.IO.Path.GetFileName(target));
            return true;
        }
        catch (OperationCanceledException)
        {
            finished = true;
            Status = "Stopped: no file was written.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                       or ArgumentException or OfficeConversionException or PdfEngine.Vector.Diagnostics.PdfVectorException)
        {
            finished = true;
            Status = $"The PDF could not be created: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    internal static string Describe(CreatePdfReport report, string name)
    {
        var parts = new List<string> { $"Created {name}: {report.Pages} page(s)" };
        if (report.JpegPassThrough > 0) parts.Add($"{report.JpegPassThrough} JPEG(s) kept as they were");
        if (report.RecognizedPages > 0) parts.Add($"text recognized on {report.RecognizedPages} page(s)");
        if (report.PdfA) parts.Add(report.PdfAIssues == 0 ? "PDF/A-2b" : $"PDF/A-2b with {report.PdfAIssues} rule(s) not met");
        return string.Join(", ", parts) + ".";
    }
}
