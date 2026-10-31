namespace PdfViewer.Web.Engine.Annotations;

/// <summary>
/// The colours a viewer offers for new annotations (the web viewer's toolbar palette), their names
/// for a screen reader, and how the colour last chosen for a kind of annotation is kept as a
/// preference (a key, and a stored value that is checked when it is read back).
/// </summary>
public static class ViewerToolColors
{
    /// <summary>The colours offered, as "#RRGGBB", with their names.</summary>
    public static IReadOnlyList<(string Value, string Name)> Palette { get; } =
        [("#FFD400", "Yellow"), ("#22A06B", "Green"), ("#E5484D", "Red"), ("#2563EB", "Blue"), ("#000000", "Black")];

    /// <summary>
    /// The kinds of new annotation a colour can be chosen for: text markup, ink, notes, shapes and
    /// text boxes (a stamp is drawn in the colour of its kind; a reply has no colour of its own).
    /// </summary>
    public static bool Choosable(ViewerAnnotationKind kind) => kind is ViewerAnnotationKind.Highlight or ViewerAnnotationKind.Underline
        or ViewerAnnotationKind.StrikeOut or ViewerAnnotationKind.Ink or ViewerAnnotationKind.Note or ViewerAnnotationKind.Square
        or ViewerAnnotationKind.Circle or ViewerAnnotationKind.Line or ViewerAnnotationKind.Polygon or ViewerAnnotationKind.PolyLine
        or ViewerAnnotationKind.FreeText;

    /// <summary>The preference key for the colour of new annotations of a kind, for example "colour.highlight".</summary>
    public static string KeyOf(ViewerAnnotationKind kind) =>
        Choosable(kind) ? "colour." + kind.ToString().ToLowerInvariant() : throw new ArgumentOutOfRangeException(nameof(kind), kind, "No colour is chosen for this kind.");

    /// <summary>
    /// A kept colour read back: "#RRGGBB" (upper case) when <paramref name="stored"/> is a colour
    /// in that form, otherwise null (nothing kept, or a value that is not a colour), and the
    /// caller uses the default colour of the kind.
    /// </summary>
    public static string? Parse(string? stored)
    {
        if (stored is not { Length: 7 } s || s[0] != '#') return null;
        for (int i = 1; i < 7; i++)
            if (!char.IsAsciiHexDigit(s[i])) return null;
        return s.ToUpperInvariant();
    }

    /// <summary>The colour of new annotations of a kind: the kept one, or <see cref="ViewerAnnotationPdf.DefaultColor"/>.</summary>
    public static string ColorOf(ViewerAnnotationKind kind, string? stored) => Parse(stored) ?? ViewerAnnotationPdf.DefaultColor(kind);

    /// <summary>A colour's name in the palette ("Yellow"), or the colour itself ("#1F9D55") when it is not one of them.</summary>
    public static string NameOf(string color)
    {
        foreach (var (value, name) in Palette)
            if (string.Equals(value, color, StringComparison.OrdinalIgnoreCase)) return name;
        return color;
    }

    /// <summary>The place of a colour in the palette, or -1 when it is not one of them.</summary>
    public static int IndexOf(string? color)
    {
        for (int i = 0; i < Palette.Count; i++)
            if (string.Equals(Palette[i].Value, color, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
