using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>Pictures of a presentation: each a part under ppt/media, named for the slides by its path from them.</summary>
internal sealed class PptxMedia : IExportMedia
{
    private readonly OpcPackage _opc;
    private readonly bool _wanted;
    public int Count { get; private set; }

    public PptxMedia(OpcPackage opc, bool wanted)
    {
        _opc = opc;
        _wanted = wanted;
    }

    public bool WantsImages => _wanted;

    public string AddImage(byte[] data, string extension)
    {
        string name = $"image{++Count}.{extension}";
        _opc.WriteMedia("ppt/media/" + name, data);
        return "../media/" + name;
    }
}

/// <summary>
/// Writes the PresentationML of a presentation by hand: a slide a page, the page's text as text
/// boxes where it is (with its fonts, sizes, colours and line spacing, turned where it is
/// turned), its tables as tables, pictures as pictures and rectangles and lines as shapes,
/// over one blank layout of one master.
/// </summary>
internal sealed class PptxWriter
{
    internal const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    internal const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = OpcPackage.R;
    private const string ContentBase = "application/vnd.openxmlformats-officedocument.presentationml.";
    private const string Presentation = "ppt/presentation.xml";
    private const double Emu = 12700;
    /// <summary>Slide sizes PowerPoint takes: 1 to 56 inches a side.</summary>
    private const long MinSide = 914_400, MaxSide = 51_206_400;

    private readonly OpcPackage _opc;
    private readonly List<string> _hyperlinks;
    private readonly long _slideW, _slideH;
    private int _slides;

    public int TextBoxes, Pictures, Shapes, Tables;

    /// <summary>A presentation whose slides are the size of a page of <paramref name="width"/> by <paramref name="height"/> points.</summary>
    public PptxWriter(OpcPackage opc, List<string> hyperlinks, double width, double height)
    {
        _opc = opc;
        _hyperlinks = hyperlinks;
        // A page larger than PowerPoint's largest slide is made smaller as a whole, keeping its shape.
        double scale = Math.Min(1, MaxSide / (Math.Max(width, height) * Emu));
        _slideW = Math.Clamp((long)Math.Round(width * scale * Emu), MinSide, MaxSide);
        _slideH = Math.Clamp((long)Math.Round(height * scale * Emu), MinSide, MaxSide);
    }

    public double SlideWidth => _slideW / Emu;
    public double SlideHeight => _slideH / Emu;

    /// <summary>A page on its slide: as large as fits, centred (pages of another size than the first are scaled to the slide).</summary>
    private readonly record struct Fit(double Scale, double X0, double Y0)
    {
        public long X(double x) => (long)Math.Round((X0 + x * Scale) * Emu);
        public long Y(double y) => (long)Math.Round((Y0 + y * Scale) * Emu);
        public long Len(double d) => Math.Max(0, (long)Math.Round(d * Scale * Emu));
    }

    private sealed class Slide
    {
        public readonly Dictionary<string, string> Rels = new(StringComparer.Ordinal);
        public readonly Dictionary<int, string> Links = new();
        public string Part = string.Empty;
        public int NextId = 2;
        public Fit Fit;
        public bool Invisible;
    }

