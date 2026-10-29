// Annotation exchange formats shared by both engines (and the only ones the published app has).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using PdfViewer.Models;

namespace PdfViewer.Services;

/// <summary>PDF date strings and XFDF export, independent of the engine that read the annotations.</summary>
public static class AnnotationExchange
{
    /// <summary>
    /// PDF date syntax, as in "D:20260906143000+05'30'". Anything else is ignored by other
    /// viewers, which is why the date column was blank for annotations this app wrote.
    /// </summary>
    internal static string FormatPdfDate(DateTime value)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(value);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "D:{0:yyyyMMddHHmmss}{1}{2:00}'{3:00}'",
            value, sign, magnitude.Hours, magnitude.Minutes);
    }

    /// <summary>
    /// Exports annotations to an XFDF XML comments file.
    /// </summary>
    /// <param name="pageSizes">
    /// Page number -> (width, height) in PDF points. Required to emit spec-conformant
    /// coordinates; when a page is missing, US Letter is assumed.
    /// </param>
    public static void ExportAnnotationsToXfdf(
        string xfdfPath,
        IEnumerable<AnnotationModel> annotations,
        IReadOnlyDictionary<int, (double Width, double Height)>? pageSizes = null,
        IReadOnlyDictionary<int, (double X, double Y)>? pageOrigins = null)
    {
        string? outDir = Path.GetDirectoryName(xfdfPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        using var writer = new StreamWriter(xfdfPath, false, Encoding.UTF8);
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\" xml:space=\"preserve\">");
        writer.WriteLine("  <annots>");

        foreach (var a in annotations)
        {
            string dateStr = a.CreationDate.ToString("yyyyMMddHHmmss");
            string annotTag = a.Type switch
            {
                AnnotationType.Highlight => "highlight",
                AnnotationType.Underline => "underline",
                AnnotationType.StrikeOut => "strikeout",
                AnnotationType.Note => "text",
                AnnotationType.FreeText => "freetext",
                AnnotationType.Rectangle => "square",
                AnnotationType.Ellipse => "circle",
                AnnotationType.Ink => "ink",
                AnnotationType.Stamp => "stamp",
                AnnotationType.Line or AnnotationType.Arrow => "line",
                AnnotationType.Polygon => "polygon",
                AnnotationType.PolyLine => "polyline",
                AnnotationType.Measurement => a.MeasureKind switch
                {
                    PdfEngine.Vector.Annotations.PdfMeasurementKind.Distance => "line",
                    PdfEngine.Vector.Annotations.PdfMeasurementKind.Perimeter => "polyline",
                    _ => "polygon",
                },
                _ => "highlight"
            };

            // XFDF rect is PDF user-space points with a BOTTOM-LEFT origin
            // (left,bottom,right,top). The annotation model stores normalized 0..1 top-down
            // values; writing those raw collapsed every annotation into a sub-point speck at
            // the page origin in Acrobat. The color attribute must also be #RRGGBB, whereas
            // ColorHex is #AARRGGBB.
            double pw = 612, ph = 792;
            if (pageSizes != null && pageSizes.TryGetValue(a.PageNumber, out var ps))
            {
                pw = ps.Width;
                ph = ps.Height;
            }

            var (ox, oy) = pageOrigins != null && pageOrigins.TryGetValue(a.PageNumber, out var po) ? po : (0.0, 0.0);
            double left = ox + a.X * pw;
            double right = ox + (a.X + a.Width) * pw;
            double bottom = oy + (1.0 - a.Y - a.Height) * ph;
            double top = oy + (1.0 - a.Y) * ph;

            string rect = string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4},{3:F4}", left, bottom, right, top);
            string colorAttr = ToXfdfRgbHex(a.ColorHex);

            writer.Write($"    <{annotTag} page=\"{a.PageNumber - 1}\" rect=\"{rect}\" color=\"{colorAttr}\" title=\"{System.Security.SecurityElement.Escape(a.Author ?? string.Empty)}\" date=\"D:{dateStr}\"");

            // Markup annotations are drawn from quadpoints; without coords most viewers
            // render nothing for highlight/underline/strikeout.
            if (a.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut)
            {
                var boxes = a.Quads.Count > 0 ? a.Quads : new List<Rect> { new(a.X, a.Y, a.Width, a.Height) };
                string coords = string.Join(",", boxes.Select(q =>
                {
                    double ql = q.X * pw, qr = (q.X + q.Width) * pw, qt = (1.0 - q.Y) * ph, qb = (1.0 - q.Y - q.Height) * ph;
                    return string.Format(CultureInfo.InvariantCulture,
                        "{0:F4},{1:F4},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4}", ql, qt, qr, qt, ql, qb, qr, qb);
                }));
                writer.Write($" coords=\"{coords}\"");
            }

            string Point(Point p) => string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4}", ox + p.X * pw, oy + (1.0 - p.Y) * ph);
            if (a.Type == AnnotationType.Measurement && a.MeasureKind == PdfEngine.Vector.Annotations.PdfMeasurementKind.Distance && a.Vertices.Count >= 2)
                writer.Write($" start=\"{Point(a.Vertices[0])}\" end=\"{Point(a.Vertices[^1])}\"");
            if (a.Type is AnnotationType.Line or AnnotationType.Arrow && a.Vertices.Count >= 2)
                writer.Write($" start=\"{Point(a.Vertices[0])}\" end=\"{Point(a.Vertices[^1])}\"");
            if (a.IsVertexShape && NativeDocumentServices.ShapeEndings(a) is { Length: 2 } ends)
            {
                // XFDF names the start "head" and the end "tail".
                if (ends[0] != "None") writer.Write($" head=\"{System.Security.SecurityElement.Escape(ends[0])}\"");
                if (ends[1] != "None") writer.Write($" tail=\"{System.Security.SecurityElement.Escape(ends[1])}\"");
            }
            if (a.IsVertexShape)
                writer.Write(string.Format(CultureInfo.InvariantCulture, " width=\"{0:0.###}\"", a.StrokeThickness));
            if (a.Type == AnnotationType.Stamp)
                writer.Write($" icon=\"{System.Security.SecurityElement.Escape(a.StampName)}\"");

            writer.WriteLine(">");
            if (!string.IsNullOrEmpty(a.Contents))
            {
                writer.WriteLine($"      <contents>{System.Security.SecurityElement.Escape(a.Contents)}</contents>");
            }
            if ((a.Type == AnnotationType.Measurement && a.MeasureKind != PdfEngine.Vector.Annotations.PdfMeasurementKind.Distance
                 || a.Type is AnnotationType.Polygon or AnnotationType.PolyLine) && a.Vertices.Count > 0)
                writer.WriteLine($"      <vertices>{string.Join(";", a.Vertices.Select(Point))}</vertices>");
            writer.WriteLine($"    </{annotTag}>");
        }

        writer.WriteLine("  </annots>");
        writer.WriteLine("</xfdf>");
    }

    /// <summary>
    /// Converts the app's #AARRGGBB (or #RRGGBB) color to the #RRGGBB form XFDF requires.
    /// </summary>
    private static string ToXfdfRgbHex(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex)) return "#000000";
        string hex = colorHex.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex.Substring(2);
        if (hex.Length != 6) return "#000000";
        return "#" + hex.ToUpperInvariant();
    }
}
