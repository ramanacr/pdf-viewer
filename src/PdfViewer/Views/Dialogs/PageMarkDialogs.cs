using System;
using System.Windows;
using Microsoft.Win32;
using PdfEngine.Vector.Editing;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Opens the dialog for a page-mark view model, and what the dialogs share.</summary>
public static class PageMarkDialogs
{
    /// <summary>Shows the dialog modally; true when it closed with Add, Update or Remove.</summary>
    public static bool Show(PageMarkDialogViewModel vm, Window? owner)
    {
        Window dialog = vm switch
        {
            BatesNumberingViewModel b => new BatesNumberingDialog(b),
            HeaderFooterViewModel h => new HeaderFooterDialog(h),
            WatermarkViewModel { IsBackground: true } g => new BackgroundDialog(g),
            WatermarkViewModel w => new WatermarkDialog(w),
            _ => throw new ArgumentException("There is no dialog for this view model."),
        };
        dialog.Owner = owner;
        return dialog.ShowDialog() == true && vm.Result != null;
    }

    internal static void Attach(Window window, PageMarkDialogViewModel vm)
    {
        window.DataContext = vm;
        EventHandler close = (_, _) => window.DialogResult = true;
        vm.CloseRequested += close;
        window.Closed += (_, _) => vm.CloseRequested -= close;
        window.Loaded += async (_, _) => await vm.RefreshPreviewAsync();
    }

    /// <summary>Chooses the picture or PDF a watermark or background shows.</summary>
    internal static void Browse(Window owner, WatermarkViewModel vm)
    {
        var dialog = new OpenFileDialog
        {
            Title = vm.Source == PdfMarkSource.Page ? "Choose a PDF" : "Choose a picture",
            Filter = vm.Source == PdfMarkSource.Page
                ? "PDF documents (*.pdf)|*.pdf"
                : "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(owner) == true) vm.SourcePath = dialog.FileName;
    }
}
