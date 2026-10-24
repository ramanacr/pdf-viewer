using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.PdfA.Xmp;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Services;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Accessibility;

/// <summary>Marked content, the structure tree, the catalog and the metadata, written as a complete copy.</summary>
internal sealed partial class AutoTagRun
{
    private readonly TagNode _document = new("Document", -1);
    private string? _title;
    private int _headings, _paragraphs, _figures, _links, _otherAnnots;

    public byte[] Write()
    {
        foreach (var page in _pages)
        {
            page.Top.RemoveAll(n => !Keep(n));
            foreach (var n in page.Top) _document.Add(n);
        }
        AssignHeadingLevels();
        foreach (var page in _pages)
        {
            _ct.ThrowIfCancellationRequested();
            Emit(page);
        }
        Measure(_document);
        return WriteObjects();
    }

    /// <summary>Whether an element has content (operators or an annotation) to hold, after its empty children are dropped.</summary>
    private static bool Keep(TagNode n)
    {
        n.Children.RemoveAll(c => !Keep(c));
        return n.Type switch
        {
            "TR" or "TD" or "TH" => true, // a table keeps its grid; empty cells are cells
            "Table" => HasContent(n),
            "LI" => n.Children.Any(c => c.Type == "LBody" || c.Type == "Lbl"),
            _ => n.Used > 0 || n.Children.Count > 0,
        };
    }

    private static bool HasContent(TagNode n) => n.Used > 0 || n.Children.Any(HasContent);

    /// <summary>
    /// Heading levels by size class (larger first, bold before regular at the same size), then
    /// in reading order no level is skipped and the first heading is H1.
    /// </summary>
    private void AssignHeadingLevels()
    {
        var headings = _document.Children.Where(n => n.Type == "H").ToList();
        var classes = headings.Select(h => h.HeadingClass!.Value).Distinct().OrderByDescending(c => c.Size).ThenByDescending(c => c.Bold).ToList();
        int previous = 0;
        foreach (var h in headings)
        {
            int rank = classes.IndexOf(h.HeadingClass!.Value) + 1;
            int level = Math.Min(Math.Min(rank, previous + 1), 6);
            h.Type = "H" + level;
            previous = level;
            _title ??= h.HeadingText is { Length: > 0 } t ? (t.Length > 200 ? t[..200] : t) : null;
        }
    }

    private sealed class Group
    {
        public required object Tag;
        public bool Text;
        public int Depth, First, Last, KeyOp;
    }

