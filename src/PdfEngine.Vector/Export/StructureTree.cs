using System;
using System.Collections.Generic;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Export;

/// <summary>An element of a Tagged PDF's structure tree: its standard type and where it sits.</summary>
internal sealed class StructNode
{
    public string Type = "NonStruct";
    public StructNode? Parent;
    /// <summary>Order among all elements (depth first), so siblings compare by position.</summary>
    public int Order;
    public int ColSpan = 1, RowSpan = 1;
    public string? ListNumbering;
    public List<StructNode> Children = new();

    public StructNode? Ancestor(Func<StructNode, bool> match)
    {
        for (var n = this; n != null; n = n.Parent)
            if (match(n)) return n;
        return null;
    }

    public int HeadingLevel => Type switch
    {
        "H" or "H1" or "Title" => 1,
        "H2" => 2,
        "H3" => 3,
        "H4" => 4,
        "H5" => 5,
        "H6" => 6,
        _ => 0,
    };
}

/// <summary>
/// Reads the structure tree (ISO 32000-2 14.7): which element each marked-content sequence of a
/// page belongs to, with custom types mapped to standard ones through the role map.
/// </summary>
internal sealed class StructureTree
{
    private const int MaxElements = 400_000;
    private readonly Dictionary<(int Page, int Mcid), StructNode> _content = new();

    public int Count => _content.Count;

    public StructNode? Find(int pageObject, int mcid) => _content.TryGetValue((pageObject, mcid), out var n) ? n : null;

    private static readonly HashSet<string> Standard = new(StringComparer.Ordinal)
    {
        "Document", "DocumentFragment", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "Title", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP", "Figure",
        "Formula", "Form", "FENote", "Sub", "Em", "Strong", "Aside", "Artifact",
    };

    /// <summary>The document's structure tree, or null when it has none (or none that marks content).</summary>
    public static StructureTree? Read(PdfVectorDocument doc)
    {
        var r = doc.Resolver;
        if (doc.XrefTable.Trailer is not { } trailer || r.Resolve(trailer["Root"]) is not PdfDictionary root) return null;
        if (r.Resolve(root["StructTreeRoot"]) is not PdfDictionary treeRoot) return null;
        var roleMap = r.Resolve(treeRoot["RoleMap"]) as PdfDictionary;
        var tree = new StructureTree();
        var visited = new HashSet<int>();
        int order = 0;
        var top = new StructNode { Type = "Document" };
        // Depth first with an explicit stack: (object, parent, inherited page).
        var stack = new Stack<(PdfObject Obj, StructNode Parent, int Page)>();
        PushKids(treeRoot["K"], top, -1);
        while (stack.Count > 0 && order < MaxElements)
        {
            var (obj, parent, page) = stack.Pop();
            if (obj is PdfIndirectRef ir && !visited.Add(ir.ObjectNumber)) continue;
            var value = r.Resolve(obj);
            switch (value)
            {
                case PdfInteger mcid:
                    if (page >= 0) tree._content.TryAdd((page, (int)mcid.Value), parent);
                    break;
                case PdfDictionary d when d.GetName("Type") == "MCR":
                    int mp = d["Pg"] is PdfIndirectRef mpg ? mpg.ObjectNumber : page;
                    if (mp >= 0 && r.Resolve(d["MCID"]) is PdfInteger mi) tree._content.TryAdd((mp, (int)mi.Value), parent);
                    break;
                case PdfDictionary d when d.GetName("Type") == "OBJR":
                    break;
                case PdfDictionary d when d.ContainsKey("S"):
                {
                    var node = new StructNode { Type = Role(d.GetName("S") ?? "NonStruct", roleMap, r), Parent = parent, Order = ++order };
                    parent.Children.Add(node);
                    ReadAttributes(d, node, r);
                    int pg = d["Pg"] is PdfIndirectRef p ? p.ObjectNumber : page;
                    PushKids(d["K"], node, pg);
                    break;
                }
            }
        }
        return tree._content.Count > 0 ? tree : null;

        void PushKids(PdfObject? kids, StructNode parent, int page)
        {
            var k = kids is PdfIndirectRef ? r.Resolve(kids) is PdfArray ra ? ra : kids : kids;
            if (k is PdfArray arr)
            {
                // Pushed in reverse so they are visited in order.
                for (int i = arr.Count - 1; i >= 0; i--) stack.Push((arr[i], parent, page));
            }
            else if (k != null) stack.Push((k, parent, page));
        }
    }

    private static string Role(string type, PdfDictionary? roleMap, PdfObjectResolver r)
    {
        for (int hop = 0; hop < 10 && !Standard.Contains(type) && roleMap != null; hop++)
        {
            if (r.Resolve(roleMap[type]) is not PdfName mapped) break;
            type = mapped.Value;
        }
        return type;
    }

    private static void ReadAttributes(PdfDictionary d, StructNode node, PdfObjectResolver r)
    {
        void Read(PdfDictionary a)
        {
            if (r.Resolve(a["ColSpan"]) is PdfInteger cs && cs.Value is > 1 and < 1000) node.ColSpan = (int)cs.Value;
            if (r.Resolve(a["RowSpan"]) is PdfInteger rs && rs.Value is > 1 and < 10000) node.RowSpan = (int)rs.Value;
            if (r.Resolve(a["ListNumbering"]) is PdfName ln) node.ListNumbering = ln.Value;
        }
        switch (r.Resolve(d["A"]))
        {
            case PdfDictionary a: Read(a); break;
            case PdfArray arr:
                foreach (var item in arr)
                    if (r.Resolve(item) is PdfDictionary ad) Read(ad);
                break;
        }
    }
}
