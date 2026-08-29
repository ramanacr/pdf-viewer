using PdfEngine.Geometry;

namespace PdfEngine.Annotations;

public enum AnnotationType
{
    Highlight,
    Underline,
    StrikeOut,
    Note,
    FreeText,
    Rectangle,
    Ellipse,
    Ink,
    Stamp,
    Redaction
}

public enum AnnotationSaveMode
{
    Embedded,
    Flattened,
    ExportXfdf
}

public class InkStroke
{
    public List<PdfPoint> Points { get; set; } = new();
    public string Color { get; set; } = "#FF0000";
    public double Thickness { get; set; } = 2.0;
}

public class AnnotationModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int PageNumber { get; set; }
    public AnnotationType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Color { get; set; } = "#FFFF00";
    public double Opacity { get; set; } = 0.5;
    public string Contents { get; set; } = string.Empty;
    public string Author { get; set; } = Environment.UserName;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public List<InkStroke> InkStrokes { get; set; } = new();
    public List<PdfRect> QuadPoints { get; set; } = new();
    public double StrokeThickness { get; set; } = 2.0;
    public string BorderColor { get; set; } = "#000000";
    public double FontSize { get; set; } = 12.0;
    public string FontColor { get; set; } = "#000000";
    public bool IsSelected { get; set; }

    public PdfRect Bounds => new(X, Y, Width, Height);
}

public interface IPdfAnnotationService
{
    ValueTask<IReadOnlyList<AnnotationModel>> LoadAnnotationsAsync(
        IPdfDocument document,
        int pageNumber,
        CancellationToken cancellationToken = default);

    ValueTask SaveAnnotatedDocumentAsync(
        IPdfDocument document,
        string targetPath,
        IReadOnlyList<AnnotationModel> annotations,
        AnnotationSaveMode mode,
        CancellationToken cancellationToken = default);
}
