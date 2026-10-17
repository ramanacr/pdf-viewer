using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>
/// A set of changed and new objects for one document, written as an incremental update (the
/// original bytes untouched, so signatures on them stay valid and an encrypted document's new
/// objects are encrypted with its key). A document whose cross-reference data had to be rebuilt
/// cannot take an update; it is then written whole by <see cref="PdfFullWriter"/>.
/// </summary>
public sealed class PdfUpdate
{
    private readonly byte[] _original;
    private readonly Dictionary<int, PdfObject> _objects = new();
    private int _next;

    public PdfUpdate(PdfVectorDocument document, byte[] original)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _original = original ?? throw new ArgumentNullException(nameof(original));
        _next = PdfIncrementalWriter.NextObjectNumber(document);
    }

    public PdfVectorDocument Document { get; }
    public PdfObjectResolver Resolver => Document.Resolver;
    public bool HasChanges => _objects.Count > 0;
    public IReadOnlyDictionary<int, PdfObject> Objects => _objects;

    /// <summary>The object as it stands in this update: changed here, else as read.</summary>
    public PdfObject? Get(int number) => _objects.TryGetValue(number, out var o) ? o : Resolver.Resolve(number);

    /// <summary>Resolves a reference against this update, so a second edit of an object sees the first.</summary>
    public PdfObject? Resolve(PdfObject? obj) => obj is PdfIndirectRef r ? Get(r.ObjectNumber) : obj;

    public void Set(int number, PdfObject obj) => _objects[number] = obj;

    public int Add(PdfObject obj)
    {
        int number = _next++;
        _objects[number] = obj;
        return number;
    }

    public PdfIndirectRef AddRef(PdfObject obj) => new(Add(obj));

    /// <summary>The catalog's object number.</summary>
    public int RootNumber => Document.XrefTable.Trailer?["Root"] is PdfIndirectRef r
        ? r.ObjectNumber
        : throw new NotSupportedException("The document's catalog is not an indirect object.");

    public PdfDictionary Catalog => Get(RootNumber) as PdfDictionary ?? throw new InvalidOperationException("The catalog cannot be read.");

    /// <summary>The page's dictionary as it stands in this update, and its object number.</summary>
    public (int Number, PdfDictionary Page) Page(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > Document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} is outside the document's {Document.PageCount} pages.");
        int number = Document.PageTree.PageObjectNumbers[pageNumber - 1];
        if (number <= 0)
            throw new NotSupportedException($"Page {pageNumber} is a direct object and cannot be changed in an update.");
        return (number, Get(number) as PdfDictionary ?? throw new InvalidOperationException($"Page {pageNumber} cannot be read."));
    }

    /// <summary>The bytes with this update applied; the original bytes when nothing changed.</summary>
    public byte[] Commit(bool forceFullRewrite = false)
    {
        if (_objects.Count == 0 && !forceFullRewrite) return _original;
        if (!forceFullRewrite && !Document.WasRepaired && PdfIncrementalWriter.FindStartXref(_original) >= 0)
            return PdfIncrementalWriter.Append(_original, Document, _objects);
        return PdfFullWriter.Rewrite(Document, _objects);
    }

    // ------------------------------------------------------------------ small builders

    internal static PdfDictionary With(PdfDictionary d, params (string Key, PdfObject? Value)[] changes)
    {
        var entries = new Dictionary<string, PdfObject>(d.Entries);
        foreach (var (k, v) in changes)
        {
            if (v == null) entries.Remove(k);
            else entries[k] = v;
        }
        return new PdfDictionary(entries);
    }

    internal static PdfArray Array(IEnumerable<PdfObject> items) => new(items.ToList());

    internal static PdfArray Numbers(params double[] values) =>
        new(values.Select(v => (PdfObject)(Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(Math.Round(v, 4)))).ToList());
}
