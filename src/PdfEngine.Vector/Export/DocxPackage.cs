using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace PdfEngine.Vector.Export;

/// <summary>
/// The .docx package: a zip of WordprocessingML parts with their content types and
/// relationships. Images are written as they are added, so they are not all held in memory.
/// </summary>
internal sealed class DocxPackage : IDisposable
{
    internal const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    internal const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string ContentBase = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

    private readonly ZipArchive _zip;
    private readonly List<(string Id, string Type, string Target, bool External)> _documentRels = new();
    private readonly List<(string Part, string ContentType)> _overrides = new();
    private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private int _nextRel = 1, _images;

    public DocxPackage(Stream output)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
    }

    public string NewRelationship(string type, string target, bool external = false)
    {
        string id = "rId" + (_nextRel++).ToString(CultureInfo.InvariantCulture);
        _documentRels.Add((id, RelBase + type, target, external));
        return id;
    }

    /// <summary>Adds an image part (png or jpeg); returns its relationship id from the document.</summary>
    public string AddImage(byte[] data, string extension)
    {
        string name = $"media/image{++_images}.{extension}";
        var entry = _zip.CreateEntry("word/" + name, CompressionLevel.NoCompression);
        using (var s = entry.Open()) s.Write(data);
        _extensions.Add(extension);
        return NewRelationship("image", name);
    }

    public int ImageCount => _images;

    /// <summary>Writes an XML part of the document (under word/).</summary>
    public void WritePart(string name, string contentType, Action<XmlWriter> write)
    {
        var entry = _zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = true });
        xml.WriteStartDocument(true);
        write(xml);
        xml.WriteEndDocument();
        _overrides.Add(("/" + name, contentType));
    }

    public static string WordType(string part) => ContentBase + part + "+xml";

    /// <summary>Writes the package's own parts: content types, relationships and properties.</summary>
    public void Finish(string? title)
    {
        WritePart("docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml", x =>
        {
            x.WriteStartElement("cp", "coreProperties", "http://schemas.openxmlformats.org/package/2006/metadata/core-properties");
            x.WriteAttributeString("xmlns", "dc", null, "http://purl.org/dc/elements/1.1/");
            x.WriteAttributeString("xmlns", "dcterms", null, "http://purl.org/dc/terms/");
            x.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            if (!string.IsNullOrWhiteSpace(title)) x.WriteElementString("dc", "title", "http://purl.org/dc/elements/1.1/", TextKinds.Clean(title));
            x.WriteStartElement("dcterms", "created", "http://purl.org/dc/terms/");
            x.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "dcterms:W3CDTF");
            x.WriteString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            x.WriteEndElement();
            x.WriteEndElement();
        });
        WritePart("docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml", x =>
        {
            x.WriteStartElement("Properties", "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties");
            x.WriteElementString("Application", "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties", "PDF Viewer");
            x.WriteEndElement();
        });
        Rels("word/_rels/document.xml.rels", _documentRels);
        Rels("_rels/.rels", new List<(string, string, string, bool)>
        {
            ("rId1", RelBase + "officeDocument", "word/document.xml", false),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml", false),
            ("rId3", RelBase + "extended-properties", "docProps/app.xml", false),
        });
        var entry = _zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        const string ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        xml.WriteStartDocument(true);
        xml.WriteStartElement("Types", ct);
        void Default(string ext, string type)
        {
            xml.WriteStartElement("Default", ct);
            xml.WriteAttributeString("Extension", ext);
            xml.WriteAttributeString("ContentType", type);
            xml.WriteEndElement();
        }
        Default("rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default("xml", "application/xml");
        foreach (string ext in _extensions) Default(ext, ext == "png" ? "image/png" : "image/jpeg");
        foreach (var (part, type) in _overrides)
        {
            xml.WriteStartElement("Override", ct);
            xml.WriteAttributeString("PartName", part);
            xml.WriteAttributeString("ContentType", type);
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private void Rels(string name, List<(string Id, string Type, string Target, bool External)> rels)
    {
        var entry = _zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        xml.WriteStartDocument(true);
        xml.WriteStartElement("Relationships", ns);
        foreach (var (id, type, target, external) in rels)
        {
            xml.WriteStartElement("Relationship", ns);
            xml.WriteAttributeString("Id", id);
            xml.WriteAttributeString("Type", type);
            xml.WriteAttributeString("Target", target);
            if (external) xml.WriteAttributeString("TargetMode", "External");
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    public void Dispose() => _zip.Dispose();
}
