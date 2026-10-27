using System;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Geometry;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Document;

/// <summary>
/// Traverses the PDF page tree (/Pages) according to ISO 32000-2 Clause 7.7.3,
/// resolving inherited attributes (MediaBox, CropBox, Resources, Rotate).
/// <para>
/// A tree loaded <see cref="IsLazy">lazily</see> (a document read by ranges) reads only its root
/// at first: <see cref="Count"/> is the root's /Count, and each page is found when it is first
/// asked for, by descending from the root with the /Count of the nodes on the way. Only the
/// nodes on the path to that page are read.
/// </para>
/// </summary>
public sealed class PdfPageTree
{
    private readonly PdfObjectResolver _resolver;
    private readonly PdfSecurityLimits _limits;
    private readonly List<PdfPageNode> _pages = new();
    private readonly List<int> _pageObjectNumbers = new();
    private readonly List<int[]> _pageAncestors = new();

    // A lazy tree: the root, its /Count, and the pages found so far.
    private PdfDictionary? _root;
    private int _rootNumber = -1;
    private int _lazyCount;
    private PdfPageNode?[] _lazyPages = [];
    private int[] _lazyNumbers = [];
    private int[]?[] _lazyAncestors = [];
    private readonly object _lazySync = new();

    public IReadOnlyList<PdfPageNode> Pages { get; }
    public int Count => IsLazy ? _lazyCount : _pages.Count;

    /// <summary>The object number of each page's dictionary, or -1 for a page written as a direct object.</summary>
    public IReadOnlyList<int> PageObjectNumbers { get; }

    /// <summary>
    /// For each page, the object numbers of the /Pages nodes above it, root first (-1 for a node
    /// that is a direct object). Page-tree edits change the last one's /Kids and every /Count.
    /// </summary>
    public IReadOnlyList<int[]> PageAncestors { get; }

    /// <summary>
    /// Resolve each page's /Contents when it is first read instead of while loading (a document
    /// opened by ranges: opening then needs only the page dictionaries, not their content streams).
    /// </summary>
    internal bool DeferContents { get; init; }

    /// <summary>
    /// True when each page is found when it is first asked for (a document opened by ranges; see
    /// the class summary). Reading a page that the tree cannot place (a /Count that does not match
    /// the kids, a loop, a tree too deep) throws <see cref="PdfPageTreeMismatchException"/>, and the
    /// caller loads the tree whole instead. Bytes that are not loaded yet throw
    /// <see cref="PdfDataNotLoadedException"/>; nothing is kept from the failed read.
    /// </summary>
    public bool IsLazy { get; init; }

    /// <summary>
    /// For a lazy tree, the most pages the root's /Count may claim (the objects of the file: each
    /// page is one). A larger /Count is not trusted.
    /// </summary>
    internal int MaxLazyPages { get; init; } = int.MaxValue;

    /// <summary>How many kids after one whose bytes are missing are read in the same pass, so that their misses are fetched together.</summary>
    internal const int KidsReadAhead = 64;

    public PdfPageTree(PdfObjectResolver resolver, PdfSecurityLimits? limits = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _limits = limits ?? PdfSecurityLimits.Default;
        Pages = new PageList<PdfPageNode>(this, i => IsLazy ? Resolve(i) : _pages[i]);
        PageObjectNumbers = new PageList<int>(this, i => IsLazy ? NumberAt(i) : _pageObjectNumbers[i]);
        PageAncestors = new PageList<int[]>(this, i => IsLazy ? AncestorsAt(i) : _pageAncestors[i]);
    }

    public void Load(PdfDictionary rootCatalog)
    {
        _pages.Clear();
        _pageObjectNumbers.Clear();
        _pageAncestors.Clear();
        var pagesRef = rootCatalog["Pages"];
        var pagesNode = _resolver.Resolve(pagesRef);
        if (IsLazy)
        {
            LoadLazy(pagesNode as PdfDictionary, NumberOf(pagesRef));
            return;
        }
        if (pagesNode is not PdfDictionary pagesDict)
            return;

        var visited = new HashSet<PdfDictionary>();
        CollectPages(pagesDict, PdfRect.Empty, PdfRect.Empty, 0, null, visited, depth: 0, NumberOf(pagesRef), new List<int>());
    }

    /// <summary>
    /// The page at <paramref name="index"/> (0-based) if it is read already, without reading
    /// anything: always, for a tree that is not lazy; null for a page of a lazy tree not asked for yet.
    /// </summary>
    public PdfPageNode? LoadedPage(int index)
    {
        if (index < 0 || index >= Count) return null;
        if (!IsLazy) return _pages[index];
        lock (_lazySync) return _lazyPages[index];
    }

