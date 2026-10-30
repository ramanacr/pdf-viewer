using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
    /// <param name="signatures">Signature checks already made for this file (shared by the analyses of its signatures).</param>
    internal static async Task<PdfChangeReport> ChangeReportAsync(byte[] file, int revisionEnd, string? password, CancellationToken ct,
        SignatureCheckCache? signatures = null)
    {
        signatures ??= new SignatureCheckCache();
        try
        {
            using var before = await PdfVectorDocument.OpenAsync(file.AsSpan(0, revisionEnd).ToArray(), password: password, cancellationToken: ct).ConfigureAwait(false);
            using var after = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: ct).ConfigureAwait(false);
            // The revisions the later signatures signed, worked out once per file.
            await signatures.PrepareAsync(file, password,
                SignedFields(after).Where(s => s.Range.Length == 4).Select(s => s.Range[2] + s.Range[3]).Where(e => e > revisionEnd), ct).ConfigureAwait(false);
            bool repairedBefore = before.WasRepaired, repairedAfter = after.WasRepaired;
            // The budget grows with the file: a legitimate file is never too large to check, a crafted one cannot run away.
            long limit = Math.Max(2_000_000, Math.Max(64L * (before.XrefTable.Entries.Count + after.XrefTable.Entries.Count), file.LongLength / 4));
            var budget = new WorkBudget(ct, limit);
            var beforeMap = new UsageMap(before, null, budget);
            var afterMap = new UsageMap(after, beforeMap, budget, revisionEnd, signatures);
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

    /// <summary>
    /// The signing revisions of one file, worked out once and shared by the analyses of all its
    /// signatures. A file whose signatures end at more than <see cref="MaxChecks"/> places, or
    /// whose signature checks would hash too much, cannot be analysed (its changes are unverifiable).
    /// </summary>
    internal sealed class SignatureCheckCache
    {
        public const int MaxChecks = 1024;
        /// <summary>By byte range end: the revision that signature signed, or null when it is not a signing revision.</summary>
        internal readonly Dictionary<long, SigningRevision?> Revisions = new();
        private long _hashed;

        internal async Task PrepareAsync(byte[] file, string? password, IEnumerable<long> ends, CancellationToken ct)
        {
            foreach (long end in ends.Distinct())
            {
                if (Revisions.ContainsKey(end)) continue;
                if (Revisions.Count >= MaxChecks) throw new InvalidOperationException("The document holds too many signatures to check.");
                Revisions[end] = await SigningRevisionAsync(file, password, end, this, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Counts bytes hashed to check signatures; past a limit that grows with the file, the file is not checked.</summary>
        internal void Hashed(long bytes, long fileLength)
        {
            _hashed += bytes;
            if (_hashed > Math.Max(1L << 30, 256L * fileLength)) throw new InvalidOperationException("The document's signatures cover too much to check.");
        }
    }

    /// <summary>An object as a signature signed it: a stream by its dictionary and the hash of its decoded data, anything else as itself.</summary>
    internal sealed class SignedObject
    {
        private readonly PdfObject? _object;
        private readonly PdfDictionary? _streamDictionary;
        private readonly byte[]? _streamHash;

        public SignedObject(PdfObject? obj, Parsing.PdfObjectResolver r)
        {
            if (obj is PdfStream s)
            {
                _streamDictionary = s.Dictionary;
                _streamHash = SHA256.HashData(r.DecodeStream(s));
            }
            else _object = obj;
        }

        public bool Matches(PdfObject? now, Parsing.PdfObjectResolver r)
        {
            if (_streamDictionary == null) return now is not PdfStream && Same(_object, now, null, null, 0);
            if (now is not PdfStream s || !SameEntries(_streamDictionary, s.Dictionary, null, null, 0, "Length", "Filter", "DecodeParms", "DL")) return false;
            try { return SHA256.HashData(r.DecodeStream(s)).AsSpan().SequenceEqual(_streamHash); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

    /// <summary>
    /// A signing revision: the revision a signature signed, as its signer saw it (the file up to
    /// the end of its byte range). It holds the one signature field whose new signature dictionary
    /// (with /ByteRange and /Contents written directly) has its value in that revision's gap and
    /// verifies; and the objects that make up that field, as signed: the field, its widgets, the
    /// signature dictionary and the appearances.
    /// </summary>
    internal sealed class SigningRevision
    {
        public int Holder, Value;
        public readonly Dictionary<int, SignedObject> Objects = new();
    }

    private static async Task<SigningRevision?> SigningRevisionAsync(byte[] file, string? password, long end, SignatureCheckCache cache, CancellationToken ct)
    {
        if (end <= 0 || end > file.Length) return null;
        // The signed bytes must end a revision: %%EOF, then only line ends.
        int t = (int)end;
        while (t > 0 && file[t - 1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t' or 0) t--;
        if (t < 5 || !file.AsSpan(t - 5, 5).SequenceEqual("%%EOF"u8)) return null;
        long previous = RevisionEnds(file).Where(e => e <= t - 5).DefaultIfEmpty(0).Max();

        PdfVectorDocument doc;
        try
        {
            doc = await PdfVectorDocument.OpenAsync(file.AsSpan(0, (int)end).ToArray(), password: password, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
        using (doc)
        {
            var r = doc.Resolver;
            var entries = doc.XrefTable.Entries;
            long Offset(int number) =>
                !entries.TryGetValue(number, out var e) || !e.IsInUse ? -1
                : !e.IsCompressed ? e.ByteOffset
                : entries.TryGetValue(e.CompressedStreamObjectNumber, out var s) && s.IsInUse && !s.IsCompressed ? s.ByteOffset : -1;

            // The signature fields of the form, as the signer saw it, whose signature ends here.
            var root = r.Resolve(doc.XrefTable.Trailer?["Root"]) as PdfDictionary;
            var form = r.Resolve(root?["AcroForm"]) as PdfDictionary;
            var candidates = new List<(int Holder, PdfDictionary Field, int Value, PdfDictionary Sig, long[] Range)>();
            var holdersOfValue = new Dictionary<int, int>();
            var visited = new HashSet<int>();
            var stack = new Stack<(PdfObject Item, int Depth)>();
            if (r.Resolve(form?["Fields"]) is PdfArray fields) foreach (var f in fields) stack.Push((f, 0));
            while (stack.Count > 0)
            {
                var (item, depth) = stack.Pop();
                if (item is not PdfIndirectRef ir || depth > 32 || !visited.Add(ir.ObjectNumber) || visited.Count > 100_000) continue;
                if (r.Resolve(ir) is not PdfDictionary field) continue;
                if (field["V"] is PdfIndirectRef vr)
                {
                    holdersOfValue[vr.ObjectNumber] = holdersOfValue.GetValueOrDefault(vr.ObjectNumber) + 1;
                    if (r.Resolve(vr) is PdfDictionary sig && sig["ByteRange"] is PdfArray { Count: 4 } br && br.All(o => o is PdfInteger)
                        && br.Select(o => o.TryGetInteger(out long n) ? n : -1).ToArray() is { } range && range[2] + range[3] == end)
                        candidates.Add((ir.ObjectNumber, field, vr.ObjectNumber, sig, range));
                }
                if (r.Resolve(field["Kids"]) is PdfArray kids) foreach (var k in kids) stack.Push((k, depth + 1));
            }
            if (candidates.Count != 1) return null;
            var (holder, holderDict, value, sigDict, byteRange) = candidates[0];
            if (holdersOfValue.GetValueOrDefault(value) != 1) return null;
            // The value lies in this revision, inside its signature dictionary, which (with the field) this revision wrote.
            if (byteRange[0] != 0 || byteRange[1] >= byteRange[2] || byteRange[1] <= previous || sigDict["Contents"] is not PdfString contents) return null;
            if (!entries.TryGetValue(value, out var valueEntry) || !valueEntry.IsInUse || valueEntry.IsCompressed
                || valueEntry.ByteOffset <= previous || valueEntry.ByteOffset >= byteRange[1])
                return null;
            if (Offset(holder) <= previous) return null;
            // The gap holds this dictionary's /Contents (an encrypted file's strings are read decrypted, so there only the offsets are compared).
            if (doc.Security == null && (ContentsFromGap(file, byteRange[1], byteRange[2], trim: false) is not { } gap
                                         || !gap.AsSpan().SequenceEqual(contents.RawBytes.Span)))
                return null;
            cache.Hashed(byteRange[1] + byteRange[3], file.Length);
            if (!SignatureIntact(file, sigDict, byteRange, new WorkBudget(ct, long.MaxValue))) return null;

            var revision = new SigningRevision { Holder = holder, Value = value };
            void Add(int number) { if (!revision.Objects.ContainsKey(number)) revision.Objects[number] = new SignedObject(r.Resolve(number), r); }
            Add(holder);
            Add(value);
            // The widgets: the field itself, or its kids that are not fields; and everything their appearances refer to.
            var widgets = new List<(int Number, PdfDictionary Dict)>();
            if (r.Resolve(holderDict["Kids"]) is PdfArray widgetKids)
            {
                foreach (var k in widgetKids)
                    if (k is PdfIndirectRef kr && r.Resolve(kr) is PdfDictionary kd && kd["T"] == null) widgets.Add((kr.ObjectNumber, kd));
            }
            else widgets.Add((holder, holderDict));
            var seen = new HashSet<int>();
            foreach (var (number, dict) in widgets)
            {
                Add(number);
                var pending = new Stack<PdfObject>();
                if (dict["AP"] is { } ap) pending.Push(ap);
                while (pending.Count > 0)
                {
                    if (seen.Count > 100_000) return null;
                    switch (pending.Pop())
                    {
                        case PdfIndirectRef ar:
                            if (!seen.Add(ar.ObjectNumber)) continue;
                            Add(ar.ObjectNumber);
                            if (r.Resolve(ar) is { } resolved) pending.Push(resolved);
                            continue;
                        case PdfStream s: pending.Push(s.Dictionary); continue;
                        case PdfDictionary d:
                            foreach (var v in d.Entries.Values) if (v is PdfIndirectRef or PdfDictionary or PdfArray) pending.Push(v);
                            continue;
                        case PdfArray a:
                            foreach (var v in a) if (v is PdfIndirectRef or PdfDictionary or PdfArray) pending.Push(v);
                            continue;
                    }
                }
            }
            return revision;
        }
    }

    /// <summary>What identifies a signature value: its byte range and the hash of its /Contents.</summary>
    private static string SignatureKey(long[] range, PdfDictionary sig, Parsing.PdfObjectResolver r) =>
        string.Join(",", range) + ":" + (r.Resolve(sig["Contents"]) is PdfString s ? Convert.ToHexString(SHA256.HashData(s.RawBytes.Span)) : "-");

    /// <summary>
    /// Whether a signature dictionary added after the signed revision is a real, intact signature:
    /// its value (a CMS signature, or an RFC 3161 token for a document timestamp) verifies over its
    /// byte range. Trust in the signer is not needed here; the signature gets its own check.
    /// Hashing counts against the work budget.
    /// </summary>
    private static bool SignatureIntact(byte[] file, PdfDictionary sig, long[] range, WorkBudget budget)
    {
        try
        {
            budget.Charge(1 + (range[1] + range[3]) / (1 << 20)); // one step per megabyte hashed
            byte[] signedBytes = new byte[range[1] + range[3]];
            Array.Copy(file, 0, signedBytes, 0, range[1]);
            Array.Copy(file, range[2], signedBytes, range[1], range[3]);
            byte[]? blob = ContentsFromGap(file, range[1], range[2], trim: true);
            if (blob == null || blob.Length == 0) return false;
            if (IsDocTimestamp(sig))
                return System.Security.Cryptography.Pkcs.Rfc3161TimestampToken.TryDecode(blob, out var token, out _)
                       && token!.VerifySignatureForData(signedBytes, out _);
            var cms = new System.Security.Cryptography.Pkcs.SignedCms();
            cms.Decode(blob);
            if (cms.ContentInfo.Content.Length == 0)
            {
                cms = new System.Security.Cryptography.Pkcs.SignedCms(new System.Security.Cryptography.Pkcs.ContentInfo(signedBytes), detached: true);
                cms.Decode(blob);
            }
            if (cms.SignerInfos.Count == 0) return false;
            if (!DigestMatches(cms, cms.SignerInfos[0], signedBytes, sig.GetName("SubFilter") ?? string.Empty, out _)) return false;
            cms.CheckSignature(verifySignatureOnly: true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>How much reading the change analysis may do; past it, the changes are unverifiable.</summary>
    private sealed class WorkBudget(CancellationToken ct, long limit)
    {
        private long _steps;

        public void Step() => Charge(1);

        public void Charge(long steps)
        {
            long before = _steps;
            _steps += steps;
            if (_steps > limit) throw new InvalidOperationException("The document is too large to check for changes.");
            if ((before >> 10) != (_steps >> 10)) ct.ThrowIfCancellationRequested();
        }
    }

    // Where an object is used: one bit each. Owner bits: the annotation or field dictionary itself,
    // by category. Owned bits: what an annotation or field refers to, by category and by the key it
    // is reached through.
    private const int ContentBit = 0, OtherBit = 1, MetadataBit = 2, DssBit = 3, FormResourcesBit = 4,
        PageNodeBit = 5, AnnotsArrayBit = 6, FormBit = 7, CatalogBit = 8, OwnerBase = 9, OwnedBase = 16;

    /// <summary>What an annotation or field is, for what changing it (or what it owns) means.</summary>
    /// <remarks>
    /// <see cref="SignatureNew"/>: signed by a signature dictionary new since the signed revision.
    /// <see cref="SignatureEmpty"/>: a signature field with no signature: like any annotation.
    /// </remarks>
    private enum Category { Markup, Field, SignatureNew, SignatureSigned, Timestamp, SignatureEmpty }

    /// <summary>How an annotation or field refers to an object it owns.</summary>
    private enum Via { Appearance, Value, Action, Other }

    private const int Categories = 6, Vias = 4;

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
        public int HolderNumber = -1;         // the field dictionary holding /V (itself or a parent); -1 when direct
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
        /// <summary>The signature dictionaries held by signed fields of the field tree, direct fields included.</summary>
        public readonly HashSet<int> SignedValues = new();
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

        private readonly long _signedEnd;
        private readonly SignatureCheckCache? _signatures;
        /// <summary>Signature values, by object and by value (byte range and contents): how many annotations or fields hold each.</summary>
        private readonly Dictionary<int, int> _holdersByObject = new();
        private readonly Dictionary<string, int> _holdersByValue = new(StringComparer.Ordinal);
        /// <summary>The values (byte range and contents) of every signature this revision holds.</summary>
        public readonly HashSet<string> SignatureValueKeys = new(StringComparer.Ordinal);
        /// <summary>The form's default resources (/DR) and their category dictionaries, where they are their own objects.</summary>
        public readonly HashSet<int> ResourceDictionaries = new(), ResourceCategories = new();

        /// <param name="signedEnd">For the final revision's map: where the signed revision ends (a new signature must sign a later revision).</param>
        /// <param name="signatures">For the final revision's map: the file's signing revisions.</param>
        public UsageMap(PdfVectorDocument doc, UsageMap? before, WorkBudget budget, long signedEnd = 0, SignatureCheckCache? signatures = null)
        {
            Doc = doc;
            R = doc.Resolver;
            _before = before;
            _budget = budget;
            _signedEnd = signedEnd;
            _signatures = signatures;
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
                if (Form["DR"] is PdfIndirectRef drRef) ResourceDictionaries.Add(drRef.ObjectNumber);
                if (R.Resolve(Form["DR"]) is PdfDictionary dr)
                    foreach (var category in dr.Entries.Values)
                        if (category is PdfIndirectRef cr) ResourceCategories.Add(cr.ObjectNumber);
            }
            // Who holds each signature value: a signature counts as signing only for the one field that holds it.
            foreach (var owner in owners.Distinct())
            {
                if (!owner.IsSignature || owner.Value == null || owner.Dict["V"] == null) continue;
                if (owner.ValueNumber >= 0) _holdersByObject[owner.ValueNumber] = _holdersByObject.GetValueOrDefault(owner.ValueNumber) + 1;
                string key = ValueKey(owner.Value);
                _holdersByValue[key] = _holdersByValue.GetValueOrDefault(key) + 1;
                SignatureValueKeys.Add(key);
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
                    "DSS" or "Extensions" => DssBit, // developer extensions are updated with validation data
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
                if (owner is { IsSignature: true, Value: not null, ValueNumber: >= 0 }) SignedValues.Add(owner.ValueNumber);
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
            int nodeNumber = number;
            for (int depth = 0; node != null && depth < 32 && seen.Add(node); depth++)
            {
                type ??= node.GetName("FT");
                if (value == null && node["V"] is { } v)
                {
                    value = v;
                    owner.HolderNumber = nodeNumber; // the field that holds the value
                }
                if (node["T"] is PdfString t) names.Add(t.AsDecodedString());
                nodeNumber = node["Parent"] is PdfIndirectRef parent ? parent.ObjectNumber : -1;
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
        /// revision is part of what was signed; one with no signature is an annotation like any
        /// other; an invisible document timestamp field with no actions is a timestamp; a field
        /// signed since is signing.
        /// </summary>
        public Category CategoryOf(Owner owner)
        {
            if (!owner.IsField) return Category.Markup;
            if (!owner.IsSignature) return Category.Field;
            if (_before == null) return owner.Value != null ? Category.SignatureSigned : Category.SignatureEmpty;
            if (_before.WasSigned(owner)) return Category.SignatureSigned;
            // Signing is a field of the form (so the signature is listed and checked) holding a new
            // signature that is intact over a revision after the signed one. Anything else (no value,
            // a placeholder, a field outside the form) is an annotation like any other.
            if (owner.Value == null || owner.Number < 0 || !FieldNodes.Contains(owner.Number)) return Category.SignatureEmpty;
            // A copy of a signature the signed revision already held is not a new signature.
            string key = ValueKey(owner.Value);
            if (_before.SignatureValueKeys.Contains(key)) return Category.SignatureSigned;
            // The signature value must be this field's alone.
            if ((owner.ValueNumber >= 0 && _holdersByObject.GetValueOrDefault(owner.ValueNumber) != 1) || _holdersByValue.GetValueOrDefault(key) != 1)
                return Category.SignatureEmpty;
            // And the field must be the one its signature's own revision signed, as it was signed.
            if (!SignedInItsRevision(owner)) return Category.SignatureEmpty;
            return IsInvisibleTimestampField(owner.Dict, R) && owner.Dict["A"] == null && owner.Dict["AA"] == null
                ? Category.Timestamp
                : Category.SignatureNew;
        }

        private string ValueKey(PdfDictionary sig)
        {
            var br = (R.Resolve(sig["ByteRange"]) as PdfArray)?.Select(o => R.Resolve(o) is PdfObject v && v.TryGetInteger(out long n) ? n : -1).ToArray()
                     ?? Array.Empty<long>();
            return SignatureKey(br, sig, R);
        }

        /// <summary>
        /// Whether the field is the one its signature's own revision signed (<see cref="SigningRevision"/>),
        /// that revision comes after the signed one, and the field, its widgets, its signature
        /// dictionary and its appearance are still what that signature signed (compared by content,
        /// so an unchanged rewrite elsewhere in the file is no change).
        /// </summary>
        private bool SignedInItsRevision(Owner owner)
        {
            if (_signatures == null || owner.Value == null || owner.HolderNumber < 0) return false;
            if (owner.Value["ByteRange"] is not PdfArray { Count: 4 } br || !br[2].TryGetInteger(out long b2) || !br[3].TryGetInteger(out long b3)) return false;
            long end = b2 + b3;
            if (end <= _signedEnd || !_signatures.Revisions.TryGetValue(end, out var revision) || revision == null) return false;
            if (revision.Holder != owner.HolderNumber || revision.Value != owner.ValueNumber || !revision.Objects.ContainsKey(owner.Number)) return false;
            foreach (var (number, signed) in revision.Objects)
            {
                _budget.Step();
                if (!InUse(number) || !signed.Matches(R.Resolve(number), R)) return false;
            }
            return true;
        }

        /// <summary>Whether this (signed) revision has the field, or the signature value it holds, signed.</summary>
        public bool WasSigned(Owner owner)
        {
            if (owner.Number >= 0 && Owners.TryGetValue(owner.Number, out var old) && old.IsSignature && old.Value != null) return true;
            return owner.ValueNumber >= 0 && InUse(owner.ValueNumber) && R.Resolve(owner.ValueNumber) is PdfDictionary;
        }

        /// <summary>Whether an object can refer to another: references, dictionaries, arrays and streams.</summary>
        private static bool Refers(PdfObject o) => o is PdfIndirectRef or PdfDictionary or PdfArray or PdfStream;

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
                            if (Refers(value)) stack.Push((value, nm, -1));
                        }
                        continue;
                    }
                    case PdfArray a:
                        // Numbers, names and strings refer to nothing: not pushed, not counted (a font's /W can hold thousands).
                        foreach (var item in a)
                            if (Refers(item)) stack.Push((item, nm, -1));
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
            // The same for signed fields written directly in a parent's /Kids: each signature must still be held by a field.
            if (!before.SignedValues.IsSubsetOf(after.SignedValues)) return Report(changes | PdfLaterChanges.Other);

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
            // Default resources that are new change no field by themselves: they are added by a change
            // to the dictionary that holds them. Resources that gained entries change the fields whose
            // /DA names one of the entries (FieldMDP). Anything else could change any field.
            // Only /DR itself and its category dictionaries (/Font, /XObject...) may gain entries that
            // way: a change inside a resource (a font's /Encoding, say) can change any field using it.
            if (Has(FormResourcesBit))
            {
                int depth = after.ResourceDictionaries.Contains(number) || before.ResourceDictionaries.Contains(number) ? 1
                    : after.ResourceCategories.Contains(number) || before.ResourceCategories.Contains(number) ? 0 : -1;
                changes |= was == null ? PdfLaterChanges.FormFields
                    : depth >= 0 && was is PdfDictionary oldResources && now is PdfDictionary newResources && AddsOnly(oldResources, newResources, 0, depth)
                        ? ResourcesAdded(oldResources, newResources)
                        : Unnamed();
            }
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
            // An empty signature field signed since is judged by what it is now (signing, or a timestamp).
            if (category == Category.SignatureEmpty && now != null
                && after.Owners.GetValueOrDefault(number)?.Category is Category.SignatureNew or Category.Timestamp)
                return PdfLaterChanges.None;
            if (category == Category.Field) Name(owner?.FieldName);
            if (now == null) return PdfLaterChanges.Annotations; // removed
            if (now is not PdfDictionary newDict) return PdfLaterChanges.Other;
            if (was == null)
                return category switch
                {
                    Category.Timestamp => PdfLaterChanges.DocumentTimestamp,
                    Category.SignatureNew => newDict["A"] != null || newDict["AA"] != null ? PdfLaterChanges.Other : PdfLaterChanges.Signatures,
                    _ => PdfLaterChanges.Annotations, // a new field, or an empty signature field, is not filling in or signing
                };
            if (was is not PdfDictionary oldDict) return PdfLaterChanges.Other;
            var changed = oldDict.Entries.Keys.Union(newDict.Entries.Keys).Where(k => !Same(oldDict[k], newDict[k], null, null, 0)).ToList();
            // Actions change what the document does, whatever annotation holds them.
            if (changed.Contains("AA") || changed.Contains("A")) return PdfLaterChanges.Other;
            if (category is Category.Markup or Category.SignatureEmpty) return PdfLaterChanges.Annotations;
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
            // An action of a new annotation is part of it; a changed action changes what the document does.
            if (via == Via.Action)
                return category is Category.Markup or Category.SignatureEmpty && was == null ? PdfLaterChanges.Annotations : PdfLaterChanges.Other;
            if (category == Category.Field)
            {
                Name(after.FieldNames.TryGetValue(number, out var n) ? n : before.FieldNames.GetValueOrDefault(number));
                return via is Via.Appearance or Via.Value ? PdfLaterChanges.FormFields : PdfLaterChanges.Annotations;
            }
            return (category, via) switch
            {
                (Category.Markup or Category.SignatureEmpty, _) => PdfLaterChanges.Annotations,
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

        /// <summary>
        /// The catalog, compared entry by entry: only the form, the DSS, the metadata and the pages'
        /// annotations may change, and, as validation data updates do, the developer extensions
        /// (/Extensions) and a higher /Version.
        /// </summary>
        private PdfLaterChanges CatalogChange(PdfDictionary was, PdfDictionary now)
        {
            if (!SameExcept(was, now, "AcroForm", "DSS", "Metadata", "Pages", "Extensions", "Version")) return PdfLaterChanges.Other;
            var changes = PdfLaterChanges.None;
            if (!Same(was["Extensions"], now["Extensions"], null, null, 0)) changes |= PdfLaterChanges.ValidationData;
            if (!Same(was["Version"], now["Version"], null, null, 0))
            {
                static decimal? Version(PdfObject? o) =>
                    o is PdfName n && decimal.TryParse(n.Value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
                var oldVersion = Version(was["Version"]);
                var newVersion = Version(now["Version"]);
                bool raised = newVersion != null && (was["Version"] == null || (oldVersion != null && newVersion > oldVersion));
                changes |= raised ? PdfLaterChanges.ValidationData : PdfLaterChanges.Other;
            }
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
                    // New default resources change only the fields whose /DA names one of them (a viewer
                    // makes their appearance from /V, /DA and /DR). /NeedAppearances makes every field's.
                    "DR" when AddsOnly(before.R.Resolve(was[key]) as PdfDictionary, after.R.Resolve(now[key]) as PdfDictionary, 0, 1)
                        => ResourcesAdded(before.R.Resolve(was[key]) as PdfDictionary, after.R.Resolve(now[key]) as PdfDictionary),
                    "NeedAppearances" or "DR" or "DA" or "Q" or "CO" => Unnamed(),
                    _ => PdfLaterChanges.Other, // /XFA among them
                };
            }
            return changes;
        }

        /// <summary>
        /// Default resources that only gained entries: the fields whose /DA (their own, inherited, or
        /// the form's) names an added entry may now look different, so they count as changed.
        /// </summary>
        private PdfLaterChanges ResourcesAdded(PdfDictionary? was, PdfDictionary? now)
        {
            var added = new HashSet<string>(StringComparer.Ordinal);
            AddedNames(was, now, added, 0);
            if (added.Count == 0) return PdfLaterChanges.FormFields;
            string? formDa = after.R.Resolve(after.Form?["DA"]) is PdfString fda ? fda.AsDecodedString() : null;
            foreach (var (number, owner) in after.Owners)
            {
                budget.Step();
                if (!owner.IsField || owner.IsSignature || !after.FieldNodes.Contains(number)) continue;
                string? da = null;
                var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                for (var node = owner.Dict; node != null && da == null && seen.Count < 32 && seen.Add(node); node = after.R.Resolve(node["Parent"]) as PdfDictionary)
                    if (after.R.Resolve(node["DA"]) is PdfString s) da = s.AsDecodedString();
                da ??= formDa;
                if (da == null) continue;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(da, @"/([^\s/\[\]()<>{}%]+)"))
                    if (added.Contains(m.Groups[1].Value)) { Name(owner.FieldName); break; }
            }
            return PdfLaterChanges.FormFields;
        }

        /// <summary>The names of entries <paramref name="now"/> has that <paramref name="was"/> did not, at any (direct or resolved) dictionary level.</summary>
        private void AddedNames(PdfDictionary? was, PdfDictionary? now, HashSet<string> added, int depth)
        {
            if (now == null || depth > 4) return;
            foreach (var (key, value) in now.Entries)
            {
                budget.Step();
                var old = was?[key];
                if (old == null)
                {
                    added.Add(key);
                    if (after.R.Resolve(value) is PdfDictionary sub) AddedNames(null, sub, added, depth + 1);
                }
                else if (old is PdfDictionary oldSub && value is PdfDictionary newSub) AddedNames(oldSub, newSub, added, depth + 1);
            }
        }

        /// <summary>Whether <paramref name="now"/> only adds entries to <paramref name="was"/> (directly nested dictionaries likewise).</summary>
        /// <param name="maxDepth">How many levels of directly nested dictionaries may also only gain entries (1: /DR and its categories; 0: a category).</param>
        private bool AddsOnly(PdfDictionary? was, PdfDictionary? now, int depth, int maxDepth)
        {
            if (now == null || depth > maxDepth) return false;
            if (was == null) return true;
            foreach (var (key, value) in was.Entries)
            {
                budget.Step();
                if (Same(value, now[key], null, null, 0)) continue;
                if (value is PdfDictionary oldSub && now[key] is PdfDictionary newSub && AddsOnly(oldSub, newSub, depth + 1, maxDepth)) continue;
                return false;
            }
            return true;
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
                    if (compareDirect && oldDirect.Any(o => { budget.Step(); return Same(o, item, null, null, 0); })) continue;
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
                    if (compareDirect && newDirect.Any(n => { budget.Step(); return Same(item, n, null, null, 0); })) continue;
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
