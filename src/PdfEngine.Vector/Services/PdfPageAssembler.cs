using System.Security.Cryptography;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>
/// One page of a document being assembled: page <see cref="PageNumber"/> of <see cref="Source"/>
/// turned a further <see cref="Rotation"/> degrees, or a blank page of <see cref="BlankSize"/>
/// when there is no source.
/// </summary>
public sealed record PdfAssemblyPage(PdfVectorDocument? Source, int PageNumber, int Rotation = 0)
{
    public PdfSize BlankSize { get; init; } = new(612, 792);

    public static PdfAssemblyPage Blank(double width, double height) => new(null, 0) { BlankSize = new PdfSize(width, height) };
}

public sealed class PdfAssemblyOptions
{
    /// <summary>Carry the sources' outlines, keeping the items that still lead somewhere.</summary>
    public bool KeepOutlines { get; init; } = true;

    /// <summary>Carry form fields whose widgets are on the pages taken (with the form's resources).</summary>
    public bool KeepForms { get; init; } = true;

    /// <summary>Write identical fonts, images and other resources once, across all sources.</summary>
    public bool Deduplicate { get; init; } = true;

    /// <summary>
    /// With more than one source: one top-level outline item per source, titled from this list
    /// (else the source's /Title, else "Document n"), holding that source's outline.
    /// </summary>
    public IReadOnlyList<string?>? SourceTitles { get; init; }

    /// <summary>The result's document information, in place of the first source's (Combine Files).</summary>
    public Create.PdfCreateInfo? Info { get; init; }
}

/// <summary>
/// Builds a new document from pages of existing ones (FPDF_ImportPages into FPDF_CreateNewDocument).
/// Each page takes what it uses - resources, annotations, form fields - copied once per source and
/// deduplicated across sources. Links and outline items keep working when the page they lead to
/// is in the result: named destinations are resolved into explicit ones there, so no name tree is
/// carried; those leading to pages left out are dropped. The structure tree, document JavaScript,
/// the open action and signature values (which could only be invalid in a new file) are not carried.
/// Sources must stay open until <see cref="Build"/> returns. Encrypted sources give an unencrypted result.
/// </summary>
public static class PdfPageAssembler
{
    public static byte[] Build(IReadOnlyList<PdfAssemblyPage> pages, PdfAssemblyOptions? options = null)
    {
        if (pages == null || pages.Count == 0)
            throw new ArgumentException("A document needs at least one page.", nameof(pages));
        options ??= new PdfAssemblyOptions();
        foreach (var p in pages)
        {
            if (p.Source != null && (p.PageNumber < 1 || p.PageNumber > p.Source.PageCount))
                throw new ArgumentOutOfRangeException(nameof(pages), $"Page {p.PageNumber} is outside the source's {p.Source.PageCount} pages.");
            if (p.Rotation % 90 != 0)
                throw new ArgumentException("Pages turn in quarter turns.", nameof(pages));
        }
        return new Assembly(pages, options).Run();
    }

    /// <summary>A new document of <paramref name="pageNumbers"/> of one source, in that order.</summary>
    public static byte[] Extract(PdfVectorDocument source, IEnumerable<int> pageNumbers, PdfAssemblyOptions? options = null) =>
        Build(pageNumbers.Select(n => new PdfAssemblyPage(source, n)).ToList(), options);

    /// <summary>Every page of every source, in order.</summary>
    public static byte[] Merge(IReadOnlyList<PdfVectorDocument> sources, PdfAssemblyOptions? options = null) =>
        Build(sources.SelectMany(s => Enumerable.Range(1, s.PageCount).Select(n => new PdfAssemblyPage(s, n))).ToList(), options);

    // ------------------------------------------------------------------ the assembly

    private sealed class Assembly
    {
        private readonly IReadOnlyList<PdfAssemblyPage> _pages;
        private readonly PdfAssemblyOptions _options;
        private readonly Dictionary<int, PdfObject> _objects = new();
        private readonly Dictionary<PdfVectorDocument, Copier> _copiers = new(ReferenceEqualityComparer.Instance);
        private readonly List<PdfVectorDocument> _sources = new();
        private readonly HashSet<int> _structural = new();
        private int _next = 1;

