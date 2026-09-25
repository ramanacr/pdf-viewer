using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PdfEngine.Vector.Tests.Fixtures;

/// <summary>
/// Builds minimal, byte-exact PDFs for single-feature fixtures (07_TEST_CORPUS "Generated fixtures").
/// Objects are numbered from 1 in the order added; the xref offsets are computed, never hand-typed.
/// </summary>
internal sealed class VectorPdfBuilder
{
    private readonly List<byte[]> _objects = new();
    private readonly List<int> _pageObjects = new();
    private string _extraTrailer = string.Empty;
    private string _catalogExtra = string.Empty;

    public const int CatalogObject = 1;
    public const int PagesObject = 2;

    public VectorPdfBuilder()
    {
        _objects.Add(Array.Empty<byte>()); // 1: catalog (written at Build)
        _objects.Add(Array.Empty<byte>()); // 2: pages (written at Build)
    }

    /// <summary>Adds an object body (without "N 0 obj"/"endobj") and returns its number.</summary>
    public int Add(string body) => Add(Encoding.Latin1.GetBytes(body));

    public int Add(byte[] body)
    {
        _objects.Add(body);
        return _objects.Count;
    }

    public int AddStream(string dictEntries, string content, bool flate = false) =>
        AddStream(dictEntries, Encoding.Latin1.GetBytes(content), flate);

    public int AddStream(string dictEntries, byte[] data, bool flate = false)
    {
        byte[] payload = data;
        string filter = string.Empty;
        if (flate)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(data);
            payload = ms.ToArray();
            filter = " /Filter /FlateDecode";
        }

        using var body = new MemoryStream();
        Write(body, $"<< {dictEntries}{filter} /Length {payload.Length} >>\nstream\n");
        body.Write(payload);
        Write(body, "\nendstream");
        return Add(body.ToArray());
    }

    /// <summary>Adds a page with the given content stream and resources dictionary text.</summary>
    public int AddPage(string content, string resources = "<< >>", string mediaBox = "[0 0 200 200]", string extra = "", bool flate = false)
    {
        int contentObj = AddStream(string.Empty, content, flate);
        int page = Add($"<< /Type /Page /Parent {PagesObject} 0 R /MediaBox {mediaBox} /Resources {resources} /Contents {contentObj} 0 R {extra} >>");
        _pageObjects.Add(page);
        return page;
    }

    public VectorPdfBuilder WithTrailer(string entries)
    {
        _extraTrailer = entries;
        return this;
    }

    public VectorPdfBuilder WithCatalog(string entries)
    {
        _catalogExtra = entries;
        return this;
    }

    public byte[] Build(bool corruptStartXref = false, bool omitXref = false)
    {
        var kids = new StringBuilder();
        foreach (var p in _pageObjects) kids.Append(p).Append(" 0 R ");
        _objects[CatalogObject - 1] = Encoding.Latin1.GetBytes($"<< /Type /Catalog /Pages {PagesObject} 0 R {_catalogExtra} >>");
        _objects[PagesObject - 1] = Encoding.Latin1.GetBytes($"<< /Type /Pages /Kids [{kids}] /Count {_pageObjects.Count} >>");

        using var ms = new MemoryStream();
        Write(ms, "%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[_objects.Count];
        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = ms.Position;
            Write(ms, $"{i + 1} 0 obj\n");
            ms.Write(_objects[i]);
            Write(ms, "\nendobj\n");
        }

        long xrefPos = ms.Position;
        if (!omitXref)
        {
            Write(ms, $"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var off in offsets)
                Write(ms, off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            Write(ms, $"trailer\n<< /Size {_objects.Count + 1} /Root {CatalogObject} 0 R {_extraTrailer} >>\n");
        }
        Write(ms, $"startxref\n{(corruptStartXref ? xrefPos + 7777 : xrefPos)}\n%%EOF\n");
        return ms.ToArray();
    }

    private static void Write(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));
}
