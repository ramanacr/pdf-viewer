using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>Writes the laid-out document as WordprocessingML parts.</summary>
internal sealed class DocxWriter
{
    private const string W = DocxPackage.W;
    private readonly DocxPackage _package;
    private readonly DocDocument _doc;
    private readonly Dictionary<int, string> _linkRels = new();
    private readonly HashSet<string> _fonts = new(StringComparer.Ordinal);
    private int _drawingId, _anchorOrder;
    private bool _inHeader;
    private readonly Dictionary<DocSection, (string Header, string Footer)> _sectionRefs = new(ReferenceEqualityComparer.Instance);

    public DocxWriter(DocxPackage package, DocDocument doc)
    {
        _package = package;
        _doc = doc;
    }

    private static string Tw(double points) => ((long)Math.Round(points * 20)).ToString(CultureInfo.InvariantCulture);
    private static string Emu(double points) => ((long)Math.Round(points * 12700)).ToString(CultureInfo.InvariantCulture);
    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    public void Write()
    {
        _package.NewRelationship("styles", "styles.xml");
        _package.NewRelationship("settings", "settings.xml");
        _package.NewRelationship("fontTable", "fontTable.xml");
        if (_doc.Lists.Count > 0) _package.NewRelationship("numbering", "numbering.xml");
        for (int i = 0; i < _doc.Hyperlinks.Count; i++) _linkRels[i] = _package.NewRelationship("hyperlink", _doc.Hyperlinks[i], external: true);

        // Headers and footers: one part each, and an empty one for pages without (a section without
        // a reference would show the one before it).
        int parts = 0;
        string Part(string kind, DocHeaderFooter? content)
        {
            string name = $"{kind}{++parts}.xml";
            string id = _package.NewRelationship(kind, name);
            _package.WritePart("word/" + name, DocxPackage.WordType(kind), x =>
            {
                x.WriteStartElement("w", kind == "header" ? "hdr" : "ftr", W);
                Namespaces(x);
                _inHeader = true;
                if (content is { Floating: true })
                    Paragraph(x, new DocParagraph { TextBoxes = content.Boxes }, null);
                else if (content != null && content.Paragraphs.Count > 0)
                    foreach (var p in content.Paragraphs) Paragraph(x, p, null);
                else Paragraph(x, new DocParagraph(), null);
                _inHeader = false;
                x.WriteEndElement();
            });
            return id;
        }
        if (_doc.Headers.Count + _doc.Footers.Count > 0)
        {
            // Word takes a header with drawings that several sections share for a damaged file: those get a copy per section.
            var headerIds = _doc.Headers.Select(h => h.Floating ? null : Part("header", h)).ToList();
            var footerIds = _doc.Footers.Select(f => f.Floating ? null : Part("footer", f)).ToList();
            string emptyHeader = Part("header", null), emptyFooter = Part("footer", null);
            foreach (var s in _doc.Sections)
            {
                _sectionRefs[s] = (
                    s.HeaderVariant < 0 ? emptyHeader : headerIds[s.HeaderVariant] ?? Part("header", _doc.Headers[s.HeaderVariant]),
                    s.FooterVariant < 0 ? emptyFooter : footerIds[s.FooterVariant] ?? Part("footer", _doc.Footers[s.FooterVariant]));
            }
        }

        _package.WritePart("word/document.xml", DocxPackage.WordType("document.main"), x =>
        {
            x.WriteStartElement("w", "document", W);
            Namespaces(x);
            x.WriteStartElement("w", "body", W);
            for (int s = 0; s < _doc.Sections.Count; s++)
            {
                var section = _doc.Sections[s];
                bool last = s == _doc.Sections.Count - 1;
                for (int i = 0; i < section.Items.Count; i++)
                {
                    var item = section.Items[i];
                    bool carries = !last && i == section.Items.Count - 1 && item is DocParagraph;
                    switch (item)
                    {
                        case DocParagraph p: Paragraph(x, p, carries ? section : null); break;
                        // Tables one after the other would join into one: a hairline paragraph keeps them apart.
                        case DocTable t: Table(x, t, i > 0 && section.Items[i - 1] is DocTable); break;
                    }
                }
                // A section ending in a table (or holding nothing) ends with a paragraph of its own.
                if (!last && (section.Items.Count == 0 || section.Items[^1] is not DocParagraph))
                    Paragraph(x, new DocParagraph(), section);
                if (last)
                {
                    if (section.Items.Count > 0 && section.Items[^1] is DocTable) Paragraph(x, new DocParagraph(), null);
                    SectionProperties(x, section);
                }
            }
            if (_doc.Sections.Count == 0)
            {
                x.WriteStartElement("w", "p", W);
                x.WriteEndElement();
            }
            x.WriteEndElement();
            x.WriteEndElement();
        });

        _package.WritePart("word/styles.xml", DocxPackage.WordType("styles"), Styles);
        _package.WritePart("word/settings.xml", DocxPackage.WordType("settings"), Settings);
        if (_doc.Lists.Count > 0) _package.WritePart("word/numbering.xml", DocxPackage.WordType("numbering"), Numbering);
        _package.WritePart("word/fontTable.xml", DocxPackage.WordType("fontTable"), x =>
        {
            x.WriteStartElement("w", "fonts", W);
            foreach (string f in _fonts.Append(_doc.BodyFont).Distinct().OrderBy(f => f, StringComparer.Ordinal))
            {
                x.WriteStartElement("w", "font", W);
                x.WriteAttributeString("w", "name", W, f);
                // Where the font is not installed, Word stands in one of the same kind.
                var (serif, fixedPitch) = _doc.FontKinds.GetValueOrDefault(f);
                string alt = fixedPitch ? "Courier New" : serif ? "Times New Roman" : "Arial";
                if (alt != f) Element(x, "altName", ("val", alt));
                Element(x, "family", ("val", fixedPitch ? "modern" : serif ? "roman" : "swiss"));
                Element(x, "pitch", ("val", fixedPitch ? "fixed" : "variable"));
                x.WriteEndElement();
            }
            x.WriteEndElement();
        });
    }

