using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Geometry;
using PdfEngine.Rendering;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Parsing;
using PdfViewer.Models;
using PdfViewer.RenderingAdapters;
using PdfViewer.Services;

namespace PdfViewer.ViewModels;

/// <summary>A stamp chosen to place: a standard or dynamic one by name, or a custom one from the library.</summary>
public sealed record StampChoice(string Name, string Label, bool Dynamic, CustomStamp? Custom = null)
{
    public string DisplayName => Custom?.Name ?? (Dynamic ? Label + " (dynamic)" : Label);
}

/// <summary>
/// Comment → Stamp: the standard and dynamic stamps, and custom stamps made from a picture or a
/// PDF page and kept in the local library. A stamp is placed by clicking (at its usual size) or
/// by dragging its box, turned by the chosen rotation; it is a Stamp annotation with /Name and an
/// appearance drawn by the engine, undoable like any other comment.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The stamp the next click places, or null.</summary>
    [ObservableProperty]
    private StampChoice? _activeStamp;

    /// <summary>How far new stamps are turned, degrees counter-clockwise.</summary>
    [ObservableProperty]
    private double _stampRotation;

    /// <summary>The custom stamps in the library, for the Stamp menu.</summary>
    public ObservableCollection<CustomStamp> CustomStamps { get; } = new();

    /// <summary>Asks for the name dynamic stamps show (the current one, or a suggestion); null when cancelled.</summary>
    public Func<string, string?>? RequestStampIdentityFunc { get; set; }

    /// <summary>Picks a PDF and one of its pages for a custom stamp: the path and page, or null.</summary>
    public Func<(string Path, int Page)?>? PickStampPdfFunc { get; set; }

    /// <summary>Asks for a custom stamp's name (a suggestion given); null when cancelled.</summary>
    public Func<string, string?>? RequestStampNameFunc { get; set; }

    public bool IsPlacingStamp => ActiveStamp != null;

    partial void OnActiveStampChanged(StampChoice? value)
    {
        if (value != null)
        {
            ActiveAnnotationTool = null;
            ActiveMeasureTool = null;
            IsPlacingSignature = false;
            IsMarkingRedaction = false;
            IsPanningEnabled = false;
        }
        OnPropertyChanged(nameof(IsPlacingStamp));
    }

    /// <summary>Re-reads the library for the menu.</summary>
    public void RefreshCustomStamps()
    {
        CustomStamps.Clear();
        foreach (var s in StampLibrary.CustomStamps) CustomStamps.Add(s);
    }

    /// <summary>
    /// Chooses the stamp to place: "S:Name" for a standard one, "D:Name" for a dynamic one, "C:id"
    /// for a custom one. A dynamic stamp needs the user's name, asked for once and remembered.
    /// </summary>
    [RelayCommand]
    public void ChooseStamp(string key)
    {
        if (!IsDocumentLoaded || string.IsNullOrEmpty(key) || key.Length < 3) return;
        if (!Permit(DocumentPermissions.CanAnnotate, "adding stamps")) return;
        string id = key[2..];
        StampChoice? choice = key[0] switch
        {
            'S' => PdfStamps.Standard.FirstOrDefault(s => s.Name == id) is { } s ? new StampChoice(s.Name, s.Label, false) : null,
            'D' => PdfStamps.Dynamic.FirstOrDefault(s => s.Name == id) is { } d ? new StampChoice(d.Name, d.Label, true) : null,
            'C' => StampLibrary.CustomStamps.FirstOrDefault(c => c.Id == id) is { } c ? new StampChoice("Custom", c.Name, false, c) : null,
            _ => null,
        };
        if (choice == null) return;
        if (choice.Dynamic && StampIdentity() == null) return;
        ActiveStamp = choice;
        StatusText = $"Click where the {choice.DisplayName} stamp goes, or drag its box (Esc cancels).";
    }

    /// <summary>The name dynamic stamps show: remembered, else asked for (and remembered). Null when the user declines.</summary>
    private string? StampIdentity()
    {
        if (StampLibrary.Identity is { } known) return known;
        string? name = RequestStampIdentityFunc?.Invoke(string.IsNullOrWhiteSpace(SelectedAnnotationAuthor) ? Environment.UserName : SelectedAnnotationAuthor);
        if (string.IsNullOrWhiteSpace(name)) return null;
        StampLibrary.SetIdentity(name);
        return name.Trim();
    }

    /// <summary>Changes the name dynamic stamps show.</summary>
    [RelayCommand]
    public void SetStampIdentity()
    {
        string current = StampLibrary.Identity ?? SelectedAnnotationAuthor;
        if (RequestStampIdentityFunc?.Invoke(current) is { } name && !string.IsNullOrWhiteSpace(name))
        {
            StampLibrary.SetIdentity(name);
            StatusText = $"Dynamic stamps now show \"{name.Trim()}\".";
        }
    }

    [RelayCommand]
    public void SetStampRotation(string degrees)
    {
        if (double.TryParse(degrees, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
            StampRotation = PdfStamps.NormalizeDegrees(d);
    }

    /// <summary>Makes a custom stamp from a PNG or JPEG picture.</summary>
    [RelayCommand]
    public void CreateStampFromImage()
    {
        if (PickImageFileFunc?.Invoke() is not { } path) return;
        string fallback = System.IO.Path.GetFileNameWithoutExtension(path);
        string? name = RequestStampNameFunc is { } ask ? ask(fallback) : fallback; // null from the dialog means Cancel
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var stamp = StampLibrary.AddImage(path, name);
            RefreshCustomStamps();
            ChooseStamp("C:" + stamp.Id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.IO.FileFormatException)
        {
            ShowAlert($"The picture could not be made into a stamp:\n\n{ex.Message}", "Custom Stamp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Makes a custom stamp from a page of a PDF.</summary>
    [RelayCommand]
    public void CreateStampFromPdf()
    {
        if (PickStampPdfFunc?.Invoke() is not { } pick) return;
        string fallback = System.IO.Path.GetFileNameWithoutExtension(pick.Path);
        string? name = RequestStampNameFunc is { } ask ? ask(fallback) : fallback; // null from the dialog means Cancel
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var stamp = StampLibrary.AddPdfPage(pick.Path, pick.Page, name);
            RefreshCustomStamps();
            ChooseStamp("C:" + stamp.Id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowAlert($"The page could not be made into a stamp:\n\n{ex.Message}", "Custom Stamp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Removes a custom stamp from the library (stamps already placed stay in their documents).</summary>
    [RelayCommand]
    public void RemoveCustomStamp(CustomStamp? stamp)
    {
        if (stamp == null) return;
        if (!Confirm($"Remove the custom stamp \"{stamp.Name}\" from this computer? Stamps already placed in documents stay.", "Custom Stamp")) return;
        StampLibrary.Remove(stamp);
        RefreshCustomStamps();
    }

    /// <summary>
    /// Places the chosen stamp on <paramref name="page"/>: centred at <paramref name="normCentre"/>
    /// at its usual size, or filling <paramref name="normBox"/> when the user dragged one. The
    /// stamp stays chosen only for this placement.
    /// </summary>
    public async Task<AnnotationModel?> PlaceStampAsync(int page, Point normCentre, Rect? normBox = null)
    {
        if (ActiveStamp is not { } choice || page < 1 || page > Pages.Count) return null;
        await PrepareMeasurePageAsync(page);
        var crop = Frame(page)?.Crop ?? new PdfRect(0, 0, Pages[page - 1].UnrotatedWidthPt, Pages[page - 1].UnrotatedHeightPt);
        string? identity = choice.Dynamic ? StampIdentity() : null;
        if (choice.Dynamic && identity == null) return null;

        byte[]? file = null;
        double w, h;
        if (choice.Custom is { } custom)
        {
            try { file = StampLibrary.Load(custom); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                ShowAlert($"The custom stamp's file could not be read:\n\n{ex.Message}", "Stamp", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            (w, h) = custom.PlacedSize();
        }
        else
        {
            (w, h) = PdfStamps.DefaultSize(choice.Label, choice.Dynamic);
        }

        var centre = NativeDocumentServices.ToPage(normCentre, crop);
        if (normBox is { Width: > 0, Height: > 0 } box)
        {
            var a = NativeDocumentServices.ToPage(box.TopLeft, crop);
            var b = NativeDocumentServices.ToPage(box.BottomRight, crop);
            double bw = Math.Abs(b.X - a.X), bh = Math.Abs(b.Y - a.Y);
            if (bw >= 8 && bh >= 8)
            {
                centre = new PdfPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                if (choice.Custom != null)
                {
                    // A picture keeps its shape, as large as fits the box.
                    double k = Math.Min(bw / w, bh / h);
                    w *= k; h *= k;
                }
                else
                {
                    w = bw; h = bh;
                }
            }
        }
        var rect = PdfStamps.RotatedBounds(centre, w, h, StampRotation);
        var model = new AnnotationModel
        {
            PageNumber = page,
            Type = AnnotationType.Stamp,
            StampName = choice.Name,
            StampSubtitle = identity != null ? PdfStamps.DynamicLine(identity, DateTime.Now) : null,
            StampRotation = StampRotation,
            StampSize = new Size(w, h),
            StampImage = choice.Custom is { IsPdf: false } ? file : null,
            StampPdf = choice.Custom is { IsPdf: true } ? file : null,
            Contents = choice.Custom?.Name ?? choice.Label,
            Title = "Stamp",
            Author = identity ?? SelectedAnnotationAuthor,
            Opacity = 1,
            StrokeThickness = 0,
            ColorHex = "#FF000000",
        };
        model.SetGeometry(NativeDocumentServices.FromPage(rect, crop), Array.Empty<Rect>());
        AddAnnotation(model);
        ActiveStamp = null;
        StatusText = $"Placed the {choice.DisplayName} stamp on page {page}.";
        model.StampPreview = await RenderStampPreviewAsync(model, rect);
        return model;
    }

    /// <summary>
    /// The stamp as the engine draws it into the document, at about twice screen resolution, with
    /// its transparency: drawn once on white and once on black, the difference is its alpha.
    /// </summary>
    internal static async Task<ImageSource?> RenderStampPreviewAsync(AnnotationModel model, PdfRect rect, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var annotation = NativeDocumentServices.StampAnnotation(model, rect);
                annotation.Color = null;
                double longest = Math.Max(Math.Max(rect.Width, rect.Height), 1);
                var request = new RenderRequest { PageNumber = 1, Dpi = Math.Clamp(72 * 600 / longest, 72, 288) };
                async Task<(byte[] Pixels, int Width, int Height, int Stride)> Draw(bool black)
                {
                    using var doc = PdfVectorDocument.Open(new MemoryByteSource(PdfStamps.Preview(annotation, black)));
                    using var page = await VectorPdfRenderer.Shared.RenderPageAsync(doc, request, ct);
                    return (page.Pixels.ToArray(), page.WidthPixels, page.HeightPixels, page.Stride);
                }
                var (onWhite, w, h, stride) = await Draw(false);
                var (onBlack, bw, bh, _) = await Draw(true);
                if (bw != w || bh != h || onBlack.Length != onWhite.Length) return null;
                var premultiplied = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 4, o = (y * w + x) * 4;
                        int diff = (onWhite[i] - onBlack[i] + onWhite[i + 1] - onBlack[i + 1] + onWhite[i + 2] - onBlack[i + 2]) / 3;
                        byte alpha = (byte)Math.Clamp(255 - diff, 0, 255);
                        // On black the colour is already multiplied by alpha.
                        premultiplied[o] = Math.Min(onBlack[i], alpha);
                        premultiplied[o + 1] = Math.Min(onBlack[i + 1], alpha);
                        premultiplied[o + 2] = Math.Min(onBlack[i + 2], alpha);
                        premultiplied[o + 3] = alpha;
                    }
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, premultiplied, w * 4);
                bitmap.Freeze();
                return (ImageSource)bitmap;
            }, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null; // the stamp is placed all the same; the page shows it once saved
        }
    }
}
