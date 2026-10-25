using Microsoft.JSInterop;
using PdfViewer.Web.Engine;
using PdfViewer.Web.Engine.Embed;
using PdfViewer.Web.Interop;

namespace PdfViewer.Web.Components;

/// <summary>
/// Text selection with the mouse, on one page at a time: press and drag selects from caret to
/// caret, a double click selects a word, Ctrl+A the current page, and the browser's copy (Ctrl+C)
/// puts the text on the clipboard when the document allows copying. The engine's text layout
/// (ViewerTextLayout, shared with the Android app) finds the characters.
/// </summary>
public sealed partial class Viewer
{
    private ViewerTextSelection? _selection;
    private IReadOnlyList<ViewerRect> _selectionBands = [];
    private ViewerPoint _anchor;
    private bool _wordSelection;

    /// <summary>Text layouts of the pages selected on lately, newest last.</summary>
    private readonly List<(int Page, ViewerTextLayout Layout)> _layouts = new();
    private const int KeepLayouts = 6;

    private async Task<ViewerTextLayout?> LayoutAsync(int page)
    {
        foreach (var (p, layout) in _layouts)
            if (p == page) return layout;
        var doc = _doc;
        if (doc == null) return null;
        ViewerTextLayout result;
        try
        {
            result = await doc.GetTextLayoutAsync(page);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        if (_doc != doc) return null;
        _layouts.Add((page, result));
        if (_layouts.Count > KeepLayouts) _layouts.RemoveAt(0);
        return result;
    }

    private ViewerPoint PointOn(int page, double fx, double fy)
    {
        var pv = _doc!.Pages[page - 1];
        return new ViewerPoint(fx * pv.Width, fy * pv.Height);
    }

    [JSInvokable]
    public async Task OnSelectStart(int page, double fx, double fy, int clicks)
    {
        if (_doc == null || page < 1 || page > _doc.PageCount || !double.IsFinite(fx) || !double.IsFinite(fy)) return;
        var doc = _doc;
        var point = PointOn(page, fx, fy);
        var layout = await LayoutAsync(page);
        if (layout == null || _doc != doc) return;
        var selection = new ViewerTextSelection(page, layout);
        _anchor = point;
        _wordSelection = clicks >= 2;
        if (_wordSelection) selection.SelectWordAt(point, slack: 12);
        _selection = selection;
        UpdateSelection();
    }

    [JSInvokable]
    public void OnSelectMove(int page, double fx, double fy)
    {
        if (_doc == null || _selection == null || _selection.Page != page || _wordSelection || !double.IsFinite(fx) || !double.IsFinite(fy)) return;
        _selection.SelectBetween(_anchor, PointOn(page, fx, fy));
        UpdateSelection();
    }

    [JSInvokable]
    public void OnSelectClear() => ClearSelection();

    /// <summary>The browser copied the selected text (viewer.js put it on the clipboard).</summary>
    [JSInvokable]
    public void OnCopied()
    {
        if (_selection is not { IsEmpty: false } s || _doc is not { CanCopy: true }) return;
        string text = s.Text;
        _status = $"Copied {text.Length} {(text.Length == 1 ? "character" : "characters")}";
        Emit(EmbedEvents.TextCopy, w => w.WriteString("copiedText", text));
        StateHasChanged();
    }

    private async Task SelectAllOnPageAsync()
    {
        if (_doc == null) return;
        int page = _currentPage;
        var layout = await LayoutAsync(page);
        if (layout == null) return;
        _selection = new ViewerTextSelection(page, layout);
        _selection.SelectAll();
        UpdateSelection();
    }

    private void ClearSelection()
    {
        if (_selection == null) return;
        _selection = null;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        bool has = _selection is { IsEmpty: false };
        _selectionBands = has ? _selection!.Bands : [];
        bool canCopy = _doc?.CanCopy == true;
        CanvasInterop.SetSelectionText(has && canCopy ? _selection!.Text : null);
        if (has && !canCopy) _status = "This document does not allow copying its text.";
        StateHasChanged();
    }

    private void ForgetSelection()
    {
        _selection = null;
        _selectionBands = [];
        _layouts.Clear();
        CanvasInterop.SetSelectionText(null);
    }
}
