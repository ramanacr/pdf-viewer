using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Services;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>
/// The viewer's document information, bookmarks and annotations from the vector engine's native
/// document services (M9), with the semantics the PDFium path had: annotation positions are
/// normalized to the unrotated crop box from its top-left corner, the same subtypes are read and
/// managed, and the same defaults fill in what a file leaves out.
/// </summary>
internal sealed class NativeDocumentServices
{
    /// <summary>The subtypes the viewer reads and writes; everything else on a page belongs to the document and is left alone.</summary>
    internal static readonly string[] ManagedSubtypes = { "Highlight", "Underline", "StrikeOut", "Text", "FreeText", "Square", "Circle", "Ink", "Stamp" };

    /// <summary>
    /// The viewer reads and writes this annotation: one of <see cref="ManagedSubtypes"/>, a line,
    /// polyline or polygon that is a measurement (has a rectilinear /Measure), or a plain line
    /// (with its /L), polyline or polygon (with its /Vertices). Loading and saving use the same
    /// test, so nothing the viewer does not show is ever deleted.
    /// </summary>
    internal static bool IsManaged(PdfAnnotation a, PdfVectorDocument doc) =>
        Array.IndexOf(ManagedSubtypes, a.Subtype) >= 0
        || a.Measure != null && PdfMeasurements.KindOf(a.Subtype) != null
        || IsPlainShape(a, doc);

    /// <summary>
    /// A line with its two points, or a polyline or polygon with enough of them, that is no
    /// measurement of any kind: its dictionary has no /Measure at all (a geospatial or broken one
    /// reads as none, and must still be left alone). A polygon or polyline with more points than
    /// the reader keeps is left alone too, so a save never writes it cut short.
    /// </summary>
    private static bool IsPlainShape(PdfAnnotation a, PdfVectorDocument doc)
    {
        bool shape = a.Subtype switch
        {
            "Line" => a.Line is { Length: 4 },
            "PolyLine" => a.Vertices.Count >= 2,
            "Polygon" => a.Vertices.Count >= 3,
            _ => false,
        };
        if (!shape || a.Measure != null || RawDictionary(doc, a) is not { } raw || raw.ContainsKey("Measure")) return false;
        return doc.Resolver.Resolve(raw["Vertices"]) is not PdfArray v || v.Count <= 2 * PdfAnnotationLimits.MaxVertices;
    }

    /// <summary>The annotation's own dictionary in its page's /Annots.</summary>
    private static PdfDictionary? RawDictionary(PdfVectorDocument doc, PdfAnnotation a)
    {
        if (a.PageNumber < 1 || a.PageNumber > doc.PageCount) return null;
        var r = doc.Resolver;
        return r.Resolve(doc.PageTree.Pages[a.PageNumber - 1].Dictionary["Annots"]) is PdfArray annots && a.Index >= 0 && a.Index < annots.Count
            ? r.Resolve(annots[a.Index]) as PdfDictionary
            : null;
    }

    /// <summary>Where each loaded annotation came from, and what it looked like then, keyed by the model's id.</summary>
    private readonly Dictionary<Guid, (int Page, int Index, int ObjectNumber, string Fingerprint)> _loaded = new();

    /// <summary>Where a loaded annotation came from (page, index in /Annots, object number), or null for one made here.</summary>
    internal (int Page, int Index, int ObjectNumber)? Origin(Guid id) =>
        _loaded.TryGetValue(id, out var o) ? (o.Page, o.Index, o.ObjectNumber) : null;

    /// <summary>
    /// The object numbers of the loaded annotations that <paramref name="annotations"/> has changed
    /// or no longer has: the page's own drawing of them is out of date.
    /// </summary>
    internal IReadOnlyCollection<int> ChangedOrDeleted(IEnumerable<AnnotationModel> annotations)
    {
        var byId = new Dictionary<Guid, AnnotationModel>();
        foreach (var m in annotations) byId.TryAdd(m.Id, m);
        var numbers = new List<int>();
        foreach (var (id, origin) in _loaded)
        {
            if (origin.ObjectNumber <= 0) continue;
            if (!byId.TryGetValue(id, out var m) || m.PageNumber != origin.Page || Fingerprint(m) != origin.Fingerprint)
                numbers.Add(origin.ObjectNumber);
        }
        return numbers;
    }

    /// <summary>The annotation is a loaded one, on its page and unchanged, so the page draws it from the file.</summary>
    internal bool IsAsLoaded(AnnotationModel m) =>
        _loaded.TryGetValue(m.Id, out var o) && o.Page == m.PageNumber && Fingerprint(m) == o.Fingerprint;

    // ------------------------------------------------------------------ metadata and bookmarks

