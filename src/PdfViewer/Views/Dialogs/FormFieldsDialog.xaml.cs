using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using PdfEngine.Forms;

namespace PdfViewer.Views.Dialogs;

/// <summary>
/// Lists the document's form fields and lets their values be edited, then saved to a new
/// document. Read-only fields are shown but cannot be changed, matching how the PDF itself
/// declares them.
/// </summary>
public partial class FormFieldsDialog : Window
{
    public ObservableCollection<FormFieldModel> Fields { get; }

    /// <summary>Set when the user chose to save; the caller then performs the write.</summary>
    public bool SaveRequested { get; private set; }

    public FormFieldsDialog(IReadOnlyList<FormFieldModel> fields, string documentName)
    {
        InitializeComponent();

        Fields = new ObservableCollection<FormFieldModel>(fields);
        FieldsGrid.ItemsSource = Fields;

        SubtitleText.Text = Fields.Count == 0
            ? $"{documentName} contains no form fields."
            : $"{Fields.Count} field(s) in {documentName}. Edit the Value column, then Save As.";

        int editable = Fields.Count(f => !f.IsReadOnly);
        HintText.Text = Fields.Count == 0
            ? string.Empty
            : $"{editable} of {Fields.Count} editable";

        SaveButton.IsEnabled = editable > 0;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Commit any in-progress cell edit before handing the values back.
        FieldsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        SaveRequested = true;
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