    // ------------------------------------------------------------------ sections

    private static void Namespaces(XmlWriter x)
    {
        x.WriteAttributeString("xmlns", "r", null, DocxPackage.R);
        x.WriteAttributeString("xmlns", "wp", null, DocxPackage.WP);
        x.WriteAttributeString("xmlns", "a", null, DocxPackage.A);
        x.WriteAttributeString("xmlns", "pic", null, DocxPackage.Pic);
        x.WriteAttributeString("xmlns", "wps", null, DocxPackage.Wps);
    }

    private void SectionProperties(XmlWriter x, DocSection s)
    {
        x.WriteStartElement("w", "sectPr", W);
        bool numbered = false;
        if (_sectionRefs.TryGetValue(s, out var refs))
        {
            void Reference(string kind, string id)
            {
                x.WriteStartElement("w", kind + "Reference", W);
                x.WriteAttributeString("w", "type", W, "default");
                x.WriteAttributeString("r", "id", DocxPackage.R, id);
                x.WriteEndElement();
            }
            Reference("header", refs.Header);
            Reference("footer", refs.Footer);
            numbered = true;
        }
        Element(x, "type", ("val", s.Continuous ? "continuous" : "nextPage"));
        double width = Math.Clamp(s.Width, 7.2, 1584), height = Math.Clamp(s.Height, 7.2, 1584);
        x.WriteStartElement("w", "pgSz", W);
        x.WriteAttributeString("w", "w", W, Tw(width));
        x.WriteAttributeString("w", "h", W, Tw(height));
        if (width > height) x.WriteAttributeString("w", "orient", W, "landscape");
        x.WriteEndElement();
        Element(x, "pgMar", ("top", Tw(s.MarginT)), ("right", Tw(s.MarginR)), ("bottom", Tw(s.MarginB)), ("left", Tw(s.MarginL)),
            ("header", Tw(s.HeaderDistance)), ("footer", Tw(s.FooterDistance)), ("gutter", "0"));
        if (numbered && (s.PageStart != null || s.NumberFormat != "decimal"))
        {
            x.WriteStartElement("w", "pgNumType", W);
            if (s.NumberFormat != "decimal") x.WriteAttributeString("w", "fmt", W, s.NumberFormat);
            if (s.PageStart is int start) x.WriteAttributeString("w", "start", W, I(Math.Max(0, start)));
            x.WriteEndElement();
        }
        x.WriteStartElement("w", "cols", W);
        if (s.Columns.Count > 1)
        {
            x.WriteAttributeString("w", "num", W, I(s.Columns.Count));
            x.WriteAttributeString("w", "space", W, Tw(s.Columns[0].Space));
            x.WriteAttributeString("w", "equalWidth", W, "0");
            foreach (var (w, space) in s.Columns) Element(x, "col", ("w", Tw(w)), ("space", Tw(space)));
        }
        else x.WriteAttributeString("w", "space", W, "720");
        x.WriteEndElement();
        if (s.VerticalWriting) Element(x, "textDirection", ("val", "tbRl"));
        x.WriteEndElement();
    }

    // ------------------------------------------------------------------ paragraphs

