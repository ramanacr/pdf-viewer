using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Signatures;

/// <summary>What later revisions changed, and which form fields they changed.</summary>
/// <param name="Changes">The kinds of change.</param>
/// <param name="Fields">Full names of the form fields whose value, appearance or presence changed.</param>
/// <param name="UnnamedFields">A change that can affect any field (the form's default resources or appearance settings, or a field with no name).</param>
internal sealed record PdfChangeReport(PdfLaterChanges Changes, IReadOnlySet<string> Fields, bool UnnamedFields)
{
    public static readonly PdfChangeReport Unreadable = new(PdfLaterChanges.Other, new HashSet<string>(), true);
}

public static partial class PdfSignatureValidator
{
    /// <summary>What later revisions changed: the kinds of change only.</summary>
    internal static async Task<PdfLaterChanges> LaterChangesAsync(byte[] file, int revisionEnd, string? password, CancellationToken ct) =>
        (await ChangeReportAsync(file, revisionEnd, password, ct).ConfigureAwait(false)).Changes;

    /// <summary>
    /// What later revisions changed. Every object that is new, changed or freed after the signed
    /// revision is weighed by where the document uses it, in the signed revision and in the final
    /// one: page content and resources, the page tree, annotations and their appearances, form
    /// fields and their appearances, signature values, metadata, validation data or anything else
    /// the catalog holds. An object's own /Type, /Subtype, /FT or /ByteRange never decides what it
    /// is: a page's content stream labelled as anything is still page content. An object used in
    /// several places counts as the most significant of them.
    /// </summary>
    internal static async Task<PdfChangeReport> ChangeReportAsync(byte[] file, int revisionEnd, string? password, CancellationToken ct)
    {
        PdfVectorDocument before;
        try
        {
            before = await PdfVectorDocument.OpenAsync(file.AsSpan(0, revisionEnd).ToArray(), password: password, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PdfChangeReport.Unreadable; // the signed revision cannot be read on its own
        }
        using (before)
        using (var after = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: ct).ConfigureAwait(false))
        {
            var diff = new RevisionDiff(new UsageMap(before), new UsageMap(after));
            return diff.Run(ct);
        }
    }

    /// <summary>Where an object is used, which decides what changing it means.</summary>
    private enum UseKind
    {
        /// <summary>Drawn or used by a page (contents, resources, anything a page refers to but its annotations).</summary>
        Content,
        /// <summary>A node of the page tree.</summary>
        PageNode,
        /// <summary>A page's /Annots array, as its own object.</summary>
        AnnotsArray,
        /// <summary>An annotation, a form field or a widget, or something it refers to (its appearance, its value).</summary>
        Owned,
        /// <summary>The value (/V) of a signature field, or something it refers to.</summary>
        SignatureValue,
        /// <summary>The form dictionary, as its own object (compared as a whole).</summary>
        Form,
        /// <summary>The form's default resources and other form-wide entries.</summary>
        FormResources,
        /// <summary>The catalog (compared as a whole).</summary>
        Catalog,
        /// <summary>The catalog's XMP metadata, or the document information dictionary.</summary>
        Metadata,
        /// <summary>The Document Security Store.</summary>
        Dss,
        /// <summary>Anything else the catalog or trailer refers to (outlines, names, actions, structure, /Perms, /Encrypt).</summary>
        Other,
    }

    /// <summary>An annotation or form field that owns objects (its appearance streams, its value).</summary>
    private sealed class Owner
    {
        public int Number = -1;              // -1 when the dictionary is direct
        public required PdfDictionary Dict;
        public bool IsField;                  // a form field or a widget
        public bool IsSignature;              // a field whose (inherited) /FT is /Sig
        public string? FieldName;             // the field's full name
        public PdfDictionary? Value;          // a signature field's signature dictionary
        public int ValueNumber = -1;
    }

    private readonly record struct Use(UseKind Kind, Owner? Owner);

