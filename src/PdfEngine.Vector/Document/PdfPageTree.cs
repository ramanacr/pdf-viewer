using System;
using System.Collections.Generic;
using System.IO;
using PdfEngine.Geometry;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Document;

/// <summary>
/// Traverses the PDF page tree (/Pages) according to ISO 32000-2 Clause 7.7.3,
/// resolving inherited attributes (MediaBox, CropBox, Resources, Rotate).
/// </summary>
public sealed class PdfPageTree
{
    private readonly PdfObjectResolver _resolver;
    private readonly PdfSecurityLimits _limits;
    private readonly List<PdfPageNode> _pages = new();

    public IReadOnlyList<PdfPageNode> Pages => _pages;
    public int Count => _pages.Count;

    public PdfPageTree(PdfObjectResolver resolver, PdfSecurityLimits? limits = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _limits = limits ?? PdfSecurityLimits.Default;
    }

    public void Load(PdfDictionary rootCatalog)
    {
        _pages.Clear();
        var pagesRef = rootCatalog["Pages"];
        var pagesNode = _resolver.Resolve(pagesRef);
        if (pagesNode is not PdfDictionary pagesDict)
            return;

        var visited = new HashSet<PdfDictionary>();
        CollectPages(pagesDict, PdfRect.Empty, PdfRect.Empty, 0, null, visited, depth: 0);
    }

    private void CollectPages(
        PdfDictionary node,
        PdfRect inheritedMediaBox,
        PdfRect inheritedCropBox,
        int inheritedRotate,
        PdfDictionary? inheritedResources,
        HashSet<PdfDictionary> visited,
        int depth)
    {
        if (depth > _limits.MaxPageTreeDepth)
            throw new InvalidDataException($"PDF Page tree recursion depth exceeded limit ({_limits.MaxPageTreeDepth}).");

        if (!visited.Add(node))
            return; // Cycle guard

        // Inherited boxes and attributes
        PdfRect mediaBox = ParseRect(node["MediaBox"]) ?? inheritedMediaBox;
        PdfRect cropBox = ParseRect(node["CropBox"]) ?? (inheritedCropBox.IsEmpty ? mediaBox : inheritedCropBox);
        int rotate = (int)(node.GetInteger("Rotate") ?? inheritedRotate);
        PdfDictionary mergedResources = MergeResources(inheritedResources, _resolver.Resolve(node["Resources"]) as PdfDictionary);

        string? type = node.GetName("Type");
        if (type == "Page" || (!node.ContainsKey("Kids") && node.ContainsKey("Contents")))
        {
            // Leaf Page Node
            int pageNum = _pages.Count + 1;
            var contentsList = ResolveContents(node["Contents"]);
            var page = new PdfPageNode(pageNum, node, mediaBox, cropBox, rotate, mergedResources, contentsList);
            _pages.Add(page);
        }
        else
        {
            // Intermediate Pages Node
            var kidsObj = _resolver.Resolve(node["Kids"]);
            if (kidsObj is PdfArray kidsArray)
            {
                foreach (var kidRef in kidsArray)
                {
                    var kidNode = _resolver.Resolve(kidRef);
                    if (kidNode is PdfDictionary kidDict)
                    {
                        CollectPages(kidDict, mediaBox, cropBox, rotate, mergedResources, visited, depth + 1);
                    }
                }
            }
        }
    }

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
