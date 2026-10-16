using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Geometry;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Accessibility;

/// <summary>A structure element as the checks see it: its type, the standard type it maps to, and what it holds.</summary>
internal sealed class StructElem
{
    public required PdfDictionary Dict { get; init; }
    public int? ObjectNumber { get; init; }
    /// <summary>The type as written (/S).</summary>
    public required string Type { get; init; }
    /// <summary>The standard type it is or maps to, or null when the mapping does not end at one.</summary>
    public string? Std { get; set; }
    public StructElem? Parent { get; init; }
    public List<StructElem> Children { get; } = new();
    /// <summary>Marked content it holds: (page index, form XObject number or 0 for the page, MCID).</summary>
    public List<(int Page, int Stm, int Mcid)> Mcids { get; } = new();
    /// <summary>Objects (annotations) it holds by object reference.</summary>
    public List<int> Objects { get; } = new();
    /// <summary>The page its content is on (/Pg, or the nearest ancestor's), 0-based.</summary>
    public int? PageIndex { get; init; }
    public required string Path { get; init; }
    public string? Lang { get; init; }
    /// <summary>Its own /Lang or the nearest ancestor's (empty values do not count).</summary>
    public string? InheritedLang { get; init; }
    public Dictionary<string, int> ChildTypeCounts { get; } = new(StringComparer.Ordinal);
}

