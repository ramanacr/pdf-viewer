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
    /// <summary>PDF/A-2b (default), PDF/A-3b or PDF/A-1b. PDF/A-1b is refused for a document that uses transparency, which cannot be removed without changing the page.</summary>
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

/// <summary>A conversion the target part cannot take without changing how the document looks (PDF/A-1b of a document with transparency).</summary>
public sealed class PdfAConversionRefusedException : InvalidOperationException
{
    public PdfAConversionRefusedException(string message) : base(message) { }
}

/// <summary>
/// Converts a document to PDF/A-2b, -3b or -1b: a complete rewrite (no earlier revisions, no
/// encryption) that keeps every page's appearance. Fonts that are not embedded are embedded from
/// the installed fonts; an sRGB output intent (generated, not shipped) is added and device colour
/// is given default colour spaces; XMP metadata is rebuilt from the valid existing properties and
/// the document information; scripts, forbidden actions, external content, transfer functions and
/// similar are removed; annotations are made printable and get appearances; text codes whose
/// glyph the font program lacks are taken out; the result is then validated, and whatever could
/// not be fixed is reported. PDF/A-1b is refused for a document that uses transparency.
/// </summary>
public static class PdfAConverter
{
    public static PdfAConversionResult Convert(PdfVectorDocument document, byte[] original, PdfAConversionOptions? options = null)
    {
        options ??= new PdfAConversionOptions();
        if (options.Flavour.Part is not (1 or 2 or 3)) throw new ArgumentException("PDF/A-1, PDF/A-2 and PDF/A-3 are the conversion targets.", nameof(options));

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
            if (options.Flavour.Part == 1 && RefusalForPart1(doc, source) is { } reason) throw new PdfAConversionRefusedException(reason);
            var conversion = new Conversion(doc, options, source, changes);
            byte[] output = conversion.Run();
            var result = PdfVectorDocument.Open(new MemoryByteSource(output), document.FilePath);
            PdfAReport report;
            try
            {
                report = PdfAValidator.Validate(result, output, options.Flavour);
                // Text showing codes whose glyph the (final) font program lacks: those codes are taken out.
                if (report.Issues.Any(i => i.RuleId == (options.Flavour.Part == 1 ? "6.3.5-1" : "6.2.11.8-1")))
                {
                    var remover = new NotdefGlyphRemover(result);
                    int next = PdfIncrementalWriter.NextObjectNumber(result);
                    remover.Run(() => next++);
                    if (remover.Removed > 0)
                    {
                        output = Conversion.WriteFile(result, remover.Objects, options.Flavour.Part);
                        result.Dispose();
                        result = PdfVectorDocument.Open(new MemoryByteSource(output), document.FilePath);
                        report = PdfAValidator.Validate(result, output, options.Flavour);
                        changes[$"Took out {remover.Removed} text glyph(s) the fonts do not have (they drew .notdef); the other glyphs keep their places"] = 1;
                    }
                }
            }
            finally
            {
                result.Dispose();
            }
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

    /// <summary>
    /// Why a document cannot become PDF/A-1b without changing how it looks, or null: PDF/A-1
    /// forbids transparency (soft masks, constant alpha below 1, blend modes other than Normal
    /// and Compatible, transparency groups, translucent annotations) and optional content, so
    /// hidden layers would show.
    /// </summary>
    private static string? RefusalForPart1(PdfVectorDocument doc, PdfAReport source)
    {
        var kinds = new List<string>();
        void Kind(bool found, string what) { if (found && !kinds.Contains(what)) kinds.Add(what); }
        foreach (var issue in source.Issues)
        {
            Kind(issue.RuleId == "6.4-1", "soft masks");
            Kind(issue.RuleId == "6.4-2", "constant alpha below 1");
            Kind(issue.RuleId == "6.4-3", "transparency groups");
            Kind(issue.RuleId == "6.4-4", "blend modes other than Normal");
            Kind(issue.RuleId == "6.5.3-3", "translucent annotations");
        }
        var r = doc.Resolver;
        foreach (var (number, entry) in doc.XrefTable.Entries)
            if (entry.IsInUse && r.Resolve(number) is PdfStream { Dictionary: var d } && d.GetName("Subtype") == "Image" && (d.GetInteger("SMaskInData") ?? 0) != 0)
                Kind(true, "soft masks");
        if (kinds.Count > 0)
            return $"PDF/A-1b does not allow transparency, and this document uses it ({string.Join(", ", kinds)}). Removing it would change how the pages look; convert to PDF/A-2b instead.";
        if (r.Resolve(r.Resolve(doc.XrefTable.Trailer?["Root"]) is PdfDictionary c ? c["OCProperties"] : null) is PdfDictionary oc
            && r.Resolve(oc["D"]) is PdfDictionary config
            && (config.GetName("BaseState") is "OFF" or "Unchanged" || r.Resolve(config["OFF"]) is PdfArray { Count: > 0 }))
            return "PDF/A-1b does not allow optional content, and this document has layers that are hidden; they would show. Convert to PDF/A-2b instead.";
        return null;
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
            return WriteFile(_doc, _overrides, _part);
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
            // Wherever the image is reached from first (a form's /PieceInfo can reach it before its resources do).
            if (_part == 1 && d.GetName("Subtype") == "Image" && Filters(d).Contains("JPXDecode") && JpxAsFlate(stream, fixedDict) is { } flat)
            {
                Changed("Re-encoded JPEG 2000 images as Flate (PDF/A-1 does not allow JPEG 2000)");
                return flat;
            }
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

        /// <summary>
        /// A JPEG 2000 image (which PDF/A-1 does not allow) decoded and stored as 8-bit samples,
        /// Flate-compressed; null when it has its opacity in its data (transparency, refused for
        /// PDF/A-1 anyway) or cannot be decoded.
        /// </summary>
        private PdfStream? JpxAsFlate(PdfStream stream, PdfDictionary fixedDict)
        {
            var d = stream.Dictionary;
            if ((d.GetInteger("SMaskInData") ?? 0) != 0 || _r.Resolve(d["Mask"]) is PdfArray) return null;
            byte[] data;
            try
            {
                var decoded = _decoder.DecodeImageStream(stream);
                if (decoded.ImageFilter != "JPXDecode") return null;
                data = decoded.Data;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            var cs = d["ColorSpace"];
            bool indexed = _r.Resolve(cs) is PdfArray ia && ia.Count > 0 && _r.Resolve(ia[0]) is PdfName { Value: "Indexed" or "I" };
            // With an /Indexed space the samples are palette indices: the JP2 file's own palette is not applied.
            if (indexed && Images.PdfImageSource.TryGetJp2Codestream(data) is { } codestream) data = codestream;
            Images.PdfImageSource.JpxPlanes image;
            try { image = Images.PdfImageSource.ReadJpx(data); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            {
                int width = image.Width, height = image.Height, count = image.Planes.Length;
                if (width <= 0 || height <= 0 || (long)width * height > PdfSecurityLimits.Default.MaxImagePixels || count <= 0) return null;
                int n = cs != null ? Components(cs) : count >= 4 ? 4 : count >= 3 ? 3 : 1;
                if (n <= 0 || count < n) return null;
                var planes = new int[n][];
                var max = new int[n];
                for (int c = 0; c < n; c++)
                {
                    planes[c] = image.Planes[c];
                    int depth = Math.Clamp(image.Depths[c], 1, 16);
                    max[c] = (1 << depth) - 1;
                    if (planes[c].Length < width * height || (indexed && depth > 8)) return null;
                }
                var samples = new byte[checked(width * height * n)];
                for (int i = 0, p = 0; i < width * height; i++)
                    for (int c = 0; c < n; c++, p++)
                    {
                        int v = Math.Clamp(planes[c][i], 0, max[c]);
                        samples[p] = indexed ? (byte)v : (byte)Math.Round(v * 255.0 / max[c]);
                    }
                var entries = new Dictionary<string, PdfObject>(fixedDict.Entries);
                foreach (var key in new[] { "Filter", "DecodeParms", "SMaskInData", "Decode", "Length", "DL" }) entries.Remove(key);
                entries["Width"] = new PdfInteger(width);
                entries["Height"] = new PdfInteger(height);
                entries["BitsPerComponent"] = new PdfInteger(8);
                entries["Filter"] = new PdfName("FlateDecode");
                if (cs == null) entries["ColorSpace"] = IccFor(new PdfName(n == 4 ? "DeviceCMYK" : n == 3 ? "DeviceRGB" : "DeviceGray"));
                return PdfObjectWriter.NewStream(entries, ContentRedactor.Deflate(samples));
            }
        }

        /// <summary>The number of colour components of an image's colour space; 0 when unknown (Lab, whose range an 8-bit rescale would change, is left alone).</summary>
        private int Components(PdfObject cs) => _r.Resolve(cs) switch
        {
            PdfName n => n.Value switch { "DeviceGray" or "G" or "CalGray" => 1, "DeviceRGB" or "RGB" => 3, "DeviceCMYK" or "CMYK" => 4, _ => 0 },
            PdfArray a when a.Count > 1 && _r.Resolve(a[0]) is PdfName family => family.Value switch
            {
                "CalGray" or "Indexed" or "I" or "Separation" => 1,
                "CalRGB" => 3,
                "ICCBased" => (int)((_r.Resolve(a[1]) as PdfStream)?.Dictionary.GetInteger("N") ?? 0),
                "DeviceN" => (_r.Resolve(a[1]) as PdfArray)?.Count ?? 0,
                _ => 0,
            },
            _ => 0,
        };

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
            if (_part == 1)
            {
                Remove("EF", "Removed embedded file data (PDF/A-1 does not allow embedded files)");
                if (e.ContainsKey("HalftoneType")) Remove("TransferFunction", "Removed halftone transfer functions");
            }

            switch (role)
            {
                case Role.Catalog:
                    Remove("AA", "Removed document-level actions (/AA)");
                    Remove("NeedsRendering", "Removed the XFA rendering request");
                    if (_part == 1) Remove("Version", "Removed the catalog's later PDF version (PDF/A-1 is PDF 1.4)");
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
                    if (WithSubsetLists(d) is var listed && !ReferenceEquals(listed, d)) { d = listed; e = new Dictionary<string, PdfObject>(d.Entries); changed = true; }
                    break;
                case Role.FontDescriptor when _part != 1:
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
                        if (_part == 1) Remove("OC", "Removed optional content (PDF/A-1 does not have it)");
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
                if (_part != 3 && n.Remove("EmbeddedFiles")) Changed(_part == 1 ? "Removed embedded files (PDF/A-1 does not allow them)" : "Removed embedded files (PDF/A-2 allows only PDF/A attachments; PDF/A-3 keeps them)");
                e["Names"] = new PdfDictionary(n);
            }
            if (_part == 3) AssociateEmbeddedFiles(e);
            if (_r.Resolve(e.GetValueOrDefault("Perms")) is PdfDictionary perms)
            {
                var p = perms.Entries.Where(kv => kv.Key is "UR3" or "DocMDP").ToDictionary(kv => kv.Key, kv => kv.Value);
                if (p.Count != perms.Count) { e["Perms"] = new PdfDictionary(p); Changed("Removed permissions other than UR3 and DocMDP"); }
            }
            // PDF/A-1 has no optional content; what remains of it is shown (a document with hidden layers was refused).
            if (_part == 1) { if (e.Remove("OCProperties")) Changed("Removed optional content (PDF/A-1 does not have it)"); }
            else if (_r.Resolve(e.GetValueOrDefault("OCProperties")) is PdfDictionary oc) e["OCProperties"] = FixOptionalContent(oc);
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
            // Whitespace alone is not kept by XML readers: such a value is left out (and dropped from the information for PDF/A-1).
            string? Info(string key) => info != null && _r.Resolve(info[key]) is PdfString s && CleanText(s.AsDecodedString()) is { } text && text.Trim().Length > 0 ? text : null;
            // What the old packet has that is valid PDF/A metadata is kept (for PDF/A-1, as XMP 2004 defines it, with the preferred prefixes).
            if (old != null && old.Error == null)
            {
                foreach (var p in old.Properties)
                {
                    if (p.Namespace is XmpSchemas.PdfaId || p.Namespace == XmpPacket.RdfNs) continue;
                    if (!XmpSchemas.Schemas.TryGetValue(p.Namespace, out var schema) || !schema.TryGetValue(p.Name, out var type)
                        || (_part == 1 && (p.Namespace == XmpSchemas.XmpDM || XmpSchemas.NotIn2004(p.Namespace, p.Name))))
                    {
                        Changed("Dropped metadata properties PDF/A does not define");
                        continue;
                    }
                    if (_part == 1 && XmpSchemas.Types2004.TryGetValue((p.Namespace, p.Name), out var old2004)) type = old2004;
                    if (XmpSchemas.Check(p.Value, type, XmpSchemas.Predefined) != null) { Changed("Dropped metadata properties with invalid values"); continue; }
                    props.Add(_part == 1 && XmpSchemas.Prefixes.TryGetValue(p.Namespace, out var preferred) ? p with { Prefix = preferred } : p);
                }
            }
            // PDF/A-1 (6.7.3): the document information and the metadata agree, so the information wins.
            bool Has(string ns, string name) => _part != 1 && props.Any(p => p.Namespace == ns && p.Name == name);
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
            // One instant for both, so the information's /ModDate and xmp:ModifyDate agree.
            var instant = DateTimeOffset.Now;
            string now = XmpDate(instant);
            Set(XmpSchemas.Xmp, "xmp", "ModifyDate", XmpWriter.Simple(now));
            Set(XmpSchemas.Xmp, "xmp", "MetadataDate", XmpWriter.Simple(now));
            Set(XmpSchemas.PdfaId, "pdfaid", "part", XmpWriter.Simple(_part.ToString(CultureInfo.InvariantCulture)));
            Set(XmpSchemas.PdfaId, "pdfaid", "conformance", XmpWriter.Simple("B"));
            _modifyDate = PdfDate(instant);
            return XmpWriter.Write(props);
        }

        /// <summary>Text as XML can carry it: without control characters XML forbids, and with its line ends as XML reads them back.</summary>
        private static string CleanText(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
                if (ch is '\t' or '\n' || (ch >= 0x20 && ch < 0xFFFE)) sb.Append(ch);
            return sb.ToString();
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
            {
                if (_r.Resolve(v) is not PdfString s) continue; // only text entries; references to other objects are dropped
                e[k] = s;
                // PDF/A-1: the entries the metadata repeats are written as it has them; a date it cannot have is dropped.
                if (_part != 1) continue;
                if (k is "Title" or "Author" or "Subject" or "Keywords" or "Creator" or "Producer" && CleanText(s.AsDecodedString()) is var clean && clean != s.AsDecodedString())
                    e[k] = PdfObjectWriter.TextString(clean);
                if (k is "Title" or "Author" or "Subject" or "Keywords" or "Creator" or "Producer" && CleanText(s.AsDecodedString()).Trim().Length == 0) e.Remove(k);
                if (k == "CreationDate" && PdfDates.ParsePdf(s.AsDecodedString()) == null) e.Remove(k);
            }
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
            if (_part == 1 && subtype is not ("Text" or "Link" or "FreeText" or "Line" or "Square" or "Circle" or "Highlight" or "Underline" or "Squiggly" or "StrikeOut"
                    or "Stamp" or "Ink" or "Popup" or "Widget" or "PrinterMark" or "TrapNet"))
            {
                Changed($"Removed {subtype} annotations (PDF/A-1 does not allow them)");
                return null;
            }
            var e = new Dictionary<string, PdfObject>(a.Entries);
            if (_part == 1 && e.Remove("OC")) Changed("Removed optional content (PDF/A-1 does not have it)");
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
            // A stand-in font keeps the document's spacing: its program is given the document's widths,
            // for the glyph each code reaches through the encoding as written.
            string? WrittenName(int code)
            {
                string? name = pdfFont.GetGlyphName(code), winAnsi = code < FontEncodings.WinAnsi.Length ? FontEncodings.WinAnsi[code] : null;
                return name != null && name != winAnsi && GlyphList.ToUnicode(name) != null ? name : winAnsi;
            }
            var uses = new List<GlyphUse>();
            for (int code = first; code <= last; code++)
            {
                int gid = symbol ? file.GlyphFor(code) : WrittenName(code) is { } name && GlyphList.ToUnicode(name) is { } u ? file.GlyphFor(char.ConvertToUtf32(u, 0)) : 0;
                if (widths[code - first] is PdfReal w) uses.Add(new GlyphUse(code, gid, w.Value));
            }
            var (advances, copies) = PlanWidths(uses, file, file);
            if (advances.Count > 0 && TrueTypeFontFile.WithAdvances(program, advances) is { } spaced) program = spaced;
            var renamed = new Dictionary<int, (string Name, string Unicode)>();
            if (copies.Count > 0 && SplitSharedGlyphs(program, copies, symbol, WrittenName, code => pdfFont.ToUnicodeMap?.ContainsKey(code) ?? !font.ContainsKey("ToUnicode")) is { } split)
            {
                program = split.Program;
                renamed = split.Renamed;
            }
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
            if (renamed.Count > 0) Rename(e, renamed);
            Changed($"Embedded {face.Family}{(face.Subfamily is "Regular" ? "" : " " + face.Subfamily)} for the font {baseFont}");
            return new PdfDictionary(e);
        }

        /// <summary>
        /// An embedded TrueType font whose program's advance widths disagree with the widths the
        /// document gives gets a program with the document's widths (what readers use to space the
        /// text), so nothing moves and the two agree. Codes that share a glyph but are given
        /// different widths get copies of it, through the cmap or the encoding.
        /// </summary>
        private PdfDictionary? HarmonizeSimple(PdfDictionary font, PdfDictionary descriptor)
        {
            PdfFont pdfFont;
            try { pdfFont = (_fontResolver ??= new PdfFontResolver(_r, PdfSecurityLimits.Default)).ResolveFont(font, "F"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            if (pdfFont.EmbeddedFontData is not { } data || TrueTypeFontFile.TryLoad(data) is not { } file || FontProgramPreparer.Prepare(pdfFont) is not { } prepared) return null;
            var uses = new List<GlyphUse>();
            for (int code = Math.Max(0, pdfFont.FirstChar); code <= Math.Min(255, pdfFont.LastChar); code++)
                uses.Add(new GlyphUse(code, prepared.GetGlyphId(code, code), pdfFont.GetGlyphWidth(code)));
            var (advances, copies) = PlanWidths(uses, file, TrueTypeFontFile.TryLoad(prepared.Sfnt) ?? file);
            if (advances.Count == 0 && copies.Count == 0) return null;
            byte[] program = data;
            if (advances.Count > 0 && TrueTypeFontFile.WithAdvances(data, advances) is { } spaced) program = spaced;
            var renamed = new Dictionary<int, (string Name, string Unicode)>();
            if (copies.Count > 0 && SplitSharedGlyphs(program, copies, pdfFont.IsSymbolic, pdfFont.GetGlyphName,
                    code => pdfFont.ToUnicodeMap?.ContainsKey(code) ?? !font.ContainsKey("ToUnicode")) is { } split)
            {
                program = split.Program;
                renamed = split.Renamed;
            }
            if (ReferenceEquals(program, data)) return null;
            var result = WithProgram(font, descriptor, "FontFile", program);
            if (renamed.Count == 0) return result;
            var e = new Dictionary<string, PdfObject>(result.Entries);
            Rename(e, renamed);
            return new PdfDictionary(e);
        }

        /// <summary>A code (a CID in a composite font) and the glyph it shows, with the width the document gives it.</summary>
        private readonly record struct GlyphUse(int Code, int Gid, double Width);

        /// <summary>A copy of glyph <c>Source</c> to append, with its advance (font units), for <c>Codes</c>.</summary>
        private sealed record GlyphCopy(int Source, int Advance, List<int> Codes);

        /// <summary>
        /// How a program's widths are made to agree with the document's: new advances for glyphs,
        /// and a copy of a glyph for each further width its codes are given. A glyph keeps the width
        /// its program already has when a code agrees with it (within a thousandth of an em), else
        /// its first code's. <paramref name="seen"/> is the program as readers and the validator
        /// load it (it can have other metrics than <paramref name="file"/> when those are broken).
        /// </summary>
        private static (Dictionary<int, int> Advances, List<GlyphCopy> Copies) PlanWidths(IEnumerable<GlyphUse> uses, TrueTypeFontFile file, TrueTypeFontFile seen)
        {
            int Units(double width) => (int)Math.Round(width * file.UnitsPerEm / 1000.0);
            var advances = new Dictionary<int, int>();
            var copies = new List<GlyphCopy>();
            foreach (var glyph in uses.Where(u => u.Gid > 0 && u.Gid < file.GlyphCount && u.Gid < seen.GlyphCount).GroupBy(u => u.Gid))
            {
                var widths = new List<(double Width, List<int> Codes)>();
                foreach (var use in glyph.OrderBy(u => u.Code))
                {
                    int at = widths.FindIndex(w => Math.Abs(w.Width - use.Width) <= 1);
                    if (at < 0) widths.Add((use.Width, new List<int> { use.Code }));
                    else widths[at].Codes.Add(use.Code);
                }
                int keep = widths.FindIndex(w => Math.Abs(w.Width - seen.Advance(glyph.Key)) <= 1);
                if (keep < 0) { keep = 0; advances[glyph.Key] = Units(widths[0].Width); }
                // Metrics the program's own table does not have (rebuilt when read) are written out.
                else if (Math.Abs(file.Advance(glyph.Key) - seen.Advance(glyph.Key)) > 1) advances[glyph.Key] = Units(widths[keep].Width);
                for (int i = 0; i < widths.Count; i++)
                    if (i != keep) copies.Add(new GlyphCopy(glyph.Key, Units(widths[i].Width), widths[i].Codes));
            }
            return (advances, copies);
        }

        /// <summary>
        /// A simple TrueType program with the copies appended and their codes moved to them: for a
        /// code the reader maps through the (3,0) or (1,0) subtable, that subtable's entry; for one
        /// mapped by glyph name through the Unicode subtable, a new name (<c>uniE000</c> and on, in
        /// the private use area) that the Unicode subtables map to the copy. <paramref name="canRename"/>
        /// says whether a code's text survives a new name (the font's /ToUnicode has it, or there is
        /// none and one is written). Codes mapped any other way stay where they are. Null when no code moves.
        /// </summary>
        private (byte[] Program, Dictionary<int, (string Name, string Unicode)> Renamed)? SplitSharedGlyphs(byte[] program, List<GlyphCopy> copies, bool symbolic,
            Func<int, string?> nameOf, Func<int, bool> canRename)
        {
            if (TrueTypeCmap.TryParse(program) is not { } cmap) return null;
            var subtables = TrueTypeGlyphCopies.Subtables(program);
            var unicodeTables = subtables.Where(s => (s.Platform == 0 && s.Encoding != 5) || (s.Platform == 3 && s.Encoding is 1 or 10)).ToList();
            // How the reader finds each code's glyph (as PdfFont.GetGlyphId does): the cmap keys to change, or a name to give.
            (List<((int, int) Table, int Key)>? Keys, string? Unicode) Route(int code, int gid)
            {
                if (!symbolic && nameOf(code) is { } name)
                {
                    if (cmap.Has(3, 1) && GlyphList.ToUnicode(name) is { Length: > 0 } u && cmap.Lookup(3, 1, char.ConvertToUtf32(u, 0)) > 0) return (null, u);
                    if (cmap.Has(1, 0) && FontEncodings.TryGetMacRomanCode(name, out int mac) && cmap.Lookup(1, 0, mac) > 0) return (null, null);
                }
                var keys = new List<((int, int), int)>();
                if (cmap.Has(3, 0))
                    foreach (int prefix in new[] { 0, 0xF000, 0xF100, 0xF200 })
                        if (cmap.Lookup(3, 0, prefix + code) == gid) keys.Add(((3, 0), prefix + code));
                if (cmap.Has(1, 0) && cmap.Lookup(1, 0, code) == gid) keys.Add(((1, 0), code));
                return (keys.Count > 0 ? keys : null, null);
            }
            var moves = new List<(GlyphCopy Copy, List<(int Code, List<((int, int) Table, int Key)>? Keys, string? Unicode)> Codes)>();
            foreach (var copy in copies)
            {
                var codes = new List<(int, List<((int, int), int)>?, string?)>();
                foreach (int code in copy.Codes)
                {
                    var (keys, unicode) = Route(code, copy.Source);
                    if (keys != null || (unicode != null && unicodeTables.Count > 0 && canRename(code))) codes.Add((code, keys, unicode));
                }
                if (codes.Count > 0) moves.Add((copy, codes));
            }
            if (moves.Count == 0 || TrueTypeGlyphCopies.Append(program, moves.Select(m => (m.Copy.Source, m.Copy.Advance)).ToList()) is not { } appended) return null;
            var changes = new Dictionary<(int, int), Dictionary<int, int>>();
            void Map((int, int) table, int key, int gid)
            {
                if (!changes.TryGetValue(table, out var entries)) changes[table] = entries = new Dictionary<int, int>();
                entries[key] = gid;
            }
            var renamed = new Dictionary<int, (string, string)>();
            int privateUse = 0xE000;
            for (int i = 0; i < moves.Count; i++)
            {
                int gid = appended.FirstCopy + i;
                foreach (var (code, keys, unicode) in moves[i].Codes)
                {
                    if (keys != null) { foreach (var (table, key) in keys) Map(table, key, gid); continue; }
                    while (privateUse <= 0xF8FF && unicodeTables.Any(t => cmap.Lookup(t.Platform, t.Encoding, privateUse) != 0)) privateUse++;
                    if (privateUse > 0xF8FF) continue;
                    foreach (var table in unicodeTables) Map(table, privateUse, gid);
                    renamed[code] = ("uni" + privateUse.ToString("X4", CultureInfo.InvariantCulture), unicode!);
                    privateUse++;
                }
            }
            if (TrueTypeGlyphCopies.WithCmap(appended.Font, changes.ToDictionary(c => c.Key, c => (IReadOnlyDictionary<int, int>)c.Value)) is not { } remapped) return null;
            Changed("Gave codes that share a glyph but not its width a copy of the glyph with their width");
            return (remapped, renamed);
        }

        /// <summary>
        /// Gives codes new glyph names in a simple font's encoding (as differences), and keeps their
        /// text: a /ToUnicode is written for them when the font has none.
        /// </summary>
        private void Rename(Dictionary<string, PdfObject> font, Dictionary<int, (string Name, string Unicode)> renamed)
        {
            var encoding = _r.Resolve(font.GetValueOrDefault("Encoding"));
            string? baseEncoding = encoding is PdfName n ? n.Value : (encoding as PdfDictionary)?.GetName("BaseEncoding");
            var names = new SortedDictionary<int, string>();
            if (encoding is PdfDictionary ed && _r.Resolve(ed["Differences"]) is PdfArray old)
            {
                int code = 0;
                foreach (var item in old.Items.Select(_r.Resolve))
                {
                    if (item is PdfInteger i) code = (int)i.Value;
                    else if (item is PdfName name) names[code++] = name.Value;
                }
            }
            foreach (var (code, (name, _)) in renamed) names[code] = name;
            var differences = new List<PdfObject>();
            int previous = -2;
            foreach (var (code, name) in names)
            {
                if (code != previous + 1) differences.Add(new PdfInteger(code));
                differences.Add(new PdfName(name));
                previous = code;
            }
            var entries = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("Encoding"), ["Differences"] = new PdfArray(differences) };
            if (baseEncoding != null) entries["BaseEncoding"] = new PdfName(baseEncoding);
            font["Encoding"] = new PdfDictionary(entries);
            if (font.ContainsKey("ToUnicode")) return;
            var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
                .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n")
                .Append(renamed.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (code, (_, unicode)) in renamed.OrderBy(r => r.Key))
                cmap.Append('<').Append(code.ToString("X2", CultureInfo.InvariantCulture)).Append("> <").Append(System.Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(unicode))).Append(">\n");
            cmap.Append("endbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            font["ToUnicode"] = new PdfIndirectRef(Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") },
                ContentRedactor.Deflate(Encoding.ASCII.GetBytes(cmap.ToString())))));
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
            var uses = new List<GlyphUse>();
            int cids = map != null ? map.Length / 2 : file.GlyphCount;
            for (int c = 1; c < cids && c < 65536; c++)
                uses.Add(new GlyphUse(c, map == null ? c : (map[c * 2] << 8) | map[c * 2 + 1], pdfFont.GetGlyphWidth(c)));
            var seen = FontProgramPreparer.Prepare(pdfFont) is { Format: PdfFontProgramFormat.TrueType } prepared ? TrueTypeFontFile.TryLoad(prepared.Sfnt) ?? file : file;
            var (advances, copies) = PlanWidths(uses, file, seen);
            bool needMap = cid["CIDToGIDMap"] == null;
            if (advances.Count == 0 && copies.Count == 0 && !needMap) return null;
            var ce = new Dictionary<string, PdfObject>(cid.Entries);
            if (needMap) { ce["CIDToGIDMap"] = new PdfName("Identity"); Changed("Gave embedded CID fonts an explicit /CIDToGIDMap (the identity they already used)"); }
            byte[] program = data;
            if (advances.Count > 0 && TrueTypeFontFile.WithAdvances(data, advances) is { } spaced) program = spaced;
            // CIDs that share a glyph but not its width are given copies of it through the CIDToGIDMap.
            if (copies.Count > 0 && copies.Count + file.GlyphCount <= 65535 && TrueTypeGlyphCopies.Append(program, copies.Select(c => (c.Source, c.Advance)).ToList()) is { } appended)
            {
                int size = Math.Max(cids, copies.SelectMany(c => c.Codes).Max() + 1);
                var newMap = new byte[size * 2];
                for (int c = 0; c < size; c++)
                {
                    int gid = map == null ? c : c * 2 + 1 < map.Length ? (map[c * 2] << 8) | map[c * 2 + 1] : 0;
                    newMap[c * 2] = (byte)(gid >> 8);
                    newMap[c * 2 + 1] = (byte)gid;
                }
                for (int i = 0; i < copies.Count; i++)
                    foreach (int c in copies[i].Codes)
                    {
                        newMap[c * 2] = (byte)((appended.FirstCopy + i) >> 8);
                        newMap[c * 2 + 1] = (byte)(appended.FirstCopy + i);
                    }
                program = appended.Font;
                ce["CIDToGIDMap"] = new PdfIndirectRef(Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, ContentRedactor.Deflate(newMap))));
                Changed("Gave codes that share a glyph but not its width a copy of the glyph with their width");
            }
            var newCid = !ReferenceEquals(program, data) ? WithProgram(new PdfDictionary(ce), descriptor, "FontFile2", program) : new PdfDictionary(ce);
            // The new descendant still needs its descriptor fixed (subset lists).
            var fixedCid = (PdfDictionary)FixDictionary(WithSubsetLists(newCid), Role.Generic, false)!;
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
            if (_part != 1)
            {
                d.Remove("CIDSet");
                d.Remove("CharSet");
            }
            int descriptorNumber = Add(new PdfDictionary(d));
            Changed("Made embedded fonts' glyph widths agree with the widths the document gives");
            return new PdfDictionary(new Dictionary<string, PdfObject>(font.Entries) { ["FontDescriptor"] = new PdfIndirectRef(descriptorNumber) });
        }

        /// <summary>
        /// PDF/A-1 (6.3.5): a subset Type 1 font lists its glyphs (/CharSet) and a subset CIDFont its
        /// CIDs (/CIDSet). A missing list is made from the embedded program.
        /// </summary>
        private PdfDictionary WithSubsetLists(PdfDictionary font)
        {
            if (_part != 1 || Current(font["FontDescriptor"]) is not PdfDictionary descriptor) return font;
            string baseFont = font.GetName("BaseFont") ?? string.Empty;
            if (!(baseFont.Length > 7 && baseFont[6] == '+' && baseFont.Take(6).All(char.IsAsciiLetterUpper))) return font;
            string? subtype = font.GetName("Subtype");
            string key;
            PdfObject? list;
            if (subtype is "Type1" or "MMType1" && descriptor["CharSet"] == null) { key = "CharSet"; list = CharSet(descriptor); }
            else if (subtype is "CIDFontType0" or "CIDFontType2" && _r.Resolve(descriptor["CIDSet"]) is not PdfStream) { key = "CIDSet"; list = CidSet(font, descriptor); }
            else return font;
            if (list == null) return font;
            var d = new Dictionary<string, PdfObject>(descriptor.Entries) { [key] = list };
            int number = Add(FixDictionary(new PdfDictionary(d), Role.FontDescriptor, false)!);
            Changed($"Listed the glyphs of subset fonts (/{key}), which PDF/A-1 requires");
            return new PdfDictionary(new Dictionary<string, PdfObject>(font.Entries) { ["FontDescriptor"] = new PdfIndirectRef(number) });
        }

        /// <summary>A value as this conversion has it: an object it replaced or added, else the document's.</summary>
        private PdfObject? Current(PdfObject? value) => value is PdfIndirectRef r && _overrides.TryGetValue(r.ObjectNumber, out var o) ? o : _r.Resolve(value);

        /// <summary>The glyph names of an embedded Type 1 or CFF program, as a /CharSet string.</summary>
        private PdfString? CharSet(PdfDictionary descriptor)
        {
            byte[]? cff = null;
            if (_r.Resolve(descriptor["FontFile3"]) is PdfStream f3 && Decode(f3) is { } bare) cff = bare;
            else if (_r.Resolve(descriptor["FontFile"]) is PdfStream f1 && Decode(f1) is { } type1) cff = Type1Converter.TryConvert(type1)?.Cff;
            if (cff == null || CffFont.TryParse(cff) is not { } program) return null;
            var names = new StringBuilder();
            for (int gid = 1; gid < program.GlyphCount; gid++)
                if (program.GlyphName(gid) is { Length: > 0 } name && name != ".notdef") names.Append('/').Append(name);
            return PdfObjectWriter.TextString(names.ToString());
        }

        /// <summary>The CIDs an embedded CIDFont program has, as a /CIDSet bit stream (CID 0 is the high bit of the first byte).</summary>
        private PdfIndirectRef? CidSet(PdfDictionary font, PdfDictionary descriptor)
        {
            var present = new List<int>();
            if (font.GetName("Subtype") == "CIDFontType2")
            {
                // Subset programs often have no cmap: the glyph count is read from maxp.
                if (Current(descriptor["FontFile2"]) is not PdfStream fs || Decode(fs) is not { } data || TrueTypeFontFile.ReadTables(data, 0) is not { } tables || !tables.TryGetValue("maxp", out var maxp)) return null;
                int glyphs = BigEndian.U16(maxp, 4);
                byte[]? map = Current(font["CIDToGIDMap"]) is PdfStream ms ? Decode(ms) : null;
                int cids = map != null ? map.Length / 2 : glyphs;
                for (int cid = 0; cid < cids && cid < 65536; cid++)
                {
                    int gid = map == null ? cid : (map[cid * 2] << 8) | map[cid * 2 + 1];
                    if (gid < glyphs && (cid == 0 || gid != 0)) present.Add(cid);
                }
            }
            else
            {
                if (_r.Resolve(descriptor["FontFile3"]) is not PdfStream f3 || Decode(f3) is not { } data || CffFont.TryParse(data) is not { } cff) return null;
                if (!cff.IsCidKeyed) present.AddRange(Enumerable.Range(0, cff.GlyphCount));
                else
                    for (int cid = 0; cid < 65536; cid++)
                        if (cid == 0 || cff.GidForCid(cid) > 0) present.Add(cid);
            }
            if (present.Count == 0) return null;
            var bits = new byte[present[^1] / 8 + 1];
            foreach (int cid in present) bits[cid / 8] |= (byte)(0x80 >> (cid % 8));
            return new PdfIndirectRef(Add(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, ContentRedactor.Deflate(bits))));
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

        /// <summary>
        /// A complete, unencrypted rewrite of what the catalog reaches, with <paramref name="overrides"/>
        /// replacing or adding objects: a PDF 1.7 header (1.4 for PDF/A-1) with a binary comment, one
        /// cross-reference table, a file identifier.
        /// </summary>
        public static byte[] WriteFile(PdfVectorDocument doc, IReadOnlyDictionary<int, PdfObject> overrides, int part)
        {
            var r = doc.Resolver;
            var trailer = doc.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
            if (trailer["Root"] is not PdfIndirectRef root) throw new InvalidOperationException("The document has no catalog.");
            int encrypt = trailer["Encrypt"] is PdfIndirectRef er ? er.ObjectNumber : -1;
            PdfObject? Get(int n) => overrides.TryGetValue(n, out var o) ? o : r.Resolve(n);
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
            W(part == 1 ? "%PDF-1.4\n%âãÏÓ\n" : "%PDF-1.7\n%âãÏÓ\n");
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
            var id = trailer["ID"] is PdfArray { Count: 2 } ids && ids.Items.All(i => r.Resolve(i) is PdfString { RawBytes.Length: > 0 })
                ? ids.Items.Select(i => r.Resolve(i)!).ToArray()
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
