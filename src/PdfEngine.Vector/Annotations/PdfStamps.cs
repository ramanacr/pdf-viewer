using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Annotations;

/// <summary>
/// How a new stamp's normal appearance is drawn: one of our designed stamps (its label from /Name
/// unless <see cref="Label"/> is given, and for a dynamic stamp a second line), or a picture, or
/// a page of a PDF; at <see cref="Width"/> by <see cref="Height"/> points, turned by
/// <see cref="Rotation"/> through the form's /Matrix.
/// </summary>
public sealed class PdfStampAppearance
{
    /// <summary>The text on a designed stamp; null for the one /Name stands for.</summary>
    public string? Label { get; init; }

    /// <summary>The second line of a dynamic stamp (who and when), or null.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Degrees counter-clockwise.</summary>
    public double Rotation { get; init; }

    /// <summary>The stamp's size before it is turned; 0 to use the rectangle's.</summary>
    public double Width { get; init; }
    public double Height { get; init; }

    /// <summary>A picture to stamp (a custom stamp).</summary>
    public PdfImageContent? Image { get; init; }

    /// <summary>A PDF whose page <see cref="SourcePage"/> is stamped (a custom stamp).</summary>
    public byte[]? SourcePdf { get; init; }
    public int SourcePage { get; init; } = 1;
}

/// <summary>One of the stamps the viewer offers: its /Name, the text it shows and whether it is dynamic (adds who and when).</summary>
public sealed record PdfStandardStamp(string Name, string Label, bool Dynamic);

/// <summary>
/// The stamps the viewer offers, named as ISO 32000-2 Table 184 and Acrobat's Standard Business
/// set name them so other readers recognise them, and the geometry of a turned stamp.
/// </summary>
public static class PdfStamps
{
    /// <summary>Dynamic stamps: the label, then the user's name and the date and time.</summary>
    public static IReadOnlyList<PdfStandardStamp> Dynamic { get; } = new PdfStandardStamp[]
    {
        new("Approved", "APPROVED", true),
        new("Confidential", "CONFIDENTIAL", true),
        new("Received", "RECEIVED", true),
        new("SBReviewed", "REVIEWED", true),
        new("SBRevised", "REVISED", true),
    };

    /// <summary>Static stamps: the label alone.</summary>
    public static IReadOnlyList<PdfStandardStamp> Standard { get; } = new PdfStandardStamp[]
    {
        new("Approved", "APPROVED", false),
        new("SBCompleted", "COMPLETED", false),
        new("Confidential", "CONFIDENTIAL", false),
        new("Draft", "DRAFT", false),
        new("Final", "FINAL", false),
        new("ForComment", "FOR COMMENT", false),
        new("ForPublicRelease", "FOR PUBLIC RELEASE", false),
        new("SBInformationOnly", "INFORMATION ONLY", false),
        new("NotApproved", "NOT APPROVED", false),
        new("NotForPublicRelease", "NOT FOR PUBLIC RELEASE", false),
        new("SBPreliminaryResults", "PRELIMINARY RESULTS", false),
        new("Received", "RECEIVED", false),
        new("SBReviewed", "REVIEWED", false),
        new("SBRevised", "REVISED", false),
        new("SBVoid", "VOID", false),
        new("Expired", "EXPIRED", false),
        new("TopSecret", "TOP SECRET", false),
        new("AsIs", "AS IS", false),
    };

    /// <summary>The usual size of a stamp, in points: wider for a long label, taller for a dynamic one.</summary>
    public static (double Width, double Height) DefaultSize(string label, bool dynamic)
    {
        double width = Math.Clamp(label.Length * 11.5 + 30, 110, 260);
        return (width, dynamic ? 56 : 40);
    }

    /// <summary>The second line of a dynamic stamp: "By Jane Doe at 10:42, 25 Oct 2026".</summary>
    public static string DynamicLine(string user, DateTime when) =>
        string.Create(CultureInfo.InvariantCulture, $"By {user.Trim()} at {when:HH:mm}, {when:d MMM yyyy}");