    /// <summary>
    /// Writes the page's content again with marked content around each run of operators that is
    /// part of one element (with an MCID) or of the page's furniture (as an artifact). Sequences
    /// never cross the document's own marked content, a text object's bounds or a Q that ends the
    /// state they began in; the document's own MCIDs (from earlier tags) are taken out.
    /// </summary>
    private void Emit(PageModel page)
    {
        var ops = page.Ops;
        var edits = new List<AutoTagContent.Edit>();
        Group? open = null;
        int floor = -1; // operators up to here are behind a barrier or in a closed sequence
        void Close()
        {
            if (open == null) return;
            var first = ops[open.First];
            var last = ops[open.Last];
            if (open.Tag is TagNode node)
            {
                int mcid = page.ParentArray.Count;
                edits.Add(new AutoTagContent.Edit(first.Start, 2, $"\n/{node.Type} <</MCID {mcid}>>BDC\n"));
                node.Mcids.Add((mcid, page.OpKey.TryGetValue(open.KeyOp, out var key) ? key : ReadKey.Last));
                page.ParentArray.Add(node);
            }
            else
            {
                edits.Add(new AutoTagContent.Edit(first.Start, 2, "\n" + ((ArtifactTag)open.Tag).Open + "\n"));
                page.ArtifactSequences++;
            }
            edits.Add(new AutoTagContent.Edit(last.End, 0, "\nEMC\n"));
            floor = Math.Max(floor, open.Last);
            open = null;
        }

        // Where each sequence the document opens ends.
        var ends = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == ContentOpKind.BeginMarked) stack.Push(i);
            else if (ops[i].Kind == ContentOpKind.EndMarked && stack.Count > 0) ends[stack.Pop()] = i;
        }

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            // A sequence with /ActualText is read as its text only when it is the innermost
            // sequence with properties: ours goes around it, whole, never inside it.
            if (op.Kind == ContentOpKind.BeginMarked && !op.InArtifact && op.Tag != "Artifact" && op.Properties?.ContainsKey("ActualText") == true
                && ends.TryGetValue(i, out int spanEnd) && SpanOwner(page, i, spanEnd) is { } spanTag)
            {
                if (AutoTagContent.WithoutMcid(op) is { } spanReplaced) edits.Add(new AutoTagContent.Edit(op.Start, 1, spanReplaced, op.End));
                for (int k = i + 1; k < spanEnd; k++)
                    if (ops[k].Kind == ContentOpKind.BeginMarked && AutoTagContent.WithoutMcid(ops[k]) is { } inner)
                        edits.Add(new AutoTagContent.Edit(ops[k].Start, 1, inner, ops[k].End));
                if (open != null && ReferenceEquals(open.Tag, spanTag) && open.Text && op.InText == ops[open.Last].InText)
                    open.Last = spanEnd;
                else
                {
                    Close();
                    open = new Group { Tag = spanTag, Text = true, Depth = op.Depth, First = i, Last = spanEnd, KeyOp = FirstOwned(page, i, spanEnd) };
                }
                i = spanEnd;
                continue;
            }
            switch (op.Kind)
            {
                case ContentOpKind.BeginMarked:
                    Close();
                    if (AutoTagContent.WithoutMcid(op) is { } replaced) edits.Add(new AutoTagContent.Edit(op.Start, 1, replaced, op.End));
                    floor = i;
                    continue;
                case ContentOpKind.EndMarked or ContentOpKind.BeginText or ContentOpKind.EndText:
                    Close();
                    floor = i;
                    continue;
                case ContentOpKind.Restore:
                    if (open != null && op.Depth <= open.Depth) Close();
                    floor = Math.Max(floor, open == null ? i : floor);
                    continue;
            }
            if (!page.Owner.TryGetValue(i, out var tag)) continue;
            bool text = op.Kind == ContentOpKind.Text;
            int start = Math.Max(op.Kind == ContentOpKind.PathPaint ? op.PathStart : i, Math.Max(floor, open?.Last ?? -1) + 1);
            if (start > i) start = i;
            if (tag is InheritTag)
            {
                if (open != null && open.Text && text) { open.Last = i; continue; }
                tag = ArtifactTag.Plain;
            }
            if (open != null && ReferenceEquals(open.Tag, tag) && open.Text == text)
            {
                open.Last = i;
                continue;
            }
            Close();
            open = new Group { Tag = tag, Text = text, Depth = ops[start].Depth, First = start, Last = i, KeyOp = i };
        }
        Close();
        if (edits.Count > 0) page.Tagged = AutoTagContent.Apply(page.Content, edits);
    }

    /// <summary>What a sequence with /ActualText is part of: its first content's element or artifact (text without a line of its own: an artifact).</summary>
    private static object? SpanOwner(PageModel page, int from, int to)
    {
        bool any = false;
        for (int k = from + 1; k < to; k++)
        {
            if (!page.Owner.TryGetValue(k, out var owner)) continue;
            if (owner is TagNode or ArtifactTag) return owner;
            any = true;
        }
        return any ? ArtifactTag.Plain : null;
    }

    private static int FirstOwned(PageModel page, int from, int to)
    {
        for (int k = from + 1; k < to; k++)
            if (page.Owner.TryGetValue(k, out var owner) && owner is TagNode) return k;
        return from;
    }

    /// <summary>Each element's place in reading order: the first of its content's.</summary>
    private static ReadKey Measure(TagNode n)
    {
        var key = ReadKey.Last;
        foreach (var (_, k) in n.Mcids) if (k.CompareTo(key) < 0) key = k;
        foreach (var c in n.Children)
        {
            var ck = Measure(c);
            if (ck.CompareTo(key) < 0) key = ck;
        }
        n.Key = key;
        return key;
    }

    // ------------------------------------------------------------------ objects

    private static PdfIndirectRef Ref(int n) => new(n);
    private static PdfName Name(string s) => new(s);

    private byte[] WriteObjects()
    {
        var trailer = _doc.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (trailer["Root"] is not PdfIndirectRef rootRef) throw new InvalidOperationException("The document's catalog is not an indirect object.");
        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(_doc);
        int Alloc() => next++;
        var pageObjects = PdfRedactor.PageObjectNumbers(_doc.Catalog, _r);

        // Pages: their new content, their place in the parent tree, their annotations.
        int parentKey = _pages.Count;
        var annotKeys = new List<(int Key, TagNode Node)>();
        for (int i = 0; i < _pages.Count && i < pageObjects.Count; i++)
        {
            var page = _pages[i];
            var entries = new Dictionary<string, PdfObject>(page.Node.Dictionary.Entries);
            bool changed = false;
            if (page.Tagged != null)
            {
                int content = Alloc();
                objects[content] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = Name("FlateDecode") }, ContentRedactor.Deflate(page.Tagged));
                entries["Contents"] = Ref(content);
                changed = true;
            }
            if (entries.Remove("StructParents")) changed = true;
            if (page.ParentArray.Count > 0)
            {
                entries["StructParents"] = new PdfInteger(i);
                changed = true;
            }
            if (page.Annots.Count > 0 && _r.Resolve(page.Node.Dictionary["Annots"]) is PdfArray annots)
            {
                var items = annots.Items.ToList();
                foreach (var (index, number, dict, node, contents) in page.Annots)
                {
                    var a = new Dictionary<string, PdfObject>(dict.Entries);
                    bool annotChanged = a.Remove("StructParent");
                    if (node != null)
                    {
                        a["StructParent"] = new PdfInteger(parentKey);
                        annotKeys.Add((parentKey++, node));
                        if (contents != null) a["Contents"] = PdfObjectWriter.TextString(contents);
                        annotChanged = true;
                    }
                    if (!annotChanged) continue;
                    int target = number;
                    if (target < 0)
                    {
                        // An object reference needs an object: a direct annotation becomes one.
                        target = Alloc();
                        items[index] = Ref(target);
                        entries["Annots"] = new PdfArray(items);
                    }
                    if (node != null) node.Annot = target;
                    objects[target] = new PdfDictionary(a);
                }
                if (page.Annots.Any(x => x.Node != null)) entries["Tabs"] = Name("S");
                changed = true;
            }
            if (changed) objects[pageObjects[i]] = new PdfDictionary(entries);
        }
        StripFormMcids(objects, Alloc);

        // The structure tree.
        int root = Alloc(), parentTree = Alloc();
        Number(_document, Alloc);
        WriteElement(_document, root, pageObjects, objects);
        var nums = new List<PdfObject>();
        foreach (var page in _pages)
        {
            if (page.ParentArray.Count == 0) continue;
            nums.Add(new PdfInteger(page.Index));
            nums.Add(new PdfArray(page.ParentArray.Select(n => (PdfObject)Ref(n!.Number)).ToList()));
        }
        foreach (var (key, node) in annotKeys)
        {
            nums.Add(new PdfInteger(key));
            nums.Add(Ref(node.Number));
        }
        objects[parentTree] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Nums"] = new PdfArray(nums) });
        objects[root] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = Name("StructTreeRoot"), ["K"] = Ref(_document.Number), ["ParentTree"] = Ref(parentTree), ["ParentTreeNextKey"] = new PdfInteger(parentKey),
        });

        // The catalog: tagged, its language, its title shown, its metadata.
        var catalog = new Dictionary<string, PdfObject>(_doc.Catalog.Entries);
        catalog["MarkInfo"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Marked"] = PdfBoolean.True });
        catalog["StructTreeRoot"] = Ref(root);
        catalog["Lang"] = PdfObjectWriter.TextString(Language);
        var prefs = _r.Resolve(catalog.GetValueOrDefault("ViewerPreferences")) is PdfDictionary vp ? new Dictionary<string, PdfObject>(vp.Entries) : new();
        prefs["DisplayDocTitle"] = PdfBoolean.True;
        if (catalog.GetValueOrDefault("ViewerPreferences") is PdfIndirectRef vpRef) objects[vpRef.ObjectNumber] = new PdfDictionary(prefs);
        else catalog["ViewerPreferences"] = new PdfDictionary(prefs);

        string? infoTitle = _r.Resolve(trailer["Info"]) is PdfDictionary info ? Text(_r.Resolve(info["Title"]))?.Trim() : null;
        if (string.IsNullOrEmpty(infoTitle)) infoTitle = null;
        var xmp = PdfAValidator.ReadMetadata(_doc);
        var properties = xmp is { Error: null } ? new List<XmpProperty>(xmp.Properties) : new List<XmpProperty>();
        bool xmpChanged = xmp is not { Error: null };
        string? xmpTitle = properties.FirstOrDefault(p => p.Namespace == XmpSchemas.Dc && p.Name == "title") is { } tp
            ? (tp.Value.Kind == XmpKind.Simple ? tp.Value.Text : tp.Value.Items.Select(v => v.Text).FirstOrDefault(t => t.Trim().Length > 0))?.Trim()
            : null;
        string? title = infoTitle ?? (string.IsNullOrEmpty(xmpTitle) ? null : xmpTitle) ?? _title;
        if (string.IsNullOrEmpty(xmpTitle) && title != null)
        {
            properties.RemoveAll(p => p.Namespace == XmpSchemas.Dc && p.Name == "title");
            properties.Add(new XmpProperty(XmpSchemas.Dc, "dc", "title", XmpWriter.LangAlt(title)));
            xmpChanged = true;
        }
        if (_options.ClaimPdfUa)
        {
            properties.RemoveAll(p => p.Namespace == PdfUaChecker.PdfUaIdNs);
            properties.Add(new XmpProperty(PdfUaChecker.PdfUaIdNs, "pdfuaid", "part", XmpWriter.Simple("1")));
            xmpChanged = true;
        }
        if (xmpChanged)
        {
            var metadata = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Type"] = Name("Metadata"), ["Subtype"] = Name("XML") }, XmpWriter.Write(properties));
            if (catalog.GetValueOrDefault("Metadata") is PdfIndirectRef mRef) objects[mRef.ObjectNumber] = metadata;
            else
            {
                int m = Alloc();
                objects[m] = metadata;
                catalog["Metadata"] = Ref(m);
            }
        }
        if (infoTitle == null && title != null && trailer["Info"] is PdfIndirectRef infoRef && _r.Resolve(infoRef) is PdfDictionary infoDict)
            objects[infoRef.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(infoDict.Entries) { ["Title"] = PdfObjectWriter.TextString(title) });
        objects[rootRef.ObjectNumber] = new PdfDictionary(catalog);
        return Services.PdfFullWriter.Rewrite(_doc, objects);
    }

    private static void Number(TagNode n, Func<int> alloc)
    {
        n.Number = alloc();
        foreach (var c in n.Children) Number(c, alloc);
    }

    private void WriteElement(TagNode n, int parent, List<int> pageObjects, Dictionary<int, PdfObject> objects)
    {
        var entries = new Dictionary<string, PdfObject> { ["Type"] = Name("StructElem"), ["S"] = Name(n.Type), ["P"] = Ref(parent) };
        bool onPage = n.Page >= 0 && n.Page < pageObjects.Count;
        if (onPage) entries["Pg"] = Ref(pageObjects[n.Page]);
        var kids = new List<(ReadKey Key, int Order, PdfObject Kid)>();
        foreach (var (mcid, key) in n.Mcids) kids.Add((key, kids.Count, new PdfInteger(mcid)));
        foreach (var c in n.Children) kids.Add((c.Key, kids.Count, Ref(c.Number)));
        if (n.Mcids.Count > 0) kids = kids.OrderBy(k => k.Key).ThenBy(k => k.Order).ToList();
        if (n.Annot is int annot and >= 0)
        {
            var objr = new Dictionary<string, PdfObject> { ["Type"] = Name("OBJR"), ["Obj"] = Ref(annot) };
            if (onPage) objr["Pg"] = Ref(pageObjects[n.Page]);
            kids.Add((ReadKey.Last, kids.Count, new PdfDictionary(objr)));
        }
        if (kids.Count > 0) entries["K"] = new PdfArray(kids.Select(k => k.Kid).ToList());
        if (n.Type == "Figure" && n.Alt != null) entries["Alt"] = PdfObjectWriter.TextString(n.Alt);
        if (n.Attributes != null) entries["A"] = n.Attributes;
        objects[n.Number] = new PdfDictionary(entries);
        foreach (var c in n.Children) WriteElement(c, n.Number, pageObjects, objects);
        Count(n);
    }

    private void Count(TagNode n)
    {
        switch (n.Type)
        {
            case "P": _paragraphs++; break;
            case "Figure":
                _figures++;
                if (n.Bounds is { } b && (_options.FigureAltPlaceholder == null || n.Alt == _options.FigureAltPlaceholder)) FiguresNeedingAlt.Add(new PdfAutoTagFigure(n.Page + 1, b));
                break;
            case "Link": _links++; break;
            case "Annot" or "Form": _otherAnnots++; break;
            default:
                if (n.Type.Length == 2 && n.Type[0] == 'H' && char.IsDigit(n.Type[1])) _headings++;
                break;
        }
    }

    /// <summary>
    /// Forms the pages draw that carry MCIDs of earlier tags: written again without them (their
    /// content is tagged where the page draws the form), so no marked content is left unowned.
    /// </summary>
    private void StripFormMcids(Dictionary<int, PdfObject> objects, Func<int> alloc)
    {
        var decoder = new PdfStreamDecoder(null, _r.Resolve);
        var seen = new HashSet<int>();
        void Visit(PdfDictionary? resources, int depth)
        {
            if (depth > 12 || _r.Resolve(resources?["XObject"]) is not PdfDictionary xobjects) return;
            foreach (var value in xobjects.Entries.Values)
            {
                if (value is not PdfIndirectRef reference || !seen.Add(reference.ObjectNumber)) continue;
                if (_r.Resolve(reference) is not PdfStream form || form.Dictionary.GetName("Subtype") != "Form") continue;
                var own = _r.Resolve(form.Dictionary["Resources"]) as PdfDictionary;
                Visit(own, depth + 1);
                byte[] data;
                try { data = decoder.DecodeStream(form); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                if (!Encoding.Latin1.GetString(data).Contains("/MCID", StringComparison.Ordinal) && !form.Dictionary.ContainsKey("StructParents")) continue;
                List<ContentOp> ops;
                try { ops = AutoTagContent.Scan(data, own ?? resources, _r); }
                catch (AutoTagContentException) { continue; }
                var edits = ops.Select(o => (Op: o, Text: AutoTagContent.WithoutMcid(o))).Where(x => x.Text != null)
                    .Select(x => new AutoTagContent.Edit(x.Op.Start, 1, x.Text!, x.Op.End)).ToList();
                var entries = new Dictionary<string, PdfObject>(form.Dictionary.Entries);
                entries.Remove("StructParents");
                entries.Remove("StructParent");
                if (edits.Count == 0)
                {
                    objects[reference.ObjectNumber] = form with { Dictionary = new PdfDictionary(entries) };
                    continue;
                }
                foreach (var key in new[] { "Length", "Filter", "DecodeParms", "DL" }) entries.Remove(key);
                entries["Filter"] = Name("FlateDecode");
                objects[reference.ObjectNumber] = PdfObjectWriter.NewStream(entries, ContentRedactor.Deflate(AutoTagContent.Apply(data, edits)));
            }
        }
        foreach (var page in _pages) Visit(page.Node.Resources, 0);
    }

    // ------------------------------------------------------------------ what was done

    public IReadOnlyList<PdfAutoTagElement> Elements() =>
        _document.Children.Where(n => n.Bounds != null).Select(n => new PdfAutoTagElement(n.Page + 1, n.Type, n.Bounds!.Value)).ToList();

    public PdfAutoTagSummary Summary() => new()
    {
        Pages = _pages.Count,
        Headings = _headings,
        Paragraphs = _paragraphs,
        Lists = _pages.Sum(p => p.Lists),
        ListItems = _pages.Sum(p => p.ListItems),
        Tables = _pages.Sum(p => p.Tables),
        TablesWithHeaders = _pages.Sum(p => p.TablesWithHeaders),
        Figures = _figures,
        Links = _links,
        OtherAnnotations = _otherAnnots,
        HeadersAndFooters = _pages.Sum(p => p.HeadersAndFooters),
        ArtifactSequences = _pages.Sum(p => p.ArtifactSequences),
        MultiColumnPages = _pages.Count(p => p.MultiColumn),
        RotatedPages = _pages.Count(p => p.Node.RotationDegrees != 0),
    };

    /// <summary>The checks a person should make (Matterhorn's human checks the tags make relevant).</summary>
    public IReadOnlyList<string> Review(IReadOnlyList<PdfAutoTagRemainingIssue> remaining)
    {
        var s = Summary();
        var review = new List<string>();
        if (FiguresNeedingAlt.Count > 0)
            review.Add(_options.FigureAltPlaceholder != null
                ? $"{FiguresNeedingAlt.Count} figure(s) have placeholder alternate text: describe each one, or mark it as decorative."
                : $"{FiguresNeedingAlt.Count} figure(s) have no alternate text: describe each one, or mark it as decorative.");
        review.Add(s.MultiColumnPages > 0
            ? $"Check the reading order, especially on the {s.MultiColumnPages} page(s) read as columns."
            : "Check the reading order.");
        if (s.Headings > 0 || s.Lists > 0)
            review.Add("Check the headings (found from font size and weight) and the lists (found from bullets and numbers).");
        if (s.Tables > 0)
            review.Add(s.TablesWithHeaders < s.Tables
                ? $"Check the {s.Tables} table(s); {s.Tables - s.TablesWithHeaders} of them have no header row found: mark their header cells."
                : $"Check the {s.Tables} table(s) and their header cells.");
        if (_title == null && remaining.Any(r => r.Checkpoint == "06-003"))
            review.Add("The document has no title: give it one in the document properties.");
        if (remaining.Any(r => r.Checkpoint is "28-012" or "28-004" or "28-005"))
            review.Add("Describe the links, annotations and form fields that have no description.");
        if (!_options.ClaimPdfUa)
            review.Add("After the review, the file can claim PDF/UA conformance (its metadata does not yet).");
        return review;
    }
}