    private void Paragraph(XmlWriter x, DocParagraph p, DocSection? sectionEnd)
    {
        x.WriteStartElement("w", "p", W);
        x.WriteStartElement("w", "pPr", W);
        if (p.Heading > 0) Element(x, "pStyle", ("val", "Heading" + I(p.Heading)));
        else if (_inHeader) Element(x, "pStyle", ("val", "Header"));
        // The heading style keeps with the next paragraph; not across the page break after the last thing of a page.
        if (p.Heading > 0) Element(x, "keepNext", ("val", p.LastOnPage ? "0" : "1"));
        if (p.PageBreakBefore) Element(x, "pageBreakBefore");
        if (p.List is { NumId: > 0 } list)
        {
            x.WriteStartElement("w", "numPr", W);
            Element(x, "ilvl", ("val", I(list.Level)));
            Element(x, "numId", ("val", I(list.NumId)));
            x.WriteEndElement();
        }
        if (p.Shading != null) Element(x, "shd", ("val", "clear"), ("color", "auto"), ("fill", p.Shading));
        if (p.Tabs.Count > 0)
        {
            x.WriteStartElement("w", "tabs", W);
            foreach (var (pos, kind) in p.Tabs.OrderBy(t => t.X))
                if (kind == TabKind.RightDots) Element(x, "tab", ("val", "right"), ("leader", "dot"), ("pos", Tw(pos)));
                else Element(x, "tab", ("val", kind switch { TabKind.Center => "center", TabKind.Right => "right", _ => "left" }), ("pos", Tw(pos)));
            x.WriteEndElement();
        }
        if (p.Rtl) Element(x, "bidi");
        bool text = p.Image == null && p.LineHeight > 0;
        if (text) Element(x, "spacing", ("before", Tw(p.SpaceBefore)), ("after", "0"), ("line", Tw(Math.Max(p.LineHeight, 1))), ("lineRule", "exact"));
        else if (p.Image != null) Element(x, "spacing", ("before", Tw(p.SpaceBefore)), ("after", "0"), ("line", "240"), ("lineRule", "auto"));
        else Element(x, "spacing", ("before", "0"), ("after", "0"), ("line", "20"), ("lineRule", "exact"));
        // Indents on the side lines start from: for right-to-left text that is the right (w:left means the start side there).
        double start = p.Rtl ? p.RightIndent : p.Indent, end = p.Rtl ? p.Indent : p.RightIndent;
        if (Math.Abs(start) > 0.05 || Math.Abs(end) > 0.05 || Math.Abs(p.FirstLine) > 0.05)
        {
            x.WriteStartElement("w", "ind", W);
            x.WriteAttributeString("w", "left", W, Tw(start));
            x.WriteAttributeString("w", "right", W, Tw(end));
            if (p.FirstLine < -0.05) x.WriteAttributeString("w", "hanging", W, Tw(-p.FirstLine));
            else if (p.FirstLine > 0.05) x.WriteAttributeString("w", "firstLine", W, Tw(p.FirstLine));
            x.WriteEndElement();
        }
        var align = p.Align;
        if (p.Rtl && align is PdfTextAlignment.Left or PdfTextAlignment.Right) align = align == PdfTextAlignment.Left ? PdfTextAlignment.Right : PdfTextAlignment.Left;
        string? jc = align switch { PdfTextAlignment.Center => "center", PdfTextAlignment.Right => "right", PdfTextAlignment.Justified => "both", _ => null };
        if (jc != null) Element(x, "jc", ("val", jc));
        // The paragraph mark takes the look of the text, or a hair's height in an empty paragraph.
        x.WriteStartElement("w", "rPr", W);
        if (p.Runs.Count > 0) RunLook(x, p.Runs[0], mark: true);
        else Element(x, "sz", ("val", "2"));
        x.WriteEndElement();
        if (sectionEnd != null) SectionProperties(x, sectionEnd);
        x.WriteEndElement(); // pPr

        if (p.ColumnBreakBefore)
        {
            x.WriteStartElement("w", "r", W);
            Element(x, "br", ("type", "column"));
            x.WriteEndElement();
        }
        foreach (var img in p.Anchors) Drawing(x, img, anchored: true);
        foreach (var shape in p.Shapes) Shape(x, shape);
        foreach (var box in p.TextBoxes) TextBox(x, box);
        if (p.Image != null) Drawing(x, p.Image, anchored: false);

        var runs = p.HardLines ? WithBreaks(p) : p.Runs;
        int i = 0;
        while (i < runs.Count)
        {
            int link = _inHeader ? -1 : runs[i].Link;
            if (link >= 0 && _linkRels.TryGetValue(link, out var rel))
            {
                x.WriteStartElement("w", "hyperlink", W);
                x.WriteAttributeString("r", "id", DocxPackage.R, rel);
                x.WriteAttributeString("w", "history", W, "1");
                while (i < runs.Count && runs[i].Link == link) Run(x, runs[i++]);
                x.WriteEndElement();
            }
            else Run(x, runs[i++]);
        }
        x.WriteEndElement();
    }

    /// <summary>The runs with a line break (\n) where the page's lines end, the space before it kept so words stay apart.</summary>
    private static List<DocRun> WithBreaks(DocParagraph p)
    {
        if (p.CharLine.Count != p.Text.Length) return p.Runs;
        var result = new List<DocRun>();
        int k = 0;
        foreach (var r in p.Runs)
        {
            var sb = new System.Text.StringBuilder(r.Text.Length + 2);
            foreach (char c in r.Text)
            {
                if (k > 0 && p.CharLine[k] != p.CharLine[k - 1]) sb.Append('\n');
                sb.Append(c);
                k++;
            }
            result.Add(r.Clone(sb.ToString()));
        }
        return result;
    }

    private void Run(XmlWriter x, DocRun r)
    {
        if (r.Text.Length == 0) return;
        if (r.Field != null)
        {
            x.WriteStartElement("w", "fldSimple", W);
            x.WriteAttributeString("w", "instr", W, $" {r.Field} ");
            WriteRun(x, r);
            x.WriteEndElement();
            return;
        }
        WriteRun(x, r);
    }

    private void WriteRun(XmlWriter x, DocRun r)
    {
        x.WriteStartElement("w", "r", W);
        x.WriteStartElement("w", "rPr", W);
        RunLook(x, r, mark: false);
        x.WriteEndElement();
        int from = 0;
        for (int k = 0; k <= r.Text.Length; k++)
        {
            if (k < r.Text.Length && r.Text[k] != '\t' && r.Text[k] != '\n') continue;
            if (k > from)
            {
                x.WriteStartElement("w", "t", W);
                x.WriteAttributeString("xml", "space", null, "preserve");
                x.WriteString(r.Text[from..k]);
                x.WriteEndElement();
            }
            if (k < r.Text.Length) Element(x, r.Text[k] == '\t' ? "tab" : "br");
            from = k + 1;
        }
        x.WriteEndElement();
    }