internal sealed partial class PdfUaChecker
{
    /// <summary>The standard structure types of ISO 32000-1 14.8.4, which PDF/UA-1 builds on.</summary>
    private static readonly HashSet<string> StandardTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    };

    /// <summary>PDF 1.7 types the PDF 2.0 standard structure namespace leaves out.</summary>
    private static readonly HashSet<string> NotInPdf20 = new(StringComparer.Ordinal) { "Art", "BlockQuote", "Note", "Private" };

    private PdfDictionary? _structRoot;
    private readonly Dictionary<string, string> _roleMap = new(StringComparer.Ordinal);
    private readonly List<StructElem> _elements = new();
    private readonly Dictionary<int, StructElem> _elementByObject = new();
    private readonly Dictionary<(int Page, int Stm, int Mcid), StructElem> _mcidOwner = new();
    private readonly Dictionary<int, StructElem> _objectOwner = new();
    private readonly Dictionary<long, PdfObject> _parentTree = new();
    private const int MaxElements = 200_000;

    // ------------------------------------------------------------------ reading the tree

    private void BuildStructureTree()
    {
        if (_catalog == null || _r.Resolve(_catalog["StructTreeRoot"]) is not PdfDictionary root) return;
        _structRoot = root;
        if (_r.Resolve(root["RoleMap"]) is PdfDictionary roleMap)
            foreach (var (key, value) in roleMap.Entries)
                if (_r.Resolve(value) is PdfName n) _roleMap[key] = n.Value;
        if (_r.Resolve(root["ParentTree"]) is PdfDictionary parentTree) ReadNumberTree(parentTree, 0);
        var visited = new HashSet<int>();
        var rootCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        VisitKids(root["K"], null, null, 0, visited, rootCounts);
        foreach (var e in _elements) e.Std = StandardOf(e.Type);
    }

    private void ReadNumberTree(PdfDictionary node, int depth)
    {
        if (depth > 32 || _parentTree.Count > 1_000_000) return;
        if (_r.Resolve(node["Nums"]) is PdfArray nums)
            for (int i = 0; i + 1 < nums.Count; i += 2)
                if (_r.Resolve(nums[i]) is { } k && k.TryGetInteger(out long key)) _parentTree[key] = nums[i + 1];
        if (_r.Resolve(node["Kids"]) is PdfArray kids)
            foreach (var kid in kids)
                if (_r.Resolve(kid) is PdfDictionary d) ReadNumberTree(d, depth + 1);
    }

    private void VisitKids(PdfObject? k, StructElem? parent, int? page, int depth, HashSet<int> visited, Dictionary<string, int> siblingCounts)
    {
        if (depth > 256 || _elements.Count > MaxElements) return;
        int? number = k is PdfIndirectRef r ? r.ObjectNumber : null;
        var v = _r.Resolve(k);
        switch (v)
        {
            case PdfArray a:
                foreach (var item in a) VisitKids(item, parent, page, depth + 1, visited, siblingCounts);
                break;
            case PdfInteger mcid when parent != null && page != null:
                AddMcid(parent, page.Value, 0, (int)mcid.Value);
                break;
            case PdfDictionary d when d.ContainsKey("S") && d.GetName("Type") is null or "StructElem":
                if (number is { } n && !visited.Add(n)) return;
                AddElement(d, number, parent, page, depth, visited, siblingCounts);
                break;
            case PdfDictionary d when parent != null && (d.GetName("Type") == "MCR" || d.ContainsKey("MCID")):
                int? p = d["Pg"] is PdfIndirectRef pg && _pageIndex.TryGetValue(pg.ObjectNumber, out int pi) ? pi : page;
                int stm = d["Stm"] is PdfIndirectRef s ? s.ObjectNumber : 0;
                if (p != null && _r.Resolve(d["MCID"]) is { } m && m.TryGetInteger(out long id)) AddMcid(parent, p.Value, stm, (int)id);
                break;
            case PdfDictionary d when parent != null && (d.GetName("Type") == "OBJR" || d.ContainsKey("Obj")):
                if (d["Obj"] is PdfIndirectRef obj)
                {
                    parent.Objects.Add(obj.ObjectNumber);
                    _objectOwner.TryAdd(obj.ObjectNumber, parent);
                }
                break;
        }
    }

    private void AddMcid(StructElem owner, int page, int stm, int mcid)
    {
        owner.Mcids.Add((page, stm, mcid));
        _mcidOwner.TryAdd((page, stm, mcid), owner);
    }

    private void AddElement(PdfDictionary d, int? number, StructElem? parent, int? inheritedPage, int depth, HashSet<int> visited, Dictionary<string, int> siblingCounts)
    {
        string type = d.GetName("S") ?? string.Empty;
        int? page = d["Pg"] is PdfIndirectRef pg && _pageIndex.TryGetValue(pg.ObjectNumber, out int pi) ? pi : inheritedPage;
        siblingCounts.TryGetValue(type, out int count);
        siblingCounts[type] = ++count;
        string label = (type.Length == 0 ? "(no type)" : Short(type)) + (count > 1 ? $"[{count}]" : string.Empty);
        string? lang = TextOf(d, "Lang");
        var elem = new StructElem
        {
            Dict = d, ObjectNumber = number, Type = type, Parent = parent, PageIndex = page,
            Path = parent == null ? label : parent.Path + " > " + label,
            Lang = lang, InheritedLang = string.IsNullOrEmpty(lang) ? parent?.InheritedLang : lang,
        };
        _elements.Add(elem);
        parent?.Children.Add(elem);
        if (number is { } n) _elementByObject.TryAdd(n, elem);
        VisitKids(d["K"], elem, page, depth + 1, visited, elem.ChildTypeCounts);
    }

    /// <summary>The standard type <paramref name="type"/> is or maps to through the role map, or null.</summary>
    private string? StandardOf(string type)
    {
        if (StandardTypes.Contains(type)) return type;
        var seen = new HashSet<string>(StringComparer.Ordinal) { type };
        string t = type;
        while (_roleMap.TryGetValue(t, out var next))
        {
            if (StandardTypes.Contains(next)) return next;
            if (!seen.Add(next)) return null;
            t = next;
        }
        return null;
    }

    /// <summary>The structure element marked content belongs to, by the tree or else by the parent tree.</summary>
    private StructElem? OwnerOf(int page, int stm, int mcid, int? structParents)
    {
        if (_mcidOwner.TryGetValue((page, stm, mcid), out var e)) return e;
        if (structParents is { } sp && _parentTree.TryGetValue(sp, out var entry) && _r.Resolve(entry) is PdfArray arr && mcid >= 0 && mcid < arr.Count
            && arr[mcid] is PdfIndirectRef r && _elementByObject.TryGetValue(r.ObjectNumber, out var owner))
            return owner;
        return null;
    }

    /// <summary>The structure element an annotation belongs to, by an object reference or its /StructParent.</summary>
    private StructElem? OwnerOfObject(int? number, PdfDictionary annot)
    {
        if (number is { } n && _objectOwner.TryGetValue(n, out var e)) return e;
        if (_r.Resolve(annot["StructParent"]) is { } sp && sp.TryGetInteger(out long key) && _parentTree.TryGetValue(key, out var entry)
            && entry is PdfIndirectRef r && _elementByObject.TryGetValue(r.ObjectNumber, out var owner))
            return owner;
        return null;
    }

    private readonly Dictionary<StructElem, PdfRect?> _boundsCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>The union of what the element and its descendants draw on <paramref name="page"/> (1-based), when known.</summary>
    private PdfRect? ElementBounds(StructElem elem, int? page)
    {
        if (page == null) return null;
        if (_boundsCache.TryGetValue(elem, out var cached)) return cached;
        PdfRect? result = null;
        var stack = new Stack<StructElem>();
        stack.Push(elem);
        int visited = 0;
        while (stack.Count > 0 && visited++ < 20_000)
        {
            var e = stack.Pop();
            foreach (var key in e.Mcids)
                if (key.Page == page - 1 && _mcidBounds.TryGetValue(key, out var b)) result = Union(result, b);
            foreach (var o in e.Objects)
                if (_annotRects.TryGetValue(o, out var a) && a.Page == page - 1) result = Union(result, a.Rect);
            foreach (var c in e.Children) stack.Push(c);
        }
        if (result == null && Attribute(elem, "Layout", "BBox") is PdfArray box && box.Count == 4)
        {
            double[] v = box.Items.Select(i => _r.Resolve(i) is { } x && x.TryGetNumber(out double n) ? n : 0).ToArray();
            result = new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1]));
        }
        _boundsCache[elem] = result;
        return result;
    }

    private static PdfRect Union(PdfRect? a, PdfRect b)
    {
        if (a is not { } r) return b;
        double x0 = Math.Min(r.X, b.X), y0 = Math.Min(r.Y, b.Y), x1 = Math.Max(r.X + r.Width, b.X + b.Width), y1 = Math.Max(r.Y + r.Height, b.Y + b.Height);
        return new PdfRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>An attribute of <paramref name="owner"/> (Table, Layout, ...) from /A or the classes in /C.</summary>
    private PdfObject? Attribute(StructElem e, string owner, string key)
    {
        PdfObject? Find(PdfObject? attrs)
        {
            IEnumerable<PdfObject?> items = _r.Resolve(attrs) is PdfArray a ? (IEnumerable<PdfObject?>)a.Items : new[] { attrs };
            foreach (var item in items)
                if (_r.Resolve(item) is PdfDictionary d && d.GetName("O") == owner && _r.Resolve(d[key]) is { } value) return value;
            return null;
        }
        if (Find(e.Dict["A"]) is { } direct) return direct;
        if (_r.Resolve(_structRoot?["ClassMap"]) is not PdfDictionary classMap) return null;
        var classes = _r.Resolve(e.Dict["C"]);
        IEnumerable<PdfObject?> names = classes is PdfArray ca ? (IEnumerable<PdfObject?>)ca.Items : new[] { classes };
        foreach (var c in names)
            if (_r.Resolve(c) is PdfName name && Find(classMap[name.Value]) is { } value) return value;
        return null;
    }

    private string? AltOf(StructElem e) => TextOf(e.Dict, "Alt");

    // ------------------------------------------------------------------ checks

    private void CheckStructure()
    {
        if (_structRoot == null) return;
        CheckRoleMap();
        bool pdf20 = _file.Length > 8 && _file[5] == (byte)'2';
        var noteIds = new Dictionary<string, StructElem>(StringComparer.Ordinal);
        foreach (var e in _elements)
        {
            string? alt = AltOf(e), actual = TextOf(e.Dict, "ActualText"), expansion = TextOf(e.Dict, "E");
            switch (e.Std)
            {
                case "Figure" when string.IsNullOrEmpty(alt) && actual == null:
                    Report("13-004", "7.3", PdfUaCategory.AlternateText, "A figure has no alternate text (Alt or ActualText).", elem: e);
                    break;
                case "Formula" when string.IsNullOrEmpty(alt) && actual == null:
                    Report("17-002", "7.7", PdfUaCategory.AlternateText, "A formula has no alternate text (Alt or ActualText).", elem: e);
                    break;
                case "Note":
                    string? id = TextOf(e.Dict, "ID");
                    if (string.IsNullOrEmpty(id)) Report("19-003", "7.9", PdfUaCategory.Structure, "A note has no ID.", elem: e);
                    else if (!noteIds.TryAdd(id, e)) Report("19-004", "7.9", PdfUaCategory.Structure, "A note has the same ID as another note.", elem: e);
                    break;
            }
            if (e.Lang != null && !PdfUaValidator.IsValidLanguageTag(e.Lang))
                Report("11-001", "7.2", PdfUaCategory.Structure, "A structure element's language (/Lang) is not a valid language tag.", elem: e);
            if ((alt != null || actual != null || expansion != null) && _documentLang == null && e.InheritedLang == null)
                Report("11-002", "7.2", PdfUaCategory.AlternateText, "The language of a structure element's alternate text, actual text or expansion cannot be determined.", elem: e);
            if (pdf20 && e.Std != null && NotInPdf20.Contains(e.Std))
                Report("01-006", "7.1", PdfUaCategory.Structure, $"The structure type {e.Std} is not part of PDF 2.0.", elem: e, severity: PdfUaSeverity.Warning);
            CheckNesting(e);
            if (e.Std == "Table") CheckTable(e);
        }
        CheckHeadings();
    }

    private void CheckRoleMap()
    {
        const PdfUaCategory cat = PdfUaCategory.Structure;
        var circular = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, _) in _roleMap)
        {
            if (StandardTypes.Contains(from))
            {
                Report("02-004", "7.1", cat, $"The standard structure type {from} is remapped in the role map.");
                continue;
            }
            // Follow the chain; coming back to a type already on it is a cycle.
            var chain = new List<string> { from };
            string t = from;
            while (_roleMap.TryGetValue(t, out var next) && !StandardTypes.Contains(next))
            {
                if (chain.Contains(next))
                {
                    if (chain.Skip(chain.IndexOf(next)).All(c => circular.Add(c)))
                        Report("02-003", "7.1", cat, $"The role map maps {Short(next)} back to itself through other types.");
                    break;
                }
                chain.Add(next);
                t = next;
            }
        }
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _elements)
            if (e.Std == null && !circular.Contains(e.Type) && reported.Add(e.Type))
                Report("02-001", "7.1", cat, e.Type.Length == 0 ? "A structure element has no type (/S)." : $"The structure type {Short(e.Type)} is not mapped to a standard type.", elem: e);
    }

    private static readonly HashSet<string> TableGroups = new(StringComparer.Ordinal) { "THead", "TBody", "TFoot" };

    /// <summary>Where table, list and table of contents elements may be (ISO 32000-1 14.8.4.3, veraPDF's PDF/UA-1 7.2 rules).</summary>
    private void CheckNesting(StructElem e)
    {
        string? parent = e.Parent?.Std;
        var kids = e.Children.Where(c => c.Std != null).Select(c => c.Std!).ToList();
        void Table(string message) => Report("09-004", "7.2", PdfUaCategory.Tables, message, elem: e);
        void List(string message) => Report("09-005", "7.2", PdfUaCategory.Lists, message, elem: e);
        void Toc(string message) => Report("09-006", "7.2", PdfUaCategory.Structure, message, elem: e);
        switch (e.Std)
        {
            case "Table":
                if (kids.Any(k => k is not ("TR" or "THead" or "TBody" or "TFoot" or "Caption"))) Table("A table holds elements other than TR, THead, TBody, TFoot and Caption.");
                int captions = kids.Count(k => k == "Caption");
                if (captions > 1) Table("A table has more than one caption.");
                else if (captions == 1 && kids[0] != "Caption" && kids[^1] != "Caption") Table("A table's caption is neither its first nor its last child.");
                if (kids.Count(k => k == "THead") > 1) Table("A table has more than one THead.");
                if (kids.Count(k => k == "TFoot") > 1) Table("A table has more than one TFoot.");
                if ((kids.Contains("THead") || kids.Contains("TFoot")) && !kids.Contains("TBody")) Table("A table has a THead or TFoot but no TBody.");
                break;
            case "TR":
                if (parent is not ("Table" or "THead" or "TBody" or "TFoot")) Table("A table row (TR) is not in a Table, THead, TBody or TFoot.");
                if (kids.Any(k => k is not ("TH" or "TD"))) Table("A table row holds elements other than TH and TD.");
                break;
            case "THead" or "TBody" or "TFoot":
                if (parent != "Table") Table($"A {e.Std} is not in a Table.");
                if (kids.Any(k => k != "TR")) Table($"A {e.Std} holds elements other than TR.");
                break;
            case "TH" or "TD":
                if (parent != "TR") Table($"A table cell ({e.Std}) is not in a table row (TR).");
                break;
            case "L":
                if (kids.Any(k => k is not ("LI" or "L" or "Caption"))) List("A list holds elements other than LI, L and Caption.");
                int listCaptions = kids.Count(k => k == "Caption");
                if (listCaptions > 1) List("A list has more than one caption.");
                else if (listCaptions == 1 && kids[0] != "Caption") List("A list's caption is not its first child.");
                break;
            case "LI":
                if (parent != "L") List("A list item (LI) is not in a list (L).");
                if (kids.Any(k => k is not ("Lbl" or "LBody"))) List("A list item holds elements other than Lbl and LBody.");
                break;
            case "LBody":
                if (parent != "LI") List("A list body (LBody) is not in a list item (LI).");
                break;
            case "TOCI":
                if (parent != "TOC") Toc("A table of contents item (TOCI) is not in a TOC.");
                break;
            case "TOC":
                if (kids.Any(k => k is not ("TOCI" or "TOC" or "Caption"))) Toc("A table of contents holds elements other than TOCI, TOC and Caption.");
                int tocCaptions = kids.Count(k => k == "Caption");
                if (tocCaptions > 1) Toc("A table of contents has more than one caption.");
                else if (tocCaptions == 1 && kids[0] != "Caption") Toc("A table of contents' caption is not its first child.");
                break;
        }
    }

    private sealed record Cell(StructElem Elem, int Row, int Col, int RowSpan, int ColSpan);

    /// <summary>
    /// Lays the table out on a grid (spans included): rows must cover the same columns and cells may
    /// not overlap (veraPDF 7.2-41 to 7.2-43), and every data cell must have a header it can be read
    /// with, by Scope or Headers (7.5).
    /// </summary>
    private void CheckTable(StructElem table)
    {
        var rows = new List<StructElem>();
        foreach (var c in table.Children)
        {
            if (c.Std == "TR") rows.Add(c);
            else if (c.Std != null && TableGroups.Contains(c.Std)) rows.AddRange(c.Children.Where(r => r.Std == "TR"));
        }
        if (rows.Count == 0) return;
        var taken = new Dictionary<(int, int), Cell>();
        var cells = new List<Cell>();
        bool spanReported = false, overlapReported = false;
        for (int r = 0; r < rows.Count; r++)
        {
            int col = 0;
            foreach (var cellElem in rows[r].Children.Where(c => c.Std is "TH" or "TD"))
            {
                int rs = Span(cellElem, "RowSpan"), cs = Span(cellElem, "ColSpan");
                while (taken.ContainsKey((r, col))) col++;
                if (r + rs > rows.Count)
                {
                    if (!spanReported) Report("09-004", "7.2", PdfUaCategory.Tables, "A table cell spans more rows than the table has.", elem: cellElem);
                    spanReported = true;
                    rs = rows.Count - r;
                }
                var cell = new Cell(cellElem, r, col, rs, cs);
                cells.Add(cell);
                for (int y = r; y < r + rs; y++)
                    for (int x = col; x < col + cs; x++)
                        if (!taken.TryAdd((y, x), cell) && !overlapReported)
                        {
                            Report("09-004", "7.2", PdfUaCategory.Tables, "Table cells overlap: a cell's row or column span runs into another cell.", elem: cellElem);
                            overlapReported = true;
                        }
                col += cs;
            }
        }
        var widths = Enumerable.Range(0, rows.Count).Select(r => taken.Keys.Count(k => k.Item1 == r)).Distinct().ToList();
        if (widths.Count > 1 && !overlapReported)
            Report("09-004", "7.2", PdfUaCategory.Tables, $"The table's rows cover different numbers of columns ({widths.Min()} to {widths.Max()}).", elem: table);

        var headers = cells.Where(c => c.Elem.Std == "TH").ToList();
        if (headers.Count == 0) return;
        string? Scope(Cell c) => (Attribute(c.Elem, "Table", "Scope") as PdfName)?.Value;
        int unconnected = 0;
        // An empty cell (the corner of a two-way table) has nothing to read with a header.
        foreach (var td in cells.Where(c => c.Elem.Std == "TD" && (c.Elem.Mcids.Count > 0 || c.Elem.Children.Count > 0 || c.Elem.Objects.Count > 0)))
        {
            // Headers count when they name an element that exists.
            if (Attribute(td.Elem, "Table", "Headers") is PdfArray ids && ids.Items.Select(i => Text(_r.Resolve(i))).Any(id => id != null && ElementIds.Contains(id))) continue;
            bool rowHeader = headers.Any(h => Scope(h) is "Row" or "Both" && h.Row < td.Row + td.RowSpan && td.Row < h.Row + h.RowSpan);
            bool colHeader = headers.Any(h => Scope(h) is "Column" or "Both" && h.Col < td.Col + td.ColSpan && td.Col < h.Col + h.ColSpan);
            if (rowHeader || colHeader) continue;
            unconnected++;
            Report("15-005", "7.5", PdfUaCategory.Tables, $"The header of the table cell in row {td.Row + 1}, column {td.Col + 1} cannot be determined: no header cell with a Scope covers it and it has no Headers.", elem: td.Elem);
        }
        int noScope = headers.Count(h => Scope(h) == null);
        if (unconnected > 0 && noScope > 0)
            Report("15-003", "7.5", PdfUaCategory.Tables, $"{noScope} header cell(s) of the table have no Scope, and the table does not use Headers and IDs throughout.", elem: table);
    }

    private HashSet<string>? _elementIds;

    /// <summary>The structure elements' IDs.</summary>
    private HashSet<string> ElementIds => _elementIds ??= _elements.Select(e => TextOf(e.Dict, "ID")).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToHashSet(StringComparer.Ordinal);

    private int Span(StructElem cell, string key) =>
        Attribute(cell, "Table", key) is { } v && v.TryGetInteger(out long n) && n >= 1 ? (int)Math.Min(n, 1000) : 1;

    private void CheckHeadings()
    {
        const PdfUaCategory cat = PdfUaCategory.Headings;
        var numbered = _elements.Where(e => e.Std is "H1" or "H2" or "H3" or "H4" or "H5" or "H6").ToList();
        if (numbered.Count > 0)
        {
            int Level(StructElem e) => e.Std![1] - '0';
            int previous = Level(numbered[0]);
            if (previous != 1) Report("14-002", "7.4.2", cat, $"The first heading is H{previous}, not H1.", elem: numbered[0]);
            foreach (var h in numbered.Skip(1))
            {
                int level = Level(h);
                if (level > previous + 1) Report("14-003", "7.4.2", cat, $"An H{level} follows an H{previous}: heading levels are skipped.", elem: h);
                previous = level;
            }
        }
        bool unnumbered = false;
        foreach (var e in _elements)
        {
            if (e.Std == "H") unnumbered = true;
            if (e.Children.Count(c => c.Std == "H") > 1) Report("14-006", "7.4.4", cat, "A structure element holds more than one H heading.", elem: e);
        }
        if (unnumbered && numbered.Count > 0)
            Report("14-007", "7.4.4", cat, "The document uses both H and numbered headings (H1 to H6).", elem: numbered[0]);
    }
}
