using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Parsing;

namespace PdfEngine.Vector.Forms;

public enum PdfFormFieldKind { Text, CheckBox, RadioGroup, PushButton, ComboBox, ListBox, Signature }

/// <summary>One widget annotation (the visible part) of a field.</summary>
public sealed record PdfFormWidget(
    int ObjectNumber,
    int PageNumber,
    PdfRect Rect,
    string? OnState,
    string? AppearanceState,
    bool Hidden,
    int Rotation,
    double[]? Background,
    double[]? Border,
    double BorderWidth);

/// <summary>
/// A terminal form field (ISO 32000-2 12.7.4) with its inherited attributes resolved: the full
/// dotted name, kind, flags, value, options and widgets.
/// </summary>
public sealed class PdfFormField
{
    public required string FullName { get; init; }
    public required PdfFormFieldKind Kind { get; init; }
    public required int ObjectNumber { get; init; }
    public int Flags { get; init; }
    public string Value { get; init; } = string.Empty;
    /// <summary>Selected values of a multi-select list box.</summary>
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
    public string DefaultValue { get; init; } = string.Empty;
    /// <summary>Choice options: (export value, display text).</summary>
    public IReadOnlyList<(string Export, string Display)> Options { get; init; } = Array.Empty<(string, string)>();
    public int MaxLength { get; init; }
    /// <summary>0 left, 1 centred, 2 right (/Q).</summary>
    public int Quadding { get; init; }
    public string? DefaultAppearance { get; init; }
    public string? Tooltip { get; init; }
    public required IReadOnlyList<PdfFormWidget> Widgets { get; init; }

    public bool IsReadOnly => (Flags & 1) != 0;
    public bool IsRequired => (Flags & 2) != 0;
    public bool IsMultiline => Kind == PdfFormFieldKind.Text && (Flags & (1 << 12)) != 0;
    public bool IsPassword => Kind == PdfFormFieldKind.Text && (Flags & (1 << 13)) != 0;
    public bool IsComb => Kind == PdfFormFieldKind.Text && (Flags & (1 << 24)) != 0 && MaxLength > 0;
    public bool IsEditableCombo => Kind == PdfFormFieldKind.ComboBox && (Flags & (1 << 18)) != 0;
    public bool IsMultiSelect => Kind == PdfFormFieldKind.ListBox && (Flags & (1 << 21)) != 0;
    /// <summary>Radio buttons with the same on-state turn on and off together (/Ff bit 26).</summary>
    public bool RadiosInUnison => Kind == PdfFormFieldKind.RadioGroup && (Flags & (1 << 25)) != 0;
    /// <summary>
    /// The field's JavaScript actions (/AA, 12.6.3 Table 199) by trigger: K keystroke, F format,
    /// V validate, C calculate. Read, never run: <see cref="PdfFormScripts"/> recognises the
    /// standard Acrobat functions they call.
    /// </summary>
    public IReadOnlyDictionary<string, string> Scripts { get; init; } = new Dictionary<string, string>();
    /// <summary>A signature field whose /V holds a signature (an empty one is waiting to be signed).</summary>
    public bool IsSigned { get; init; }
    public bool IsChecked => Kind is PdfFormFieldKind.CheckBox && Value.Length > 0 && Value != "Off";
}

/// <summary>The document's interactive form (/Root /AcroForm), read into terminal fields.</summary>
public sealed class PdfAcroForm
{
    public IReadOnlyList<PdfFormField> Fields { get; }
    public PdfDictionary? DefaultResources { get; }
    public string? DefaultAppearance { get; }
    public bool NeedAppearances { get; }
    public int ObjectNumber { get; }
    /// <summary>Fields with calculate scripts, in the order they are recalculated (/CO), by full name.</summary>
    public IReadOnlyList<string> CalculationOrder { get; private init; } = Array.Empty<string>();

    private PdfAcroForm(IReadOnlyList<PdfFormField> fields, PdfDictionary? dr, string? da, bool needAppearances, int objectNumber)
    {
        Fields = fields;
        DefaultResources = dr;
        DefaultAppearance = da;
        NeedAppearances = needAppearances;
        ObjectNumber = objectNumber;
    }

    public PdfFormField? this[string fullName] => Fields.FirstOrDefault(f => f.FullName == fullName);

