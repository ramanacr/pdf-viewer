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
/// <param name="Unverifiable">The changes could not be worked out (unreadable, damaged or too large to check); reported as a change.</param>
internal sealed record PdfChangeReport(PdfLaterChanges Changes, IReadOnlySet<string> Fields, bool UnnamedFields, bool Unverifiable = false)
{
    public static readonly PdfChangeReport Unreadable = new(PdfLaterChanges.Other, new HashSet<string>(), true, Unverifiable: true);
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
    /// fields and their values, signature values, metadata, validation data or anything else the
    /// catalog holds. An object's own /Type, /Subtype, /FT or /ByteRange never decides what it is:
    /// a page's content stream labelled as anything is still page content. An object used in
    /// several places counts as the most significant of them. Whatever cannot be worked out
    /// (a damaged file, a structure too large to check) is reported as a change, never as none.
    /// </summary>
    internal static async Task<PdfChangeReport> ChangeReportAsync(byte[] file, int revisionEnd, string? password, CancellationToken ct)
    {
        try
        {
            using var before = await PdfVectorDocument.OpenAsync(file.AsSpan(0, revisionEnd).ToArray(), password: password, cancellationToken: ct).ConfigureAwait(false);
            using var after = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: ct).ConfigureAwait(false);
            bool repairedBefore = before.WasRepaired, repairedAfter = after.WasRepaired;
            var budget = new WorkBudget(ct, Math.Max(2_000_000, 64L * (before.XrefTable.Entries.Count + after.XrefTable.Entries.Count)));
            var beforeMap = new UsageMap(before, null, budget);
            var afterMap = new UsageMap(after, beforeMap, budget);
            var report = new RevisionDiff(beforeMap, afterMap, budget).Run();
            // A cross-reference table rebuilt while reading means the entries compared were not the ones read.
            if (before.WasRepaired != repairedBefore || after.WasRepaired != repairedAfter) return PdfChangeReport.Unreadable;
            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return PdfChangeReport.Unreadable; // a revision cannot be read, or the work budget ran out
        }
    }

    /// <summary>How much reading the change analysis may do; past it, the changes are unverifiable.</summary>
    private sealed class WorkBudget(CancellationToken ct, long limit)
    {
        private long _steps;

        public void Step()
        {
            if (++_steps > limit) throw new InvalidOperationException("The document is too large to check for changes.");
            if ((_steps & 1023) == 0) ct.ThrowIfCancellationRequested();
        }
    }

    // Where an object is used: one bit each. Owner bits: the annotation or field dictionary itself,
    // by category. Owned bits: what an annotation or field refers to, by category and by the key it
    // is reached through.
    private const int ContentBit = 0, OtherBit = 1, MetadataBit = 2, DssBit = 3, FormResourcesBit = 4,
        PageNodeBit = 5, AnnotsArrayBit = 6, FormBit = 7, CatalogBit = 8, OwnerBase = 9, OwnedBase = 16;

    /// <summary>What an annotation or field is, for what changing it (or what it owns) means.</summary>
    private enum Category { Markup, Field, SignatureNew, SignatureSigned, Timestamp }

    /// <summary>How an annotation or field refers to an object it owns.</summary>
    private enum Via { Appearance, Value, Action, Other }

    private const int Categories = 5, Vias = 4;

    private static int OwnerBit(Category c) => OwnerBase + (int)c;
    private static int OwnedBit(Category c, Via v) => OwnedBase + (int)c * Vias + (int)v;
    private static bool IsFieldOwned(int bit) => bit >= OwnedBit(Category.Field, 0) && bit < OwnedBit(Category.Field + 1, 0);

    private static Via ViaKey(string key) => key switch
    {
        "AP" => Via.Appearance,
        "V" => Via.Value,
        "A" or "AA" => Via.Action,
        _ => Via.Other,
    };

    /// <summary>An annotation or form field.</summary>
    private sealed class Owner
    {
        public int Number = -1;              // -1 when the dictionary is direct
        public required PdfDictionary Dict;
        public bool IsField;                  // a form field or a widget
        public bool IsSignature;              // a field whose (inherited) /FT is /Sig
        public string? FieldName;             // the field's full name
        public PdfDictionary? Value;          // a signature field's signature dictionary
        public int ValueNumber = -1;
        public Category Category;
    }

    /// <summary>For one revision: every object reachable from the trailer, and where it is used.</summary>
    private sealed class UsageMap
    {
        public readonly PdfVectorDocument Doc;
        public readonly Parsing.PdfObjectResolver R;
        public readonly Dictionary<int, ulong> Uses = new();
        /// <summary>For objects owned by form fields: the one field name they belong to, or null for several (or unnamed).</summary>
        public readonly Dictionary<int, string?> FieldNames = new();
        /// <summary>Annotations, fields, page tree nodes, /Annots and /Fields arrays, the catalog and the form.</summary>
        public readonly HashSet<int> Structural = new();
        public readonly Dictionary<int, Owner> Owners = new();
        /// <summary>Objects reached as form fields (from /Fields and /Kids).</summary>
        public readonly HashSet<int> FieldNodes = new();
        /// <summary>Object numbers referred to that the revision does not define.</summary>
        public readonly HashSet<int> Dangling = new();
        /// <summary>Page tree nodes that are their own objects.</summary>
        public readonly HashSet<int> PageNodes = new();
        public readonly PdfDictionary? Catalog;
        public readonly int CatalogNumber = -1;
        public readonly PdfDictionary? Form;
        private readonly UsageMap? _before;
        private readonly WorkBudget _budget;
        private readonly HashSet<(int, int)> _walked = new();

        public UsageMap(PdfVectorDocument doc, UsageMap? before, WorkBudget budget)
        {
            Doc = doc;
            R = doc.Resolver;
            _before = before;
            _budget = budget;
            var trailer = doc.XrefTable.Trailer;
            if (trailer?["Root"] is PdfIndirectRef rootRef)
            {
                CatalogNumber = rootRef.ObjectNumber;
                Catalog = R.Resolve(rootRef) as PdfDictionary;
            }
            if (Catalog == null) return;
            Mark(CatalogNumber, CatalogBit);
            Structural.Add(CatalogNumber);

            // First the structure (what is a page, an annotation, a field), then what each part refers to.
            var leaves = new List<PdfDictionary>();
            var nodes = new List<PdfDictionary>();
            PageTree(Catalog["Pages"], leaves, nodes, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), 0);
            var owners = new List<Owner>();
            foreach (var page in leaves)
            {
                if (page["Annots"] is PdfIndirectRef annotsRef)
                {
                    Structural.Add(annotsRef.ObjectNumber);
                    Mark(annotsRef.ObjectNumber, AnnotsArrayBit);
                }
                if (R.Resolve(page["Annots"]) is not PdfArray annots) continue;
                foreach (var item in annots)
                {
                    _budget.Step();
                    if (OwnerOf(item, field: false) is { } owner) owners.Add(owner);
                }
            }
            if (Catalog["AcroForm"] is PdfIndirectRef formRef)
            {
                Structural.Add(formRef.ObjectNumber);
                Mark(formRef.ObjectNumber, FormBit);
            }
            Form = R.Resolve(Catalog["AcroForm"]) as PdfDictionary;
            if (Form != null)
            {
                if (Form["Fields"] is PdfIndirectRef fieldsRef)
                {
                    Structural.Add(fieldsRef.ObjectNumber);
                    Mark(fieldsRef.ObjectNumber, FormBit); // compared with the form (FormChange)
                }
                if (R.Resolve(Form["Fields"]) is PdfArray formFields) FieldTree(formFields, owners, 0);
            }
            foreach (var owner in owners)
            {
                owner.Category = CategoryOf(owner);
                if (owner.Number >= 0) Mark(owner.Number, OwnerBit(owner.Category));
            }

            foreach (var node in nodes) WalkEntries(node, ContentBit, null, "Kids", "Parent");
            foreach (var page in leaves) WalkEntries(page, ContentBit, null, "Annots", "Parent");
            foreach (var owner in owners.Distinct())
                foreach (var (key, value) in owner.Dict.Entries)
                    Walk(value, OwnedBit(owner.Category, ViaKey(key)), owner.FieldName);
            if (Form != null)
                foreach (var (key, value) in Form.Entries)
                    if (key != "Fields") Walk(value, key == "XFA" ? ContentBit : FormResourcesBit, null);
            foreach (var (key, value) in Catalog.Entries)
            {
                int bit = key switch
                {
                    "Pages" or "AcroForm" => -1,
                    "Metadata" => MetadataBit,
                    "DSS" => DssBit,
                    _ => OtherBit,
                };
                if (bit >= 0) Walk(value, bit, null);
            }
            Walk(trailer?["Info"], MetadataBit, null);
            Walk(trailer?["Encrypt"], OtherBit, null);
        }

        public bool InUse(int number) => Doc.XrefTable.Entries.TryGetValue(number, out var e) && e.IsInUse;

        public ulong UsesOf(int number) => Uses.TryGetValue(number, out var u) ? u : 0;

        private void Mark(int number, int bit)
        {
            Uses[number] = UsesOf(number) | (1UL << bit);
            if (!InUse(number)) Dangling.Add(number);
        }

        /// <summary>The leaf pages and intermediate nodes, read as the renderer reads them (<see cref="PdfPageTree"/>): direct or indirect nodes, a leaf being /Type /Page or a node with /Contents and no /Kids.</summary>
        private void PageTree(PdfObject? node, List<PdfDictionary> leaves, List<PdfDictionary> nodes, HashSet<PdfDictionary> visited, int depth)
        {
            _budget.Step();
            if (depth > 64) throw new InvalidOperationException("The page tree is too deep to check.");
            if (node is PdfIndirectRef nodeRef)
            {
                Mark(nodeRef.ObjectNumber, PageNodeBit);
                Structural.Add(nodeRef.ObjectNumber);
                PageNodes.Add(nodeRef.ObjectNumber);
            }
            if (R.Resolve(node) is not PdfDictionary dict || !visited.Add(dict)) return;
            if (dict.GetName("Type") == "Page" || (!dict.ContainsKey("Kids") && dict.ContainsKey("Contents")))
            {
                leaves.Add(dict);
                return;
            }
            nodes.Add(dict);
            if (dict["Kids"] is PdfIndirectRef kidsRef)
            {
                Mark(kidsRef.ObjectNumber, PageNodeBit);
                Structural.Add(kidsRef.ObjectNumber);
            }
            if (R.Resolve(dict["Kids"]) is PdfArray kids)
                foreach (var kid in kids) PageTree(kid, leaves, nodes, visited, depth + 1);
        }

        private void FieldTree(PdfArray items, List<Owner> owners, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("The form's field tree is too deep to check.");
            foreach (var item in items)
            {
                _budget.Step();
                if (item is PdfIndirectRef ir && !FieldNodes.Add(ir.ObjectNumber)) continue; // listed twice, or a loop in /Kids
                if (OwnerOf(item, field: true) is not { } owner) continue;
                owners.Add(owner);
                if (R.Resolve(owner.Dict["Kids"]) is PdfArray kids) FieldTree(kids, owners, depth + 1);
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
                        Mark(ir.ObjectNumber, OwnerBit(Category.Markup));
                        return null;
                    }
                    known = Describe(d, ir.ObjectNumber, field);
                    Owners[ir.ObjectNumber] = known;
                }
                else if (field && !known.IsField)
                {
                    var described = Describe(known.Dict, ir.ObjectNumber, true);
                    (known.IsField, known.IsSignature, known.FieldName, known.Value, known.ValueNumber) =
                        (true, described.IsSignature, described.FieldName, described.Value, described.ValueNumber);
                }
                return known;
            }
            return item is PdfDictionary direct ? Describe(direct, -1, field) : null;
        }

        /// <summary>An annotation or field dictionary, with its field type, value and name inherited through /Parent (12.7.4.1).</summary>
        public Owner Describe(PdfDictionary d, int number, bool field)
        {
            var owner = new Owner { Number = number, Dict = d, IsField = field || d.GetName("Subtype") == "Widget" };
            if (!owner.IsField) return owner;
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
        /// What changing the annotation or field means: a signature field signed in the signed
        /// revision is part of what was signed; an invisible document timestamp field with no
        /// actions is a timestamp; any other signature field is signing.
        /// </summary>
        public Category CategoryOf(Owner owner)
        {
            if (!owner.IsField) return Category.Markup;
            if (!owner.IsSignature) return Category.Field;
            if (_before == null) return owner.Value != null ? Category.SignatureSigned : Category.SignatureNew;
            if (_before.WasSigned(owner)) return Category.SignatureSigned;
            return IsInvisibleTimestampField(owner.Dict, R) && owner.Dict["A"] == null && owner.Dict["AA"] == null
                ? Category.Timestamp
                : Category.SignatureNew;
        }

        /// <summary>Whether this (signed) revision has the field, or the signature value it holds, signed.</summary>
        public bool WasSigned(Owner owner)
        {
            if (owner.Number >= 0 && Owners.TryGetValue(owner.Number, out var old) && old.IsSignature && old.Value != null) return true;
            return owner.ValueNumber >= 0 && InUse(owner.ValueNumber) && R.Resolve(owner.ValueNumber) is PdfDictionary;
        }

        private void WalkEntries(PdfDictionary dict, int bit, string? name, params string[] skip)
        {
            foreach (var (key, value) in dict.Entries)
                if (Array.IndexOf(skip, key) < 0) Walk(value, bit, name);
        }

        /// <summary>
        /// Notes every object <paramref name="start"/> refers to as used the given way, without
        /// recursion and within the work budget. Page content goes into everything it reaches,
        /// annotations and fields included (page content wins), except a page node's /Annots, /Kids
        /// and /Parent. Every other use stops at annotations, fields, pages, the form and the
        /// catalog: they are judged as themselves. Objects owned by form fields keep the field's
        /// name, or none when several fields share them.
        /// </summary>
        private void Walk(PdfObject? start, int bit, string? name)
        {
            if (start == null) return;
            bool content = bit == ContentBit, fieldOwned = IsFieldOwned(bit);
            var stack = new Stack<(PdfObject Obj, string? Name, int Number)>();
            stack.Push((start, name, -1));
            while (stack.Count > 0)
            {
                _budget.Step();
                var (o, nm, from) = stack.Pop();
                switch (o)
                {
                    case PdfIndirectRef ir:
                    {
                        int n = ir.ObjectNumber;
                        if (Structural.Contains(n) && !content)
                        {
                            if (!InUse(n)) Dangling.Add(n);
                            continue;
                        }
                        Mark(n, bit);
                        bool first = _walked.Add((n, bit));
                        string? carry = nm;
                        if (fieldOwned)
                        {
                            bool had = FieldNames.TryGetValue(n, out var current);
                            string? merged = !had ? nm : current != null && current == nm ? current : null;
                            bool widened = had && current != null && merged == null;
                            FieldNames[n] = merged;
                            if (!first && !widened) continue;
                            carry = merged;
                        }
                        else if (!first) continue;
                        if (R.Resolve(n) is { } resolved) stack.Push((resolved, carry, n));
                        continue;
                    }
                    case PdfStream s:
                        stack.Push((s.Dictionary, nm, from));
                        continue;
                    case PdfDictionary d:
                    {
                        bool pageNode = content && from >= 0 && PageNodes.Contains(from);
                        // Form actions name the fields they act on; naming a field does not draw it.
                        bool formAction = d.GetName("S") is "SubmitForm" or "ResetForm" or "ImportData" or "Hide";
                        foreach (var (key, value) in d.Entries)
                        {
                            if (pageNode && key is "Annots" or "Kids" or "Parent") continue;
                            if (formAction && key is "Fields" or "T") continue;
                            stack.Push((value, nm, -1));
                        }
                        continue;
                    }
                    case PdfArray a:
                        foreach (var item in a) stack.Push((item, nm, -1));
                        continue;
                }
            }
        }
    }

    /// <summary>The comparison of the signed revision with the final file.</summary>
    private sealed class RevisionDiff(UsageMap before, UsageMap after, WorkBudget budget)
    {
        private const int MaxDirectCompared = 512;
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        private bool _unnamed;

        public PdfChangeReport Run()
        {
            if (before.CatalogNumber != after.CatalogNumber || before.Catalog == null || after.Catalog == null)
                return PdfChangeReport.Unreadable;
            var changes = CatalogChange(before.Catalog, after.Catalog);

            // A signature signed in the signed revision must still be a signed field of the form, with the same value.
            foreach (var (number, old) in before.Owners)
            {
                if (!old.IsSignature || old.Value == null || !before.FieldNodes.Contains(number)) continue;
                if (!after.FieldNodes.Contains(number) || !after.Owners.TryGetValue(number, out var now) || now.Value == null
                    || now.ValueNumber != old.ValueNumber)
                    return Report(changes | PdfLaterChanges.Other);
            }

            // Snapshots: reading an object may rebuild a damaged cross-reference table.
            var bEntries = before.Doc.XrefTable.Entries.ToDictionary(e => e.Key, e => e.Value);
            var aEntries = after.Doc.XrefTable.Entries.ToDictionary(e => e.Key, e => e.Value);
            foreach (int number in bEntries.Keys.Union(aEntries.Keys))
            {
                if (number <= 0) continue;
                if ((changes & PdfLaterChanges.Other) != 0) break;
                budget.Step();
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
            return Report(changes);
        }

        private PdfChangeReport Report(PdfLaterChanges changes) => new(changes, _fields, _unnamed);

        /// <summary>A new (<paramref name="was"/> null), changed or freed (<paramref name="now"/> null) object, by every place either revision uses it.</summary>
        private PdfLaterChanges Classify(int number, PdfObject? was, PdfObject? now)
        {
            ulong uses = before.UsesOf(number) | after.UsesOf(number);
            bool Has(int bit) => (uses & (1UL << bit)) != 0;
            // Used nowhere: a new object is harmless (cross-reference and object streams, orphans);
            // an object of the signed revision that changed or went is not known to be.
            if (uses == 0) return was == null ? PdfLaterChanges.None : PdfLaterChanges.Other;

            // A page tree node is judged as one, whatever else refers to it: only its pages' /Annots may change.
            if (Has(PageNodeBit)) return PageNodeChange(was, now, 0);
            if (Has(ContentBit) || Has(OtherBit)) return PdfLaterChanges.Other;

            var changes = PdfLaterChanges.None;
            // Compared as a whole (CatalogChange, FormChange); gone altogether is a change of everything.
            if ((Has(CatalogBit) || Has(FormBit)) && now == null) return PdfLaterChanges.Other;
            // A new /Annots array is judged with the page that refers to it.
            if (Has(AnnotsArrayBit) && was != null) changes |= AnnotsChange(was as PdfArray, now as PdfArray);
            if (Has(FormResourcesBit)) changes |= Unnamed();
            if (Has(MetadataBit)) changes |= PdfLaterChanges.Metadata;
            // Validation data: objects of the DSS that are new, or were validation data already. An
            // existing object of any other kind does not become harmless by being listed in the DSS.
            if (Has(DssBit))
                changes |= was == null || (before.UsesOf(number) & (1UL << DssBit)) != 0 ? PdfLaterChanges.ValidationData : PdfLaterChanges.Other;
            for (int c = 0; c < Categories; c++)
            {
                var category = (Category)c;
                if (Has(OwnerBit(category))) changes |= OwnerChange(number, category, was, now);
                for (int v = 0; v < Vias; v++)
                    if (Has(OwnedBit(category, (Via)v))) changes |= OwnedChange(number, category, (Via)v, was);
            }
            return changes;
        }

        private PdfLaterChanges Unnamed()
        {
            _unnamed = true;
            return PdfLaterChanges.FormFields;
        }

        private void Name(string? name)
        {
            if (name != null) _fields.Add(name);
            else _unnamed = true;
        }

        private static readonly HashSet<string> FieldValueKeys = new(StringComparer.Ordinal) { "V", "I", "AS", "AP", "RV" };
        private static readonly HashSet<string> SigningKeys = new(StringComparer.Ordinal) { "V", "AP", "AS" };
        private static readonly HashSet<string> TimestampKeys = new(StringComparer.Ordinal) { "V", "AP" };

        /// <summary>
        /// An annotation or field dictionary added, changed or removed. Filling a field may change
        /// its value and appearance only; signing an empty signature field, its value and
        /// appearance; anything else about a field (where it is, how it looks, its flags) is
        /// editing an annotation; its actions are a change of the document.
        /// </summary>
        private PdfLaterChanges OwnerChange(int number, Category category, PdfObject? was, PdfObject? now)
        {
            var owner = after.Owners.GetValueOrDefault(number) ?? before.Owners.GetValueOrDefault(number);
            if (category == Category.SignatureSigned) return PdfLaterChanges.Other;
            if (category == Category.Field) Name(owner?.FieldName);
            if (now == null) return PdfLaterChanges.Annotations; // removed
            if (now is not PdfDictionary newDict) return PdfLaterChanges.Other;
            if (was == null)
                return category switch
                {
                    Category.Markup or Category.Field => PdfLaterChanges.Annotations, // a new field is not filling one in
                    Category.Timestamp => PdfLaterChanges.DocumentTimestamp,
                    _ => newDict["A"] != null || newDict["AA"] != null ? PdfLaterChanges.Other : PdfLaterChanges.Signatures,
                };
            if (was is not PdfDictionary oldDict) return PdfLaterChanges.Other;
            var changed = oldDict.Entries.Keys.Union(newDict.Entries.Keys).Where(k => !Same(oldDict[k], newDict[k], null, null, 0)).ToList();
            if (changed.Contains("AA")) return PdfLaterChanges.Other;
            if (category == Category.Markup) return PdfLaterChanges.Annotations;
            if (changed.Contains("A")) return PdfLaterChanges.Other;
            return category switch
            {
                Category.Field => changed.All(FieldValueKeys.Contains) ? PdfLaterChanges.FormFields : PdfLaterChanges.Annotations,
                Category.Timestamp => changed.All(TimestampKeys.Contains) ? PdfLaterChanges.DocumentTimestamp : PdfLaterChanges.Annotations,
                _ => changed.All(SigningKeys.Contains) ? PdfLaterChanges.Signatures : PdfLaterChanges.Annotations,
            };
        }

        /// <summary>Something an annotation or field refers to, by what it is to it.</summary>
        private PdfLaterChanges OwnedChange(int number, Category category, Via via, PdfObject? was)
        {
            if (category == Category.SignatureSigned) return PdfLaterChanges.Other; // part of a signature already made
            if (via == Via.Action)
                return category == Category.Markup && was == null ? PdfLaterChanges.Annotations : PdfLaterChanges.Other;
            if (category == Category.Field)
            {
                Name(after.FieldNames.TryGetValue(number, out var n) ? n : before.FieldNames.GetValueOrDefault(number));
                return via is Via.Appearance or Via.Value ? PdfLaterChanges.FormFields : PdfLaterChanges.Annotations;
            }
            return (category, via) switch
            {
                (Category.Markup, _) => PdfLaterChanges.Annotations,
                (Category.Timestamp, Via.Value or Via.Appearance) => PdfLaterChanges.DocumentTimestamp,
                (_, Via.Value or Via.Appearance) => PdfLaterChanges.Signatures,
                _ => PdfLaterChanges.Annotations,
            };
        }

        /// <summary>A page tree node: only the /Annots of its pages may change, including pages written directly in its /Kids.</summary>
        private PdfLaterChanges PageNodeChange(PdfObject? was, PdfObject? now, int depth)
        {
            budget.Step();
            if (depth > 64 || was is not PdfDictionary oldNode || now is not PdfDictionary newNode) return PdfLaterChanges.Other;
            if (!SameExcept(oldNode, newNode, "Annots", "Kids")) return PdfLaterChanges.Other;
            var changes = PdfLaterChanges.None;
            if (!Same(oldNode["Kids"], newNode["Kids"], null, null, 0))
            {
                if (oldNode["Kids"] is not PdfArray oldKids || newNode["Kids"] is not PdfArray newKids || oldKids.Count != newKids.Count)
                    return PdfLaterChanges.Other;
                for (int i = 0; i < oldKids.Count; i++)
                {
                    if (Same(oldKids[i], newKids[i], null, null, 0)) continue;
                    changes |= oldKids[i] is PdfDictionary && newKids[i] is PdfDictionary
                        ? PageNodeChange(oldKids[i], newKids[i], depth + 1)
                        : PdfLaterChanges.Other;
                }
            }
            return changes | AnnotsChange(before.R.Resolve(oldNode["Annots"]) as PdfArray, after.R.Resolve(newNode["Annots"]) as PdfArray);
        }

        /// <summary>The catalog, compared entry by entry: only the form, the DSS, the metadata and the pages' annotations may change.</summary>
        private PdfLaterChanges CatalogChange(PdfDictionary was, PdfDictionary now)
        {
            if (!SameExcept(was, now, "AcroForm", "DSS", "Metadata", "Pages")) return PdfLaterChanges.Other;
            var changes = PdfLaterChanges.None;
            if (!Same(was["Pages"], now["Pages"], null, null, 0))
                changes |= was["Pages"] is PdfDictionary && now["Pages"] is PdfDictionary ? PageNodeChange(was["Pages"], now["Pages"], 0) : PdfLaterChanges.Other;
            if (!Same(was["Metadata"], now["Metadata"], null, null, 0)) changes |= PdfLaterChanges.Metadata;
            if (!Same(was["DSS"], now["DSS"], null, null, 0))
                changes |= now["DSS"] is PdfIndirectRef dss && before.InUse(dss.ObjectNumber) && (before.UsesOf(dss.ObjectNumber) & (1UL << DssBit)) == 0
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
            // The fields, by what the arrays hold (the array may be its own object, changed in place).
            var changes = ItemsChange(before.R.Resolve(was["Fields"]) as PdfArray, after.R.Resolve(now["Fields"]) as PdfArray, annots: false);
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

        private PdfLaterChanges AnnotsChange(PdfArray? was, PdfArray? now) => ItemsChange(was, now, annots: true);

        /// <summary>
        /// What a page's /Annots or the form's /Fields gained or lost. A new signature field is
        /// signing (or a document timestamp); any other new annotation or field is commenting.
        /// Removing a signature already made is a change of the document; removing anything else
        /// is editing annotations.
        /// </summary>
        private PdfLaterChanges ItemsChange(PdfArray? was, PdfArray? now, bool annots)
        {
            var oldItems = was?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
            var newItems = now?.Items ?? (IReadOnlyList<PdfObject>)Array.Empty<PdfObject>();
            var oldRefs = oldItems.OfType<PdfIndirectRef>().Select(r => r.ObjectNumber).ToHashSet();
            var newRefs = newItems.OfType<PdfIndirectRef>().Select(r => r.ObjectNumber).ToHashSet();
            var oldDirect = oldItems.Where(o => o is not PdfIndirectRef).ToList();
            var newDirect = newItems.Where(o => o is not PdfIndirectRef).ToList();
            bool compareDirect = oldDirect.Count <= MaxDirectCompared && newDirect.Count <= MaxDirectCompared;
            var changes = PdfLaterChanges.None;

            foreach (var item in newItems)
            {
                budget.Step();
                Owner? owner;
                if (item is PdfIndirectRef ir)
                {
                    if (oldRefs.Contains(ir.ObjectNumber)) continue;
                    owner = after.Owners.GetValueOrDefault(ir.ObjectNumber);
                }
                else
                {
                    if (compareDirect && oldDirect.Any(o => Same(o, item, null, null, 0))) continue;
                    owner = item is PdfDictionary d ? after.Describe(d, -1, field: !annots) : null;
                    if (owner != null) owner.Category = after.CategoryOf(owner);
                }
                if (owner == null) { changes |= annots ? PdfLaterChanges.Annotations : Unnamed(); continue; }
                if (owner.IsField && !owner.IsSignature) Name(owner.FieldName);
                changes |= owner.Category switch
                {
                    Category.SignatureSigned => PdfLaterChanges.Other,
                    Category.SignatureNew => PdfLaterChanges.Signatures,
                    Category.Timestamp => PdfLaterChanges.DocumentTimestamp,
                    _ => PdfLaterChanges.Annotations, // a new field is not filling one in
                };
            }
            foreach (var item in oldItems)
            {
                budget.Step();
                Owner? owner;
                if (item is PdfIndirectRef ir)
                {
                    if (newRefs.Contains(ir.ObjectNumber)) continue;
                    owner = before.Owners.GetValueOrDefault(ir.ObjectNumber);
                }
                else
                {
                    if (compareDirect && newDirect.Any(n => Same(item, n, null, null, 0))) continue;
                    owner = item is PdfDictionary d ? before.Describe(d, -1, field: !annots) : null;
                }
                if (owner is { IsSignature: true, Value: not null }) return PdfLaterChanges.Other; // a signature removed
                if (owner is { IsField: true }) Name(owner.FieldName);
                changes |= PdfLaterChanges.Annotations;
            }
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
        if (report.UnnamedFields) return true; // could be any field
        if (report.Fields.Count == 0) return false;
        // A name covers the field and its kids; a change to a parent changes its kids' inherited value.
        bool Listed(string changed) => fieldLock.Fields.Any(l =>
            changed == l || changed.StartsWith(l + ".", StringComparison.Ordinal) || l.StartsWith(changed + ".", StringComparison.Ordinal));
        return fieldLock.Action switch
        {
            "Include" => report.Fields.Any(Listed),
            "Exclude" => report.Fields.Any(f => !Listed(f)),
            _ => true, // /All, and any action this validator does not know
        };
    }
}
