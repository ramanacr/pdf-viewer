using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.PdfA;

internal sealed partial class PdfAChecker
{
    private static readonly HashSet<string> AnnotationsP1 = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Highlight", "Underline", "Squiggly", "StrikeOut", "Stamp", "Ink", "Popup", "Widget", "PrinterMark", "TrapNet",
    };

    private static readonly HashSet<string> AnnotationsP2 = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline", "Squiggly", "StrikeOut", "Stamp", "Caret", "Ink",
        "Popup", "FileAttachment", "Widget", "PrinterMark", "TrapNet", "Watermark", "Redact",
    };

    // ------------------------------------------------------------------ annotations (P1 6.5, P2 6.3) and forms (P1 6.9, P2 6.4)

    private void CheckAnnotationsAndForms()
    {
        foreach (var page in _doc.PageTree.Pages)
        {
            string where = $"page {page.PageNumber}";
            if (_r.Resolve(page.Dictionary["Annots"]) is not PdfArray annots) continue;
            foreach (var item in annots)
                if (_r.Resolve(item) is PdfDictionary annot) CheckAnnotation(annot, where);
        }
        if (Catalog != null && _r.Resolve(Catalog["AcroForm"]) is PdfDictionary form)
        {
            if (_r.Resolve(form["NeedAppearances"]) is PdfBoolean { Value: true })
                Fail("6.9", "6.4.1", 3, "The form asks readers to generate field appearances (NeedAppearances true).");
            if (form.ContainsKey("XFA")) Fail("6.9", "6.4.2", 1, "The form has an XFA part.");
            if (_r.Resolve(form["Fields"]) is PdfArray fields)
                foreach (var f in fields) if (_r.Resolve(f) is PdfDictionary field) CheckField(field, 0);
        }
        if (Catalog != null && _part >= 2 && _r.Resolve(Catalog["NeedsRendering"]) is PdfBoolean { Value: true })
            Fail(null, "6.4.2", 2, "The catalog asks for the document to be rendered from XFA (NeedsRendering true).");
    }

    private void CheckField(PdfDictionary field, int depth)
    {
        if (depth > 32 || !_checkedObjects.Add(field)) return;
        if (field.ContainsKey("AA")) Fail("6.9", "6.4.1", 2, "A form field has additional actions (/AA).");
        if (_r.Resolve(field["Kids"]) is PdfArray kids)
            foreach (var k in kids) if (_r.Resolve(k) is PdfDictionary kid) CheckField(kid, depth + 1);
    }

    private void CheckAnnotation(PdfDictionary annot, string where)
    {
        string? subtype = annot.GetName("Subtype");
        var allowed = _part == 1 ? AnnotationsP1 : AnnotationsP2;
        if (subtype == null || !allowed.Contains(subtype))
        {
            Fail("6.5.2", "6.3.1", 1, $"A /{subtype} annotation is not allowed.", where);
            return;
        }
        // Dictionaries (P1 6.5.3, P2 6.3.2): printable and not hidden.
        if (subtype != "Popup")
        {
            long flags = _r.Resolve(annot["F"]) is PdfInteger f ? f.Value : -1;
            if (flags < 0) Fail("6.5.3", "6.3.2", 1, $"A /{subtype} annotation has no flags (/F).", where);
            else if ((flags & 4) == 0 || (flags & (1 | 2 | 32 | 256)) != 0)
                Fail("6.5.3", "6.3.2", 2, $"A /{subtype} annotation is not set to print, or is hidden.", where);
        }
        if (_part == 1 && annot["CA"] is { } ca && Num(ca) != 1) Fail("6.5.3", 3, $"A /{subtype} annotation is not opaque (/CA).", where);
        if ((annot.ContainsKey("C") || annot.ContainsKey("IC")) && _intentSpace != "RGB ")
            Fail("6.5.3", "6.3.3", 4, $"A /{subtype} annotation has a /C or /IC colour, which needs an RGB output intent.", where);
        if (subtype == "Widget" && (annot.ContainsKey("A") || annot.ContainsKey("AA")))
            Fail("6.9", "6.4.1", 1, "A widget annotation has an action (/A or /AA).", where);
        else if (_part >= 2 && annot.ContainsKey("AA"))
            Fail(null, "6.5.2", 3, $"A /{subtype} annotation has additional actions (/AA).", where);
        if (subtype == "Widget" && annot.ContainsKey("AA") && _part == 1) { }

        // Appearances (P1 6.5.3, P2 6.3.3): a normal appearance only; a stream except for button widgets.
        var rect = _r.Resolve(annot["Rect"]) as PdfArray;
        bool zeroArea = rect != null && rect.Count >= 4 && Math.Abs(Num(rect[2]) - Num(rect[0])) < 1e-9 && Math.Abs(Num(rect[3]) - Num(rect[1])) < 1e-9;
        if (subtype is "Popup" or "Link" || zeroArea) return;
        if (_r.Resolve(annot["AP"]) is not PdfDictionary ap)
        {
            Fail(subtype == "Widget" ? "6.9" : "6.5.3", "6.3.3", 1, $"A /{subtype} annotation has no appearance (/AP).", where);
            return;
        }
        if (ap.Count != 1 || !ap.ContainsKey("N")) Fail("6.5.3", "6.3.3", 2, $"A /{subtype} annotation's appearance has entries other than /N.", where);
        var normal = _r.Resolve(ap["N"]);
        bool button = subtype == "Widget" && (FieldType(annot) == "Btn");
        if (button ? normal is not PdfDictionary : normal is not PdfStream)
            Fail("6.5.3", "6.3.3", 3, button ? "A button widget's normal appearance is not a dictionary of states." : $"A /{subtype} annotation's normal appearance is not a stream.", where);
        var usage = new Usage();
        foreach (var appearance in normal is PdfDictionary states ? states.Entries.Values.Select(_r.Resolve).OfType<PdfStream>() : normal is PdfStream one ? new[] { one } : Array.Empty<PdfStream>())
            CheckXObject(WithFormSubtype(appearance), where + ", annotation appearance", usage, 1);
    }

    /// <summary>Appearance streams are form XObjects even when they omit /Subtype.</summary>
    private static PdfStream WithFormSubtype(PdfStream s) =>
        s.Dictionary.GetName("Subtype") == "Form" ? s : s with { Dictionary = new PdfDictionary(new Dictionary<string, PdfObject>(s.Dictionary.Entries) { ["Subtype"] = new PdfName("Form") }) };

    private string? FieldType(PdfDictionary annot)
    {
        var node = annot;
        for (int i = 0; i < 32 && node != null; i++)
        {
            if (node.GetName("FT") is { } ft) return ft;
            node = _r.Resolve(node["Parent"]) as PdfDictionary;
        }
        return null;
    }

    // ------------------------------------------------------------------ actions (P1 6.6, P2 6.5)

    // PDF/A-1 with its corrigenda: also Hide and the deprecated set-state and no-op actions.
    private static readonly HashSet<string> ForbiddenP1 = new(StringComparer.Ordinal) { "Launch", "Sound", "Movie", "ResetForm", "ImportData", "JavaScript", "Hide", "SetState", "NoOp" };
    private static readonly HashSet<string> ForbiddenP2 = new(StringComparer.Ordinal)
        { "Launch", "Sound", "Movie", "ResetForm", "ImportData", "Hide", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "JavaScript", "SetState", "NoOp" };
    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
        { "GoTo", "GoToR", "GoToE", "Launch", "Thread", "URI", "Sound", "Movie", "Hide", "Named", "SubmitForm", "ResetForm", "ImportData", "JavaScript", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "SetState", "NoOp" };

    private void CheckActions()
    {
        if (Catalog != null && Catalog.ContainsKey("AA")) Fail("6.6.2", "6.5.2", 1, "The document catalog has additional actions (/AA).");
        if (Catalog != null && _r.Resolve(Catalog["Names"]) is PdfDictionary names && names.ContainsKey("JavaScript"))
            Fail("6.6.1", "6.5.1", 1, "The document has document-level JavaScript.");
        foreach (var (number, obj) in AllObjects())
        {
            if (obj is not PdfDictionary d) continue;
            if (d.GetName("Type") == "Action" || (d.ContainsKey("S") && d.GetName("S") is { } s && KnownActions.Contains(s))) CheckAction(d, Ref(number));
            foreach (var key in new[] { "A", "OpenAction", "Next" })
                if (d[key] is PdfDictionary direct && direct.ContainsKey("S")) CheckAction(direct, Ref(number));
        }
    }

    private void CheckAction(PdfDictionary action, string where)
    {
        if (!_checkedObjects.Add(action)) return;
        string? s = action.GetName("S");
        if (s == null) return;
        var forbidden = _part == 1 ? ForbiddenP1 : ForbiddenP2;
        if (forbidden.Contains(s) || !KnownActions.Contains(s)) Fail("6.6.1", "6.5.1", 1, $"A /{s} action is not allowed.", where);
        else if (s == "Named" && action.GetName("N") is { } n && n is not ("NextPage" or "PrevPage" or "FirstPage" or "LastPage"))
            Fail("6.6.1", "6.5.1", 2, $"The named action /{n} is not allowed.", where);
    }

    // ------------------------------------------------------------------ optional content, embedded files, permissions

    private void CheckOptionalContent()
    {
        if (Catalog == null || _r.Resolve(Catalog["OCProperties"]) is not PdfDictionary oc) return;
        if (_part == 1) { Fail("6.1.13", 1, "The document has optional content, which PDF/A-1 does not allow."); return; }
        var configs = new List<PdfDictionary>();
        if (_r.Resolve(oc["D"]) is PdfDictionary d) configs.Add(d);
        if (_r.Resolve(oc["Configs"]) is PdfArray more) configs.AddRange(more.Items.Select(_r.Resolve).OfType<PdfDictionary>());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var config in configs)
        {
            if (_r.Resolve(config["Name"]) is not PdfString name) Fail(null, "6.9", 1, "An optional content configuration has no /Name.");
            else if (!names.Add(name.AsDecodedString())) Fail(null, "6.9", 2, "Two optional content configurations have the same name.");
            if (config.ContainsKey("AS")) Fail(null, "6.9", 4, "An optional content configuration has an /AS entry.");
        }
        if (_r.Resolve(oc["OCGs"]) is PdfArray groups)
        {
            foreach (var g in groups)
                if (_r.Resolve(g) is PdfDictionary group && _r.Resolve(group["Name"]) is not PdfString)
                    Fail(null, "6.9", 1, "An optional content group has no /Name.");
            // Every group is in the default configuration's /Order.
            if (configs.Count > 0 && _r.Resolve(configs[0]["Order"]) is PdfArray order)
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
                if (groups.OfType<PdfIndirectRef>().Any(g => !listed.Contains(g.ObjectNumber)))
                    Fail(null, "6.9", 3, "An optional content group is missing from the default configuration's /Order.");
            }
        }
    }

    private void CheckEmbeddedFiles()
    {
        if (Catalog == null) return;
        var specs = new List<PdfDictionary>();
        var specNumbers = new List<int>();
        var associated = new HashSet<int>();
        foreach (var (number, obj) in AllObjects())
        {
            var dict = obj as PdfDictionary ?? (obj as PdfStream)?.Dictionary;
            if (dict == null) continue;
            if (dict.ContainsKey("EF")) { specs.Add(dict); specNumbers.Add(number); }
            if (_r.Resolve(dict["AF"]) is PdfArray af) foreach (var item in af) if (item is PdfIndirectRef r) associated.Add(r.ObjectNumber);
        }
        bool nameTree = _r.Resolve(Catalog["Names"]) is PdfDictionary names && names.ContainsKey("EmbeddedFiles");
        if (_part == 1 && (specs.Count > 0 || nameTree)) { Fail("6.1.11", 1, "The document embeds files, which PDF/A-1 does not allow."); return; }
        if (specs.Count == 0) return;
        foreach (var spec in specs)
        {
            if (!spec.ContainsKey("F") || !spec.ContainsKey("UF")) Fail(null, _part == 2 ? "6.8" : "6.8", 2, "An embedded file's specification lacks /F or /UF.");
            if (_part == 3)
            {
                if (spec.GetName("AFRelationship") == null) Fail(null, "6.8", 1, "An embedded file has no /AFRelationship.");
                else if (!associated.Contains(specNumbers[specs.IndexOf(spec)])) Fail(null, "6.8", 2, "An embedded file is not associated with the document or a part of it (no /AF refers to it).");
                if (_r.Resolve((_r.Resolve(spec["EF"]) as PdfDictionary)?["F"]) is PdfStream file)
                {
                    if (file.Dictionary.GetName("Subtype") == null) Fail(null, "6.8", 3, "An embedded file has no MIME type (/Subtype).");
                    if (_r.Resolve(file.Dictionary["Params"]) is not PdfDictionary p || p["ModDate"] == null) Fail(null, "6.8", 4, "An embedded file has no modification date.");
                }
            }
            else
            {
                // PDF/A-2: embedded files are themselves PDF/A files; their content is checked by claiming it.
                if (_r.Resolve((_r.Resolve(spec["EF"]) as PdfDictionary)?["F"]) is PdfStream file && Decode(file) is { } data && !(data.Length > 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F'))
                    Fail(null, "6.8", 1, "An embedded file is not a PDF (PDF/A-2 embeds only PDF/A files).");
            }
        }
    }

    private void CheckPermissions()
    {
        if (_part == 1 || Catalog == null || _r.Resolve(Catalog["Perms"]) is not PdfDictionary perms) return;
        foreach (var key in perms.Entries.Keys)
            if (key is not ("UR3" or "DocMDP")) Fail(null, "6.1.12", 1, $"The permissions dictionary has /{key}; only /UR3 and /DocMDP are allowed.");
    }
}

/// <summary>PDF and XMP date parsing (ISO 32000-1 7.9.4, ISO 8601 as XMP uses it).</summary>
internal static class PdfDates
{
    public static DateTimeOffset? ParsePdf(string s)
    {
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        if (s.Length < 4 || !int.TryParse(s[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int year)) return null;
        int Part(int at, int len, int dflt) => s.Length >= at + len && int.TryParse(s.AsSpan(at, len), NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : dflt;
        int month = Part(4, 2, 1), day = Part(6, 2, 1), hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
        var offset = TimeSpan.Zero;
        if (s.Length > 14 && (s[14] == '+' || s[14] == '-'))
        {
            int oh = Part(15, 2, 0);
            int om = s.Length >= 20 && s[17] == '\'' ? Part(18, 2, 0) : 0;
            offset = new TimeSpan(oh, om, 0) * (s[14] == '-' ? -1 : 1);
        }
        try { return new DateTimeOffset(year, month, day, hour, minute, second, offset); }
        catch (ArgumentException) { return null; }
    }

    public static DateTimeOffset? ParseXmp(string s)
    {
        s = s.Trim();
        string[] formats = { "yyyy", "yyyy-MM", "yyyy-MM-dd", "yyyy-MM-ddTHH:mmK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss" };
        return DateTimeOffset.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