    /// <summary>The attributes a node passes down to its kids (ISO 32000-2 Table 31).</summary>
    private readonly record struct Inherited(PdfRect MediaBox, PdfRect CropBox, int Rotate, PdfDictionary? Resources);

    private Inherited Inherit(PdfDictionary node, Inherited parent)
    {
        PdfRect mediaBox = ParseRect(node["MediaBox"]) ?? parent.MediaBox;
        // Only an explicitly specified /CropBox is inherited; the default (the media box) is the
        // leaf's own media box, not an ancestor's.
        PdfRect specifiedCropBox = ParseRect(node["CropBox"]) ?? parent.CropBox;
        // /Rotate must be a multiple of 90 (ISO 32000-2 Table 31); snap anything else.
        int rotate = (int)(Math.Round((node.GetInteger("Rotate") ?? parent.Rotate) / 90.0) * 90) % 360;
        PdfDictionary mergedResources = MergeResources(parent.Resources, _resolver.Resolve(node["Resources"]) as PdfDictionary);
        return new Inherited(mediaBox, specifiedCropBox, rotate, mergedResources);
    }

    private static bool IsLeaf(PdfDictionary node) =>
        node.GetName("Type") == "Page" || (!node.ContainsKey("Kids") && node.ContainsKey("Contents"));

    private PdfPageNode MakePage(int pageNum, PdfDictionary node, Inherited a)
    {
        // Leaf Page Node. The crop box is clipped to the media box (ISO 32000-2 14.11.2) —
        // only here, once both boxes are final.
        PdfRect mediaBox = a.MediaBox;
        PdfRect cropBox = a.CropBox.IsEmpty ? mediaBox : a.CropBox;
        if (!mediaBox.IsEmpty && !cropBox.IsEmpty)
        {
            double x0 = Math.Max(mediaBox.Left, cropBox.Left), y0 = Math.Max(mediaBox.Top, cropBox.Top);
            double x1 = Math.Min(mediaBox.Right, cropBox.Right), y1 = Math.Min(mediaBox.Bottom, cropBox.Bottom);
            cropBox = x1 > x0 && y1 > y0 ? new PdfRect(x0, y0, x1 - x0, y1 - y0) : mediaBox;
        }
        var contentsObj = node["Contents"];
        return DeferContents
            ? new PdfPageNode(pageNum, node, mediaBox, cropBox, a.Rotate, a.Resources, () => ResolveContents(contentsObj))
            : new PdfPageNode(pageNum, node, mediaBox, cropBox, a.Rotate, a.Resources, ResolveContents(contentsObj));
    }

