using System.Security.Cryptography;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.Services;

/// <summary>Where an embedded file sits: the document's /EmbeddedFiles name tree, or a file attachment annotation on a page.</summary>
public enum PdfAttachmentSource { EmbeddedFiles, Annotation }

/// <summary>
/// One embedded file (7.11.4). <see cref="Name"/> and <see cref="Description"/> are untrusted
/// document text; <see cref="Size"/> is the decoded length, -1 when the data cannot be decoded.
/// </summary>
public sealed record PdfAttachment(int Index, PdfAttachmentSource Source, string Key, string Name, long Size)
{
    public string? Description { get; init; }
    public string? MimeType { get; init; }
    public DateTime? Modified { get; init; }

    /// <summary>For an annotation: its page (1-based) and its position in the page's /Annots.</summary>
    public int PageNumber { get; init; }
    public int AnnotationIndex { get; init; } = -1;
}

/// <summary>
/// Lists, extracts, adds and deletes embedded files. The name-tree files come first, in tree order
/// (the order FPDFDoc_GetAttachment indexes them); file attachment annotations follow, page by page.
/// Nothing here opens or runs a payload.
/// </summary>
public static class PdfAttachments
{
    /// <summary>A single attachment larger than this is not decoded into memory.</summary>
    public const long MaxAttachmentBytes = 512L * 1024 * 1024;

    private static readonly PdfSecurityLimits DecodeLimits = new() { MaxDecodedStreamBytes = MaxAttachmentBytes, MaxDecompressionRatio = 100_000 };

    public static IReadOnlyList<PdfAttachment> List(PdfVectorDocument document, bool includeAnnotations = true)
    {
        var r = document.Resolver;
        var nav = new PdfNavigation(document);
        var result = new List<PdfAttachment>();
        var listed = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (var (key, value) in EmbeddedFiles(document))
        {
            result.Add(Describe(result.Count, PdfAttachmentSource.EmbeddedFiles, key, value, r, nav));
            if (FileStream(value, r) is { } s) listed.Add(s);
        }
        if (!includeAnnotations) return result;
        for (int p = 0; p < document.PageCount; p++)
        {
            if (r.Resolve(document.PageTree.Pages[p].Dictionary["Annots"]) is not PdfArray annots) continue;
            for (int i = 0; i < annots.Count; i++)
            {
                if (r.Resolve(annots[i]) is not PdfDictionary a || a.GetName("Subtype") != "FileAttachment" || a["FS"] == null) continue;
                // The same file shown on a page and listed in the name tree is one file.
                if (FileStream(a["FS"], r) is { } shown && !listed.Add(shown)) continue;
                result.Add(Describe(result.Count, PdfAttachmentSource.Annotation, string.Empty, a["FS"], r, nav) with { PageNumber = p + 1, AnnotationIndex = i });
            }
        }
        return result;
    }

    /// <summary>The /EmbeddedFiles name tree's entries, in tree order.</summary>
    internal static IEnumerable<(string Key, PdfObject? Value)> EmbeddedFiles(PdfVectorDocument document)
    {
        var r = document.Resolver;
        return r.Resolve(document.Catalog["Names"]) is PdfDictionary names && names["EmbeddedFiles"] != null
            ? PdfNameTree.Enumerate(names["EmbeddedFiles"], r)
            : Array.Empty<(string, PdfObject?)>();
    }

