namespace PdfEngine.Vector.Annotations;

/// <summary>
/// The limits that annotations from untrusted input (XFDF, a host page's annotation calls) must
/// keep. The XFDF reader and the web viewer's annotation store use the same values, so an
/// imported annotation is never refused by the store for a limit the reader let through.
/// </summary>
public static class PdfAnnotationLimits
{
    /// <summary>Longest author (/T, XFDF title), subject and name, in characters.</summary>
    public const int MaxTextLength = 256;

    /// <summary>Longest text (/Contents) of one annotation, in characters.</summary>
    public const int MaxContentsLength = 10000;

    /// <summary>Most quadrilaterals of one text markup annotation.</summary>
    public const int MaxQuads = 1000;

    /// <summary>Most strokes of one ink annotation.</summary>
    public const int MaxInkStrokes = 100;

    /// <summary>Most points of one ink stroke.</summary>
    public const int MaxInkPointsPerStroke = 10000;

    /// <summary>Most points of one ink annotation, over all its strokes.</summary>
    public const int MaxInkPoints = 50000;

    /// <summary>Most ink, quadrilateral and vertex points of all annotations of one document (a quadrilateral has 4).</summary>
    public const int MaxPointsPerDocument = 1_000_000;

    /// <summary>
    /// Most /Vertices points of one polygon or polyline. The reader keeps no more than this of a
    /// file's list, and the appearance generator draws no more.
    /// </summary>
    public const int MaxVertices = 10000;

    /// <summary>Most replies between a reply and the annotation its thread starts at (/IRT chains, 12.5.6.4).</summary>
    public const int MaxReplyDepth = 64;

    /// <summary>The line ending names of /LE (ISO 32000-2 Table 179); untrusted input gives one of these.</summary>
    public static IReadOnlySet<string> LineEndings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "None", "Square", "Circle", "Diamond", "OpenArrow", "ClosedArrow", "Butt", "ROpenArrow", "RClosedArrow", "Slash",
    };
}
