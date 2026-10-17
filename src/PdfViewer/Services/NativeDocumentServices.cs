using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using PdfEngine.Geometry;
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
    internal static readonly string[] ManagedSubtypes = { "Highlight", "Underline", "StrikeOut", "Text", "FreeText", "Square", "Circle", "Ink" };

    /// <summary>Where each loaded annotation came from, and what it looked like then, keyed by the model's id.</summary>
    private readonly Dictionary<Guid, (int Page, int Index, int ObjectNumber, string Fingerprint)> _loaded = new();

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
                if (ToModel(a, crop) is not { } model) continue;
                _loaded[model.Id] = (p, a.Index, a.ObjectNumber, Fingerprint(model));
                list.Add(model);
            }
        }
        return list;
    }

    private static AnnotationType? TypeOf(string subtype) => subtype switch
    {
        "Highlight" => AnnotationType.Highlight,
        "Underline" => AnnotationType.Underline,
        "StrikeOut" => AnnotationType.StrikeOut,
        "Text" => AnnotationType.Note,
        "FreeText" => AnnotationType.FreeText,
        "Square" => AnnotationType.Rectangle,
        "Circle" => AnnotationType.Ellipse,
        "Ink" => AnnotationType.Ink,
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
        _ => "Highlight",
    };

    internal static AnnotationModel? ToModel(PdfAnnotation a, PdfRect crop)
    {
        if (TypeOf(a.Subtype) is not AnnotationType type || a.Rect.IsEmpty && a.Rect.Width <= 0 && a.Rect.Height <= 0 && a.Rect.X == 0 && a.Rect.Y == 0)
            return null;
        double w = crop.Width > 0 ? crop.Width : 612, h = crop.Height > 0 ? crop.Height : 792;
        double ox = crop.Left, oy = crop.Top;
        var rect = a.Rect;

        // The colour from /C; for free text without one, the border colour of its /DA.
        var colour = PdfAnnotation.ToRgb(a.Color) ?? (a.Subtype == "FreeText" ? StrokeOf(a.DefaultAppearance) : null);
        string colorHex = colour is var (r, g, b) ? PdfNativeAnnotationService.Hex(r, g, b) : "#FF32CD32";
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
            StrokeThickness = a.BorderWidth is double bw && bw > 0 ? bw : 2.0,
            Contents = a.Contents ?? string.Empty,
            Author = string.IsNullOrEmpty(a.Author) ? "Author" : a.Author,
            Title = string.IsNullOrEmpty(a.Subject) ? type.ToString() : a.Subject,
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
        return model;
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
        foreach (var s in a.InkStrokes) { foreach (var p in s) sb.Append(CultureInfo.InvariantCulture, $"{p.X:F6},{p.Y:F6};"); sb.Append('/'); }
        sb.Append('|');
        foreach (var q in a.Quads) sb.Append(CultureInfo.InvariantCulture, $"{q.X:F6},{q.Y:F6},{q.Width:F6},{q.Height:F6};");
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
                if (Array.IndexOf(ManagedSubtypes, existing.Subtype) < 0) continue;
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
        var a = new PdfAnnotation { PageNumber = m.PageNumber, Subtype = SubtypeOf(m.Type) };
        Apply(a, m, crop, geometry: true);
        if (m.Type == AnnotationType.FreeText)
        {
            // The viewer draws free text black on a box outlined in the annotation's colour.
            var c = Rgb(m.ColorHex);
            a.Color = null;
            a.DefaultAppearance = $"/Helv 11 Tf 0 0 0 rg {F(c[0])} {F(c[1])} {F(c[2])} RG";
        }
        return a;
    }

    /// <summary>The annotation as read, with what the viewer edits replaced; everything else it carries is kept.</summary>
    private static PdfAnnotation Changed(PdfAnnotation existing, AnnotationModel m, PdfRect crop, bool geometryChanged)
    {
        string? before = existing.Contents;
        var colourBefore = existing.Color;
        Apply(existing, m, crop, geometryChanged);
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

    /// <summary>#RRGGBB or #AARRGGBB; lime green when it is neither, as the PDFium path wrote it.</summary>
    private static double[] Rgb(string? hex) =>
        PdfNativeAnnotationService.ParseHex(hex) ?? new[] { 50 / 255.0, 205 / 255.0, 50 / 255.0 };

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