    /// <summary>The form, or null when the document has none (or no fields).</summary>
    public static PdfAcroForm? Read(PdfVectorDocument document)
    {
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer;
        if (r.Resolve(trailer?["Root"]) is not PdfDictionary root)
            return null;
        var acroRef = root["AcroForm"];
        if (r.Resolve(acroRef) is not PdfDictionary acro || r.Resolve(acro["Fields"]) is not PdfArray fields)
            return null;

        // Widget object number → page.
        var pageOf = new Dictionary<int, int>();
        foreach (var page in document.PageTree.Pages)
            if (r.Resolve(page.Dictionary["Annots"]) is PdfArray annots)
                foreach (var a in annots)
                    if (a is PdfIndirectRef ar) pageOf.TryAdd(ar.ObjectNumber, page.PageNumber);

        var result = new List<PdfFormField>();
        var visited = new HashSet<int>();
        var inherited = new Inherited(null, null, null, null, null, null, null, 0);
        foreach (var f in fields)
            Walk(f, string.Empty, inherited, r, pageOf, result, visited, depth: 0);

        // /CO lists the calculated fields in order; fields with a calculate script but not in it follow.
        var byNumber = result.GroupBy(f => f.ObjectNumber).ToDictionary(g => g.Key, g => g.First().FullName);
        var order = new List<string>();
        if (r.Resolve(acro["CO"]) is PdfArray co)
            foreach (var c in co)
                if (c is PdfIndirectRef cr && byNumber.TryGetValue(cr.ObjectNumber, out var n) && !order.Contains(n)) order.Add(n);
        foreach (var f in result)
            if (f.Scripts.ContainsKey("C") && !order.Contains(f.FullName)) order.Add(f.FullName);

        return new PdfAcroForm(result,
            r.Resolve(acro["DR"]) as PdfDictionary,
            Text(r.Resolve(acro["DA"])),
            r.Resolve(acro["NeedAppearances"]) is PdfBoolean { Value: true },
            acroRef is PdfIndirectRef ai ? ai.ObjectNumber : -1) { CalculationOrder = order };
    }

    private sealed record Inherited(string? FieldType, long? Flags, PdfObject? Value, PdfObject? DefaultValue, string? DA, long? Q, long? MaxLen, int Dummy);

    private static void Walk(PdfObject node, string parentName, Inherited up, PdfObjectResolver r, Dictionary<int, int> pageOf,
        List<PdfFormField> result, HashSet<int> visited, int depth)
    {
        if (depth > 32 || node is not PdfIndirectRef nref || !visited.Add(nref.ObjectNumber) || r.Resolve(nref) is not PdfDictionary dict)
            return;
        string? partial = Text(r.Resolve(dict["T"]));
        string name = partial == null ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;
        var here = new Inherited(
            dict.GetName("FT") ?? up.FieldType,
            r.Resolve(dict["Ff"]) is PdfInteger ff ? ff.Value : up.Flags,
            dict["V"] != null ? r.Resolve(dict["V"]) : up.Value,
            dict["DV"] != null ? r.Resolve(dict["DV"]) : up.DefaultValue,
            Text(r.Resolve(dict["DA"])) ?? up.DA,
            r.Resolve(dict["Q"]) is PdfInteger q ? q.Value : up.Q,
            r.Resolve(dict["MaxLen"]) is PdfInteger ml ? ml.Value : up.MaxLen,
            0);

        var kids = r.Resolve(dict["Kids"]) as PdfArray;
        // Kids that are fields (have /T) make this a non-terminal field; widget kids do not.
        var childFields = kids?.Where(k => r.Resolve(k) is PdfDictionary kd && kd.ContainsKey("T")).ToList() ?? new List<PdfObject>();
        if (childFields.Count > 0)
        {
            foreach (var k in childFields)
                Walk(k, name, here, r, pageOf, result, visited, depth + 1);
            // Mixed kids (rare): widgets beside fields belong to this one.
            if (kids!.Count == childFields.Count) return;
        }

        var widgets = new List<PdfFormWidget>();
        if (dict.GetName("Subtype") == "Widget" || kids == null)
            AddWidget(nref.ObjectNumber, dict, r, pageOf, widgets);
        if (kids != null)
            foreach (var k in kids)
                if (k is PdfIndirectRef kr && r.Resolve(kr) is PdfDictionary kd && !kd.ContainsKey("T") && visited.Add(kr.ObjectNumber))
                    AddWidget(kr.ObjectNumber, kd, r, pageOf, widgets);
        if (here.FieldType == null)
            return;

        long flags = here.Flags ?? 0;
        var kind = here.FieldType switch
        {
            "Tx" => PdfFormFieldKind.Text,
            "Btn" => (flags & (1 << 16)) != 0 ? PdfFormFieldKind.PushButton : (flags & (1 << 15)) != 0 ? PdfFormFieldKind.RadioGroup : PdfFormFieldKind.CheckBox,
            "Ch" => (flags & (1 << 17)) != 0 ? PdfFormFieldKind.ComboBox : PdfFormFieldKind.ListBox,
            "Sig" => PdfFormFieldKind.Signature,
            _ => (PdfFormFieldKind?)null,
        };
        if (kind == null)
            return;

        var options = new List<(string, string)>();
        if (r.Resolve(dict["Opt"]) is PdfArray opt)
        {
            foreach (var o in opt)
            {
                var item = r.Resolve(o);
                if (item is PdfArray pair && pair.Count >= 2)
                    options.Add((Text(r.Resolve(pair[0])) ?? string.Empty, Text(r.Resolve(pair[1])) ?? string.Empty));
                else if (Text(item) is { } t)
                    options.Add((t, t));
            }
        }

        var values = here.Value is PdfArray va ? va.Select(v => Text(r.Resolve(v)) ?? string.Empty).ToList() : new List<string>();
        string value = kind switch
        {
            PdfFormFieldKind.CheckBox or PdfFormFieldKind.RadioGroup => NameOrText(here.Value) ?? "Off",
            _ => values.Count > 0 ? values[0] : Text(here.Value) ?? string.Empty,
        };
        if (kind == PdfFormFieldKind.CheckBox && value == "Off" && widgets.FirstOrDefault(w => w.AppearanceState is { } s && s != "Off") is { } on)
            value = on.AppearanceState!; // /V missing but the widget shows checked
        if (values.Count == 0 && value.Length > 0 && kind is PdfFormFieldKind.ListBox or PdfFormFieldKind.ComboBox)
            values.Add(value);

        result.Add(new PdfFormField
        {
            FullName = name,
            Kind = kind.Value,
            ObjectNumber = nref.ObjectNumber,
            Flags = (int)flags,
            Value = value,
            Values = values,
            IsSigned = kind == PdfFormFieldKind.Signature && here.Value is PdfDictionary,
            DefaultValue = NameOrText(here.DefaultValue) ?? string.Empty,
            Options = options,
            MaxLength = (int)(here.MaxLen ?? 0),
            Quadding = (int)(here.Q ?? 0),
            DefaultAppearance = here.DA,
            Tooltip = Text(r.Resolve(dict["TU"])),
            Scripts = ReadScripts(dict, kids, r),
            Widgets = widgets,
        });
    }

