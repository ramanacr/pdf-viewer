using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Vector.Editing;

namespace PdfViewer.ViewModels;

/// <summary>What a page-mark dialog asks for: add, update or remove marks of its kind, with the design to draw.</summary>
public sealed record PageMarkRequest(PdfMarkAction Action, PdfMarkKind Kind, PdfHeaderFooter? HeaderFooter, PdfWatermark? Watermark);

/// <summary>One of the six places a header or footer's text goes.</summary>
public enum PageMarkPosition { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>
/// What the Header &amp; Footer, Bates Numbering, Watermark and Background dialogs share: the page
/// range, the preview of the current page with the marks drawn, and Add, Update and Remove
/// (Update and Remove only when the document already has marks of the kind, Acrobat's or ours).
/// </summary>
public abstract partial class PageMarkDialogViewModel : ObservableObject
{
    private readonly Func<PageMarkRequest, int, CancellationToken, Task<ImageSource?>>? _preview;
    private CancellationTokenSource? _previewCts;
    private bool _suspendPreview;

    protected PageMarkDialogViewModel(PdfMarkKind kind, int pageCount, int currentPage, bool hasExisting,
        Func<PageMarkRequest, int, CancellationToken, Task<ImageSource?>>? preview)
    {
        Kind = kind;
        PageCount = Math.Max(1, pageCount);
        _previewPage = Math.Clamp(currentPage, 1, PageCount);
        _rangeTo = PageCount;
        HasExisting = hasExisting;
        _preview = preview;
    }

    public PdfMarkKind Kind { get; }
    public int PageCount { get; }
    public bool HasExisting { get; }
    public abstract string Title { get; }

    public IReadOnlyList<PdfPageSubset> Subsets { get; } = Enum.GetValues<PdfPageSubset>();

    /// <summary>The installed font families the text can be set in.</summary>
    public IReadOnlyList<string> FontFamilies { get; init; } = new[] { "Arial" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SomePages))]
    private bool _allPages = true;

    /// <summary>Only the pages from <see cref="RangeFrom"/> to <see cref="RangeTo"/>.</summary>
    public bool SomePages
    {
        get => !AllPages;
        set => AllPages = !value;
    }
    [ObservableProperty] private int _rangeFrom = 1;
    [ObservableProperty] private int _rangeTo;
    [ObservableProperty] private PdfPageSubset _subset = PdfPageSubset.All;
    [ObservableProperty] private int _previewPage;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private string _message = string.Empty;

    /// <summary>What was asked for, once Add, Update or Remove is chosen.</summary>
    public PageMarkRequest? Result { get; private set; }

    /// <summary>Raised when a choice closes the dialog.</summary>
    public event EventHandler? CloseRequested;

    protected PdfPageRange Range => AllPages && Subset == PdfPageSubset.All ? PdfPageRange.All
        : AllPages ? new PdfPageRange(1, null, Subset) : new PdfPageRange(Math.Max(1, RangeFrom), Math.Max(RangeFrom, RangeTo), Subset);

    /// <summary>The design as the controls describe it, or null with <see cref="Message"/> saying what is missing.</summary>
    protected abstract PageMarkRequest? Build(PdfMarkAction action);

    [RelayCommand]
    private void Add() => Finish(PdfMarkAction.Add);

    [RelayCommand(CanExecute = nameof(HasExisting))]
    private void Update() => Finish(PdfMarkAction.Update);

    [RelayCommand(CanExecute = nameof(HasExisting))]
    private void Remove()
    {
        Result = new PageMarkRequest(PdfMarkAction.Remove, Kind, null, null);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Finish(PdfMarkAction action)
    {
        if (Build(action) is not { } request) return;
        Result = request;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Draws the current page with the marks as they are set now.</summary>
    [RelayCommand]
    public Task RefreshPreviewAsync() => PreviewAsync(TimeSpan.Zero);

    // After a pause, so typing does not draw the page for every key.
    private async Task PreviewAsync(TimeSpan delay)
    {
        if (_preview == null) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        if (delay > TimeSpan.Zero)
        {
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
        var request = Build(PdfMarkAction.Add);
        if (request == null)
        {
            PreviewImage = null;
            return;
        }
        Message = string.Empty;
        try
        {
            var image = await _preview(ForPreview(request), PreviewPage, cts.Token);
            if (!cts.IsCancellationRequested) PreviewImage = image;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or IOException)
        {
            if (!cts.IsCancellationRequested) Message = "The preview could not be drawn: " + ex.Message;
        }
    }

    // Only the page shown is marked for the preview; a Bates number is the one that page gets.
    private PageMarkRequest ForPreview(PageMarkRequest r)
    {
        var only = new PdfPageRange(PreviewPage, PreviewPage);
        if (r.HeaderFooter is { } hf)
        {
            var pages = hf.Pages.Pages(PageCount).ToList();
            int index = pages.IndexOf(PreviewPage);
            if (index < 0) return r with { HeaderFooter = hf with { TopLeft = "", TopCenter = "", TopRight = "", BottomLeft = "", BottomCenter = "", BottomRight = "" } };
            return r with { HeaderFooter = hf with { Pages = only, Bates = hf.Bates is { } b ? b with { Start = b.Start + index } : null } };
        }
        if (r.Watermark is { } wm)
            return r with { Watermark = wm with { Pages = wm.Pages.Pages(PageCount).Contains(PreviewPage) ? only : new PdfPageRange(0, 0) } };
        return r;
    }

    [RelayCommand]
    private void PreviousPreviewPage()
    {
        if (PreviewPage > 1) PreviewPage--;
    }

    [RelayCommand]
    private void NextPreviewPage()
    {
        if (PreviewPage < PageCount) PreviewPage++;
    }

    /// <summary>Sets several properties without drawing the preview after each.</summary>
    protected void Quietly(Action set)
    {
        _suspendPreview = true;
        try { set(); }
        finally { _suspendPreview = false; }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_suspendPreview || e.PropertyName is nameof(PreviewImage) or nameof(Message)) return;
        _ = PreviewAsync(TimeSpan.FromMilliseconds(300));
    }

    protected static double[] Rgb(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex);
            return new[] { c.R / 255.0, c.G / 255.0, c.B / 255.0 };
        }
        catch (FormatException)
        {
            return new double[] { 0, 0, 0 };
        }
    }

    protected static string Hex(double r, double g, double b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}");
}

