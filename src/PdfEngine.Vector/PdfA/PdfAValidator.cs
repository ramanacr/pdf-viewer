using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.PdfA.Xmp;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.PdfA;

/// <summary>
/// Checks a document against PDF/A-1, -2 or -3 (ISO 19005), level B or U: the file's syntax,
/// every object's limits, colour and output intents, fonts, images, transparency, annotations,
/// forms, actions and the XMP metadata with its schemas. Each issue names its clause and test as
/// the veraPDF validation profiles number them, so reports can be compared with that reference.
/// Only reads: nothing in the document is executed or changed.
/// </summary>
public static class PdfAValidator
{
    /// <summary>
    /// Validates against <paramref name="flavour"/>, or against what the document claims to be
    /// (PDF/A-2b when it claims nothing).
    /// </summary>
    public static PdfAReport Validate(PdfVectorDocument document, byte[] file, PdfAFlavour? flavour = null)
    {
        var claimed = ClaimedFlavour(document);
        var target = flavour ?? claimed ?? PdfAFlavour.A2b;
        if (target.Level == PdfAConformance.A) target = target with { Level = target.Part == 1 ? PdfAConformance.B : PdfAConformance.U };
        var checker = new PdfAChecker(document, file, target);
        checker.Run();
        return new PdfAReport { Flavour = target, Claimed = claimed, Issues = checker.Issues };
    }

    /// <summary>The conformance the document's XMP metadata claims (pdfaid:part and pdfaid:conformance), or null.</summary>
    public static PdfAFlavour? ClaimedFlavour(PdfVectorDocument document)
    {
        var xmp = ReadMetadata(document);
        if (xmp == null || xmp.Error != null) return null;
        var part = xmp.Simple(XmpSchemas.PdfaId, "part");
        var conformance = xmp.Simple(XmpSchemas.PdfaId, "conformance");
        if (!int.TryParse(part, out int p) || p is < 1 or > 4) return null;
        return PdfAFlavour.Parse($"{p}{conformance}");
    }