        public Assembly(IReadOnlyList<PdfAssemblyPage> pages, PdfAssemblyOptions options)
        {
            _pages = pages;
            _options = options;
        }

        public int Reserve() => _next++;
        public void Set(int number, PdfObject obj) => _objects[number] = obj;
        public int Add(PdfObject obj) { int n = Reserve(); Set(n, obj); return n; }
        public void MarkStructural(int number) => _structural.Add(number);

        public byte[] Run()
        {
            int catalog = Reserve();
            int pagesRoot = Reserve();
            MarkStructural(catalog);
            MarkStructural(pagesRoot);

            // Every page gets its number first, so references between pages resolve whichever is copied first.
            var dstPages = new int[_pages.Count];
            for (int i = 0; i < _pages.Count; i++)
            {
                dstPages[i] = Reserve();
                MarkStructural(dstPages[i]);
                var p = _pages[i];
                if (p.Source == null) continue;
                var copier = CopierFor(p.Source);
                int srcNumber = p.Source.PageTree.PageObjectNumbers[p.PageNumber - 1];
                copier.FirstOccurrence.TryAdd(p.PageNumber - 1, dstPages[i]);
                if (srcNumber > 0) copier.PageMap.TryAdd(srcNumber, dstPages[i]);
            }

            for (int i = 0; i < _pages.Count; i++)
            {
                var p = _pages[i];
                if (p.Source == null)
                {
                    Set(dstPages[i], new PdfDictionary(new Dictionary<string, PdfObject>
                    {
                        ["Type"] = new PdfName("Page"),
                        ["Parent"] = new PdfIndirectRef(pagesRoot),
                        ["MediaBox"] = PdfUpdate.Numbers(0, 0, p.BlankSize.Width, p.BlankSize.Height),
                        ["Resources"] = new PdfDictionary(new Dictionary<string, PdfObject>()),
                    }));
                    continue;
                }
                var copier = _copiers[p.Source];
                bool first = copier.FirstOccurrence[p.PageNumber - 1] == dstPages[i];
                Set(dstPages[i], copier.CopyPage(p.PageNumber, p.Rotation, dstPages[i], pagesRoot, first));
            }
            foreach (var copier in _copiers.Values) copier.Drain();

            Set(pagesRoot, new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Pages"),
                ["Kids"] = new PdfArray(dstPages.Select(n => (PdfObject)new PdfIndirectRef(n)).ToList()),
                ["Count"] = new PdfInteger(dstPages.Length),
            }));