/// <summary>Header &amp; Footer: text in six places with page numbers, dates and the file name.</summary>
public partial class HeaderFooterViewModel : PageMarkDialogViewModel
{
    public HeaderFooterViewModel(int pageCount, int currentPage, bool hasExisting, PdfHeaderFooter? existing,
        Func<PageMarkRequest, int, CancellationToken, Task<ImageSource?>>? preview)
        : this(PdfMarkKind.HeaderFooter, pageCount, currentPage, hasExisting, existing, preview)
    {
    }

    protected HeaderFooterViewModel(PdfMarkKind kind, int pageCount, int currentPage, bool hasExisting, PdfHeaderFooter? existing,
        Func<PageMarkRequest, int, CancellationToken, Task<ImageSource?>>? preview)
        : base(kind, pageCount, currentPage, hasExisting, preview)
    {
        if (existing != null) Quietly(() => Load(existing));
    }

    public override string Title => "Header & Footer";

    [ObservableProperty] private string _topLeft = string.Empty;
    [ObservableProperty] private string _topCenter = string.Empty;
    [ObservableProperty] private string _topRight = string.Empty;
    [ObservableProperty] private string _bottomLeft = string.Empty;
    [ObservableProperty] private string _bottomCenter = string.Empty;
    [ObservableProperty] private string _bottomRight = string.Empty;
    [ObservableProperty] private string _fontFamily = "Arial";
    [ObservableProperty] private double _fontSize = 10;
    [ObservableProperty] private string _textColor = "#000000";
    [ObservableProperty] private double _topMargin = 36;
    [ObservableProperty] private double _bottomMargin = 36;
    [ObservableProperty] private double _leftMargin = 72;
    [ObservableProperty] private double _rightMargin = 72;
    [ObservableProperty] private int _startPageNumber = 1;
    [ObservableProperty] private string _dateFormat = "M/d/yyyy";

    /// <summary>The box the Insert buttons put their token into.</summary>
    [ObservableProperty] private PageMarkPosition _selectedBox = PageMarkPosition.BottomCenter;