    /// <summary>For one revision: every object reachable from the trailer, and where it is used.</summary>
    private sealed class UsageMap
    {
        public readonly PdfVectorDocument Doc;
        public readonly Parsing.PdfObjectResolver R;
        public readonly Dictionary<int, List<Use>> Uses = new();
        /// <summary>Annotations, fields, page tree nodes, the catalog and the form: classified as themselves, not walked into from elsewhere.</summary>
        public readonly HashSet<int> Structural = new();
        public readonly Dictionary<int, Owner> Owners = new();
        /// <summary>Object numbers referred to that the revision does not define.</summary>
        public readonly HashSet<int> Dangling = new();
        public readonly PdfDictionary? Catalog;
        public readonly int CatalogNumber = -1;
        public readonly PdfDictionary? Form;
        private readonly HashSet<(int, UseKind, Owner?)> _walked = new();

        public UsageMap(PdfVectorDocument doc)
        {
            Doc = doc;
            R = doc.Resolver;
            var trailer = doc.XrefTable.Trailer;
            if (trailer?["Root"] is PdfIndirectRef rootRef)
            {
                CatalogNumber = rootRef.ObjectNumber;
                Catalog = R.Resolve(rootRef) as PdfDictionary;
            }
            if (Catalog == null) return;
            Note(CatalogNumber, new Use(UseKind.Catalog, null));
            Structural.Add(CatalogNumber);

            // First the structure (what is an annotation, a field, a page), then what each part refers to.
            var leaves = new List<PdfDictionary>();
            var nodes = new List<PdfDictionary>();
            PageTree(Catalog["Pages"], leaves, nodes, 0);
            var annotations = new List<Owner>();
            foreach (var page in leaves)
            {
                if (page["Annots"] is PdfIndirectRef annotsRef)
                {
                    Structural.Add(annotsRef.ObjectNumber);
                    Note(annotsRef.ObjectNumber, new Use(UseKind.AnnotsArray, null));
                }
                if (R.Resolve(page["Annots"]) is not PdfArray annots) continue;
                foreach (var item in annots)
                    if (OwnerOf(item, field: false) is { } owner) annotations.Add(owner);
            }
            var fields = new List<Owner>();
            if (Catalog["AcroForm"] is PdfIndirectRef formRef)
            {
                Structural.Add(formRef.ObjectNumber);
                Note(formRef.ObjectNumber, new Use(UseKind.Form, null));
            }
            Form = R.Resolve(Catalog["AcroForm"]) as PdfDictionary;
            if (Form != null && R.Resolve(Form["Fields"]) is PdfArray formFields)
            {
                if (Form["Fields"] is PdfIndirectRef fieldsRef)
                {
                    Structural.Add(fieldsRef.ObjectNumber);
                    Note(fieldsRef.ObjectNumber, new Use(UseKind.Form, null)); // compared with the form (FormChange)
                }
                FieldTree(formFields, fields, 0);
            }

            foreach (var node in nodes)
                foreach (var (key, value) in node.Entries)
                    if (key is not ("Kids" or "Parent")) Walk(value, new Use(UseKind.Content, null), 0);
            foreach (var page in leaves)
                foreach (var (key, value) in page.Entries)
                    if (key is not ("Annots" or "Parent")) Walk(value, new Use(UseKind.Content, null), 0);
            foreach (var owner in annotations.Concat(fields))
                foreach (var (key, value) in owner.Dict.Entries)
                    Walk(value, new Use(owner.IsSignature && key == "V" ? UseKind.SignatureValue : UseKind.Owned, owner), 0);
            if (Form != null)
                foreach (var (key, value) in Form.Entries)
                    if (key != "Fields") Walk(value, new Use(key == "XFA" ? UseKind.Content : UseKind.FormResources, null), 0);
            foreach (var (key, value) in Catalog.Entries)
            {
                var kind = key switch
                {
                    "Pages" or "AcroForm" => (UseKind?)null,
                    "Metadata" => UseKind.Metadata,
                    "DSS" => UseKind.Dss,
                    _ => UseKind.Other,
                };
                if (kind != null) Walk(value, new Use(kind.Value, null), 0);
            }
            Walk(trailer?["Info"], new Use(UseKind.Metadata, null), 0);
            Walk(trailer?["Encrypt"], new Use(UseKind.Other, null), 0);
        }

