using System;
using System.IO;
using CommunityToolkit.Mvvm.Input;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the Reduce File Size window (set by the main window; tests call the view model directly).</summary>
    public Action<ReduceSizeViewModel>? ShowReduceSizeAction { get; set; }

    /// <summary>
    /// A view model for making a smaller copy of the open document. A password-encrypted document is
    /// reduced with the password it was opened with and stays encrypted the same way; one encrypted
    /// for certificates is held only as a decrypted copy here, so it is not offered.
    /// </summary>
    public ReduceSizeViewModel? CreateReduceSizeViewModel()
    {
        if (_docService.CurrentBytes is not { } bytes) return null;
        string fileName = Path.GetFileName(_docService.CurrentFilePath);
        return new ReduceSizeViewModel(bytes, _docService.CurrentFilePath, DocumentPermissions.IsEncrypted || _docService.IsDecryptedCopy, _openPassword,
            _docService.IsDecryptedCopy ? "This document is encrypted for specific recipients; its size cannot be reduced here." : null)
        {
            RequestOwnerPassword = RequestOwnerPasswordFunc != null ? () => RequestOwnerPasswordFunc(fileName) : null,
        };
    }

    [RelayCommand]
    public void ShowReduceSize()
    {
        if (!IsDocumentLoaded || CreateReduceSizeViewModel() is not { } vm) return;
        ShowReduceSizeAction?.Invoke(vm);
    }
}
