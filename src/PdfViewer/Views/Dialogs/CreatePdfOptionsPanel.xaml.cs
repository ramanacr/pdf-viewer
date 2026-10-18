using System.Windows.Controls;

namespace PdfViewer.Views.Dialogs;

/// <summary>The Create PDF options (page size, image quality, greyscale, recognition, PDF/A), bound to a CreatePdfOptionsViewModel.</summary>
public partial class CreatePdfOptionsPanel : UserControl
{
    public CreatePdfOptionsPanel() => InitializeComponent();
}