    private void CollectPages(
        PdfDictionary node,
        PdfRect inheritedMediaBox,
        PdfRect inheritedCropBox,
        int inheritedRotate,
        PdfDictionary? inheritedResources,
        HashSet<PdfDictionary> visited,
        int depth,
        int selfNumber,
        List<int> ancestors)
    {
        if (depth > _limits.MaxPageTreeDepth)
            throw new PdfResourceLimitException(nameof(PdfSecurityLimits.MaxPageTreeDepth), $"PDF Page tree recursion depth exceeded limit ({_limits.MaxPageTreeDepth}).");

        if (!visited.Add(node))
            return; // Cycle guard

        if (_pages.Count >= _limits.MaxObjectsCount)
            return;

        // Inherited boxes and attributes
        var a = Inherit(node, new Inherited(inheritedMediaBox, inheritedCropBox, inheritedRotate, inheritedResources));

        if (IsLeaf(node))
        {
            _pages.Add(MakePage(_pages.Count + 1, node, a));
            _pageObjectNumbers.Add(selfNumber);
            _pageAncestors.Add(ancestors.ToArray());
        }
        else
        {
            // Intermediate Pages Node
            var kidsObj = _resolver.Resolve(node["Kids"]);
            if (kidsObj is PdfArray kidsArray)
            {
                ancestors.Add(selfNumber);
                foreach (var kidRef in kidsArray)
                {
                    var kidNode = _resolver.Resolve(kidRef);
                    if (kidNode is PdfDictionary kidDict)
                    {
                        CollectPages(kidDict, a.MediaBox, a.CropBox, a.Rotate, a.Resources, visited, depth + 1, NumberOf(kidRef), ancestors);
                    }
                }
                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }
    }

    // ------------------------------------------------------------------ lazy tree

    private void LoadLazy(PdfDictionary? root, int rootNumber)
    {
        _root = root;
        _rootNumber = rootNumber;
        int count = 0;
        if (root != null)
        {
            if (IsLeaf(root))
            {
                count = 1;
            }
            else
            {
                // The root's /Count is checked before anything is sized by it: each page is an
                // object of the file, and a root has at least as many pages as kids (a kid without
                // pages is allowed by the standard, but such a tree is read whole).
                long? claimed = (_resolver.Resolve(root["Count"]) as PdfInteger)?.Value;
                int kids = (_resolver.Resolve(root["Kids"]) as PdfArray)?.Count ?? -1;
                if (claimed is not { } c || kids < 0 || c < kids || c > Math.Min(_limits.MaxObjectsCount, MaxLazyPages))
                    throw new PdfPageTreeMismatchException($"The page tree's /Count ({claimed?.ToString() ?? "none"}) does not fit its {kids} kids or the file.");
                count = (int)c;
            }
        }
        _lazyCount = count;
        _lazyPages = new PdfPageNode?[count];
        _lazyNumbers = new int[count];
        _lazyAncestors = new int[]?[count];
    }

    private int NumberAt(int index)
    {
        Resolve(index);
        lock (_lazySync) return _lazyNumbers[index];
    }

    private int[] AncestorsAt(int index)
    {
        Resolve(index);
        lock (_lazySync) return _lazyAncestors[index]!;
    }

    /// <summary>
    /// Finds page <paramref name="index"/> (0-based) from the root: at each node, the kid whose
    /// pages hold the index, by the kids' /Count. Each step is checked against the counts above
    /// it; a mismatch throws <see cref="PdfPageTreeMismatchException"/>. The descent is a loop
    /// bounded by <see cref="PdfSecurityLimits.MaxPageTreeDepth"/>, not a recursion.
    /// </summary>
    private PdfPageNode Resolve(int index)
    {
        if ((uint)index >= (uint)_lazyCount) throw new ArgumentOutOfRangeException(nameof(index));
        lock (_lazySync)
        {
            if (_lazyPages[index] is { } known) return known;
            var node = _root!;
            int number = _rootNumber, count = _lazyCount, local = index;
            var path = new List<int>();
            var onPath = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var a = default(Inherited);
            for (int depth = 0; ; depth++)
            {
                if (depth > _limits.MaxPageTreeDepth)
                    throw new PdfPageTreeMismatchException($"The page tree is deeper than {_limits.MaxPageTreeDepth} levels.");
                if (!onPath.Add(node))
                    throw new PdfPageTreeMismatchException("The page tree has a loop.");
                a = Inherit(node, a);
                if (IsLeaf(node))
                {
                    if (local != 0 || count != 1)
                        throw new PdfPageTreeMismatchException($"Page {index + 1} is not where the page tree's /Count places it.");
                    var page = MakePage(index + 1, node, a);
                    _lazyPages[index] = page;
                    _lazyNumbers[index] = number;
                    _lazyAncestors[index] = path.ToArray();
                    return page;
                }
                if (_resolver.Resolve(node["Kids"]) is not PdfArray kids)
                    throw new PdfPageTreeMismatchException("A /Pages node has no /Kids.");
                // The full walk reads a node once, wherever it is listed; the counts would not.
                if (ListsAKidTwice(kids))
                    throw new PdfPageTreeMismatchException("A /Pages node lists the same kid twice.");
                path.Add(number);
                (node, number, count, local) = FindKid(kids, local, count);
            }
        }
    }

    /// <summary>
    /// The kid of a node of <paramref name="count"/> pages that holds the node's page
    /// <paramref name="local"/>, with that page's index in the kid. When the node has as many kids
    /// as pages and the kid at that index is a page, it is taken directly; otherwise the kids are
    /// read in order, adding up their pages. When a kid's bytes are not loaded, up to
    /// <see cref="KidsReadAhead"/> kids after it are read too (their misses are recorded, so that
    /// the next round fetches them together), then the first miss is thrown.
    /// </summary>
    private (PdfDictionary Kid, int Number, int Count, int Local) FindKid(PdfArray kids, int local, int count)
    {
        if (kids.Count == count && _resolver.Resolve(kids[local]) is PdfDictionary direct && IsLeaf(direct))
            return (direct, NumberOf(kids[local]), 1, 0);

        // Every kid is read, not only those up to the page: their pages must add up to the node's
        // /Count, or a kid's wrong /Count would shift every page after it.
        long before = 0;
        (PdfDictionary Kid, int Number, int Count, int Local)? found = null;
        PdfDataNotLoadedException? missing = null;
        int readAhead = 0;
        for (int j = 0; j < kids.Count; j++)
        {
            PdfDictionary? kid;
            try
            {
                kid = _resolver.Resolve(kids[j]) as PdfDictionary;
            }
            catch (PdfDataNotLoadedException ex)
            {
                missing ??= ex;
                if (++readAhead > KidsReadAhead) break;
                continue;
            }
            if (missing != null)
            {
                if (++readAhead > KidsReadAhead) break;
                continue;
            }
            if (kid == null) continue; // not a node: the full walk skips it too
            int pages = PagesUnder(kid);
            if (found == null && local < before + pages)
                found = (kid, NumberOf(kids[j]), pages, (int)(local - before));
            before += pages;
            if (before > count)
                throw new PdfPageTreeMismatchException($"The kids of a /Pages node hold more pages than the {count} of its /Count.");
        }
        if (missing != null) throw missing;
        if (before != count || found is not { } kidFound)
            throw new PdfPageTreeMismatchException($"The kids of a /Pages node hold {before} pages, not the {count} of its /Count.");
        return kidFound;
    }

    // Whether a /Kids array names an object twice, by array (checked once per node: no bytes are read).
    private readonly Dictionary<PdfArray, bool> _duplicateKids = new(ReferenceEqualityComparer.Instance);

    private bool ListsAKidTwice(PdfArray kids)
    {
        if (_duplicateKids.TryGetValue(kids, out bool known)) return known;
        var seen = new HashSet<int>();
        bool twice = false;
        foreach (var kid in kids)
        {
            if (kid is PdfIndirectRef r && !seen.Add(r.ObjectNumber))
            {
                twice = true;
                break;
            }
        }
        _duplicateKids[kids] = twice;
        return twice;
    }

    /// <summary>The pages under a kid: 1 for a page, its /Count for a /Pages node (which must have one).</summary>
    private int PagesUnder(PdfDictionary kid)
    {
        if (IsLeaf(kid)) return 1;
        if (_resolver.Resolve(kid["Count"]) is not PdfInteger { Value: >= 0 and <= int.MaxValue } c)
            throw new PdfPageTreeMismatchException("A /Pages node has no valid /Count.");
        return (int)c.Value;
    }

    /// <summary>A read-only list of the tree's pages (for a lazy tree, an item is found when it is asked for).</summary>
    private sealed class PageList<T>(PdfPageTree tree, Func<int, T> item) : IReadOnlyList<T>
    {
        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)tree.Count) throw new ArgumentOutOfRangeException(nameof(index));
                return item(index);
            }
        }

        public int Count => tree.Count;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < tree.Count; i++) yield return item(i);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static int NumberOf(PdfObject? reference) => reference is PdfIndirectRef r ? r.ObjectNumber : -1;

    private IReadOnlyList<PdfStream> ResolveContents(PdfObject? contentsObj)
    {
        var list = new List<PdfStream>();
        var resolved = _resolver.Resolve(contentsObj);

        if (resolved is PdfStream stream)
        {
            list.Add(stream);
        }
        else if (resolved is PdfArray arr)
        {
            foreach (var item in arr)
            {
                var s = _resolver.Resolve(item);
                if (s is PdfStream str)
                {
                    list.Add(str);
                }
            }
        }

        return list;
    }

    private PdfRect? ParseRect(PdfObject? obj)
    {
        var resolved = _resolver.Resolve(obj);
        if (resolved is not PdfArray arr || arr.Count < 4)
            return null;

        double x1 = arr[0].TryGetNumber(out double n0) ? n0 : 0;
        double y1 = arr[1].TryGetNumber(out double n1) ? n1 : 0;
        double x2 = arr[2].TryGetNumber(out double n2) ? n2 : 0;
        double y2 = arr[3].TryGetNumber(out double n3) ? n3 : 0;

        double minX = Math.Min(x1, x2);
        double maxX = Math.Max(x1, x2);
        double minY = Math.Min(y1, y2);
        double maxY = Math.Max(y1, y2);

        return new PdfRect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    private static PdfDictionary MergeResources(PdfDictionary? parent, PdfDictionary? current)
    {
        if (parent == null && current == null)
            return new PdfDictionary(new Dictionary<string, PdfObject>());
        if (parent == null)
            return current!;
        if (current == null)
            return parent;

        var dict = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        foreach (var (k, v) in parent.Entries)
        {
            dict[k] = v;
        }
        foreach (var (k, v) in current.Entries)
        {
            dict[k] = v; // Child overrides parent
        }
        return new PdfDictionary(dict);
    }
}

/// <summary>
/// A lazy page tree (<see cref="PdfPageTree.IsLazy"/>) found that its /Count values do not match
/// its kids, a loop, or a tree too deep: the tree must be read whole instead.
/// </summary>
internal sealed class PdfPageTreeMismatchException(string message) : PdfVectorException(PdfVectorErrorKind.Syntax, message);