        public bool InUse(int number) => Doc.XrefTable.Entries.TryGetValue(number, out var e) && e.IsInUse;

        private void Note(int number, Use use)
        {
            if (!Uses.TryGetValue(number, out var list)) Uses[number] = list = new List<Use>();
            if (!list.Contains(use)) list.Add(use);
            if (!InUse(number)) Dangling.Add(number);
        }

        private void PageTree(PdfObject? node, List<PdfDictionary> leaves, List<PdfDictionary> nodes, int depth)
        {
            if (depth > 64 || node is not PdfIndirectRef nodeRef) return;
            Note(nodeRef.ObjectNumber, new Use(UseKind.PageNode, null));
            if (!Structural.Add(nodeRef.ObjectNumber) || R.Resolve(nodeRef) is not PdfDictionary dict) return;
            if (R.Resolve(dict["Kids"]) is PdfArray kids && dict.GetName("Type") != "Page")
            {
                if (dict["Kids"] is PdfIndirectRef kidsRef) Note(kidsRef.ObjectNumber, new Use(UseKind.PageNode, null));
                nodes.Add(dict);
                foreach (var kid in kids) PageTree(kid, leaves, nodes, depth + 1);
            }
            else leaves.Add(dict);
        }

        private readonly HashSet<int> _fieldNodes = new();

        private void FieldTree(PdfArray items, List<Owner> fields, int depth)
        {
            if (depth > 32) return;
            foreach (var item in items)
            {
                if (item is PdfIndirectRef ir && !_fieldNodes.Add(ir.ObjectNumber)) continue; // listed twice, or a loop in /Kids
                if (OwnerOf(item, field: true) is not { } owner) continue;
                fields.Add(owner);
                if (owner.Dict["Kids"] is PdfIndirectRef kidsRef) Note(kidsRef.ObjectNumber, new Use(UseKind.Owned, owner));
                if (R.Resolve(owner.Dict["Kids"]) is PdfArray kids) FieldTree(kids, fields, depth + 1);
            }
        }

        /// <summary>The annotation or field an /Annots, /Fields or /Kids item is.</summary>
        private Owner? OwnerOf(PdfObject item, bool field)
        {
            if (item is PdfIndirectRef ir)
            {
                Structural.Add(ir.ObjectNumber);
                if (!Owners.TryGetValue(ir.ObjectNumber, out var known))
                {
                    if (R.Resolve(ir) is not PdfDictionary d)
                    {
                        Note(ir.ObjectNumber, new Use(UseKind.Owned, null));
                        return null;
                    }
                    known = Describe(d, ir.ObjectNumber, field);
                    Owners[ir.ObjectNumber] = known;
                }
                else if (field) known.IsField = true;
                Note(ir.ObjectNumber, new Use(UseKind.Owned, known));
                return known;
            }
            return item is PdfDictionary direct ? Describe(direct, -1, field) : null;
        }

        private Owner Describe(PdfDictionary d, int number, bool field)
        {
            var owner = new Owner { Number = number, Dict = d, IsField = field || d.GetName("Subtype") == "Widget" };
            if (!owner.IsField) return owner;
            // The field's type, value and name, inherited through /Parent (12.7.4.1).
            string? type = null;
            PdfObject? value = null;
            var names = new List<string>();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            PdfDictionary? node = d;
            for (int depth = 0; node != null && depth < 32 && seen.Add(node); depth++)
            {
                type ??= node.GetName("FT");
                value ??= node["V"];
                if (node["T"] is PdfString t) names.Add(t.AsDecodedString());
                node = R.Resolve(node["Parent"]) as PdfDictionary;
            }
            names.Reverse();
            owner.FieldName = names.Count > 0 ? string.Join(".", names) : null;
            owner.IsSignature = type == "Sig";
            if (owner.IsSignature)
            {
                owner.Value = R.Resolve(value) as PdfDictionary;
                owner.ValueNumber = value is PdfIndirectRef vr ? vr.ObjectNumber : -1;
            }
            return owner;
        }

