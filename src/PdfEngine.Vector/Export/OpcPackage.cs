using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PdfEngine.Vector.Export;

/// <summary>
/// An Office Open XML package written by hand: a zip of parts with their content types, the
/// relationships of each part, and the core and app properties. Parts are written as they are
/// made (images too), so a large export is never held in memory. The Word, Excel and
/// PowerPoint exports each put their own parts in one.
/// </summary>
internal sealed class OpcPackage : IDisposable
{
    internal const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal const string RelBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    private readonly ZipArchive _zip;
    /// <summary>Each source part's relationships ("" is the package's own), in the order they were made.</summary>
    private readonly Dictionary<string, List<(string Id, string Type, string Target, bool External)>> _rels = new(StringComparer.Ordinal);
    private readonly List<string> _relOrder = new();
    private readonly List<(string Part, string ContentType)> _overrides = new();
    private readonly SortedSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public OpcPackage(Stream output)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
    }

    /// <summary>A relationship from <paramref name="source"/> (a part name, "" for the package); returns its id. A short type is one of the officeDocument ones.</summary>
    public string AddRelationship(string source, string type, string target, bool external = false)
    {
        if (!_rels.TryGetValue(source, out var list))
        {
            _rels[source] = list = new List<(string, string, string, bool)>();
            _relOrder.Add(source);
        }
        string id = "rId" + (list.Count + 1).ToString(CultureInfo.InvariantCulture);
        list.Add((id, type.Contains("://", StringComparison.Ordinal) ? type : RelBase + type, target, external));
        return id;
    }

    /// <summary>Writes an XML part; <paramref name="name"/> has no leading slash.</summary>
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

    /// <summary>Writes a binary part (a png or jpeg picture) whose content type comes from its extension.</summary>
    public void WriteMedia(string name, byte[] data)
    {
        var entry = _zip.CreateEntry(name, CompressionLevel.NoCompression);
        using (var s = entry.Open()) s.Write(data);
        _extensions.Add(Path.GetExtension(name).TrimStart('.'));
    }

    /// <summary>Writes the package's own parts: the properties, every part's relationships and the content types.</summary>
    public void Finish(string? title, string mainPart)
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
        AddRelationship(string.Empty, "officeDocument", mainPart);
        AddRelationship(string.Empty, "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        AddRelationship(string.Empty, "extended-properties", "docProps/app.xml");
        foreach (string source in _relOrder) Rels(source, _rels[source]);

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
        foreach (string ext in _extensions) Default(ext, ext.Equals("png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
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

    /// <summary>The relationships part of a part: word/document.xml has word/_rels/document.xml.rels; the package has _rels/.rels.</summary>
    private void Rels(string source, List<(string Id, string Type, string Target, bool External)> rels)
    {
        int slash = source.LastIndexOf('/');
        string name = source.Length == 0 ? "_rels/.rels" : source[..(slash + 1)] + "_rels/" + source[(slash + 1)..] + ".rels";
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

/// <summary>Where the page reader puts the pictures it finds: a part of the package being written, named by what it returns.</summary>
internal interface IExportMedia
{
    /// <summary>Pictures are wanted (an export to Excel has no use for them, so they are not decoded).</summary>
    bool WantsImages { get; }

    /// <summary>Adds a png or jpeg picture; returns how the writer refers to it.</summary>
    string AddImage(byte[] data, string extension);
}
