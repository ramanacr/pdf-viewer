using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Fonts.Programs;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Limits;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;
using PdfEngine.Vector.PdfA.Xmp;
using PdfEngine.Vector.Redaction;
using PdfEngine.Vector.Streams;

namespace PdfEngine.Vector.PdfA;

public sealed class PdfAConversionOptions
{
    /// <summary>PDF/A-2b (default) or PDF/A-3b. PDF/A-1 is not a target: it forbids transparency, which cannot be removed without changing the page.</summary>
    public PdfAFlavour Flavour { get; init; } = PdfAFlavour.A2b;
    /// <summary>Where fonts for embedding come from (the installed fonts by default).</summary>
    public SystemFontCatalog? Fonts { get; init; }
    /// <summary>A CMYK ICC profile for DeviceCMYK content (by default the one Windows installs, when present).</summary>
    public byte[]? CmykProfile { get; init; }
    /// <summary>Builds a normal appearance for an annotation that has none (drawn as Acrobat draws it); null removes such annotations instead.</summary>
    public Func<PdfDictionary, PdfObjectResolver, Func<PdfObject, int>, PdfStream?>? AnnotationAppearance { get; init; } = Annotations.PdfAnnotationAppearances.Generate;
}

public sealed class PdfAConversionResult
{
    public required byte[] Bytes { get; init; }
    /// <summary>What was changed to conform, each with how often.</summary>
    public required IReadOnlyList<string> Changes { get; init; }
    /// <summary>The result checked against the target: its remaining issues, if any, could not be fixed automatically.</summary>
    public required PdfAReport Report { get; init; }
}

/// <summary>
/// Converts a document to PDF/A-2b or -3b: a complete rewrite (no earlier revisions, no
/// encryption) that keeps every page's appearance. Fonts that are not embedded are embedded from
/// the installed fonts; an sRGB output intent (generated, not shipped) is added and device colour
/// is given default colour spaces; XMP metadata is rebuilt from the valid existing properties and
/// the document information; scripts, forbidden actions, external content, transfer functions and
/// similar are removed; annotations are made printable and get appearances; the result is then
/// validated, and whatever could not be fixed is reported.
/// </summary>
public static class PdfAConverter
{
    public static PdfAConversionResult Convert(PdfVectorDocument document, byte[] original, PdfAConversionOptions? options = null)
    {
        options ??= new PdfAConversionOptions();
        if (options.Flavour.Part is not (2 or 3)) throw new ArgumentException("PDF/A-2 and PDF/A-3 are the conversion targets.", nameof(options));

        // Form fields whose widgets have no appearance get one first, from their values.
        var changes = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[] bytes = original;
        var doc = document;
        PdfVectorDocument? regenerated = null;
        try
        {
            if (RegenerateWidgetAppearances(document, original) is { } withAppearances)
            {
                bytes = withAppearances.Bytes;
                regenerated = doc = PdfVectorDocument.Open(new MemoryByteSource(bytes), document.FilePath);
                changes[$"Generated appearances for {withAppearances.Count} form field widget(s)"] = 1;
            }
            var source = PdfAValidator.Validate(doc, bytes, options.Flavour);
            var conversion = new Conversion(doc, options, source, changes);
            byte[] output = conversion.Run();
            PdfAReport report;
            using (var result = PdfVectorDocument.Open(new MemoryByteSource(output), document.FilePath))
                report = PdfAValidator.Validate(result, output, options.Flavour);
            return new PdfAConversionResult
            {
                Bytes = output,
                Changes = changes.Select(c => c.Value > 1 ? $"{c.Key} ({c.Value} times)" : c.Key).ToList(),
                Report = report,
            };
        }
        finally
        {
            regenerated?.Dispose();
        }
    }

