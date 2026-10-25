using System;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Geometry;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the Output Preview window (set by the main window; tests call the view model directly).</summary>
    public Action<OutputPreviewViewModel>? ShowOutputPreviewAction { get; set; }

    /// <summary>Shows the Preflight window (set by the main window).</summary>
    public Action<PreflightViewModel>? ShowPreflightAction { get; set; }

    /// <summary>A view model for the Output Preview of the open document, starting at the current page.</summary>
    public OutputPreviewViewModel? CreateOutputPreviewViewModel() =>
        _docService.CurrentBytes is { } bytes ? new OutputPreviewViewModel(bytes, _docService.CurrentFilePath, _openPassword, CurrentPageNumber) : null;

    /// <summary>A view model for preflighting the open document.</summary>
    public PreflightViewModel? CreatePreflightViewModel() =>
        _docService.CurrentBytes is { } bytes ? new PreflightViewModel(bytes, _docService.CurrentFilePath, _openPassword, ShowPreflightFinding) : null;

    [RelayCommand]
    public void ShowOutputPreview()
    {
        if (!IsDocumentLoaded || CreateOutputPreviewViewModel() is not { } vm) return;
        if (ShowOutputPreviewAction != null) ShowOutputPreviewAction(vm);
        else _ = vm.RenderAsync();
    }

    [RelayCommand]
    public void ShowPreflight()
    {
        if (!IsDocumentLoaded || CreatePreflightViewModel() is not { } vm) return;
        if (ShowPreflightAction != null) ShowPreflightAction(vm);
        else _ = vm.RunAsync();
    }

    /// <summary>Goes to <paramref name="page"/> and highlights the preflight finding's bounds (fractions of the page from its top-left).</summary>
    public void ShowPreflightFinding(int page, PdfRect? normalizedBounds) => ShowPageHighlight(page, normalizedBounds, "Preflight finding");
}
