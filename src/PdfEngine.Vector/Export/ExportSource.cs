using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Redaction;

namespace PdfEngine.Vector.Export;

/// <summary>What the exports to Word, Excel and PowerPoint share: reading the pages, and writing a file whole or not at all.</summary>
internal static class ExportSource
{
    /// <summary>
    /// A page reader for the document, with its structure tree (Tagged PDF) when the tree can be
    /// read and its pages match the document's; <paramref name="usedTags"/> says whether it was.
    /// </summary>
    public static PageReader Reader(PdfVectorDocument document, IExportMedia media, List<string> hyperlinks, IPdfVectorRenderer? renderer, double graphicsDpi,
        List<string> warnings, out bool usedTags)
    {
        StructureTree? tags = null;
        try { tags = StructureTree.Read(document); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add($"The document's tags could not be read ({ex.GetType().Name}); its layout is used instead.");
        }
        List<int>? pageObjects = null;
        if (tags != null && document.XrefTable.Trailer is { } trailer && document.Resolver.Resolve(trailer["Root"]) is PdfDictionary root)
        {
            pageObjects = PdfRedactor.PageObjectNumbers(root, document.Resolver);
            if (pageObjects.Count != document.PageCount) pageObjects = null;
        }
        usedTags = pageObjects != null && tags != null;
        return new PageReader(document, pageObjects != null ? tags : null, pageObjects, media, hyperlinks, renderer, graphicsDpi);
    }

    /// <summary>Writes a file through <paramref name="write"/>: complete, or not at all (a cancelled or failed export leaves nothing behind).</summary>
    public static async Task<T> ToFileAsync<T>(string path, Func<Stream, Task<T>> write)
    {
        string temp = path + ".partial";
        try
        {
            T result;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                result = await write(stream).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return result;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Takes no pictures: for an export that has no use for them.</summary>
internal sealed class NoMedia : IExportMedia
{
    public static readonly NoMedia Instance = new();
    public bool WantsImages => false;
    public string AddImage(byte[] data, string extension) => string.Empty;
}