    /// <summary>
    /// Writes a slide of the page. With <paramref name="background"/> (a picture of the page
    /// without its text) the page's look is that picture and only the text is laid over it;
    /// <paramref name="invisibleText"/> keeps recognized text over a scan see-through, as it is in the PDF.
    /// </summary>
    public void AddSlide(DocPage page, string? background, bool invisibleText)
    {
        int number = ++_slides;
        var slide = new Slide { Part = $"ppt/slides/slide{number}.xml", Invisible = invisibleText };
        double sw = _slideW / Emu, sh = _slideH / Emu;
        double scale = Math.Min(sw / Math.Max(page.Width, 1), sh / Math.Max(page.Height, 1));
        slide.Fit = new Fit(scale, (sw - page.Width * scale) / 2, (sh - page.Height * scale) / 2);
        _opc.AddRelationship(slide.Part, "slideLayout", "../slideLayouts/slideLayout1.xml");
        _opc.AddRelationship(Presentation, "slide", $"slides/slide{number}.xml");
        bool fills = Math.Abs(page.Width * scale - sw) < 1 && Math.Abs(page.Height * scale - sh) < 1;

        _opc.WritePart(slide.Part, ContentBase + "slide+xml", x =>
        {
            x.WriteStartElement("p", "sld", P);
            x.WriteAttributeString("xmlns", "a", null, A);
            x.WriteAttributeString("xmlns", "r", null, R);
            x.WriteStartElement("p", "cSld", P);
            if (background != null && fills)
            {
                // The page as the slide's background: it cannot be moved by mistake while the text is edited.
                x.WriteStartElement("p", "bg", P);
                x.WriteStartElement("p", "bgPr", P);
                x.WriteStartElement("a", "blipFill", A);
                x.WriteAttributeString("dpi", "0");
                x.WriteAttributeString("rotWithShape", "1");
                Blip(x, slide, background);
                x.WriteStartElement("a", "stretch", A);
                x.WriteElementString("a", "fillRect", A, null);
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteElementString("a", "effectLst", A, null);
                x.WriteEndElement();
                x.WriteEndElement();
            }
            x.WriteStartElement("p", "spTree", P);
            GroupProperties(x);
            if (background != null && !fills) Picture(x, slide, background, new Box(0, 0, page.Width, page.Height), 0, false, false, 0, 0, 0, 0, page.Width, page.Height, "Page");
            if (background == null)
            {
                foreach (var shape in page.Shapes) Shape(x, slide, shape);
                foreach (var image in page.Floating.Where(i => i.Behind)) Image(x, slide, image);
                foreach (var p in page.Items.OfType<DocParagraph>().Where(p => p.Image != null)) Image(x, slide, p.Image!);
                foreach (var image in page.Floating.Where(i => !i.Behind)) Image(x, slide, image);
                foreach (var table in page.Items.OfType<DocTable>()) Table(x, slide, table);
            }
            else
            {
                // Over a picture of the page, a table's text is in text boxes where it is: its lines are in the picture.
                foreach (var cell in page.Items.OfType<DocTable>().SelectMany(t => t.Cells))
                    foreach (var p in cell.AllParagraphs.Where(p => !p.IsEmpty)) Paragraph(x, slide, p);
            }
            foreach (var p in page.Items.OfType<DocParagraph>().Where(p => p.Image == null && !p.IsEmpty)) Paragraph(x, slide, p);
            foreach (var box in page.TextBoxes) TurnedBox(x, slide, box);
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("p", "clrMapOvr", P);
            x.WriteElementString("a", "masterClrMapping", A, null);
            x.WriteEndElement();
            x.WriteEndElement();
        });
    }

    // ------------------------------------------------------------------ shapes

