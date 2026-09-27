using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfViewer.RenderingAdapters;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows a Header &amp; Footer, Bates Numbering, Watermark or Background dialog; true when it was closed with a choice. Set by the window; tests replace it.</summary>
    public Func<PageMarkDialogViewModel, bool>? ShowPageMarkDialogFunc { get; set; }

    [RelayCommand] public Task ShowHeaderFooterAsync() => ShowPageMarkDialogAsync(PdfMarkKind.HeaderFooter);
    [RelayCommand] public Task ShowBatesNumberingAsync() => ShowPageMarkDialogAsync(PdfMarkKind.Bates);
    [RelayCommand] public Task ShowWatermarkAsync() => ShowPageMarkDialogAsync(PdfMarkKind.Watermark);
    [RelayCommand] public Task ShowBackgroundAsync() => ShowPageMarkDialogAsync(PdfMarkKind.Background);

    private async Task ShowPageMarkDialogAsync(PdfMarkKind kind)
    {
        if (ShowPageMarkDialogFunc == null || await CreatePageMarkViewModelAsync(kind) is not { } vm) return;
        if (ShowPageMarkDialogFunc(vm) && vm.Result is { } request) await ApplyPageMarksAsync(request);
    }

    /// <summary>The dialog for a kind of mark, filled in with the settings the document's marks were made with, when this viewer made them.</summary>
    public async Task<PageMarkDialogViewModel?> CreatePageMarkViewModelAsync(PdfMarkKind kind)
    {
        if (WhyCannotEditContent() is { } reason)
        {
            StatusText = reason;
            ShowAlert(reason, PageMarkTitle(kind), MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        PdfPageMarksInfo info;
        try
        {
            info = await WithEditDocumentAsync((doc, _) => PdfPageMarks.Read(doc));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.IO.IOException)
        {
            ShowAlert($"The document's pages cannot be read:\n\n{ex.Message}", PageMarkTitle(kind), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        var fonts = (EditFontCatalog ?? SystemFontCatalog.Installed).Faces.Select(f => f.Family).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (fonts.Count == 0) fonts.Add("Arial");
        int pages = Pages.Count, current = Math.Clamp(CurrentPageNumber, 1, Math.Max(1, Pages.Count));
        bool has = info.Has(kind);
        return kind switch
        {
            PdfMarkKind.HeaderFooter => new HeaderFooterViewModel(pages, current, has, info.HeaderFooter, RenderPageMarkPreviewAsync) { FontFamilies = fonts },
            PdfMarkKind.Bates => new BatesNumberingViewModel(pages, current, has, info.Bates, RenderPageMarkPreviewAsync) { FontFamilies = fonts },
            PdfMarkKind.Watermark => new WatermarkViewModel(pages, current, has, info.Watermark, RenderPageMarkPreviewAsync) { FontFamilies = fonts },
            _ => new WatermarkViewModel(pages, current, has, info.Background, RenderPageMarkPreviewAsync, background: true) { FontFamilies = fonts },
        };
    }

    private static string PageMarkTitle(PdfMarkKind kind) => kind switch
    {
        PdfMarkKind.HeaderFooter => "Header & Footer", PdfMarkKind.Bates => "Bates Numbering", PdfMarkKind.Watermark => "Watermark", _ => "Background",
    };

    /// <summary>A page drawn with the marks the request would add (nothing is changed in the document).</summary>
    internal async Task<ImageSource?> RenderPageMarkPreviewAsync(PageMarkRequest request, int pageNumber, CancellationToken ct)
    {
        var fonts = EditFontCatalog ?? SystemFontCatalog.Installed;
        byte[] marked = await WithEditDocumentAsync((doc, bytes) => Mark(doc, bytes, request, fonts).Bytes);
        ct.ThrowIfCancellationRequested();
        string? password = _openPassword;
        return await Task.Run(async () =>
        {
            using var engine = new PdfiumEngine();
            await using var doc = await engine.OpenDocumentAsync(marked, password, ct);
            var info = await doc.GetPageInfoAsync(pageNumber, ct);
            // About 420 pixels across the longer side.
            double longest = Math.Max(Math.Max(info.WidthPoints, info.HeightPoints), 1);
            using var page = await engine.Renderer.RenderPageAsync(doc, new RenderRequest { PageNumber = pageNumber, Dpi = 72 * 420 / longest }, ct);
            return (ImageSource)WpfBitmapAdapter.ToBitmapSource(page);
        }, ct);
    }

    private static PdfContentEditResult Mark(PdfVectorDocument doc, byte[] bytes, PageMarkRequest request, SystemFontCatalog fonts)
    {
        var options = new PdfContentEditOptions { Fonts = fonts };
        return request switch
        {
            { Action: PdfMarkAction.Remove } => PdfPageMarks.Remove(doc, bytes, request.Kind),
            { HeaderFooter: { } hf, Action: PdfMarkAction.Add } => PdfPageMarks.Add(doc, bytes, hf, options),
            { HeaderFooter: { } hf } => PdfPageMarks.Update(doc, bytes, hf, options),
            { Watermark: { } wm, Action: PdfMarkAction.Add } => PdfPageMarks.Add(doc, bytes, wm, options),
            { Watermark: { } wm } => PdfPageMarks.Update(doc, bytes, wm, options),
            _ => throw new ArgumentException("The request has no design."),
        };
    }

    /// <summary>
    /// Adds, updates or removes marks as a new revision in memory, like a content edit: written
    /// as an incremental update on Save, with the previous revision kept for Undo.
    /// </summary>
    public async Task<bool> ApplyPageMarksAsync(PageMarkRequest request)
    {
        string title = PageMarkTitle(request.Kind);
        if (WhyCannotEditContent() is { } reason)
        {
            StatusText = reason;
            return false;
        }
        if (HasSignatures && !_signedEditConfirmed)
        {
            if (!Confirm("This document is signed. Changing it adds a revision after the signatures, and they will show that the document was changed after signing.\n\nChange it anyway?",
                    "Change a signed document"))
                return false;
            _signedEditConfirmed = true;
        }
        byte[] before = _docService.CurrentBytes!;
        PdfContentEditResult result;
        try
        {
            var fonts = EditFontCatalog ?? SystemFontCatalog.Installed;
            result = await WithEditDocumentAsync((doc, bytes) => Mark(doc, bytes, request, fonts));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.IO.IOException)
        {
            StatusText = $"The {title.ToLowerInvariant()} could not be changed.";
            ShowAlert($"The {title.ToLowerInvariant()} could not be changed:\n\n{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        await ShowRevisionAsync(result.Bytes, null);
        string done = request.Action switch
        {
            PdfMarkAction.Add => $"Added the {title.ToLowerInvariant()}.",
            PdfMarkAction.Update => $"Updated the {title.ToLowerInvariant()}.",
            _ => $"Removed the {title.ToLowerInvariant()}.",
        };
        StatusText = done + (result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : string.Empty);
        return true;
    }
}
