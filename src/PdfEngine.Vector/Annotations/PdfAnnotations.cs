using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Services;

namespace PdfEngine.Vector.Annotations;

/// <summary>
/// An annotation's properties (ISO 32000-2 12.5), in default user space. Read from a page by
/// <see cref="PdfAnnotationReader"/>, or filled in to create or change one with
/// <see cref="PdfAnnotationEditor"/>. Every text value is untrusted document data.
/// </summary>
public sealed class PdfAnnotation
{
    /// <summary>1-based page the annotation is on.</summary>
    public int PageNumber { get; set; }

    /// <summary>Position in the page's /Annots when read; -1 for a new annotation.</summary>
    public int Index { get; set; } = -1;

    /// <summary>The annotation's object number, 0 when it is a direct object or new.</summary>
    public int ObjectNumber { get; set; }

    public string Subtype { get; set; } = "Text";

    /// <summary>/Rect, normalized (X, Y the lower-left corner).</summary>
    public PdfRect Rect { get; set; }

    /// <summary>/C and /IC as written: 1 (gray), 3 (RGB) or 4 (CMYK) components; null when absent.</summary>
    public double[]? Color { get; set; }
    public double[]? InteriorColor { get; set; }

    /// <summary>/CA, 1 when absent.</summary>
    public double Opacity { get; set; } = 1;

    public string? Contents { get; set; }
    /// <summary>/T, the author of a markup annotation.</summary>
    public string? Author { get; set; }
    /// <summary>/Subj.</summary>
    public string? Subject { get; set; }
    /// <summary>/NM, the annotation's unique name.</summary>
    public string? Name { get; set; }
    /// <summary>/CreationDate and /M as written ("D:..."), null when absent.</summary>
    public string? CreationDate { get; set; }
    public string? ModifiedDate { get; set; }

    /// <summary>/F; new annotations default to Print (4).</summary>
    public int Flags { get; set; } = 4;

    /// <summary>The border width from /BS /W, else /Border's third number; null when neither is given.</summary>
    public double? BorderWidth { get; set; }
    /// <summary>/BS /S: S, D, B, I or U.</summary>
    public string? BorderStyle { get; set; }
    public double[]? BorderDash { get; set; }

    /// <summary>/InkList strokes.</summary>
    public List<IReadOnlyList<PdfPoint>> InkList { get; set; } = new();

    /// <summary>/QuadPoints, eight numbers per quadrilateral (x1 y1 x2 y2 x3 y3 x4 y4).</summary>
    public List<double[]> QuadPoints { get; set; } = new();

    /// <summary>/L of a line: x1 y1 x2 y2.</summary>
    public double[]? Line { get; set; }

    /// <summary>/Vertices of a polygon or polyline.</summary>
    public List<PdfPoint> Vertices { get; set; } = new();

    /// <summary>/LE: the line ending names (one for free text callouts, two for lines).</summary>
    public string[]? LineEndings { get; set; }

    /// <summary>/Name: the icon of a note, stamp or file attachment.</summary>
    public string? Icon { get; set; }

    /// <summary>/DA, /Q, /IT and /CL of free text; /RC rich text.</summary>
    public string? DefaultAppearance { get; set; }
    public int Justification { get; set; }
    public string? Intent { get; set; }
    public double[]? CalloutLine { get; set; }
    public string? RichContents { get; set; }

    /// <summary>The /Popup annotation's object number (0 when none), its rectangle and /Open.</summary>
    public int PopupObjectNumber { get; set; }
    public PdfRect? PopupRect { get; set; }
    public bool PopupOpen { get; set; }

    /// <summary>/IRT (the object this replies to, 0 when none), /RT, /State and /StateModel.</summary>
    public int InReplyTo { get; set; }
    /// <summary>
    /// For a reply being added whose parent has no object number yet (it is added in the same
    /// edit, or comes from XFDF): the parent's /NM. <see cref="PdfAnnotationEditor.Add"/> finds
    /// the parent by it and writes /IRT. Ignored when <see cref="InReplyTo"/> is set.
    /// </summary>
    public string? InReplyToName { get; set; }
    public string? ReplyType { get; set; }
    public string? State { get; set; }
    public string? StateModel { get; set; }

    /// <summary>The annotation has a normal appearance stream.</summary>
    public bool HasAppearance { get; set; }

    /// <summary>/Measure of a measurement annotation (12.9), rectilinear only; null when absent or geospatial.</summary>
    public Annotations.PdfMeasure? Measure { get; set; }

    /// <summary>/Cap: a line shows its /Contents as a caption.</summary>
    public bool Caption { get; set; }

    /// <summary>
    /// For a stamp being created or redrawn: how its appearance is drawn (a designed stamp with an
    /// optional second line, a picture or a page of a PDF, and its rotation). Null when read, and
    /// when changing a stamp whose own appearance is kept.
    /// </summary>
    public Annotations.PdfStampAppearance? Stamp { get; set; }