    private static (byte[] Bytes, int Count)? RegenerateWidgetAppearances(PdfVectorDocument doc, byte[] original)
    {
        PdfAcroForm? form;
        try { form = PdfAcroForm.Read(doc); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        if (form == null) return null;
        var r = doc.Resolver;
        var fields = new List<PdfFieldChange>();
        int widgets = 0;
        foreach (var field in form.Fields)
        {
            if (field.Kind is PdfFormFieldKind.PushButton or PdfFormFieldKind.Signature) continue;
            int missing = field.Widgets.Count(w => r.Resolve(w.ObjectNumber) is PdfDictionary a && r.Resolve(a["AP"]) is not PdfDictionary);
            if (missing == 0) continue;
            widgets += missing;
            fields.Add(field.Kind switch
            {
                PdfFormFieldKind.CheckBox => new PdfFieldChange(field.FullName, Checked: field.Value is { Length: > 0 } v && v != "Off"),
                PdfFormFieldKind.RadioGroup => new PdfFieldChange(field.FullName, Choice: field.Value),
                PdfFormFieldKind.ComboBox or PdfFormFieldKind.ListBox => new PdfFieldChange(field.FullName, Choice: field.Value),
                _ => new PdfFieldChange(field.FullName, Text: field.Value),
            });
        }
        if (fields.Count == 0) return null;
        try { return (PdfFormFiller.Apply(doc, original, fields), widgets); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private enum Role { Generic, Catalog, PageTree, Page, Resources, Annot, Action, Font, FontDescriptor, XObject, ExtGState, ColorSpace, AcroForm, Field, Metadata }

    private sealed class Conversion
    {
        private readonly PdfVectorDocument _doc;
        private readonly PdfObjectResolver _r;
        private readonly PdfAConversionOptions _options;
        private readonly PdfAReport _source;
        private readonly Dictionary<string, int> _changes;
        private readonly Dictionary<int, PdfObject> _overrides = new();
        private readonly HashSet<int> _visited = new();
        private readonly PdfStreamDecoder _decoder;
        private readonly SystemFontCatalog _fonts;
        private int _next;
        private PdfIndirectRef? _srgb, _cmyk;
        private bool _needDefaultRgb, _needDefaultCmyk, _cmykIntent;
        private readonly int _part;

        public Conversion(PdfVectorDocument doc, PdfAConversionOptions options, PdfAReport source, Dictionary<string, int> changes)
        {
            _doc = doc;
            _r = doc.Resolver;
            _options = options;
            _source = source;
            _changes = changes;
            _decoder = new PdfStreamDecoder(null, _r.Resolve);
            _fonts = options.Fonts ?? SystemFontCatalog.Installed;
            _next = PdfIncrementalWriter.NextObjectNumber(doc);
            _part = options.Flavour.Part;
        }

        private void Changed(string what) => _changes[what] = _changes.TryGetValue(what, out int n) ? n + 1 : 1;

        private int Add(PdfObject obj)
        {
            int n = _next++;
            _overrides[n] = obj;
            _visited.Add(n);
            return n;
        }

        public byte[] Run()
        {
            var trailer = _doc.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
            if (trailer["Root"] is not PdfIndirectRef rootRef) throw new InvalidOperationException("The document has no catalog.");
            if (_doc.IsEncrypted) Changed("Removed the encryption (PDF/A files cannot be encrypted)");
            PrepareColour();
            Visit(rootRef, Role.Catalog);
            if (trailer["Info"] is PdfIndirectRef infoRef) FixInfo(infoRef.ObjectNumber);
            return Write(rootRef);
        }

        // ------------------------------------------------------------------ colour

        /// <summary>Chooses the output intent and which device colour spaces need a default (ICC-based) space.</summary>
        private void PrepareColour()
        {
            bool Uses(string space) => _source.Issues.Any(i => i.Message.StartsWith(space + " is used", StringComparison.Ordinal));
            bool hasIntent = !_source.Issues.Any(i => i.Message.Contains("without an output intent", StringComparison.Ordinal)) && HasPdfaIntent();
            string? existing = hasIntent ? ExistingIntentSpace() : null;
            if (existing == null)
            {
                // CMYK content keeps being device CMYK (a CMYK output intent), so it looks as it did in
                // every viewer; RGB then gets sRGB, which draws the same as device RGB.
                var cmyk = Uses("DeviceCMYK") ? _options.CmykProfile ?? WindowsCmykProfile() : null;
                if (cmyk != null && PdfAChecker.IccHeader.Read(cmyk) is { Space: "CMYK", Class: "prtr" or "mntr" })
                {
                    _cmyk = new PdfIndirectRef(Add(IccStream(cmyk, 4)));
                    _cmykIntent = true;
                    _needDefaultRgb = Uses("DeviceRGB");
                    if (_needDefaultRgb) _srgb = new PdfIndirectRef(Add(IccStream(SrgbProfile.Bytes, 3)));
                    return;
                }
                _srgb = new PdfIndirectRef(Add(IccStream(SrgbProfile.Bytes, 3)));
                _needDefaultCmyk = Uses("DeviceCMYK");
            }
            else
            {
                _needDefaultRgb = existing == "CMYK" && Uses("DeviceRGB");
                _needDefaultCmyk = existing == "RGB " && Uses("DeviceCMYK");
                if (_needDefaultRgb) _srgb = new PdfIndirectRef(Add(IccStream(SrgbProfile.Bytes, 3)));
            }
            if (_needDefaultCmyk)
            {
                var cmyk = _options.CmykProfile ?? WindowsCmykProfile();
                if (cmyk != null && PdfAChecker.IccHeader.Read(cmyk) is { Space: "CMYK" }) _cmyk = new PdfIndirectRef(Add(IccStream(cmyk, 4)));
                else _needDefaultCmyk = false; // reported by the validation of the result
            }
        }

        private bool HasPdfaIntent() =>
            _r.Resolve(_r.Resolve(_doc.XrefTable.Trailer?["Root"]) is PdfDictionary c ? c["OutputIntents"] : null) is PdfArray a
            && a.Items.Select(_r.Resolve).OfType<PdfDictionary>().Any(i => i.GetName("S") == "GTS_PDFA1" && _r.Resolve(i["DestOutputProfile"]) is PdfStream);

        private string? ExistingIntentSpace()
        {
            if (_r.Resolve(_doc.XrefTable.Trailer?["Root"]) is not PdfDictionary c || _r.Resolve(c["OutputIntents"]) is not PdfArray a) return null;
            foreach (var i in a.Items.Select(_r.Resolve).OfType<PdfDictionary>())
                if (i.GetName("S") == "GTS_PDFA1" && _r.Resolve(i["DestOutputProfile"]) is PdfStream p && DecodeBytes(p) is { } d && PdfAChecker.IccHeader.Read(d) is { } h)
                    return h.Space;
            return null;
        }

        private static byte[]? WindowsCmykProfile()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm");
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static PdfStream IccStream(byte[] profile, int n) => PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
        {
            ["N"] = new PdfInteger(n), ["Filter"] = new PdfName("FlateDecode"),
        }, ContentRedactor.Deflate(profile));

        /// <summary>A device colour space name inside a colour space (an alternate, a base, an image's) as its ICC-based equivalent, when one is needed.</summary>
        private PdfObject IccFor(PdfObject cs)
        {
            if (cs is PdfName { Value: "DeviceRGB" } && _needDefaultRgb && _srgb != null) { Changed("Gave RGB colour an sRGB ICC profile"); return new PdfArray(new PdfObject[] { new PdfName("ICCBased"), _srgb }); }
            if (cs is PdfName { Value: "DeviceCMYK" } && _needDefaultCmyk && _cmyk != null) { Changed("Gave CMYK colour an ICC profile"); return new PdfArray(new PdfObject[] { new PdfName("ICCBased"), _cmyk }); }
            return cs;
        }

        // ------------------------------------------------------------------ traversal

        private byte[]? DecodeBytes(PdfStream s)
        {
            try { return _decoder.DecodeStream(s); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        }

        /// <summary>Fixes an indirect object once (its replacement goes into the overrides).</summary>
        private void Visit(PdfIndirectRef reference, Role role)
        {
            if (!_visited.Add(reference.ObjectNumber)) return;
            var obj = _r.Resolve(reference.ObjectNumber);
            if (obj == null) return;
            var fixedObj = Fix(obj, role);
            if (fixedObj == null) _overrides[reference.ObjectNumber] = PdfNull.Instance;
            else if (!ReferenceEquals(fixedObj, obj)) _overrides[reference.ObjectNumber] = fixedObj;
        }

        /// <summary>A value fixed for PDF/A in the role it plays; null removes it from its parent.</summary>
        private PdfObject? Fix(PdfObject? value, Role role)
        {
            switch (value)
            {
                case null: return null;
                case PdfIndirectRef reference:
                    Visit(reference, role);
                    return _overrides.TryGetValue(reference.ObjectNumber, out var o) && o is PdfNull && role is Role.Action or Role.Annot ? null : reference;
                case PdfArray array:
                    var items = new List<PdfObject>(array.Count);
                    bool changedArray = false;
                    foreach (var item in array)
                    {
                        var f = Fix(item, role is Role.Annot or Role.Action or Role.Field or Role.Font or Role.ColorSpace ? role : Role.Generic);
                        if (f == null) { changedArray = true; continue; }
                        changedArray |= !ReferenceEquals(f, item);
                        items.Add(f);
                    }
                    return changedArray ? new PdfArray(items) : array;
                case PdfStream stream:
                    return FixStream(stream, role);
                case PdfDictionary dict:
                    return FixDictionary(dict, role, isStream: false);
                default:
                    return value;
            }
        }

        private PdfObject? FixStream(PdfStream stream, Role role)
        {
            var d = stream.Dictionary;
            if (role == Role.XObject && (d.GetName("Subtype") == "PS" || d.GetName("Subtype2") == "PS" && false))
            {
                Changed("Replaced PostScript XObjects with empty forms (readers do not draw them)");
                return PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"),
                    ["BBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) }),
                }, Array.Empty<byte>());
            }
            if (role == Role.Metadata) return FixMetadataStream(stream);
            var fixedDict = (PdfDictionary)FixDictionary(d, role, isStream: true)!;
            byte[]? data = null;
            // Filters PDF/A does not allow (LZW, Crypt, non-standard ones): the data is stored decoded and Flate-compressed.
            var filters = Filters(d);
            if (filters.Any(f => f is not ("ASCIIHexDecode" or "ASCII85Decode" or "FlateDecode" or "RunLengthDecode" or "CCITTFaxDecode" or "JBIG2Decode" or "DCTDecode" or "JPXDecode"))
                && (data = DecodeBytes(stream)) != null)
            {
                var entries = new Dictionary<string, PdfObject>(fixedDict.Entries);
                entries.Remove("DecodeParms");
                entries["Filter"] = new PdfName("FlateDecode");
                Changed("Recompressed streams that used filters PDF/A does not allow (LZW and others)");
                return PdfObjectWriter.NewStream(entries, ContentRedactor.Deflate(data));
            }
            if (ReferenceEquals(fixedDict, d)) return stream;
            return stream with { Dictionary = fixedDict };
        }

        private List<string> Filters(PdfDictionary d) => _r.Resolve(d["Filter"]) switch
        {
            PdfName n => new List<string> { n.Value },
            PdfArray a => a.Items.Select(_r.Resolve).OfType<PdfName>().Select(n => n.Value).ToList(),
            _ => new List<string>(),
        };

        private PdfObject? FixDictionary(PdfDictionary d, Role role, bool isStream)
        {
            var e = new Dictionary<string, PdfObject>(d.Entries);
            bool changed = false;
            void Remove(string key, string why)
            {
                if (e.Remove(key)) { changed = true; Changed(why); }
            }

            // External stream data.
            if (isStream) foreach (var k in new[] { "F", "FFilter", "FDecodeParms" }) Remove(k, "Removed references to external stream data");

            switch (role)
            {
                case Role.Catalog:
                    Remove("AA", "Removed document-level actions (/AA)");
                    Remove("NeedsRendering", "Removed the XFA rendering request");
                    if (!e.ContainsKey("Type")) { e["Type"] = new PdfName("Catalog"); changed = true; }
                    FixCatalog(e);
                    changed = true;
                    break;
                case Role.Page:
                    Remove("AA", "Removed page actions (/AA)");
                    Remove("PresSteps", "Removed presentation steps");
                    break;
                case Role.Annot:
                    return FixAnnotation(d);
                case Role.Action:
                    return FixAction(d);
                case Role.Font:
                    // A replaced font (embedded, or with harmonized widths) still has its other parts fixed.
                    if (FixFont(d) is { } replacement) { d = replacement; e = new Dictionary<string, PdfObject>(d.Entries); changed = true; }
                    break;
                case Role.FontDescriptor:
                    Remove("CIDSet", "Removed font subset lists (CIDSet), which PDF/A-2 does not need");
                    Remove("CharSet", "Removed font subset lists (CharSet), which PDF/A-2 does not need");
                    break;
                case Role.ExtGState:
                    Remove("TR", "Removed transfer functions");
                    if (_r.Resolve(e.GetValueOrDefault("TR2")) is { } tr2 && tr2 is not PdfName { Value: "Default" }) Remove("TR2", "Removed transfer functions");
                    Remove("HTP", "Removed halftone phases");
                    if (_r.Resolve(e.GetValueOrDefault("RI")) is PdfName ri && ri.Value is not ("RelativeColorimetric" or "AbsoluteColorimetric" or "Perceptual" or "Saturation"))
                    { e["RI"] = new PdfName("RelativeColorimetric"); changed = true; Changed("Replaced non-standard rendering intents"); }
                    if (_r.Resolve(e.GetValueOrDefault("BM")) is PdfName bm && !StandardBlend(bm.Value))
                    { e["BM"] = new PdfName("Normal"); changed = true; Changed("Replaced non-standard blend modes"); }
                    break;
                case Role.XObject when isStream:
                    if (d.GetName("Subtype") == "Image")
                    {
                        Remove("Alternates", "Removed alternate images");
                        Remove("OPI", "Removed OPI references");
                        if (_r.Resolve(e.GetValueOrDefault("Interpolate")) is PdfBoolean { Value: true }) Remove("Interpolate", "Turned off image interpolation");
                        if (_r.Resolve(e.GetValueOrDefault("Intent")) is PdfName ii && ii.Value is not ("RelativeColorimetric" or "AbsoluteColorimetric" or "Perceptual" or "Saturation"))
                            Remove("Intent", "Replaced non-standard rendering intents");
                        if (e.TryGetValue("ColorSpace", out var ics) && _r.Resolve(ics) is PdfName) { var icc = IccFor(_r.Resolve(ics)!); if (!ReferenceEquals(icc, _r.Resolve(ics))) { e["ColorSpace"] = icc; changed = true; } }
                    }
                    else
                    {
                        Remove("OPI", "Removed OPI references");
                        Remove("PS", "Removed embedded PostScript");
                        Remove("Subtype2", "Removed embedded PostScript");
                        Remove("Ref", "Removed reference XObject links (the fallback content stays)");
                    }
                    break;
                case Role.AcroForm:
                    Remove("NeedAppearances", "Removed the form's request to regenerate appearances");
                    Remove("XFA", "Removed the XFA form data (the AcroForm fields stay)");
                    break;
                case Role.Field:
                    Remove("AA", "Removed form field actions (/AA)");
                    break;
            }

            foreach (var (key, value) in d.Entries)
            {
                if (!e.ContainsKey(key)) continue;
                var childRole = ChildRole(role, key, d);
                if (childRole == null) continue;
                PdfObject? fixedValue;
                if (childRole == Role.Resources && _r.Resolve(value) is PdfDictionary resources)
                    fixedValue = value is PdfIndirectRef rr ? FixResourcesRef(rr) : FixResources(resources);
                else if (key is "AA")
                {
                    // Additional actions are forbidden on the catalog, pages, annotations and fields; elsewhere they are fixed.
                    fixedValue = Fix(value, Role.Generic);
                }
                else fixedValue = Fix(value, childRole.Value);
                if (fixedValue == null) { e.Remove(key); changed = true; }
                else if (!ReferenceEquals(fixedValue, value)) { e[key] = fixedValue; changed = true; }
            }
            if (role == Role.Generic && _r.Resolve(e.GetValueOrDefault("S")) is PdfName { Value: "Transparency" } && e.TryGetValue("CS", out var gcs))
            {
                var icc = IccFor(_r.Resolve(gcs)!);
                if (!ReferenceEquals(icc, _r.Resolve(gcs))) { e["CS"] = icc; changed = true; }
            }
            return changed ? new PdfDictionary(e) : d;
        }

        private static bool StandardBlend(string bm) => bm is "Normal" or "Compatible" or "Multiply" or "Screen" or "Overlay" or "Darken" or "Lighten" or "ColorDodge"
            or "ColorBurn" or "HardLight" or "SoftLight" or "Difference" or "Exclusion" or "Hue" or "Saturation" or "Color" or "Luminosity";

        /// <summary>The role of a dictionary entry's value; null leaves it as it is (not followed).</summary>
        private static Role? ChildRole(Role parent, string key, PdfDictionary d)
        {
            switch (key)
            {
                case "Parent" or "P" or "Prev" or "First" or "Last" or "Dest" or "D" when parent is not Role.Generic: return null;
                case "Parent" or "P": return null;
                case "Resources" or "DR": return Role.Resources;
                case "A" or "OpenAction" or "Next": return parent == Role.Annot && key == "A" ? Role.Action : Role.Action;
                case "Metadata": return Role.Metadata;
                case "FontDescriptor": return Role.FontDescriptor;
                case "DescendantFonts": return Role.Font;
                case "Pages" when parent == Role.Catalog: return Role.PageTree;
                case "AcroForm" when parent == Role.Catalog: return Role.AcroForm;
                case "Fields" when parent == Role.AcroForm: return Role.Field;
                case "Kids" when parent == Role.PageTree: return Role.PageTree;
                case "Kids" when parent == Role.Field: return Role.Field;
                case "Annots": return Role.Annot;
                case "Popup": return Role.Annot;
                case "AP": return Role.Generic;
                case "N" or "R" when parent == Role.Generic: return Role.XObject;
                case "SMask" or "Mask" when parent == Role.XObject || parent == Role.ExtGState: return Role.XObject;
                case "G" when parent == Role.Generic: return Role.XObject;
                case "CharProcs": return Role.Generic;
                case "ColorSpace" when parent == Role.XObject: return null;
                default: return Role.Generic;
            }
        }

        /// <summary>Page tree nodes and pages are told apart by their /Type.</summary>
        private Role PageRole(PdfObject kid) => _r.Resolve(kid) is PdfDictionary k && k.GetName("Type") == "Page" ? Role.Page : Role.PageTree;

        private readonly Dictionary<int, PdfObject> _resourcesDone = new();

        private PdfObject FixResourcesRef(PdfIndirectRef rr)
        {
            if (!_visited.Add(rr.ObjectNumber)) return rr;
            if (_r.Resolve(rr.ObjectNumber) is PdfDictionary res)
            {
                var f = FixResources(res);
                if (!ReferenceEquals(f, res)) _overrides[rr.ObjectNumber] = f;
            }
            return rr;
        }

        /// <summary>Resources: every font, image, form, graphics state and colour space fixed; default colour spaces added where device colour needs them.</summary>
        private PdfDictionary FixResources(PdfDictionary res)
        {
            var e = new Dictionary<string, PdfObject>(res.Entries);
            bool changed = false;
            foreach (var (category, childRole) in new[] { ("Font", Role.Font), ("XObject", Role.XObject), ("ExtGState", Role.ExtGState), ("ColorSpace", Role.ColorSpace),
                         ("Pattern", Role.Generic), ("Shading", Role.Generic), ("Properties", Role.Generic) })
            {
                if (!e.TryGetValue(category, out var mapObj)) continue;
                var map = _r.Resolve(mapObj) as PdfDictionary;
                if (map == null) continue;
                var m = new Dictionary<string, PdfObject>(map.Entries);
                bool mapChanged = false;
                foreach (var (name, value) in map.Entries)
                {
                    var f = childRole == Role.ColorSpace ? FixColourSpace(value) : Fix(value, childRole);
                    if (f == null) { m.Remove(name); mapChanged = true; }
                    else if (!ReferenceEquals(f, value)) { m[name] = f; mapChanged = true; }
                }
                if (category == "ColorSpace" || (_needDefaultCmyk || _needDefaultRgb)) { }
                if (mapChanged)
                {
                    if (mapObj is PdfIndirectRef mr) _overrides[mr.ObjectNumber] = new PdfDictionary(m);
                    else { e[category] = new PdfDictionary(m); changed = true; }
                }
            }
            // Default colour spaces: device colour in this content is then ICC-based.
            if ((_needDefaultCmyk && _cmyk != null) || (_needDefaultRgb && _srgb != null))
            {
                var cs = new Dictionary<string, PdfObject>((_r.Resolve(e.GetValueOrDefault("ColorSpace")) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
                if (_needDefaultCmyk && _cmyk != null && !cs.ContainsKey("DefaultCMYK")) { cs["DefaultCMYK"] = new PdfArray(new PdfObject[] { new PdfName("ICCBased"), _cmyk }); Changed("Gave CMYK colour an ICC profile"); }
                if (_needDefaultRgb && _srgb != null && !cs.ContainsKey("DefaultRGB")) { cs["DefaultRGB"] = new PdfArray(new PdfObject[] { new PdfName("ICCBased"), _srgb }); Changed("Gave RGB colour an sRGB ICC profile"); }
                if (e.GetValueOrDefault("ColorSpace") is PdfIndirectRef csRef) _overrides[csRef.ObjectNumber] = new PdfDictionary(cs);
                else e["ColorSpace"] = new PdfDictionary(cs);
                changed = true;
            }
            return changed ? new PdfDictionary(e) : res;
        }

        /// <summary>Alternates and bases of colour spaces that are device spaces get their ICC-based equivalent.</summary>
        private PdfObject FixColourSpace(PdfObject value)
        {
            var cs = _r.Resolve(value);
            if (cs is not PdfArray a || a.Count < 2 || _r.Resolve(a[0]) is not PdfName family) return value;
            int slot = family.Value switch { "Separation" or "DeviceN" => 2, "Indexed" or "I" => 1, _ => -1 };
            if (slot < 0 || slot >= a.Count) return value;
            var inner = _r.Resolve(a[slot])!;
            var fixedInner = inner is PdfName ? IccFor(inner) : FixColourSpace(a[slot]);
            if (ReferenceEquals(fixedInner, inner) || ReferenceEquals(fixedInner, a[slot])) return value;
            var items = a.Items.ToList();
            items[slot] = fixedInner;
            var fixedArray = new PdfArray(items);
            if (value is PdfIndirectRef r) { _overrides[r.ObjectNumber] = fixedArray; _visited.Add(r.ObjectNumber); return r; }
            return fixedArray;
        }

        // ------------------------------------------------------------------ catalog

        private void FixCatalog(Dictionary<string, PdfObject> e)
        {
            // Output intent.
            if ((_cmykIntent ? _cmyk : _srgb) is { } profile && !HasPdfaIntent())
            {
                string condition = _cmykIntent ? "CGATS TR 001 (SWOP)" : "sRGB IEC61966-2.1";
                var intent = new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("OutputIntent"), ["S"] = new PdfName("GTS_PDFA1"),
                    ["OutputConditionIdentifier"] = PdfObjectWriter.TextString(_cmykIntent ? "CGATS TR 001" : "sRGB IEC61966-2.1"), ["RegistryName"] = PdfObjectWriter.TextString("http://www.color.org"),
                    ["Info"] = PdfObjectWriter.TextString(condition), ["DestOutputProfile"] = profile,
                });
                var intents = (_r.Resolve(e.GetValueOrDefault("OutputIntents")) as PdfArray)?.Items.Where(i => (_r.Resolve(i) as PdfDictionary)?.GetName("S") != "GTS_PDFA1").ToList() ?? new List<PdfObject>();
                intents.Add(new PdfIndirectRef(Add(intent)));
                e["OutputIntents"] = new PdfArray(intents);
                Changed(_cmykIntent ? "Added a CMYK (SWOP) output intent, so CMYK colour stays as it is" : "Added an sRGB output intent");
            }
            // Names: no document JavaScript; embedded files only as PDF/A allows.
            if (_r.Resolve(e.GetValueOrDefault("Names")) is PdfDictionary names)
            {
                var n = new Dictionary<string, PdfObject>(names.Entries);
                if (n.Remove("JavaScript")) Changed("Removed document JavaScript");
                if (_part == 2 && n.Remove("EmbeddedFiles")) Changed("Removed embedded files (PDF/A-2 allows only PDF/A attachments; PDF/A-3 keeps them)");
                e["Names"] = new PdfDictionary(n);
            }
            if (_part == 3) AssociateEmbeddedFiles(e);
            if (_r.Resolve(e.GetValueOrDefault("Perms")) is PdfDictionary perms)
            {
                var p = perms.Entries.Where(kv => kv.Key is "UR3" or "DocMDP").ToDictionary(kv => kv.Key, kv => kv.Value);
                if (p.Count != perms.Count) { e["Perms"] = new PdfDictionary(p); Changed("Removed permissions other than UR3 and DocMDP"); }
            }
            if (_r.Resolve(e.GetValueOrDefault("OCProperties")) is PdfDictionary oc) e["OCProperties"] = FixOptionalContent(oc);
            // Metadata.
            var info = _r.Resolve(_doc.XrefTable.Trailer?["Info"]) as PdfDictionary;
            var old = _r.Resolve(e.GetValueOrDefault("Metadata")) is PdfStream ms && DecodeBytes(ms) is { } md ? XmpPacket.Parse(md) : null;
            var packet = BuildMetadata(old, info);
            e["Metadata"] = new PdfIndirectRef(Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Metadata"), ["Subtype"] = new PdfName("XML"),
            }, packet)));
            Changed($"Wrote PDF/A-{_part}B identification and metadata (XMP)");
        }

