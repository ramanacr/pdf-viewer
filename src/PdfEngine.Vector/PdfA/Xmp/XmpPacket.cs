using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace PdfEngine.Vector.PdfA.Xmp;

public enum XmpKind { Simple, Struct, Bag, Seq, Alt }

/// <summary>A value in the XMP data model: a simple value, a structure of fields, or an array of items.</summary>
public sealed class XmpValue
{
    public XmpKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    /// <summary>Struct fields, by namespace URI and local name, in document order.</summary>
    public List<XmpProperty> Fields { get; } = new();
    public List<XmpValue> Items { get; } = new();
    public List<XmpProperty> Qualifiers { get; } = new();
    /// <summary>The value was given as rdf:resource (a URI).</summary>
    public bool IsUri { get; init; }

    public string? Lang => Qualifiers.FirstOrDefault(q => q.Namespace == XmlNs && q.Name == "lang")?.Value.Text;
    public XmpValue? Field(string ns, string name) => Fields.FirstOrDefault(f => f.Namespace == ns && f.Name == name)?.Value;

    internal const string XmlNs = "http://www.w3.org/XML/1998/namespace";
}

/// <summary>A property (or struct field, or qualifier): its namespace, prefix, name and value.</summary>
public sealed record XmpProperty(string Namespace, string Prefix, string Name, XmpValue Value);

/// <summary>
/// An XMP packet parsed into the XMP data model (XMP Specification Part 1, the RDF/XML
/// serialization): every top-level property of every rdf:Description, with arrays, structs
/// (rdf:parseType="Resource", nested rdf:Description or attribute shorthand) and qualifiers.
/// Malformed packets report <see cref="Error"/> instead of throwing.
/// </summary>
public sealed class XmpPacket
{
    public const string RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    public List<XmpProperty> Properties { get; } = new();
    /// <summary>Why the packet is not well-formed XMP, or null.</summary>
    public string? Error { get; private set; }
    /// <summary>The xpacket header's attributes (begin, id, bytes, encoding), when there is one.</summary>
    public string? PacketHeader { get; private set; }
    /// <summary>Namespace URIs by the prefixes the packet declares.</summary>
    public Dictionary<string, string> PrefixUris { get; } = new(StringComparer.Ordinal);

    public XmpProperty? Get(string ns, string name) => Properties.FirstOrDefault(p => p.Namespace == ns && p.Name == name);
    public string? Simple(string ns, string name) => Get(ns, name)?.Value is { Kind: XmpKind.Simple } v ? v.Text : null;

