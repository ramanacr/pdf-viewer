using System;
using CommunityToolkit.Mvvm.Input;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the Reduce File Size window (set by the main window; tests call the view model directly).</summary>
    public Action<ReduceSizeViewModel>? ShowReduceSizeAction { get; set; }

    /// <summary>A view model for making a smaller copy of the open document.</summary>
    public ReduceSizeViewModel? CreateReduceSizeViewModel() =>
        _docService.CurrentBytes is { } bytes
            ? new ReduceSizeViewModel(bytes, _docService.CurrentFilePath, DocumentPermissions.IsEncrypted || _docService.IsDecryptedCopy)
            : null;

    [RelayCommand]
    public void ShowReduceSize()
    {
        if (!IsDocumentLoaded || CreateReduceSizeViewModel() is not { } vm) return;
        ShowReduceSizeAction?.Invoke(vm);
    }
}