    /// <summary>An angle in degrees brought into [0, 360).</summary>
    public static double NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees)) return 0;
        double d = degrees % 360;
        if (d < 0) d += 360;
        return Math.Abs(d) < 1e-9 || Math.Abs(d - 360) < 1e-9 ? 0 : d;
    }

    /// <summary>The rectangle a <paramref name="width"/> by <paramref name="height"/> stamp turned by <paramref name="degrees"/> about <paramref name="centre"/> covers.</summary>
    public static PdfRect RotatedBounds(PdfPoint centre, double width, double height, double degrees)
    {
        double a = NormalizeDegrees(degrees) * Math.PI / 180;
        double c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        double w = width * c + height * s, h = width * s + height * c;
        return new PdfRect(centre.X - w / 2, centre.Y - h / 2, w, h);
    }

    internal static PdfArray RotationMatrix(double degrees)
    {
        double a = NormalizeDegrees(degrees) * Math.PI / 180;
        double cos = Math.Round(Math.Cos(a), 6), sin = Math.Round(Math.Sin(a), 6);
        return new PdfArray(new PdfObject[] { new PdfReal(cos), new PdfReal(sin), new PdfReal(-sin), new PdfReal(cos), new PdfInteger(0), new PdfInteger(0) });
    }

    /// <summary>
    /// A one-page PDF the size of the stamp with the stamp on it, for showing it before it is
    /// placed (its appearance is drawn exactly as it will be in the document).
    /// </summary>
    public static byte[] Preview(PdfAnnotation stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var rect = stamp.Rect;
        byte[] blank = BlankPage(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        using var doc = PdfVectorDocument.Open(new MemoryByteSource(blank));
        var copy = Clone(stamp);
        copy.PageNumber = 1;
        copy.Rect = new PdfRect(0, 0, rect.Width, rect.Height);
        return PdfAnnotationEditor.Apply(doc, blank, e => e.Add(copy));
    }

    /// <summary>A one-page PDF with nothing on its page.</summary>
    internal static byte[] BlankPage(double width, double height)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PdfObjectWriter.FormatReal(Math.Round(width, 4))} {PdfObjectWriter.FormatReal(Math.Round(height, 4))}] /Resources << >> /Contents 4 0 R >>"));
        Obj("<< /Length 0 >>\nstream\n\nendstream");
        int xref = sb.Length;
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfAnnotation Clone(PdfAnnotation a) => new()
    {
        Subtype = a.Subtype, Rect = a.Rect, Color = a.Color, Opacity = a.Opacity, Contents = a.Contents, Author = a.Author,
        Subject = a.Subject, Icon = a.Icon, Flags = a.Flags, Stamp = a.Stamp, BorderWidth = a.BorderWidth,
    };
}

/// <summary>
/// The appearance forms of custom stamps (a picture, or a page of another PDF), written into an
/// update with everything they use. A designed stamp is drawn by <see cref="PdfAnnotationAppearances"/>.
/// </summary>
internal static class PdfStampForms
{
    /// <summary>The form for a custom stamp, or null when <paramref name="stamp"/> is a designed one.</summary>
    public static PdfStream? Build(PdfUpdate update, PdfStampAppearance stamp, PdfRect rect, double opacity)
    {
        if (stamp.Image is { } image) return Picture(update, image, stamp, rect, opacity);
        if (stamp.SourcePdf is { Length: > 0 } pdf) return Page(update, pdf, stamp, opacity);
        return null;
    }

    private static PdfStream Picture(PdfUpdate update, PdfImageContent image, PdfStampAppearance stamp, PdfRect rect, double opacity)
    {
        var objects = new Dictionary<int, PdfObject>();
        int number = PdfImageXObject.Write(image, () => update.Add(PdfNull.Instance), objects, interpolate: true);
        foreach (var (n, o) in objects) update.Set(n, o);
        double w = stamp.Width > 0 ? stamp.Width : rect.Width, h = stamp.Height > 0 ? stamp.Height : rect.Height;
        var resources = new Dictionary<string, PdfObject> { ["XObject"] = Dict(("Im0", new PdfIndirectRef(number))) };
        var content = new StringBuilder();
        Opacity(opacity, resources, content);
        content.Append(CultureInfo.InvariantCulture, $"q {F(w)} 0 0 {F(h)} 0 0 cm /Im0 Do Q\n");
        return Form(new PdfRect(0, 0, w, h), PdfMatrix.Identity, stamp.Rotation, resources, content.ToString());
    }

