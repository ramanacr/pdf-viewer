using System;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Geometry;
using PdfViewer.Models;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the accessibility check window (set by the main window; tests call the view model directly).</summary>
    public Action<AccessibilityCheckViewModel>? ShowAccessibilityCheckAction { get; set; }

    /// <summary>The element the accessibility check is pointing at, drawn with the search highlights.</summary>
    private SearchMatch? _accessibilityHighlight;

    /// <summary>A view model for checking the open document's accessibility.</summary>
    public AccessibilityCheckViewModel? CreateAccessibilityCheckViewModel() =>
        _docService.CurrentBytes is { } bytes ? new AccessibilityCheckViewModel(bytes, _docService.CurrentFilePath, _openPassword, ShowAccessibilityIssue) : null;

    [RelayCommand]
    public void ShowAccessibilityCheck()
    {
        if (!IsDocumentLoaded || CreateAccessibilityCheckViewModel() is not { } vm) return;
        if (ShowAccessibilityCheckAction != null) ShowAccessibilityCheckAction(vm);
        else _ = vm.CheckAsync();
    }

    /// <summary>Goes to <paramref name="page"/> and highlights <paramref name="normalizedBounds"/> (fractions of the page from its top-left) when given.</summary>
    public void ShowAccessibilityIssue(int page, PdfRect? normalizedBounds) => ShowPageHighlight(page, normalizedBounds, "Accessibility issue");

    /// <summary>
    /// Goes to <paramref name="page"/> and highlights <paramref name="normalizedBounds"/> with the search highlights, for a
    /// checker's result (accessibility, preflight); one at a time.
    /// </summary>
    public void ShowPageHighlight(int page, PdfRect? normalizedBounds, string label)
    {
        ClearAccessibilityHighlight();
        if (page < 1 || page > Pages.Count) return;
        NavigateToPage(page);
        if (normalizedBounds is not { } b) return;
        _accessibilityHighlight = new SearchMatch
        {
            PageNumber = page, X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
            ContextSnippet = label, IsCurrentMatch = true,
        };
        Pages[page - 1].MatchesOnPage.Add(_accessibilityHighlight);
        ScrollToMatchAction?.Invoke(page, b.X, b.Y);
    }

    public void ClearAccessibilityHighlight()
    {
        if (_accessibilityHighlight is not { } h) return;
        if (h.PageNumber >= 1 && h.PageNumber <= Pages.Count) Pages[h.PageNumber - 1].MatchesOnPage.Remove(h);
        _accessibilityHighlight = null;
    }

    /// <summary>Whether the accessibility check is highlighting an element on <paramref name="page"/>.</summary>
    public bool HasAccessibilityHighlight(int page) => _accessibilityHighlight?.PageNumber == page;
}