        private PdfDictionary FixOptionalContent(PdfDictionary oc)
        {
            var e = new Dictionary<string, PdfObject>(oc.Entries);
            var names = new HashSet<string>(StringComparer.Ordinal);
            PdfDictionary FixConfig(PdfDictionary c, int index)
            {
                var ce = new Dictionary<string, PdfObject>(c.Entries);
                string name = _r.Resolve(ce.GetValueOrDefault("Name")) is PdfString s ? s.AsDecodedString() : string.Empty;
                if (name.Length == 0 || !names.Add(name))
                {
                    name = index == 0 ? "Default" : $"Configuration {index}";
                    while (!names.Add(name)) name += "+";
                    ce["Name"] = PdfObjectWriter.TextString(name);
                    Changed("Named optional content configurations");
                }
                if (ce.Remove("AS")) Changed("Removed automatic optional content states (/AS)");
                return new PdfDictionary(ce);
            }
            if (_r.Resolve(e.GetValueOrDefault("D")) is PdfDictionary d) e["D"] = FixConfig(d, 0);
            if (_r.Resolve(e.GetValueOrDefault("Configs")) is PdfArray configs)
                e["Configs"] = new PdfArray(configs.Items.Select(_r.Resolve).OfType<PdfDictionary>().Select((c, i) => (PdfObject)FixConfig(c, i + 1)).ToList());
            if (_r.Resolve(e.GetValueOrDefault("OCGs")) is PdfArray groups)
                foreach (var g in groups.OfType<PdfIndirectRef>())
                    if (_r.Resolve(g) is PdfDictionary group && _r.Resolve(group["Name"]) is not PdfString)
                    {
                        _overrides[g.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(group.Entries) { ["Name"] = PdfObjectWriter.TextString($"Layer {g.ObjectNumber}") });
                        _visited.Add(g.ObjectNumber);
                        Changed("Named optional content groups");
                    }
            // Every group appears in the default configuration's /Order.
            if (_r.Resolve(e.GetValueOrDefault("OCGs")) is PdfArray all && e.GetValueOrDefault("D") is PdfDictionary dc && _r.Resolve(dc["Order"]) is PdfArray order)
            {
                var listed = new HashSet<int>();
                void Walk(PdfArray a, int depth)
                {
                    if (depth > 16) return;
                    foreach (var i in a)
                    {
                        if (i is PdfIndirectRef r) listed.Add(r.ObjectNumber);
                        if (_r.Resolve(i) is PdfArray nested) Walk(nested, depth + 1);
                    }
                }
                Walk(order, 0);
                var missing = all.OfType<PdfIndirectRef>().Where(g => !listed.Contains(g.ObjectNumber)).ToList();
                if (missing.Count > 0)
                {
                    e["D"] = new PdfDictionary(new Dictionary<string, PdfObject>(dc.Entries) { ["Order"] = new PdfArray(order.Items.Concat(missing).ToList()) });
                    Changed("Listed every optional content group in the layer order");
                }
            }
            return new PdfDictionary(e);
        }

