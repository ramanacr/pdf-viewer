using System.Globalization;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Services;

/// <summary>
/// Creates, changes, deletes and flattens annotations as part of a <see cref="PdfUpdate"/>.
/// Positions (<see cref="PdfAnnotation.Index"/>) always refer to the page as it was read, however
/// many edits come before. New and changed annotations get a normal appearance from
/// <see cref="PdfAnnotationAppearances"/>, so every viewer draws them the same way; entries this
/// class does not manage are kept as they were. Call <see cref="Finish"/> before committing.
/// </summary>
public sealed class PdfAnnotationEditor
{
    private readonly PdfUpdate _update;
    private readonly Dictionary<int, PageState> _pages = new();
    private readonly HashSet<int> _removedWidgets = new();
    private bool _finished;

    public PdfAnnotationEditor(PdfUpdate update) => _update = update ?? throw new ArgumentNullException(nameof(update));

    public PdfUpdate Update => _update;

    /// <summary>Runs <paramref name="edit"/> on a new update of <paramref name="original"/> and returns the bytes.</summary>
    public static byte[] Apply(PdfVectorDocument document, byte[] original, Action<PdfAnnotationEditor> edit)
    {
        var editor = new PdfAnnotationEditor(new PdfUpdate(document, original));
        edit(editor);
        editor.Finish();
        return editor.Update.Commit();
    }

    private sealed class PageState
    {
        public int Number;
        public Dictionary<string, PdfObject> Dict = new();
        public List<PdfObject?> Items = new();
        public List<PdfObject> Added = new();
        public bool Changed;
    }

    private PageState State(int pageNumber)
    {
        if (_pages.TryGetValue(pageNumber, out var s)) return s;
        var (number, page) = _update.Page(pageNumber);
        s = new PageState { Number = number, Dict = new Dictionary<string, PdfObject>(page.Entries) };
        if (_update.Resolve(page["Annots"]) is PdfArray annots) s.Items.AddRange(annots.Items);
        _pages[pageNumber] = s;
        return s;
    }

    private PdfDictionary? Resolved(PdfObject? item) => _update.Resolve(item) as PdfDictionary;

    // ------------------------------------------------------------------ add, change, delete

    /// <summary>Adds an annotation to its page; returns its object number.</summary>
    public int Add(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var page = State(annotation.PageNumber);
        int self = _update.Add(PdfNull.Instance);
        var dict = Build(annotation, null, new PdfIndirectRef(page.Number), self, page);
        _update.Set(self, dict);
        page.Added.Add(new PdfIndirectRef(self));
        page.Changed = true;
        return self;
    }

    /// <summary>Replaces the managed entries of the annotation at <see cref="PdfAnnotation.Index"/> on its page and draws it again.</summary>
    public void Modify(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var page = State(annotation.PageNumber);
        if (annotation.Index < 0 || annotation.Index >= page.Items.Count || page.Items[annotation.Index] is not { } item)
            throw new ArgumentOutOfRangeException(nameof(annotation), $"Page {annotation.PageNumber} has no annotation at position {annotation.Index}.");
        var existing = Resolved(item) ?? throw new InvalidOperationException("The annotation cannot be read.");
        int self = item is PdfIndirectRef r ? r.ObjectNumber : 0;
        var dict = Build(annotation, existing, new PdfIndirectRef(page.Number), self, page);
        if (self > 0) _update.Set(self, dict);
        else page.Items[annotation.Index] = dict;
        page.Changed = true;
    }

