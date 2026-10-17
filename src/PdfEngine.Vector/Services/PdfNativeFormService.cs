using System.Text;
using System.Xml.Linq;
using PdfEngine.Documents;
using PdfEngine.Forms;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Services;

/// <summary>What a form data import changed: the fields filled, and the names the document does not have.</summary>
public sealed record PdfFormImportResult(int Applied, IReadOnlyList<string> UnknownFields, IReadOnlyList<string> ReadOnlyFields);

/// <summary>
/// Interactive forms without PDFium: fields as the viewer models them, values set with their
/// appearances regenerated (<see cref="PdfFormFiller"/>), reset, form data exported and imported
/// as XFDF (ISO 19444-1) or FDF (ISO 32000-2 12.7.8), and flattening. Every change is committed to
/// the <see cref="PdfNativeDocument"/> as one incremental update, so signatures on earlier
/// revisions stay valid.
/// </summary>
/// <remarks>
/// Document JavaScript is never executed. Formats, keystroke and validate rules and calculations
/// that use the standard Acrobat functions are applied as <see cref="PdfFormScripts"/> recognises
/// them; fields with custom scripts are filled as given. Calculated fields are recalculated in the
/// form's calculation order (/CO) after every change.
/// </remarks>
public sealed class PdfNativeFormService : IPdfFormService
{
    // ------------------------------------------------------------------ reading

    public ValueTask<IReadOnlyList<FormFieldModel>> GetFormFieldsAsync(IPdfDocument document, int pageNumber, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        if (pageNumber < 1 || pageNumber > doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} is outside the document's {doc.PageCount} pages.");
        return ValueTask.FromResult(ReadFields(doc.Vector, pageNumber));
    }

    /// <summary>The fields of every page, page by page.</summary>
    public static IReadOnlyList<FormFieldModel> ReadAllFields(PdfVectorDocument document)
    {
        var all = new List<FormFieldModel>();
        for (int p = 1; p <= document.PageCount; p++) all.AddRange(ReadFields(document, p));
        return all;
    }

    /// <summary>
    /// One model per widget on the page, in the order of the page's /Annots (the order PDFium
    /// enumerates them). Names are full dotted names; bounds are normalized to the unrotated
    /// crop box from its top-left corner, like annotations.
    /// </summary>
    public static IReadOnlyList<FormFieldModel> ReadFields(PdfVectorDocument document, int pageNumber)
    {
        var form = PdfAcroForm.Read(document);
        if (form == null) return Array.Empty<FormFieldModel>();
        var r = document.Resolver;
        var page = document.PageTree.Pages[pageNumber - 1];
        var order = new Dictionary<int, int>();
        if (r.Resolve(page.Dictionary["Annots"]) is PdfArray annots)
            for (int i = 0; i < annots.Count; i++)
                if (annots[i] is PdfIndirectRef ar) order.TryAdd(ar.ObjectNumber, i);
        var crop = page.CropBox;
        var result = new List<(int Order, FormFieldModel Model)>();
        foreach (var field in form.Fields)
        {
            foreach (var w in field.Widgets.Where(w => w.PageNumber == pageNumber))
            {
                var model = new FormFieldModel
                {
                    Name = field.FullName,
                    AlternateName = field.Tooltip ?? string.Empty,
                    Type = field.Kind switch
                    {
                        PdfFormFieldKind.Text => FormFieldType.TextField,
                        PdfFormFieldKind.CheckBox => FormFieldType.CheckBox,
                        PdfFormFieldKind.RadioGroup => FormFieldType.RadioButton,
                        PdfFormFieldKind.PushButton => FormFieldType.PushButton,
                        PdfFormFieldKind.ComboBox => FormFieldType.ComboBox,
                        PdfFormFieldKind.ListBox => FormFieldType.ListBox,
                        PdfFormFieldKind.Signature => FormFieldType.Signature,
                        _ => FormFieldType.Unknown,
                    },
                    PageNumber = pageNumber,
                    Value = field.Value,
                    DefaultValue = field.DefaultValue,
                    IsReadOnly = field.IsReadOnly,
                    IsRequired = field.IsRequired,
                    MaxLength = field.MaxLength,
                    IsMultiLine = field.IsMultiline,
                    IsPassword = field.IsPassword,
                    Options = field.Options.Select(o => o.Display).ToList(),
                };
                if (crop.Width > 0 && crop.Height > 0)
                    model.Bounds = new PdfRect(
                        Math.Clamp((w.Rect.Left - crop.Left) / crop.Width, 0, 1),
                        Math.Clamp(1 - (w.Rect.Bottom - crop.Top) / crop.Height, 0, 1),
                        Math.Clamp(w.Rect.Width / crop.Width, 0, 1),
                        Math.Clamp(w.Rect.Height / crop.Height, 0, 1));
                if (field.Kind is PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioGroup)
                    model.IsChecked = WidgetIsOn(field, w);
                if (field.Kind is PdfFormFieldKind.ComboBox or PdfFormFieldKind.ListBox)
                    model.SelectedIndex = field.Options.ToList().FindIndex(o => o.Export == field.Value);
                result.Add((order.TryGetValue(w.ObjectNumber, out int at) ? at : int.MaxValue, model));
            }
        }
        return result.OrderBy(x => x.Order).Select(x => x.Model).ToList();
    }

