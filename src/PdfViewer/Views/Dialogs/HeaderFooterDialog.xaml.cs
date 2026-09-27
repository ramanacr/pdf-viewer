using System;
using System.Windows;
using System.Windows.Controls;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Header &amp; Footer: text in six places, with a live preview of the current page.</summary>
public partial class HeaderFooterDialog : Window
{
    private readonly HeaderFooterViewModel _vm;

    public HeaderFooterDialog(HeaderFooterViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        PageMarkDialogs.Attach(this, vm);
    }

    // The Insert buttons add to the box last clicked.
    private void Box_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string tag } && Enum.TryParse<PageMarkPosition>(tag, out var position)) _vm.SelectedBox = position;
    }
}