    private void RunLook(XmlWriter x, DocRun r, bool mark)
    {
        _fonts.Add(r.Font);
        x.WriteStartElement("w", "rFonts", W);
        x.WriteAttributeString("w", "ascii", W, r.Font);
        x.WriteAttributeString("w", "hAnsi", W, r.Font);
        x.WriteAttributeString("w", "eastAsia", W, r.Font);
        x.WriteAttributeString("w", "cs", W, r.Font);
        if (r.EastAsian) x.WriteAttributeString("w", "hint", W, "eastAsia");
        x.WriteEndElement();
        Element(x, "b", ("val", r.Bold ? "1" : "0"));
        Element(x, "bCs", ("val", r.Bold ? "1" : "0"));
        Element(x, "i", ("val", r.Italic ? "1" : "0"));
        Element(x, "iCs", ("val", r.Italic ? "1" : "0"));
        if (r.Strike && !mark) Element(x, "strike");
        Element(x, "color", ("val", r.Color));
        if (Math.Abs(r.Spacing) >= 0.05 && !mark) Element(x, "spacing", ("val", Tw(r.Spacing)));
        if (r.Scale != 100) Element(x, "w", ("val", I(r.Scale)));
        string size = I((long)Math.Max(2, Math.Round(r.Size * 2)));
        Element(x, "sz", ("val", size));
        Element(x, "szCs", ("val", size));
        if (r.Underline && !mark) Element(x, "u", ("val", "single"));
        if (r.VertAlign != 0 && !mark) Element(x, "vertAlign", ("val", r.VertAlign > 0 ? "superscript" : "subscript"));
        if (r.Rtl) Element(x, "rtl");
    }

    // ------------------------------------------------------------------ tables

