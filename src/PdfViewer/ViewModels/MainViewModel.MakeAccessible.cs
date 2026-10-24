using System;
using CommunityToolkit.Mvvm.Input;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>Shows the Make Accessible dialog (set by the main window; tests call the view model directly).</summary>
    public Action<MakeAccessibleViewModel>? ShowMakeAccessibleAction { get; set; }

    /// <summary>A view model for tagging the open document.</summary>
    public MakeAccessibleViewModel? CreateMakeAccessibleViewModel() =>
        _docService.CurrentBytes is { } bytes ? new MakeAccessibleViewModel(bytes, _docService.CurrentFilePath, _openPassword) : null;

    [RelayCommand]
    public void ShowMakeAccessible()
    {
        if (!IsDocumentLoaded || CreateMakeAccessibleViewModel() is not { } vm) return;
        if (ShowMakeAccessibleAction != null) ShowMakeAccessibleAction(vm);
        else _ = vm.LoadAsync();
    }
}