    /// <summary>The rotation of the normal appearance (/AP /N /Matrix), degrees counter-clockwise; 0 when it has none.</summary>
    public double AppearanceRotation { get; set; }

    /// <summary>
    /// The normal appearance's size before it is turned, in default user space as /Rect fits it
    /// (12.5.5); null when it has no appearance stream. A turned stamp's /Rect is the turned box,
    /// so this is the stamp's own width and height.
    /// </summary>
    public (double Width, double Height)? AppearanceSize { get; set; }

    /// <summary>
    /// For a changed stamp that keeps its own appearance: degrees counter-clockwise to turn that
    /// appearance by, through its form's /Matrix (0 leaves it as it is). The editor writes the
    /// turned form as a new object, so nothing else that shares the old one changes.
    /// </summary>
    public double AppearanceTurn { get; set; }

    /// <summary>For a file attachment: the file's name, and its bytes when creating one.</summary>
    public string? FileName { get; set; }
    public byte[]? FileData { get; set; }

    /// <summary>The colour as RGB in 0..1 (gray and CMYK converted naively), or null.</summary>
    public static (double R, double G, double B)? ToRgb(double[]? c) => c switch
    {
        { Length: 1 } => (c[0], c[0], c[0]),
        { Length: 3 } => (c[0], c[1], c[2]),
        { Length: 4 } => ((1 - c[0]) * (1 - c[3]), (1 - c[1]) * (1 - c[3]), (1 - c[2]) * (1 - c[3])),
        _ => null,
    };
}

/// <summary>Reads every annotation on a page with its properties (FPDFPage_GetAnnot and the FPDFAnnot_Get* accessors).</summary>
public static class PdfAnnotationReader
{
    public static IReadOnlyList<PdfAnnotation> Read(PdfVectorDocument document, int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var r = document.Resolver;
        var page = document.PageTree.Pages[pageNumber - 1];
        var list = new List<PdfAnnotation>();
        if (r.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) return list;
        var nav = new Lazy<PdfNavigation>(() => new PdfNavigation(document));
        for (int i = 0; i < annots.Count; i++)
        {
            if (r.Resolve(annots[i]) is not PdfDictionary d) continue;
            var a = FromDictionary(d, r, nav);
            a.PageNumber = pageNumber;
            a.Index = i;
            a.ObjectNumber = annots[i] is PdfIndirectRef ir ? ir.ObjectNumber : 0;
            list.Add(a);
        }
        return list;
    }

