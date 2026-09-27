using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Signatures;

/// <summary>What to sign with and where the signature goes.</summary>
public sealed record PdfSignatureRequest
{
    /// <summary>An existing, unsigned signature field to fill; null creates a new field.</summary>
    public string? FieldName { get; init; }
    /// <summary>Page of a new field (1-based).</summary>
    public int PageNumber { get; init; } = 1;
    /// <summary>Rectangle of a new field in default user space; null makes the signature invisible.</summary>
    public PdfRect? Rect { get; init; }
    public required string SignerName { get; init; }
    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }
    public DateTimeOffset SigningTime { get; init; } = DateTimeOffset.Now;
    /// <summary>
    /// Certify rather than approve (a DocMDP signature, ISO 32000-2 12.8.2.2): the changes later
    /// revisions may make. 1 none, 2 form filling and signing, 3 also annotations. Only the first
    /// signature in a document can certify it.
    /// </summary>
    public int? CertificationLevel { get; init; }
    /// <summary>Bytes reserved for the CMS signature (hex doubles it in the file). Timestamps and long chains need more.</summary>
    public int ContentsSize { get; init; } = 16384;
    /// <summary>What a visible signature shows (a handwritten or picture signature, which details, the font); null shows the details as text.</summary>
    public PdfSignatureAppearance? Appearance { get; init; }
}

/// <summary>
/// A document with a signature placeholder appended as an incremental update: /Contents reserved
/// and /ByteRange filled in. <see cref="SignedBytes"/> is what the signature covers (everything
/// but the /Contents hex string); <see cref="Complete"/> writes the signature into the placeholder.
/// </summary>
public sealed class PdfPreparedSignature
{
    private readonly byte[] _bytes;
    private readonly long _contentsStart; // first hex digit, after '<'
    private readonly int _contentsHexLength;

    internal PdfPreparedSignature(byte[] bytes, long contentsStart, int contentsHexLength, long[] byteRange)
    {
        _bytes = bytes;
        _contentsStart = contentsStart;
        _contentsHexLength = contentsHexLength;
        ByteRange = byteRange;
    }

    /// <summary>[offset1 length1 offset2 length2], as written in the signature dictionary.</summary>
    public IReadOnlyList<long> ByteRange { get; }

    /// <summary>The bytes the signature covers.</summary>
    public byte[] SignedBytes()
    {
        var data = new byte[ByteRange[1] + ByteRange[3]];
        Array.Copy(_bytes, ByteRange[0], data, 0, ByteRange[1]);
        Array.Copy(_bytes, ByteRange[2], data, ByteRange[1], ByteRange[3]);
        return data;
    }

    /// <summary>The signed document: <paramref name="signature"/> (DER CMS) written into /Contents.</summary>
    public byte[] Complete(byte[] signature)
    {
        string hex = Convert.ToHexString(signature);
        if (hex.Length > _contentsHexLength)
            throw new InvalidOperationException($"The signature ({signature.Length} bytes) does not fit the {_contentsHexLength / 2} bytes reserved for it.");
        var result = (byte[])_bytes.Clone();
        Encoding.ASCII.GetBytes(hex, 0, hex.Length, result, (int)_contentsStart);
        return result;
    }
}

/// <summary>
/// Adds a digital signature to a PDF as an incremental update (ISO 32000-2 12.8): a signature
/// dictionary (PAdES: /SubFilter /ETSI.CAdES.detached), a new or existing signature field with its
/// widget (an appearance when visible), and the field in /AcroForm with /SigFlags 3. Earlier
/// revisions stay byte-for-byte as they were, so signatures already on them stay valid.
/// </summary>
public static class PdfSigner
{
    private const string ByteRangePlaceholder = "[0 9999999999 9999999999 9999999999]";