        /// <summary>
        /// Notes every object <paramref name="o"/> refers to as used the given way. The walk does not
        /// go into annotations, fields, pages, the form or the catalog: they are judged as themselves.
        /// Page content that reaches one of them still marks it as page content.
        /// </summary>
        private void Walk(PdfObject? o, Use use, int depth)
        {
            if (o == null || depth > 64) return;
            switch (o)
            {
                case PdfIndirectRef ir:
                    int n = ir.ObjectNumber;
                    bool structural = Structural.Contains(n);
                    if (!structural || use.Kind == UseKind.Content) Note(n, use);
                    else if (!InUse(n)) Dangling.Add(n);
                    if (structural || !_walked.Add((n, use.Kind, use.Owner))) return;
                    Walk(R.Resolve(n), use, depth + 1);
                    return;
                case PdfStream s:
                    Walk(s.Dictionary, use, depth + 1);
                    return;
                case PdfDictionary d:
                    // Form actions name the fields they act on; naming a field does not draw it.
                    bool formAction = d.GetName("S") is "SubmitForm" or "ResetForm" or "ImportData" or "Hide";
                    foreach (var (key, value) in d.Entries)
                        if (!(formAction && key is "Fields" or "T")) Walk(value, use, depth + 1);
                    return;
                case PdfArray a:
                    foreach (var item in a) Walk(item, use, depth + 1);
                    return;
            }
        }
    }

    /// <summary>The comparison of the signed revision with the final file.</summary>
    private sealed class RevisionDiff(UsageMap before, UsageMap after)
    {
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        private bool _unnamed;

        public PdfChangeReport Run(CancellationToken ct)
        {
            var changes = PdfLaterChanges.None;
            if (before.CatalogNumber != after.CatalogNumber || before.Catalog == null || after.Catalog == null)
                return PdfChangeReport.Unreadable;
            changes |= CatalogChange(before.Catalog, after.Catalog);

            var bEntries = before.Doc.XrefTable.Entries;
            var aEntries = after.Doc.XrefTable.Entries;
            foreach (int number in bEntries.Keys.Union(aEntries.Keys))
            {
                if (number <= 0) continue;
                if ((changes & PdfLaterChanges.Other) != 0) break;
                ct.ThrowIfCancellationRequested();
                bool wasInUse = bEntries.TryGetValue(number, out var old) && old.IsInUse;
                bool isInUse = aEntries.TryGetValue(number, out var now) && now.IsInUse;
                if (!wasInUse && !isInUse) continue;
                // An object read from the same offset of the signed bytes is the same object. One in an
                // object stream is compared by content: the stream may have been written again.
                if (wasInUse && isInUse && old == now && !old.IsCompressed) continue;
                PdfObject? was = wasInUse ? before.R.Resolve(number) : null;
                PdfObject? obj = isInUse ? after.R.Resolve(number) : null;
                if (was == null && obj == null) continue;
                if (was != null && obj != null && Same(was, obj, before.R, after.R, 0)) continue;
                if (was == null && before.Dangling.Contains(number) && after.Uses.ContainsKey(number))
                {
                    // The signed revision referred to this number and drew nothing for it: defining it now changes what was signed.
                    changes |= PdfLaterChanges.Other;
                    continue;
                }
                changes |= Classify(number, was, obj);
            }
            // Pages that disappeared, or were added, are content changes.
            if (before.Doc.PageCount != after.Doc.PageCount) changes |= PdfLaterChanges.Other;
            return new PdfChangeReport(changes, _fields, _unnamed);
        }