    public static DocumentMetadata Metadata(PdfVectorDocument doc, string filePath)
    {
        var p = PdfDocumentInfo.ReadProperties(doc);
        var file = new FileInfo(filePath);
        double width = 612, height = 792;
        if (doc.PageCount > 0)
        {
            var size = doc.PageTree.Pages[0].PageSize;
            width = size.Width;
            height = size.Height;
        }
        return new DocumentMetadata
        {
            FileName = file.Name,
            FilePath = file.FullName,
            FileSizeBytes = file.Exists ? file.Length : 0,
            PageCount = doc.PageCount,
            Title = p.Title,
            Author = p.Author,
            Subject = p.Subject,
            Keywords = p.Keywords,
            Creator = p.Creator,
            Producer = p.Producer,
            CreationDate = p.CreationDate,
            ModDate = p.ModDate,
            PdfFormatVersion = p.HeaderVersion ?? "1.7",
            IsEncrypted = p.IsEncrypted,
            IsLinearized = false,
            DefaultPageWidthPt = width,
            DefaultPageHeightPt = height,
            LicenseStatus = "Vector engine (native document services)",
        };
    }

    public static ObservableCollection<BookmarkItem> Bookmarks(PdfVectorDocument doc)
    {
        var roots = new ObservableCollection<BookmarkItem>();
        foreach (var item in PdfDocumentInfo.ReadOutline(doc)) roots.Add(Convert(item, doc.PageCount));
        return roots;
    }

    private static BookmarkItem Convert(PdfOutlineItem item, int pageCount)
    {
        var model = new BookmarkItem
        {
            Title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title,
            // A destination outside the document goes nowhere new: page 1, as the PDFium path did.
            TargetPageNumber = item.PageIndex >= 0 && item.PageIndex < pageCount ? item.PageIndex + 1 : 1,
            IsBold = (item.Style & 2) != 0,
            IsItalic = (item.Style & 1) != 0,
        };
        foreach (var child in item.Children) model.Children.Add(Convert(child, pageCount));
        return model;
    }

    // ------------------------------------------------------------------ loading annotations