    /// <summary>The widget shows its on state: its /AS, else the field's value (as the renderer draws it).</summary>
    private static bool WidgetIsOn(PdfFormField field, PdfFormWidget w)
    {
        string? state = w.AppearanceState;
        if (string.IsNullOrEmpty(state))
            state = w.OnState != null && field.Value == w.OnState ? w.OnState : "Off";
        return !string.Equals(state, "Off", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ setting values

    public ValueTask SetFieldValueAsync(IPdfDocument document, string fieldName, string value, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (string.IsNullOrEmpty(fieldName)) throw new ArgumentException("Field name must be supplied.", nameof(fieldName));
        cancellationToken.ThrowIfCancellationRequested();
        SetValues(doc, new[] { (fieldName, (IReadOnlyList<string>)new[] { value ?? string.Empty }) }, validate: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Sets several fields in one revision. A value is the field's text, a check box's on/off
    /// ("Off", "", "false", "0" and "No" are off), a radio group's or combo box's choice (export
    /// value or display text), or a list box's selections. With <paramref name="validate"/> the
    /// field's own rules apply (a refused value throws <see cref="PdfFieldValidationException"/>);
    /// calculated fields are recalculated either way. Returns the full names of the fields changed.
    /// </summary>
    public static IReadOnlyList<string> SetValues(PdfNativeDocument doc, IEnumerable<(string Name, IReadOnlyList<string> Values)> values, bool validate)
    {
        var form = PdfAcroForm.Read(doc.Vector) ?? throw new KeyNotFoundException("The document has no form fields.");
        var changes = new List<PdfFieldChange>();
        foreach (var (name, v) in values)
        {
            var field = Find(form, name) ?? throw new KeyNotFoundException($"No form field named '{name}' exists in the document.");
            if (field.IsReadOnly) throw new InvalidOperationException($"Form field '{field.FullName}' is read-only and cannot be modified.");
            changes.Add(ToChange(field, v));
        }
        return Commit(doc, form, changes, validate);
    }

    /// <summary>A field by full name, else the one field whose last name part is <paramref name="name"/> (PDFium's partial names).</summary>
    public static PdfFormField? Find(PdfAcroForm form, string name)
    {
        if (form[name] is { } exact) return exact;
        var partial = form.Fields.Where(f => f.FullName.EndsWith("." + name, StringComparison.Ordinal)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    private static readonly string[] OffWords = { "", "off", "false", "0", "no" };

    internal static PdfFieldChange ToChange(PdfFormField field, IReadOnlyList<string> values)
    {
        string value = values.Count > 0 ? values[0] : string.Empty;
        string Export(string v) =>
            field.Options.Any(o => o.Export == v) ? v : field.Options.FirstOrDefault(o => o.Display == v).Export ?? v;
        switch (field.Kind)
        {
            case PdfFormFieldKind.Text:
                return new PdfFieldChange(field.FullName, Text: value);
            case PdfFormFieldKind.ComboBox:
                return new PdfFieldChange(field.FullName, Text: Export(value));
            case PdfFormFieldKind.ListBox:
                return new PdfFieldChange(field.FullName, Selections: values.Where(v => v.Length > 0).Select(Export).ToList());
            case PdfFormFieldKind.CheckBox:
            {
                bool on = field.Widgets.Any(w => w.OnState == value) || !OffWords.Contains(value.Trim().ToLowerInvariant());
                return new PdfFieldChange(field.FullName, Checked: on);
            }
            case PdfFormFieldKind.RadioGroup:
            {
                if (OffWords.Contains(value.Trim().ToLowerInvariant()) && field.Widgets.All(w => w.OnState != value))
                    return new PdfFieldChange(field.FullName, Choice: "Off");
                // An /Opt export value names the widget at its index (12.7.5.2.4).
                int index = field.Options.ToList().FindIndex(o => o.Export == value);
                string? onState = field.Widgets.Any(w => w.OnState == value) ? value
                    : index >= 0 && index < field.Widgets.Count ? field.Widgets[index].OnState : null;
                return new PdfFieldChange(field.FullName, Choice: onState ?? throw new ArgumentException($"'{value}' is not an option of '{field.FullName}'."));
            }
            default:
                throw new InvalidOperationException($"Form field '{field.FullName}' ({field.Kind}) does not take a value.");
        }
    }

    /// <summary>Writes the changes (and the recalculated fields) as one revision of the document.</summary>
    private static IReadOnlyList<string> Commit(PdfNativeDocument doc, PdfAcroForm form, List<PdfFieldChange> changes, bool validate)
    {
        if (changes.Count == 0) return Array.Empty<string>();
        var prepared = validate ? PdfFormScripts.Prepare(form, changes) : WithCalculations(form, changes);
        var vector = doc.Vector;
        byte[] original = doc.Bytes;
        if (vector.WasRepaired || PdfIncrementalWriter.FindStartXref(original) < 0)
        {
            // A damaged file cannot take an incremental update: it is written whole first.
            doc.Replace(PdfFullWriter.Rewrite(vector));
            vector = doc.Vector;
            original = doc.Bytes;
        }
        doc.Replace(PdfFormFiller.Apply(vector, original, prepared));
        return prepared.Select(c => c.FullName).Distinct().ToList();
    }

    /// <summary>The changes as given, followed by every calculated field they change (calculation order, formatted).</summary>
    private static List<PdfFieldChange> WithCalculations(PdfAcroForm form, List<PdfFieldChange> changes)
    {
        var values = form.Fields.GroupBy(f => f.FullName).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        foreach (var c in changes)
            if ((c.Text ?? c.Choice) is { } v) values[c.FullName] = v;
        var result = new List<PdfFieldChange>(changes);
        foreach (var (name, value) in PdfFormScripts.Recalculate(form, values))
        {
            if (form[name] is not { Kind: PdfFormFieldKind.Text } field) continue;
            var display = PdfFormScripts.Display(PdfFormScripts.Read(field), value);
            result.RemoveAll(c => c.FullName == name);
            result.Add(new PdfFieldChange(name, Text: value, Display: display.Text != value ? display.Text : null, DisplayRed: display.Red));
        }
        return result;
    }

    // ------------------------------------------------------------------ reset

    public ValueTask ResetFormAsync(IPdfDocument document, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        Reset(doc);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A reset-form action without its script: every field that takes a value and is not
    /// read-only goes back to its default (/DV), or empty / off when it has none. Returns the
    /// fields changed.
    /// </summary>
    public static IReadOnlyList<string> Reset(PdfNativeDocument doc, IReadOnlyCollection<string>? onlyFields = null)
    {
        var form = PdfAcroForm.Read(doc.Vector);
        if (form == null) return Array.Empty<string>();
        var changes = new List<PdfFieldChange>();
        foreach (var f in form.Fields)
        {
            if (f.IsReadOnly || f.Kind is PdfFormFieldKind.PushButton or PdfFormFieldKind.Signature) continue;
            if (onlyFields != null && !onlyFields.Contains(f.FullName)) continue;
            string dv = f.DefaultValue;
            changes.Add(f.Kind switch
            {
                PdfFormFieldKind.CheckBox => new PdfFieldChange(f.FullName, Checked: dv.Length > 0 && dv != "Off"),
                PdfFormFieldKind.RadioGroup => new PdfFieldChange(f.FullName, Choice: f.Widgets.Any(w => w.OnState == dv) ? dv : "Off"),
                PdfFormFieldKind.ListBox => new PdfFieldChange(f.FullName, Selections: dv.Length > 0 ? new[] { dv } : Array.Empty<string>()),
                _ => new PdfFieldChange(f.FullName, Text: dv),
            });
        }
        return Commit(doc, form, changes, validate: false);
    }

    // ------------------------------------------------------------------ XFDF and FDF

    public async ValueTask ExportFormDataXfdfAsync(IPdfDocument document, string targetXfdfPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        var xfdf = ExportXfdf(doc.Vector, Path.GetFileName(doc.FilePath));
        await PdfNativeEngine.WriteAsync(targetXfdfPath, Encoding.UTF8.GetBytes(xfdf), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ImportFormDataXfdfAsync(IPdfDocument document, string sourceXfdfPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        if (!File.Exists(sourceXfdfPath)) throw new FileNotFoundException($"Form data file not found: {sourceXfdfPath}", sourceXfdfPath);
        byte[] data = await File.ReadAllBytesAsync(sourceXfdfPath, cancellationToken).ConfigureAwait(false);
        Import(doc, data);
    }

    /// <summary>Writes the form data as FDF (12.7.8): the field hierarchy with each value.</summary>
    public async ValueTask ExportFormDataFdfAsync(IPdfDocument document, string targetFdfPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        cancellationToken.ThrowIfCancellationRequested();
        await PdfNativeEngine.WriteAsync(targetFdfPath, ExportFdf(doc.Vector, Path.GetFileName(doc.FilePath)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fields that carry form data: every field except push buttons and signatures.</summary>
    private static IEnumerable<PdfFormField> DataFields(PdfAcroForm form) =>
        form.Fields.Where(f => f.Kind is not (PdfFormFieldKind.PushButton or PdfFormFieldKind.Signature));

    private static IReadOnlyList<string> DataValues(PdfFormField f) =>
        f.Kind == PdfFormFieldKind.ListBox ? f.Values : new[] { f.Value };

    /// <summary>XFDF with the fields nested by name part, as Acrobat writes it.</summary>
    public static string ExportXfdf(PdfVectorDocument document, string? href = null)
    {
        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var root = new XElement(ns + "fields");
        var form = PdfAcroForm.Read(document);
        if (form != null)
        {
            foreach (var f in DataFields(form))
            {
                var parent = root;
                var parts = f.FullName.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    var existing = parent.Elements(ns + "field").FirstOrDefault(e => (string?)e.Attribute("name") == parts[i]);
                    if (existing == null)
                    {
                        existing = new XElement(ns + "field", new XAttribute("name", parts[i]));
                        parent.Add(existing);
                    }
                    parent = existing;
                }
                foreach (var v in DataValues(f)) parent.Add(new XElement(ns + "value", v));
            }
        }
        var xfdf = new XElement(ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"));
        if (!string.IsNullOrEmpty(href)) xfdf.Add(new XElement(ns + "f", new XAttribute("href", href)));
        xfdf.Add(root);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), xfdf).Declaration + "\n" + xfdf;
    }

    /// <summary>FDF with the field hierarchy (/T, /Kids) and values: text strings, names for buttons, arrays for list selections.</summary>
    public static byte[] ExportFdf(PdfVectorDocument document, string? href = null)
    {
        var form = PdfAcroForm.Read(document);
        var tree = new Dictionary<string, object>(StringComparer.Ordinal); // name part → subtree or field
        var order = new List<string>();
        if (form != null)
        {
            foreach (var f in DataFields(form))
            {
                var node = tree;
                var parts = f.FullName.Split('.');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!node.TryGetValue(parts[i], out var child) || child is not Dictionary<string, object> sub)
                    {
                        sub = new Dictionary<string, object>(StringComparer.Ordinal);
                        node[parts[i]] = sub;
                    }
                    node = sub;
                }
                node[parts[^1]] = f;
            }
        }
        PdfArray Fields(Dictionary<string, object> node)
        {
            var items = new List<PdfObject>();
            foreach (var (name, value) in node)
            {
                var entries = new Dictionary<string, PdfObject> { ["T"] = PdfObjectWriter.TextString(name) };
                if (value is Dictionary<string, object> sub) entries["Kids"] = Fields(sub);
                else if (value is PdfFormField f) entries["V"] = f.Kind switch
                {
                    PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioGroup => new PdfName(f.Value.Length > 0 ? f.Value : "Off"),
                    PdfFormFieldKind.ListBox when f.Values.Count != 1 => new PdfArray(f.Values.Select(v => (PdfObject)PdfObjectWriter.TextString(v)).ToList()),
                    PdfFormFieldKind.ListBox => PdfObjectWriter.TextString(f.Values[0]),
                    _ => PdfObjectWriter.TextString(f.Value),
                };
                items.Add(new PdfDictionary(entries));
            }
            return new PdfArray(items);
        }
        var fdf = new Dictionary<string, PdfObject> { ["Fields"] = Fields(tree) };
        if (!string.IsNullOrEmpty(href)) fdf["F"] = PdfObjectWriter.TextString(href);
        var catalog = new PdfDictionary(new Dictionary<string, PdfObject> { ["FDF"] = new PdfDictionary(fdf) });

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%FDF-1.2\n%âãÏÓ\n1 0 obj\n");
        PdfObjectWriter.Write(ms, catalog);
        W("\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// Fills the fields named in XFDF or FDF data (told apart by content). Fields the document
    /// does not have, and read-only ones, are skipped and reported; the rest are set in one
    /// revision, as given (no keystroke rules), and calculated fields recalculated.
    /// </summary>
    public static PdfFormImportResult Import(PdfNativeDocument doc, byte[] data)
    {
        var values = LooksLikeFdf(data) ? ReadFdf(data) : ReadXfdf(data);
        var form = PdfAcroForm.Read(doc.Vector) ?? throw new KeyNotFoundException("The document has no form fields.");
        var unknown = new List<string>();
        var readOnly = new List<string>();
        var changes = new List<PdfFieldChange>();
        foreach (var (name, v) in values)
        {
            var field = Find(form, name);
            if (field == null || field.Kind is PdfFormFieldKind.PushButton or PdfFormFieldKind.Signature)
            {
                unknown.Add(name);
                continue;
            }
            if (field.IsReadOnly)
            {
                readOnly.Add(field.FullName);
                continue;
            }
            changes.RemoveAll(c => c.FullName == field.FullName);
            changes.Add(ToChange(field, v));
        }
        Commit(doc, form, changes, validate: false);
        return new PdfFormImportResult(changes.Count, unknown, readOnly);
    }

    private static bool LooksLikeFdf(byte[] data)
    {
        int n = Math.Min(data.Length, 1024);
        return Encoding.Latin1.GetString(data, 0, n).Contains("%FDF-", StringComparison.Ordinal);
    }

    /// <summary>(full name, values) pairs from XFDF: nested &lt;field&gt; elements build the name, or a flat dotted name.</summary>
    public static List<(string Name, IReadOnlyList<string> Values)> ReadXfdf(byte[] data)
    {
        XDocument xml;
        try
        {
            using var ms = new MemoryStream(data);
            xml = XDocument.Load(ms, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("The form data is not readable XFDF.", ex);
        }
        var result = new List<(string, IReadOnlyList<string>)>();
        void Walk(XElement e, string prefix)
        {
            foreach (var f in e.Elements().Where(x => x.Name.LocalName == "field"))
            {
                string? part = (string?)f.Attribute("name");
                if (string.IsNullOrEmpty(part)) continue;
                string name = prefix.Length == 0 ? part : prefix + "." + part;
                var vals = f.Elements().Where(x => x.Name.LocalName is "value" or "value-richtext").Select(x => x.Value).ToList();
                if (vals.Count > 0) result.Add((name, vals));
                Walk(f, name);
            }
        }
        foreach (var fields in xml.Descendants().Where(x => x.Name.LocalName == "fields"))
            Walk(fields, string.Empty);
        return result;
    }

    /// <summary>(full name, values) pairs from FDF: the /FDF /Fields tree, names from /T, values from /V.</summary>
    public static List<(string Name, IReadOnlyList<string> Values)> ReadFdf(byte[] data)
    {
        var objects = new Dictionary<int, PdfObject>();
        PdfDictionary? fdf = null;
        try
        {
            var lexer = new PdfLexer(new MemoryByteSource(data));
            var parser = new PdfParser();
            // Objects are read in order ("n g obj ... endobj"); the trailer's /Root is the catalog.
            while (true)
            {
                var t = lexer.NextToken();
                if (t.Type == PdfTokenType.EndOfFile) break;
                if (t.Type == PdfTokenType.Integer)
                {
                    long at = lexer.Position;
                    var g = lexer.NextToken();
                    var kw = lexer.NextToken();
                    if (g.Type == PdfTokenType.Integer && kw is { Type: PdfTokenType.Keyword, TextValue: "obj" })
                    {
                        if (parser.ParseObject(lexer) is { } obj) objects[(int)t.IntValue] = obj;
                        continue;
                    }
                    lexer.Position = at;
                }
                else if (t is { Type: PdfTokenType.Keyword, TextValue: "trailer" } && parser.ParseObject(lexer) is PdfDictionary trailer)
                {
                    if (Resolve(trailer["Root"]) is PdfDictionary catalog && Resolve(catalog["FDF"]) is PdfDictionary f) fdf = f;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException("The form data is not readable FDF.", ex);
        }
        PdfObject? Resolve(PdfObject? o) => o is PdfIndirectRef r ? objects.GetValueOrDefault(r.ObjectNumber) : o;
        fdf ??= objects.Values.OfType<PdfDictionary>().Select(d => Resolve(d["FDF"])).OfType<PdfDictionary>().FirstOrDefault()
            ?? throw new InvalidDataException("The form data has no /FDF dictionary.");

        var result = new List<(string, IReadOnlyList<string>)>();
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Walk(PdfArray? fields, string prefix, int depth)
        {
            if (fields == null || depth > 32) return;
            foreach (var item in fields)
            {
                if (Resolve(item) is not PdfDictionary field || !visited.Add(field)) continue;
                string? part = PdfTextStrings.Decode(Resolve(field["T"]));
                string name = string.IsNullOrEmpty(part) ? prefix : prefix.Length == 0 ? part : prefix + "." + part;
                switch (Resolve(field["V"]))
                {
                    case PdfName n: result.Add((name, new[] { n.Value })); break;
                    case PdfString s: result.Add((name, new[] { PdfTextStrings.Decode(s.RawBytes.Span) })); break;
                    case PdfArray a: result.Add((name, a.Select(Resolve).Select(v => v is PdfString vs ? PdfTextStrings.Decode(vs.RawBytes.Span) : v is PdfName vn ? vn.Value : string.Empty).ToList())); break;
                }
                Walk(Resolve(field["Kids"]) as PdfArray, name, depth + 1);
            }
        }
        Walk(Resolve(fdf["Fields"]) as PdfArray, string.Empty, 0);
        return result.Where(x => x.Item1.Length > 0).ToList();
    }

    // ------------------------------------------------------------------ flattening

    public async ValueTask FlattenFormFieldsAsync(IPdfDocument document, string targetPath, CancellationToken cancellationToken = default)
    {
        var doc = PdfNativeEngine.Require(document);
        PdfNativeEngine.RefuseOriginal(doc, targetPath, "A flattened form");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = Flatten(doc.Vector, doc.Bytes);
        await PdfNativeEngine.WriteAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The document with every visible widget drawn into its page as the screen shows it (the
    /// generated appearance where the form asks for one or a widget has none, else the widget's
    /// own for its state) and removed from the form; an emptied form goes altogether. Other
    /// annotations stay. Written whole, so the fillable revision does not remain in the file.
    /// </summary>
    public static byte[] Flatten(PdfVectorDocument document, byte[] bytes)
    {
        var update = new PdfUpdate(document, bytes);
        if (PdfAcroForm.Read(document) is { } form)
        {
            var r = document.Resolver;
            foreach (var field in form.Fields)
            {
                foreach (var w in field.Widgets)
                {
                    if (update.Get(w.ObjectNumber) is not PdfDictionary widget) continue;
                    var normal = r.Resolve((r.Resolve(widget["AP"]) as PdfDictionary)?["N"]);
                    string? state = null;
                    if (normal is PdfDictionary states)
                    {
                        state = widget.GetName("AS") ?? (states.ContainsKey(field.Value) ? field.Value : "Off");
                        normal = r.Resolve(states[state]);
                    }
                    if (PdfFormFiller.GenerateAppearance(form, field, w, r, normal is PdfStream) is { } generated)
                    {
                        var ap = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = update.AddRef(generated) });
                        update.Set(w.ObjectNumber, PdfUpdate.With(widget, ("AP", ap), ("AS", null)));
                    }
                    else if (state != null && widget.GetName("AS") == null)
                    {
                        update.Set(w.ObjectNumber, PdfUpdate.With(widget, ("AS", new PdfName(state))));
                    }
                }
            }
        }
        var editor = new PdfAnnotationEditor(update);
        editor.FlattenAll(widgetsOnly: true);
        editor.Finish();
        return update.Commit(forceFullRewrite: true);
    }
}