    /// <summary>The field's /AA JavaScript, from the field or (merged or single-widget fields) its widget.</summary>
    private static Dictionary<string, string> ReadScripts(PdfDictionary field, PdfArray? kids, PdfObjectResolver r)
    {
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        void From(PdfDictionary? d)
        {
            if (r.Resolve(d?["AA"]) is not PdfDictionary aa) return;
            foreach (var trigger in new[] { "K", "F", "V", "C" })
            {
                if (scripts.ContainsKey(trigger) || r.Resolve(aa[trigger]) is not PdfDictionary action || action.GetName("S") != "JavaScript")
                    continue;
                string? js = r.Resolve(action["JS"]) switch
                {
                    PdfString s => s.AsDecodedString(),
                    PdfStream st => DecodeScript(st, r),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(js)) scripts[trigger] = js;
            }
        }
        From(field);
        if (kids is { Count: 1 }) From(r.Resolve(kids[0]) as PdfDictionary);
        return scripts;
    }

    private static string? DecodeScript(PdfStream stream, PdfObjectResolver r)
    {
        try
        {
            byte[] data = new Streams.PdfStreamDecoder(null, r.Resolve).DecodeStream(stream);
            // UTF-16BE with a BOM, else PDFDocEncoding/Latin-1 (7.9.2.2).
            return data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF
                ? System.Text.Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)
                : System.Text.Encoding.Latin1.GetString(data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void AddWidget(int number, PdfDictionary w, PdfObjectResolver r, Dictionary<int, int> pageOf, List<PdfFormWidget> into)
    {
        if (r.Resolve(w["Rect"]) is not PdfArray rect || rect.Count < 4)
            return;
        double N(int i) => r.Resolve(rect[i]) is { } o && o.TryGetNumber(out double v) ? v : 0;
        double x0 = Math.Min(N(0), N(2)), y0 = Math.Min(N(1), N(3)), x1 = Math.Max(N(0), N(2)), y1 = Math.Max(N(1), N(3));
        int page = pageOf.TryGetValue(number, out int p) ? p : 0;
        string? onState = null;
        if (r.Resolve(w["AP"]) is PdfDictionary ap && r.Resolve(ap["N"]) is PdfDictionary normal)
            onState = normal.Entries.Keys.FirstOrDefault(k => k != "Off");
        long annotFlags = r.Resolve(w["F"]) is PdfInteger fl ? fl.Value : 0;
        var mk = r.Resolve(w["MK"]) as PdfDictionary;
        double[]? Colour(string key) => r.Resolve(mk?[key]) is PdfArray a && a.Count > 0
            ? a.Select(v => r.Resolve(v) is { } o && o.TryGetNumber(out double d) ? d : 0).ToArray() : null;
        double bw = r.Resolve(w["BS"]) is PdfDictionary bs && r.Resolve(bs["W"]) is { } bwo && bwo.TryGetNumber(out double bwv) ? bwv : 1;
        into.Add(new PdfFormWidget(number, page, new PdfRect(x0, y0, x1 - x0, y1 - y0), onState, w.GetName("AS"),
            (annotFlags & 2) != 0, (int)(r.Resolve(mk?["R"]) is PdfInteger rot ? rot.Value : 0), Colour("BG"), Colour("BC"), bw));
    }

    internal static string? Text(PdfObject? o) => o switch
    {
        PdfString s => s.AsDecodedString(),
        PdfName n => n.Value,
        _ => null,
    };

    private static string? NameOrText(PdfObject? o) => o switch
    {
        PdfName n => n.Value,
        PdfString s => s.AsDecodedString(),
        _ => null,
    };
}