    private static void GroupProperties(XmlWriter x)
    {
        x.WriteStartElement("p", "nvGrpSpPr", P);
        NonVisual(x, 1, string.Empty);
        x.WriteElementString("p", "cNvGrpSpPr", P, null);
        x.WriteElementString("p", "nvPr", P, null);
        x.WriteEndElement();
        x.WriteStartElement("p", "grpSpPr", P);
        x.WriteStartElement("a", "xfrm", A);
        foreach (var (name, a, b) in new[] { ("off", "x", "y"), ("ext", "cx", "cy"), ("chOff", "x", "y"), ("chExt", "cx", "cy") })
        {
            x.WriteStartElement("a", name, A);
            x.WriteAttributeString(a, "0");
            x.WriteAttributeString(b, "0");
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void NonVisual(XmlWriter x, int id, string name)
    {
        x.WriteStartElement("p", "cNvPr", P);
        x.WriteAttributeString("id", id.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("name", name);
        x.WriteEndElement();
    }

    private static void Transform(XmlWriter x, string prefix, string ns, long left, long top, long width, long height, int rotation = 0, bool flipH = false, bool flipV = false)
    {
        x.WriteStartElement(prefix, "xfrm", ns);
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation != 0) x.WriteAttributeString("rot", (rotation * 60000L).ToString(CultureInfo.InvariantCulture));
        if (flipH) x.WriteAttributeString("flipH", "1");
        if (flipV) x.WriteAttributeString("flipV", "1");
        x.WriteStartElement("a", "off", A);
        x.WriteAttributeString("x", left.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("y", top.ToString(CultureInfo.InvariantCulture));
        x.WriteEndElement();
        x.WriteStartElement("a", "ext", A);
        x.WriteAttributeString("cx", width.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("cy", height.ToString(CultureInfo.InvariantCulture));
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Geometry(XmlWriter x, string preset)
    {
        x.WriteStartElement("a", "prstGeom", A);
        x.WriteAttributeString("prst", preset);
        x.WriteElementString("a", "avLst", A, null);
        x.WriteEndElement();
    }

    private static void SolidFill(XmlWriter x, string color, bool invisible = false)
    {
        x.WriteStartElement("a", "solidFill", A);
        x.WriteStartElement("a", "srgbClr", A);
        x.WriteAttributeString("val", color);
        if (invisible)
        {
            x.WriteStartElement("a", "alpha", A);
            x.WriteAttributeString("val", "0");
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Line(XmlWriter x, string? color, double width, Fit fit)
    {
        x.WriteStartElement("a", "ln", A);
        if (color == null) x.WriteElementString("a", "noFill", A, null);
        else
        {
            x.WriteAttributeString("w", Math.Clamp(fit.Len(width), 0, 20_116_800).ToString(CultureInfo.InvariantCulture));
            SolidFill(x, color);
        }
        x.WriteEndElement();
    }

    private void Shape(XmlWriter x, Slide slide, DocShape shape)
    {
        var f = slide.Fit;
        if (shape.Kind == ShapeKind.Picture)
        {
            Picture(x, slide, shape.RelId, shape.Box, 0, false, false, 0, 0, 0, 0, shape.Box.W, shape.Box.H, "Drawing");
            return;
        }
        bool line = shape.Kind == ShapeKind.Line;
        x.WriteStartElement("p", line ? "cxnSp" : "sp", P);
        x.WriteStartElement("p", line ? "nvCxnSpPr" : "nvSpPr", P);
        int id = slide.NextId++;
        NonVisual(x, id, (line ? "Line " : "Rectangle ") + id.ToString(CultureInfo.InvariantCulture));
        x.WriteElementString("p", line ? "cNvCxnSpPr" : "cNvSpPr", P, null);
        x.WriteElementString("p", "nvPr", P, null);
        x.WriteEndElement();
        x.WriteStartElement("p", "spPr", P);
        Transform(x, "a", A, f.X(shape.Box.L), f.Y(shape.Box.T), f.Len(shape.Box.W), f.Len(shape.Box.H), 0, shape.FlipH, shape.FlipV);
        Geometry(x, line ? "line" : "rect");
        if (!line)
        {
            if (shape.Fill != null) SolidFill(x, shape.Fill);
            else x.WriteElementString("a", "noFill", A, null);
        }
        Line(x, shape.Stroke, shape.StrokeWidth, f);
        x.WriteEndElement();
        x.WriteEndElement();
        Shapes++;
    }

    private void Image(XmlWriter x, Slide slide, DocImage image)
    {
        // Turned a quarter: the picture's own (unturned) box is centred where the turned one is.
        double w = image.Rotation is 90 or 270 ? image.Box.H : image.Box.W, h = image.Rotation is 90 or 270 ? image.Box.W : image.Box.H;
        var box = new Box(image.Box.Cx - w / 2, image.Box.Cy - h / 2, image.Box.Cx + w / 2, image.Box.Cy + h / 2);
        Picture(x, slide, image.RelId, box, image.Rotation, image.FlipH, image.FlipV, image.CropL, image.CropT, image.CropR, image.CropB, w, h, "Picture");
    }

    private void Picture(XmlWriter x, Slide slide, string target, Box box, int rotation, bool flipH, bool flipV, double cropL, double cropT, double cropR, double cropB, double w, double h, string name)
    {
        if (target.Length == 0) return;
        var f = slide.Fit;
        x.WriteStartElement("p", "pic", P);
        x.WriteStartElement("p", "nvPicPr", P);
        int id = slide.NextId++;
        NonVisual(x, id, name + " " + id.ToString(CultureInfo.InvariantCulture));
        x.WriteStartElement("p", "cNvPicPr", P);
        x.WriteStartElement("a", "picLocks", A);
        x.WriteAttributeString("noChangeAspect", "1");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteElementString("p", "nvPr", P, null);
        x.WriteEndElement();
        x.WriteStartElement("p", "blipFill", P);
        Blip(x, slide, target);
        if (cropL + cropT + cropR + cropB > 0)
        {
            x.WriteStartElement("a", "srcRect", A);
            void Crop(string a, double v) { if (v > 0) x.WriteAttributeString(a, ((long)Math.Round(v * 100000)).ToString(CultureInfo.InvariantCulture)); }
            Crop("l", cropL); Crop("t", cropT); Crop("r", cropR); Crop("b", cropB);
            x.WriteEndElement();
        }
        x.WriteStartElement("a", "stretch", A);
        x.WriteElementString("a", "fillRect", A, null);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("p", "spPr", P);
        Transform(x, "a", A, f.X(box.L), f.Y(box.T), Math.Max(1, f.Len(w)), Math.Max(1, f.Len(h)), rotation, flipH, flipV);
        Geometry(x, "rect");
        x.WriteEndElement();
        x.WriteEndElement();
        Pictures++;
    }

    private void Blip(XmlWriter x, Slide slide, string target)
    {
        if (!slide.Rels.TryGetValue(target, out var rel))
            slide.Rels[target] = rel = SlideRel(slide, "image", target);
        x.WriteStartElement("a", "blip", A);
        x.WriteAttributeString("r", "embed", R, rel);
        x.WriteEndElement();
    }

    private string SlideRel(Slide slide, string type, string target, bool external = false) => _opc.AddRelationship(slide.Part, type, target, external);

    // ------------------------------------------------------------------ text

    /// <summary>A paragraph of the page as a text box over its lines: the page's line breaks are kept, so it reads as the page does.</summary>
    private void Paragraph(XmlWriter x, Slide slide, DocParagraph p)
    {
        var lines = LinesOf(p);
        if (lines.Count == 0) return;
        double size = p.Size, pitch = p.Lines.Count >= 2 && p.Pitch > 0 ? p.Pitch : 1.2 * size;
        // PowerPoint sets a line's baseline about four fifths of the line's height under the top of its box.
        double ascent = p.Lines.Count >= 2 ? 0.8 * pitch : 0.92 * size;
        double top = p.Vertical ? p.Box.T : p.FirstBaseline - ascent;
        double height = p.Vertical ? p.Box.H : Math.Max(p.Box.B - top, lines.Count * pitch);
        var box = new Box(p.Box.L, top, p.Box.R + 0.02 * size, top + height);
        TextShape(x, slide, box, box.W, box.H, 0, p.Vertical, new[] { (p, lines) });
    }

    private void TurnedBox(XmlWriter x, Slide slide, DocTextBox box)
    {
        var content = box.Paragraphs.Where(p => !p.IsEmpty).Select(p => (p, LinesOf(p))).Where(c => c.Item2.Count > 0).ToList();
        if (content.Count == 0) return;
        double w = box.Width, h = box.Height;
        var own = new Box(box.Box.Cx - w / 2, box.Box.Cy - h / 2, box.Box.Cx + w / 2, box.Box.Cy + h / 2);
        TextShape(x, slide, own, w, h, box.Rotation, false, content);
    }

    private void TextShape(XmlWriter x, Slide slide, Box box, double w, double h, int rotation, bool vertical, IReadOnlyList<(DocParagraph P, List<List<DocRun>> Lines)> content)
    {
        var f = slide.Fit;
        x.WriteStartElement("p", "sp", P);
        x.WriteStartElement("p", "nvSpPr", P);
        int id = slide.NextId++;
        NonVisual(x, id, "TextBox " + id.ToString(CultureInfo.InvariantCulture));
        x.WriteStartElement("p", "cNvSpPr", P);
        x.WriteAttributeString("txBox", "1");
        x.WriteEndElement();
        x.WriteElementString("p", "nvPr", P, null);
        x.WriteEndElement();
        x.WriteStartElement("p", "spPr", P);
        Transform(x, "a", A, f.X(box.L), f.Y(box.T), Math.Max(1, f.Len(w)), Math.Max(1, f.Len(h)), rotation);
        Geometry(x, "rect");
        x.WriteElementString("a", "noFill", A, null);
        x.WriteEndElement();
        x.WriteStartElement("p", "txBody", P);
        x.WriteStartElement("a", "bodyPr", A);
        x.WriteAttributeString("wrap", "none");
        foreach (string inset in new[] { "lIns", "tIns", "rIns", "bIns" }) x.WriteAttributeString(inset, "0");
        x.WriteAttributeString("rtlCol", "0");
        if (vertical) x.WriteAttributeString("vert", "eaVert");
        x.WriteAttributeString("anchor", "t");
        x.WriteElementString("a", "noAutofit", A, null);
        x.WriteEndElement();
        x.WriteElementString("a", "lstStyle", A, null);
        foreach (var (p, lines) in content)
        {
            x.WriteStartElement("a", "p", A);
            ParagraphProperties(x, p, f, lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    x.WriteStartElement("a", "br", A);
                    RunProperties(x, slide, lines[i - 1].LastOrDefault() ?? p.Runs[0], f, end: true);
                    x.WriteEndElement();
                }
                foreach (var run in lines[i]) Run(x, slide, run, f);
            }
            EndParagraph(x, slide, p, f);
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteEndElement();
        TextBoxes++;
    }

    private static void ParagraphProperties(XmlWriter x, DocParagraph p, Fit f, int lines)
    {
        x.WriteStartElement("a", "pPr", A);
        string align = !p.AlignKnown ? "l" : p.Align switch { PdfTextAlignment.Center => "ctr", PdfTextAlignment.Right => "r", _ => "l" };
        x.WriteAttributeString("algn", align);
        if (p.Rtl) x.WriteAttributeString("rtl", "1");
        if (lines > 1 && p.Pitch > 0)
        {
            x.WriteStartElement("a", "lnSpc", A);
            x.WriteStartElement("a", "spcPts", A);
            x.WriteAttributeString("val", Math.Clamp((long)Math.Round(p.Pitch * f.Scale * 100), 0, 158_400).ToString(CultureInfo.InvariantCulture));
            x.WriteEndElement();
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private void EndParagraph(XmlWriter x, Slide slide, DocParagraph p, Fit f)
    {
        var last = p.Runs.LastOrDefault(r => r.Text.Length > 0) ?? new DocRun();
        x.WriteStartElement("a", "endParaRPr", A);
        RunAttributes(x, last, f);
        x.WriteEndElement();
    }

    private void Run(XmlWriter x, Slide slide, DocRun run, Fit f)
    {
        string text = TextKinds.Clean(run.Text);
        if (text.Length == 0) return;
        x.WriteStartElement("a", "r", A);
        RunProperties(x, slide, run, f, end: false);
        x.WriteElementString("a", "t", A, text);
        x.WriteEndElement();
    }

    private static void RunAttributes(XmlWriter x, DocRun run, Fit f)
    {
        x.WriteAttributeString("lang", "en-US");
        x.WriteAttributeString("sz", Math.Clamp((long)Math.Round(run.Size * f.Scale * 100), 100, 400_000).ToString(CultureInfo.InvariantCulture));
        if (run.Bold) x.WriteAttributeString("b", "1");
        if (run.Italic) x.WriteAttributeString("i", "1");
    }

    private void RunProperties(XmlWriter x, Slide slide, DocRun run, Fit f, bool end)
    {
        x.WriteStartElement("a", "rPr", A);
        RunAttributes(x, run, f);
        if (!end)
        {
            if (run.Underline) x.WriteAttributeString("u", "sng");
            if (run.Strike) x.WriteAttributeString("strike", "sngStrike");
            if (run.VertAlign != 0) x.WriteAttributeString("baseline", run.VertAlign > 0 ? "30000" : "-25000");
            if (Math.Abs(run.Spacing) > 0.01) x.WriteAttributeString("spc", Math.Clamp((long)Math.Round(run.Spacing * f.Scale * 100), -400_000, 400_000).ToString(CultureInfo.InvariantCulture));
        }
        x.WriteAttributeString("dirty", "0");
        SolidFill(x, run.Color, slide.Invisible);
        string font = TextKinds.Clean(run.Font);
        foreach (string script in new[] { "latin", "ea", "cs" })
        {
            if (script == "ea" && !run.EastAsian || script == "cs" && !run.Rtl) continue;
            x.WriteStartElement("a", script, A);
            x.WriteAttributeString("typeface", font);
            x.WriteEndElement();
        }
        if (!end && run.Link >= 0 && run.Link < _hyperlinks.Count)
        {
            if (!slide.Links.TryGetValue(run.Link, out var rel))
                slide.Links[run.Link] = rel = SlideRel(slide, "hyperlink", _hyperlinks[run.Link], external: true);
            x.WriteStartElement("a", "hlinkClick", A);
            x.WriteAttributeString("r", "id", R, rel);
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    /// <summary>A paragraph's runs, line by line as the page breaks them, without the spaces at the lines' ends.</summary>
    internal static List<List<DocRun>> LinesOf(DocParagraph p)
    {
        var lines = new List<List<DocRun>>();
        string text = p.Text;
        if (p.CharLine.Count != text.Length || p.Lines.Count < 2)
        {
            lines.Add(p.Runs.Select(r => r.Clone(r.Text)).ToList());
        }
        else
        {
            int pos = 0;
            foreach (var run in p.Runs)
            {
                int start = 0;
                for (int k = 0; k <= run.Text.Length; k++)
                {
                    bool end = k == run.Text.Length;
                    if (!end && k > start && p.CharLine[pos + k] != p.CharLine[pos + k - 1] || end && k > start)
                    {
                        int line = p.CharLine[pos + k - 1];
                        while (lines.Count <= line) lines.Add(new List<DocRun>());
                        lines[line].Add(run.Clone(run.Text[start..k]));
                        start = k;
                    }
                }
                pos += run.Text.Length;
            }
            lines.RemoveAll(l => l.Count == 0);
        }
        foreach (var line in lines)
        {
            while (line.Count > 0 && line[0].Text.TrimStart().Length == 0) line.RemoveAt(0);
            if (line.Count > 0) line[0].Text = line[0].Text.TrimStart();
            while (line.Count > 0 && line[^1].Text.TrimEnd().Length == 0) line.RemoveAt(line.Count - 1);
            if (line.Count > 0) line[^1].Text = line[^1].Text.TrimEnd();
        }
        lines.RemoveAll(l => l.Count == 0);
        return lines;
    }

    // ------------------------------------------------------------------ tables

    private void Table(XmlWriter x, Slide slide, DocTable table)
    {
        int nRows = table.Rows.Length - 1, nCols = table.Cols.Length - 1;
        if (nRows <= 0 || nCols <= 0) return;
        var f = slide.Fit;
        // Every square of the grid: the cell that starts there, or the cell whose area covers it.
        var at = new DocCell?[nRows, nCols];
        var owner = new DocCell?[nRows, nCols];
        foreach (var cell in table.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col))
        {
            if (cell.Row < 0 || cell.Col < 0 || cell.Row >= nRows || cell.Col >= nCols || owner[cell.Row, cell.Col] != null) continue;
            int rs = Math.Clamp(cell.RowSpan, 1, nRows - cell.Row), cs = Math.Clamp(cell.ColSpan, 1, nCols - cell.Col);
            bool free = true;
            for (int r = cell.Row; r < cell.Row + rs; r++)
                for (int c = cell.Col; c < cell.Col + cs; c++)
                    if (owner[r, c] != null) free = false;
            if (!free) { rs = 1; cs = 1; }
            cell.RowSpan = rs;
            cell.ColSpan = cs;
            at[cell.Row, cell.Col] = cell;
            for (int r = cell.Row; r < cell.Row + rs; r++)
                for (int c = cell.Col; c < cell.Col + cs; c++)
                    owner[r, c] = cell;
        }
        var cols = Enumerable.Range(0, nCols).Select(c => Math.Max(1, f.Len(table.Cols[c + 1] - table.Cols[c]))).ToArray();
        var rows = Enumerable.Range(0, nRows).Select(r => Math.Max(1, f.Len(table.Rows[r + 1] - table.Rows[r]))).ToArray();

        x.WriteStartElement("p", "graphicFrame", P);
        x.WriteStartElement("p", "nvGraphicFramePr", P);
        int id = slide.NextId++;
        NonVisual(x, id, "Table " + id.ToString(CultureInfo.InvariantCulture));
        x.WriteStartElement("p", "cNvGraphicFramePr", P);
        x.WriteStartElement("a", "graphicFrameLocks", A);
        x.WriteAttributeString("noGrp", "1");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteElementString("p", "nvPr", P, null);
        x.WriteEndElement();
        Transform(x, "p", P, f.X(table.Cols[0]), f.Y(table.Rows[0]), cols.Sum(), rows.Sum());
        x.WriteStartElement("a", "graphic", A);
        x.WriteStartElement("a", "graphicData", A);
        x.WriteAttributeString("uri", "http://schemas.openxmlformats.org/drawingml/2006/table");
        x.WriteStartElement("a", "tbl", A);
        x.WriteStartElement("a", "tblPr", A);
        if (table.HeaderRow) x.WriteAttributeString("firstRow", "1");
        x.WriteEndElement();
        x.WriteStartElement("a", "tblGrid", A);
        foreach (long w in cols)
        {
            x.WriteStartElement("a", "gridCol", A);
            x.WriteAttributeString("w", w.ToString(CultureInfo.InvariantCulture));
            x.WriteEndElement();
        }
        x.WriteEndElement();
        for (int r = 0; r < nRows; r++)
        {
            x.WriteStartElement("a", "tr", A);
            x.WriteAttributeString("h", rows[r].ToString(CultureInfo.InvariantCulture));
            for (int c = 0; c < nCols; c++)
            {
                x.WriteStartElement("a", "tc", A);
                var cell = owner[r, c];
                var start = at[r, c];
                if (start != null)
                {
                    if (start.RowSpan > 1) x.WriteAttributeString("rowSpan", start.RowSpan.ToString(CultureInfo.InvariantCulture));
                    if (start.ColSpan > 1) x.WriteAttributeString("gridSpan", start.ColSpan.ToString(CultureInfo.InvariantCulture));
                }
                else if (cell != null)
                {
                    // Covered by a merged cell: to the right of its start (hMerge), under it (vMerge), or both.
                    if (c > cell.Col) x.WriteAttributeString("hMerge", "1");
                    else if (cell.ColSpan > 1) x.WriteAttributeString("gridSpan", cell.ColSpan.ToString(CultureInfo.InvariantCulture));
                    if (r > cell.Row) x.WriteAttributeString("vMerge", "1");
                }
                CellBody(x, slide, start, f);
                CellProperties(x, cell, f);
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        Tables++;
    }

    private void CellBody(XmlWriter x, Slide slide, DocCell? cell, Fit f)
    {
        x.WriteStartElement("a", "txBody", A);
        x.WriteElementString("a", "bodyPr", A, null);
        x.WriteElementString("a", "lstStyle", A, null);
        var paragraphs = cell?.AllParagraphs.Where(p => !p.IsEmpty).ToList() ?? new List<DocParagraph>();
        if (paragraphs.Count == 0)
        {
            x.WriteStartElement("a", "p", A);
            x.WriteStartElement("a", "endParaRPr", A);
            RunAttributes(x, new DocRun { Size = 10 }, f);
            x.WriteEndElement();
            x.WriteEndElement();
        }
        foreach (var p in paragraphs)
        {
            x.WriteStartElement("a", "p", A);
            x.WriteStartElement("a", "pPr", A);
            x.WriteAttributeString("algn", CellAlign(cell!, paragraphs));
            if (p.Rtl) x.WriteAttributeString("rtl", "1");
            x.WriteEndElement();
            // In a cell the text wraps at the cell's width, as it does on the page.
            var runs = p.Runs.Select(r => r.Clone(r.Text)).Where(r => r.Text.Length > 0).ToList();
            if (runs.Count > 0) { runs[0].Text = runs[0].Text.TrimStart(); runs[^1].Text = runs[^1].Text.TrimEnd(); }
            foreach (var run in runs) Run(x, slide, run, f);
            EndParagraph(x, slide, p, f);
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private static string CellAlign(DocCell cell, List<DocParagraph> paragraphs)
    {
        double l = paragraphs.Min(p => p.Box.L) - cell.Box.L, r = cell.Box.R - paragraphs.Max(p => p.Box.R);
        if (Math.Abs(l - r) < Math.Max(2.5, 0.08 * cell.Box.W) && l > 3) return "ctr";
        return r < l - 4 ? "r" : "l";
    }

    private static void CellProperties(XmlWriter x, DocCell? cell, Fit f)
    {
        x.WriteStartElement("a", "tcPr", A);
        long margin = f.Len(2);
        foreach (string m in new[] { "marL", "marR" }) x.WriteAttributeString(m, margin.ToString(CultureInfo.InvariantCulture));
        foreach (string m in new[] { "marT", "marB" }) x.WriteAttributeString(m, f.Len(1).ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("anchor", cell?.VAlign == "center" ? "ctr" : cell?.VAlign == "bottom" ? "b" : "t");
        foreach (var (name, border) in new[] { ("lnL", cell?.Left), ("lnR", cell?.Right), ("lnT", cell?.Top), ("lnB", cell?.Bottom) })
        {
            x.WriteStartElement("a", name, A);
            if (border == null)
            {
                x.WriteAttributeString("w", "0");
                x.WriteElementString("a", "noFill", A, null);
            }
            else
            {
                x.WriteAttributeString("w", Math.Max(1, f.Len(border.Width)).ToString(CultureInfo.InvariantCulture));
                SolidFill(x, border.Color);
            }
            x.WriteEndElement();
        }
        if (cell?.Shading is { } shade) SolidFill(x, shade);
        else x.WriteElementString("a", "noFill", A, null);
        x.WriteEndElement();
    }

    // ------------------------------------------------------------------ presentation

    /// <summary>Writes the presentation and its master, layout, theme and properties, then the package's own parts.</summary>
    public void Finish(string? title)
    {
        const string master = "ppt/slideMasters/slideMaster1.xml", layout = "ppt/slideLayouts/slideLayout1.xml";
        _opc.AddRelationship(Presentation, "slideMaster", "slideMasters/slideMaster1.xml");
        _opc.AddRelationship(Presentation, "presProps", "presProps.xml");
        _opc.AddRelationship(Presentation, "viewProps", "viewProps.xml");
        _opc.AddRelationship(Presentation, "theme", "theme/theme1.xml");
        _opc.AddRelationship(Presentation, "tableStyles", "tableStyles.xml");
        _opc.AddRelationship(master, "slideLayout", "../slideLayouts/slideLayout1.xml");
        _opc.AddRelationship(master, "theme", "../theme/theme1.xml");
        _opc.AddRelationship(layout, "slideMaster", "../slideMasters/slideMaster1.xml");

        _opc.WritePart(Presentation, ContentBase + "presentation.main+xml", x =>
        {
            Root(x, "presentation");
            x.WriteAttributeString("saveSubsetFonts", "1");
            x.WriteStartElement("p", "sldMasterIdLst", P);
            x.WriteStartElement("p", "sldMasterId", P);
            x.WriteAttributeString("id", "2147483648");
            // The slides' relationships were made first: the master's comes after them.
            x.WriteAttributeString("r", "id", R, "rId" + (_slides + 1).ToString(CultureInfo.InvariantCulture));
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("p", "sldIdLst", P);
            for (int i = 0; i < _slides; i++)
            {
                x.WriteStartElement("p", "sldId", P);
                x.WriteAttributeString("id", (256 + i).ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("r", "id", R, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
                x.WriteEndElement();
            }
            x.WriteEndElement();
            x.WriteStartElement("p", "sldSz", P);
            x.WriteAttributeString("cx", _slideW.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("cy", _slideH.ToString(CultureInfo.InvariantCulture));
            x.WriteEndElement();
            x.WriteStartElement("p", "notesSz", P);
            x.WriteAttributeString("cx", "6858000");
            x.WriteAttributeString("cy", "9144000");
            x.WriteEndElement();
            x.WriteEndElement();
        });
        _opc.WritePart(master, ContentBase + "slideMaster+xml", x =>
        {
            Root(x, "sldMaster");
            x.WriteStartElement("p", "cSld", P);
            x.WriteStartElement("p", "bg", P);
            x.WriteStartElement("p", "bgPr", P);
            SolidFill(x, "FFFFFF");
            x.WriteElementString("a", "effectLst", A, null);
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("p", "spTree", P);
            GroupProperties(x);
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("p", "clrMap", P);
            foreach (var (k, v) in new[] { ("bg1", "lt1"), ("tx1", "dk1"), ("bg2", "lt2"), ("tx2", "dk2"), ("accent1", "accent1"), ("accent2", "accent2"), ("accent3", "accent3"),
                         ("accent4", "accent4"), ("accent5", "accent5"), ("accent6", "accent6"), ("hlink", "hlink"), ("folHlink", "folHlink") })
                x.WriteAttributeString(k, v);
            x.WriteEndElement();
            x.WriteStartElement("p", "sldLayoutIdLst", P);
            x.WriteStartElement("p", "sldLayoutId", P);
            x.WriteAttributeString("id", "2147483649");
            x.WriteAttributeString("r", "id", R, "rId1");
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteEndElement();
        });
        _opc.WritePart(layout, ContentBase + "slideLayout+xml", x =>
        {
            Root(x, "sldLayout");
            x.WriteAttributeString("type", "blank");
            x.WriteAttributeString("preserve", "1");
            x.WriteStartElement("p", "cSld", P);
            x.WriteAttributeString("name", "Blank");
            x.WriteStartElement("p", "spTree", P);
            GroupProperties(x);
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("p", "clrMapOvr", P);
            x.WriteElementString("a", "masterClrMapping", A, null);
            x.WriteEndElement();
            x.WriteEndElement();
        });
        _opc.WritePart("ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml", Theme);
        _opc.WritePart("ppt/presProps.xml", ContentBase + "presProps+xml", x => { Root(x, "presentationPr"); x.WriteEndElement(); });
        _opc.WritePart("ppt/viewProps.xml", ContentBase + "viewProps+xml", x =>
        {
            Root(x, "viewPr");
            x.WriteStartElement("p", "gridSpacing", P);
            x.WriteAttributeString("cx", "76200");
            x.WriteAttributeString("cy", "76200");
            x.WriteEndElement();
            x.WriteEndElement();
        });
        _opc.WritePart("ppt/tableStyles.xml", ContentBase + "tableStyles+xml", x =>
        {
            x.WriteStartElement("a", "tblStyleLst", A);
            x.WriteAttributeString("def", "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}");
            x.WriteEndElement();
        });
        _opc.Finish(title, Presentation);
    }

    private static void Root(XmlWriter x, string name)
    {
        x.WriteStartElement("p", name, P);
        x.WriteAttributeString("xmlns", "a", null, A);
        x.WriteAttributeString("xmlns", "r", null, R);
    }

    /// <summary>The Office theme, as small as PowerPoint takes it: colours, fonts and three of each kind of fill, line and effect.</summary>
    private static void Theme(XmlWriter x)
    {
        x.WriteStartElement("a", "theme", A);
        x.WriteAttributeString("name", "Office Theme");
        x.WriteStartElement("a", "themeElements", A);
        x.WriteStartElement("a", "clrScheme", A);
        x.WriteAttributeString("name", "Office");
        void Sys(string name, string val, string last)
        {
            x.WriteStartElement("a", name, A);
            x.WriteStartElement("a", "sysClr", A);
            x.WriteAttributeString("val", val);
            x.WriteAttributeString("lastClr", last);
            x.WriteEndElement();
            x.WriteEndElement();
        }
        void Rgb(string name, string val)
        {
            x.WriteStartElement("a", name, A);
            x.WriteStartElement("a", "srgbClr", A);
            x.WriteAttributeString("val", val);
            x.WriteEndElement();
            x.WriteEndElement();
        }
        Sys("dk1", "windowText", "000000");
        Sys("lt1", "window", "FFFFFF");
        Rgb("dk2", "44546A"); Rgb("lt2", "E7E6E6"); Rgb("accent1", "4472C4"); Rgb("accent2", "ED7D31"); Rgb("accent3", "A5A5A5");
        Rgb("accent4", "FFC000"); Rgb("accent5", "5B9BD5"); Rgb("accent6", "70AD47"); Rgb("hlink", "0563C1"); Rgb("folHlink", "954F72");
        x.WriteEndElement();
        x.WriteStartElement("a", "fontScheme", A);
        x.WriteAttributeString("name", "Office");
        foreach (var (kind, face) in new[] { ("majorFont", "Calibri Light"), ("minorFont", "Calibri") })
        {
            x.WriteStartElement("a", kind, A);
            foreach (var (script, f) in new[] { ("latin", face), ("ea", ""), ("cs", "") })
            {
                x.WriteStartElement("a", script, A);
                x.WriteAttributeString("typeface", f);
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("a", "fmtScheme", A);
        x.WriteAttributeString("name", "Office");
        void Scheme()
        {
            x.WriteStartElement("a", "solidFill", A);
            x.WriteStartElement("a", "schemeClr", A);
            x.WriteAttributeString("val", "phClr");
            x.WriteEndElement();
            x.WriteEndElement();
        }
        x.WriteStartElement("a", "fillStyleLst", A);
        for (int i = 0; i < 3; i++) Scheme();
        x.WriteEndElement();
        x.WriteStartElement("a", "lnStyleLst", A);
        foreach (string w in new[] { "6350", "12700", "19050" })
        {
            x.WriteStartElement("a", "ln", A);
            x.WriteAttributeString("w", w);
            Scheme();
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("a", "effectStyleLst", A);
        for (int i = 0; i < 3; i++)
        {
            x.WriteStartElement("a", "effectStyle", A);
            x.WriteElementString("a", "effectLst", A, null);
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteStartElement("a", "bgFillStyleLst", A);
        for (int i = 0; i < 3; i++) Scheme();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }
}
