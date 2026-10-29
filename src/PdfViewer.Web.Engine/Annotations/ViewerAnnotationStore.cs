using PdfEngine.Vector.Annotations;

namespace PdfViewer.Web.Engine.Annotations;

/// <summary>Which annotations a host page asks for: by id, by page range (1-based, inclusive), or all when both are null.</summary>
public sealed record ViewerAnnotationFilter(IReadOnlySet<string>? Ids = null, int? StartPage = null, int? EndPage = null)
{
    public static ViewerAnnotationFilter All { get; } = new();

    public bool Matches(ViewerAnnotation a) =>
        (Ids == null || Ids.Contains(a.Id)) && (StartPage is not int s || a.Page >= s) && (EndPage is not int e || a.Page <= e);
}

/// <summary>An annotation that a host page or the reader asked for and that cannot be: the message says why.</summary>
public sealed class ViewerAnnotationException(string message) : Exception(message);

/// <summary>
/// The annotations of one open document as the reader and the host page change them: the ones
/// read from the file, the current ones, and undo and redo. Every change is checked against the
/// pages (on a page of the document, inside its media box, within the limits below) and kept as
/// a new list, so undo is a step back to the list before. The document's bytes are written from
/// the difference (<see cref="ViewerAnnotationPdf.Write"/>).
/// </summary>
public sealed class ViewerAnnotationStore
{
    /// <summary>Most annotations in one document.</summary>
    public const int MaxAnnotations = 10000;

    /// <summary>Most annotations in one call of the host page.</summary>
    public const int MaxPerCall = 1000;

    // The limits shared with the XFDF reader (PdfAnnotationLimits), so an import is never refused for one of them.

    /// <summary>Longest text (/Contents) of one annotation, in characters.</summary>
    public const int MaxContentsLength = PdfAnnotationLimits.MaxContentsLength;

    public const int MaxAuthorLength = PdfAnnotationLimits.MaxTextLength;
    public const int MaxIdLength = 200;
    public const int MaxQuads = PdfAnnotationLimits.MaxQuads;
    public const int MaxInkStrokes = PdfAnnotationLimits.MaxInkStrokes;
    public const int MaxInkPointsPerStroke = PdfAnnotationLimits.MaxInkPointsPerStroke;
    public const int MaxInkPoints = PdfAnnotationLimits.MaxInkPoints;
    public const int MaxVertices = PdfAnnotationLimits.MaxVertices;
    public const int MaxReplyDepth = PdfAnnotationLimits.MaxReplyDepth;

    /// <summary>Most ink, quadrilateral and vertex points of all the document's annotations together.</summary>
    public const int MaxPoints = PdfAnnotationLimits.MaxPointsPerDocument;
    public const double MaxStrokeWidth = 100;
    public const double MinFontSize = 4, MaxFontSize = 144;

    /// <summary>How far outside the media box (points) a shape may reach, for a line drawn along its edge.</summary>
    public const double Tolerance = 2;

    private const int MaxUndo = 100;

    private readonly IReadOnlyList<ViewerPageGeometry> _pages;
    private readonly List<IReadOnlyList<ViewerAnnotation>> _undo = new();
    private readonly List<IReadOnlyList<ViewerAnnotation>> _redo = new();

    public ViewerAnnotationStore(IReadOnlyList<ViewerPageGeometry> pages, IReadOnlyList<ViewerAnnotation> fromFile)
    {
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        FromFile = fromFile ?? throw new ArgumentNullException(nameof(fromFile));
        Current = fromFile;
    }

    /// <summary>The annotations as the file has them (empty when the file's own annotations are not managed).</summary>
    public IReadOnlyList<ViewerAnnotation> FromFile { get; }

    /// <summary>The annotations now, in page order, newest last on each page.</summary>
    public IReadOnlyList<ViewerAnnotation> Current { get; private set; }

