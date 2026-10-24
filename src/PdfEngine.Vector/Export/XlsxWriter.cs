using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using PdfEngine.Vector.Editing;

namespace PdfEngine.Vector.Export;

/// <summary>
/// Writes the SpreadsheetML of a workbook by hand: each sheet as it is finished, then the
/// workbook, its shared strings and its styles. A table's cells keep their place in the grid
/// (merged where the PDF's cells span), the PDF's column widths and row heights, their fonts,
/// shading and ruling lines; numbers and dates are stored as values with a number format.
/// </summary>
internal sealed class XlsxWriter
{
    internal const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string ContentBase = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
    private const string Workbook = "xl/workbook.xml";
    private const int MaxRows = 1_048_576, MaxCols = 16_384, MaxCellText = 32_767;
    /// <summary>The default font's widest digit, in pixels: Excel's unit of column width.</summary>
    private const double DigitPixels = 7;

    private readonly OpcPackage _opc;
    private readonly List<string> _sheets = new();
    private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _strings = new();
    private readonly List<List<DocRun>?> _rich = new();
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);
    private int _stringRefs;

    private readonly List<string> _fonts = new() { "Calibri|11|0|0|0|0|000000" };
    private readonly List<string> _fills = new() { "none", "gray125" };
    private readonly List<string> _borders = new() { "||||" };
    private readonly List<string> _numFmts = new();
    private readonly List<(int NumFmt, int Font, int Fill, int Border, string H, string V, bool Wrap)> _xfs = new() { (0, 0, 0, 0, "", "", false) };
    private readonly Dictionary<string, int> _fontIndex = new(StringComparer.Ordinal), _fillIndex = new(StringComparer.Ordinal), _borderIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<(int, int, int, int, string, string, bool), int> _xfIndex = new();

    public int Tables, Cells, Numbers, Dates, Merged, TextRows;
    public List<string> Warnings { get; } = new();

    public XlsxWriter(OpcPackage opc)
    {
        _opc = opc;
        _fontIndex[_fonts[0]] = 0;
    }

    public int SheetCount => _sheets.Count;

    private sealed class CellData
    {
        public int Style;
        /// <summary>s: a shared string; n: a number; empty: styled only (covered by a merge, or blank).</summary>
        public string Type = string.Empty;
        public string Value = string.Empty;
    }

    private sealed class RowData
    {
        public double? Height;
        public SortedDictionary<int, CellData> Cells = new();
    }

    /// <summary>Writes a sheet of the given tables and paragraphs, one under the other in the order given.</summary>
    public void AddSheet(string name, IReadOnlyList<DocItem> items)
    {
        var rows = new SortedDictionary<int, RowData>();
        var widths = new Dictionary<int, double>();
        var merges = new List<string>();
        int row = 0;
        bool afterTable = false, full = false;
        foreach (var item in items)
        {
            if (item is DocTable table)
            {
                if (row > 0) row++;
                int used = PlaceTable(table, row, rows, widths, merges);
                if (used < 0) { full = true; break; }
                row += used;
                afterTable = true;
                Tables++;
            }
            else if (item is DocParagraph p && !p.IsEmpty && p.Image == null)
            {
                if (afterTable) row++;
                afterTable = false;
                if (row >= MaxRows) { full = true; break; }
                string text = Limit(p.Text.Trim());
                var main = MainRun(p.Runs);
                int style = Style(0, FontOf(main), 0, 0, "", "", false);
                var r = Row(rows, row);
                r.Cells[0] = new CellData { Style = style, Type = "s", Value = String(text, Rich(new[] { p })).ToString(CultureInfo.InvariantCulture) };
                if (main.Size > 13) r.Height = Math.Min(409, Math.Round(main.Size * 1.3, 2));
                TextRows++;
                row++;
            }
        }
        if (full) Warnings.Add("A sheet ran out of rows; the rest of its content was left out.");
        WriteSheet(UniqueName(name), rows, widths, merges);
    }

    private static RowData Row(SortedDictionary<int, RowData> rows, int r)
    {
        if (!rows.TryGetValue(r, out var data)) rows[r] = data = new RowData();
        return data;
    }

    /// <summary>Places a table with its top row at <paramref name="top"/>; returns the rows it takes, or -1 when the sheet has no room.</summary>
    private int PlaceTable(DocTable table, int top, SortedDictionary<int, RowData> rows, Dictionary<int, double> widths, List<string> merges)
    {
        int nRows = table.Rows.Length - 1, nCols = Math.Min(table.Cols.Length - 1, MaxCols);
        if (nRows <= 0 || nCols <= 0) return 0;
        if (top + nRows > MaxRows) return -1;

        // How each column writes its dates: a day over 12 anywhere in it says which part is the day.
        var orders = new DateOrder[nCols];
        foreach (var group in table.Cells.Where(c => c.ColSpan == 1 && c.Col < nCols).GroupBy(c => c.Col))
        {
            var seen = group.Select(c => CellValues.Evidence(CellText(c))).Where(o => o != DateOrder.Unknown).Distinct().ToList();
            orders[group.Key] = seen.Count == 1 ? seen[0] : DateOrder.Unknown;
        }

        var taken = new bool[nRows, nCols];
        var heights = new double[nRows];
        for (int r = 0; r < nRows; r++) heights[r] = table.Rows[r + 1] - table.Rows[r];
        var colWidths = new double[nCols];
        for (int c = 0; c < nCols; c++) colWidths[c] = table.Cols[c + 1] - table.Cols[c];

        foreach (var cell in table.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col))
        {
            if (cell.Row < 0 || cell.Col < 0 || cell.Row >= nRows || cell.Col >= nCols || taken[cell.Row, cell.Col]) continue;
            int rowSpan = Math.Clamp(cell.RowSpan, 1, nRows - cell.Row), colSpan = Math.Clamp(cell.ColSpan, 1, nCols - cell.Col);
            // Cells that would overlap another's area are kept to their own square: overlapping merges make Excel repair the file.
            for (int r = cell.Row; r < cell.Row + rowSpan; r++)
                for (int c = cell.Col; c < cell.Col + colSpan; c++)
                    if (taken[r, c]) { rowSpan = 1; colSpan = 1; }
            for (int r = cell.Row; r < cell.Row + rowSpan; r++)
                for (int c = cell.Col; c < cell.Col + colSpan; c++)
                    taken[r, c] = true;

            string text = Limit(CellText(cell));
            var paragraphs = cell.AllParagraphs.Where(p => !p.IsEmpty).ToList();
            var runs = paragraphs.SelectMany(p => p.Runs).ToList();
            var main = MainRun(runs);
            int lines = paragraphs.Sum(p => Math.Max(1, p.Lines.Count));
            var value = paragraphs.Count == 1 ? CellValues.Parse(text, colSpan == 1 ? orders[cell.Col] : DateOrder.Unknown) : CellValue.Text;
            bool wrap = value.Kind == CellKind.Text && lines > 1;
            int font = text.Length > 0 ? FontOf(main) : 0;
            int fill = cell.Shading is { } shade ? Fill(shade) : 0;
            string h = HorizontalAlignment(cell, paragraphs, value.Kind);
            string v = cell.VAlign == "center" ? "center" : cell.VAlign == "bottom" ? "bottom" : "top";
            int numFmt = value.Kind == CellKind.Text ? 0 : value.Format.Length == 0 ? 0 : NumFmt(value.Format);

            // Each square of the cell's area: the area's own edges are its borders.
            for (int r = cell.Row; r < cell.Row + rowSpan; r++)
                for (int c = cell.Col; c < cell.Col + colSpan; c++)
                {
                    int border = Border(c == cell.Col ? cell.Left : null, c == cell.Col + colSpan - 1 ? cell.Right : null,
                        r == cell.Row ? cell.Top : null, r == cell.Row + rowSpan - 1 ? cell.Bottom : null);
                    int style = Style(numFmt, font, fill, border, h, v, wrap);
                    var data = new CellData { Style = style };
                    if (r == cell.Row && c == cell.Col && text.Length > 0)
                    {
                        if (value.Kind == CellKind.Text)
                        {
                            data.Type = "s";
                            data.Value = String(text, Rich(paragraphs)).ToString(CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            data.Type = "n";
                            data.Value = value.Number.ToString("R", CultureInfo.InvariantCulture);
                            if (value.Kind == CellKind.Number) Numbers++; else Dates++;
                        }
                        Cells++;
                    }
                    if (style != 0 || data.Type.Length > 0) Row(rows, top + r).Cells[c] = data;
                }
            if (rowSpan > 1 || colSpan > 1)
            {
                merges.Add($"{Ref(top + cell.Row, cell.Col)}:{Ref(top + cell.Row + rowSpan - 1, cell.Col + colSpan - 1)}");
                Merged++;
            }

            // Room for what the cell holds, in the font Excel will show it in.
            double size = Math.Max(main.Size, 6);
            if (rowSpan == 1) heights[cell.Row] = Math.Max(heights[cell.Row], lines * size * 1.25 + 2);
            if (colSpan == 1 && value.Kind != CellKind.Text)
                colWidths[cell.Col] = Math.Max(colWidths[cell.Col], (text.Length + 1) * size * 0.6 + 6);
        }

        for (int r = 0; r < nRows; r++) Row(rows, top + r).Height = Math.Round(Math.Clamp(heights[r], 6, 409), 2);
        for (int c = 0; c < nCols; c++) widths[c] = Math.Max(widths.GetValueOrDefault(c), colWidths[c]);
        return nRows;
    }

    private static string CellText(DocCell cell) =>
        string.Join("\n", cell.AllParagraphs.Where(p => !p.IsEmpty).Select(p => p.Text.Trim()));

    private static string Limit(string text) => CleanLines(text.Length > MaxCellText ? text[..MaxCellText] : text);

    /// <summary>Text XML can hold, with its line breaks (a cell of several lines keeps them).</summary>
    private static string CleanLines(string text) => text.Contains('\n') ? string.Join("\n", text.Split('\n').Select(TextKinds.Clean)) : TextKinds.Clean(text);

    /// <summary>Where the text sits across the cell, from where the page puts it: centred, against the right, or (the default) the left.</summary>
    private static string HorizontalAlignment(DocCell cell, List<DocParagraph> paragraphs, CellKind kind)
    {
        if (paragraphs.Count == 0) return string.Empty;
        double l = paragraphs.Min(p => p.Box.L) - cell.Box.L, r = cell.Box.R - paragraphs.Max(p => p.Box.R);
        bool centred = Math.Abs(l - r) < Math.Max(2.5, 0.08 * cell.Box.W) && l > 3 || paragraphs.All(p => p.AlignKnown && p.Align == PdfTextAlignment.Center);
        bool right = !centred && r < l - 4;
        // Numbers go to the right on their own.
        if (kind != CellKind.Text) return centred ? "center" : right ? string.Empty : "left";
        return centred ? "center" : right ? "right" : string.Empty;
    }

    private static DocRun MainRun(IEnumerable<DocRun> runs) =>
        runs.Where(r => r.Text.Trim().Length > 0).GroupBy(r => (r.Font, r.Size, r.Bold, r.Italic, r.Color))
            .OrderByDescending(g => g.Sum(r => r.Text.Length)).Select(g => g.First()).FirstOrDefault() ?? new DocRun();

    /// <summary>The runs of the text when they do not all look the same (a bold word in a cell), or null.</summary>
    private static List<DocRun>? Rich(IReadOnlyList<DocParagraph> paragraphs)
    {
        var runs = new List<DocRun>();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            // Characters XML cannot hold are left out first: a run of only those would be an empty run, which Excel rejects.
            var own = paragraphs[i].Runs.Select(r => r.Clone(CleanLines(r.Text))).Where(r => r.Text.Length > 0).ToList();
            for (int k = 0; k < own.Count; k++)
            {
                string t = own[k].Text;
                if (k == 0) t = t.TrimStart();
                if (k == own.Count - 1) t = t.TrimEnd() + (i < paragraphs.Count - 1 ? "\n" : string.Empty);
                if (t.Length == 0) continue;
                if (runs.Count > 0 && Look(runs[^1]) == Look(own[k])) runs[^1].Text += t;
                else runs.Add(own[k].Clone(t));
            }
        }
        return runs.Select(Look).Distinct().Count() > 1 ? runs : null;
    }

    private static string Look(DocRun r) => FormattableString.Invariant($"{FontNames(r)}|{r.Size}|{(r.Bold ? 1 : 0)}|{(r.Italic ? 1 : 0)}|{(r.Underline ? 1 : 0)}|{(r.Strike ? 1 : 0)}|{r.Color}");

    /// <summary>The font's name as Excel keeps it: at most 31 characters.</summary>
    private static string FontNames(DocRun r)
    {
        string name = TextKinds.Clean(r.Font).Replace('|', ' ').Trim();
        if (name.Length == 0) name = "Calibri";
        return name.Length > 31 ? name[..31] : name;
    }

    // ------------------------------------------------------------------ shared strings and styles

    private int String(string text, List<DocRun>? rich)
    {
        if (rich != null && string.Concat(rich.Select(r => r.Text)).Length > MaxCellText) rich = null;
        string key = rich == null ? "t:" + text : "r:" + string.Join("\u0001", rich.Select(r => Look(r) + "\u0002" + r.Text));
        _stringRefs++;
        if (_stringIndex.TryGetValue(key, out int index)) return index;
        index = _strings.Count;
        _strings.Add(text);
        _rich.Add(rich);
        _stringIndex[key] = index;
        return index;
    }

    private int FontOf(DocRun main) => Index(_fonts, _fontIndex, Look(main));

    private int Fill(string color) => Index(_fills, _fillIndex, color);

    private int Border(DocBorder? left, DocBorder? right, DocBorder? top, DocBorder? bottom)
    {
        static string Edge(DocBorder? b) => b == null ? string.Empty : FormattableString.Invariant($"{EdgeStyle(b.Width)}:{b.Color}");
        string key = $"{Edge(left)}|{Edge(right)}|{Edge(top)}|{Edge(bottom)}|";
        return Index(_borders, _borderIndex, key);
    }

    private static string EdgeStyle(double width) => width <= 1.1 ? "thin" : width <= 2.2 ? "medium" : "thick";

    private int NumFmt(string code)
    {
        int i = _numFmts.IndexOf(code);
        if (i < 0) { _numFmts.Add(code); i = _numFmts.Count - 1; }
        return 164 + i;
    }

    private int Style(int numFmt, int font, int fill, int border, string h, string v, bool wrap)
    {
        var key = (numFmt, font, fill, border, h, v, wrap);
        if (numFmt == 0 && font == 0 && fill == 0 && border == 0 && h.Length == 0 && (v.Length == 0 || v == "bottom") && !wrap) return 0;
        if (_xfIndex.TryGetValue(key, out int i)) return i;
        _xfs.Add(key);
        return _xfIndex[key] = _xfs.Count - 1;
    }

    private static int Index(List<string> list, Dictionary<string, int> index, string key)
    {
        if (index.TryGetValue(key, out int i)) return i;
        list.Add(key);
        return index[key] = list.Count - 1;
    }

    // ------------------------------------------------------------------ parts

    /// <summary>A sheet name Excel takes: no []:*?/\, at most 31 characters, unique whatever the case.</summary>
    private string UniqueName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in TextKinds.Clean(name)) sb.Append(c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? ' ' : c);
        string clean = sb.ToString().Trim().Trim('\'');
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];
        string unique = clean;
        for (int n = 2; _sheetNames.Contains(unique); n++)
        {
            string suffix = $" ({n})";
            unique = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        _sheetNames.Add(unique);
        return unique;
    }

    internal static string Ref(int row, int col) => Column(col) + (row + 1).ToString(CultureInfo.InvariantCulture);

    internal static string Column(int col)
    {
        string s = string.Empty;
        for (int c = col + 1; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
        return s;
    }

    private void WriteSheet(string name, SortedDictionary<int, RowData> rows, Dictionary<int, double> widths, List<string> merges)
    {
        _sheets.Add(name);
        int number = _sheets.Count;
        string part = $"xl/worksheets/sheet{number}.xml";
        _opc.AddRelationship(Workbook, "worksheet", $"worksheets/sheet{number}.xml");
        int maxRow = rows.Count > 0 ? rows.Keys.Max() : 0;
        int maxCol = Math.Max(rows.Values.Select(r => r.Cells.Count > 0 ? r.Cells.Keys.Max() : 0).DefaultIfEmpty(0).Max(), widths.Count > 0 ? widths.Keys.Max() : 0);
        _opc.WritePart(part, ContentBase + "worksheet+xml", x =>
        {
            x.WriteStartElement("worksheet", S);
            x.WriteAttributeString("xmlns", "r", null, OpcPackage.R);
            x.WriteStartElement("dimension", S);
            x.WriteAttributeString("ref", rows.Count == 0 ? "A1" : $"A1:{Ref(maxRow, maxCol)}");
            x.WriteEndElement();
            x.WriteStartElement("sheetViews", S);
            x.WriteStartElement("sheetView", S);
            if (number == 1) x.WriteAttributeString("tabSelected", "1");
            x.WriteAttributeString("workbookViewId", "0");
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("sheetFormatPr", S);
            x.WriteAttributeString("defaultRowHeight", "15");
            x.WriteEndElement();
            if (widths.Count > 0)
            {
                x.WriteStartElement("cols", S);
                foreach (var (col, points) in widths.OrderBy(w => w.Key))
                {
                    x.WriteStartElement("col", S);
                    string n = (col + 1).ToString(CultureInfo.InvariantCulture);
                    x.WriteAttributeString("min", n);
                    x.WriteAttributeString("max", n);
                    // Excel's width is in widths of the default font's digit, its padding included.
                    double chars = Math.Clamp(Math.Truncate(points * 96 / 72 / DigitPixels * 256) / 256, 0.5, 255);
                    x.WriteAttributeString("width", chars.ToString("0.########", CultureInfo.InvariantCulture));
                    x.WriteAttributeString("customWidth", "1");
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteStartElement("sheetData", S);
            foreach (var (r, data) in rows)
            {
                x.WriteStartElement("row", S);
                x.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
                if (data.Height is double ht)
                {
                    x.WriteAttributeString("ht", ht.ToString("0.##", CultureInfo.InvariantCulture));
                    x.WriteAttributeString("customHeight", "1");
                }
                foreach (var (c, cell) in data.Cells)
                {
                    x.WriteStartElement("c", S);
                    x.WriteAttributeString("r", Ref(r, c));
                    if (cell.Style != 0) x.WriteAttributeString("s", cell.Style.ToString(CultureInfo.InvariantCulture));
                    if (cell.Type == "s") x.WriteAttributeString("t", "s");
                    if (cell.Type.Length > 0) x.WriteElementString("v", S, cell.Value);
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteEndElement();
            if (merges.Count > 0)
            {
                x.WriteStartElement("mergeCells", S);
                x.WriteAttributeString("count", merges.Count.ToString(CultureInfo.InvariantCulture));
                foreach (string m in merges)
                {
                    x.WriteStartElement("mergeCell", S);
                    x.WriteAttributeString("ref", m);
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteStartElement("pageMargins", S);
            foreach (var (k, v) in new[] { ("left", "0.7"), ("right", "0.7"), ("top", "0.75"), ("bottom", "0.75"), ("header", "0.3"), ("footer", "0.3") })
                x.WriteAttributeString(k, v);
            x.WriteEndElement();
            x.WriteEndElement();
        });
    }

    /// <summary>Writes the workbook, its shared strings and styles, and the package's own parts.</summary>
    public void Finish(string? title)
    {
        if (_sheets.Count == 0) WriteSheet(UniqueName("Sheet1"), new SortedDictionary<int, RowData>(), new Dictionary<int, double>(), new List<string>());
        _opc.AddRelationship(Workbook, "styles", "styles.xml");
        _opc.AddRelationship(Workbook, "sharedStrings", "sharedStrings.xml");
        _opc.WritePart(Workbook, ContentBase + "sheet.main+xml", x =>
        {
            x.WriteStartElement("workbook", S);
            x.WriteAttributeString("xmlns", "r", null, OpcPackage.R);
            x.WriteStartElement("bookViews", S);
            x.WriteStartElement("workbookView", S);
            x.WriteEndElement();
            x.WriteEndElement();
            x.WriteStartElement("sheets", S);
            for (int i = 0; i < _sheets.Count; i++)
            {
                x.WriteStartElement("sheet", S);
                x.WriteAttributeString("name", _sheets[i]);
                x.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                // The sheets' relationships were made first, in order: rId1 is the first sheet's.
                x.WriteAttributeString("r", "id", OpcPackage.R, "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
                x.WriteEndElement();
            }
            x.WriteEndElement();
            x.WriteEndElement();
        });
        _opc.WritePart("xl/sharedStrings.xml", ContentBase + "sharedStrings+xml", SharedStrings);
        _opc.WritePart("xl/styles.xml", ContentBase + "styles+xml", Styles);
        _opc.Finish(title, Workbook);
    }

    private void SharedStrings(XmlWriter x)
    {
        x.WriteStartElement("sst", S);
        x.WriteAttributeString("count", _stringRefs.ToString(CultureInfo.InvariantCulture));
        x.WriteAttributeString("uniqueCount", _strings.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < _strings.Count; i++)
        {
            x.WriteStartElement("si", S);
            if (_rich[i] is { } runs)
                foreach (var run in runs)
                {
                    x.WriteStartElement("r", S);
                    x.WriteStartElement("rPr", S);
                    FontProperties(x, run.Font, run.Size, run.Bold, run.Italic, run.Underline, run.Strike, run.Color, rich: true);
                    x.WriteEndElement();
                    Text(x, run.Text);
                    x.WriteEndElement();
                }
            else Text(x, _strings[i]);
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private static void Text(XmlWriter x, string text)
    {
        x.WriteStartElement("t", S);
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n')))
            x.WriteAttributeString("xml", "space", null, "preserve");
        x.WriteString(CleanLines(text));
        x.WriteEndElement();
    }

    private static void FontProperties(XmlWriter x, string font, double size, bool bold, bool italic, bool underline, bool strike, string color, bool rich)
    {
        void Val(string name, string value)
        {
            x.WriteStartElement(name, S);
            x.WriteAttributeString("val", value);
            x.WriteEndElement();
        }
        if (rich) Val("rFont", FontNames(new DocRun { Font = font }));
        if (bold) x.WriteElementString("b", S, null);
        if (italic) x.WriteElementString("i", S, null);
        if (strike) x.WriteElementString("strike", S, null);
        if (!rich)
        {
            if (underline) x.WriteElementString("u", S, null);
            Val("sz", Math.Clamp(size, 1, 409).ToString("0.#", CultureInfo.InvariantCulture));
        }
        x.WriteStartElement("color", S);
        x.WriteAttributeString("rgb", "FF" + color);
        x.WriteEndElement();
        if (rich)
        {
            Val("sz", Math.Clamp(size, 1, 409).ToString("0.#", CultureInfo.InvariantCulture));
            if (underline) x.WriteElementString("u", S, null);
        }
        else Val("name", font);
    }

    private void Styles(XmlWriter x)
    {
        void Count(int n) => x.WriteAttributeString("count", n.ToString(CultureInfo.InvariantCulture));
        x.WriteStartElement("styleSheet", S);
        if (_numFmts.Count > 0)
        {
            x.WriteStartElement("numFmts", S);
            Count(_numFmts.Count);
            for (int i = 0; i < _numFmts.Count; i++)
            {
                x.WriteStartElement("numFmt", S);
                x.WriteAttributeString("numFmtId", (164 + i).ToString(CultureInfo.InvariantCulture));
                x.WriteAttributeString("formatCode", _numFmts[i]);
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }

        x.WriteStartElement("fonts", S);
        Count(_fonts.Count);
        foreach (string key in _fonts)
        {
            var p = key.Split('|');
            x.WriteStartElement("font", S);
            FontProperties(x, p[0], double.Parse(p[1], CultureInfo.InvariantCulture), p[2] == "1", p[3] == "1", p[4] == "1", p[5] == "1", p[6], rich: false);
            x.WriteEndElement();
        }
        x.WriteEndElement();

        x.WriteStartElement("fills", S);
        Count(_fills.Count);
        foreach (string fill in _fills)
        {
            x.WriteStartElement("fill", S);
            x.WriteStartElement("patternFill", S);
            if (fill is "none" or "gray125") x.WriteAttributeString("patternType", fill);
            else
            {
                x.WriteAttributeString("patternType", "solid");
                x.WriteStartElement("fgColor", S);
                x.WriteAttributeString("rgb", "FF" + fill);
                x.WriteEndElement();
                x.WriteStartElement("bgColor", S);
                x.WriteAttributeString("indexed", "64");
                x.WriteEndElement();
            }
            x.WriteEndElement();
            x.WriteEndElement();
        }
        x.WriteEndElement();

        x.WriteStartElement("borders", S);
        Count(_borders.Count);
        foreach (string border in _borders)
        {
            var edges = border.Split('|');
            x.WriteStartElement("border", S);
            string[] names = { "left", "right", "top", "bottom" };
            for (int i = 0; i < 4; i++)
            {
                x.WriteStartElement(names[i], S);
                if (edges[i].Length > 0)
                {
                    var e = edges[i].Split(':');
                    x.WriteAttributeString("style", e[0]);
                    x.WriteStartElement("color", S);
                    x.WriteAttributeString("rgb", "FF" + e[1]);
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteElementString("diagonal", S, null);
            x.WriteEndElement();
        }
        x.WriteEndElement();

        x.WriteStartElement("cellStyleXfs", S);
        Count(1);
        x.WriteStartElement("xf", S);
        foreach (var a in new[] { "numFmtId", "fontId", "fillId", "borderId" }) x.WriteAttributeString(a, "0");
        x.WriteEndElement();
        x.WriteEndElement();

        x.WriteStartElement("cellXfs", S);
        Count(_xfs.Count);
        foreach (var (numFmt, font, fill, border, h, v, wrap) in _xfs)
        {
            x.WriteStartElement("xf", S);
            x.WriteAttributeString("numFmtId", numFmt.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("fillId", fill.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("borderId", border.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("xfId", "0");
            if (numFmt != 0) x.WriteAttributeString("applyNumberFormat", "1");
            if (font != 0) x.WriteAttributeString("applyFont", "1");
            if (fill != 0) x.WriteAttributeString("applyFill", "1");
            if (border != 0) x.WriteAttributeString("applyBorder", "1");
            bool align = h.Length > 0 || v.Length > 0 && v != "bottom" || wrap;
            if (align)
            {
                x.WriteAttributeString("applyAlignment", "1");
                x.WriteStartElement("alignment", S);
                if (h.Length > 0) x.WriteAttributeString("horizontal", h);
                if (v.Length > 0 && v != "bottom") x.WriteAttributeString("vertical", v);
                if (wrap) x.WriteAttributeString("wrapText", "1");
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();

        x.WriteStartElement("cellStyles", S);
        Count(1);
        x.WriteStartElement("cellStyle", S);
        x.WriteAttributeString("name", "Normal");
        x.WriteAttributeString("xfId", "0");
        x.WriteAttributeString("builtinId", "0");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("dxfs", S);
        Count(0);
        x.WriteEndElement();
        x.WriteStartElement("tableStyles", S);
        Count(0);
        x.WriteAttributeString("defaultTableStyle", "TableStyleMedium2");
        x.WriteAttributeString("defaultPivotStyle", "PivotStyleLight16");
        x.WriteEndElement();
        x.WriteEndElement();
    }
}