        /// <summary>A new (<paramref name="was"/> null), changed or freed (<paramref name="now"/> null) object, by every place either revision uses it.</summary>
        private PdfLaterChanges Classify(int number, PdfObject? was, PdfObject? now)
        {
            var uses = new List<(Use Use, bool Before)>();
            if (before.Uses.TryGetValue(number, out var b)) uses.AddRange(b.Select(u => (u, true)));
            if (after.Uses.TryGetValue(number, out var a)) uses.AddRange(a.Select(u => (u, false)));
            if (uses.Count == 0) return PdfLaterChanges.None; // used nowhere: cross-reference streams, object streams, orphans

            // A page tree node is judged as one, whatever else refers to it: only its /Annots may change.
            if (uses.Any(u => u.Use.Kind == UseKind.PageNode))
            {
                if (was is PdfDictionary oldNode && now is PdfDictionary newNode && SameExcept(oldNode, newNode, "Annots"))
                    return AnnotsChange(before.R.Resolve(oldNode["Annots"]) as PdfArray, after.R.Resolve(newNode["Annots"]) as PdfArray);
                return PdfLaterChanges.Other;
            }

            var changes = PdfLaterChanges.None;
            foreach (var (use, inBefore) in uses)
            {
                changes |= use.Kind switch
                {
                    UseKind.Content or UseKind.Other => PdfLaterChanges.Other,
                    // Compared as a whole (CatalogChange); gone altogether is a change of everything.
                    UseKind.Catalog or UseKind.Form => now == null ? PdfLaterChanges.Other : PdfLaterChanges.None,
                    // A new /Annots array is judged with the page that refers to it.
                    UseKind.AnnotsArray => was == null ? PdfLaterChanges.None : AnnotsChange(was as PdfArray, now as PdfArray),
                    UseKind.Owned => OwnedChange(use.Owner, was, now),
                    UseKind.SignatureValue => was != null ? PdfLaterChanges.Other : SignatureAdded(use.Owner),
                    UseKind.FormResources => Unnamed(),
                    UseKind.Metadata => PdfLaterChanges.Metadata,
                    // Validation data: objects of the DSS that are new, or were validation data already. An
                    // existing object of any other kind does not become harmless by being listed in the DSS.
                    UseKind.Dss => was == null || HasUse(before, number, UseKind.Dss) ? PdfLaterChanges.ValidationData : PdfLaterChanges.Other,
                    _ => PdfLaterChanges.Other,
                };
                if ((changes & PdfLaterChanges.Other) != 0) return changes;
            }
            return changes;
        }

        private static bool HasUse(UsageMap map, int number, UseKind kind) =>
            map.Uses.TryGetValue(number, out var uses) && uses.Any(u => u.Kind == kind);

        private PdfLaterChanges Unnamed()
        {
            _unnamed = true;
            return PdfLaterChanges.FormFields;
        }

        /// <summary>A change to an annotation or a field, or to something it owns (its appearance, its value, its actions).</summary>
        private PdfLaterChanges OwnedChange(Owner? owner, PdfObject? was, PdfObject? now)
        {
            if (owner == null) return PdfLaterChanges.Annotations; // an /Annots item that is not a dictionary
            if (!owner.IsField) return PdfLaterChanges.Annotations;
            if (owner.IsSignature)
            {
                // A signature, once made, is part of what later signatures signed.
                if (SignedBefore(owner)) return PdfLaterChanges.Other;
                if (now == null) return PdfLaterChanges.Annotations; // an empty signature field removed
                return SignatureAdded(owner);
            }
            if (owner.FieldName != null) _fields.Add(owner.FieldName);
            else _unnamed = true;
            // A field (or widget) removed is not filling in a form.
            return now == null && was is PdfDictionary ? PdfLaterChanges.FormFields | PdfLaterChanges.Annotations : PdfLaterChanges.FormFields;
        }