    public IReadOnlyList<string> DateFormats { get; } = new[] { "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "d MMMM yyyy", "MMMM d, yyyy" };

    [RelayCommand] private void InsertPageNumber() => Insert("Page <<1>> of <<n>>");
    [RelayCommand] private void InsertDate() => Insert("<<Date>>");
    [RelayCommand] private void InsertFileName() => Insert("<<File>>");

    protected void Insert(string token)
    {
        string current = Box(SelectedBox);
        SetBox(SelectedBox, current.Length == 0 ? token : current + " " + token);
    }

    public string Box(PageMarkPosition p) => p switch
    {
        PageMarkPosition.TopLeft => TopLeft, PageMarkPosition.TopCenter => TopCenter, PageMarkPosition.TopRight => TopRight,
        PageMarkPosition.BottomLeft => BottomLeft, PageMarkPosition.BottomCenter => BottomCenter, _ => BottomRight,
    };

    public void SetBox(PageMarkPosition p, string text)
    {
        switch (p)
        {
            case PageMarkPosition.TopLeft: TopLeft = text; break;
            case PageMarkPosition.TopCenter: TopCenter = text; break;
            case PageMarkPosition.TopRight: TopRight = text; break;
            case PageMarkPosition.BottomLeft: BottomLeft = text; break;
            case PageMarkPosition.BottomCenter: BottomCenter = text; break;
            default: BottomRight = text; break;
        }
    }

    protected virtual void Load(PdfHeaderFooter hf)
    {
        TopLeft = hf.TopLeft; TopCenter = hf.TopCenter; TopRight = hf.TopRight;
        BottomLeft = hf.BottomLeft; BottomCenter = hf.BottomCenter; BottomRight = hf.BottomRight;
        FontFamily = hf.Format.FontFamily; FontSize = hf.Format.Size; TextColor = Hex(hf.Format.Red, hf.Format.Green, hf.Format.Blue);
        TopMargin = hf.TopMargin; BottomMargin = hf.BottomMargin; LeftMargin = hf.LeftMargin; RightMargin = hf.RightMargin;
        StartPageNumber = hf.StartPageNumber; DateFormat = hf.DateFormat;
        AllPages = hf.Pages.From <= 1 && hf.Pages.To == null;
        RangeFrom = hf.Pages.From; RangeTo = hf.Pages.To ?? PageCount; Subset = hf.Pages.Subset;
    }

    protected PdfHeaderFooter Design()
    {
        var rgb = Rgb(TextColor);
        return new PdfHeaderFooter
        {
            TopLeft = TopLeft, TopCenter = TopCenter, TopRight = TopRight, BottomLeft = BottomLeft, BottomCenter = BottomCenter, BottomRight = BottomRight,
            Format = new PdfTextFormat(FontFamily, FontSize > 0 ? FontSize : 10, Red: rgb[0], Green: rgb[1], Blue: rgb[2]),
            TopMargin = TopMargin, BottomMargin = BottomMargin, LeftMargin = LeftMargin, RightMargin = RightMargin,
            StartPageNumber = StartPageNumber, DateFormat = DateFormat, Pages = Range,
        };
    }

    protected override PageMarkRequest? Build(PdfMarkAction action)
    {
        var design = Design();
        if (design.IsEmpty)
        {
            Message = "Type the text of at least one header or footer.";
            return null;
        }
        return new PageMarkRequest(action, Kind, design, null);
    }
}

/// <summary>Bates Numbering: a prefix, a number of fixed digits and a suffix, in one of the six places.</summary>
public partial class BatesNumberingViewModel : HeaderFooterViewModel
{
    public BatesNumberingViewModel(int pageCount, int currentPage, bool hasExisting, PdfHeaderFooter? existing,
        Func<PageMarkRequest, int, CancellationToken, Task<ImageSource?>>? preview)
        : base(PdfMarkKind.Bates, pageCount, currentPage, hasExisting, existing, preview)
    {
    }

    public override string Title => "Bates Numbering";

    public IReadOnlyList<PageMarkPosition> Positions { get; } = Enum.GetValues<PageMarkPosition>();

    [ObservableProperty] private PageMarkPosition _position = PageMarkPosition.BottomRight;
    [ObservableProperty] private string _prefix = string.Empty;
    [ObservableProperty] private string _suffix = string.Empty;
    [ObservableProperty] private long _start = 1;
    [ObservableProperty] private int _digits = 6;

    /// <summary>The number the first page gets, as it will show.</summary>
    public string Sample => new PdfBatesNumbering(Prefix, Suffix, Start, Digits).Format(Start);

