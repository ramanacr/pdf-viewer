using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Page-tree edits in place, as part of an update (FPDFPage_SetRotation, FPDFPage_Delete,
/// FPDFPage_New): only the page, the /Pages node holding it and the /Count of the nodes above
/// change. Page numbers refer to the document as it was opened, so make one edit per update.
/// </summary>
public static class PdfPageTreeEditor
{
    /// <summary>Sets the page's /Rotate to <paramref name="degrees"/> (a multiple of 90, any sign).</summary>
    public static void Rotate(PdfUpdate update, int pageNumber, int degrees)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Pages turn in quarter turns.", nameof(degrees));
        var (number, page) = update.Page(pageNumber);
        int rotate = ((degrees % 360) + 360) % 360;
        update.Set(number, PdfUpdate.With(page, ("Rotate", new PdfInteger(rotate))));
    }

    public static void Delete(PdfUpdate update, int pageNumber)
    {
        var doc = update.Document;
        if (doc.PageCount <= 1) throw new InvalidOperationException("Cannot delete the only remaining page in a document.");
        var (number, _) = update.Page(pageNumber);
        var ancestors = Ancestors(update, pageNumber);
        int parent = ancestors[^1];
        var parentDict = Node(update, parent);
        var kids = (update.Resolve(parentDict["Kids"]) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
        int at = kids.FindIndex(k => k is PdfIndirectRef r && r.ObjectNumber == number);
        if (at < 0) throw new InvalidOperationException($"Page {pageNumber} is not among its parent's kids.");
        kids.RemoveAt(at);
        update.Set(parent, PdfUpdate.With(parentDict, ("Kids", new PdfArray(kids)), ("Count", Count(update, parentDict, -1))));
        foreach (int node in ancestors.Take(ancestors.Length - 1))
        {
            var d = Node(update, node);
            update.Set(node, PdfUpdate.With(d, ("Count", Count(update, d, -1))));
        }
    }

    /// <summary>Inserts a blank page so that it becomes page <paramref name="index"/> + 1; returns its object number.</summary>
    public static int InsertBlank(PdfUpdate update, int index, double width, double height)
    {
        var doc = update.Document;
        index = Math.Clamp(index, 0, doc.PageCount);
        int parent, at;
        int[] ancestors;
        if (doc.PageCount == 0)
        {
            parent = update.Catalog["Pages"] is PdfIndirectRef root ? root.ObjectNumber : throw new NotSupportedException("The page tree root is not an indirect object.");
            ancestors = new[] { parent };
            at = 0;
        }
        else
        {
            int neighbour = index < doc.PageCount ? index + 1 : doc.PageCount;
            ancestors = Ancestors(update, neighbour);
            parent = ancestors[^1];
            int neighbourNumber = update.Page(neighbour).Number;
            var siblings = (update.Resolve(Node(update, parent)["Kids"]) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
            at = siblings.FindIndex(k => k is PdfIndirectRef r && r.ObjectNumber == neighbourNumber);
            if (at < 0) throw new InvalidOperationException("The page tree does not hold the page it lists.");
            if (index >= doc.PageCount) at++;
        }

        int page = update.Add(new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Page"),
            ["Parent"] = new PdfIndirectRef(parent),
            ["MediaBox"] = PdfUpdate.Numbers(0, 0, width, height),
            // Explicit, as FPDFPage_New writes them: nothing inherited from the node it lands in.
            ["Rotate"] = new PdfInteger(0),
            ["Resources"] = new PdfDictionary(new Dictionary<string, PdfObject>()),
        }));
        var parentDict = Node(update, parent);
        var kids = (update.Resolve(parentDict["Kids"]) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
        kids.Insert(Math.Clamp(at, 0, kids.Count), new PdfIndirectRef(page));
        update.Set(parent, PdfUpdate.With(parentDict, ("Kids", new PdfArray(kids)), ("Count", Count(update, parentDict, +1))));
        foreach (int node in ancestors.Take(ancestors.Length - 1))
        {
            var d = Node(update, node);
            update.Set(node, PdfUpdate.With(d, ("Count", Count(update, d, +1))));
        }
        return page;
    }

    private static int[] Ancestors(PdfUpdate update, int pageNumber)
    {
        var ancestors = update.Document.PageTree.PageAncestors[pageNumber - 1];
        if (ancestors.Length == 0 || ancestors.Any(n => n <= 0))
            throw new NotSupportedException("The page tree has nodes written as direct objects; save a full copy to reorganise it.");
        return ancestors;
    }

    private static PdfDictionary Node(PdfUpdate update, int number) =>
        update.Get(number) as PdfDictionary ?? throw new InvalidOperationException("A page tree node cannot be read.");

    private static PdfInteger Count(PdfUpdate update, PdfDictionary node, int delta) =>
        new(Math.Max(0, (update.Resolve(node["Count"]) is { } c && c.TryGetInteger(out long n) ? n : 0) + delta));
}