    private static PdfAttachment Describe(int index, PdfAttachmentSource source, string key, PdfObject? spec, PdfObjectResolver r, PdfNavigation nav)
    {
        string name = nav.FileName(spec) ?? key;
        var stream = FileStream(spec, r);
        long size = -1;
        if (stream != null)
        {
            try { size = Decode(stream, r).LongLength; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { size = -1; }
        }
        var specDict = r.Resolve(spec) as PdfDictionary;
        var parameters = stream != null ? r.Resolve(stream.Dictionary["Params"]) as PdfDictionary : null;
        return new PdfAttachment(index, source, key, name, size)
        {
            Description = specDict != null ? PdfTextStrings.Decode(r.Resolve(specDict["Desc"])) : null,
            MimeType = stream?.Dictionary.GetName("Subtype"),
            Modified = parameters != null ? PdfTextStrings.ParseDate(PdfTextStrings.Decode(r.Resolve(parameters["ModDate"]))) : null,
        };
    }

    /// <summary>The file specification of name-tree entry <paramref name="index"/>.</summary>
    public static PdfObject? EmbeddedFileSpec(PdfVectorDocument document, int index) => EmbeddedFiles(document).ElementAtOrDefault(index).Value;

    /// <summary>The embedded file stream of a file specification: /EF /UF, /F, /DOS, /Mac, /Unix, as CPDF_FileSpec looks it up.</summary>
    public static PdfStream? FileStream(PdfObject? spec, PdfObjectResolver r)
    {
        if (r.Resolve(spec) is not PdfDictionary d || r.Resolve(d["EF"]) is not PdfDictionary ef) return null;
        foreach (var key in new[] { "UF", "F", "DOS", "Mac", "Unix" })
            if (r.Resolve(ef[key]) is PdfStream s) return s;
        return null;
    }

    private static byte[] Decode(PdfStream stream, PdfObjectResolver r) => new PdfStreamDecoder(DecodeLimits, r.Resolve).DecodeStream(stream);

    /// <summary>The file's bytes, decoded.</summary>
    public static byte[] Extract(PdfVectorDocument document, int index)
    {
        var list = List(document);
        if (index < 0 || index >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"The document has {list.Count} embedded file(s).");
        var item = list[index];
        var r = document.Resolver;
        PdfObject? spec = item.Source == PdfAttachmentSource.EmbeddedFiles
            ? EmbeddedFiles(document).ElementAt(index).Value
            : (r.Resolve((r.Resolve(document.PageTree.Pages[item.PageNumber - 1].Dictionary["Annots"]) as PdfArray)![item.AnnotationIndex]) as PdfDictionary)?["FS"];
        var stream = FileStream(spec, r) ?? throw new InvalidDataException($"Embedded file {index + 1} has no contents.");
        return Decode(stream, r);
    }

    /// <summary>A file specification with its embedded file stream (Flate-compressed, with size, dates and checksum).</summary>
    internal static PdfDictionary NewFileSpec(PdfUpdate update, string name, byte[] data, string? description, string? mimeType, DateTime? modified)
    {
        var parameters = new Dictionary<string, PdfObject>
        {
            ["Size"] = new PdfInteger(data.LongLength),
            ["CheckSum"] = new PdfString(MD5.HashData(data)),
        };
        string date = PdfTextStrings.FormatDate(modified ?? DateTime.Now);
        parameters["ModDate"] = PdfObjectWriter.TextString(date);
        parameters["CreationDate"] = PdfObjectWriter.TextString(date);
        var entries = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("EmbeddedFile"),
            ["Filter"] = new PdfName("FlateDecode"),
            ["Params"] = new PdfDictionary(parameters),
        };
        if (!string.IsNullOrEmpty(mimeType)) entries["Subtype"] = new PdfName(mimeType);
        var file = update.AddRef(PdfObjectWriter.NewStream(entries, Redaction.ContentRedactor.Deflate(data)));
        var spec = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Filespec"),
            ["F"] = PdfObjectWriter.TextString(name),
            ["UF"] = PdfObjectWriter.TextString(name),
            ["EF"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["F"] = file, ["UF"] = file }),
        };
        if (!string.IsNullOrEmpty(description)) spec["Desc"] = PdfObjectWriter.TextString(description);
        return new PdfDictionary(spec);
    }

    /// <summary>Adds a file to the /EmbeddedFiles name tree under a key not already used; returns the new bytes.</summary>
    public static byte[] Add(PdfVectorDocument document, byte[] original, string name, byte[] data,
        string? description = null, string? mimeType = null, DateTime? modified = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(data);
        var update = new PdfUpdate(document, original);
        var entries = EmbeddedFiles(document).ToList();
        string key = name;
        for (int n = 2; entries.Any(e => e.Key == key); n++) key = $"{name} ({n})";
        var spec = update.AddRef(NewFileSpec(update, name, data, description, mimeType, modified));
        entries.Add((key, spec));
        WriteEmbeddedFiles(update, entries);
        return update.Commit();
    }

    /// <summary>Removes one embedded file (by its index in <see cref="List"/>); a file attachment annotation is removed from its page.</summary>
    public static byte[] Delete(PdfVectorDocument document, byte[] original, int index)
    {
        var list = List(document);
        if (index < 0 || index >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"The document has {list.Count} embedded file(s).");
        var item = list[index];
        var update = new PdfUpdate(document, original);
        if (item.Source == PdfAttachmentSource.EmbeddedFiles)
        {
            var entries = EmbeddedFiles(document).ToList();
            entries.RemoveAt(index);
            WriteEmbeddedFiles(update, entries);
        }
        else
        {
            PdfAnnotationEditor.RemoveAt(update, item.PageNumber, item.AnnotationIndex);
        }
        return update.Commit();
    }

    /// <summary>Replaces the /EmbeddedFiles tree with one sorted leaf (7.9.6 keys in byte order); empty removes it.</summary>
    private static void WriteEmbeddedFiles(PdfUpdate update, List<(string Key, PdfObject? Value)> entries)
    {
        var catalog = update.Catalog;
        var namesValue = catalog["Names"];
        var names = update.Resolve(namesValue) as PdfDictionary ?? new PdfDictionary(new Dictionary<string, PdfObject>());
        PdfObject? tree = null;
        if (entries.Count > 0)
        {
            var sorted = entries.Where(e => e.Value != null).OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
            var flat = new List<PdfObject>();
            foreach (var (key, value) in sorted)
            {
                flat.Add(PdfObjectWriter.TextString(key));
                flat.Add(value!);
            }
            tree = update.AddRef(new PdfDictionary(new Dictionary<string, PdfObject> { ["Names"] = new PdfArray(flat) }));
        }
        var newNames = PdfUpdate.With(names, ("EmbeddedFiles", tree));
        if (namesValue is PdfIndirectRef namesRef)
            update.Set(namesRef.ObjectNumber, newNames);
        else
            update.Set(update.RootNumber, PdfUpdate.With(catalog, ("Names", newNames.Count > 0 ? newNames : null)));
    }
}