    public static XmpPacket Parse(byte[] data)
    {
        var packet = new XmpPacket();
        string text;
        try { text = Decode(data); }
        catch (DecoderFallbackException) { packet.Error = "The metadata is not valid UTF-8 or UTF-16."; return packet; }

        int pi = text.IndexOf("<?xpacket ", StringComparison.Ordinal);
        if (pi >= 0)
        {
            int end = text.IndexOf("?>", pi, StringComparison.Ordinal);
            if (end > pi) packet.PacketHeader = text[(pi + 9)..end];
        }
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            doc.Load(reader);
        }
        catch (XmlException ex)
        {
            packet.Error = "The metadata is not well-formed XML: " + ex.Message;
            return packet;
        }
        var rdf = Find(doc.DocumentElement, "RDF");
        if (rdf == null)
        {
            packet.Error = "The metadata has no rdf:RDF element.";
            return packet;
        }
        CollectPrefixes(doc.DocumentElement!, packet.PrefixUris);
        try
        {
            foreach (var description in rdf.ChildNodes.OfType<XmlElement>())
            {
                if (description.NamespaceURI != RdfNs || description.LocalName != "Description")
                {
                    // A typed node: a Description whose rdf:type is the element.
                    packet.Properties.AddRange(PropertiesOf(description, packet));
                    continue;
                }
                packet.Properties.AddRange(PropertiesOf(description, packet));
            }
        }
        catch (FormatException ex)
        {
            packet.Error = ex.Message;
        }
        return packet;
    }

    private static void CollectPrefixes(XmlElement e, Dictionary<string, string> map)
    {
        foreach (XmlAttribute a in e.Attributes)
            if (a.Prefix == "xmlns" && !map.ContainsKey(a.LocalName)) map[a.LocalName] = a.Value;
        foreach (var child in e.ChildNodes.OfType<XmlElement>()) CollectPrefixes(child, map);
    }

    /// <summary>The packet's text, in the encoding its byte order mark or first bytes show (XML 1.0 appendix F): UTF-8, UTF-16 or UTF-32.</summary>
    private static string Decode(byte[] data)
    {
        Encoding enc;
        int skip = 0;
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xFE && data[3] == 0xFF) { enc = new UTF32Encoding(true, false, true); skip = 4; }
        else if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0) { enc = new UTF32Encoding(false, false, true); skip = 4; }
        else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { enc = new UnicodeEncoding(true, false, true); skip = 2; }
        else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { enc = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { enc = new UTF8Encoding(false, true); skip = 3; }
        else if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] != 0) enc = new UTF32Encoding(true, false, true);
        else if (data.Length >= 4 && data[0] != 0 && data[1] == 0 && data[2] == 0 && data[3] == 0) enc = new UTF32Encoding(false, false, true);
        else if (data.Length >= 2 && data[0] == 0 && data[1] != 0) enc = new UnicodeEncoding(true, false, true);
        else if (data.Length >= 2 && data[0] != 0 && data[1] == 0) enc = new UnicodeEncoding(false, false, true);
        else enc = new UTF8Encoding(false, true);
        string text = enc.GetString(data, skip, data.Length - skip);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static XmlElement? Find(XmlElement? e, string local)
    {
        if (e == null) return null;
        if (e.NamespaceURI == RdfNs && e.LocalName == local) return e;
        foreach (var child in e.ChildNodes.OfType<XmlElement>())
            if (Find(child, local) is { } found) return found;
        return null;
    }

    private static bool IsRdfOrXml(XmlAttribute a) =>
        a.NamespaceURI == RdfNs || a.NamespaceURI == XmpValue.XmlNs || a.Prefix == "xmlns" || a.Name == "xmlns";

    /// <summary>The properties a Description (or a struct node) carries, as attributes and child elements.</summary>
    private static IEnumerable<XmpProperty> PropertiesOf(XmlElement node, XmpPacket packet)
    {
        foreach (XmlAttribute a in node.Attributes)
        {
            if (IsRdfOrXml(a)) continue;
            if (a.NamespaceURI.Length == 0) throw new FormatException($"The attribute \"{a.Name}\" has no namespace.");
            yield return new XmpProperty(a.NamespaceURI, a.Prefix, a.LocalName, new XmpValue { Kind = XmpKind.Simple, Text = a.Value });
        }
        foreach (var e in node.ChildNodes.OfType<XmlElement>())
        {
            if (e.NamespaceURI.Length == 0) throw new FormatException($"The element \"{e.Name}\" has no namespace.");
            yield return new XmpProperty(e.NamespaceURI, e.Prefix, e.LocalName, ValueOf(e, packet));
        }
    }

    /// <summary>The value of a property element (RDF/XML productions of the XMP serialization).</summary>
    private static XmpValue ValueOf(XmlElement e, XmpPacket packet)
    {
        var children = e.ChildNodes.OfType<XmlElement>().ToList();
        var qualifiers = new List<XmpProperty>();
        string? lang = e.GetAttribute("xml:lang") is { Length: > 0 } l ? l : null;
        if (lang != null) qualifiers.Add(new XmpProperty(XmpValue.XmlNs, "xml", "lang", new XmpValue { Kind = XmpKind.Simple, Text = lang }));

        if (e.GetAttributeNode("resource", RdfNs) is { } resource)
        {
            var v = new XmpValue { Kind = XmpKind.Simple, Text = resource.Value, IsUri = true };
            v.Qualifiers.AddRange(qualifiers);
            return v;
        }
        if (e.GetAttribute("parseType", RdfNs) == "Resource")
        {
            var s = new XmpValue { Kind = XmpKind.Struct };
            s.Qualifiers.AddRange(qualifiers);
            foreach (var p in PropertiesOf(e, packet)) AddField(s, p);
            return Qualified(s);
        }
        if (children.Count == 1 && children[0].NamespaceURI == RdfNs && children[0].LocalName is "Bag" or "Seq" or "Alt")
        {
            var container = children[0];
            var kind = container.LocalName switch { "Bag" => XmpKind.Bag, "Seq" => XmpKind.Seq, _ => XmpKind.Alt };
            var array = new XmpValue { Kind = kind };
            array.Qualifiers.AddRange(qualifiers);
            foreach (var li in container.ChildNodes.OfType<XmlElement>())
            {
                if (li.NamespaceURI != RdfNs || li.LocalName != "li") throw new FormatException($"An array holds \"{li.Name}\" instead of rdf:li.");
                array.Items.Add(ValueOf(li, packet));
            }
            return array;
        }
        if (children.Count == 1 && children[0].NamespaceURI == RdfNs && children[0].LocalName == "Description")
        {
            var s = new XmpValue { Kind = XmpKind.Struct };
            s.Qualifiers.AddRange(qualifiers);
            foreach (var p in PropertiesOf(children[0], packet)) AddField(s, p);
            return Qualified(s);
        }
        if (children.Count > 0)
            throw new FormatException($"The property \"{e.Name}\" holds elements that are neither an array nor a structure.");

        // Attributes other than rdf:/xml: are the fields of a struct written in short form.
        var fields = e.Attributes.OfType<XmlAttribute>().Where(a => !IsRdfOrXml(a)).ToList();
        if (fields.Count > 0 && e.InnerText.Trim().Length == 0)
        {
            var s = new XmpValue { Kind = XmpKind.Struct };
            s.Qualifiers.AddRange(qualifiers);
            foreach (var a in fields) s.Fields.Add(new XmpProperty(a.NamespaceURI, a.Prefix, a.LocalName, new XmpValue { Kind = XmpKind.Simple, Text = a.Value }));
            return s;
        }
        var simple = new XmpValue { Kind = XmpKind.Simple, Text = e.InnerText };
        simple.Qualifiers.AddRange(qualifiers);
        return simple;
    }

    private static void AddField(XmpValue s, XmpProperty p)
    {
        if (p.Namespace == RdfNs && p.Name == "value") { s.Fields.Add(p); return; }
        s.Fields.Add(p);
    }

    /// <summary>A struct with rdf:value is a qualified simple value: the other fields are its qualifiers.</summary>
    private static XmpValue Qualified(XmpValue s)
    {
        var value = s.Fields.FirstOrDefault(f => f.Namespace == RdfNs && f.Name == "value");
        if (value == null) return s;
        var v = new XmpValue { Kind = value.Value.Kind, Text = value.Value.Text, IsUri = value.Value.IsUri };
        v.Items.AddRange(value.Value.Items);
        v.Fields.AddRange(value.Value.Fields);
        v.Qualifiers.AddRange(s.Qualifiers);
        v.Qualifiers.AddRange(s.Fields.Where(f => !ReferenceEquals(f, value)));
        return v;
    }
}
