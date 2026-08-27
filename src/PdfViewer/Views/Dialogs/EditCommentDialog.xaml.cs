using System.Collections.Generic;
using System.Windows;
using PdfViewer.Models;

namespace PdfViewer.Views.Dialogs;

public partial class EditCommentDialog : Window
{
    public AnnotationModel Annotation { get; }
    public bool IsConfirmed { get; private set; }

    private static readonly List<string> DefaultColors = new()
    {
        "#FFFF00", // Yellow
        "#FFD700", // Gold
        "#00E676", // Mint / Light Green
        "#00E5FF", // Cyan
        "#2979FF", // Blue
        "#FF4081", // Pink / Magenta
        "#FF5252", // Coral Red
        "#FF9100", // Orange
        "#AA00FF", // Purple
        "#212121"  // Charcoal Black
    };

    public EditCommentDialog(AnnotationModel annotation, bool isNew = false)
    {
        InitializeComponent();

        Annotation = annotation;
        DialogHeaderTextBlock.Text = isNew ? "Add Comment / Sticky Note" : "Edit Comment / Sticky Note";

        TitleTextBox.Text = string.IsNullOrWhiteSpace(annotation.Title) ? "Note" : annotation.Title;
        CommentTextBox.Text = annotation.Contents;
        AuthorTextBox.Text = string.IsNullOrWhiteSpace(annotation.Author) ? System.Environment.UserName : annotation.Author;

        ColorComboBox.ItemsSource = DefaultColors;
        ColorComboBox.SelectedItem = DefaultColors.Contains(annotation.ColorHex) ? annotation.ColorHex : DefaultColors[0];

        Loaded += (s, e) =>
        {
            CommentTextBox.Focus();
            CommentTextBox.SelectAll();
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Annotation.Title = TitleTextBox.Text.Trim();
        Annotation.Contents = CommentTextBox.Text;
        Annotation.Author = AuthorTextBox.Text.Trim();

        if (ColorComboBox.SelectedItem is string colorHex)
        {
            Annotation.ColorHex = colorHex;
        }

        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }
}
