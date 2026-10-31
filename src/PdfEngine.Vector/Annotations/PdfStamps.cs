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

    /// <summary>The rectangle a <paramref name="width"/> by <paramref name="height"/> stamp turned by <paramref name="degrees"/> about <paramref name="center"/> covers.</summary>
    public static PdfRect RotatedBounds(PdfPoint center, double width, double height, double degrees)
    {
        double a = NormalizeDegrees(degrees) * Math.PI / 180;
        double c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        double w = width * c + height * s, h = width * s + height * c;
        return new PdfRect(center.X - w / 2, center.Y - h / 2, w, h);
    }

    internal static PdfArray RotationMatrix(double degrees)
    {
        double a = NormalizeDegrees(degrees) * Math.PI / 180;
        double cos = Math.Round(Math.Cos(a), 6), sin = Math.Round(Math.Sin(a), 6);
        return new PdfArray(new PdfObject[] { new PdfReal(cos), new PdfReal(sin), new PdfReal(-sin), new PdfReal(cos), new PdfInteger(0), new PdfInteger(0) });
    }

    /// <summary>
    /// A one-page PDF the size of the stamp with the stamp on it, for showing it before it is
    /// placed (its appearance is drawn exactly as it will be in the document). On a black page
    /// when <paramref name="black"/>: a render on white and one on black give the stamp's alpha.
    /// </summary>
    public static byte[] Preview(PdfAnnotation stamp, bool black = false)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        var rect = stamp.Rect;
        byte[] blank = BlankPage(Math.Max(1, rect.Width), Math.Max(1, rect.Height), black);
        using var doc = PdfVectorDocument.Open(new PdfMemoryByteSource(blank));
        var copy = Clone(stamp);
        copy.PageNumber = 1;
        copy.Rect = new PdfRect(0, 0, rect.Width, rect.Height);
        return PdfAnnotationEditor.Apply(doc, blank, e => e.Add(copy));
    }

    /// <summary>
    /// A one-page PDF with the annotation at <paramref name="index"/> on page <paramref name="pageNumber"/>
    /// drawn from its own normal appearance, turned by <paramref name="turn"/> degrees counter-clockwise
    /// (as <see cref="PdfAnnotation.AppearanceTurn"/> turns it when saved), on a page the size of the
    /// turned box (white, or black when <paramref name="black"/>). Null when it has no appearance stream.
    /// For showing a stamp read from a document while it is moved, sized or turned.
    /// </summary>
    public static byte[]? PreviewOfAppearance(PdfVectorDocument document, int pageNumber, int index, double turn = 0, bool black = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageNumber < 1 || pageNumber > document.PageCount) return null;
        var r = document.Resolver;
        if (r.Resolve(document.PageTree.Pages[pageNumber - 1].Dictionary["Annots"]) is not PdfArray annots || index < 0 || index >= annots.Count) return null;
        if (r.Resolve(annots[index]) is not PdfDictionary annot || r.Resolve(annot["AP"]) is not PdfDictionary ap || r.Resolve(ap["N"]) is not PdfStream normal) return null;
        if (PdfDocumentInfo.ReadRect(annot["Rect"], r) is not { IsEmpty: false } rect || PdfDocumentInfo.ReadRect(normal.Dictionary["BBox"], r) is not { IsEmpty: false } bbox) return null;
        var before = (PdfDocumentInfo.Numbers(r.Resolve(normal.Dictionary["Matrix"]), r) is { Length: 6 } m
            ? new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5]) : PdfMatrix.Identity).Transform(bbox);
        var form = PdfStampForms.Turned(normal, turn, r);
        var after = PdfStampForms.MatrixOf(form, r).Transform(bbox);
        if (before.Width <= 0 || before.Height <= 0 || after.Width <= 0 || after.Height <= 0) return null;
        // The scale /Rect gives it now, kept as it turns.
        double scale = (rect.Width / before.Width + rect.Height / before.Height) / 2;
        double w = Math.Max(1, after.Width * scale), h = Math.Max(1, after.Height * scale);
        byte[] blank = BlankPage(w, h);
        using var doc = PdfVectorDocument.Open(new PdfMemoryByteSource(blank));
        var update = new PdfUpdate(doc, blank);
        int fm = update.Add(PdfStampForms.CopyInto(update, form, r));
        double opacity = Math.Clamp(r.Resolve(annot["CA"]) switch { PdfReal x => x.Value, PdfInteger n => n.Value, _ => 1.0 }, 0, 1);
        var resources = new Dictionary<string, PdfObject> { ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Fm0"] = new PdfIndirectRef(fm) }) };
        var content = new StringBuilder();
        if (black) content.Append(CultureInfo.InvariantCulture, $"0 g 0 0 {F(w)} {F(h)} re f\n");
        if (opacity < 1)
        {
            resources["ExtGState"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["GS0"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Type"] = new PdfName("ExtGState"), ["CA"] = new PdfReal(opacity), ["ca"] = new PdfReal(opacity) }),
            });
            content.Append("/GS0 gs\n");
        }
        double sx = w / after.Width, sy = h / after.Height;
        content.Append(CultureInfo.InvariantCulture, $"q {F(sx)} 0 0 {F(sy)} {F(-after.Left * sx)} {F(-after.Top * sy)} cm /Fm0 Do Q\n");
        var (pageNo, page) = update.Page(1);
        update.Set(pageNo, PdfUpdate.With(page, ("Resources", new PdfDictionary(resources)), ("Contents", update.AddRef(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes(content.ToString()))))));
        return update.Commit(forceFullRewrite: true);
    }

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));

    /// <summary>A one-page PDF with nothing on its page (or black all over it).</summary>
    internal static byte[] BlankPage(double width, double height, bool black = false)
    {
        string w = PdfObjectWriter.FormatReal(Math.Round(width, 4)), h = PdfObjectWriter.FormatReal(Math.Round(height, 4));
        string content = black ? $"0 g 0 0 {w} {h} re f" : string.Empty;
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << >> /Contents 4 0 R >>");
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
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

    // The page forms of custom stamps already written into an update, by the stamp PDF's hash, its
    // page and whether it is a group: a stamp placed many times is one copy in the file.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfUpdate, Dictionary<string, (int Number, PdfRect BBox, PdfMatrix ToDisplay)>> Shared = new();

    private static PdfStream Page(PdfUpdate update, byte[] pdf, PdfStampAppearance stamp, double opacity)
    {
        bool group = opacity < 1;
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf)) + ":" + stamp.SourcePage.ToString(CultureInfo.InvariantCulture) + (group ? ":g" : "");
        var shared = Shared.GetOrCreateValue(update);
        if (!shared.TryGetValue(key, out var inner))
        {
            using var source = PdfVectorDocument.Open(new PdfMemoryByteSource(pdf));
            if (source.IsEncrypted) throw new NotSupportedException("The stamp's PDF is encrypted; use an unencrypted copy.");
            int pageNumber = stamp.SourcePage;
            if (pageNumber < 1 || pageNumber > source.PageCount) throw new ArgumentOutOfRangeException(nameof(stamp), $"The stamp's PDF has no page {pageNumber}.");
            var node = source.PageTree.Pages[pageNumber - 1];
            var target = new CopyTarget(() => update.Add(PdfNull.Instance), update.Set);
            // The page as one form; with opacity, one translucent group, so overlapping parts do not show through each other.
            var (form, toDisplay) = PageForm(target, source, node, group);
            inner = (update.Add(form), node.CropBox, toDisplay);
            shared[key] = inner;
        }
        // Each stamp: a small form that draws the shared page form, turned and translucent as this stamp is.
        var outer = new Dictionary<string, PdfObject> { ["XObject"] = Dict(("Pg0", new PdfIndirectRef(inner.Number))) };
        var text = new StringBuilder();
        Opacity(opacity, outer, text);
        text.Append("/Pg0 Do\n");
        return Form(inner.BBox, inner.ToDisplay, stamp.Rotation, outer, text.ToString());
    }

    /// <summary>
    /// A page of <paramref name="source"/> as a form XObject (its content and its resources, copied
    /// with <see cref="Copy"/>: no action, script or link to another file), and the matrix that
    /// shows it as the page is shown (its crop box and /Rotate).
    /// </summary>
    private static (PdfStream Form, PdfMatrix ToDisplay) PageForm(CopyTarget target, PdfVectorDocument source, PdfPageNode node, bool group)
    {
        var decoder = new PdfStreamDecoder(null, source.Resolver.Resolve);
        var content = new MemoryStream();
        foreach (var stream in node.Contents)
        {
            content.Write(decoder.DecodeStream(stream));
            content.WriteByte((byte)'\n');
        }
        var (_, _, toUser) = PdfPageMarks.Display(node);
        toUser.TryInvert(out var toDisplay);
        var resources = Copy(target, node.Resources, source.Resolver, new Dictionary<int, int>(), 0) is PdfDictionary copied
            ? copied.Entries.ToDictionary(e => e.Key, e => e.Value)
            : new Dictionary<string, PdfObject>();
        return (Form(node.CropBox, PdfMatrix.Identity, 0, resources, Encoding.Latin1.GetString(content.ToArray()), group), toDisplay);
    }

    /// <summary>
    /// A new one-page PDF that draws page <paramref name="pageNumber"/> of <paramref name="source"/>
    /// and holds nothing else: a page the size of the page as shown (upright, no /Rotate) whose
    /// content draws the page's content and resources as a form XObject. No annotation, page
    /// action, piece information, metadata, document information, outline or form is kept; the
    /// resources are copied without actions, scripts or links to other files. Unencrypted.
    /// </summary>
    internal static byte[] Isolate(PdfVectorDocument source, int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > source.PageCount) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var node = source.PageTree.Pages[pageNumber - 1];
        var objects = new List<PdfObject?> { null, null, null, null, null }; // 1 catalog, 2 pages, 3 page, 4 content
        var target = new CopyTarget(() => { objects.Add(PdfNull.Instance); return objects.Count - 1; }, (n, o) => objects[n] = o);
        var (form, toDisplay) = PageForm(target, source, node, group: false);
        objects.Add(form);
        int formNumber = objects.Count - 1;
        // The page shown upright from (0, 0): turned as /Rotate shows it, its crop box moved there.
        var shown = toDisplay.Transform(node.CropBox);
        double w = shown.Width, h = shown.Height;
        var m = toDisplay;
        string content = $"q {M(m.A)} {M(m.B)} {M(m.C)} {M(m.D)} {M(m.E - shown.Left)} {M(m.F - shown.Top)} cm /Fm0 Do Q\n";
        objects[1] = Dict(("Type", new PdfName("Catalog")), ("Pages", new PdfIndirectRef(2)));
        objects[2] = Dict(("Type", new PdfName("Pages")), ("Kids", new PdfArray(new PdfObject[] { new PdfIndirectRef(3) })), ("Count", new PdfInteger(1)));
        objects[3] = Dict(("Type", new PdfName("Page")), ("Parent", new PdfIndirectRef(2)),
            ("MediaBox", new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(Math.Round(w, 4)), new PdfReal(Math.Round(h, 4)) })),
            ("Resources", Dict(("XObject", Dict(("Fm0", new PdfIndirectRef(formNumber)))))),
            ("Contents", new PdfIndirectRef(4)));
        objects[4] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.ASCII.GetBytes(content));

        using var output = new MemoryStream();
        void Raw(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Raw("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n");
        var offsets = new long[objects.Count];
        for (int n = 1; n < objects.Count; n++)
        {
            offsets[n] = output.Position;
            Raw(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            PdfObjectWriter.Write(output, objects[n] ?? PdfNull.Instance);
            Raw("\nendobj\n");
        }
        long xref = output.Position;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objects.Count).Append("\n0000000000 65535 f \n");
        for (int n = 1; n < objects.Count; n++) sb.Append(offsets[n].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(sb.ToString());
        return output.ToArray();
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

    /// <summary>
    /// A copy of a form XObject turned by <paramref name="degrees"/> counter-clockwise: its /Matrix
    /// followed by the turn (an appearance kept when a stamp is turned). The data is copied as it
    /// is read (decrypted, still filtered).
    /// </summary>
    internal static PdfStream Turned(PdfStream form, double degrees, PdfObjectResolver r)
    {
        var matrix = PdfDocumentInfo.Numbers(r.Resolve(form.Dictionary["Matrix"]), r) is { Length: 6 } m
            ? new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5])
            : PdfMatrix.Identity;
        var turned = matrix.Multiply(PdfMatrix.CreateRotation(PdfStamps.NormalizeDegrees(degrees) * Math.PI / 180));
        var entries = new Dictionary<string, PdfObject>(form.Dictionary.Entries)
        {
            ["Matrix"] = new PdfArray(new[] { turned.A, turned.B, turned.C, turned.D, turned.E, turned.F }.Select(v => (PdfObject)new PdfReal(Math.Round(v, 6))).ToList()),
        };
        entries.Remove("Length");
        byte[] raw = form.GetRawBytes().ToArray();
        return PdfObjectWriter.NewStream(entries, raw);
    }

    /// <summary>A form's /Matrix, identity when it has none.</summary>
    internal static PdfMatrix MatrixOf(PdfStream form, PdfObjectResolver r) =>
        PdfDocumentInfo.Numbers(r.Resolve(form.Dictionary["Matrix"]), r) is { Length: 6 } m
            ? new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5])
            : PdfMatrix.Identity;

    /// <summary>Another document's form with everything it uses, copied into the update.</summary>
    internal static PdfObject CopyInto(PdfUpdate update, PdfStream form, PdfObjectResolver r) =>
        Copy(new CopyTarget(() => update.Add(PdfNull.Instance), update.Set), form, r, new Dictionary<int, int>(), 0);

    /// <summary>Where copied objects go: a number for a new object, then the object.</summary>
    private sealed record CopyTarget(Func<int> Reserve, Action<int, PdfObject> Set);

    // An appearance draws and does nothing else (defence in depth: a reader does not run an
    // appearance's actions anyway). An action dictionary (/S one of the action types) is not
    // copied, and the action and script keys are left out of a dictionary shaped like an action
    // (/S, or /Type /Action) or an annotation. A dictionary whose keys are names a page gives to
    // its resources (/Font, /XObject, /ExtGState, /ColorSpace, /Pattern, /Shading, /Properties) or
    // a Type 3 font to its glyphs (/CharProcs) is copied whole: a font named /A is a font.
    private static readonly HashSet<string> ActionKeys = new(StringComparer.Ordinal) { "A", "AA", "JS", "JavaScript", "OpenAction", "URI", "Next" };
    private static readonly HashSet<string> NameKeyed = new(StringComparer.Ordinal) { "Font", "XObject", "ExtGState", "ColorSpace", "Pattern", "Shading", "Properties", "CharProcs" };
    private static readonly HashSet<string> ActionTypes = new(StringComparer.Ordinal)
    {
        "GoTo", "GoToR", "GoToE", "GoToDp", "Launch", "Thread", "URI", "Sound", "Movie", "Hide", "Named", "SubmitForm", "ResetForm",
        "ImportData", "JavaScript", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "RichMediaExecute",
    };

    // A stream's data stays in the stream: no external file (/F as a file specification, with
    // /FFilter and /FDecodeParms), reference XObject target (/Ref) or OPI proxy, left out before
    // they are copied so that nothing they point to is written (ISO 32000-2 7.3.8.2, 8.10.4, 14.11.7).
    // /F as a filter name or array of names is kept.
    private static bool KeepInStream(string key, PdfObject value, PdfObjectResolver r) => key switch
    {
        "FFilter" or "FDecodeParms" or "Ref" or "OPI" => false,
        "F" => r.Resolve(value) is PdfName or PdfArray,
        _ => true,
    };

    private static bool IsActionShaped(PdfDictionary d, PdfObjectResolver r) =>
        d.Entries.ContainsKey("S") || r.Resolve(d["Type"]) is PdfName { Value: "Action" };

    private static bool IsAnnotation(PdfDictionary d, PdfObjectResolver r) =>
        r.Resolve(d["Type"]) is PdfName { Value: "Annot" } || d.Entries.ContainsKey("Subtype") && d.Entries.ContainsKey("Rect");

    // Another document's object and what it refers to, renumbered into the target (each once,
    // cycles kept). <paramref name="names"/>: a dictionary whose keys are resource or glyph names.
    private static PdfObject Copy(CopyTarget target, PdfObject? obj, PdfObjectResolver r, Dictionary<int, int> copies, int depth, bool names = false)
    {
        if (depth > 256) return PdfNull.Instance;
        switch (obj)
        {
            case null: return PdfNull.Instance;
            case PdfIndirectRef ir:
            {
                if (copies.TryGetValue(ir.ObjectNumber, out int done)) return new PdfIndirectRef(done);
                var resolved = r.Resolve(ir);
                if (!names && resolved is PdfDictionary action && IsAction(action, r)) return PdfNull.Instance;
                int n = target.Reserve();
                copies[ir.ObjectNumber] = n;
                target.Set(n, Copy(target, resolved, r, copies, depth + 1, names));
                return new PdfIndirectRef(n);
            }
            case PdfStream s:
            {
                var entries = s.Dictionary.Entries.Where(e => KeepInStream(e.Key, e.Value, r))
                    .ToDictionary(e => e.Key, e => Copy(target, e.Value, r, copies, depth + 1, NameKeyed.Contains(e.Key)));
                byte[] raw = s.GetRawBytes().ToArray();
                return new PdfStream(new PdfDictionary(entries), 0, raw.Length, null, raw);
            }
            case PdfDictionary d when names:
                return new PdfDictionary(d.Entries.ToDictionary(e => e.Key, e => Copy(target, e.Value, r, copies, depth + 1)));
            case PdfDictionary d:
            {
                if (IsAction(d, r)) return PdfNull.Instance;
                bool strip = IsActionShaped(d, r) || IsAnnotation(d, r);
                return new PdfDictionary(d.Entries.Where(e => e.Key != "Parent" && !(strip && ActionKeys.Contains(e.Key)))
                    .ToDictionary(e => e.Key, e => Copy(target, e.Value, r, copies, depth + 1, NameKeyed.Contains(e.Key))));
            }
            case PdfArray a:
                return new PdfArray(a.Items.Select(i => Copy(target, i, r, copies, depth + 1)).ToList());
            default:
                return obj;
        }
    }

    /// <summary>An action dictionary: /S names an action type (ISO 32000-2 Table 201), and /Type is /Action or absent.</summary>
    private static bool IsAction(PdfDictionary d, PdfObjectResolver r) =>
        r.Resolve(d["S"]) is PdfName { } s && ActionTypes.Contains(s.Value)
        && r.Resolve(d["Type"]) is null or PdfNull or PdfName { Value: "Action" };

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries) => new(entries.ToDictionary(e => e.Key, e => e.Value));

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));

    private static string M(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 6));
}