    /// <summary>Counts every change (undo and redo too), for "changed since saved".</summary>
    public int Version { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when the current annotations differ from the file's.</summary>
    public bool IsChanged => Current.Count != FromFile.Count || Current.Where((a, i) => !ReferenceEquals(a, FromFile[i])).Any();

    public ViewerAnnotation? Find(string id) => Current.FirstOrDefault(a => a.Id == id);

    public IReadOnlyList<ViewerAnnotation> Get(ViewerAnnotationFilter? filter) =>
        filter == null ? Current : Current.Where(filter.Matches).ToList();

    public ViewerPageGeometry Page(int page) => _pages[page - 1];

    /// <summary>
    /// Adds annotations (all or none). Ids that are missing are made; an id in use is refused. A
    /// reply must answer an annotation that exists, or one before it in the list; it takes that
    /// annotation's page and box.
    /// </summary>
    public IReadOnlyList<ViewerAnnotation> Add(IReadOnlyList<ViewerAnnotation> annotations)
    {
        if (annotations.Count > MaxPerCall) throw new ViewerAnnotationException($"At most {MaxPerCall} annotations can be added at once.");
        if (Current.Count + annotations.Count > MaxAnnotations) throw new ViewerAnnotationException($"A document can have at most {MaxAnnotations} annotations here.");
        var byId = Current.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var added = new List<ViewerAnnotation>(annotations.Count);
        foreach (var given in annotations)
        {
            var a = given.Id.Length == 0 ? given with { Id = NewId() } : given;
            if (byId.ContainsKey(a.Id)) throw new ViewerAnnotationException($"An annotation with the id \"{a.Id}\" exists already.");
            a = a.Kind == ViewerAnnotationKind.Reply ? Answering(a, byId) : Fit(a);
            Check(a);
            a = a with { SourceIndex = -1 };
            byId[a.Id] = a;
            added.Add(a);
        }
        if (Points(added) is > 0 and long more) CheckPoints(Points(Current) + more);
        Commit(Ordered(Current.Concat(added)));
        return added;
    }

    /// <summary>
    /// How far a line's, polygon's or polyline's box reaches past its points: half the line, or
    /// with line endings their size (the engine draws arrows nine line widths long, shapes three across).
    /// </summary>
    public static double ShapeMargin(double strokeWidth, bool endings) => endings ? 5 * Math.Max(strokeWidth, 1) + 1 : strokeWidth / 2 + 1;

    /// <summary>
    /// <see cref="ShapeMargin(double, bool)"/> for these endings: a reverse arrow reaches past its
    /// point by its length (nine line widths at 30 degrees).
    /// </summary>
    public static double ShapeMargin(double strokeWidth, string start, string end)
    {
        double Reach(string ending) => ending switch
        {
            "None" => ShapeMargin(strokeWidth, false),
            "ROpenArrow" or "RClosedArrow" => 9 * Math.Max(strokeWidth, 1) * Math.Cos(Math.PI / 6) + strokeWidth / 2 + 1,
            _ => ShapeMargin(strokeWidth, true),
        };
        return Math.Max(Reach(start), Reach(end));
    }

    /// <summary>
    /// A line's or polyline's box grown to hold its points and endings, never smaller than it was
    /// (cut to the page when the store takes it).
    /// </summary>
    public static ViewerBox GrownForEndings(ViewerAnnotation a)
    {
        var around = ViewerBox.Around(a.Vertices, ShapeMargin(a.StrokeWidth, a.StartEnding, a.EndEnding));
        var b = a.Box;
        return a.Vertices.Count == 0 ? b
            : new ViewerBox(Math.Min(b.X1, around.X1), Math.Min(b.Y1, around.Y1), Math.Max(b.X2, around.X2), Math.Max(b.Y2, around.Y2));
    }

    /// <summary>A shape's box grown around its points may reach past the page: it is cut to the page (its points are checked).</summary>
    private ViewerAnnotation Fit(ViewerAnnotation a)
    {
        if (!a.HasVertices || a.Page < 1 || a.Page > _pages.Count) return a;
        var m = _pages[a.Page - 1].MediaBox;
        var b = a.Box;
        return a with { Box = new ViewerBox(Math.Max(b.X1, m.Left), Math.Max(b.Y1, m.Top), Math.Min(b.X2, m.Right), Math.Min(b.Y2, m.Bottom)) };
    }

    /// <summary>A reply placed with the annotation it answers; refused when that does not exist or the thread is too deep.</summary>
    private static ViewerAnnotation Answering(ViewerAnnotation reply, IReadOnlyDictionary<string, ViewerAnnotation> byId)
    {
        if (!byId.TryGetValue(reply.InReplyTo, out var parent))
            throw new ViewerAnnotationException($"Reply \"{reply.Id}\": no annotation has the id \"{reply.InReplyTo}\" (target).");
        if (ViewerAnnotationPdf.Depth(parent, byId) + 1 > MaxReplyDepth)
            throw new ViewerAnnotationException($"Reply \"{reply.Id}\": a thread can have at most {MaxReplyDepth} levels of replies.");
        return reply with { Page = parent.Page, Box = parent.Box };
    }

    /// <summary>The replies to the annotation, and the replies to them, and so on.</summary>
    private static HashSet<string> Descendants(IReadOnlyList<ViewerAnnotation> list, IEnumerable<string> ids)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var parents = new HashSet<string>(ids, StringComparer.Ordinal);
        // A level at a time (a reply may be anywhere in the list), at most the depth limit of levels.
        for (int level = 0; level < MaxReplyDepth + 1 && parents.Count > 0; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in list)
                if (a.Kind == ViewerAnnotationKind.Reply && parents.Contains(a.InReplyTo) && found.Add(a.Id)) next.Add(a.Id);
            parents = next;
        }
        return found;
    }

    /// <summary>
    /// Replaces the annotation with the same id. Its type, and what a reply answers, cannot
    /// change; a reply stays with the annotation it answers, and replies follow theirs to another page.
    /// </summary>
    public ViewerAnnotation Update(ViewerAnnotation annotation)
    {
        var old = Find(annotation.Id) ?? throw new ViewerAnnotationException($"No annotation has the id \"{annotation.Id}\".");
        if (old.Kind != annotation.Kind) throw new ViewerAnnotationException("An annotation's subtype cannot change.");
        if (old.InReplyTo != annotation.InReplyTo) throw new ViewerAnnotationException("What a reply answers cannot change.");
        if (old.Kind == ViewerAnnotationKind.Reply) annotation = annotation with { Page = old.Page, Box = old.Box };
        // A measurement stays one, and keeps its fill, dash and line endings.
        if (old.IsMeasurement && (annotation.FillColor != old.FillColor || !ViewerAnnotation.SameDash(annotation.Dash, old.Dash)
                                  || annotation.StartEnding != old.StartEnding || annotation.EndEnding != old.EndEnding))
            throw new ViewerAnnotationException($"Annotation \"{old.Id}\" is a measurement: its fillColor, dashPattern and lineEndings cannot change.");
        annotation = annotation with { IsMeasurement = old.IsMeasurement };
        // Moved to another page, a file's annotation is written as a new one there (and removed from its page).
        var updated = Fit(annotation with { SourceIndex = annotation.Page == old.Page ? old.SourceIndex : -1 });
        Check(updated);
        if (Points(updated) > Points(old)) CheckPoints(Points(Current) - Points(old) + Points(updated));
        var replies = updated.Page != old.Page ? Descendants(Current, [updated.Id]) : [];
        Commit(Ordered(Current.Select(a =>
            a.Id == updated.Id ? updated
            : replies.Contains(a.Id) ? a with { Page = updated.Page, Box = updated.Box, SourceIndex = -1 }
            : a)));
        return updated;
    }

    /// <summary>Moves an annotation by (dx, dy) user-space points, kept inside its page.</summary>
    public ViewerAnnotation Move(string id, double dx, double dy, DateTimeOffset now)
    {
        var old = Find(id) ?? throw new ViewerAnnotationException($"No annotation has the id \"{id}\".");
        var media = _pages[old.Page - 1].MediaBox;
        // Clamp the move so that the box stays on the page.
        static double Clamp(double v, double lo, double hi) => lo > hi ? 0 : Math.Clamp(v, lo, hi);
        dx = Clamp(dx, media.Left - old.Box.X1, media.Right - old.Box.X2);
        dy = Clamp(dy, media.Top - old.Box.Y1, media.Bottom - old.Box.Y2);
        return Update(old.Moved(dx, dy) with { Modified = now });
    }

    /// <summary>Removes the annotations the filter matches, with their replies (as Acrobat does); returns them.</summary>
    public IReadOnlyList<ViewerAnnotation> Delete(ViewerAnnotationFilter filter)
    {
        var matched = Current.Where(filter.Matches).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        matched.UnionWith(Descendants(Current, matched));
        var removed = Current.Where(a => matched.Contains(a.Id)).ToList();
        if (removed.Count > 0) Commit(Current.Where(a => !matched.Contains(a.Id)).ToList());
        return removed;
    }

    public bool Undo() => Step(_undo, _redo);

    public bool Redo() => Step(_redo, _undo);

    private bool Step(List<IReadOnlyList<ViewerAnnotation>> from, List<IReadOnlyList<ViewerAnnotation>> to)
    {
        if (from.Count == 0) return false;
        to.Add(Current);
        Current = from[^1];
        from.RemoveAt(from.Count - 1);
        Version++;
        return true;
    }

    private void Commit(IReadOnlyList<ViewerAnnotation> next)
    {
        _undo.Add(Current);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        Current = next;
        Version++;
    }

    /// <summary>Page order, keeping the order within a page (the file's /Annots order, then added).</summary>
    private static List<ViewerAnnotation> Ordered(IEnumerable<ViewerAnnotation> list) =>
        list.Select((a, i) => (a, i)).OrderBy(x => x.a.Page).ThenBy(x => x.i).Select(x => x.a).ToList();

    public static string NewId() => Guid.NewGuid().ToString();

    /// <summary>The ink, quadrilateral and vertex points of the annotations (x, y pairs).</summary>
    public static long Points(IEnumerable<ViewerAnnotation> annotations) => annotations.Sum(a => Points(a));

    public static long Points(ViewerAnnotation a) => a.QuadPoints.Count / 2 + a.InkList.Sum(s => (long)(s.Count / 2)) + a.Vertices.Count / 2;

    private static void CheckPoints(long total)
    {
        if (total > MaxPoints)
            throw new ViewerAnnotationException($"The document's annotations can have at most {MaxPoints} ink, quadPoints and vertices points together.");
    }

    /// <summary>The stamp names a host page or the reader may give (ISO 32000-2 Table 184 and Acrobat's Standard Business set).</summary>
    public static IReadOnlySet<string> StampNames { get; } =
        PdfStamps.Standard.Select(s => s.Name).Concat(["Experimental", "Sold", "Departmental"]).ToHashSet(StringComparer.Ordinal);

    /// <summary>Refuses an annotation that is not on a page of this document or does not fit its media box and the limits.</summary>
    public void Check(ViewerAnnotation a)
    {
        if (a.Id.Length is 0 or > MaxIdLength || a.Id.Any(char.IsControl)) throw new ViewerAnnotationException($"An id must be 1 to {MaxIdLength} characters, without control characters.");
        if (a.Page < 1 || a.Page > _pages.Count) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": the document has no page {a.Page} (target.selector.node.index counts from 0).");
        if (a.Contents.Length > MaxContentsLength) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": bodyValue is longer than {MaxContentsLength} characters.");
        if (a.Author.Length > MaxAuthorLength) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": creator.name is longer than {MaxAuthorLength} characters.");
        if (!(a.Opacity >= 0 && a.Opacity <= 1)) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": opacity must be from 0 to 1.");
        if (!(a.StrokeWidth >= 0 && a.StrokeWidth <= MaxStrokeWidth)) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": strokeWidth must be from 0 to {MaxStrokeWidth}.");
        if (!(a.FontSize >= MinFontSize && a.FontSize <= MaxFontSize)) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": fontSize must be from {MinFontSize} to {MaxFontSize}.");
        var media = _pages[a.Page - 1].MediaBox;
        double grow = Tolerance + a.StrokeWidth;
        bool Inside(double x, double y) =>
            double.IsFinite(x) && double.IsFinite(y) && x >= media.Left - grow && x <= media.Right + grow && y >= media.Top - grow && y <= media.Bottom + grow;
        var b = a.Box;
        if (!(b.X2 >= b.X1 && b.Y2 >= b.Y1) || !Inside(b.X1, b.Y1) || !Inside(b.X2, b.Y2))
            throw new ViewerAnnotationException($"Annotation \"{a.Id}\": boundingBox must be [x1, y1, x2, y2] with x1 ≤ x2 and y1 ≤ y2, inside page {a.Page} ({Format(media)}).");
        if (a.IsMarkup)
        {
            if (a.QuadPoints.Count % 8 != 0 || a.QuadPoints.Count > 8 * MaxQuads)
                throw new ViewerAnnotationException($"Annotation \"{a.Id}\": quadPoints must have 8 numbers per quadrilateral, at most {MaxQuads} of them.");
            for (int i = 0; i < a.QuadPoints.Count; i += 2)
                if (!Inside(a.QuadPoints[i], a.QuadPoints[i + 1])) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": a quadPoints point is outside page {a.Page}.");
        }
        if (a.Kind == ViewerAnnotationKind.Ink)
        {
            if (a.InkList.Count is 0 or > MaxInkStrokes) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": inkList must have 1 to {MaxInkStrokes} strokes.");
            int total = 0;
            foreach (var stroke in a.InkList)
            {
                if (stroke.Count < 2 || stroke.Count % 2 != 0 || stroke.Count > 2 * MaxInkPointsPerStroke)
                    throw new ViewerAnnotationException($"Annotation \"{a.Id}\": each stroke of inkList must be x, y pairs, 1 to {MaxInkPointsPerStroke} points.");
                total += stroke.Count / 2;
                if (total > MaxInkPoints) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": inkList has more than {MaxInkPoints} points.");
                for (int i = 0; i < stroke.Count; i += 2)
                    if (!Inside(stroke[i], stroke[i + 1])) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": an inkList point is outside page {a.Page}.");
            }
        }
        if (a.HasVertices)
        {
            int least = a.Kind switch { ViewerAnnotationKind.Polygon => 3, _ => 2 };
            int most = a.Kind == ViewerAnnotationKind.Line ? 2 : MaxVertices;
            var v = a.Vertices;
            if (v.Count % 2 != 0 || v.Count / 2 < least || v.Count / 2 > most)
                throw new ViewerAnnotationException(a.Kind == ViewerAnnotationKind.Line
                    ? $"Annotation \"{a.Id}\": a line's vertices are its two points, [x1, y1, x2, y2]."
                    : $"Annotation \"{a.Id}\": vertices must be x, y pairs, {least} to {MaxVertices} points.");
            for (int i = 0; i < v.Count; i += 2)
                if (!Inside(v[i], v[i + 1])) throw new ViewerAnnotationException($"Annotation \"{a.Id}\": a vertices point is outside page {a.Page}.");
        }
        bool endings = a.StartEnding != "None" || a.EndEnding != "None";
        if (endings && (!a.HasEndings || !PdfAnnotationLimits.LineEndings.Contains(a.StartEnding) || !PdfAnnotationLimits.LineEndings.Contains(a.EndEnding)))
            throw new ViewerAnnotationException($"Annotation \"{a.Id}\": lineEndings are for lines and polylines, each one of {string.Join(", ", PdfAnnotationLimits.LineEndings)}.");
        if (a.Dash != null && (!a.IsShape || !ViewerAnnotationPdf.IsValidDash(a.Dash)))
            throw new ViewerAnnotationException($"Annotation \"{a.Id}\": dashPattern is for squares, circles, lines, polygons and polylines: 1 to {ViewerAnnotationPdf.MaxDashCount} numbers from 0 to {ViewerAnnotationPdf.MaxDashLength}, not all 0.");
        if (a.Kind == ViewerAnnotationKind.Stamp ? !ViewerAnnotationPdf.IsStampName(a.StampName) : a.StampName.Length > 0)
            throw new ViewerAnnotationException($"Annotation \"{a.Id}\": a stamp needs a stampName, such as Approved or Draft.");
        if (a.Kind == ViewerAnnotationKind.Reply ? a.InReplyTo.Length is 0 or > MaxIdLength || a.InReplyTo == a.Id : a.InReplyTo.Length > 0)
            throw new ViewerAnnotationException($"Annotation \"{a.Id}\": a reply (and only a reply) answers another annotation.");
    }

    private static string Format(PdfEngine.Geometry.PdfRect r) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{r.Left:0.##}, {r.Top:0.##}, {r.Right:0.##}, {r.Bottom:0.##}]");
}