        /// <summary>Whether the field (by number, or by the signature value it holds) was already signed in the signed revision.</summary>
        private bool SignedBefore(Owner owner)
        {
            if (owner.Number >= 0 && before.Owners.TryGetValue(owner.Number, out var old) && old.IsSignature && old.Value != null) return true;
            return owner.ValueNumber >= 0 && before.InUse(owner.ValueNumber) && before.R.Resolve(owner.ValueNumber) is PdfDictionary;
        }

        private PdfLaterChanges SignatureAdded(Owner? owner)
        {
            if (owner == null) return PdfLaterChanges.Signatures;
            if (IsInvisibleTimestampField(owner.Dict, after.R)) return PdfLaterChanges.DocumentTimestamp;
            return PdfLaterChanges.Signatures;
        }

        /// <summary>The catalog, compared entry by entry: only the form, the DSS and the metadata may change.</summary>
        private PdfLaterChanges CatalogChange(PdfDictionary was, PdfDictionary now)
        {
            if (!SameExcept(was, now, "AcroForm", "DSS", "Metadata")) return PdfLaterChanges.Other;
            var changes = PdfLaterChanges.None;
            if (!Same(was["Metadata"], now["Metadata"], null, null, 0)) changes |= PdfLaterChanges.Metadata;
            if (!Same(was["DSS"], now["DSS"], null, null, 0))
                changes |= now["DSS"] is PdfIndirectRef dss && before.InUse(dss.ObjectNumber) && !HasUse(before, dss.ObjectNumber, UseKind.Dss)
                    ? PdfLaterChanges.Other // an existing object made the DSS
                    : PdfLaterChanges.ValidationData;
            return changes | FormChange(before.Form, after.Form);
        }

        /// <summary>
        /// The form dictionary, wherever it is stored (its own object or the catalog): fields added or
        /// removed, and its form-wide entries. XFA, and anything this validator does not know, is a
        /// change of the document.
        /// </summary>
        private PdfLaterChanges FormChange(PdfDictionary? was, PdfDictionary? now)
        {
            if (was == null && now == null) return PdfLaterChanges.None;
            if (now == null) return PdfLaterChanges.Other;
            was ??= new PdfDictionary(new Dictionary<string, PdfObject>());
            var changes = PdfLaterChanges.None;
            // The fields, by what the arrays hold (the array may be its own object, changed in place).
            changes |= FieldsChange(before.R.Resolve(was["Fields"]) as PdfArray, after.R.Resolve(now["Fields"]) as PdfArray);
            foreach (var key in was.Entries.Keys.Union(now.Entries.Keys))
            {
                if (key == "Fields" || Same(was[key], now[key], null, null, 0)) continue;
                changes |= key switch
                {
                    "SigFlags" => PdfLaterChanges.None,
                    "NeedAppearances" or "DR" or "DA" or "Q" or "CO" => Unnamed(),
                    _ => PdfLaterChanges.Other, // /XFA among them
                };
            }
            return changes;
        }

        /// <summary>Fields added to or removed from the form's /Fields.</summary>
        private PdfLaterChanges FieldsChange(PdfArray? was, PdfArray? now)
        {
            var oldItems = was?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
            var newItems = now?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
            var changes = PdfLaterChanges.None;
            foreach (var item in newItems)
            {
                if (oldItems.Any(o => Same(o, item, null, null, 0))) continue;
                var owner = item is PdfIndirectRef ir ? after.Owners.GetValueOrDefault(ir.ObjectNumber) : null;
                if (owner == null) { changes |= Unnamed(); continue; }
                changes |= owner.IsSignature ? SignatureAdded(owner) : Field(owner) | PdfLaterChanges.FormFields;
            }
            foreach (var item in oldItems)
            {
                if (newItems.Any(n => Same(item, n, null, null, 0))) continue;
                var owner = item is PdfIndirectRef ir ? before.Owners.GetValueOrDefault(ir.ObjectNumber) : null;
                if (owner?.IsSignature == true && owner.Value != null) return PdfLaterChanges.Other; // a signature removed
                changes |= (owner != null ? Field(owner) : Unnamed()) | PdfLaterChanges.FormFields;
            }
            return changes;
        }

