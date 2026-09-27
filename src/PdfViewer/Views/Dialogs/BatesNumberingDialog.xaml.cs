using System.Windows;
using PdfViewer.ViewModels;

namespace PdfViewer.Views.Dialogs;

/// <summary>Bates Numbering: a sequence number with a prefix and suffix on every page of the range.</summary>
public partial class BatesNumberingDialog : Window
{
    public BatesNumberingDialog(BatesNumberingViewModel vm)
    {
        InitializeComponent();
        PageMarkDialogs.Attach(this, vm);
    }
}