            var catalogEntries = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Catalog"),
                ["Pages"] = new PdfIndirectRef(pagesRoot),
            };
            var primary = _pages.FirstOrDefault(p => p.Source != null)?.Source;
            if (primary != null)
            {
                var pc = _copiers[primary];
                foreach (var key in new[] { "PageLayout", "ViewerPreferences", "Lang" })
                    if (primary.Catalog[key] is { } v) catalogEntries[key] = pc.Copy(v);
                if (_sources.Count == 1 && primary.Catalog["Metadata"] is { } xmp) catalogEntries["Metadata"] = pc.Copy(xmp);
            }
            if (_options.KeepForms && BuildAcroForm() is { } form) catalogEntries["AcroForm"] = form;
            if (BuildOptionalContent() is { } oc) catalogEntries["OCProperties"] = oc;
            foreach (var copier in _copiers.Values) copier.Drain();
            if (_options.KeepOutlines && BuildOutlines(catalog, dstPages) is int outlines)
            {
                catalogEntries["Outlines"] = new PdfIndirectRef(outlines);
                if (primary?.Catalog.GetName("PageMode") is "UseOutlines") catalogEntries["PageMode"] = new PdfName("UseOutlines");
            }
            foreach (var copier in _copiers.Values) copier.Drain();
            Set(catalog, new PdfDictionary(catalogEntries));

            int? info = null;
            if (_options.Info is { } created)
            {
                string now = PdfTextStrings.FormatDate(DateTime.Now);
                var entries = new Dictionary<string, PdfObject>();
                foreach (var (key, value) in new[] { ("Title", created.Title), ("Author", created.Author), ("Subject", created.Subject),
                             ("Creator", created.Creator), ("Producer", (string?)created.Producer), ("CreationDate", now), ("ModDate", now) })
                    if (!string.IsNullOrWhiteSpace(value)) entries[key] = PdfObjectWriter.TextString(value);
                info = Add(new PdfDictionary(entries));
            }
            else if (primary?.XrefTable.Trailer?["Info"] is { } infoRef && primary.Resolver.Resolve(infoRef) is PdfDictionary infoDict)
            {
                info = Add(_copiers[primary].CopyValue(infoDict));
                _copiers[primary].Drain();
            }

            if (_options.Deduplicate) Deduplicate();

            string version = "1.4";
            foreach (var s in _sources)
            {
                version = PdfFullWriter.Max(s.HeaderVersion, version);
                version = PdfFullWriter.Max(s.Catalog.GetName("Version"), version);
            }
            return PdfFullWriter.Write(_objects, catalog, info, version);
        }

        private Copier CopierFor(PdfVectorDocument source)
        {
            if (!_copiers.TryGetValue(source, out var c))
            {
                c = new Copier(this, source, _options);
                _copiers[source] = c;
                _sources.Add(source);
            }
            return c;
        }

        // -------------------------------------------------------------- forms

        private PdfDictionary? BuildAcroForm()
        {
            var fields = new List<PdfObject>();
            Dictionary<string, PdfObject>? form = null;
            foreach (var source in _sources)
            {
                var c = _copiers[source];
                if (c.FieldRoots.Count == 0 || source.Resolver.Resolve(source.Catalog["AcroForm"]) is not PdfDictionary acro) continue;
                foreach (int root in c.FieldRoots) fields.Add(c.Copy(new PdfIndirectRef(root)));
                if (form == null)
                {
                    form = new Dictionary<string, PdfObject>();
                    foreach (var key in new[] { "DR", "DA", "Q", "NeedAppearances" })
                        if (acro[key] is { } v) form[key] = c.Copy(v);
                }
                else if (source.Resolver.Resolve(acro["DR"]) is PdfDictionary dr && form.TryGetValue("DR", out var existing) && Resolve(existing) is PdfDictionary have)
                {
                    // Resources of later sources fill the gaps; a name already present keeps the first source's.
                    var merged = new Dictionary<string, PdfObject>(have.Entries);
                    foreach (var (kind, value) in dr.Entries)
                    {
                        if (source.Resolver.Resolve(value) is not PdfDictionary sub) continue;
                        var into = Resolve(merged.GetValueOrDefault(kind)) is PdfDictionary h ? new Dictionary<string, PdfObject>(h.Entries) : new Dictionary<string, PdfObject>();
                        foreach (var (name, res) in sub.Entries) into.TryAdd(name, c.Copy(res));
                        merged[kind] = new PdfDictionary(into);
                    }
                    form["DR"] = new PdfDictionary(merged);
                }
            }
            if (form == null || fields.Count == 0) return null;
            form["Fields"] = new PdfArray(fields);
            return new PdfDictionary(form);
        }

        private PdfObject? Resolve(PdfObject? o) => o is PdfIndirectRef r ? _objects.GetValueOrDefault(r.ObjectNumber) : o;

        // -------------------------------------------------------------- optional content

        /// <summary>The sources' optional content groups together; the default configuration of the first, with every source's OFF groups.</summary>
        private PdfDictionary? BuildOptionalContent()
        {
            var ocgs = new List<PdfObject>();
            var off = new List<PdfObject>();
            var order = new List<PdfObject>();
            Dictionary<string, PdfObject>? config = null;
            foreach (var source in _sources)
            {
                var r = source.Resolver;
                if (r.Resolve(source.Catalog["OCProperties"]) is not PdfDictionary props) continue;
                var c = _copiers[source];
                if (r.Resolve(props["OCGs"]) is PdfArray groups) ocgs.AddRange(groups.Select(g => c.Copy(g)));
                if (r.Resolve(props["D"]) is PdfDictionary d)
                {
                    config ??= d.Entries.Where(e => e.Key is "Name" or "BaseState" or "ListMode" or "Intent").ToDictionary(e => e.Key, e => c.Copy(e.Value));
                    if (r.Resolve(d["OFF"]) is PdfArray o) off.AddRange(o.Select(g => c.Copy(g)));
                    if (r.Resolve(d["Order"]) is PdfArray ord) order.AddRange(ord.Select(g => c.Copy(g)));
                }
            }
            if (ocgs.Count == 0) return null;
            config ??= new Dictionary<string, PdfObject>();
            if (off.Count > 0) config["OFF"] = new PdfArray(off);
            if (order.Count > 0) config["Order"] = new PdfArray(order);
            return new PdfDictionary(new Dictionary<string, PdfObject> { ["OCGs"] = new PdfArray(ocgs), ["D"] = new PdfDictionary(config) });
        }

        // -------------------------------------------------------------- outlines

        private sealed record OutlineNode(string Title, PdfObject? Dest, PdfObject? Action, List<OutlineNode> Children, PdfObject? Color, int Style, bool Open);

        private int? BuildOutlines(int catalog, int[] dstPages)
        {
            var top = new List<OutlineNode>();
            bool wrap = _sources.Count > 1;
            for (int s = 0; s < _sources.Count; s++)
            {
                var source = _sources[s];
                var c = _copiers[source];
                var items = c.Outline();
                if (!wrap)
                {
                    top.AddRange(items);
                    continue;
                }
                int firstPage = Array.FindIndex(_pages.ToArray(), p => ReferenceEquals(p.Source, source));
                string? title = _options.SourceTitles is { } titles && s < titles.Count ? titles[s] : null;
                if (string.IsNullOrWhiteSpace(title)) title = PdfDocumentInfo.ReadProperties(source).Title;
                if (string.IsNullOrWhiteSpace(title)) title = $"Document {s + 1}";
                var dest = new PdfArray(new PdfObject[] { new PdfIndirectRef(dstPages[firstPage]), new PdfName("Fit") });
                top.Add(new OutlineNode(title, dest, null, items, null, 0, false));
            }
            if (top.Count == 0) return null;

            int root = Reserve();
            MarkStructural(root);
            var (first, last, count) = WriteLevel(top, root);
            Set(root, new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Outlines"),
                ["First"] = new PdfIndirectRef(first),
                ["Last"] = new PdfIndirectRef(last),
                ["Count"] = new PdfInteger(count),
            }));
            return root;
        }

        /// <summary>Writes one level; returns its first and last item and the visible descendants count (12.3.3).</summary>
        private (int First, int Last, int Count) WriteLevel(List<OutlineNode> nodes, int parent)
        {
            var numbers = nodes.Select(_ => Reserve()).ToArray();
            int visible = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                MarkStructural(numbers[i]);
                var e = new Dictionary<string, PdfObject>
                {
                    ["Title"] = PdfObjectWriter.TextString(n.Title),
                    ["Parent"] = new PdfIndirectRef(parent),
                };
                if (i > 0) e["Prev"] = new PdfIndirectRef(numbers[i - 1]);
                if (i + 1 < nodes.Count) e["Next"] = new PdfIndirectRef(numbers[i + 1]);
                if (n.Dest != null) e["Dest"] = n.Dest;
                else if (n.Action != null) e["A"] = n.Action;
                if (n.Color != null) e["C"] = n.Color;
                if (n.Style != 0) e["F"] = new PdfInteger(n.Style);
                if (n.Children.Count > 0)
                {
                    var (f, l, c) = WriteLevel(n.Children, numbers[i]);
                    e["First"] = new PdfIndirectRef(f);
                    e["Last"] = new PdfIndirectRef(l);
                    e["Count"] = new PdfInteger(n.Open ? c : -c);
                    if (n.Open) visible += c;
                }
                Set(numbers[i], new PdfDictionary(e));
                visible++;
            }
            return (numbers[0], numbers[^1], visible);
        }

        // -------------------------------------------------------------- deduplication

        /// <summary>
        /// Objects with the same content and the same (deduplicated) references are written once.
        /// Pages, annotations, fields, outline items and anything in a reference cycle are left alone.
        /// </summary>
        private void Deduplicate()
        {
            var keys = new Dictionary<int, string>();
            var onStack = new HashSet<int>();
            var canonical = new Dictionary<string, int>(StringComparer.Ordinal);
            var replace = new Dictionary<int, int>();

            string Key(int n)
            {
                if (keys.TryGetValue(n, out var k)) return k;
                // A cycle, or a chain too deep to follow here: identity only.
                if (onStack.Count > 256 || !onStack.Add(n)) return "#" + n;
                var obj = _objects.GetValueOrDefault(n);
                string key;
                if (obj == null || _structural.Contains(n) || IsStructural(obj))
                {
                    key = "#" + n; // its references get their own keys from the outer loop
                }
                else
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    Serialize(obj, hash);
                    key = Convert.ToHexString(hash.GetHashAndReset());
                }
                onStack.Remove(n);
                keys[n] = key;
                if (!key.StartsWith('#'))
                {
                    if (canonical.TryGetValue(key, out int keep)) replace[n] = keep;
                    else canonical[key] = n;
                }
                return key;
            }

            void Serialize(PdfObject o, IncrementalHash h)
            {
                switch (o)
                {
                    case PdfIndirectRef r:
                        h.AppendData(System.Text.Encoding.ASCII.GetBytes("R(" + Key(r.ObjectNumber) + ")"));
                        break;
                    case PdfArray a:
                        h.AppendData("["u8);
                        foreach (var item in a) { Serialize(item, h); h.AppendData(" "u8); }
                        h.AppendData("]"u8);
                        break;
                    case PdfDictionary d:
                        h.AppendData("<<"u8);
                        foreach (var (k, v) in d.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                        {
                            h.AppendData(System.Text.Encoding.Latin1.GetBytes("/" + k + " "));
                            Serialize(v, h);
                        }
                        h.AppendData(">>"u8);
                        break;
                    case PdfStream s:
                        Serialize(s.Dictionary, h);
                        h.AppendData(s.Decrypted ? "stream*"u8 : "stream"u8);
                        h.AppendData(s.GetRawBytes().Span);
                        break;
                    default:
                        using (var ms = new MemoryStream())
                        {
                            PdfObjectWriter.Write(ms, o);
                            h.AppendData(ms.ToArray());
                        }
                        break;
                }
            }

            foreach (int n in _objects.Keys.OrderBy(k => k).ToList()) Key(n);
            if (replace.Count == 0) return;

            int Final(int n) { while (replace.TryGetValue(n, out int m)) n = m; return n; }
            PdfObject Remap(PdfObject o) => o switch
            {
                PdfIndirectRef r when replace.ContainsKey(r.ObjectNumber) => new PdfIndirectRef(Final(r.ObjectNumber)),
                PdfArray a => new PdfArray(a.Items.Select(Remap).ToList()),
                PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(kv => kv.Key, kv => Remap(kv.Value))),
                PdfStream s => s with { Dictionary = (PdfDictionary)Remap(s.Dictionary) },
                _ => o,
            };
            foreach (int n in _objects.Keys.ToList())
            {
                if (replace.ContainsKey(n)) { _objects.Remove(n); continue; }
                _objects[n] = Remap(_objects[n]);
            }
        }

        private static bool IsStructural(PdfObject obj)
        {
            var d = obj as PdfDictionary ?? (obj as PdfStream)?.Dictionary;
            if (d == null) return false;
            if (d.GetName("Type") is "Page" or "Pages" or "Annot" or "Catalog" or "Outlines" or "Sig") return true;
            return d.ContainsKey("Rect") || d.ContainsKey("Parent") || d.ContainsKey("FT") || d.ContainsKey("Kids") || d.ContainsKey("IRT");
        }

        // -------------------------------------------------------------- copying one source

        private sealed class Copier
        {
            private readonly Assembly _a;
            private readonly PdfVectorDocument _doc;
            private readonly PdfObjectResolver _r;
            private readonly PdfAssemblyOptions _options;
            private readonly PdfNavigation _nav;
            private readonly Dictionary<int, int> _map = new();
            private readonly Queue<(int Source, int Target)> _pending = new();
            private readonly HashSet<int> _pageObjects;
            private readonly HashSet<int> _treeNodes = new();
            private readonly HashSet<int> _fieldAncestors = new();
            private readonly HashSet<int> _keptWidgets = new();
            private readonly int _structTreeRoot;

            /// <summary>Source page object → the output page it became (the first occurrence when repeated).</summary>
            public Dictionary<int, int> PageMap { get; } = new();

            /// <summary>Source page index → output page object of its first occurrence.</summary>
            public Dictionary<int, int> FirstOccurrence { get; } = new();

            /// <summary>Top-level fields reached from widgets on the pages taken.</summary>
            public List<int> FieldRoots { get; } = new();

            public Copier(Assembly assembly, PdfVectorDocument doc, PdfAssemblyOptions options)
            {
                _a = assembly;
                _doc = doc;
                _r = doc.Resolver;
                _options = options;
                _nav = new PdfNavigation(doc);
                _pageObjects = doc.PageTree.PageObjectNumbers.Where(n => n > 0).ToHashSet();
                foreach (var ancestors in doc.PageTree.PageAncestors)
                    foreach (int n in ancestors) if (n > 0) _treeNodes.Add(n);
                _structTreeRoot = doc.Catalog["StructTreeRoot"] is PdfIndirectRef st ? st.ObjectNumber : -1;
            }

            /// <summary>A reference or direct value, copied into the output (references are copied once and queued).</summary>
            public PdfObject Copy(PdfObject value)
            {
                switch (value)
                {
                    case PdfIndirectRef r:
                    {
                        int n = r.ObjectNumber;
                        if (_pageObjects.Contains(n)) return PageMap.TryGetValue(n, out int page) ? new PdfIndirectRef(page) : PdfNull.Instance;
                        if (_treeNodes.Contains(n) || n == _structTreeRoot) return PdfNull.Instance;
                        if (_map.TryGetValue(n, out int done)) return new PdfIndirectRef(done);
                        if (_r.Resolve(n) == null) return PdfNull.Instance;
                        int target = _a.Reserve();
                        _map[n] = target;
                        _pending.Enqueue((n, target));
                        return new PdfIndirectRef(target);
                    }
                    case PdfArray arr:
                        return new PdfArray(arr.Items.Select(Copy).ToList());
                    case PdfDictionary d:
                        return CopyDictionary(d);
                    case PdfStream s:
                        return s with { Dictionary = CopyDictionary(s.Dictionary) };
                    default:
                        return value;
                }
            }

            public PdfObject CopyValue(PdfObject value) => Copy(value);

            private PdfDictionary CopyDictionary(PdfDictionary d, int self = 0)
            {
                var e = new Dictionary<string, PdfObject>(d.Count);
                bool field = self > 0 && _fieldAncestors.Contains(self);
                bool signature = field && d.GetName("FT") == "Sig";
                foreach (var (key, value) in d.Entries)
                {
                    if (key is "StructParent" or "StructParents") continue; // no structure tree in the result
                    if (signature && key is "V" or "Lock" or "SV") continue; // a signature value cannot be valid in a new file
                    if (field && key == "Kids" && _r.Resolve(value) is PdfArray kids)
                    {
                        // Only the branches that lead to widgets on the pages taken.
                        e[key] = new PdfArray(kids.Where(k => k is PdfIndirectRef kr && (_fieldAncestors.Contains(kr.ObjectNumber) || _keptWidgets.Contains(kr.ObjectNumber)))
                            .Select(Copy).ToList());
                        continue;
                    }
                    e[key] = Copy(value);
                }
                return new PdfDictionary(e);
            }

            /// <summary>Copies everything queued by <see cref="Copy"/>, iteratively (long chains do not recurse).</summary>
            public void Drain()
            {
                while (_pending.Count > 0)
                {
                    var (source, target) = _pending.Dequeue();
                    var obj = _r.Resolve(source);
                    _a.Set(target, obj switch
                    {
                        PdfDictionary d => CopyDictionary(d, source),
                        PdfStream s => s with { Dictionary = CopyDictionary(s.Dictionary, source) },
                        null => PdfNull.Instance,
                        _ => Copy(obj),
                    });
                    if (obj is PdfDictionary dict && IsStructural(dict)) _a.MarkStructural(target);
                }
            }

            public PdfDictionary CopyPage(int pageNumber, int rotation, int self, int parent, bool firstOccurrence)
            {
                var node = _doc.PageTree.Pages[pageNumber - 1];
                var src = node.Dictionary;
                var e = new Dictionary<string, PdfObject>();
                foreach (var (key, value) in src.Entries)
                {
                    if (key is "Parent" or "Annots" or "B" or "StructParents" or "Resources" or "MediaBox" or "CropBox" or "Rotate") continue;
                    e[key] = Copy(value);
                }
                e["Type"] = new PdfName("Page");
                e["Parent"] = new PdfIndirectRef(parent);
                e["MediaBox"] = PdfUpdate.Numbers(node.MediaBox.Left, node.MediaBox.Top, node.MediaBox.Right, node.MediaBox.Bottom);
                if (PdfDocumentInfo.Inherited(src, "CropBox", _r) != null)
                    e["CropBox"] = PdfUpdate.Numbers(node.CropBox.Left, node.CropBox.Top, node.CropBox.Right, node.CropBox.Bottom);
                // The raw value, so resources shared between pages stay shared.
                e["Resources"] = PdfDocumentInfo.Inherited(src, "Resources", _r) is { } res ? Copy(res) : new PdfDictionary(new Dictionary<string, PdfObject>());
                int rotate = (((node.RotationDegrees + rotation) % 360) + 360) % 360;
                if (rotate != 0) e["Rotate"] = new PdfInteger(rotate);

                if (_r.Resolve(src["Annots"]) is PdfArray annots && CopyAnnotations(annots, self, firstOccurrence) is { Count: > 0 } copied)
                    e["Annots"] = new PdfArray(copied);
                return new PdfDictionary(e);
            }

            /// <summary>
            /// The page's annotations: links whose target page is not taken are dropped, and named
            /// destinations become explicit. A repeated page gets its own copies (and no form widgets).
            /// </summary>
            private List<PdfObject> CopyAnnotations(PdfArray annots, int page, bool firstOccurrence)
            {
                var kept = new List<PdfObject>();
                var keptSources = new HashSet<int>();
                var popups = new List<(PdfObject Item, int Parent)>();
                foreach (var item in annots)
                {
                    if (_r.Resolve(item) is not PdfDictionary d) continue;
                    int n = item is PdfIndirectRef ir ? ir.ObjectNumber : 0;
                    string? subtype = d.GetName("Subtype");
                    if (subtype == "Popup")
                    {
                        popups.Add((item, d["Parent"] is PdfIndirectRef pr ? pr.ObjectNumber : 0));
                        continue;
                    }
                    if (subtype == "Widget")
                    {
                        if (!_options.KeepForms || !firstOccurrence || n == 0) continue;
                        NoteWidget(n, d);
                    }
                    if (subtype == "Link")
                    {
                        if (RetargetLink(d) is not { } link) continue;
                        kept.Add(Place(n, link, page, firstOccurrence));
                    }
                    else
                    {
                        kept.Add(firstOccurrence ? Copy(item) : new PdfIndirectRef(_a.Add(WithPage(CopyDictionary(d), page))));
                    }
                    if (n > 0) keptSources.Add(n);
                }
                foreach (var (item, parent) in popups)
                {
                    if (parent > 0 && !keptSources.Contains(parent)) continue;
                    if (firstOccurrence) kept.Add(Copy(item));
                }
                return kept;
            }

            private PdfObject Place(int n, PdfDictionary copied, int page, bool firstOccurrence)
            {
                copied = WithPage(copied, page);
                if (firstOccurrence && n > 0 && !_map.ContainsKey(n))
                {
                    int target = _a.Reserve();
                    _map[n] = target;
                    _a.Set(target, copied);
                    _a.MarkStructural(target);
                    return new PdfIndirectRef(target);
                }
                return new PdfIndirectRef(_a.Add(copied));
            }

            private static PdfDictionary WithPage(PdfDictionary d, int page) =>
                d.ContainsKey("P") ? PdfUpdate.With(d, ("P", new PdfIndirectRef(page))) : d;

            /// <summary>
            /// The link copied into the output with an explicit destination there; unchanged when it
            /// goes elsewhere; null when the page it leads to is not taken.
            /// </summary>
            private PdfDictionary? RetargetLink(PdfDictionary link)
            {
                if (link["Dest"] != null)
                {
                    var dest = _nav.ResolveDestination(link["Dest"]);
                    var explicitDest = dest != null ? Explicit(dest) : null;
                    return explicitDest == null ? null : PdfUpdate.With(CopyDictionary(PdfUpdate.With(link, ("Dest", null))), ("Dest", explicitDest));
                }
                if (_r.Resolve(link["A"]) is PdfDictionary action && action.GetName("S") == "GoTo")
                {
                    var dest = _nav.ResolveDestination(action["D"]);
                    var explicitDest = dest != null ? Explicit(dest) : null;
                    if (explicitDest == null) return null;
                    var copiedAction = PdfUpdate.With(CopyDictionary(PdfUpdate.With(action, ("D", null), ("Next", null))), ("D", explicitDest));
                    return PdfUpdate.With(CopyDictionary(PdfUpdate.With(link, ("A", null))), ("A", copiedAction));
                }
                return CopyDictionary(link);
            }

            /// <summary>[page /Fit ...] on the output page the destination's page became, or null when that page is not taken.</summary>
            public PdfArray? Explicit(PdfDestination dest)
            {
                if (dest.PageIndex < 0 || !FirstOccurrence.TryGetValue(dest.PageIndex, out int page)) return null;
                var items = new List<PdfObject> { new PdfIndirectRef(page) };
                if (dest.Fit == PdfDestinationFit.Unknown)
                {
                    items.Add(new PdfName("XYZ"));
                    items.AddRange(new PdfObject[] { PdfNull.Instance, PdfNull.Instance, PdfNull.Instance });
                }
                else
                {
                    items.Add(new PdfName(dest.Fit.ToString()));
                    items.AddRange(dest.Parameters.Select(p => p is double v ? (PdfObject)new PdfReal(v) : PdfNull.Instance));
                }
                return new PdfArray(items);
            }

            private void NoteWidget(int widget, PdfDictionary d)
            {
                _keptWidgets.Add(widget);
                int current = widget;
                var parent = d["Parent"];
                var seen = new HashSet<int> { widget };
                for (int depth = 0; depth < 32 && parent is PdfIndirectRef pr && seen.Add(pr.ObjectNumber); depth++)
                {
                    _fieldAncestors.Add(pr.ObjectNumber);
                    current = pr.ObjectNumber;
                    parent = (_r.Resolve(pr) as PdfDictionary)?["Parent"];
                }
                if (!FieldRoots.Contains(current)) FieldRoots.Add(current);
                _fieldAncestors.Add(widget); // a widget merged with its field is its own root
            }

            // ---------------------------------------------------------- outline

            public List<OutlineNode> Outline()
            {
                var result = new List<OutlineNode>();
                if (_r.Resolve(_doc.Catalog["Outlines"]) is not PdfDictionary root) return result;
                var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { root };
                return Level(root["First"], 0, visited);
            }

            private List<OutlineNode> Level(PdfObject? first, int depth, HashSet<PdfDictionary> visited)
            {
                var nodes = new List<OutlineNode>();
                if (depth > 64) return nodes;
                for (var current = first; current != null;)
                {
                    if (_r.Resolve(current) is not PdfDictionary item || !visited.Add(item)) break;
                    var children = Level(item["First"], depth + 1, visited);
                    PdfObject? dest = null, action = null;
                    bool leads = false;
                    if (item["Dest"] != null)
                    {
                        dest = _nav.ResolveDestination(item["Dest"]) is { } d ? Explicit(d) : null;
                        leads = dest != null;
                    }
                    else if (_r.Resolve(item["A"]) is PdfDictionary a)
                    {
                        if (a.GetName("S") == "GoTo")
                        {
                            dest = _nav.ResolveDestination(a["D"]) is { } d ? Explicit(d) : null;
                            leads = dest != null;
                        }
                        else if (a.GetName("S") is "URI" or "GoToR" or "Launch" or "Named" or "GoToE")
                        {
                            action = Copy(PdfUpdate.With(a, ("Next", null)));
                            leads = true;
                        }
                    }
                    if (leads || children.Count > 0)
                    {
                        string title = PdfTextStrings.Decode(_r.Resolve(item["Title"])) ?? string.Empty;
                        bool open = _r.Resolve(item["Count"]) is { } c && c.TryGetInteger(out long count) && count > 0;
                        int style = _r.Resolve(item["F"]) is { } f && f.TryGetInteger(out long fl) ? (int)(fl & 3) : 0;
                        var colour = _r.Resolve(item["C"]) is PdfArray { Count: 3 } col ? Copy(col) : null;
                        nodes.Add(new OutlineNode(title, dest, action, children, colour, style, open));
                    }
                    current = item["Next"];
                }
                return nodes;
            }
        }
    }
}
