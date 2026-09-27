using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PdfA;

namespace PdfViewer.ViewModels;

public partial class MainViewModel
{
    /// <summary>The PDF/A conformance the open document claims ("PDF/A-2b"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPdfAClaim))]
    private string? _pdfAClaim;

    public bool HasPdfAClaim => PdfAClaim != null;

    /// <summary>Shows the PDF/A window (set by the main window; tests call the view model directly).</summary>
    public Action<PdfAViewModel>? ShowPdfAAction { get; set; }

    /// <summary>Reads the claim from the document's metadata; the status bar shows it, as Acrobat's PDF/A banner does.</summary>
    private async Task DetectPdfAAsync()
    {
        PdfAClaim = null;
        if (_docService.CurrentBytes is not { } bytes) return;
        try
        {
            PdfAClaim = await Task.Run(async () =>
            {
                using var doc = await PdfVectorDocument.OpenAsync(bytes, _docService.CurrentFilePath, password: _openPassword);
                return PdfAValidator.ClaimedFlavour(doc)?.ToString();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.IO.IOException or ArgumentException)
        {
            PdfAClaim = null;
        }
    }

    /// <summary>A view model for checking and converting the open document.</summary>
    public PdfAViewModel? CreatePdfAViewModel() =>
        _docService.CurrentBytes is { } bytes ? new PdfAViewModel(bytes, _docService.CurrentFilePath, _openPassword, PdfAClaim) : null;

    [RelayCommand]
    public void ShowPdfA()
    {
        if (!IsDocumentLoaded || CreatePdfAViewModel() is not { } vm) return;
        if (ShowPdfAAction != null) ShowPdfAAction(vm);
        else _ = vm.CheckAsync();
    }
}