    /// <summary>
    /// Removes the annotation at <paramref name="index"/> with its pop-up and, unless
    /// <paramref name="cascadeReplies"/> is false, the replies to it (12.5.6.4, /IRT), as Acrobat does.
    /// </summary>
    public void Delete(int pageNumber, int index, bool cascadeReplies = true)
    {
        var page = State(pageNumber);
        if (index < 0 || index >= page.Items.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Page {pageNumber} has no annotation at position {index}.");
        Remove(page, index, cascadeReplies);
    }

    internal static void RemoveAt(PdfUpdate update, int pageNumber, int index)
    {
        var editor = new PdfAnnotationEditor(update);
        editor.Delete(pageNumber, index);
        editor.Finish();
    }

    private void Remove(PageState page, int index, bool cascadeReplies = true)
    {
        var item = page.Items[index];
        if (item == null) return;
        page.Items[index] = null;
        page.Changed = true;
        var dict = Resolved(item);
        int self = item is PdfIndirectRef r ? r.ObjectNumber : 0;
        if (dict?.GetName("Subtype") == "Widget" && self > 0) _removedWidgets.Add(self);
        int popup = dict?["Popup"] is PdfIndirectRef p ? p.ObjectNumber : 0;
        for (int i = 0; i < page.Items.Count; i++)
        {
            if (page.Items[i] is not { } other) continue;
            int number = other is PdfIndirectRef o ? o.ObjectNumber : 0;
            var od = Resolved(other);
            bool isPopup = number > 0 && number == popup
                || self > 0 && od?.GetName("Subtype") == "Popup" && od["Parent"] is PdfIndirectRef parent && parent.ObjectNumber == self;
            bool isReply = cascadeReplies && self > 0 && od?["IRT"] is PdfIndirectRef irt && irt.ObjectNumber == self;
            if (isPopup || isReply) Remove(page, i, cascadeReplies);
        }
    }

    // ------------------------------------------------------------------ building

    private PdfDictionary Build(PdfAnnotation a, PdfDictionary? existing, PdfIndirectRef pageRef, int self, PageState page)
    {
        var e = existing != null ? new Dictionary<string, PdfObject>(existing.Entries) : new Dictionary<string, PdfObject>();
        void Put(string key, PdfObject? value)
        {
            if (value == null) e.Remove(key);
            else e[key] = value;
        }
        PdfObject? Text(string? s) => s == null ? null : PdfObjectWriter.TextString(s);
        PdfObject? Colour(double[]? c) => c is { Length: 1 or 3 or 4 } ? PdfUpdate.Numbers(c) : null;

        var rect = a.Rect;
        e["Type"] = new PdfName("Annot");
        e["Subtype"] = new PdfName(a.Subtype);
        e["Rect"] = PdfUpdate.Numbers(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
        e["P"] = pageRef;
        e["F"] = new PdfInteger(a.Flags);
        Put("C", Colour(a.Color));
        Put("IC", Colour(a.InteriorColor));
        Put("CA", a.Opacity < 1 ? new PdfReal(Math.Round(Math.Clamp(a.Opacity, 0, 1), 4)) : null);
        Put("Contents", Text(a.Contents));
        Put("T", Text(a.Author));
        Put("Subj", Text(a.Subject));
        Put("NM", Text(a.Name));
        Put("CreationDate", Text(a.CreationDate));
        Put("M", Text(a.ModifiedDate));
        Put("RC", Text(a.RichContents));

        if (a.BorderWidth is double w)
        {
            var bs = new Dictionary<string, PdfObject> { ["W"] = PdfUpdate.Numbers(w)[0], ["S"] = new PdfName(a.BorderStyle ?? (a.BorderDash != null ? "D" : "S")) };
            if (a.BorderDash is { Length: > 0 } dash) bs["D"] = PdfUpdate.Numbers(dash);
            e["BS"] = new PdfDictionary(bs);
            // /Border too: older readers (and PDFium's FPDFAnnot_GetBorder) read only that.
            e["Border"] = PdfUpdate.Array(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), PdfUpdate.Numbers(w)[0] });
        }
        else
        {
            e.Remove("BS");
        }

        Put("InkList", a.InkList.Count > 0
            ? PdfUpdate.Array(a.InkList.Select(s => (PdfObject)PdfUpdate.Numbers(s.SelectMany(p => new[] { p.X, p.Y }).ToArray())))
            : null);
        Put("QuadPoints", a.QuadPoints.Count > 0 ? PdfUpdate.Numbers(a.QuadPoints.SelectMany(q => q).ToArray()) : null);
        Put("L", a.Line is { Length: 4 } line ? PdfUpdate.Numbers(line) : null);
        Put("Vertices", a.Vertices.Count > 0 ? PdfUpdate.Numbers(a.Vertices.SelectMany(p => new[] { p.X, p.Y }).ToArray()) : null);
        Put("LE", a.LineEndings is { Length: > 0 } le
            ? le.Length == 1 && a.Subtype == "FreeText" ? new PdfName(le[0]) : PdfUpdate.Array(le.Select(n => (PdfObject)new PdfName(n)))
            : null);
        Put("Name", a.Icon != null ? new PdfName(a.Icon) : null);
        Put("DA", Text(a.DefaultAppearance));
        Put("Q", a.Justification != 0 ? new PdfInteger(a.Justification) : null);
        Put("IT", a.Intent != null ? new PdfName(a.Intent) : null);
        Put("CL", a.CalloutLine is { Length: 4 or 6 } cl ? PdfUpdate.Numbers(cl) : null);
        Put("IRT", a.InReplyTo > 0 ? new PdfIndirectRef(a.InReplyTo) : null);
        Put("RT", a.ReplyType != null ? new PdfName(a.ReplyType) : null);
        Put("State", Text(a.State));
        Put("StateModel", Text(a.StateModel));
        // A measure the reader could not model (geospatial) is kept as it was.
        if (a.Measure != null) e["Measure"] = a.Measure.ToDictionary();
        if (a.Caption) e["Cap"] = PdfBoolean.True;
        else if (a.Subtype == "Line") e.Remove("Cap");

        if (a.Subtype == "FileAttachment" && a.FileData != null)
            e["FS"] = _update.AddRef(PdfAttachments.NewFileSpec(_update, a.FileName ?? "attachment", a.FileData, null, null, null));

        if (a.PopupRect is { } popupRect && !e.ContainsKey("Popup") && self > 0)
        {
            int popup = _update.Add(new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"),
                ["Subtype"] = new PdfName("Popup"),
                ["Rect"] = PdfUpdate.Numbers(popupRect.X, popupRect.Y, popupRect.X + popupRect.Width, popupRect.Y + popupRect.Height),
                ["Parent"] = new PdfIndirectRef(self),
                ["P"] = pageRef,
                ["Open"] = a.PopupOpen ? PdfBoolean.True : PdfBoolean.False,
                ["F"] = new PdfInteger(28), // Print, NoZoom, NoRotate
            }));
            e["Popup"] = new PdfIndirectRef(popup);
            page.Added.Add(new PdfIndirectRef(popup));
        }

        // A stamp keeps the appearance it has (a picture, a page, a dynamic stamp's name and date)
        // unless it is given a new one: moving or resizing it only changes /Rect, which 12.5.5 fits it to.
        if (a.Subtype == "Stamp" && a.Stamp == null && existing != null && _update.Resolve(existing["AP"]) is PdfDictionary)
            return new PdfDictionary(e);

        // A new normal appearance in place of the old one; a type the generator does not draw keeps what it had.
        var draft = new PdfDictionary(e);
        var form = a.Subtype == "Stamp" && a.Stamp != null
            ? Annotations.PdfStampForms.Build(_update, a.Stamp, rect, a.Opacity) ?? PdfAnnotationAppearances.Generate(draft, _update.Resolver, o => _update.Add(o), a.Stamp)
            : PdfAnnotationAppearances.Generate(draft, _update.Resolver, o => _update.Add(o));
        if (form != null)
        {
            e["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = _update.AddRef(form) });
            e.Remove("AS");
        }
        return new PdfDictionary(e);
    }

    // ------------------------------------------------------------------ flattening

    /// <summary>
    /// Draws the visible annotations of a page into its content and removes them (FPDFPage_Flatten
    /// for normal display). Links, hidden and no-view annotations, and annotations with no
    /// appearance the generator can draw stay as they are; pop-ups go with their parents.
    /// <paramref name="widgetsOnly"/> flattens form field widgets and nothing else (a form flatten).
    /// </summary>
    public int FlattenPage(int pageNumber, bool widgetsOnly = false)
    {
        var page = State(pageNumber);
        var resources = _update.Resolve(PageAttribute(page, "Resources")) as PdfDictionary;
        var xobjects = new Dictionary<string, PdfObject>((_update.Resolve(resources?["XObject"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
        var properties = new Dictionary<string, PdfObject>((_update.Resolve(resources?["Properties"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
        var content = new StringBuilder();
        int flattened = 0, counter = 0;
        string Unique(string prefix, Dictionary<string, PdfObject> names)
        {
            string name;
            do { name = prefix + (++counter).ToString(CultureInfo.InvariantCulture); } while (names.ContainsKey(name));
            return name;
        }

        var all = page.Items.Concat(page.Added.Cast<PdfObject?>()).ToList();
        var flattenedNumbers = new HashSet<int>();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i] is not { } item || Resolved(item) is not { } d) continue;
            string? subtype = d.GetName("Subtype");
            if (subtype is "Popup" or "Link") continue;
            if (widgetsOnly && subtype != "Widget") continue;
            long flags = d.GetInteger("F") ?? 0;
            if ((flags & (2 | 32)) != 0) continue; // Hidden, NoView
            if (PdfDocumentInfo.ReadRect(d["Rect"], _update.Resolver) is not { IsEmpty: false } rect) continue;

            PdfObject? appearanceRef = null;
            var normal = _update.Resolve((_update.Resolve(d["AP"]) as PdfDictionary)?["N"]);
            PdfObject? normalRef = (_update.Resolve(d["AP"]) as PdfDictionary)?["N"];
            if (normal is PdfDictionary states && d.GetName("AS") is string state)
            {
                normalRef = states[state];
                normal = _update.Resolve(normalRef);
            }
            if (normal is PdfStream && normalRef is PdfIndirectRef)
            {
                appearanceRef = normalRef;
            }
            else if (normal == null && PdfAnnotationAppearances.Generate(d, _update.Resolver, o => _update.Add(o)) is { } generated)
            {
                normal = generated;
                appearanceRef = _update.AddRef(generated);
            }
            if (normal is not PdfStream form || appearanceRef == null) continue;

            var bbox = PdfDocumentInfo.ReadRect(form.Dictionary["BBox"], _update.Resolver) ?? rect;
            var matrix = PdfDocumentInfo.Numbers(_update.Resolve(form.Dictionary["Matrix"]), _update.Resolver) is { Length: 6 } m
                ? new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5])
                : PdfMatrix.Identity;
            var t = matrix.Transform(bbox);
            if (t.Width <= 0 || t.Height <= 0) continue;
            // 12.5.5: the transformed box is fitted to /Rect.
            double sx = rect.Width / t.Width, sy = rect.Height / t.Height;
            string name = Unique("PdfFlat", xobjects);
            xobjects[name] = appearanceRef;
            string draw = string.Create(CultureInfo.InvariantCulture,
                $"q {F(sx)} 0 0 {F(sy)} {F(rect.Left - t.Left * sx)} {F(rect.Top - t.Top * sy)} cm /{name} Do Q\n");
            if (d["OC"] is { } oc)
            {
                string tag = Unique("PdfFlatOC", properties);
                properties[tag] = oc;
                draw = $"/OC /{tag} BDC\n{draw}EMC\n";
            }
            content.Append(draw);
            flattened++;
            if (item is PdfIndirectRef ir) flattenedNumbers.Add(ir.ObjectNumber);
            if (subtype == "Widget" && item is PdfIndirectRef wr) _removedWidgets.Add(wr.ObjectNumber);
            int at = i < page.Items.Count ? i : -1;
            if (at >= 0) page.Items[at] = null;
            else page.Added.Remove(item);
        }
        if (flattened == 0) return 0;

        // Pop-ups of flattened annotations go too.
        for (int i = 0; i < page.Items.Count; i++)
        {
            if (Resolved(page.Items[i]) is { } d && d.GetName("Subtype") == "Popup" && d["Parent"] is PdfIndirectRef p && flattenedNumbers.Contains(p.ObjectNumber))
                page.Items[i] = null;
        }
        page.Added.RemoveAll(item => Resolved(item) is { } d && d.GetName("Subtype") == "Popup" && d["Parent"] is PdfIndirectRef p && flattenedNumbers.Contains(p.ObjectNumber));

        // The page's own resources from now on, so pages sharing a resource dictionary are not affected.
        var newResources = new Dictionary<string, PdfObject>(resources?.Entries ?? new Dictionary<string, PdfObject>())
        {
            ["XObject"] = new PdfDictionary(xobjects),
        };
        if (properties.Count > 0) newResources["Properties"] = new PdfDictionary(properties);
        page.Dict["Resources"] = new PdfDictionary(newResources);

        // The existing content is wrapped in q ... Q, so a graphics state it leaves behind cannot move the annotations.
        var before = _update.AddRef(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.ASCII.GetBytes("q\n")));
        var after = _update.AddRef(PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") },
            Redaction.ContentRedactor.Deflate(Encoding.ASCII.GetBytes("Q\n" + content))));
        var contents = new List<PdfObject> { before };
        switch (page.Dict.GetValueOrDefault("Contents"))
        {
            case PdfArray arr: contents.AddRange(arr.Items); break;
            case PdfIndirectRef single when _update.Resolve(single) is PdfArray indirectArray: contents.AddRange(indirectArray.Items); break;
            case PdfIndirectRef single: contents.Add(single); break;
        }
        contents.Add(after);
        page.Dict["Contents"] = new PdfArray(contents);
        page.Changed = true;
        return flattened;
    }

    /// <summary>Flattens every page; returns the number of annotations drawn into content.</summary>
    public int FlattenAll(bool widgetsOnly = false)
    {
        int total = 0;
        for (int p = 1; p <= _update.Document.PageCount; p++) total += FlattenPage(p, widgetsOnly);
        return total;
    }

    private PdfObject? PageAttribute(PageState page, string key) =>
        page.Dict.TryGetValue(key, out var v) ? v : PdfDocumentInfo.Inherited(new PdfDictionary(page.Dict), key, _update.Resolver);

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 6));

    // ------------------------------------------------------------------ finishing

    /// <summary>Writes the changed pages' /Annots (and an /AcroForm without removed widgets) into the update.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        foreach (var page in _pages.Values.Where(p => p.Changed))
        {
            var annots = page.Items.Where(i => i != null).Cast<PdfObject>().Concat(page.Added).ToList();
            if (annots.Count > 0) page.Dict["Annots"] = new PdfArray(annots);
            else page.Dict.Remove("Annots");
            _update.Set(page.Number, new PdfDictionary(page.Dict));
        }
        if (_removedWidgets.Count > 0) PruneAcroForm();
    }

    /// <summary>Drops removed widgets from the field tree, and fields left with no widgets; an empty form goes altogether.</summary>
    private void PruneAcroForm()
    {
        var catalog = _update.Catalog;
        var formRef = catalog["AcroForm"];
        if (_update.Resolve(formRef) is not PdfDictionary form || _update.Resolve(form["Fields"]) is not PdfArray fields) return;
        var visited = new HashSet<int>();
        List<PdfObject> Prune(PdfArray kids, int depth)
        {
            var kept = new List<PdfObject>();
            foreach (var kid in kids)
            {
                int number = kid is PdfIndirectRef r ? r.ObjectNumber : 0;
                if (number > 0 && _removedWidgets.Contains(number)) continue;
                if (depth < 32 && (number == 0 || visited.Add(number)) && _update.Resolve(kid) is PdfDictionary field && _update.Resolve(field["Kids"]) is PdfArray children)
                {
                    var prunedKids = Prune(children, depth + 1);
                    if (prunedKids.Count == 0) continue;
                    if (prunedKids.Count != children.Count && number > 0)
                        _update.Set(number, PdfUpdate.With(field, ("Kids", new PdfArray(prunedKids))));
                }
                kept.Add(kid);
            }
            return kept;
        }
        var remaining = Prune(fields, 0);
        if (remaining.Count == fields.Count) return;
        if (remaining.Count == 0)
        {
            _update.Set(_update.RootNumber, PdfUpdate.With(catalog, ("AcroForm", null)));
            return;
        }
        var newForm = PdfUpdate.With(form, ("Fields", new PdfArray(remaining)));
        if (formRef is PdfIndirectRef fr) _update.Set(fr.ObjectNumber, newForm);
        else _update.Set(_update.RootNumber, PdfUpdate.With(catalog, ("AcroForm", newForm)));
    }
}