        private PdfLaterChanges Field(Owner owner)
        {
            if (owner.FieldName != null) _fields.Add(owner.FieldName);
            else _unnamed = true;
            return PdfLaterChanges.None;
        }

        /// <summary>What a page's /Annots gained or lost: widgets are form (or signature) changes, anything else is commenting.</summary>
        private PdfLaterChanges AnnotsChange(PdfArray? was, PdfArray? now)
        {
            var oldItems = was?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
            var newItems = now?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
            var changes = PdfLaterChanges.None;
            foreach (var item in newItems)
            {
                if (oldItems.Any(o => Same(o, item, null, null, 0))) continue;
                var owner = item is PdfIndirectRef ir ? after.Owners.GetValueOrDefault(ir.ObjectNumber) : null;
                if (owner == null || !owner.IsField) { changes |= PdfLaterChanges.Annotations; continue; }
                changes |= owner.IsSignature
                    ? SignedBefore(owner) ? PdfLaterChanges.Other : SignatureAdded(owner)
                    : Field(owner) | PdfLaterChanges.FormFields;
            }
            if (oldItems.Any(o => !newItems.Any(n => Same(o, n, null, null, 0))))
                changes |= PdfLaterChanges.Annotations; // something was removed
            return changes;
        }
    }

    /// <summary>
    /// The FieldMDP lock of a signature (12.7.5.5 and 12.8.2.4): the signature dictionary's FieldMDP
    /// reference, and the signature field's /Lock. Null when the signature locks no field.
    /// </summary>
    private static (string Action, string[] Fields)? FieldLock(PdfDictionary sig, PdfDictionary? field, Parsing.PdfObjectResolver r)
    {
        static (string, string[])? Read(PdfDictionary? d, Parsing.PdfObjectResolver r)
        {
            if (d?.GetName("Action") is not { } action) return null;
            var names = (r.Resolve(d["Fields"]) as PdfArray)?.Select(o => r.Resolve(o) is PdfString s ? s.AsDecodedString() : null)
                .Where(s => s != null).Select(s => s!).ToArray() ?? Array.Empty<string>();
            return (action, names);
        }
        if (r.Resolve(sig["Reference"]) is PdfArray refs)
            foreach (var item in refs)
                if (r.Resolve(item) is PdfDictionary reference && reference.GetName("TransformMethod") == "FieldMDP"
                    && Read(r.Resolve(reference["TransformParams"]) as PdfDictionary, r) is { } fromSignature)
                    return fromSignature;
        return Read(r.Resolve(field?["Lock"]) as PdfDictionary, r);
    }

    /// <summary>Whether later changes touched a field the signature locks.</summary>
    private static bool LockedFieldChanged((string Action, string[] Fields) fieldLock, PdfChangeReport report)
    {
        if ((report.Changes & PdfLaterChanges.FormFields) == 0) return false;
        if (report.UnnamedFields) return true; // could be any field
        // A name covers the field and its kids; a change to a parent changes its kids' inherited value.
        bool Listed(string changed) => fieldLock.Fields.Any(l =>
            changed == l || changed.StartsWith(l + ".", StringComparison.Ordinal) || l.StartsWith(changed + ".", StringComparison.Ordinal));
        return fieldLock.Action switch
        {
            "Include" => report.Fields.Any(Listed),
            "Exclude" => report.Fields.Any(f => !Listed(f)),
            _ => report.Fields.Count > 0, // /All, and any action this validator does not know
        };
    }
}