        /// <summary>PDF/A-3: every embedded file is associated with the document, with a relationship, a MIME type and a date.</summary>
        private void AssociateEmbeddedFiles(Dictionary<string, PdfObject> catalog)
        {
            var af = (_r.Resolve(catalog.GetValueOrDefault("AF")) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
            var associated = af.OfType<PdfIndirectRef>().Select(r => r.ObjectNumber).ToHashSet();
            foreach (var (number, entry) in _doc.XrefTable.Entries)
            {
                if (!entry.IsInUse || _r.Resolve(number) is not PdfDictionary spec || !spec.ContainsKey("EF")) continue;
                var s = new Dictionary<string, PdfObject>(spec.Entries);
                if (!s.ContainsKey("AFRelationship")) { s["AFRelationship"] = new PdfName("Unspecified"); Changed("Gave embedded files a relationship to the document"); }
                if (!s.ContainsKey("UF") && s.TryGetValue("F", out var f)) s["UF"] = f;
                if (!s.ContainsKey("F") && s.TryGetValue("UF", out var uf)) s["F"] = uf;
                if (_r.Resolve(s["EF"]) is PdfDictionary ef && ef["F"] is PdfIndirectRef fileRef && _r.Resolve(fileRef) is PdfStream file)
                {
                    var fd = new Dictionary<string, PdfObject>(file.Dictionary.Entries);
                    bool fileChanged = false;
                    if (!fd.ContainsKey("Subtype")) { fd["Subtype"] = new PdfName("application/octet-stream"); fileChanged = true; }
                    var p = new Dictionary<string, PdfObject>((_r.Resolve(fd.GetValueOrDefault("Params")) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
                    if (!p.ContainsKey("ModDate")) { p["ModDate"] = PdfObjectWriter.TextString(PdfDate(DateTimeOffset.Now)); fd["Params"] = new PdfDictionary(p); fileChanged = true; }
                    if (fileChanged) { _overrides[fileRef.ObjectNumber] = file with { Dictionary = new PdfDictionary(fd) }; _visited.Add(fileRef.ObjectNumber); Changed("Gave embedded files a MIME type and date"); }
                }
                _overrides[number] = new PdfDictionary(s);
                if (!associated.Contains(number)) { af.Add(new PdfIndirectRef(number)); associated.Add(number); Changed("Associated embedded files with the document (/AF)"); }
            }
            if (af.Count > 0) catalog["AF"] = new PdfArray(af);
        }

        // ------------------------------------------------------------------ metadata

        private byte[] BuildMetadata(XmpPacket? old, PdfDictionary? info)
        {
            var props = new List<XmpProperty>();
            string? Info(string key) => info != null && _r.Resolve(info[key]) is PdfString s ? s.AsDecodedString() : null;
            // What the old packet has that is valid PDF/A metadata is kept.
            if (old != null && old.Error == null)
            {
                foreach (var p in old.Properties)
                {
                    if (p.Namespace is XmpSchemas.PdfaId || p.Namespace == XmpPacket.RdfNs) continue;
                    if (!XmpSchemas.Schemas.TryGetValue(p.Namespace, out var schema) || !schema.TryGetValue(p.Name, out var type)) { Changed("Dropped metadata properties PDF/A does not define"); continue; }
                    if (XmpSchemas.Check(p.Value, type, XmpSchemas.Predefined) != null) { Changed("Dropped metadata properties with invalid values"); continue; }
                    props.Add(p);
                }
            }
            bool Has(string ns, string name) => props.Any(p => p.Namespace == ns && p.Name == name);
            void Set(string ns, string prefix, string name, XmpValue value)
            {
                props.RemoveAll(p => p.Namespace == ns && p.Name == name);
                props.Add(new XmpProperty(ns, prefix, name, value));
            }
            if (Info("Title") is { Length: > 0 } title && !Has(XmpSchemas.Dc, "title")) Set(XmpSchemas.Dc, "dc", "title", XmpWriter.LangAlt(title));
            if (Info("Author") is { Length: > 0 } author && !Has(XmpSchemas.Dc, "creator")) Set(XmpSchemas.Dc, "dc", "creator", XmpWriter.Array(XmpKind.Seq, new[] { author }));
            if (Info("Subject") is { Length: > 0 } subject && !Has(XmpSchemas.Dc, "description")) Set(XmpSchemas.Dc, "dc", "description", XmpWriter.LangAlt(subject));
            if (Info("Keywords") is { Length: > 0 } keywords && !Has(XmpSchemas.Pdf, "Keywords")) Set(XmpSchemas.Pdf, "pdf", "Keywords", XmpWriter.Simple(keywords));
            if (Info("Creator") is { Length: > 0 } creator && !Has(XmpSchemas.Xmp, "CreatorTool")) Set(XmpSchemas.Xmp, "xmp", "CreatorTool", XmpWriter.Simple(creator));
            if (Info("Producer") is { Length: > 0 } producer && !Has(XmpSchemas.Pdf, "Producer")) Set(XmpSchemas.Pdf, "pdf", "Producer", XmpWriter.Simple(producer));
            if (Info("CreationDate") is { } cd && PdfDates.ParsePdf(cd) is { } created && !Has(XmpSchemas.Xmp, "CreateDate"))
                Set(XmpSchemas.Xmp, "xmp", "CreateDate", XmpWriter.Simple(XmpDate(created)));
            string now = XmpDate(DateTimeOffset.Now);
            Set(XmpSchemas.Xmp, "xmp", "ModifyDate", XmpWriter.Simple(now));
            Set(XmpSchemas.Xmp, "xmp", "MetadataDate", XmpWriter.Simple(now));
            Set(XmpSchemas.PdfaId, "pdfaid", "part", XmpWriter.Simple(_part.ToString(CultureInfo.InvariantCulture)));
            Set(XmpSchemas.PdfaId, "pdfaid", "conformance", XmpWriter.Simple("B"));
            _modifyDate = PdfDate(DateTimeOffset.Now);
            return XmpWriter.Write(props);
        }

        private string? _modifyDate;

        private static string XmpDate(DateTimeOffset d) => d.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        private static string PdfDate(DateTimeOffset d)
        {
            var o = d.Offset;
            string sign = o < TimeSpan.Zero ? "-" : "+";
            return $"D:{d:yyyyMMddHHmmss}{sign}{Math.Abs(o.Hours):00}'{Math.Abs(o.Minutes):00}'";
        }

        private PdfObject? FixMetadataStream(PdfStream stream)
        {
            // Metadata of pages, images and fonts is kept when it is valid XMP, dropped otherwise.
            if (DecodeBytes(stream) is { } data && XmpPacket.Parse(data).Error == null) return stream;
            Changed("Removed metadata streams that are not valid XMP");
            return null;
        }

        private void FixInfo(int number)
        {
            if (_r.Resolve(number) is not PdfDictionary info) return;
            var e = new Dictionary<string, PdfObject>();
            foreach (var (k, v) in info.Entries)
                if (_r.Resolve(v) is PdfString s) e[k] = s; // only text entries; references to other objects are dropped
            if (_modifyDate != null) e["ModDate"] = PdfObjectWriter.TextString(_modifyDate);
            _overrides[number] = new PdfDictionary(e);
        }

        // ------------------------------------------------------------------ annotations and actions

        private PdfObject? FixAnnotation(PdfDictionary a)
        {
            string? subtype = a.GetName("Subtype");
            if (subtype is "Sound" or "Movie" or "Screen" or "3D" or "RichMedia" or null)
            {
                Changed($"Removed {subtype ?? "unknown"} annotations (PDF/A does not allow them)");
                return null;
            }
            var e = new Dictionary<string, PdfObject>(a.Entries);
            long flags = _r.Resolve(e.GetValueOrDefault("F")) is PdfInteger f ? f.Value : 0;
            if (subtype != "Popup" && (flags & (1 | 2 | 32 | 256)) != 0 && subtype != "Widget")
            {
                Changed("Removed hidden annotations (PDF/A requires every annotation to be visible)");
                return null;
            }
            if (subtype != "Popup" && ((flags & 4) == 0 || (flags & (1 | 2 | 32 | 256)) != 0))
            {
                e["F"] = new PdfInteger((flags | 4) & ~(1L | 2 | 32 | 256));
                Changed("Made annotations printable");
            }
            if (e.Remove("AA")) Changed("Removed annotation actions (/AA)");
            if (subtype == "Widget" && e.Remove("A")) Changed("Removed widget actions");
            var rect = _r.Resolve(e.GetValueOrDefault("Rect")) as PdfArray;
            bool zeroArea = rect != null && rect.Count >= 4 && Math.Abs(Num(rect[2]) - Num(rect[0])) < 1e-9 && Math.Abs(Num(rect[3]) - Num(rect[1])) < 1e-9;
            if (subtype is not ("Popup" or "Link") && !zeroArea)
            {
                if (_r.Resolve(e.GetValueOrDefault("AP")) is not PdfDictionary ap)
                {
                    var generated = _options.AnnotationAppearance?.Invoke(a, _r, Add);
                    if (generated == null)
                    {
                        Changed($"Removed /{subtype} annotations without an appearance");
                        return null;
                    }
                    e["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(Add(generated)) });
                    Changed($"Generated appearances for /{subtype} annotations");
                }
                else if (ap.Count != 1 || !ap.ContainsKey("N"))
                {
                    e["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = ap["N"] ?? PdfNull.Instance });
                    Changed("Kept only the normal appearance of annotations");
                }
            }
            var fixedDict = (PdfDictionary)FixDictionary(new PdfDictionary(e), Role.Generic, false)!;
            return fixedDict;
        }

        private static double Num(PdfObject? o) => o != null && o.TryGetNumber(out double v) ? v : 0;

        private PdfObject? FixAction(PdfDictionary action)
        {
            string? s = action.GetName("S");
            bool forbidden = s is "Launch" or "Sound" or "Movie" or "ResetForm" or "ImportData" or "Hide" or "SetOCGState" or "Rendition" or "Trans" or "GoTo3DView" or "JavaScript" or "SetState" or "NoOp"
                             || (s == "Named" && action.GetName("N") is not ("NextPage" or "PrevPage" or "FirstPage" or "LastPage"));
            if (forbidden)
            {
                Changed($"Removed {s} actions");
                return action["Next"] is { } next ? Fix(next is PdfArray na && na.Count > 0 ? na[0] : next, Role.Action) : null;
            }
            return FixDictionary(action, Role.Generic, false);
        }

        // ------------------------------------------------------------------ fonts

        private PdfFontResolver? _fontResolver;

        /// <summary>A simple font that is not embedded, embedded from the installed fonts (layout kept: its widths stay). Null leaves the font as it is.</summary>
        private PdfDictionary? FixFont(PdfDictionary font)
        {
            string? subtype = font.GetName("Subtype");
            if (subtype == "Type0") return FixType0(font);
            if (subtype == "CIDFontType2") return FixCidFont(font);
            if (subtype is not ("Type1" or "TrueType" or "MMType1")) return null;
            var descriptor = _r.Resolve(font["FontDescriptor"]) as PdfDictionary;
            if (descriptor != null && descriptor.ContainsKey("FontFile2") && subtype == "TrueType") return HarmonizeSimple(font, descriptor);
            if (descriptor != null && (descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3"))) return null;
            PdfFont pdfFont;
            try { pdfFont = (_fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default)).ResolveFont(font, "F"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            string baseFont = SystemFontCatalog.StripSubset(font.GetName("BaseFont") ?? "Helvetica");
            string? standard = Standard14Fonts.NormalizeName(baseFont);
            bool symbol = standard == "Symbol";
            if (standard == "ZapfDingbats") return null; // no installed equivalent; reported by the validation
            var face = symbol ? _fonts.FindFamily("Symbol", false, false) : _fonts.Match(baseFont, pdfFont.IsBold, pdfFont.IsItalic, pdfFont.IsSerif, pdfFont.IsFixedPitch);
            if (face == null || _fonts.Load(face) is not { } file) return null;

            int first = (int)(font.GetInteger("FirstChar") ?? 32), last = (int)(font.GetInteger("LastChar") ?? 255);
            first = Math.Clamp(first, 0, 255);
            last = Math.Clamp(last, first, 255);
            var glyphs = new List<int>();
            var widths = new List<PdfObject>();
            var differences = new List<PdfObject>();
            int? runStart = null;
            for (int code = first; code <= last; code++)
            {
                string? name = pdfFont.GetGlyphName(code);
                int gid = symbol ? file.GlyphFor(code) : name != null && GlyphList.ToUnicode(name) is { } u ? file.GlyphFor(char.ConvertToUtf32(u, 0)) : 0;
                if (gid > 0) glyphs.Add(gid);
                double w = pdfFont.GetGlyphWidth(code);
                if (w <= 0 && gid > 0) w = file.Advance(gid);
                widths.Add(new PdfReal(Math.Round(w, 3)));
                // The encoding written as WinAnsi plus differences, so non-symbolic codes keep their glyphs.
                if (!symbol)
                {
                    string? winAnsi = code < FontEncodings.WinAnsi.Length ? FontEncodings.WinAnsi[code] : null;
                    if (name != null && name != winAnsi && GlyphList.ToUnicode(name) != null)
                    {
                        if (runStart != code - 1 || differences.Count == 0) differences.Add(new PdfInteger(code));
                        differences.Add(new PdfName(name));
                        runStart = code;
                    }
                }
            }
            byte[] program = file.Subset(glyphs);
            // A stand-in font keeps the document's spacing: its program is given the document's widths.
            var advances = new Dictionary<int, int>();
            for (int code = first; code <= last; code++)
            {
                string? name = pdfFont.GetGlyphName(code);
                int gid = symbol ? file.GlyphFor(code) : name != null && GlyphList.ToUnicode(name) is { } u ? file.GlyphFor(char.ConvertToUtf32(u, 0)) : 0;
                if (gid > 0 && !advances.ContainsKey(gid) && widths[code - first] is PdfReal w) advances[gid] = (int)Math.Round(w.Value * file.UnitsPerEm / 1000.0);
            }
            if (advances.Count > 0 && TrueTypeFontFile.WithAdvances(program, advances) is { } spaced) program = spaced;
            string tag = SubsetTag(file.PostScriptName, glyphs);
            string embeddedName = tag + "+" + file.PostScriptName;
            int fileNumber = Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Filter"] = new PdfName("FlateDecode"), ["Length1"] = new PdfInteger(program.Length),
            }, ContentRedactor.Deflate(program)));
            double k = 1000.0 / file.UnitsPerEm;
            int flags = (file.IsFixedPitch ? 1 : 0) | (file.IsSerif ? 2 : 0) | (symbol ? 4 : 32) | (file.IsItalic ? 64 : 0);
            int descriptorNumber = Add(new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("FontDescriptor"), ["FontName"] = new PdfName(embeddedName), ["Flags"] = new PdfInteger(flags),
                ["FontBBox"] = new PdfArray(new PdfObject[]
                {
                    new PdfInteger((long)Math.Round(file.BBox.XMin * k)), new PdfInteger((long)Math.Round(file.BBox.YMin * k)),
                    new PdfInteger((long)Math.Round(file.BBox.XMax * k)), new PdfInteger((long)Math.Round(file.BBox.YMax * k)),
                }),
                ["ItalicAngle"] = new PdfReal(file.ItalicAngle), ["Ascent"] = new PdfInteger((long)Math.Round(file.Ascender * k)),
                ["Descent"] = new PdfInteger((long)Math.Round(file.Descender * k)), ["CapHeight"] = new PdfInteger((long)Math.Round(file.CapHeight * k)),
                ["StemV"] = new PdfInteger(file.IsBold ? 140 : 80), ["FontFile2"] = new PdfIndirectRef(fileNumber),
            }));
            var e = new Dictionary<string, PdfObject>(font.Entries)
            {
                ["Subtype"] = new PdfName("TrueType"), ["BaseFont"] = new PdfName(embeddedName),
                ["FirstChar"] = new PdfInteger(first), ["LastChar"] = new PdfInteger(last), ["Widths"] = new PdfArray(widths),
                ["FontDescriptor"] = new PdfIndirectRef(descriptorNumber),
            };
            if (symbol) e.Remove("Encoding");
            else e["Encoding"] = differences.Count == 0 ? new PdfName("WinAnsiEncoding")
                : new PdfDictionary(new Dictionary<string, PdfObject> { ["Type"] = new PdfName("Encoding"), ["BaseEncoding"] = new PdfName("WinAnsiEncoding"), ["Differences"] = new PdfArray(differences) });
            Changed($"Embedded {face.Family}{(face.Subfamily is "Regular" ? "" : " " + face.Subfamily)} for the font {baseFont}");
            return new PdfDictionary(e);
        }

        /// <summary>
        /// An embedded TrueType font whose program's advance widths disagree with the widths the
        /// document gives gets a program with the document's widths (what readers use to space the
        /// text), so nothing moves and the two agree.
        /// </summary>
        private PdfDictionary? HarmonizeSimple(PdfDictionary font, PdfDictionary descriptor)
        {
            PdfFont pdfFont;
            try { pdfFont = (_fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default)).ResolveFont(font, "F"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            if (pdfFont.EmbeddedFontData is not { } data || TrueTypeFontFile.TryLoad(data) is not { } file || FontProgramPreparer.Prepare(pdfFont) is not { } prepared) return null;
            // Several codes can show one glyph; a glyph changes only when no code already agrees with it.
            var wanted = new Dictionary<int, double?>();
            int first = pdfFont.FirstChar, last = pdfFont.LastChar;
            for (int code = Math.Max(0, first); code <= Math.Min(255, last); code++)
            {
                int gid = prepared.GetGlyphId(code, code);
                if (gid <= 0 || gid >= file.GlyphCount) continue;
                double declared = pdfFont.GetGlyphWidth(code);
                if (Math.Abs(declared - file.Advance(gid)) <= 1) wanted[gid] = null;
                else if (!wanted.ContainsKey(gid)) wanted[gid] = declared;
            }
            var advances = wanted.Where(w => w.Value != null).ToDictionary(w => w.Key, w => (int)Math.Round(w.Value!.Value * file.UnitsPerEm / 1000.0));
            if (advances.Count == 0 || TrueTypeFontFile.WithAdvances(data, advances) is not { } program) return null;
            return WithProgram(font, descriptor, "FontFile", program);
        }

        /// <summary>A CIDFontType2: an explicit identity CIDToGIDMap where it has none, and program widths made to agree with /W.</summary>
        private PdfDictionary? FixCidFont(PdfDictionary font)
        {
            var e = new Dictionary<string, PdfObject>(font.Entries);
            bool changed = false;
            var descriptor = _r.Resolve(font["FontDescriptor"]) as PdfDictionary;
            if (!e.ContainsKey("CIDToGIDMap") && descriptor?.ContainsKey("FontFile2") == true)
            {
                e["CIDToGIDMap"] = new PdfName("Identity");
                changed = true;
                Changed("Gave embedded CID fonts an explicit /CIDToGIDMap (the identity they already used)");
            }
            if (descriptor?["FontFile2"] is PdfIndirectRef && _r.Resolve(descriptor["FontFile2"]) is PdfStream fs && Decode(fs) is { } data && TrueTypeFontFile.TryLoad(data) is { } file)
            {
                byte[]? map = _r.Resolve(font["CIDToGIDMap"]) is PdfStream ms ? Decode(ms) : null;
                int Gid(int cid) => map == null ? cid : cid * 2 + 1 < map.Length ? (map[cid * 2] << 8) | map[cid * 2 + 1] : 0;
                var advances = new Dictionary<int, int>();
                double dw = _r.Resolve(font["DW"]) is PdfObject d && d.TryGetNumber(out double dv) ? dv : 1000;
                if (_r.Resolve(font["W"]) is PdfArray w)
                    for (int i = 0; i + 1 < w.Count;)
                    {
                        if (!_r.Resolve(w[i])!.TryGetNumber(out double startD)) break;
                        int start = (int)startD;
                        if (_r.Resolve(w[i + 1]) is PdfArray list)
                        {
                            for (int k = 0; k < list.Count; k++) Record(start + k, _r.Resolve(list[k]));
                            i += 2;
                        }
                        else if (i + 2 < w.Count && _r.Resolve(w[i + 1])!.TryGetNumber(out double endD))
                        {
                            for (int c = start; c <= (int)endD && c - start < 65536; c++) Record(c, _r.Resolve(w[i + 2]));
                            i += 3;
                        }
                        else break;
                    }
                void Record(int cid, PdfObject? width)
                {
                    int gid = Gid(cid);
                    if (gid <= 0 || gid >= file.GlyphCount || width == null || !width.TryGetNumber(out double wv) || advances.ContainsKey(gid)) return;
                    if (Math.Abs(wv - file.Advance(gid)) > 1) advances[gid] = (int)Math.Round(wv * file.UnitsPerEm / 1000.0);
                }
                _ = dw;
                if (advances.Count > 0 && TrueTypeFontFile.WithAdvances(data, advances) is { } program)
                    return WithProgram(changed ? new PdfDictionary(e) : font, descriptor, "FontFile2", program);
            }
            return changed ? new PdfDictionary(e) : null;
        }

        private byte[]? Decode(PdfStream s) => DecodeBytes(s);

        /// <summary>
        /// A composite font over an embedded CIDFontType2: the program's widths made to agree with
        /// the font's (from /W and /DW, as readers use them), for every glyph a CID reaches.
        /// </summary>
        private PdfDictionary? FixType0(PdfDictionary type0)
        {
            if (_r.Resolve(type0["DescendantFonts"]) is not PdfArray { Count: > 0 } kids || _r.Resolve(kids[0]) is not PdfDictionary cid) return null;
            if (cid.GetName("Subtype") != "CIDFontType2" || _r.Resolve(cid["FontDescriptor"]) is not PdfDictionary descriptor) return null;
            if (_r.Resolve(descriptor["FontFile2"]) is not PdfStream fs || Decode(fs) is not { } data || TrueTypeFontFile.TryLoad(data) is not { } file) return null;
            PdfFont pdfFont;
            try { pdfFont = (_fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default)).ResolveFont(type0, "F"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            byte[]? map = _r.Resolve(cid["CIDToGIDMap"]) is PdfStream ms ? Decode(ms) : null;
            var wanted = new Dictionary<int, double?>();
            int cids = map != null ? map.Length / 2 : file.GlyphCount;
            for (int c = 1; c < cids && c < 65536; c++)
            {
                int gid = map == null ? c : (map[c * 2] << 8) | map[c * 2 + 1];
                if (gid <= 0 || gid >= file.GlyphCount) continue;
                double declared = pdfFont.GetGlyphWidth(c);
                if (Math.Abs(declared - file.Advance(gid)) <= 1) wanted[gid] = null;
                else if (!wanted.ContainsKey(gid)) wanted[gid] = declared;
            }
            var advances = wanted.Where(w => w.Value != null).ToDictionary(w => w.Key, w => (int)Math.Round(w.Value!.Value * file.UnitsPerEm / 1000.0));
            bool needMap = cid["CIDToGIDMap"] == null;
            if (advances.Count == 0 && !needMap) return null;
            var ce = new Dictionary<string, PdfObject>(cid.Entries);
            if (needMap) { ce["CIDToGIDMap"] = new PdfName("Identity"); Changed("Gave embedded CID fonts an explicit /CIDToGIDMap (the identity they already used)"); }
            var newCid = advances.Count > 0 && TrueTypeFontFile.WithAdvances(data, advances) is { } program
                ? WithProgram(new PdfDictionary(ce), descriptor, "FontFile2", program)
                : new PdfDictionary(ce);
            // The new descendant still needs its descriptor fixed (subset lists).
            var fixedCid = (PdfDictionary)FixDictionary(newCid, Role.Generic, false)!;
            int number = Add(fixedCid);
            return new PdfDictionary(new Dictionary<string, PdfObject>(type0.Entries) { ["DescendantFonts"] = new PdfArray(new PdfObject[] { new PdfIndirectRef(number) }) });
        }

        private PdfDictionary WithProgram(PdfDictionary font, PdfDictionary descriptor, string _, byte[] program)
        {
            int fileNumber = Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Filter"] = new PdfName("FlateDecode"), ["Length1"] = new PdfInteger(program.Length),
            }, ContentRedactor.Deflate(program)));
            var d = new Dictionary<string, PdfObject>(descriptor.Entries) { ["FontFile2"] = new PdfIndirectRef(fileNumber) };
            d.Remove("CIDSet");
            d.Remove("CharSet");
            int descriptorNumber = Add(new PdfDictionary(d));
            Changed("Made embedded fonts' glyph widths agree with the widths the document gives");
            return new PdfDictionary(new Dictionary<string, PdfObject>(font.Entries) { ["FontDescriptor"] = new PdfIndirectRef(descriptorNumber) });
        }

        private static string SubsetTag(string name, IEnumerable<int> glyphs)
        {
            uint h = 2166136261;
            foreach (char ch in name) h = (h ^ ch) * 16777619;
            foreach (int g in glyphs) h = (h ^ (uint)g) * 16777619;
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++) { sb.Append((char)('A' + h % 26)); h = h / 26 + 7919u * (uint)(i + 1); }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ writing

        /// <summary>A complete, unencrypted rewrite of what the catalog reaches: PDF 1.7 header with a binary comment, one cross-reference table, a file identifier.</summary>
        private byte[] Write(PdfIndirectRef root)
        {
            var trailer = _doc.XrefTable.Trailer!;
            int encrypt = trailer["Encrypt"] is PdfIndirectRef er ? er.ObjectNumber : -1;
            PdfObject? Get(int n) => _overrides.TryGetValue(n, out var o) ? o : _r.Resolve(n);
            var reachable = new SortedSet<int>();
            var stack = new Stack<PdfObject>();
            stack.Push(root);
            if (trailer["Info"] is { } info) stack.Push(info);
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case PdfIndirectRef ir:
                        if (ir.ObjectNumber == encrypt || !reachable.Add(ir.ObjectNumber)) break;
                        if (Get(ir.ObjectNumber) is { } target) stack.Push(target);
                        break;
                    case PdfArray a: foreach (var i in a) stack.Push(i); break;
                    case PdfDictionary d: foreach (var v in d.Entries.Values) stack.Push(v); break;
                    case PdfStream s: foreach (var v in s.Dictionary.Entries.Values) stack.Push(v); break;
                }
            }

            using var ms = new MemoryStream();
            void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
            W("%PDF-1.7\n%âãÏÓ\n");
            // Object numbers are renumbered densely, so the file has no gaps.
            var renumber = new Dictionary<int, int>();
            foreach (int n in reachable)
                if (Get(n) is { } o && !(o is PdfStream { Dictionary: var sd } && sd.GetName("Type") is "XRef" or "ObjStm")) renumber[n] = renumber.Count + 1;
            PdfObject Renumbered(PdfObject o) => o switch
            {
                PdfIndirectRef r => renumber.TryGetValue(r.ObjectNumber, out int m) ? new PdfIndirectRef(m) : PdfNull.Instance,
                PdfArray a => new PdfArray(a.Items.Select(Renumbered).ToList()),
                PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(kv => kv.Key, kv => Renumbered(kv.Value))),
                PdfStream s => s with { Dictionary = (PdfDictionary)Renumbered(s.Dictionary) },
                _ => o,
            };
            var offsets = new long[renumber.Count + 1];
            foreach (var (old, n) in renumber.OrderBy(x => x.Value))
            {
                offsets[n] = ms.Position;
                W($"{n} 0 obj\n");
                PdfObjectWriter.Write(ms, Renumbered(Get(old)!), stripCrypt: true);
                W("\nendobj\n");
            }
            long xref = ms.Position;
            W($"xref\n0 {renumber.Count + 1}\n0000000000 65535 f \n");
            for (int n = 1; n <= renumber.Count; n++) W($"{offsets[n].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
            var id = trailer["ID"] is PdfArray { Count: 2 } ids && ids.Items.All(i => _r.Resolve(i) is PdfString { RawBytes.Length: > 0 })
                ? ids.Items.Select(i => _r.Resolve(i)!).ToArray()
                : null;
            byte[] fresh = MD5.HashData(ms.ToArray());
            var entries = new Dictionary<string, PdfObject>
            {
                ["Size"] = new PdfInteger(renumber.Count + 1),
                ["Root"] = Renumbered(root),
                ["ID"] = new PdfArray(new PdfObject[] { id?[0] ?? new PdfString(fresh), new PdfString(fresh) }),
            };
            if (trailer["Info"] is PdfIndirectRef infoRef && renumber.ContainsKey(infoRef.ObjectNumber)) entries["Info"] = Renumbered(infoRef);
            W("trailer\n");
            PdfObjectWriter.Write(ms, new PdfDictionary(entries));
            W($"\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }
    }
}