    internal static PdfAnnotation FromDictionary(PdfDictionary d, PdfObjectResolver r, Lazy<PdfNavigation>? nav = null)
    {
        string? Text(string key) => PdfTextStrings.Decode(r.Resolve(d[key]));
        double[]? Numbers(string key) => PdfDocumentInfo.Numbers(r.Resolve(d[key]), r);
        double? Number(PdfObject? o) => r.Resolve(o) is { } n && n.TryGetNumber(out double v) && double.IsFinite(v) ? v : null;

        var a = new PdfAnnotation
        {
            Subtype = d.GetName("Subtype") ?? (r.Resolve(d["Subtype"]) as PdfName)?.Value ?? string.Empty,
            Rect = PdfDocumentInfo.ReadRect(d["Rect"], r) ?? PdfRect.Empty,
            Color = Numbers("C"),
            InteriorColor = Numbers("IC"),
            Opacity = Number(d["CA"]) is double ca ? Math.Clamp(ca, 0, 1) : 1,
            Contents = Text("Contents"),
            Author = Text("T"),
            Subject = Text("Subj"),
            Name = Text("NM"),
            CreationDate = Text("CreationDate"),
            ModifiedDate = Text("M"),
            Flags = (int)(r.Resolve(d["F"]) is { } f && f.TryGetInteger(out long fl) ? fl : 0),
            Line = Numbers("L") is { Length: >= 4 } l && l.Take(4).All(double.IsFinite) ? l[..4] : null,
            Icon = r.Resolve(d["Name"]) is PdfName icon ? icon.Value : null,
            DefaultAppearance = Text("DA"),
            Justification = (int)(r.Resolve(d["Q"]) is { } q && q.TryGetInteger(out long qv) ? qv : 0),
            Intent = r.Resolve(d["IT"]) is PdfName it ? it.Value : null,
            CalloutLine = Numbers("CL"),
            RichContents = r.Resolve(d["RC"]) is PdfStream ? null : Text("RC"),
            InReplyTo = d["IRT"] is PdfIndirectRef irt ? irt.ObjectNumber : 0,
            ReplyType = r.Resolve(d["RT"]) is PdfName rt ? rt.Value : null,
            State = Text("State"),
            StateModel = Text("StateModel"),
            PopupObjectNumber = d["Popup"] is PdfIndirectRef pop ? pop.ObjectNumber : 0,
            Measure = Annotations.PdfMeasure.FromDictionary(d["Measure"], r),
            Caption = r.Resolve(d["Cap"]) is PdfBoolean { Value: true },
        };

        if (r.Resolve(d["BS"]) is PdfDictionary bs)
        {
            a.BorderWidth = Number(bs["W"]);
            a.BorderStyle = r.Resolve(bs["S"]) is PdfName s ? s.Value : null;
            a.BorderDash = PdfDocumentInfo.Numbers(r.Resolve(bs["D"]), r);
        }
        if (a.BorderWidth == null && r.Resolve(d["Border"]) is PdfArray border && border.Count >= 3)
        {
            a.BorderWidth = Number(border[2]);
            if (border.Count >= 4) a.BorderDash ??= PdfDocumentInfo.Numbers(r.Resolve(border[3]), r);
        }

        if (r.Resolve(d["LE"]) is PdfArray le)
            a.LineEndings = le.Select(e => (r.Resolve(e) as PdfName)?.Value ?? "None").ToArray();
        else if (r.Resolve(d["LE"]) is PdfName single)
            a.LineEndings = new[] { single.Value };

        if (Numbers("QuadPoints") is { } quads)
            for (int k = 0; k + 7 < quads.Length; k += 8) a.QuadPoints.Add(quads[k..(k + 8)]);

        if (r.Resolve(d["InkList"]) is PdfArray ink)
        {
            foreach (var stroke in ink)
            {
                if (PdfDocumentInfo.Numbers(r.Resolve(stroke), r) is not { } pts) continue;
                var points = new List<PdfPoint>(pts.Length / 2);
                for (int k = 0; k + 1 < pts.Length; k += 2) points.Add(new PdfPoint(pts[k], pts[k + 1]));
                a.InkList.Add(points);
            }
        }

        // A hostile list is cut to the limit; a list with a value that is not a finite number is not read.
        if (Numbers("Vertices") is { } vertices && vertices.All(double.IsFinite))
            for (int k = 0; k + 1 < vertices.Length && a.Vertices.Count < Annotations.PdfAnnotationLimits.MaxVertices; k += 2)
                a.Vertices.Add(new PdfPoint(vertices[k], vertices[k + 1]));

        if (r.Resolve(d["Popup"]) is PdfDictionary popup)
        {
            a.PopupRect = PdfDocumentInfo.ReadRect(popup["Rect"], r);
            a.PopupOpen = r.Resolve(popup["Open"]) is PdfBoolean { Value: true };
        }

        a.HasAppearance = r.Resolve(d["AP"]) is PdfDictionary ap && r.Resolve(ap["N"]) is PdfStream or PdfDictionary;
        if (r.Resolve(d["AP"]) is PdfDictionary apd && r.Resolve(apd["N"]) is PdfStream normal
            && PdfDocumentInfo.Numbers(r.Resolve(normal.Dictionary["Matrix"]), r) is { Length: 6 } m && (m[0] != 0 || m[1] != 0))
        {
            double degrees = Math.Round(Math.Atan2(m[1], m[0]) * 180 / Math.PI, 4);
            a.AppearanceRotation = degrees < 0 ? degrees + 360 : degrees;
        }
        if (r.Resolve(d["AP"]) is PdfDictionary apSize && r.Resolve(apSize["N"]) is PdfStream form)
            a.AppearanceSize = UprightSize(form, a.Rect, a.AppearanceRotation, r);
        if (a.Subtype == "FileAttachment")
            a.FileName = (nav?.Value)?.FileName(d["FS"]) ?? PdfTextStrings.Decode(r.Resolve(d["FS"]));
        return a;
    }

    /// <summary>The form's box with its turn taken out, scaled as /Rect fits the turned box (12.5.5).</summary>
    private static (double Width, double Height)? UprightSize(PdfStream form, PdfRect rect, double degrees, PdfObjectResolver r)
    {
        if (PdfDocumentInfo.ReadRect(form.Dictionary["BBox"], r) is not { IsEmpty: false } bbox) return null;
        var matrix = PdfDocumentInfo.Numbers(r.Resolve(form.Dictionary["Matrix"]), r) is { Length: 6 } m
            ? new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5])
            : PdfMatrix.Identity;
        var turned = matrix.Transform(bbox);
        var upright = matrix.Multiply(PdfMatrix.CreateRotation(-degrees * Math.PI / 180)).Transform(bbox);
        if (turned.Width <= 0 || turned.Height <= 0 || upright.Width <= 0 || upright.Height <= 0) return null;
        double scale = (rect.Width / turned.Width + rect.Height / turned.Height) / 2;
        return (upright.Width * scale, upright.Height * scale);
    }
}
