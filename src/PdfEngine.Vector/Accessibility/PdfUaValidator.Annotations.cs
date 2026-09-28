using System;
using System.Collections.Generic;
using System.Linq;
using PdfEngine.Geometry;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Accessibility;

/// <summary>
/// Annotations and forms (7.18): each is tagged in the element its kind needs (Annot, Link, Form)
/// with a description, pages with annotations use the structure tab order, and the special kinds
/// (TrapNet, PrinterMark, file attachments, media clips) follow their rules. Also the languages of
/// the bookmarks (7.2).
/// </summary>
internal sealed partial class PdfUaChecker
{
    /// <summary>The annotation subtypes ISO 32000-1 defines.</summary>
    private static readonly HashSet<string> AnnotationTypes = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline", "Squiggly", "StrikeOut",
        "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Sound", "Movie", "Widget", "Screen", "PrinterMark", "TrapNet", "Watermark", "3D", "Redact",
    };

    private void CheckAnnotations()
    {
        for (int i = 0; i < _doc.PageTree.Count; i++)
        {
            var page = _doc.PageTree.Pages[i];
            if (_r.Resolve(page.Dictionary["Annots"]) is not PdfArray annots || annots.Count == 0) continue;
            bool any = false;
            foreach (var item in annots)
            {
                if (_r.Resolve(item) is not PdfDictionary annot) continue;
                any = true;
                int? number = item is PdfIndirectRef r ? r.ObjectNumber : null;
                var rect = RectOf(annot);
                if (number is { } n && rect is { } rc) _annotRects[n] = (i, rc);
                CheckAnnotation(annot, number, rect, page.CropBox, i + 1);
            }
            if (!any) continue;
            string? tabs = page.Dictionary.GetName("Tabs");
            if (tabs == null) Report("28-008", "7.18.3", PdfUaCategory.Annotations, "The page has annotations but no tab order (/Tabs).", page: i + 1);
            else if (tabs != "S") Report("28-009", "7.18.3", PdfUaCategory.Annotations, $"The page's tab order is /{Short(tabs)}, not the structure order (/S).", page: i + 1);
        }
        if (_catalog != null && _documentLang == null && _r.Resolve(_catalog["Outlines"]) is PdfDictionary outlines && outlines["First"] != null)
            Report("11-003", "7.2", PdfUaCategory.Document, "The language of the bookmarks cannot be determined: the catalog has no /Lang.");
    }

    private PdfRect? RectOf(PdfDictionary annot)
    {
        if (_r.Resolve(annot["Rect"]) is not PdfArray a || a.Count < 4) return null;
        double[] v = a.Items.Take(4).Select(x => _r.Resolve(x) is { } o && o.TryGetNumber(out double d) ? d : 0).ToArray();
        return new PdfRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1]));
    }

    private void CheckAnnotation(PdfDictionary annot, int? number, PdfRect? rect, PdfRect crop, int page)
    {
        string subtype = annot.GetName("Subtype") ?? string.Empty;
        if (subtype == "Popup") return;
        var owner = OwnerOfObject(number, annot);
        void Issue(string checkpoint, string clause, PdfUaCategory cat, string message, PdfUaSeverity severity = PdfUaSeverity.Error) =>
            Report(checkpoint, clause, cat, message, page: page, obj: number, bounds: rect, severity: severity, elem: owner);
        const PdfUaCategory annots = PdfUaCategory.Annotations, forms = PdfUaCategory.Forms;
        if (subtype == "TrapNet") { Issue("28-007", "7.18.2", annots, "The page has a TrapNet annotation."); return; }
        if (subtype == "PrinterMark")
        {
            if (owner != null) Issue("28-017", "7.18.8", annots, "A PrinterMark annotation is in the structure tree.");
            return;
        }
        if (!AnnotationTypes.Contains(subtype))
        {
            Issue("28-006", "7.18.2", annots, $"The annotation subtype /{Short(subtype)} is not defined in ISO 32000.", PdfUaSeverity.Warning);
            return;
        }
        // Hidden annotations and those outside the crop box are not presented, so need no tag or description.
        bool hidden = ((_r.Resolve(annot["F"]) is { } f && f.TryGetInteger(out long flags) ? flags : 0) & 2) != 0;
        bool outside = rect is { } b && (b.X >= crop.X + crop.Width || b.X + b.Width <= crop.X || b.Y >= crop.Y + crop.Height || b.Y + b.Height <= crop.Y);
        if (hidden || outside) return;
        string? ownerAlt = owner == null ? null : AltOf(owner);
        string? contents = TextOf(annot, "Contents");
        bool haveLang = _documentLang != null || owner?.InheritedLang != null;
        switch (subtype)
        {
            case "Widget":
                if (owner?.Std != "Form") Issue("28-010", "7.18.4", forms, "A form field's widget is not tagged in a Form structure element.");
                string? tu = FieldOf(annot) is { } field ? TextOf(field, "TU") : null;
                if (string.IsNullOrEmpty(tu) && string.IsNullOrEmpty(ownerAlt)) Issue("28-005", "7.18.1", forms, "A form field has no description: no tooltip (/TU) and no Alt on its Form element.");
                if (!string.IsNullOrEmpty(tu) && !haveLang) Issue("11-005", "7.2", forms, "The language of a form field's tooltip (/TU) cannot be determined.");
                return;
            case "Link":
                if (owner?.Std != "Link") Issue("28-011", "7.18.5", annots, "A link is not tagged in a Link structure element.");
                if (string.IsNullOrEmpty(contents)) Issue("28-012", "7.18.5", annots, "A link has no alternate description (/Contents).");
                break;
            default:
                if (owner?.Std != "Annot") Issue("28-002", "7.18.1", annots, $"A {subtype} annotation is not tagged in an Annot structure element.");
                if (string.IsNullOrEmpty(contents) && string.IsNullOrEmpty(ownerAlt)) Issue("28-004", "7.18.1", annots, $"A {subtype} annotation has no description: no /Contents and no Alt on its Annot element.");
                break;
        }
        if (!string.IsNullOrEmpty(contents) && !haveLang) Issue("11-004", "7.2", annots, "The language of an annotation's /Contents cannot be determined.");
        if (subtype == "FileAttachment")
        {
            var spec = _r.Resolve(annot["FS"]) as PdfDictionary;
            if (spec == null || string.IsNullOrEmpty(TextOf(spec, "F")) || string.IsNullOrEmpty(TextOf(spec, "UF")))
                Issue("28-016", "7.18.7", annots, "A file attachment's file specification lacks a non-empty /F or /UF.");
        }
    }

    /// <summary>The field a widget belongs to: itself when it has a name (/T), else the nearest named parent.</summary>
    private PdfDictionary? FieldOf(PdfDictionary widget)
    {
        var node = widget;
        for (int i = 0; i < 32 && node != null; i++)
        {
            if (node.ContainsKey("T")) return node;
            node = _r.Resolve(node["Parent"]) as PdfDictionary;
        }
        return widget;
    }

    /// <summary>Media clip data (7.18.6.2): a content type and an alternate description.</summary>
    private void CheckMediaClips()
    {
        foreach (var (number, obj) in AllObjects())
            if (obj is PdfDictionary top) Visit(top, number, 0);

        // Media clips usually sit directly inside a rendition inside an action inside an annotation.
        void Visit(PdfDictionary d, int number, int depth)
        {
            if (depth > 8) return;
            if (d.GetName("S") == "MCD" && d.GetName("Type") is null or "MediaClip")
            {
                if (!d.ContainsKey("CT")) Report("28-014", "7.18.6.2", PdfUaCategory.Annotations, "A media clip has no content type (/CT).", obj: number);
                // Alt is a multi-language text array: language, text, language, text...
                bool alt = _r.Resolve(d["Alt"]) is PdfArray a && a.Items.Select(_r.Resolve).Where((_, k) => k % 2 == 1).OfType<PdfString>().Any(s => s.AsDecodedString().Length > 0);
                if (!alt) Report("28-015", "7.18.6.2", PdfUaCategory.Annotations, "A media clip has no alternate description (/Alt).", obj: number);
            }
            foreach (var value in d.Entries.Values)
                if (value is PdfDictionary child) Visit(child, number, depth + 1);
                else if (value is PdfArray array) foreach (var item in array) if (item is PdfDictionary c) Visit(c, number, depth + 1);
        }
    }
}
