using System.Collections.ObjectModel;

namespace PdfViewer.Models;

/// <summary>
/// Represents a bookmark or outline node in a PDF document.
/// </summary>
public class BookmarkItem
{
    public string Title { get; set; } = string.Empty;
    public int TargetPageNumber { get; set; } = 1;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public ObservableCollection<BookmarkItem> Children { get; set; } = new();

    public override string ToString() => $"{Title} (Page {TargetPageNumber})";
}