    private static PdfStream Page(PdfUpdate update, byte[] pdf, PdfStampAppearance stamp, double opacity)
    {
        using var source = PdfVectorDocument.Open(new MemoryByteSource(pdf));
        if (source.IsEncrypted) throw new NotSupportedException("The stamp's PDF is encrypted; use an unencrypted copy.");
        int pageNumber = stamp.SourcePage;
        if (pageNumber < 1 || pageNumber > source.PageCount) throw new ArgumentOutOfRangeException(nameof(stamp), $"The stamp's PDF has no page {pageNumber}.");
        var node = source.PageTree.Pages[pageNumber - 1];
        var decoder = new PdfStreamDecoder(null, source.Resolver.Resolve);
        var content = new MemoryStream();
        foreach (var stream in node.Contents)
        {
            content.Write(decoder.DecodeStream(stream));
            content.WriteByte((byte)'\n');
        }
        var (_, _, toUser) = PdfPageMarks.Display(node);
        toUser.TryInvert(out var toDisplay);
        var copies = new Dictionary<int, int>();
        var resources = ((PdfDictionary)Copy(update, node.Resources, source.Resolver, copies, 0)).Entries.ToDictionary(e => e.Key, e => e.Value);
        string body = Encoding.Latin1.GetString(content.ToArray());
        if (opacity < 1)
        {
            // The page as one translucent group, so overlapping parts do not show through each other.
            var inner = Form(node.CropBox, PdfMatrix.Identity, 0, resources, body, group: true);
            int innerNumber = update.Add(inner);
            var outer = new Dictionary<string, PdfObject> { ["XObject"] = Dict(("Pg0", new PdfIndirectRef(innerNumber))) };
            var text = new StringBuilder();
            Opacity(opacity, outer, text);
            text.Append("/Pg0 Do\n");
            return Form(node.CropBox, toDisplay, stamp.Rotation, outer, text.ToString());
        }
        return Form(node.CropBox, toDisplay, stamp.Rotation, resources, body);
    }

    private static void Opacity(double opacity, Dictionary<string, PdfObject> resources, StringBuilder content)
    {
        if (opacity >= 1) return;
        double a = Math.Round(Math.Clamp(opacity, 0, 1), 4);
        resources["ExtGState"] = Dict(("GS0", Dict(("Type", new PdfName("ExtGState")), ("CA", new PdfReal(a)), ("ca", new PdfReal(a)))));
        content.Append("/GS0 gs\n");
    }

    private static PdfStream Form(PdfRect bbox, PdfMatrix toDisplay, double rotation, Dictionary<string, PdfObject> resources, string content, bool group = false)
    {
        double r = PdfStamps.NormalizeDegrees(rotation) * Math.PI / 180;
        var matrix = rotation == 0 ? toDisplay : toDisplay.Multiply(PdfMatrix.CreateRotation(r));
        var dict = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["FormType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray(new PdfObject[] { new PdfReal(Math.Round(bbox.Left, 4)), new PdfReal(Math.Round(bbox.Top, 4)), new PdfReal(Math.Round(bbox.Right, 4)), new PdfReal(Math.Round(bbox.Bottom, 4)) }),
            ["Resources"] = new PdfDictionary(resources),
            ["Filter"] = new PdfName("FlateDecode"),
        };
        if (!matrix.IsIdentity)
            dict["Matrix"] = new PdfArray(new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F }.Select(v => (PdfObject)new PdfReal(Math.Round(v, 6))).ToList());
        if (group) dict["Group"] = Dict(("Type", new PdfName("Group")), ("S", new PdfName("Transparency")));
        return PdfObjectWriter.NewStream(dict, ContentRedactor.Deflate(Encoding.Latin1.GetBytes(content)));
    }

    // Another document's object and what it refers to, renumbered into the update (each once, cycles kept).
    private static PdfObject Copy(PdfUpdate update, PdfObject? obj, PdfObjectResolver r, Dictionary<int, int> copies, int depth)
    {
        if (depth > 256) return PdfNull.Instance;
        switch (obj)
        {
            case null: return PdfNull.Instance;
            case PdfIndirectRef ir:
            {
                if (copies.TryGetValue(ir.ObjectNumber, out int done)) return new PdfIndirectRef(done);
                int n = update.Add(PdfNull.Instance);
                copies[ir.ObjectNumber] = n;
                update.Set(n, Copy(update, r.Resolve(ir), r, copies, depth + 1));
                return new PdfIndirectRef(n);
            }
            case PdfStream s:
            {
                var dict = (PdfDictionary)Copy(update, s.Dictionary, r, copies, depth + 1);
                byte[] raw = s.GetRawBytes().ToArray();
                return new PdfStream(dict, 0, raw.Length, null, raw);
            }
            case PdfDictionary d:
                return new PdfDictionary(d.Entries.Where(e => e.Key != "Parent").ToDictionary(e => e.Key, e => Copy(update, e.Value, r, copies, depth + 1)));
            case PdfArray a:
                return new PdfArray(a.Items.Select(i => Copy(update, i, r, copies, depth + 1)).ToList());
            default:
                return obj;
        }
    }

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries) => new(entries.ToDictionary(e => e.Key, e => e.Value));

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));
}