    internal static XmpPacket? ReadMetadata(PdfVectorDocument document)
    {
        var r = document.Resolver;
        if (r.Resolve(document.XrefTable.Trailer?["Root"]) is not PdfDictionary root) return null;
        if (r.Resolve(root["Metadata"]) is not PdfStream stream) return null;
        try { return XmpPacket.Parse(new PdfStreamDecoder(null, r.Resolve).DecodeStream(stream)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}

/// <summary>The rules, split by topic over partial files.</summary>
internal sealed partial class PdfAChecker
{
    private readonly PdfVectorDocument _doc;
    private readonly byte[] _file;
    private readonly PdfObjectResolver _r;
    private readonly PdfStreamDecoder _decoder;
    private readonly int _part;
    private readonly bool _unicode;
    private readonly Dictionary<string, int> _perRule = new(StringComparer.Ordinal);
    private const int MaxPerRule = 25;

    public List<PdfAIssue> Issues { get; } = new();

    private PdfDictionary Trailer { get; }
    private PdfDictionary? Catalog { get; }

    public PdfAChecker(PdfVectorDocument doc, byte[] file, PdfAFlavour flavour)
    {
        _doc = doc;
        _file = file;
        _r = doc.Resolver;
        _decoder = new PdfStreamDecoder(null, _r.Resolve);
        _part = flavour.Part;
        _unicode = flavour.Level == PdfAConformance.U;
        Trailer = doc.XrefTable.Trailer ?? new PdfDictionary(new Dictionary<string, PdfObject>());
        Catalog = _r.Resolve(Trailer["Root"]) as PdfDictionary;
    }

    /// <summary>Records an issue under the clause of the part being checked (<paramref name="p1"/> for PDF/A-1, <paramref name="p2"/> for -2 and -3).</summary>
    private void Fail(string? p1, string? p2, int test, string message, string? where = null) => Fail(_part == 1 ? p1 : p2, test, message, where);

    private void Fail(string? clause, int test, string message, string? where = null)
    {
        if (clause == null) return;
        string id = clause + "-" + test;
        _perRule.TryGetValue(id, out int n);
        _perRule[id] = n + 1;
        if (n < MaxPerRule) Issues.Add(new PdfAIssue(clause, test, message, where));
    }

    public void Run()
    {
        Guard(CheckFileStructure);
        Guard(CheckObjects);
        Guard(CheckMetadata);
        Guard(CheckOutputIntents);
        Guard(CheckPages);
        Guard(CheckAnnotationsAndForms);
        Guard(CheckFonts);
        Guard(CheckActions);
        Guard(CheckOptionalContent);
        Guard(CheckEmbeddedFiles);
        Guard(CheckPermissions);
    }

    /// <summary>A rule that cannot be evaluated (a broken structure) is reported, not thrown.</summary>
    private void Guard(Action check)
    {
        try { check(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Fail("6.1", 0, $"Part of the document could not be checked: {ex.Message}");
        }
    }

    private static string Ref(int number) => $"object {number}";

    private byte[]? Decode(PdfStream stream)
    {
        try { return _decoder.DecodeStream(stream); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // ------------------------------------------------------------------ metadata (P1 6.7, P2/3 6.6)

    private void CheckMetadata()
    {
        if (Catalog == null) return;
        var metaObj = _r.Resolve(Catalog["Metadata"]);
        if (metaObj is not PdfStream stream)
        {
            Fail("6.7.2", "6.6.2.1", 1, "The document catalog has no metadata stream.");
            return;
        }
        if (stream.Dictionary.GetName("Type") != "Metadata" || stream.Dictionary.GetName("Subtype") != "XML")
            Fail("6.7.2", "6.6.2.1", 1, "The metadata stream is not of type /Metadata, subtype /XML.");
        if (_part == 1 && stream.Dictionary.ContainsKey("Filter"))
            Fail("6.7.2", 2, "The metadata stream is compressed (it has a /Filter).");
        byte[]? data = Decode(stream);
        if (data == null)
        {
            Fail("6.7.2", "6.6.2.1", 1, "The metadata stream cannot be read.");
            return;
        }
        var xmp = XmpPacket.Parse(data);
        if (xmp.Error != null)
        {
            Fail("6.7.9", "6.6.2.1", 1, xmp.Error);
            return;
        }
        if (xmp.PacketHeader is { } header && (header.Contains("bytes=", StringComparison.Ordinal) || header.Contains("encoding=", StringComparison.Ordinal)))
            Fail("6.7.5", "6.6.2.1", 2, "The XMP packet header has a bytes or encoding attribute.");

        CheckPdfaId(xmp);
        var extensions = CheckExtensionSchemas(xmp);
        CheckProperties(xmp, extensions);
        if (_part == 1) CheckInfoMatchesXmp(xmp);

        // Metadata streams of other objects must be XMP too.
        foreach (var (number, obj) in AllObjects())
        {
            if (obj is not PdfStream s || s.Dictionary.GetName("Type") != "Metadata" || ReferenceEquals(s, stream)) continue;
            if (Decode(s) is { } d && XmpPacket.Parse(d).Error is { } err) Fail("6.7.9", "6.6.2.1", 1, err, Ref(number));
        }
    }

    private void CheckPdfaId(XmpPacket xmp)
    {
        var partProp = xmp.Get(XmpSchemas.PdfaId, "part");
        var conf = xmp.Get(XmpSchemas.PdfaId, "conformance");
        if (partProp == null)
        {
            Fail("6.7.11", "6.6.4", 1, "The metadata does not identify the PDF/A part (pdfaid:part).");
            return;
        }
        if (partProp.Value.Text.Trim() != _part.ToString(System.Globalization.CultureInfo.InvariantCulture))
            Fail("6.7.11", "6.6.4", 1, $"The metadata claims PDF/A part {partProp.Value.Text}, not {_part}.");
        string level = conf?.Value.Text.Trim() ?? string.Empty;
        bool ok = _part == 1 ? level is "A" or "B" : level is "A" or "B" or "U";
        if (!ok) Fail("6.7.11", "6.6.4", 1, $"The metadata's conformance level (pdfaid:conformance) is \"{level}\".");
        if (partProp.Prefix != "pdfaid" || (conf != null && conf.Prefix != "pdfaid"))
            Fail("6.7.11", "6.6.4", 1, "The PDF/A identification does not use the pdfaid prefix.");
        if (xmp.Get(XmpSchemas.PdfaId, "amd") is { } amd && amd.Value.Kind != XmpKind.Simple)
            Fail("6.7.11", "6.6.4", 1, "pdfaid:amd is not text.");
    }

    // ------------------------------------------------------------------ XMP properties and schemas

    /// <summary>A schema an extension schema container defines: its properties and value types.</summary>
    private sealed record ExtensionSchema(string Namespace, Dictionary<string, string> Properties, Dictionary<string, XmpSchemas.StructType> Types);

    private Dictionary<string, ExtensionSchema> CheckExtensionSchemas(XmpPacket xmp)
    {
        var result = new Dictionary<string, ExtensionSchema>(StringComparer.Ordinal);
        var container = xmp.Get(XmpSchemas.PdfaExtension, "schemas");
        if (container == null) return result;
        string c1 = "6.7.8", c2 = "6.6.2.3.3";
        // The container schemas are written with their own prefixes.
        foreach (var (ns, prefix, test) in new[] { (XmpSchemas.PdfaExtension, "pdfaExtension", 1), (XmpSchemas.PdfaSchema, "pdfaSchema", 6), (XmpSchemas.PdfaProperty, "pdfaProperty", 5),
                     (XmpSchemas.PdfaType, "pdfaType", 7), (XmpSchemas.PdfaField, "pdfaField", 4) })
            if (xmp.PrefixUris.Any(p => p.Value == ns && p.Key != prefix))
                Fail(c1, c2, test, $"The namespace {ns} is given the prefix \"{xmp.PrefixUris.First(p => p.Value == ns && p.Key != prefix).Key}\" instead of \"{prefix}\".");
        if (container.Value.Kind != XmpKind.Bag) { Fail(c1, c2, 1, "pdfaExtension:schemas is not a Bag."); return result; }
        foreach (var schema in container.Value.Items)
        {
            if (schema.Kind != XmpKind.Struct) { Fail(c1, c2, 1, "An extension schema is not a structure."); continue; }
            string? ns = null;
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            var types = new Dictionary<string, XmpSchemas.StructType>(StringComparer.Ordinal);
            bool hasSchema = false, hasPrefix = false;
            foreach (var f in schema.Fields)
            {
                if (f.Namespace != XmpSchemas.PdfaSchema) { Fail(c1, c2, 1, $"An extension schema has the unknown field {f.Prefix}:{f.Name}."); continue; }
                switch (f.Name)
                {
                    case "schema": hasSchema = f.Value.Kind == XmpKind.Simple; if (!hasSchema) Fail(c1, c2, 1, "pdfaSchema:schema is not text."); break;
                    case "namespaceURI": if (f.Value.Kind == XmpKind.Simple) ns = f.Value.Text; else Fail(c1, c2, 1, "pdfaSchema:namespaceURI is not text."); break;
                    case "prefix": hasPrefix = f.Value.Kind == XmpKind.Simple; if (!hasPrefix) Fail(c1, c2, 1, "pdfaSchema:prefix is not text."); break;
                    case "property":
                        if (f.Value.Kind != XmpKind.Seq) { Fail(c1, c2, 2, "pdfaSchema:property is not a Seq."); break; }
                        foreach (var p in f.Value.Items) ReadExtensionProperty(p, props, c1, c2);
                        break;
                    case "valueType":
                        if (f.Value.Kind != XmpKind.Seq) { Fail(c1, c2, 3, "pdfaSchema:valueType is not a Seq."); break; }
                        foreach (var t in f.Value.Items) ReadExtensionType(t, types, c1, c2);
                        break;
                    default: Fail(c1, c2, 1, $"An extension schema has the unknown field pdfaSchema:{f.Name}."); break;
                }
            }
            if (!hasSchema || ns == null || !hasPrefix) Fail(c1, c2, 1, "An extension schema lacks its schema, namespaceURI or prefix.");
            if (ns != null) result[ns] = new ExtensionSchema(ns, props, types);
        }
        return result;
    }

    private void ReadExtensionProperty(XmpValue p, Dictionary<string, string> props, string c1, string c2)
    {
        if (p.Kind != XmpKind.Struct) { Fail(c1, c2, 2, "An extension property is not a structure."); return; }
        string? name = null, type = null, category = null, description = null;
        foreach (var f in p.Fields)
        {
            if (f.Namespace != XmpSchemas.PdfaProperty || f.Value.Kind != XmpKind.Simple) { Fail(c1, c2, 2, $"An extension property has the field {f.Prefix}:{f.Name}, which it may not have."); continue; }
            switch (f.Name)
            {
                case "name": name = f.Value.Text; break;
                case "valueType": type = f.Value.Text; break;
                case "category": category = f.Value.Text; break;
                case "description": description = f.Value.Text; break;
                default: Fail(c1, c2, 2, $"An extension property has the unknown field pdfaProperty:{f.Name}."); break;
            }
        }
        if (name == null || type == null || category == null || description == null)
            Fail(c1, c2, 2, "An extension property lacks its name, valueType, category or description.");
        else if (category is not ("internal" or "external"))
            Fail(c1, c2, 2, $"An extension property's category is \"{category}\", not internal or external.");
        if (name != null && type != null) props[name] = type;
    }

    private void ReadExtensionType(XmpValue t, Dictionary<string, XmpSchemas.StructType> types, string c1, string c2)
    {
        if (t.Kind != XmpKind.Struct) { Fail(c1, c2, 3, "An extension value type is not a structure."); return; }
        string? name = null, ns = null, prefix = null, description = null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in t.Fields)
        {
            if (f.Namespace != XmpSchemas.PdfaType) { Fail(c1, c2, 3, $"An extension value type has the field {f.Prefix}:{f.Name}, which it may not have."); continue; }
            switch (f.Name)
            {
                case "type": name = f.Value.Text; break;
                case "namespaceURI": ns = f.Value.Text; break;
                case "prefix": prefix = f.Value.Text; break;
                case "description": description = f.Value.Text; break;
                case "field":
                    if (f.Value.Kind != XmpKind.Seq) { Fail(c1, c2, 4, "pdfaType:field is not a Seq."); break; }
                    foreach (var fd in f.Value.Items)
                    {
                        if (fd.Kind != XmpKind.Struct) { Fail(c1, c2, 4, "An extension field is not a structure."); continue; }
                        string? fn = null, ft = null, fdesc = null;
                        foreach (var x in fd.Fields)
                        {
                            if (x.Namespace != XmpSchemas.PdfaField || x.Value.Kind != XmpKind.Simple) { Fail(c1, c2, 4, $"An extension field has the field {x.Prefix}:{x.Name}, which it may not have."); continue; }
                            switch (x.Name)
                            {
                                case "name": fn = x.Value.Text; break;
                                case "valueType": ft = x.Value.Text; break;
                                case "description": fdesc = x.Value.Text; break;
                                default: Fail(c1, c2, 4, $"An extension field has the unknown field pdfaField:{x.Name}."); break;
                            }
                        }
                        if (fn == null || ft == null || fdesc == null) Fail(c1, c2, 4, "An extension field lacks its name, valueType or description.");
                        else fields[fn] = ft;
                    }
                    break;
                default: Fail(c1, c2, 3, $"An extension value type has the unknown field pdfaType:{f.Name}."); break;
            }
        }
        if (name == null || ns == null || prefix == null || description == null)
            Fail(c1, c2, 3, "An extension value type lacks its type, namespaceURI, prefix or description.");
        else types[name] = new XmpSchemas.StructType(name, ns, fields);
    }

    private void CheckProperties(XmpPacket xmp, Dictionary<string, ExtensionSchema> extensions)
    {
        string c1 = "6.7.2", c2 = "6.6.2.3.1";
        XmpSchemas.StructType? Struct(string name)
        {
            if (XmpSchemas.Predefined(name) is { } s) return s;
            foreach (var e in extensions.Values) if (e.Types.TryGetValue(name, out var t)) return t;
            return null;
        }
        bool extensionContainerSeen = false;
        foreach (var prop in xmp.Properties)
        {
            if (prop.Namespace is XmpSchemas.PdfaExtension)
            {
                extensionContainerSeen = true;
                continue;
            }
            if (prop.Namespace == XmpSchemas.XmpDM && _part == 1 && !extensions.ContainsKey(prop.Namespace))
            {
                Fail("6.7.8", 1, $"{prop.Prefix}:{prop.Name} belongs to a schema PDF/A-1 does not predefine, and no extension schema describes it.");
                continue;
            }
            if (_part == 1 && prop.Namespace == XmpSchemas.PdfaId && prop.Name is "corr" or "rev" && !extensions.ContainsKey(prop.Namespace))
            {
                Fail("6.7.11", 2, $"pdfaid:{prop.Name} is not defined for PDF/A-1, and no extension schema describes it.");
                continue;
            }
            if (_part == 1 && XmpSchemas.NotIn2004(prop.Namespace, prop.Name) && !extensions.ContainsKey(prop.Namespace))
            {
                Fail(c1, 1, $"{prop.Prefix}:{prop.Name} is not defined in XMP 2004, which PDF/A-1 uses, and no extension schema describes it.");
                continue;
            }
            if (XmpSchemas.Schemas.TryGetValue(prop.Namespace, out var schema))
            {
                if (!schema.TryGetValue(prop.Name, out var type))
                {
                    if (extensions.TryGetValue(prop.Namespace, out var ext) && ext.Properties.TryGetValue(prop.Name, out var extType))
                        type = extType;
                    else
                    {
                        Fail(c1, c2, 1, $"{prop.Prefix}:{prop.Name} is not a property of its predefined schema, and no extension schema describes it.");
                        continue;
                    }
                }
                if (_part == 1 && XmpSchemas.Types2004.TryGetValue((prop.Namespace, prop.Name), out var old2004)) type = old2004;
                if (XmpSchemas.Check(prop.Value, type, Struct) is { } why)
                    Fail(c1, c2, 1, $"{prop.Prefix}:{prop.Name} {why}.");
                if (_part == 1 && XmpSchemas.Prefixes.TryGetValue(prop.Namespace, out var preferred) && prop.Prefix != preferred)
                    Fail(c1, 1, $"{prop.Prefix}:{prop.Name} uses the prefix \"{prop.Prefix}\" instead of \"{preferred}\".");
                continue;
            }
            if (extensions.TryGetValue(prop.Namespace, out var extension))
            {
                if (!extension.Properties.TryGetValue(prop.Name, out var type))
                {
                    Fail("6.7.8", "6.6.2.3.2", 1, $"{prop.Prefix}:{prop.Name} is not defined by the extension schema of its namespace.");
                    continue;
                }
                if (XmpSchemas.Check(prop.Value, type, Struct) is { } why)
                    Fail("6.7.8", "6.6.2.3.2", 1, $"{prop.Prefix}:{prop.Name} {why}.");
                continue;
            }
            Fail("6.7.8", "6.6.2.3.2", 1, $"{prop.Prefix}:{prop.Name} belongs to a schema that is neither predefined nor described by an extension schema.");
        }
        _ = extensionContainerSeen;
    }

    /// <summary>PDF/A-1 6.7.3: the document information dictionary and the XMP metadata agree.</summary>
    private void CheckInfoMatchesXmp(XmpPacket xmp)
    {
        if (_r.Resolve(Trailer["Info"]) is not PdfDictionary info) return;
        string? Text(string key) => _r.Resolve(info[key]) is PdfString s ? s.AsDecodedString() : null;
        string? LangDefault(string ns, string name)
        {
            var v = xmp.Get(ns, name)?.Value;
            if (v == null) return null;
            if (v.Kind == XmpKind.Alt) return (v.Items.FirstOrDefault(i => i.Lang == "x-default") ?? v.Items.FirstOrDefault())?.Text;
            return v.Kind == XmpKind.Simple ? v.Text : null;
        }
        void Same(string key, string? xmpValue, string property)
        {
            string? value = Text(key);
            if (value == null) return;
            if (xmpValue == null) Fail("6.7.3", 1, $"The document information entry /{key} has no equivalent {property} in the metadata.");
            else if (value != xmpValue) Fail("6.7.3", 1, $"The document information entry /{key} differs from {property} in the metadata.");
        }
        Same("Title", LangDefault(XmpSchemas.Dc, "title"), "dc:title");
        Same("Author", xmp.Get(XmpSchemas.Dc, "creator")?.Value is { Kind: XmpKind.Seq } creators && creators.Items.Count == 1 ? creators.Items[0].Text : null, "dc:creator");
        Same("Subject", LangDefault(XmpSchemas.Dc, "description"), "dc:description");
        Same("Keywords", xmp.Simple(XmpSchemas.Pdf, "Keywords"), "pdf:Keywords");
        Same("Creator", xmp.Simple(XmpSchemas.Xmp, "CreatorTool"), "xmp:CreatorTool");
        Same("Producer", xmp.Simple(XmpSchemas.Pdf, "Producer"), "pdf:Producer");
        foreach (var (key, prop) in new[] { ("CreationDate", "CreateDate"), ("ModDate", "ModifyDate") })
        {
            string? pdfDate = Text(key);
            if (pdfDate == null) continue;
            string? xmpDate = xmp.Simple(XmpSchemas.Xmp, prop);
            if (xmpDate == null) { Fail("6.7.3", 1, $"The document information entry /{key} has no equivalent xmp:{prop} in the metadata."); continue; }
            if (!SameInstant(pdfDate, xmpDate)) Fail("6.7.3", 1, $"The document information entry /{key} differs from xmp:{prop} in the metadata.");
        }
    }

    internal static bool SameInstant(string pdfDate, string xmpDate)
    {
        var a = PdfDates.ParsePdf(pdfDate);
        var b = PdfDates.ParseXmp(xmpDate);
        return a != null && b != null && a.Value == b.Value;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Every indirect object in use, resolved (object streams included).</summary>
    private IEnumerable<(int Number, PdfObject Object)> AllObjects()
    {
        foreach (var (number, entry) in _doc.XrefTable.Entries.OrderBy(e => e.Key))
        {
            if (!entry.IsInUse || number == 0) continue;
            PdfObject? obj;
            try { obj = _r.Resolve(number); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (obj != null) yield return (number, obj);
        }
    }
}