    public List<AnnotationModel> Load(PdfVectorDocument doc)
    {
        _loaded.Clear();
        var list = new List<AnnotationModel>();
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var crop = doc.PageTree.Pages[p - 1].CropBox;
            foreach (var a in PdfAnnotationReader.Read(doc, p))
            {
                if (ToModel(a, crop, doc) is not { } model) continue;
                _loaded[model.Id] = (p, a.Index, a.ObjectNumber, Fingerprint(model));
                list.Add(model);
            }
        }
        return list;
    }

    private static AnnotationType? TypeOf(PdfAnnotation a) => a.Measure != null && PdfMeasurements.KindOf(a.Subtype) != null
        ? AnnotationType.Measurement
        : a.Subtype switch
    {
        // A line with an ending at either end is an arrow (the measuring tools write theirs with /Measure).
        "Line" => a.LineEndings?.Any(e => e != "None") == true ? AnnotationType.Arrow : AnnotationType.Line,
        "Polygon" => AnnotationType.Polygon,
        "PolyLine" => AnnotationType.PolyLine,
        "Highlight" => AnnotationType.Highlight,
        "Underline" => AnnotationType.Underline,
        "StrikeOut" => AnnotationType.StrikeOut,
        "Text" => AnnotationType.Note,
        "FreeText" => AnnotationType.FreeText,
        "Square" => AnnotationType.Rectangle,
        "Circle" => AnnotationType.Ellipse,
        "Ink" => AnnotationType.Ink,
        "Stamp" => AnnotationType.Stamp,
        _ => null,
    };

    private static string SubtypeOf(AnnotationType type) => type switch
    {
        AnnotationType.Underline => "Underline",
        AnnotationType.StrikeOut => "StrikeOut",
        AnnotationType.Note => "Text",
        AnnotationType.FreeText => "FreeText",
        AnnotationType.Rectangle => "Square",
        AnnotationType.Ellipse => "Circle",
        AnnotationType.Ink => "Ink",
        AnnotationType.Line or AnnotationType.Arrow => "Line",
        AnnotationType.Polygon => "Polygon",
        AnnotationType.PolyLine => "PolyLine",
        _ => "Highlight",
    };

    internal static AnnotationModel? ToModel(PdfAnnotation a, PdfRect crop, PdfVectorDocument doc)
    {
        if (!IsManaged(a, doc) || TypeOf(a) is not AnnotationType type || a.Rect.IsEmpty && a.Rect.Width <= 0 && a.Rect.Height <= 0 && a.Rect.X == 0 && a.Rect.Y == 0)
            return null;
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        double ox = crop.Left, oy = crop.Top;
        var rect = a.Rect;

        // The colour from /C; for free text without one, the border colour of its /DA.
        var colour = PdfAnnotation.ToRgb(a.Color) ?? (a.Subtype == "FreeText" ? StrokeOf(a.DefaultAppearance) : null);
        string colorHex = LoadedColorHex(a, type);
        bool shape = IsVertexShapeType(type);
        double opacity = colour != null ? a.Opacity : (a.Opacity < 1 ? a.Opacity : type == AnnotationType.Highlight ? 0.4 : 1.0);

        var model = new AnnotationModel
        {
            PageNumber = 0,
            Type = type,
            X = Math.Max(0, (rect.Left - ox) / w),
            Y = Math.Max(0, 1.0 - (rect.Bottom - oy) / h),
            Width = Math.Max(0.01, rect.Width / w),
            Height = Math.Max(0.01, rect.Height / h),
            ColorHex = colorHex,
            Opacity = Math.Clamp(opacity, 0, 1),
            StrokeThickness = LoadedWidth(a, type),
            Contents = a.Contents ?? string.Empty,
            // A line or polygon from another program keeps no author or subject it did not have.
            Author = string.IsNullOrEmpty(a.Author) ? (shape ? string.Empty : "Author") : a.Author,
            Title = string.IsNullOrEmpty(a.Subject) ? (shape ? string.Empty : type.ToString()) : a.Subject,
            // The document's own date, not the moment it was opened.
            CreationDate = PdfTextStrings.ParseDate(a.CreationDate) ?? PdfTextStrings.ParseDate(a.ModifiedDate) ?? DateTime.Now,
        };
        model.PageNumber = a.PageNumber;

        if (type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut && a.QuadPoints.Count > 1)
        {
            var quads = a.QuadPoints.Select(q =>
            {
                double x0 = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), x1 = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
                double y0 = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), y1 = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
                return new Rect((x0 - ox) / w, 1.0 - (y1 - oy) / h, (x1 - x0) / w, (y1 - y0) / h);
            }).ToList();
            model.SetQuads(quads);
        }
        if (type == AnnotationType.Ink)
        {
            foreach (var stroke in a.InkList.Where(s => s.Count > 0))
                model.InkStrokes.Add(stroke.Select(pt => new Point(Math.Max(0, (pt.X - ox) / w), Math.Max(0, 1.0 - (pt.Y - oy) / h))).ToList());
        }
        if (type == AnnotationType.Measurement)
        {
            var points = a.Line is { Length: 4 } l ? new[] { new PdfPoint(l[0], l[1]), new PdfPoint(l[2], l[3]) } : a.Vertices.ToArray();
            model.MeasureKind = PdfMeasurements.KindOf(a.Subtype) ?? PdfMeasurementKind.Distance;
            model.Measure = a.Measure;
            model.Vertices = points.Select(p => new Point((p.X - ox) / w, 1.0 - (p.Y - oy) / h)).ToList();
            model.HasOwnAppearance = a.HasAppearance;
            model.Opacity = a.Opacity;
        }
        if (model.IsVertexShape)
        {
            var points = a.Line is { Length: 4 } l ? new[] { new PdfPoint(l[0], l[1]), new PdfPoint(l[2], l[3]) } : a.Vertices.ToArray();
            model.Vertices = points.Select(p => new Point((p.X - ox) / w, 1.0 - (p.Y - oy) / h)).ToList();
            // Endings as the file gives them (a single name too), so a save writes them back unchanged.
            model.LineEndings = type != AnnotationType.Polygon && a.LineEndings is { Length: > 0 } ends ? ends.ToArray() : null;
            model.HasOwnAppearance = a.HasAppearance;
            model.Opacity = a.Opacity;
        }
        if (model.IsStyledShape)
        {
            // The fill and dash as the file gives them, so a save writes them back unchanged.
            model.InteriorColorHex = HexOf(a.InteriorColor);
            model.BorderDash = LoadedDash(a);
            // The page draws a loaded rectangle or ellipse from its own appearance (with its
            // opacity, clouds and /RD) until it is changed; only then does the overlay draw it.
            if (type is AnnotationType.Rectangle or AnnotationType.Ellipse) model.HasOwnAppearance = a.HasAppearance;
        }
        if (type == AnnotationType.FreeText) model.TextRotation = a.TextRotation;
        if (type == AnnotationType.Stamp)
        {
            model.StampName = a.Icon ?? "Draft";
            model.StampRotation = a.AppearanceRotation;
            model.FileStampRotation = a.AppearanceRotation;
            model.StampSize = a.AppearanceSize is { } upright ? new Size(upright.Width, upright.Height) : new Size(rect.Width, rect.Height);
            model.HasOwnAppearance = a.HasAppearance;
            model.Opacity = a.Opacity;
        }
        return model;
    }

    /// <summary>A point of the normalized page (top-left origin) in default user space.</summary>
    internal static PdfPoint ToPage(Point p, PdfRect crop)
    {
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        return new PdfPoint(crop.Left + p.X * w, crop.Top + (1.0 - p.Y) * h);
    }

    /// <summary>A point of default user space on the normalized page.</summary>
    internal static Point FromPage(PdfPoint p, PdfRect crop)
    {
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        return new Point((p.X - crop.Left) / w, 1.0 - (p.Y - crop.Top) / h);
    }

    /// <summary>The normalized box of a rectangle of default user space.</summary>
    internal static Rect FromPage(PdfRect r, PdfRect crop)
    {
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        return new Rect((r.Left - crop.Left) / w, 1.0 - (r.Bottom - crop.Top) / h, r.Width / w, r.Height / h);
    }

    /// <summary>
    /// The measurement annotation for a model: its points in page space, measured with its
    /// /Measure, the result as its label. The box is the annotation's rectangle.
    /// </summary>
    internal static PdfAnnotation MeasurementAnnotation(AnnotationModel m, PdfRect crop)
    {
        var measure = m.Measure ?? PdfMeasure.FromScale(1, "in", 1, "in");
        var points = m.Vertices.Select(p => ToPage(p, crop)).ToList();
        if (m.MeasureKind == PdfMeasurementKind.Distance && points.Count > 2) points = new List<PdfPoint> { points[0], points[^1] };
        return PdfMeasurements.Create(m.MeasureKind, m.PageNumber, points, measure, Rgb(m.ColorHex), m.StrokeThickness > 0 ? m.StrokeThickness : 1);
    }

    private static bool IsVertexShapeType(AnnotationType type) =>
        type is AnnotationType.Line or AnnotationType.Arrow or AnnotationType.Polygon or AnnotationType.PolyLine;

    /// <summary>
    /// The model's colour for an annotation as read. A line or polygon without /C is not stroked:
    /// fully transparent. Anything else without one is lime green, as the PDFium path showed it.
    /// </summary>
    private static string LoadedColorHex(PdfAnnotation a, AnnotationType type)
    {
        var colour = PdfAnnotation.ToRgb(a.Color) ?? (a.Subtype == "FreeText" ? StrokeOf(a.DefaultAppearance) : null);
        return colour is var (r, g, b) ? PdfAnnotationService.Hex(r, g, b)
            : IsVertexShapeType(type) ? "#00000000"
            : "#FF32CD32";
    }

    /// <summary>The model's line width for an annotation as read: a line or polygon keeps its own (1 when it gives none, as the standard says), others 2 when they give none.</summary>
    private static double LoadedWidth(PdfAnnotation a, AnnotationType type) =>
        IsVertexShapeType(type) ? Math.Max(0, a.BorderWidth ?? 1)
        : a.BorderWidth is double bw && bw > 0 ? bw : 2.0;

    /// <summary>An /IC or /C colour as #RRGGBB; null for none.</summary>
    private static string? HexOf(double[]? colour) =>
        PdfAnnotation.ToRgb(colour) is var (r, g, b) ? PdfAnnotationService.Hex(r, g, b) : null;

    /// <summary>
    /// The dash of an annotation's border as read: /BS /S /D with its /D (the standard's [3] when
    /// it gives none), or the dash of a /Border array; null for a solid line.
    /// </summary>
    internal static double[]? LoadedDash(PdfAnnotation a)
    {
        var dash = a.BorderDash is { Length: > 0 } d && d.All(v => v >= 0 && double.IsFinite(v)) && d.Sum() > 0 ? d : null;
        return a.BorderStyle == "D" ? dash?.ToArray() ?? new[] { 3.0 }
            : a.BorderStyle == null ? dash?.ToArray()
            : null;
    }

    private static bool SameDash(double[]? x, double[]? y) =>
        (x ?? Array.Empty<double>()).AsSpan().SequenceEqual(y ?? Array.Empty<double>());

    private static bool SameEndings(string[]? x, string[]? y) =>
        (x ?? Array.Empty<string>()).AsSpan().SequenceEqual(y ?? Array.Empty<string>());

    /// <summary>A new shape's fill (/IC) and dash (/BS /S /D).</summary>
    private static void ApplyStyle(PdfAnnotation a, AnnotationModel m)
    {
        a.InteriorColor = m.InteriorColorHex != null ? Rgb(m.InteriorColorHex) : null;
        a.BorderStyle = m.BorderDash != null ? "D" : null;
        a.BorderDash = m.BorderDash?.ToArray();
    }

    /// <summary>
    /// A loaded shape's fill and dash: what the user changed is written, anything else stays as
    /// the file had it (a beveled border, a dash given in /Border, no /IC).
    /// </summary>
    private static void ApplyChangedStyle(PdfAnnotation existing, AnnotationModel m)
    {
        if (!string.Equals(m.InteriorColorHex, HexOf(existing.InteriorColor), StringComparison.OrdinalIgnoreCase))
            existing.InteriorColor = m.InteriorColorHex != null ? Rgb(m.InteriorColorHex) : null;
        if (!SameDash(m.BorderDash, LoadedDash(existing)))
        {
            existing.BorderStyle = m.BorderDash != null ? "D" : "S";
            existing.BorderDash = m.BorderDash?.ToArray();
            existing.BorderWidth ??= Math.Max(0, m.StrokeThickness); // the dash lives in /BS, which needs a width
        }
    }

    /// <summary>A line's or connected lines' endings as saved: as read or set, else an open arrow at the end of an arrow.</summary>
    internal static string[]? ShapeEndings(AnnotationModel m) =>
        m.Type == AnnotationType.Polygon ? null
        : m.LineEndings is { Length: > 0 } le ? le
        : m.Type == AnnotationType.Arrow ? new[] { "None", "OpenArrow" }
        : null;

    /// <summary>
    /// The rectangle a line, arrow, polygon or connected lines is drawn in: its points grown by
    /// half the line width, or by the arrow heads (nine line widths long at 30 degrees) when it
    /// has line endings.
    /// </summary>
    internal static PdfRect ShapeBounds(IReadOnlyList<PdfPoint> points, double width, bool endings)
    {
        double margin = endings ? 5 * Math.Max(width, 1) + 1 : width / 2 + 1;
        double minX = points.Min(p => p.X) - margin, minY = points.Min(p => p.Y) - margin;
        double maxX = points.Max(p => p.X) + margin, maxY = points.Max(p => p.Y) + margin;
        return new PdfRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// A line, arrow, polygon or connected lines takes the model's points (/L or /Vertices), its
    /// endings (/LE) and a rectangle around them; the annotation editor then draws its appearance.
    /// </summary>
    private static void ApplyShape(PdfAnnotation a, AnnotationModel m, PdfRect crop, bool setEndings)
    {
        var points = m.Vertices.Select(p => ToPage(p, crop)).ToList();
        if (points.Count < 2) return;
        // A loaded shape keeps its /LE as read unless the user chose other endings.
        var ends = setEndings ? ShapeEndings(m) : a.LineEndings;
        var bounds = ShapeBounds(points, m.StrokeThickness > 0 ? m.StrokeThickness : 1, ends?.Any(e => e != "None") == true);
        // A loaded shape's rectangle may hold more than its points (a caption, leader lines): it is kept, moved with them.
        var box = BoxOf(m, crop);
        double minX = Math.Min(bounds.X, box.X), minY = Math.Min(bounds.Y, box.Y);
        double maxX = Math.Max(bounds.X + bounds.Width, box.X + box.Width), maxY = Math.Max(bounds.Y + bounds.Height, box.Y + box.Height);
        a.Rect = new PdfRect(minX, minY, maxX - minX, maxY - minY);
        if (m.Type is AnnotationType.Line or AnnotationType.Arrow)
        {
            a.Line = new[] { points[0].X, points[0].Y, points[^1].X, points[^1].Y };
            a.Vertices = new List<PdfPoint>();
        }
        else
        {
            a.Line = null;
            a.Vertices = points;
        }
        if (setEndings) a.LineEndings = ends?.ToArray();
    }

    /// <summary>The stamp annotation for a model, with how its appearance is drawn (a new stamp, or one given a new look).</summary>
    internal static PdfAnnotation StampAnnotation(AnnotationModel m, PdfRect rect)
    {
        var size = m.StampSize.Width > 0 && m.StampSize.Height > 0 ? m.StampSize : new Size(rect.Width, rect.Height);
        PdfEngine.Vector.Editing.PdfImageContent? image = null;
        if (m.StampImage is { Length: > 0 } picture) image = EditImageLoader.Load(picture);
        return new PdfAnnotation
        {
            PageNumber = m.PageNumber,
            Subtype = "Stamp",
            Icon = string.IsNullOrEmpty(m.StampName) ? "Draft" : m.StampName,
            Rect = rect,
            Opacity = Math.Clamp(m.Opacity, 0, 1),
            Contents = string.IsNullOrEmpty(m.Contents) ? null : m.Contents,
            Stamp = new PdfStampAppearance
            {
                Subtitle = m.StampSubtitle,
                Rotation = m.StampRotation,
                Width = size.Width,
                Height = size.Height,
                Image = image,
                SourcePdf = m.StampPdf,
                SourcePage = 1,
            },
        };
    }

    /// <summary>The stroking colour ("r g b RG", "g G", "c m y k K") of a /DA string.</summary>
    private static (double R, double G, double B)? StrokeOf(string? da)
    {
        if (string.IsNullOrWhiteSpace(da)) return null;
        var tokens = da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            int n = tokens[i] switch { "RG" => 3, "G" => 1, "K" => 4, _ => 0 };
            if (n == 0 || i < n) continue;
            var values = new double[n];
            for (int k = 0; k < n; k++)
                if (!double.TryParse(tokens[i - n + k], NumberStyles.Float, CultureInfo.InvariantCulture, out values[k])) return null;
            return PdfAnnotation.ToRgb(values);
        }
        return null;
    }

    /// <summary>What a save compares to decide whether a loaded annotation changed.</summary>
    internal static string Fingerprint(AnnotationModel a)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{a.Type}|{a.PageNumber}|{a.X:F6},{a.Y:F6},{a.Width:F6},{a.Height:F6}|{a.ColorHex}|{a.Opacity:F4}|{a.StrokeThickness:F3}|");
        sb.Append(a.Contents).Append('|').Append(a.Author).Append('|').Append(a.Title).Append('|').Append(a.CreationDate.Ticks).Append('|');
        // A shape's fill, endings and dash (one part, so the geometry stays the last two).
        sb.Append(a.InteriorColorHex).Append(';').Append(string.Join(",", a.LineEndings ?? Array.Empty<string>())).Append(';')
            .Append(string.Join(",", (a.BorderDash ?? Array.Empty<double>()).Select(v => v.ToString("R", CultureInfo.InvariantCulture)))).Append('|');
        foreach (var s in a.InkStrokes) { foreach (var p in s) sb.Append(CultureInfo.InvariantCulture, $"{p.X:F6},{p.Y:F6};"); sb.Append('/'); }
        sb.Append('|');
        foreach (var q in a.Quads) sb.Append(CultureInfo.InvariantCulture, $"{q.X:F6},{q.Y:F6},{q.Width:F6},{q.Height:F6};");
        // A measurement's points and scale, and a stamp's look, count as its geometry (the last part).
        foreach (var v in a.Vertices) sb.Append(CultureInfo.InvariantCulture, $"v{v.X:F6},{v.Y:F6};");
        if (a.Measure is { } m) sb.Append("m").Append(m.Ratio.Replace('|', ' ')).Append(CultureInfo.InvariantCulture, $"{m.X[0].Factor:R}");
        if (a.Type == AnnotationType.Stamp)
            sb.Append(CultureInfo.InvariantCulture, $"s{a.StampName.Replace('|', ' ')},{a.StampRotation:F3},{a.StampSize.Width:F3},{a.StampSize.Height:F3}");
        return sb.ToString();
    }

    private static string Geometry(string fingerprint)
    {
        var parts = fingerprint.Split('|');
        return parts.Length > 3 ? parts[2] + "|" + string.Join("|", parts.Skip(parts.Length - 2)) : fingerprint;
    }

    // ------------------------------------------------------------------ saving annotations

    /// <summary>
    /// The document with exactly <paramref name="annotations"/> as its managed annotations: loaded
    /// ones that did not change are kept as they are (appearance, replies, pop-ups and all), changed
    /// ones are rewritten and drawn again, missing ones are deleted, new ones are added. A signed
    /// document gets an incremental update so its signatures stay valid; any other is written whole,
    /// so deleted comments do not linger in an earlier revision. Flattened draws every annotation
    /// into its page and is always written whole.
    /// </summary>
    public byte[] Save(PdfVectorDocument doc, byte[] bytes, IEnumerable<AnnotationModel> annotations, bool flatten)
    {
        var models = annotations.Where(a => a.PageNumber >= 1 && a.PageNumber <= doc.PageCount).ToList();
        var byId = new Dictionary<Guid, AnnotationModel>();
        foreach (var m in models) byId.TryAdd(m.Id, m);
        var byOrigin = _loaded.Where(e => byId.ContainsKey(e.Key))
            .ToDictionary(e => (e.Value.Page, e.Value.Index), e => (Id: e.Key, e.Value.ObjectNumber, e.Value.Fingerprint));

        var update = new PdfUpdate(doc, bytes);
        var editor = new PdfAnnotationEditor(update);
        var handled = new HashSet<Guid>();
        for (int p = 1; p <= doc.PageCount; p++)
        {
            var crop = doc.PageTree.Pages[p - 1].CropBox;
            foreach (var existing in PdfAnnotationReader.Read(doc, p))
            {
                if (!IsManaged(existing, doc)) continue;
                if (byOrigin.TryGetValue((p, existing.Index), out var origin) && origin.ObjectNumber == existing.ObjectNumber
                    && byId.TryGetValue(origin.Id, out var model) && model.PageNumber == p && handled.Add(origin.Id))
                {
                    string now = Fingerprint(model);
                    if (now != origin.Fingerprint)
                        editor.Modify(Changed(existing, model, crop, geometryChanged: Geometry(now) != Geometry(origin.Fingerprint)));
                    continue;
                }
                editor.Delete(p, existing.Index, cascadeReplies: false);
            }
        }
        foreach (var model in models.Where(m => !handled.Contains(m.Id)))
            editor.Add(New(model, doc.PageTree.Pages[model.PageNumber - 1].CropBox));
        if (flatten) editor.FlattenAll();
        editor.Finish();
        return update.Commit(forceFullRewrite: flatten || !IsSigned(doc));
    }

    /// <summary>The document carries a signed signature field (FPDF_GetSignatureCount).</summary>
    internal static bool IsSigned(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        if (r.Resolve(doc.Catalog["AcroForm"]) is not PdfDictionary form || r.Resolve(form["Fields"]) is not PdfArray fields) return false;
        var stack = new Stack<(PdfObject Field, int Depth)>(fields.Select(f => (f, 0)));
        var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            var (f, depth) = stack.Pop();
            if (depth > 32 || f is PdfIndirectRef ir && !seen.Add(ir.ObjectNumber) || r.Resolve(f) is not PdfDictionary d) continue;
            if (d.GetName("FT") == "Sig" && r.Resolve(d["V"]) is PdfDictionary) return true;
            if (r.Resolve(d["Kids"]) is PdfArray kids) foreach (var k in kids) stack.Push((k, depth + 1));
        }
        return false;
    }

    private static PdfAnnotation New(AnnotationModel m, PdfRect crop)
    {
        if (m.Type == AnnotationType.Measurement)
        {
            var measurement = MeasurementAnnotation(m, crop);
            Apply(measurement, m, crop, geometry: false);
            measurement.Contents = string.IsNullOrEmpty(m.Contents) ? measurement.Contents : m.Contents;
            return measurement;
        }
        if (m.Type == AnnotationType.Stamp)
        {
            var stamp = StampAnnotation(m, BoxOf(m, crop));
            Apply(stamp, m, crop, geometry: true);
            stamp.Color = null; // a designed stamp is drawn in its own colour
            stamp.BorderWidth = null;
            return stamp;
        }
        var a = new PdfAnnotation { PageNumber = m.PageNumber, Subtype = SubtypeOf(m.Type) };
        Apply(a, m, crop, geometry: true);
        if (m.IsVertexShape) ApplyShape(a, m, crop, setEndings: true);
        if (m.IsStyledShape) ApplyStyle(a, m);
        if (m.Type == AnnotationType.FreeText)
        {
            // The viewer draws free text black on a box outlined in the annotation's colour.
            var c = Rgb(m.ColorHex);
            a.Color = null;
            a.DefaultAppearance = $"/Helv 11 Tf 0 0 0 rg {F(c[0])} {F(c[1])} {F(c[2])} RG";
            // Made in a turned view: turned to read upright there (/Rotate, as Acrobat writes it).
            a.TextRotation = m.TextRotation;
        }
        return a;
    }

    /// <summary>The annotation as read, with what the viewer edits replaced; everything else it carries is kept.</summary>
    private static PdfAnnotation Changed(PdfAnnotation existing, AnnotationModel m, PdfRect crop, bool geometryChanged)
    {
        string? before = existing.Contents;
        var colourBefore = existing.Color;
        if (m.Type == AnnotationType.Stamp)
        {
            // Its appearance is kept: a new rectangle moves and sizes it, as 12.5.5 fits it, and a
            // new turn goes into its form's /Matrix.
            var width = existing.BorderWidth;
            double turn = PdfStamps.NormalizeDegrees(m.StampRotation - existing.AppearanceRotation);
            existing.AppearanceTurn = Math.Abs(turn) < 1e-3 || Math.Abs(turn - 360) < 1e-3 ? 0 : turn;
            Apply(existing, m, crop, geometryChanged);
            existing.Color = colourBefore;
            existing.BorderWidth = width;
            return existing;
        }
        if (m.Type == AnnotationType.Measurement)
        {
            Apply(existing, m, crop, geometry: false);
            if (geometryChanged || m.Measure is { } measure && !measure.SameAs(existing.Measure))
            {
                var redrawn = MeasurementAnnotation(m, crop);
                existing.Rect = redrawn.Rect;
                existing.Line = redrawn.Line;
                existing.Vertices = redrawn.Vertices;
                existing.Measure = redrawn.Measure;
            }
            return existing;
        }
        var widthBefore = existing.BorderWidth;
        string? authorBefore = existing.Author, subjectBefore = existing.Subject;
        string loadedHex = LoadedColorHex(existing, m.Type);
        double loadedWidth = LoadedWidth(existing, m.Type);
        Apply(existing, m, crop, geometryChanged);
        if (m.IsVertexShape || m.Type is AnnotationType.Rectangle or AnnotationType.Ellipse)
        {
            // What the viewer filled in for what the file left out is not written back: no /C
            // stays no colour, no /BS (or /W 0) stays as it was, unless the user chose another.
            if (string.Equals(m.ColorHex, loadedHex, StringComparison.OrdinalIgnoreCase)) existing.Color = colourBefore;
            if (Math.Abs(m.StrokeThickness - loadedWidth) < 1e-9) existing.BorderWidth = widthBefore;
            ApplyChangedStyle(existing, m);
        }
        if (m.IsVertexShape)
        {
            if (string.IsNullOrEmpty(authorBefore) && string.IsNullOrEmpty(m.Author)) existing.Author = authorBefore;
            if (string.IsNullOrEmpty(subjectBefore) && string.IsNullOrEmpty(m.Title)) existing.Subject = subjectBefore;
            // Its rectangle follows its points, line width and endings; its /LE changes only when the user chose others.
            ApplyShape(existing, m, crop, setEndings: m.HasLineEndings && !SameEndings(m.LineEndings, existing.LineEndings));
        }
        if (!string.Equals(before ?? string.Empty, m.Contents ?? string.Empty, StringComparison.Ordinal))
            existing.RichContents = null; // rich text of the old contents would contradict the new
        if (m.Type == AnnotationType.FreeText && colourBefore == null)
            existing.Color = null; // its colour lives in /DA
        return existing;
    }

    private static void Apply(PdfAnnotation a, AnnotationModel m, PdfRect crop, bool geometry)
    {
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        double ox = crop.Left, oy = crop.Top;
        double X(double nx) => ox + nx * w;
        double Y(double ny) => oy + (1.0 - ny) * h;

        a.Color = Rgb(m.ColorHex);
        a.Opacity = Math.Clamp(m.Opacity, 0, 1);
        a.Contents = string.IsNullOrEmpty(m.Contents) ? null : m.Contents;
        a.Author = string.IsNullOrEmpty(m.Author) ? null : m.Author;
        a.Subject = string.IsNullOrEmpty(m.Title) ? null : m.Title;
        a.CreationDate = AnnotationExchange.FormatPdfDate(m.CreationDate);
        a.ModifiedDate = AnnotationExchange.FormatPdfDate(m.ModifiedDate);
        if (m.StrokeThickness > 0) a.BorderWidth = m.StrokeThickness;
        if (!geometry) return;

        a.Rect = new PdfRect(X(m.X), Y(m.Y + m.Height), m.Width * w, m.Height * h);
        a.QuadPoints.Clear();
        if (m.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut)
        {
            // One quadrilateral per line of a multi-line markup, else the box: other readers draw markup from these.
            var boxes = m.Quads.Count > 0 ? m.Quads : new List<Rect> { new(m.X, m.Y, m.Width, m.Height) };
            foreach (var q in boxes)
                a.QuadPoints.Add(new[] { X(q.X), Y(q.Y), X(q.X + q.Width), Y(q.Y), X(q.X), Y(q.Y + q.Height), X(q.X + q.Width), Y(q.Y + q.Height) });
        }
        a.InkList.Clear();
        if (m.Type == AnnotationType.Ink)
            foreach (var stroke in m.InkStrokes.Where(s => s != null && s.Count >= 2))
                a.InkList.Add(stroke.Select(p => new PdfPoint(X(p.X), Y(p.Y))).ToList());
    }

    /// <summary>The model's box in default user space.</summary>
    internal static PdfRect BoxOf(AnnotationModel m, PdfRect crop)
    {
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        return new PdfRect(crop.Left + m.X * w, crop.Top + (1.0 - m.Y - m.Height) * h, m.Width * w, m.Height * h);
    }

    /// <summary>#RRGGBB or #AARRGGBB; lime green when it is neither, as the PDFium path wrote it.</summary>
    private static double[] Rgb(string? hex) =>
        PdfAnnotationService.ParseHex(hex) ?? new[] { 50 / 255.0, 205 / 255.0, 50 / 255.0 };

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));

    /// <summary>Unrotated page sizes and origins for the XFDF export.</summary>
    public static (Dictionary<int, (double Width, double Height)> Sizes, Dictionary<int, (double X, double Y)> Origins) PageFrames(PdfVectorDocument doc, IEnumerable<int> pages)
    {
        var sizes = new Dictionary<int, (double, double)>();
        var origins = new Dictionary<int, (double, double)>();
        foreach (int p in pages.Distinct().Where(p => p >= 1 && p <= doc.PageCount))
        {
            var crop = doc.PageTree.Pages[p - 1].CropBox;
            sizes[p] = (crop.Width, crop.Height);
            origins[p] = (crop.Left, crop.Top);
        }
        return (sizes, origins);
    }
}