    private void Table(XmlWriter x, DocTable t, bool afterTable)
    {
        // A table cannot carry a page or column break: a paragraph before it does.
        if (t.PageBreakBefore || t.ColumnBreakBefore || t.SpaceBefore > 2 || afterTable)
            Paragraph(x, new DocParagraph
            {
                PageBreakBefore = t.PageBreakBefore, ColumnBreakBefore = t.ColumnBreakBefore,
                LineHeight = Math.Max(1, t.SpaceBefore), Lines = { new DocLine() }, Runs = { new DocRun { Text = string.Empty, Size = 1 } },
            }, null);
        int nc = t.Cols.Length - 1, nr = t.Rows.Length - 1;
        var widths = Enumerable.Range(0, nc).Select(c => Math.Max(t.Cols[c + 1] - t.Cols[c], 2)).ToArray();
        x.WriteStartElement("w", "tbl", W);
        x.WriteStartElement("w", "tblPr", W);
        Element(x, "tblW", ("w", Tw(widths.Sum())), ("type", "dxa"));
        Element(x, "tblInd", ("w", Tw(t.Indent)), ("type", "dxa"));
        Element(x, "tblLayout", ("type", "fixed"));
        x.WriteStartElement("w", "tblCellMar", W);
        Element(x, "top", ("w", "0"), ("type", "dxa"));
        Element(x, "left", ("w", Tw(DocumentLayout.CellPad)), ("type", "dxa"));
        Element(x, "bottom", ("w", "0"), ("type", "dxa"));
        Element(x, "right", ("w", Tw(DocumentLayout.CellPad)), ("type", "dxa"));
        x.WriteEndElement();
        Element(x, "tblLook", ("val", "0000"), ("firstRow", "0"), ("lastRow", "0"), ("firstColumn", "0"), ("lastColumn", "0"), ("noHBand", "1"), ("noVBand", "1"));
        x.WriteEndElement();
        x.WriteStartElement("w", "tblGrid", W);
        foreach (double w in widths) Element(x, "gridCol", ("w", Tw(w)));
        x.WriteEndElement();

        var owner = new DocCell?[nr, nc];
        foreach (var cell in t.Cells)
            for (int r = cell.Row; r < Math.Min(nr, cell.Row + cell.RowSpan); r++)
                for (int c = cell.Col; c < Math.Min(nc, cell.Col + cell.ColSpan); c++) owner[r, c] = cell;
        for (int r = 0; r < nr; r++)
        {
            x.WriteStartElement("w", "tr", W);
            x.WriteStartElement("w", "trPr", W);
            Element(x, "trHeight", ("val", Tw(Math.Max(t.Rows[r + 1] - t.Rows[r], 1))), ("hRule", "atLeast"));
            if (r == 0 && t.HeaderRow) Element(x, "tblHeader");
            x.WriteEndElement();
            int col = 0;
            while (col < nc)
            {
                var cell = owner[r, col];
                int span = cell != null ? Math.Min(cell.ColSpan, nc - col) : 1;
                if (cell != null && cell.Col != col) span = 1; // covered from the left: not expected, written plain
                Cell(x, cell, r, span, widths.Skip(col).Take(span).Sum());
                col += span;
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private void Cell(XmlWriter x, DocCell? cell, int row, int span, double width)
    {
        bool continues = cell != null && cell.Row < row;
        x.WriteStartElement("w", "tc", W);
        x.WriteStartElement("w", "tcPr", W);
        Element(x, "tcW", ("w", Tw(width)), ("type", "dxa"));
        if (span > 1) Element(x, "gridSpan", ("val", I(span)));
        if (cell is { RowSpan: > 1 }) { if (continues) Element(x, "vMerge"); else Element(x, "vMerge", ("val", "restart")); }
        if (cell != null)
        {
            x.WriteStartElement("w", "tcBorders", W);
            Border(x, "top", cell.Top);
            Border(x, "left", cell.Left);
            Border(x, "bottom", cell.Bottom);
            Border(x, "right", cell.Right);
            x.WriteEndElement();
            if (cell.Shading != null) Element(x, "shd", ("val", "clear"), ("color", "auto"), ("fill", cell.Shading));
            // A cell of one line on the page stays one line.
            if (cell.Items == null && cell.Paragraphs.Count > 0 && cell.Paragraphs.All(p => p.Lines.Count <= 1)) Element(x, "noWrap");
            if (cell.VAlign != "top") Element(x, "vAlign", ("val", cell.VAlign));
        }
        x.WriteEndElement();
        if (cell is { Items: { } items } && !continues)
        {
            // A layout cell: its tables (and paragraphs) in order; a cell ends with a paragraph.
            for (int i = 0; i < items.Count; i++)
                if (items[i] is DocTable nested) Table(x, nested, i > 0 && items[i - 1] is DocTable);
                else if (items[i] is DocParagraph para) Paragraph(x, para, null);
            if (items.Count == 0 || items[^1] is DocTable) Paragraph(x, new DocParagraph(), null);
        }
        else if (cell == null || continues || cell.Paragraphs.Count == 0) Paragraph(x, new DocParagraph(), null);
        else foreach (var p in cell.Paragraphs) Paragraph(x, p, null);
        x.WriteEndElement();
    }

    private static void Border(XmlWriter x, string side, DocBorder? b)
    {
        if (b == null) Element(x, side, ("val", "nil"));
        else Element(x, side, ("val", "single"), ("sz", I((long)Math.Clamp(Math.Round(b.Width * 8), 2, 96))), ("space", "0"), ("color", b.Color));
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>The start of a drawing anchored on the page (no text wraps around it), up to its graphic.</summary>
    private void AnchorStart(XmlWriter x, int id, string name, double left, double top, double width, double height, bool behind)
    {
        const string wp = DocxPackage.WP;
        x.WriteStartElement("w", "r", W);
        x.WriteStartElement("w", "drawing", W);
        x.WriteStartElement("wp", "anchor", wp);
        foreach (var (k, v) in new[] { ("distT", "0"), ("distB", "0"), ("distL", "0"), ("distR", "0"), ("simplePos", "0"),
                     ("relativeHeight", I(251658240 + ++_anchorOrder)), ("behindDoc", behind ? "1" : "0"), ("locked", "0"), ("layoutInCell", "1"), ("allowOverlap", "1") })
            x.WriteAttributeString(k, v);
        x.WriteStartElement("wp", "simplePos", wp);
        x.WriteAttributeString("x", "0");
        x.WriteAttributeString("y", "0");
        x.WriteEndElement();
        foreach (var (tag, value) in new[] { ("positionH", left), ("positionV", top) })
        {
            x.WriteStartElement("wp", tag, wp);
            x.WriteAttributeString("relativeFrom", "page");
            x.WriteElementString("wp", "posOffset", wp, Emu(value));
            x.WriteEndElement();
        }
        x.WriteStartElement("wp", "extent", wp);
        x.WriteAttributeString("cx", Emu(Math.Max(0, width)));
        x.WriteAttributeString("cy", Emu(Math.Max(0, height)));
        x.WriteEndElement();
        x.WriteStartElement("wp", "effectExtent", wp);
        foreach (var k in new[] { "l", "t", "r", "b" }) x.WriteAttributeString(k, "0");
        x.WriteEndElement();
        x.WriteStartElement("wp", "wrapNone", wp);
        x.WriteEndElement();
        x.WriteStartElement("wp", "docPr", wp);
        x.WriteAttributeString("id", I(id));
        x.WriteAttributeString("name", name + " " + I(id));
        x.WriteEndElement();
        x.WriteStartElement("wp", "cNvGraphicFramePr", wp);
        x.WriteEndElement();
        x.WriteStartElement("a", "graphic", DocxPackage.A);
        x.WriteStartElement("a", "graphicData", DocxPackage.A);
        x.WriteAttributeString("uri", DocxPackage.Wps);
    }

    private static void AnchorEnd(XmlWriter x)
    {
        x.WriteEndElement(); // graphicData
        x.WriteEndElement(); // graphic
        x.WriteEndElement(); // anchor
        x.WriteEndElement(); // drawing
        x.WriteEndElement(); // r
    }

    private static void Transform(XmlWriter x, double width, double height, int rotation = 0, bool flipH = false, bool flipV = false)
    {
        const string a = DocxPackage.A;
        x.WriteStartElement("a", "xfrm", a);
        if (rotation != 0) x.WriteAttributeString("rot", I(rotation * 60000L));
        if (flipH) x.WriteAttributeString("flipH", "1");
        if (flipV) x.WriteAttributeString("flipV", "1");
        x.WriteStartElement("a", "off", a);
        x.WriteAttributeString("x", "0");
        x.WriteAttributeString("y", "0");
        x.WriteEndElement();
        x.WriteStartElement("a", "ext", a);
        x.WriteAttributeString("cx", Emu(Math.Max(0, width)));
        x.WriteAttributeString("cy", Emu(Math.Max(0, height)));
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Geometry(XmlWriter x, string preset)
    {
        x.WriteStartElement("a", "prstGeom", DocxPackage.A);
        x.WriteAttributeString("prst", preset);
        x.WriteStartElement("a", "avLst", DocxPackage.A);
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Fill(XmlWriter x, string? color)
    {
        const string a = DocxPackage.A;
        if (color == null) { x.WriteStartElement("a", "noFill", a); x.WriteEndElement(); return; }
        x.WriteStartElement("a", "solidFill", a);
        x.WriteStartElement("a", "srgbClr", a);
        x.WriteAttributeString("val", color);
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Outline(XmlWriter x, string? color, double width)
    {
        x.WriteStartElement("a", "ln", DocxPackage.A);
        if (color != null) x.WriteAttributeString("w", Emu(Math.Max(0.25, width)));
        Fill(x, color);
        x.WriteEndElement();
    }

    /// <summary>A text box at its place on the page, turned as the text was; its lines do not wrap.</summary>
    private void TextBox(XmlWriter x, DocTextBox box)
    {
        const string wps = DocxPackage.Wps;
        int id = ++_drawingId;
        // The frame before it is turned: centred where the text is.
        AnchorStart(x, id, "Text Box", box.Box.Cx - box.Width / 2, box.Box.Cy - box.Height / 2, box.Width, box.Height, behind: false);
        x.WriteStartElement("wps", "wsp", wps);
        x.WriteStartElement("wps", "cNvSpPr", wps);
        x.WriteAttributeString("txBox", "1");
        x.WriteEndElement();
        x.WriteStartElement("wps", "spPr", wps);
        Transform(x, box.Width, box.Height, box.Rotation);
        Geometry(x, "rect");
        Fill(x, null);
        Outline(x, null, 0);
        x.WriteEndElement();
        x.WriteStartElement("wps", "txbx", wps);
        x.WriteStartElement("w", "txbxContent", W);
        foreach (var p in box.Paragraphs) Paragraph(x, p, null);
        if (box.Paragraphs.Count == 0) Paragraph(x, new DocParagraph(), null);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("wps", "bodyPr", wps);
        foreach (var (k, v) in new[] { ("rot", "0"), ("vert", "horz"), ("wrap", "none"), ("lIns", "0"), ("tIns", "0"), ("rIns", "0"), ("bIns", "0"), ("anchor", "t"), ("anchorCtr", "0") })
            x.WriteAttributeString(k, v);
        x.WriteStartElement("a", "noAutofit", DocxPackage.A);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement(); // wsp
        AnchorEnd(x);
    }

    /// <summary>A rectangle or line the page draws, as a Word shape at its place; a picture of a group of drawings as a picture.</summary>
    private void Shape(XmlWriter x, DocShape s)
    {
        if (s.Kind == ShapeKind.Picture)
        {
            Drawing(x, new DocImage { RelId = s.RelId, Box = s.Box, Width = s.Box.W, Height = s.Box.H, Anchored = true, Behind = s.Behind }, anchored: true);
            return;
        }
        const string wps = DocxPackage.Wps;
        int id = ++_drawingId;
        AnchorStart(x, id, s.Kind == ShapeKind.Line ? "Line" : "Rectangle", s.Box.L, s.Box.T, s.Box.W, s.Box.H, s.Behind);
        x.WriteStartElement("wps", "wsp", wps);
        x.WriteStartElement("wps", "cNvSpPr", wps);
        x.WriteEndElement();
        x.WriteStartElement("wps", "spPr", wps);
        Transform(x, s.Box.W, s.Box.H, 0, s.FlipH, s.FlipV);
        Geometry(x, s.Kind == ShapeKind.Line ? "line" : "rect");
        if (s.Kind == ShapeKind.Line) Fill(x, null); else Fill(x, s.Fill);
        Outline(x, s.Stroke, s.StrokeWidth);
        x.WriteEndElement();
        x.WriteStartElement("wps", "bodyPr", wps);
        x.WriteEndElement();
        x.WriteEndElement(); // wsp
        AnchorEnd(x);
    }

    private void Drawing(XmlWriter x, DocImage img, bool anchored)
    {
        int id = ++_drawingId;
        x.WriteStartElement("w", "r", W);
        x.WriteStartElement("w", "drawing", W);
        const string wp = DocxPackage.WP, a = DocxPackage.A;
        if (anchored)
        {
            x.WriteStartElement("wp", "anchor", wp);
            foreach (var (k, v) in new[] { ("distT", "0"), ("distB", "0"), ("distL", img.Behind ? "0" : "57150"), ("distR", img.Behind ? "0" : "57150"), ("simplePos", "0"),
                         ("relativeHeight", I(251658240 + ++_anchorOrder)), ("behindDoc", img.Behind ? "1" : "0"), ("locked", "0"), ("layoutInCell", "1"), ("allowOverlap", "1") })
                x.WriteAttributeString(k, v);
            x.WriteStartElement("wp", "simplePos", wp);
            x.WriteAttributeString("x", "0");
            x.WriteAttributeString("y", "0");
            x.WriteEndElement();
            // The frame before it is turned: centred where the picture is.
            double left = img.Box.Cx - img.Width / 2, top = img.Box.Cy - img.Height / 2;
            foreach (var (tag, value) in new[] { ("positionH", left), ("positionV", top) })
            {
                x.WriteStartElement("wp", tag, wp);
                x.WriteAttributeString("relativeFrom", "page");
                x.WriteElementString("wp", "posOffset", wp, Emu(value));
                x.WriteEndElement();
            }
        }
        else
        {
            x.WriteStartElement("wp", "inline", wp);
            foreach (var k in new[] { "distT", "distB", "distL", "distR" }) x.WriteAttributeString(k, "0");
        }
        x.WriteStartElement("wp", "extent", wp);
        x.WriteAttributeString("cx", Emu(img.Width));
        x.WriteAttributeString("cy", Emu(img.Height));
        x.WriteEndElement();
        x.WriteStartElement("wp", "effectExtent", wp);
        foreach (var k in new[] { "l", "t", "r", "b" }) x.WriteAttributeString(k, "0");
        x.WriteEndElement();
        // Text keeps the places the page gives it (its indents and spacing already leave room), so nothing wraps.
        if (anchored) { x.WriteStartElement("wp", "wrapNone", wp); x.WriteEndElement(); }
        x.WriteStartElement("wp", "docPr", wp);
        x.WriteAttributeString("id", I(id));
        x.WriteAttributeString("name", "Picture " + I(id));
        x.WriteEndElement();
        x.WriteStartElement("wp", "cNvGraphicFramePr", wp);
        x.WriteStartElement("a", "graphicFrameLocks", a);
        x.WriteAttributeString("noChangeAspect", "1");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("a", "graphic", a);
        x.WriteStartElement("a", "graphicData", a);
        x.WriteAttributeString("uri", DocxPackage.Pic);
        x.WriteStartElement("pic", "pic", DocxPackage.Pic);
        x.WriteStartElement("pic", "nvPicPr", DocxPackage.Pic);
        x.WriteStartElement("pic", "cNvPr", DocxPackage.Pic);
        x.WriteAttributeString("id", I(id));
        x.WriteAttributeString("name", "Picture " + I(id));
        x.WriteEndElement();
        x.WriteStartElement("pic", "cNvPicPr", DocxPackage.Pic);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("pic", "blipFill", DocxPackage.Pic);
        x.WriteStartElement("a", "blip", a);
        x.WriteAttributeString("r", "embed", DocxPackage.R, img.RelId);
        x.WriteEndElement();
        if (img.CropL + img.CropT + img.CropR + img.CropB > 0.0001)
        {
            x.WriteStartElement("a", "srcRect", a);
            foreach (var (k, v) in new[] { ("l", img.CropL), ("t", img.CropT), ("r", img.CropR), ("b", img.CropB) })
                if (v > 0) x.WriteAttributeString(k, I((long)Math.Round(v * 100000)));
            x.WriteEndElement();
        }
        x.WriteStartElement("a", "stretch", a);
        x.WriteStartElement("a", "fillRect", a);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("pic", "spPr", DocxPackage.Pic);
        x.WriteStartElement("a", "xfrm", a);
        if (img.Rotation != 0) x.WriteAttributeString("rot", I(img.Rotation * 60000L));
        if (img.FlipH) x.WriteAttributeString("flipH", "1");
        if (img.FlipV) x.WriteAttributeString("flipV", "1");
        x.WriteStartElement("a", "off", a);
        x.WriteAttributeString("x", "0");
        x.WriteAttributeString("y", "0");
        x.WriteEndElement();
        x.WriteStartElement("a", "ext", a);
        x.WriteAttributeString("cx", Emu(img.Width));
        x.WriteAttributeString("cy", Emu(img.Height));
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("a", "prstGeom", a);
        x.WriteAttributeString("prst", "rect");
        x.WriteStartElement("a", "avLst", a);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement(); // spPr
        x.WriteEndElement(); // pic
        x.WriteEndElement(); // graphicData
        x.WriteEndElement(); // graphic
        x.WriteEndElement(); // inline / anchor
        x.WriteEndElement(); // drawing
        x.WriteEndElement(); // r
    }

    // ------------------------------------------------------------------ styles, settings, numbering

    private void Styles(XmlWriter x)
    {
        x.WriteStartElement("w", "styles", W);
        x.WriteStartElement("w", "docDefaults", W);
        x.WriteStartElement("w", "rPrDefault", W);
        x.WriteStartElement("w", "rPr", W);
        Element(x, "rFonts", ("ascii", _doc.BodyFont), ("hAnsi", _doc.BodyFont), ("eastAsia", _doc.BodyFont), ("cs", _doc.BodyFont));
        string size = I((long)Math.Max(2, Math.Round(_doc.BodySize * 2)));
        Element(x, "sz", ("val", size));
        Element(x, "szCs", ("val", size));
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("w", "pPrDefault", W);
        x.WriteStartElement("w", "pPr", W);
        // Lines stay on the page the PDF put them on.
        Element(x, "widowControl", ("val", "0"));
        Element(x, "spacing", ("after", "0"), ("line", "240"), ("lineRule", "auto"));
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();

        void Style(string type, string id, string name, bool isDefault, Action? body = null)
        {
            x.WriteStartElement("w", "style", W);
            x.WriteAttributeString("w", "type", W, type);
            if (isDefault) x.WriteAttributeString("w", "default", W, "1");
            x.WriteAttributeString("w", "styleId", W, id);
            Element(x, "name", ("val", name));
            body?.Invoke();
            x.WriteEndElement();
        }
        Style("paragraph", "Normal", "Normal", true, () => Element(x, "qFormat"));
        for (int level = 1; level <= 3; level++)
        {
            int l = level;
            Style("paragraph", "Heading" + I(l), "heading " + I(l), false, () =>
            {
                Element(x, "basedOn", ("val", "Normal"));
                Element(x, "next", ("val", "Normal"));
                Element(x, "uiPriority", ("val", "9"));
                Element(x, "qFormat");
                x.WriteStartElement("w", "pPr", W);
                Element(x, "keepNext");
                Element(x, "outlineLvl", ("val", I(l - 1)));
                x.WriteEndElement();
            });
        }
        Style("paragraph", "Header", "header", false, () => Element(x, "basedOn", ("val", "Normal")));
        Style("paragraph", "Footer", "footer", false, () => Element(x, "basedOn", ("val", "Normal")));
        Style("character", "DefaultParagraphFont", "Default Paragraph Font", true, () => { Element(x, "uiPriority", ("val", "1")); Element(x, "semiHidden"); });
        Style("table", "TableNormal", "Normal Table", true, () =>
        {
            Element(x, "semiHidden");
            x.WriteStartElement("w", "tblPr", W);
            Element(x, "tblInd", ("w", "0"), ("type", "dxa"));
            x.WriteStartElement("w", "tblCellMar", W);
            Element(x, "top", ("w", "0"), ("type", "dxa"));
            Element(x, "left", ("w", "108"), ("type", "dxa"));
            Element(x, "bottom", ("w", "0"), ("type", "dxa"));
            Element(x, "right", ("w", "108"), ("type", "dxa"));
            x.WriteEndElement();
            x.WriteEndElement();
        });
        Style("numbering", "NoList", "No List", true, () => Element(x, "semiHidden"));
        x.WriteEndElement();
    }

    private static void Settings(XmlWriter x)
    {
        x.WriteStartElement("w", "settings", W);
        Element(x, "zoom", ("percent", "100"));
        Element(x, "defaultTabStop", ("val", "720"));
        Element(x, "characterSpacingControl", ("val", "doNotCompress"));
        x.WriteStartElement("w", "compat", W);
        Element(x, "compatSetting", ("name", "compatibilityMode"), ("uri", "http://schemas.microsoft.com/office/word"), ("val", "15"));
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private void Numbering(XmlWriter x)
    {
        x.WriteStartElement("w", "numbering", W);
        foreach (var list in _doc.Lists)
        {
            x.WriteStartElement("w", "abstractNum", W);
            x.WriteAttributeString("w", "abstractNumId", W, I(list.NumId - 1));
            Element(x, "multiLevelType", ("val", "hybridMultilevel"));
            for (int level = 0; level < 9; level++)
            {
                list.Levels.TryGetValue(level, out var m);
                string format = m?.Format ?? "bullet";
                string text = m == null ? "•" : format == "bullet" ? m.Text : m.Text.Replace("%1", "%" + I(level + 1), StringComparison.Ordinal);
                x.WriteStartElement("w", "lvl", W);
                x.WriteAttributeString("w", "ilvl", W, I(level));
                Element(x, "start", ("val", I(format == "bullet" ? 1 : Math.Max(0, m?.Value ?? 1))));
                Element(x, "numFmt", ("val", format));
                Element(x, "lvlText", ("val", text));
                Element(x, "lvlJc", ("val", "left"));
                x.WriteStartElement("w", "pPr", W);
                Element(x, "ind", ("left", I(360 * (level + 1))), ("hanging", "360"));
                x.WriteEndElement();
                if (m?.Look is { } look)
                {
                    x.WriteStartElement("w", "rPr", W);
                    Element(x, "rFonts", ("ascii", look.Font), ("hAnsi", look.Font), ("eastAsia", look.Font), ("cs", look.Font));
                    Element(x, "b", ("val", look.Bold ? "1" : "0"));
                    Element(x, "i", ("val", look.Italic ? "1" : "0"));
                    Element(x, "color", ("val", look.Color));
                    Element(x, "sz", ("val", I((long)Math.Max(2, Math.Round(look.Size * 2)))));
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        foreach (var list in _doc.Lists)
        {
            x.WriteStartElement("w", "num", W);
            x.WriteAttributeString("w", "numId", W, I(list.NumId));
            Element(x, "abstractNumId", ("val", I(list.NumId - 1)));
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private static void Element(XmlWriter x, string name, params (string Name, string Value)[] attributes)
    {
        x.WriteStartElement("w", name, W);
        foreach (var (n, v) in attributes) x.WriteAttributeString("w", n, W, v);
        x.WriteEndElement();
    }
}