    partial void OnPrefixChanged(string value) => OnPropertyChanged(nameof(Sample));
    partial void OnSuffixChanged(string value) => OnPropertyChanged(nameof(Sample));
    partial void OnStartChanged(long value) => OnPropertyChanged(nameof(Sample));
    partial void OnDigitsChanged(int value) => OnPropertyChanged(nameof(Sample));

    protected override void Load(PdfHeaderFooter hf)
    {
        base.Load(hf);
        if (hf.Bates is { } b)
        {
            Prefix = b.Prefix; Suffix = b.Suffix; Start = b.Start; Digits = b.Digits;
        }
        Position = Positions.FirstOrDefault(p => Box(p).Contains("<<Bates>>", StringComparison.OrdinalIgnoreCase), PageMarkPosition.BottomRight);
    }

    protected override PageMarkRequest? Build(PdfMarkAction action)
    {
        if (Digits is < 1 or > 20)
        {
            Message = "The number of digits must be between 1 and 20.";
            return null;
        }
        var design = Design() with { TopLeft = "", TopCenter = "", TopRight = "", BottomLeft = "", BottomCenter = "", BottomRight = "" };
        design = Position switch
        {
            PageMarkPosition.TopLeft => design with { TopLeft = "<<Bates>>" },
            PageMarkPosition.TopCenter => design with { TopCenter = "<<Bates>>" },
            PageMarkPosition.TopRight => design with { TopRight = "<<Bates>>" },
            PageMarkPosition.BottomLeft => design with { BottomLeft = "<<Bates>>" },
            PageMarkPosition.BottomCenter => design with { BottomCenter = "<<Bates>>" },
            _ => design with { BottomRight = "<<Bates>>" },
        };
        return new PageMarkRequest(action, Kind, design with { Bates = new PdfBatesNumbering(Prefix, Suffix, Math.Max(0, Start), Digits) }, null);
    }
}

/// <summary>Watermark (and, as a background, Background): text, a picture or a page of a PDF, placed, turned, scaled and faded.</summary>
public partial class WatermarkViewModel : PageMarkDialogViewModel
{
    public WatermarkViewModel(int pageCount, int currentPage, bool hasExisting, PdfWatermark? existing,
        Func<PageMarkRequest, int, CancellationToken, Task<ImageSource?>>? preview, bool background = false)
        : base(background ? PdfMarkKind.Background : PdfMarkKind.Watermark, pageCount, currentPage, hasExisting, preview)
    {
        IsBackground = background;
        Quietly(() =>
        {
            if (background)
            {
                Source = PdfMarkSource.Color;
                RelativeScalePercent = 100;
                UseRelativeScale = true;
            }
            if (existing != null) Load(existing);
        });
    }

    public bool IsBackground { get; }
    public override string Title => IsBackground ? "Background" : "Watermark";

    public IReadOnlyList<PdfMarkSource> Sources => IsBackground
        ? new[] { PdfMarkSource.Color, PdfMarkSource.Image, PdfMarkSource.Page }
        : new[] { PdfMarkSource.Text, PdfMarkSource.Image, PdfMarkSource.Page };
    public IReadOnlyList<PdfMarkHorizontal> HorizontalAlignments { get; } = Enum.GetValues<PdfMarkHorizontal>();
    public IReadOnlyList<PdfMarkVertical> VerticalAlignments { get; } = Enum.GetValues<PdfMarkVertical>();