    public static PdfPreparedSignature Prepare(PdfVectorDocument document, byte[] original, PdfSignatureRequest request)
    {
        if (request.ContentsSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "At least 1 KB must be reserved for the signature.");

        // The signature value.
        var sig = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Sig"),
            ["Filter"] = new PdfName("Adobe.PPKLite"),
            ["SubFilter"] = new PdfName("ETSI.CAdES.detached"),
            ["M"] = PdfObjectWriter.TextString(PdfDate(request.SigningTime)),
            ["Name"] = PdfObjectWriter.TextString(request.SignerName),
        };
        if (!string.IsNullOrWhiteSpace(request.Reason)) sig["Reason"] = PdfObjectWriter.TextString(request.Reason!);
        if (!string.IsNullOrWhiteSpace(request.Location)) sig["Location"] = PdfObjectWriter.TextString(request.Location!);
        if (!string.IsNullOrWhiteSpace(request.ContactInfo)) sig["ContactInfo"] = PdfObjectWriter.TextString(request.ContactInfo!);
        if (request.CertificationLevel is int level)
        {
            if (level is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(request), "A certification level is 1, 2 or 3.");
            if (PdfAcroForm.Read(document)?.Fields.Any(f => f.Kind == PdfFormFieldKind.Signature && f.IsSigned) == true)
                throw new InvalidOperationException("The document is already signed; only the first signature can certify it.");
            sig["Reference"] = new PdfArray(new PdfObject[]
            {
                new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Type"] = new PdfName("SigRef"),
                    ["TransformMethod"] = new PdfName("DocMDP"),
                    ["TransformParams"] = new PdfDictionary(new Dictionary<string, PdfObject>
                    {
                        ["Type"] = new PdfName("TransformParams"),
                        ["P"] = new PdfInteger(level),
                        ["V"] = new PdfName("1.2"),
                    }),
                }),
            });
        }
        return Place(document, original, sig, request, "Signature");
    }

    /// <summary>
    /// A document timestamp placeholder (ISO 32000-2 12.8.5, PAdES-B-LTA): an invisible field whose
    /// value is a /Type /DocTimeStamp dictionary with /SubFilter /ETSI.RFC3161. Its /Contents is to
    /// be the RFC 3161 timestamp token over <see cref="PdfPreparedSignature.SignedBytes"/>.
    /// </summary>
    public static PdfPreparedSignature PrepareDocumentTimestamp(PdfVectorDocument document, byte[] original, int contentsSize = 16384)
    {
        if (contentsSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(contentsSize), "At least 1 KB must be reserved for the timestamp.");
        var sig = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("DocTimeStamp"),
            ["Filter"] = new PdfName("Adobe.PPKLite"),
            ["SubFilter"] = new PdfName("ETSI.RFC3161"),
        };
        return Place(document, original, sig, new PdfSignatureRequest { SignerName = string.Empty, ContentsSize = contentsSize }, "DocTimeStamp");
    }

    /// <summary>Appends the signature dictionary, its field and widget, and the form entries, with the placeholders.</summary>
    private static PdfPreparedSignature Place(PdfVectorDocument document, byte[] original, Dictionary<string, PdfObject> sig,
        PdfSignatureRequest request, string namePrefix)
    {
        var r = document.Resolver;
        var trailer = document.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (trailer["Root"] is not PdfIndirectRef rootRef || r.Resolve(rootRef) is not PdfDictionary root)
            throw new InvalidOperationException("The document has no catalog.");

        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(document);
        int Allocate() => next++;
        PdfDictionary Current(int number) =>
            objects.TryGetValue(number, out var o) ? (PdfDictionary)o : r.Resolve(number) as PdfDictionary
            ?? throw new InvalidOperationException($"Object {number} is not a dictionary.");

        int sigNumber = next++;
        // Placeholders: patched in place once the file is laid out (same length, so no offset moves).
        sig["ByteRange"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(9999999999), new PdfInteger(9999999999), new PdfInteger(9999999999) });
        sig["Contents"] = new PdfString(new byte[request.ContentsSize], IsHex: true);
        objects[sigNumber] = new PdfDictionary(sig);

        // The form: /Root /AcroForm, inline or indirect.
        var acroObj = root["AcroForm"];
        int? acroNumber = acroObj is PdfIndirectRef ar ? ar.ObjectNumber : null;
        var acro = new Dictionary<string, PdfObject>((r.Resolve(acroObj) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>());
        var fields = (r.Resolve(acro.GetValueOrDefault("Fields")) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
        acro["SigFlags"] = new PdfInteger(3); // SignaturesExist | AppendOnly

        if (request.FieldName != null)
        {
            var form = PdfAcroForm.Read(document) ?? throw new InvalidOperationException("The document has no form.");
            var field = form[request.FieldName] ?? throw new KeyNotFoundException($"No field named \"{request.FieldName}\".");
            if (field.Kind != PdfFormFieldKind.Signature)
                throw new InvalidOperationException($"\"{request.FieldName}\" is not a signature field.");
            var fieldDict = Current(field.ObjectNumber);
            if (r.Resolve(fieldDict["V"]) is PdfDictionary)
                throw new InvalidOperationException($"\"{request.FieldName}\" is already signed.");
            objects[field.ObjectNumber] = With(fieldDict, "V", new PdfIndirectRef(sigNumber));
            foreach (var w in field.Widgets)
            {
                var widget = Current(w.ObjectNumber);
                if (w.Rect.Width > 0 && w.Rect.Height > 0)
                {
                    int apNumber = next++;
                    objects[apNumber] = PdfSignatureAppearanceBuilder.Build(request, w.Rect.Width, w.Rect.Height, w.Rotation, r, Allocate, objects);
                    widget = With(widget, "AP", new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(apNumber) }));
                }
                objects[w.ObjectNumber] = widget;
            }
        }
        else
        {
            var pageRefs = PageObjectNumbers(root, r);
            if (request.PageNumber < 1 || request.PageNumber > pageRefs.Count)
                throw new ArgumentOutOfRangeException(nameof(request), $"The document has no page {request.PageNumber}.");
            int pageNumber = pageRefs[request.PageNumber - 1];
            var page = document.PageTree.Pages[request.PageNumber - 1];

            string name = UniqueName(PdfAcroForm.Read(document), namePrefix);
            var rect = request.Rect ?? new PdfRect(0, 0, 0, 0);
            int fieldNumber = next++;
            var field = new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"),
                ["Subtype"] = new PdfName("Widget"),
                ["FT"] = new PdfName("Sig"),
                ["T"] = PdfObjectWriter.TextString(name),
                ["V"] = new PdfIndirectRef(sigNumber),
                ["F"] = new PdfInteger(request.Rect == null ? 2 | 128 : 4 | 128), // hidden or print; locked
                ["P"] = new PdfIndirectRef(pageNumber),
                ["Rect"] = new PdfArray(new PdfObject[] { Num(rect.X), Num(rect.Y), Num(rect.X + rect.Width), Num(rect.Y + rect.Height) }),
            };
            if (request.Rect is { Width: > 0, Height: > 0 } visible)
            {
                int apNumber = next++;
                objects[apNumber] = PdfSignatureAppearanceBuilder.Build(request, visible.Width, visible.Height, page.RotationDegrees, r, Allocate, objects);
                field["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(apNumber) });
                if (page.RotationDegrees != 0)
                    field["MK"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["R"] = new PdfInteger(page.RotationDegrees) });
            }
            objects[fieldNumber] = new PdfDictionary(field);
            fields.Add(new PdfIndirectRef(fieldNumber));

            // The page's /Annots, inline or indirect.
            var pageDict = Current(pageNumber);
            var annotsObj = pageDict["Annots"];
            var annots = (r.Resolve(annotsObj) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
            annots.Add(new PdfIndirectRef(fieldNumber));
            if (annotsObj is PdfIndirectRef annotsRef)
                objects[annotsRef.ObjectNumber] = new PdfArray(annots);
            else
                objects[pageNumber] = With(pageDict, "Annots", new PdfArray(annots));
        }

        acro["Fields"] = new PdfArray(fields);
        if (acroNumber is int an)
            objects[an] = new PdfDictionary(acro);
        else
            objects[rootRef.ObjectNumber] = With(Current(rootRef.ObjectNumber), "AcroForm", new PdfDictionary(acro));
        if (request.CertificationLevel != null)
        {
            // /Perms /DocMDP points at the certifying signature.
            var catalog = Current(rootRef.ObjectNumber);
            var perms = new Dictionary<string, PdfObject>((r.Resolve(catalog["Perms"]) as PdfDictionary)?.Entries ?? new Dictionary<string, PdfObject>())
            {
                ["DocMDP"] = new PdfIndirectRef(sigNumber),
            };
            objects[rootRef.ObjectNumber] = With(catalog, "Perms", new PdfDictionary(perms));
        }

        byte[] bytes = PdfIncrementalWriter.Append(original, document, objects);
        // Search from the signature object itself, so nothing else in the update can be mistaken for it.
        int sigAt = IndexOf(bytes, Encoding.ASCII.GetBytes($"{sigNumber} 0 obj\n"), original.Length);
        return Layout(bytes, sigAt < 0 ? original.Length : sigAt, request.ContentsSize);
    }

    /// <summary>Finds the placeholders in the appended section and fills in /ByteRange (same width, so nothing moves).</summary>
    private static PdfPreparedSignature Layout(byte[] bytes, int appendedFrom, int contentsSize)
    {
        byte[] rangeKey = Encoding.ASCII.GetBytes("/ByteRange " + ByteRangePlaceholder);
        int rangeAt = IndexOf(bytes, rangeKey, appendedFrom);
        byte[] contentsKey = Encoding.ASCII.GetBytes("/Contents <");
        int contentsAt = IndexOf(bytes, contentsKey, appendedFrom);
        if (rangeAt < 0 || contentsAt < 0)
            throw new InvalidOperationException("The signature placeholder was not written.");
        long hexStart = contentsAt + contentsKey.Length;
        int hexLength = contentsSize * 2;
        if (hexStart + hexLength >= bytes.Length || bytes[hexStart + hexLength] != (byte)'>')
            throw new InvalidOperationException("The signature placeholder is malformed.");

        // The signature covers everything except the hex string with its angle brackets.
        long gapStart = hexStart - 1, gapEnd = hexStart + hexLength + 1;
        long[] range = { 0, gapStart, gapEnd, bytes.Length - gapEnd };
        string written = string.Create(CultureInfo.InvariantCulture, $"[{range[0]} {range[1]} {range[2]} {range[3]}]");
        if (written.Length > ByteRangePlaceholder.Length)
            throw new InvalidOperationException("The document is too large to sign.");
        written = written.PadRight(ByteRangePlaceholder.Length);
        int arrayAt = rangeAt + "/ByteRange ".Length;
        Encoding.ASCII.GetBytes(written, 0, written.Length, bytes, arrayAt);
        return new PdfPreparedSignature(bytes, hexStart, hexLength, range);
    }

    private static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        int i = data.AsSpan(from).IndexOf(pattern);
        return i < 0 ? -1 : from + i;
    }

    private static List<int> PageObjectNumbers(PdfDictionary root, Parsing.PdfObjectResolver r)
    {
        var result = new List<int>();
        var visited = new HashSet<int>();
        void Walk(PdfObject? node, int depth)
        {
            if (depth > 64 || node is not PdfIndirectRef nr || !visited.Add(nr.ObjectNumber)) return;
            if (r.Resolve(nr) is not PdfDictionary d) return;
            if (d.GetName("Type") == "Pages" || d["Kids"] != null && d.GetName("Type") != "Page")
            {
                if (r.Resolve(d["Kids"]) is PdfArray kids)
                    foreach (var k in kids) Walk(k, depth + 1);
            }
            else result.Add(nr.ObjectNumber);
        }
        Walk(root["Pages"], 0);
        return result;
    }

    private static string UniqueName(PdfAcroForm? form, string prefix)
    {
        var taken = new HashSet<string>(form?.Fields.Select(f => f.FullName) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        for (int i = 1; ; i++)
            if (!taken.Contains(prefix + i)) return prefix + i;
    }

    /// <summary>A PDF date string (7.9.4): D:YYYYMMDDHHmmSS+HH'mm'.</summary>
    internal static string PdfDate(DateTimeOffset t)
    {
        var off = t.Offset;
        string zone = off == TimeSpan.Zero ? "Z" : $"{(off < TimeSpan.Zero ? '-' : '+')}{Math.Abs(off.Hours):00}'{Math.Abs(off.Minutes):00}'";
        return "D:" + t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }

    private static PdfDictionary With(PdfDictionary d, string key, PdfObject value) =>
        new(new Dictionary<string, PdfObject>(d.Entries) { [key] = value });

    private static PdfObject Num(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e12 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(v);

    private static string F(double v) => PdfObjectWriter.FormatReal(Math.Round(v, 4));
}
