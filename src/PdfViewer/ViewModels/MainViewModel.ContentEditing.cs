using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Text and images of the pages can be selected, edited, moved, resized and deleted.</summary>
    [ObservableProperty]
    private bool _isEditingContent;

    /// <summary>The next click on a page starts a new text box there.</summary>
    [ObservableProperty]
    private bool _isAddingText;

    [ObservableProperty]
    private ContentEditItem? _selectedEditItem;

    /// <summary>How new text looks.</summary>
    [ObservableProperty]
    private PdfTextFormat _newTextFormat = new("Arial", 12);

    /// <summary>Chooses an image file (null when cancelled). Set by the window; tests replace it.</summary>
    public Func<string?>? PickImageFileFunc { get; set; }

    /// <summary>Where installed fonts come from (tests use a fixed folder).</summary>
    public SystemFontCatalog? EditFontCatalog { get; set; }

    private readonly Stack<byte[]> _editUndo = new(), _editRedo = new();
    private readonly System.Threading.SemaphoreSlim _editDocLock = new(1, 1);
    private byte[]? _editDocBytes;
    private PdfVectorDocument? _editDoc;

    private async Task ReleaseEditDocumentAsync()
    {
        await _editDocLock.WaitAsync();
        try
        {
            _editDoc?.Dispose();
            _editDoc = null;
            _editDocBytes = null;
        }
        finally
        {
            _editDocLock.Release();
        }
    }

    /// <summary>Runs <paramref name="work"/> on the current revision, opened once per revision.</summary>
    private async Task<T> WithEditDocumentAsync<T>(Func<PdfVectorDocument, byte[], T> work)
    {
        byte[] bytes = _docService.CurrentBytes ?? throw new InvalidOperationException("No document is open.");
        await _editDocLock.WaitAsync();
        try
        {
            if (_editDoc == null || !ReferenceEquals(_editDocBytes, bytes))
            {
                _editDoc?.Dispose();
                _editDoc = null;
                _editDoc = await PdfVectorDocument.OpenAsync(bytes, _docService.CurrentFilePath, password: _openPassword);
                _editDocBytes = bytes;
            }
            var doc = _editDoc;
            return await Task.Run(() => work(doc, bytes));
        }
        finally
        {
            _editDocLock.Release();
        }
    }
    private const int MaxEditUndo = 30;
    private bool _signedEditConfirmed;

    public bool CanUndoContentEdit => _editUndo.Count > 0;

    /// <summary>The revision shown now (tests read it).</summary>
    internal byte[]? CurrentDocumentBytes() => _docService.CurrentBytes;
    public bool CanRedoContentEdit => _editRedo.Count > 0;

    partial void OnIsEditingContentChanged(bool value)
    {
        if (value)
        {
            if (WhyCannotEditContent() is { } reason)
            {
                StatusText = reason;
                ShowAlert(reason, "Edit Text & Images", MessageBoxButton.OK, MessageBoxImage.Information);
                IsEditingContent = false;
                return;
            }
            ActiveAnnotationTool = null;
            IsPlacingSignature = false;
            IsMarkingRedaction = false;
            ClearSelection();
            StatusText = "Editing text and images: click a paragraph or image to select it, double-click text to edit it, drag to move. Esc to finish.";
            _ = RenderVisiblePagesAsync(); // reads the shown pages' paragraphs and images
        }
        else
        {
            IsAddingText = false;
            SelectedEditItem = null;
            foreach (var page in Pages)
            {
                page.EditItems.Clear();
                page.EditItemsLoaded = false;
            }
            _ = ReleaseEditDocumentAsync();
            StatusText = "Finished editing text and images.";
        }
    }

    partial void OnIsAddingTextChanged(bool value)
    {
        if (value) StatusText = "Click where the new text should start.";
    }

    partial void OnSelectedEditItemChanged(ContentEditItem? oldValue, ContentEditItem? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }

    private string? WhyCannotEditContent()
    {
        if (!IsDocumentLoaded || _docService.CurrentBytes == null) return "Open a document first.";
        if (!DocumentPermissions.CanModify) return "This document's security settings do not allow editing its text and images.";
        if (_docService.IsDecryptedCopy) return "This document is encrypted for specific recipients; save an unencrypted copy to edit it.";
        return null;
    }

    [RelayCommand]
    public void ToggleContentEditing() => IsEditingContent = !IsEditingContent;

    [RelayCommand]
    public void BeginAddText()
    {
        if (!IsEditingContent) IsEditingContent = true;
        if (IsEditingContent) IsAddingText = true;
    }

    // ------------------------------------------------------------------ reading the pages

    /// <summary>Reads a page's paragraphs and images for the edit layer (once per revision).</summary>
    public Task EnsureEditItemsAsync(PageViewModel page)
    {
        if (!IsEditingContent || _docService.CurrentBytes == null) return Task.CompletedTask;
        if (page.EditItemsLoaded) return page.EditItemsTask ?? Task.CompletedTask;
        page.EditItemsLoaded = true;
        return page.EditItemsTask = LoadEditItemsAsync(page);
    }

    private async Task LoadEditItemsAsync(PageViewModel page)
    {
        try
        {
            var fonts = EditFontCatalog ?? SystemFontCatalog.Installed;
            var (model, crop) = await WithEditDocumentAsync((doc, _) => (PdfContentEditor.Read(doc, page.PageNumber, fonts), doc.PageTree.Pages[page.PageNumber - 1].CropBox));
            if (!IsEditingContent) return;
            page.EditItems.Clear();
            foreach (var t in model.Texts)
                page.EditItems.Add(new ContentEditItem
                {
                    Kind = ContentEditKind.Text, Id = t.Id, PageNumber = page.PageNumber, Bounds = Normalize(t.Bounds, crop),
                    Text = t.Text, FontName = t.FontName, FontFamily = t.FontFamily, FontSize = t.FontSize, LineSpacing = t.LineSpacing,
                    Bold = t.Bold, Italic = t.Italic, Alignment = t.Alignment, AngleDegrees = t.AngleDegrees, LineCount = t.LineBounds.Count,
                    Color = Color.FromRgb(Byte(t.Color[0]), Byte(t.Color[1]), Byte(t.Color[2])),
                });
            foreach (var i in model.Images)
                page.EditItems.Add(new ContentEditItem
                {
                    Kind = ContentEditKind.Image, Id = i.Id, PageNumber = page.PageNumber, Bounds = Normalize(i.Bounds, crop),
                    PixelWidth = i.PixelWidth, PixelHeight = i.PixelHeight,
                });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.IO.IOException)
        {
            StatusText = $"Page {page.PageNumber} cannot be edited: {ex.Message}";
        }
    }

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>A user-space box normalized to the unrotated crop box, top-left origin.</summary>
    internal static Rect Normalize(PdfRect r, PdfRect crop) =>
        new((r.X - crop.X) / crop.Width, 1 - (r.Y + r.Height - crop.Y) / crop.Height, r.Width / crop.Width, r.Height / crop.Height);

    private Task<PdfRect> CropOfAsync(int pageNumber) => WithEditDocumentAsync((doc, _) => doc.PageTree.Pages[pageNumber - 1].CropBox);

    // ------------------------------------------------------------------ edits

    /// <summary>A paragraph's new text (it reflows in its width and keeps its fonts and colours).</summary>
    public Task<bool> CommitTextEditAsync(ContentEditItem item, string newText)
    {
        if (item.Kind != ContentEditKind.Text || newText == item.Text) return Task.FromResult(false);
        return ApplyContentEditsAsync(new PdfContentEdit[] { new PdfReplaceText(item.PageNumber, item.Id, newText) },
            newText.Trim().Length == 0 ? "Deleted the paragraph." : "Edited the text.");
    }

    /// <summary>Moves a paragraph or image by a distance normalized to the unrotated page.</summary>
    public async Task<bool> MoveEditItemAsync(ContentEditItem item, Vector delta)
    {
        if (Math.Abs(delta.X) < 1e-6 && Math.Abs(delta.Y) < 1e-6) return false;
        var crop = await CropOfAsync(item.PageNumber);
        var t = PdfMatrix.CreateTranslation(delta.X * crop.Width, -delta.Y * crop.Height);
        return await ApplyContentEditsAsync(new PdfContentEdit[] { new PdfTransformContent(item.PageNumber, Target(item), item.Id, t) },
            item.Kind == ContentEditKind.Text ? "Moved the paragraph." : "Moved the image.");
    }

    /// <summary>An image stretched or shrunk to new bounds (normalized to the unrotated page).</summary>
    public async Task<bool> ResizeEditItemAsync(ContentEditItem item, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        var crop = await CropOfAsync(item.PageNumber);
        var from = MainViewModel.ToUserSpaceRect(item.Bounds, crop);
        var to = MainViewModel.ToUserSpaceRect(bounds, crop);
        var t = PdfMatrix.CreateTranslation(-from.X, -from.Y) * PdfMatrix.CreateScale(to.Width / from.Width, to.Height / from.Height) * PdfMatrix.CreateTranslation(to.X, to.Y);
        return await ApplyContentEditsAsync(new PdfContentEdit[] { new PdfTransformContent(item.PageNumber, Target(item), item.Id, t) },
            item.Kind == ContentEditKind.Text ? "Resized the paragraph." : "Resized the image.");
    }

    /// <summary>Turns an image a quarter turn about its centre (positive: clockwise as seen).</summary>
    public async Task<bool> RotateEditItemAsync(ContentEditItem item, int degreesClockwise)
    {
        var crop = await CropOfAsync(item.PageNumber);
        var box = MainViewModel.ToUserSpaceRect(item.Bounds, crop);
        double cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
        var t = PdfMatrix.CreateTranslation(-cx, -cy) * PdfMatrix.CreateRotation(-degreesClockwise * Math.PI / 180) * PdfMatrix.CreateTranslation(cx, cy);
        return await ApplyContentEditsAsync(new PdfContentEdit[] { new PdfTransformContent(item.PageNumber, Target(item), item.Id, t) }, "Turned the image.");
    }

    public Task<bool> DeleteEditItemAsync(ContentEditItem item) =>
        ApplyContentEditsAsync(new PdfContentEdit[] { new PdfDeleteContent(item.PageNumber, Target(item), item.Id) },
            item.Kind == ContentEditKind.Text ? "Deleted the paragraph." : "Deleted the image.");

    [RelayCommand]
    public async Task DeleteSelectedContentAsync()
    {
        if (SelectedEditItem is { } item) await DeleteEditItemAsync(item);
    }

    /// <summary>Replaces an image with a picture from a file, fitted into its place.</summary>
    public async Task<bool> ReplaceImageAsync(ContentEditItem item, string? path = null)
    {
        if (item.Kind != ContentEditKind.Image) return false;
        path ??= PickImageFileFunc?.Invoke();
        if (path == null) return false;
        var image = LoadImage(path);
        if (image == null) return false;
        return await ApplyContentEditsAsync(new PdfContentEdit[] { new PdfReplaceImage(item.PageNumber, item.Id, image) }, "Replaced the image.");
    }

    [RelayCommand]
    public async Task ReplaceSelectedImageAsync()
    {
        if (SelectedEditItem is { Kind: ContentEditKind.Image } item) await ReplaceImageAsync(item);
    }

    [RelayCommand]
    public async Task RotateSelectedImageClockwiseAsync()
    {
        if (SelectedEditItem is { Kind: ContentEditKind.Image } item) await RotateEditItemAsync(item, 90);
    }

    [RelayCommand]
    public async Task RotateSelectedImageCounterclockwiseAsync()
    {
        if (SelectedEditItem is { Kind: ContentEditKind.Image } item) await RotateEditItemAsync(item, -90);
    }

    /// <summary>New text whose first line starts (top-left, as seen) at a point normalized to the unrotated page.</summary>
    public async Task<bool> AddTextAtAsync(int pageNumber, Point at, string text)
    {
        IsAddingText = false;
        if (text.Trim().Length == 0) return false;
        var crop = await CropOfAsync(pageNumber);
        int rotation = pageNumber >= 1 && pageNumber <= Pages.Count ? Pages[pageNumber - 1].IntrinsicRotation : 0;
        // Upright as the page is displayed: the baseline runs along the page's rotation.
        double angle = rotation * Math.PI / 180, size = NewTextFormat.Size;
        double x = crop.X + at.X * crop.Width, y = crop.Y + (1 - at.Y) * crop.Height;
        var baseline = new PdfPoint(x + Math.Sin(angle) * 0.8 * size, y - Math.Cos(angle) * 0.8 * size);
        var format = NewTextFormat with { AngleDegrees = rotation };
        return await ApplyContentEditsAsync(new PdfContentEdit[] { new PdfAddText(pageNumber, baseline, text, format) }, "Added text.");
    }

    /// <summary>A picture from a file, placed in the middle of the page at up to half its width.</summary>
    [RelayCommand]
    public async Task AddImageAsync()
    {
        if (!IsEditingContent) IsEditingContent = true;
        if (!IsEditingContent) return;
        var path = PickImageFileFunc?.Invoke();
        if (path != null) await AddImageFromFileAsync(CurrentPageNumber, path);
    }

    public async Task<bool> AddImageFromFileAsync(int pageNumber, string path)
    {
        var image = LoadImage(path);
        if (image == null) return false;
        var crop = await CropOfAsync(pageNumber);
        int rotation = Pages[pageNumber - 1].IntrinsicRotation;
        // Upright as the page is displayed (turned with the page's rotation), half the page at most, never above 72 dpi.
        bool sideways = rotation is 90 or 270;
        double seenW = sideways ? crop.Height : crop.Width, seenH = sideways ? crop.Width : crop.Height;
        double scale = Math.Min(1.0, Math.Min(seenW * 0.5 / image.Width, seenH * 0.5 / image.Height));
        double w = image.Width * scale, h = image.Height * scale;
        var bounds = new PdfRect(crop.X + (crop.Width - w) / 2, crop.Y + (crop.Height - h) / 2, w, h);
        return await ApplyContentEditsAsync(new PdfContentEdit[] { new PdfAddImage(pageNumber, bounds, image, rotation) }, "Added the image.");
    }

    private PdfImageContent? LoadImage(string path)
    {
        try
        {
            return Services.EditImageLoader.Load(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException
                                       or System.IO.FileFormatException or OverflowException)
        {
            ShowAlert($"The picture could not be read:\n\n{ex.Message}", "Image", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private static PdfEditTarget Target(ContentEditItem item) => item.Kind == ContentEditKind.Text ? PdfEditTarget.Text : PdfEditTarget.Image;

    internal static PdfRect ToUserSpaceRect(Rect normalized, PdfRect crop) =>
        new(crop.X + normalized.X * crop.Width, crop.Y + (1 - normalized.Y - normalized.Height) * crop.Height, normalized.Width * crop.Width, normalized.Height * crop.Height);

    /// <summary>
    /// Applies edits as a new revision in memory (written as an incremental update on Save), with
    /// the previous revision kept for Undo. Signed documents are warned about once: the edit
    /// is recorded after the signatures, which then show the document was changed.
    /// </summary>
    public async Task<bool> ApplyContentEditsAsync(IReadOnlyList<PdfContentEdit> edits, string description)
    {
        if (WhyCannotEditContent() is { } reason)
        {
            StatusText = reason;
            return false;
        }
        if (HasSignatures && !_signedEditConfirmed)
        {
            if (!Confirm("This document is signed. Editing it adds a revision after the signatures, and they will show that the document was changed after signing.\n\nEdit it anyway?",
                    "Edit a signed document"))
                return false;
            _signedEditConfirmed = true;
        }
        byte[] before = _docService.CurrentBytes!;
        PdfContentEditResult result;
        if (edits.Count == 0) return false;
        try
        {
            var fonts = EditFontCatalog ?? SystemFontCatalog.Installed;
            result = await WithEditDocumentAsync((doc, bytes) => PdfContentEditor.Apply(doc, bytes, edits, new PdfContentEditOptions { Fonts = fonts }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.IO.IOException)
        {
            StatusText = "The edit could not be made.";
            ShowAlert($"The edit could not be made:\n\n{ex.Message}", "Edit Text & Images", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (ReferenceEquals(result.Bytes, before))
        {
            StatusText = result.Warnings.FirstOrDefault() ?? "Nothing changed.";
            return false;
        }

        _editUndo.Push(before);
        if (_editUndo.Count > MaxEditUndo) TrimUndo();
        _editRedo.Clear();
        await ShowRevisionAsync(result.Bytes, edits.Select(e => e.PageNumber).Distinct().ToList());
        string fontsNote = result.EmbeddedFonts.Count > 0 ? $" Embedded {string.Join(", ", result.EmbeddedFonts)} for characters the document's fonts lack." : string.Empty;
        StatusText = description + fontsNote + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : string.Empty);
        return true;
    }

    private void TrimUndo()
    {
        var keep = _editUndo.Take(MaxEditUndo).Reverse().ToList();
        _editUndo.Clear();
        foreach (var b in keep) _editUndo.Push(b);
    }

    [RelayCommand]
    public async Task UndoContentEditAsync()
    {
        if (_editUndo.Count == 0) return;
        _editRedo.Push(_docService.CurrentBytes!);
        await ShowRevisionAsync(_editUndo.Pop(), null);
        StatusText = "Undid the edit.";
    }

    [RelayCommand]
    public async Task RedoContentEditAsync()
    {
        if (_editRedo.Count == 0) return;
        _editUndo.Push(_docService.CurrentBytes!);
        await ShowRevisionAsync(_editRedo.Pop(), null);
        StatusText = "Redid the edit.";
    }

    /// <summary>Loads a revision: the pages it changed are drawn again, and their text and edit items re-read.</summary>
    private async Task ShowRevisionAsync(byte[] bytes, IReadOnlyList<int>? pages)
    {
        await _docService.ReloadFromBytesAsync(bytes);
        await LoadFormAsync();
        var changed = pages ?? Enumerable.Range(1, Pages.Count).ToList();
        SelectedEditItem = null;
        var reread = new List<PageViewModel>();
        foreach (int number in changed.Where(n => n >= 1 && n <= Pages.Count))
        {
            var page = Pages[number - 1];
            page.UnloadImage();
            if (number - 1 < Thumbnails.Count) Thumbnails[number - 1].UnloadThumbnail();
            page.TextSegments.Clear();
            page.IsTextExtracted = false;
            if (page.EditItemsLoaded) reread.Add(page);
            page.EditItems.Clear();
            page.EditItemsLoaded = false;
        }
        _cache.Clear();
        _formChangedSinceSave = true; // an in-memory revision, written as it is on Save
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(CanUndoContentEdit));
        OnPropertyChanged(nameof(CanRedoContentEdit));
        _ = RenderVisiblePagesAsync();
        _ = RenderThumbnailsAsync();
        // The pages being shown for editing are read again (their ids belong to the old revision).
        foreach (var page in reread) await EnsureEditItemsAsync(page);
        if (HasSignatures) _ = ValidateSignaturesAsync();
    }
}
