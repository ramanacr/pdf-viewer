namespace PdfViewer.Models;

/// <summary>
/// Represents a found text occurrence within a PDF page.
/// </summary>
public class SearchMatch
{
    public int MatchIndex { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ContextSnippet { get; set; } = string.Empty;
    
    // Normalized page coordinates (0.0 to 1.0)
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>
    /// Indicates whether this match is the active/currently selected search occurrence.
    /// Active match is highlighted in Gold/Amber; others in Lime-Green.
    /// </summary>
    public bool IsCurrentMatch { get; set; }

    public string DisplayText => $"Page {PageNumber}: {ContextSnippet}";

    public override string ToString() => DisplayText;
}