    [ObservableProperty] private PdfMarkSource _source = PdfMarkSource.Text;
    [ObservableProperty] private string _text = "CONFIDENTIAL";
    [ObservableProperty] private string _fontFamily = "Arial";
    [ObservableProperty] private double _fontSize = 48;
    [ObservableProperty] private string _textColor = "#FF0000";
    [ObservableProperty] private string _fillColor = "#FFFFE0";
    /// <summary>A picture or PDF file.</summary>
    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private int _sourcePage = 1;
    [ObservableProperty] private double _opacityPercent = 50;
    [ObservableProperty] private double _rotation = 45;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnTop))]
    private bool _behind;

    public bool OnTop
    {
        get => !Behind;
        set => Behind = !value;
    }
    [ObservableProperty] private PdfMarkHorizontal _horizontal = PdfMarkHorizontal.Center;
    [ObservableProperty] private PdfMarkVertical _vertical = PdfMarkVertical.Center;
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scalePercent = 100;
    [ObservableProperty] private bool _useRelativeScale;
    [ObservableProperty] private double _relativeScalePercent = 50;

    public bool IsTextSource => Source == PdfMarkSource.Text;
    public bool IsFileSource => Source is PdfMarkSource.Image or PdfMarkSource.Page;
    public bool IsColorSource => Source == PdfMarkSource.Color;
    public bool IsPageSource => Source == PdfMarkSource.Page;

    partial void OnSourceChanged(PdfMarkSource value)
    {
        OnPropertyChanged(nameof(IsTextSource));
        OnPropertyChanged(nameof(IsFileSource));
        OnPropertyChanged(nameof(IsColorSource));
        OnPropertyChanged(nameof(IsPageSource));
    }

    /// <summary>Reads a picture file (set by the viewer; tests replace it).</summary>
    public Func<string, PdfImageContent>? LoadImage { get; init; }

    private (string Path, object Content)? _loaded;

    private void Load(PdfWatermark wm)
    {
        Source = wm.Source; Text = wm.Text; FontFamily = wm.Format.FontFamily; FontSize = wm.Format.Size;
        TextColor = Hex(wm.Format.Red, wm.Format.Green, wm.Format.Blue);
        if (wm.Color is { Length: >= 3 } c) FillColor = Hex(c[0], c[1], c[2]);
        SourcePath = wm.SourcePath ?? string.Empty; SourcePage = wm.SourcePage;
        OpacityPercent = Math.Round(wm.Opacity * 100); Rotation = wm.RotationDegrees; Behind = wm.Behind;
        Horizontal = wm.Horizontal; Vertical = wm.Vertical; OffsetX = wm.OffsetX; OffsetY = wm.OffsetY;
        UseRelativeScale = wm.RelativeScale.HasValue;
        RelativeScalePercent = Math.Round((wm.RelativeScale ?? 0.5) * 100);
        ScalePercent = Math.Round(wm.Scale * 100);
        AllPages = wm.Pages.From <= 1 && wm.Pages.To == null;
        RangeFrom = wm.Pages.From; RangeTo = wm.Pages.To ?? PageCount; Subset = wm.Pages.Subset;
    }

    protected override PageMarkRequest? Build(PdfMarkAction action)
    {
        var rgb = Rgb(TextColor);
        var design = new PdfWatermark
        {
            Source = Source, Text = Text, IsBackground = IsBackground, Behind = Behind,
            Format = new PdfTextFormat(FontFamily, FontSize > 0 ? FontSize : 48, Red: rgb[0], Green: rgb[1], Blue: rgb[2]),
            Color = Rgb(FillColor), Opacity = Math.Clamp(OpacityPercent / 100, 0, 1), RotationDegrees = Rotation,
            Horizontal = Horizontal, Vertical = Vertical, OffsetX = OffsetX, OffsetY = OffsetY,
            Scale = ScalePercent > 0 ? ScalePercent / 100 : 1, RelativeScale = UseRelativeScale ? Math.Max(0.01, RelativeScalePercent / 100) : null,
            Pages = Range, SourcePage = Math.Max(1, SourcePage), SourcePath = Source is PdfMarkSource.Image or PdfMarkSource.Page ? SourcePath : null,
        };
        switch (Source)
        {
            case PdfMarkSource.Text when string.IsNullOrWhiteSpace(Text):
                Message = "Type the watermark's text.";
                return null;
            case PdfMarkSource.Image or PdfMarkSource.Page:
                if (string.IsNullOrWhiteSpace(SourcePath))
                {
                    Message = Source == PdfMarkSource.Image ? "Choose a picture." : "Choose a PDF.";
                    return null;
                }
                try
                {
                    object content = _loaded is { } l && l.Path == SourcePath + "|" + Source ? l.Content
                        : Source == PdfMarkSource.Image ? (LoadImage ?? Services.EditImageLoader.Load)(SourcePath) : File.ReadAllBytes(SourcePath);
                    _loaded = (SourcePath + "|" + Source, content);
                    design = content is PdfImageContent image ? design with { Image = image } : design with { SourcePdf = (byte[])content };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
                                               or InvalidOperationException or FileFormatException or OverflowException)
                {
                    Message = $"The file could not be read: {ex.Message}";
                    return null;
                }
                break;
        }
        return new PageMarkRequest(action, Kind, null, design);
    }
}
