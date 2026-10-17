using System;
using System.Collections.Generic;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Text;

/// <summary>A structure element of a tagged PDF as one page sees it.</summary>
public sealed class PdfStructElement
{
    /// <summary>The structure type (/S) as written, before any role map.</summary>
    public string Type { get; init; } = string.Empty;
    public string? Lang { get; init; }
    public string? ActualText { get; init; }
    public string? Alt { get; init; }

    /// <summary>The marked-content identifiers the element's /K lists (an integer, an MCR's MCID; -1 for other kids).</summary>
    public List<int> MarkedContentIds { get; } = new();

    /// <summary>One entry per kid in /K: the element when it is a structure element on this page, else null.</summary>
    public List<PdfStructElement?> Children { get; } = new();

    internal PdfDictionary? Dict;
}

/// <summary>
/// The structure tree of one page (ISO 32000-2 14.7), built as PDFium builds it for
/// FPDF_StructTree_GetForPage: the page's /StructParents entry in the parent tree names the
/// elements that own its marked content; those and their ancestors up to the root are the tree,
/// with every /K entry kept in place (null where it is not an element of this page).
/// </summary>
public static class PdfStructureTree
{
    private const int MaxRecursion = 32;

    /// <summary>The root's kids for the page (null entries for kids not on the page); null when the document is not tagged or the page has no structure.</summary>
    public static IReadOnlyList<PdfStructElement?>? ForPage(PdfVectorDocument document, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (pageNumber < 1 || pageNumber > document.PageCount) return null;
        var r = document.Resolver;
        document.BuildGate.Wait();
        try
        {
            var catalog = document.Catalog;
            if (r.Resolve(catalog?["StructTreeRoot"]) is not PdfDictionary root) return null;
            var kidsObj = r.Resolve(root["K"]);
            int count = kidsObj is PdfDictionary ? 1 : kidsObj is PdfArray arr ? arr.Count : -1;
            if (count < 0) return null;
            var kids = new PdfStructElement?[count];
            var page = document.PageTree.Pages[pageNumber - 1].Dictionary;
            if (r.Resolve(root["ParentTree"]) is not PdfDictionary parentTree) return kids;
            if (page["StructParents"] is not { } sp || !sp.TryGetNumber(out double spv) || spv < 0) return kids;
            if (r.Resolve(LookupNumberTree(r, parentTree, (int)spv, 0)) is not PdfArray parents) return kids;
            var map = new Dictionary<PdfDictionary, PdfStructElement>(ReferenceEqualityComparer.Instance);
            foreach (var item in parents.Items)
                if (r.Resolve(item) is PdfDictionary parent) AddPageNode(r, root, kidsObj, kids, page, parent, map, 0);
            return kids;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or FormatException)
        {
            return null;
        }
        finally
        {
            document.BuildGate.Release();
        }
    }

    private static PdfObject? LookupNumberTree(PdfObjectResolver r, PdfDictionary node, int key, int depth)
    {
        if (depth > 32) return null;
        if (r.Resolve(node["Nums"]) is PdfArray nums)
        {
            for (int i = 0; i + 1 < nums.Count; i += 2)
                if (r.Resolve(nums.Items[i]) is { } k && k.TryGetNumber(out double kv) && (int)kv == key) return nums.Items[i + 1];
            return null;
        }
        if (r.Resolve(node["Kids"]) is PdfArray kidsArr)
        {
            foreach (var kid in kidsArr.Items)
            {
                if (r.Resolve(kid) is not PdfDictionary kd) continue;
                if (r.Resolve(kd["Limits"]) is PdfArray { Count: >= 2 } limits && limits.Items[0].TryGetNumber(out double lo) && limits.Items[1].TryGetNumber(out double hi)
                    && (key < lo || key > hi)) continue;
                if (LookupNumberTree(r, kd, key, depth + 1) is { } found) return found;
            }
        }
        return null;
    }

    private static PdfStructElement? AddPageNode(PdfObjectResolver r, PdfDictionary root, PdfObject? rootKids, PdfStructElement?[] kids, PdfDictionary page,
        PdfDictionary dict, Dictionary<PdfDictionary, PdfStructElement> map, int level)
    {
        if (level > MaxRecursion) return null;
        if (map.TryGetValue(dict, out var existing)) return existing;
        var element = Create(r, dict, page);
        map[dict] = element;
        var parent = r.Resolve(dict["P"]) as PdfDictionary;
        if (parent == null || parent.GetName("Type") == "StructTreeRoot" || ReferenceEquals(parent, root))
        {
            if (!AddTopLevelNode(r, rootKids, kids, dict, element)) map.Remove(dict);
            return element;
        }
        var parentElement = AddPageNode(r, root, rootKids, kids, page, parent, map, level + 1);
        if (parentElement == null) return element;
        bool linked = false;
        if (parentElement.Dict != null && r.Resolve(parentElement.Dict["K"]) is { } k)
        {
            var list = k is PdfArray a ? a.Items : new[] { k };
            for (int i = 0; i < list.Count && i < parentElement.Children.Count; i++)
            {
                if (ReferenceEquals(r.Resolve(list[i]), dict)) { parentElement.Children[i] = element; linked = true; }
            }
        }
        if (!linked) map.Remove(dict);
        return element;
    }

    private static bool AddTopLevelNode(PdfObjectResolver r, PdfObject? rootKids, PdfStructElement?[] kids, PdfDictionary dict, PdfStructElement element)
    {
        if (rootKids == null) return false;
        if (rootKids is PdfDictionary single)
        {
            if (!ReferenceEquals(single, dict)) return false;
            kids[0] = element;
            return true;
        }
        if (rootKids is not PdfArray top) return true;
        bool saved = false;
        for (int i = 0; i < top.Count; i++)
        {
            if (top.Items[i] is PdfIndirectRef && ReferenceEquals(r.Resolve(top.Items[i]), dict)) { kids[i] = element; saved = true; }
        }
        return saved;
    }

    private static PdfStructElement Create(PdfObjectResolver r, PdfDictionary dict, PdfDictionary page)
    {
        string? Text(string key) => r.Resolve(dict[key]) is PdfString s ? TextContentReader.TextString(s) : null;
        var e = new PdfStructElement
        {
            Type = dict.GetName("S") ?? string.Empty,
            Lang = Text("Lang"),
            ActualText = Text("ActualText"),
            Alt = Text("Alt"),
            Dict = dict,
        };
        var k = r.Resolve(dict["K"]);
        if (k == null) return e;
        var items = k is PdfArray a ? a.Items : new[] { k };
        foreach (var raw in items)
        {
            e.Children.Add(null);
            var item = r.Resolve(raw);
            if (item != null && item.TryGetNumber(out double mcid)) e.MarkedContentIds.Add((int)mcid);
            else if (item is PdfDictionary d && d.GetName("Type") == "MCR")
                e.MarkedContentIds.Add(d["MCID"] is { } m && m.TryGetNumber(out double mv) ? (int)mv : -1);
            else e.MarkedContentIds.Add(-1);
        }
        return e;
    }
}
