using System.Globalization;
using System.Xml.Linq;
using PdfEngine.Annotations;
using PdfEngine.Documents;
using PdfEngine.Exceptions;
using PdfEngine.Geometry;
using PdfEngine.Pages;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using PdfEngine.Save;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>Annotations in the engine's normalized model: 0..1 from the top-left of the unrotated crop box.</summary>
public sealed class PdfAnnotationService : IPdfAnnotationService
{
    public ValueTask<IReadOnlyList<AnnotationModel>> LoadAnnotationsAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        var crop = doc.Vector.PageTree.Pages[pageNumber - 1].CropBox;
        var result = new List<AnnotationModel>();
        foreach (var a in PdfAnnotationReader.Read(doc.Vector, pageNumber))
        {
            if (Map(a.Subtype) is not AnnotationType type) continue;
            var model = new AnnotationModel
            {
                PageNumber = pageNumber,
                Type = type,
                X = (a.Rect.Left - crop.Left) / crop.Width,
                Y = 1 - (a.Rect.Bottom - crop.Top) / crop.Height,
                Width = a.Rect.Width / crop.Width,
                Height = a.Rect.Height / crop.Height,
                Opacity = a.Opacity,
                Contents = a.Contents ?? string.Empty,
                Author = a.Author ?? string.Empty,
                StrokeThickness = a.BorderWidth ?? 2.0,
            };
            if (PdfAnnotation.ToRgb(a.Color) is var (r, g, b)) model.Color = Hex(r, g, b);
            if (PdfTextStrings.ParseDate(a.CreationDate) is DateTime created) model.CreatedAt = created;
            if (PdfTextStrings.ParseDate(a.ModifiedDate) is DateTime modified) model.ModifiedAt = modified;
            foreach (var q in a.QuadPoints)
            {
                double x0 = Math.Min(Math.Min(q[0], q[2]), Math.Min(q[4], q[6])), x1 = Math.Max(Math.Max(q[0], q[2]), Math.Max(q[4], q[6]));
                double y0 = Math.Min(Math.Min(q[1], q[3]), Math.Min(q[5], q[7])), y1 = Math.Max(Math.Max(q[1], q[3]), Math.Max(q[5], q[7]));
                model.QuadPoints.Add(new PdfRect((x0 - crop.Left) / crop.Width, 1 - (y1 - crop.Top) / crop.Height, (x1 - x0) / crop.Width, (y1 - y0) / crop.Height));
            }
            foreach (var stroke in a.InkList)
            {
                model.InkStrokes.Add(new InkStroke
                {
                    Color = model.Color,
                    Thickness = model.StrokeThickness,
                    Points = stroke.Select(p => new PdfPoint((p.X - crop.Left) / crop.Width, 1 - (p.Y - crop.Top) / crop.Height)).ToList(),
                });
            }
            result.Add(model);
        }
        return ValueTask.FromResult<IReadOnlyList<AnnotationModel>>(result);
    }

    /// <summary>Writes the document with <paramref name="annotations"/> added (and, for Flattened, every annotation drawn into the pages).</summary>
    public async ValueTask SaveAnnotatedDocumentAsync(IPdfDocument document, string targetPath, IReadOnlyList<AnnotationModel> annotations,
        PdfEngine.Annotations.AnnotationSaveMode mode, CancellationToken cancellationToken = default)
    {
        var doc = PdfVectorEngine.Require(document);
        PdfVectorEngine.RefuseOriginal(doc, targetPath, "An annotated copy");
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == PdfEngine.Annotations.AnnotationSaveMode.ExportXfdf)
        {
            await ExportXfdfAsync(doc, targetPath, annotations, cancellationToken).ConfigureAwait(false);
            return;
        }
        byte[] bytes = PdfAnnotationEditor.Apply(doc.Vector, doc.Bytes, editor =>
        {
            foreach (var model in annotations)
            {
                if (model.PageNumber < 1 || model.PageNumber > doc.PageCount) continue;
                editor.Add(ToAnnotation(doc, model));
            }
            if (mode == PdfEngine.Annotations.AnnotationSaveMode.Flattened) editor.FlattenAll();
        });
        await PdfVectorEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    internal static PdfAnnotation ToAnnotation(PdfEditableDocument doc, AnnotationModel m)
    {
        var crop = doc.Vector.PageTree.Pages[m.PageNumber - 1].CropBox;
        double X(double nx) => crop.Left + nx * crop.Width;
        double Y(double ny) => crop.Top + (1 - ny) * crop.Height;
        var a = new PdfAnnotation
        {
            PageNumber = m.PageNumber,
            Subtype = Subtype(m.Type),
            Rect = new PdfRect(X(m.X), Y(m.Y + m.Height), m.Width * crop.Width, m.Height * crop.Height),
            Color = ParseHex(m.Color),
            Opacity = Math.Clamp(m.Opacity, 0, 1),
            Contents = string.IsNullOrEmpty(m.Contents) ? null : m.Contents,
            Author = string.IsNullOrEmpty(m.Author) ? null : m.Author,
            CreationDate = PdfTextStrings.FormatDate(m.CreatedAt),
            ModifiedDate = PdfTextStrings.FormatDate(m.ModifiedAt),
            BorderWidth = m.StrokeThickness > 0 ? m.StrokeThickness : null,
        };
        if (m.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut)
        {
            var boxes = m.QuadPoints.Count > 0 ? m.QuadPoints : new List<PdfRect> { m.Bounds };
            foreach (var q in boxes)
                a.QuadPoints.Add(new[] { X(q.X), Y(q.Y), X(q.X + q.Width), Y(q.Y), X(q.X), Y(q.Y + q.Height), X(q.X + q.Width), Y(q.Y + q.Height) });
        }
        if (m.Type == AnnotationType.Ink)
            foreach (var s in m.InkStrokes.Where(s => s.Points.Count > 1))
                a.InkList.Add(s.Points.Select(p => new PdfPoint(X(p.X), Y(p.Y))).ToList());
        if (m.Type == AnnotationType.FreeText)
            a.DefaultAppearance = $"/Helv {PdfObjects(m.FontSize)} Tf {Rgb(ParseHex(m.FontColor) ?? new[] { 0.0, 0, 0 })} rg";
        return a;
    }

    private static string PdfObjects(double v) => Objects.PdfObjectWriter.FormatReal(Math.Round(v, 2));
    private static string Rgb(double[] c) => string.Join(" ", c.Select(v => Objects.PdfObjectWriter.FormatReal(Math.Round(v, 4))));

    internal static AnnotationType? Map(string subtype) => subtype switch
    {
        "Highlight" => AnnotationType.Highlight,
        "Underline" => AnnotationType.Underline,
        "StrikeOut" => AnnotationType.StrikeOut,
        "Text" => AnnotationType.Note,
        "FreeText" => AnnotationType.FreeText,
        "Square" => AnnotationType.Rectangle,
        "Circle" => AnnotationType.Ellipse,
        "Ink" => AnnotationType.Ink,
        "Stamp" => AnnotationType.Stamp,
        "Redact" => AnnotationType.Redaction,
        _ => null,
    };

    internal static string Subtype(AnnotationType type) => type switch
    {
        AnnotationType.Highlight => "Highlight",
        AnnotationType.Underline => "Underline",
        AnnotationType.StrikeOut => "StrikeOut",
        AnnotationType.Note => "Text",
        AnnotationType.FreeText => "FreeText",
        AnnotationType.Rectangle => "Square",
        AnnotationType.Ellipse => "Circle",
        AnnotationType.Ink => "Ink",
        AnnotationType.Stamp => "Stamp",
        AnnotationType.Redaction => "Redact",
        _ => "Highlight",
    };

    /// <summary>#RRGGBB or #AARRGGBB to RGB components, null when it is neither.</summary>
    public static double[]? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string h = hex.Trim().TrimStart('#');
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        return new[] { ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0 };
    }

    public static string Hex(double r, double g, double b)
    {
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{C(r):X2}{C(g):X2}{C(b):X2}";
    }

    /// <summary>XFDF (ISO 19444-1): rectangles and quadrilaterals in PDF points from the crop box's corner, colours as #RRGGBB.</summary>
    private static async ValueTask ExportXfdfAsync(PdfEditableDocument doc, string targetPath, IReadOnlyList<AnnotationModel> annotations, CancellationToken ct)
    {
        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var elements = new List<XElement>();
        foreach (var m in annotations)
        {
            if (m.PageNumber < 1 || m.PageNumber > doc.PageCount) continue;
            var a = ToAnnotation(doc, m);
            var e = new XElement(ns + Subtype(m.Type).ToLowerInvariant(),
                new XAttribute("page", m.PageNumber - 1),
                new XAttribute("rect", Join(a.Rect.Left, a.Rect.Top, a.Rect.Right, a.Rect.Bottom)),
                new XAttribute("color", Hex(a.Color?[0] ?? 0, a.Color?[1] ?? 0, a.Color?[2] ?? 0)),
                new XAttribute("opacity", m.Opacity.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("title", m.Author ?? string.Empty),
                new XElement(ns + "contents", m.Contents ?? string.Empty));
            if (a.QuadPoints.Count > 0) e.Add(new XAttribute("coords", string.Join(",", a.QuadPoints.Select(q => Join(q)))));
            elements.Add(e);
        }
        var xfdf = new XElement(ns + "xfdf", new XElement(ns + "annots", elements));
        string? directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await xfdf.SaveAsync(fs, System.Xml.Linq.SaveOptions.None, ct).ConfigureAwait(false);
    }

    private static string Join(params double[] values) => string.Join(",", values.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));
}
