using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// What a revision after a signature changed is decided by where each changed object is used in
/// the document (page content, form, annotations, metadata, validation data), never by what the
/// object says it is. Changes a signature does not permit are reported; permitted ones still are.
/// </summary>
public partial class SignatureChangeClassificationTests
{
    private const int Dangling = 900;

    private static X509Certificate2 Cert()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    private static async Task<byte[]> Sign(byte[] pdf, int? level)
    {
        using var cert = Cert();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Signer", CertificationLevel = level });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
    }

    private static PdfArray Nums(params double[] v) => new(v.Select(x => (PdfObject)new PdfReal(x)).ToArray());

    private static int PageObject(PdfVectorDocument doc)
    {
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        var pages = (PdfDictionary)doc.Resolver.Resolve(root["Pages"])!;
        return ((PdfIndirectRef)((PdfArray)pages["Kids"]!)[0]).ObjectNumber;
    }

    /// <summary>The attacker's prepared file: page 1 draws XObject /Sh = object 900, which does not exist (draws nothing).</summary>
    private static async Task<byte[]> PreparedWithDanglingXObject()
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        var res = new Dictionary<string, PdfObject>(((PdfDictionary)doc.Resolver.Resolve(page.Dictionary["Resources"])!).Entries)
        {
            ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Sh"] = new PdfIndirectRef(Dangling) }),
        };
        var contents = new PdfArray(new PdfObject[] { page.Dictionary["Contents"]!, new PdfIndirectRef(next) });
        return PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [next] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("q /Sh Do Q")),
            [pageNo] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Resources"] = new PdfDictionary(res), ["Contents"] = contents }),
        });
    }

    private static PdfStream Forged() => PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
    {
        ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = Nums(0, 0, 300, 400),
    }, Encoding.Latin1.GetBytes("1 1 1 rg 0 0 300 400 re f 1 0 0 rg 20 20 260 360 re f"));

    /// <summary>After signing: define object 900 and add a stamp whose appearance's /Resources reference it.</summary>
    private static async Task<byte[]> FillDanglingThroughStamp(byte[] signed, bool direct, bool define = true)
    {
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        int ap = next, annot = next + 1;
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var annots = (doc.Resolver.Resolve(page.Dictionary["Annots"]) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
        annots.Add(new PdfIndirectRef(annot));
        var objects = new Dictionary<int, PdfObject>
        {
            [Dangling] = Forged(),
            [annot] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Stamp"), ["Name"] = new PdfName("Approved"),
                ["Rect"] = Nums(200, 20, 210, 30),
                ["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(direct ? Dangling : ap) }),
            }),
            [pageNo] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Annots"] = new PdfArray(annots) }),
        };
        if (!define) objects.Remove(Dangling);
        if (!direct)
            objects[ap] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = Nums(0, 0, 10, 10),
                // Uses /X nowhere: the reference alone makes object 900 an "appearance part".
                ["Resources"] = new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["X"] = new PdfIndirectRef(Dangling) }),
                }),
            }, Encoding.Latin1.GetBytes("0 0 1 rg 0 0 10 10 re f"));
        return PdfIncrementalWriter.Append(signed, doc, objects);
    }

    // A new object reached from a stamp's appearance /Resources fills a dangling reference of the signed page.
    [Theory]
    [InlineData(3)]
    [InlineData(null)] // approval signature
    public async Task DanglingReference_FilledViaAppearanceResources_IsNotPermitted(int? level)
    {
        byte[] signed = await Sign(await PreparedWithDanglingXObject(), level);
        byte[] attacked = await FillDanglingThroughStamp(signed, direct: false);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(attacked));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    [Fact]
    public async Task DanglingReference_Attack_ChangesWhatThePageDraws()
    {
        byte[] signed = await Sign(await PreparedWithDanglingXObject(), 3);
        byte[] attacked = await FillDanglingThroughStamp(signed, direct: false);
        byte[] stampOnly = await FillDanglingThroughStamp(signed, direct: false, define: false);
        async Task<int> Count(byte[] b) { using var d = await PdfVectorDocument.OpenAsync(b); return (await d.GetPageDisplayListAsync(1)).Commands.Count; }
        // The same stamp, with and without object 900: defining it makes the signed page itself draw more.
        Assert.True(await Count(attacked) > await Count(stampOnly));
    }

    // The same dangling object made the appearance /N itself.
    [Fact]
    public async Task DanglingReference_FilledAsTheAppearanceItself_IsNotPermitted()
    {
        byte[] signed = await Sign(await PreparedWithDanglingXObject(), 3);
        byte[] attacked = await FillDanglingThroughStamp(signed, direct: true);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(attacked));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // An object's own /Type, /Subtype or /FT does not decide what it is.
    [Theory]
    [InlineData("Type", "XRef", 1)]
    [InlineData("Type", "ObjStm", 1)]
    [InlineData("Subtype", "XML", 2)]
    [InlineData("FT", "Tx", 2)]
    [InlineData("Type", "Annot", 3)]
    public async Task PageContentRelabelled_IsNotPermitted(string key, string value, int level)
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), level);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        int contents = ((PdfIndirectRef)doc.PageTree.Pages[0].Dictionary["Contents"]!).ObjectNumber;
        byte[] forged = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [contents] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { [key] = new PdfName(value) },
                Encoding.Latin1.GetBytes("BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET")),
        });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // The page's own content stream made a new annotation's /AP /N, then rewritten.
    [Fact]
    public async Task PageContentAlsoUsedAsAnAppearance_IsNotPermitted()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 3);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        int contents = ((PdfIndirectRef)page.Dictionary["Contents"]!).ObjectNumber;
        int annot = PdfIncrementalWriter.NextObjectNumber(doc);
        var annots = (doc.Resolver.Resolve(page.Dictionary["Annots"]) as PdfArray)!.Items.ToList();
        annots.Add(new PdfIndirectRef(annot));
        byte[] forged = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [contents] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET")),
            [annot] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Square"), ["Rect"] = Nums(0, 0, 0, 0), ["F"] = new PdfInteger(2),
                ["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(contents) }),
            }),
            [pageNo] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Annots"] = new PdfArray(annots) }),
        });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // Freeing a signed object (an xref 'f' entry) is a change.
    [Fact]
    public async Task FreeingASignedObject_IsNotPermitted()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 1);
        int contents;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
            contents = ((PdfIndirectRef)doc.PageTree.Pages[0].Dictionary["Contents"]!).ObjectNumber;
        string text = Encoding.Latin1.GetString(signed);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text[(sx + 9)..].Trim().Split('\n', '\r')[0].Trim());
        int tr = text.LastIndexOf("trailer", StringComparison.Ordinal);
        int rootAt = text.IndexOf("/Root", tr, StringComparison.Ordinal);
        string root = System.Text.RegularExpressions.Regex.Match(text[rootAt..], @"/Root\s+(\d+ \d+ R)").Groups[1].Value;
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(text[tr..], @"/Size\s+(\d+)").Groups[1].Value);
        var sb = new StringBuilder(text);
        if (!text.EndsWith('\n')) sb.Append('\n');
        long xref = sb.Length;
        sb.Append($"xref\n{contents} 1\n0000000000 00001 f \ntrailer\n<< /Size {size} /Root {root} /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        byte[] forged = Encoding.Latin1.GetBytes(sb.ToString());
        using (var after = await PdfVectorDocument.OpenAsync(forged))
            Assert.False(after.XrefTable.Entries[contents].IsInUse); // the parser sees the object as freed
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    [Fact]
    public async Task PageContentGivenAByteRange_IsNotPermitted()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 1);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        int contents = ((PdfIndirectRef)doc.PageTree.Pages[0].Dictionary["Contents"]!).ObjectNumber;
        byte[] forged = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [contents] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["ByteRange"] = Nums(0, 0, 0, 0) },
                Encoding.Latin1.GetBytes("BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET")),
        });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // The certification itself (the catalog's /Perms) is part of what was signed.
    [Fact]
    public async Task RemovingTheCertification_IsNotPermitted()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 1);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        int rootNo = ((PdfIndirectRef)doc.XrefTable.Trailer!["Root"]!).ObjectNumber;
        var root = (PdfDictionary)doc.Resolver.Resolve(rootNo)!;
        var entries = new Dictionary<string, PdfObject>(root.Entries);
        entries.Remove("Perms");
        byte[] forged = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject> { [rootNo] = new PdfDictionary(entries) });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // ---- Objects stored in object streams ----

    private static string Serialize(PdfObject o)
    {
        using var ms = new System.IO.MemoryStream();
        PdfObjectWriter.Write(ms, o);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>An object stream (7.5.7) holding the given objects, as written in the file.</summary>
    private static string ObjectStream(params (int Number, string Body)[] items)
    {
        var header = new StringBuilder();
        var bodies = new StringBuilder();
        foreach (var (number, body) in items)
        {
            header.Append($"{number} {bodies.Length} ");
            bodies.Append(body).Append('\n');
        }
        string h = header.ToString();
        return $"<< /Type /ObjStm /N {items.Length} /First {h.Length} /Length {h.Length + bodies.Length} >>\nstream\n{h}{bodies}\nendstream";
    }

    /// <summary>
    /// An incremental update with a cross-reference stream (7.5.8): <paramref name="objects"/>
    /// written in full, <paramref name="compressed"/> listed as stored in an object stream.
    /// </summary>
    private static byte[] AppendWithXrefStream(byte[] pdf, PdfVectorDocument doc, IReadOnlyList<(int Number, string Body)> objects,
        IReadOnlyList<(int Number, int Stream, int Index)> compressed)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text[(sx + 9)..].Trim().Split('\n', '\r')[0].Trim());
        var ms = new System.IO.MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        ms.Write(pdf);
        if (pdf[^1] != '\n') W("\n");
        var rows = new SortedDictionary<int, (int Type, long Field2, int Field3)>();
        foreach (var (number, body) in objects)
        {
            rows[number] = (1, ms.Position, 0);
            W($"{number} 0 obj\n{body}\nendobj\n");
        }
        foreach (var (number, stream, at) in compressed) rows[number] = (2, stream, at);
        int size = Math.Max((int)(doc.XrefTable.Trailer!.GetInteger("Size") ?? 0), rows.Keys.Max() + 1);
        int self = size++;
        long xrefAt = ms.Position;
        rows[self] = (1, xrefAt, 0);
        var data = new System.IO.MemoryStream();
        foreach (var (type, f2, f3) in rows.Values)
        {
            data.WriteByte((byte)type);
            for (int shift = 24; shift >= 0; shift -= 8) data.WriteByte((byte)(f2 >> shift));
            data.WriteByte((byte)(f3 >> 8));
            data.WriteByte((byte)f3);
        }
        string index = string.Join(" ", rows.Keys.Select(k => $"{k} 1"));
        W($"{self} 0 obj\n<< /Type /XRef /Size {size} /Root {doc.XrefTable.Trailer["Root"]} /Prev {prev} /W [1 4 2] /Index [{index}] /Length {data.Length} >>\nstream\n");
        ms.Write(data.ToArray());
        W($"\nendstream\nendobj\nstartxref\n{xrefAt}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The form, with page 1's resources moved into an object stream: (file, object stream number, resources number).</summary>
    private static async Task<(byte[] Pdf, int Stream, int Resources)> PreparedWithResourcesInObjectStream()
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var resources = (PdfDictionary)doc.Resolver.Resolve(page.Dictionary["Resources"])!;
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        int res = next, stm = next + 1;
        var newPage = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Resources"] = new PdfIndirectRef(res) });
        byte[] prepared = AppendWithXrefStream(pdf, doc,
            new[] { (stm, ObjectStream((res, Serialize(resources)))), (pageNo, Serialize(newPage)) },
            new[] { (res, stm, 0) });
        return (prepared, stm, res);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task ObjectInARewrittenObjectStream_IsAChange(int? level)
    {
        var (prepared, stm, res) = await PreparedWithResourcesInObjectStream();
        byte[] signed = await Sign(prepared, level);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var entry = doc.XrefTable.Entries[res];
        // The object stream is written again under its own number: the resources keep their cross-reference entry.
        byte[] forged = AppendWithXrefStream(signed, doc, new[] { (stm, ObjectStream((res, "<< /Font << >> >>"))) }, Array.Empty<(int, int, int)>());
        using (var after = await PdfVectorDocument.OpenAsync(forged))
        {
            Assert.Equal(entry, after.XrefTable.Entries[res]);
            Assert.Empty(((PdfDictionary)((PdfDictionary)after.Resolver.Resolve(res)!)["Font"]!).Entries); // the new definition is read
        }
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(forged));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    [Fact]
    public async Task ObjectStreams_FillingAfterSigning_IsStillPermitted()
    {
        var (prepared, _, _) = await PreparedWithResourcesInObjectStream();
        byte[] signed = await Sign(prepared, 2);
        Assert.Equal(PdfSignatureVerdict.Valid, Assert.Single(await PdfSignatureValidator.ValidateAsync(signed)).Verdict);
        byte[] filled;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
            filled = PdfEngine.Vector.Forms.PdfFormFiller.Apply(doc, signed, new[] { new PdfEngine.Vector.Forms.PdfFieldChange("person.name", Text: "Ada") });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(filled));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.Equal(PdfLaterChanges.FormFields, check.LaterChanges);
    }

    // ---- A form dictionary stored in the catalog itself ----

    private static async Task<byte[]> PreparedWithFormInCatalog()
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        int rootNo = ((PdfIndirectRef)doc.XrefTable.Trailer!["Root"]!).ObjectNumber;
        var root = (PdfDictionary)doc.Resolver.Resolve(rootNo)!;
        var form = (PdfDictionary)doc.Resolver.Resolve(root["AcroForm"])!;
        return PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [rootNo] = new PdfDictionary(new Dictionary<string, PdfObject>(root.Entries) { ["AcroForm"] = form }),
        });
    }

    private static async Task<byte[]> ChangeFormInCatalog(byte[] signed, string key)
    {
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        int rootNo = ((PdfIndirectRef)doc.XrefTable.Trailer!["Root"]!).ObjectNumber;
        var root = (PdfDictionary)doc.Resolver.Resolve(rootNo)!;
        var form = Assert.IsType<PdfDictionary>(root["AcroForm"]); // stored in the catalog, not as its own object
        PdfObject value = key switch
        {
            "XFA" => new PdfArray(new PdfObject[] { PdfObjectWriter.TextString("template"), PdfObjectWriter.TextString("<template/>") }),
            "NeedAppearances" => new PdfBoolean(true),
            "DR" => new PdfDictionary(new Dictionary<string, PdfObject> { ["Font"] = new PdfDictionary(new Dictionary<string, PdfObject>()) }),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        var changed = new PdfDictionary(new Dictionary<string, PdfObject>(form.Entries) { [key] = value });
        return PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [rootNo] = new PdfDictionary(new Dictionary<string, PdfObject>(root.Entries) { ["AcroForm"] = changed }),
        });
    }

    [Theory]
    [InlineData("XFA", null, false)]
    [InlineData("XFA", 3, false)]
    [InlineData("NeedAppearances", 1, false)]
    [InlineData("NeedAppearances", 2, true)]
    [InlineData("DR", 1, false)]
    [InlineData("DR", 2, true)]
    public async Task FormInCatalog_ChangesAreClassified(string key, int? level, bool permitted)
    {
        byte[] signed = await Sign(await PreparedWithFormInCatalog(), level);
        Assert.Equal(PdfSignatureVerdict.Valid, Assert.Single(await PdfSignatureValidator.ValidateAsync(signed)).Verdict);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(await ChangeFormInCatalog(signed, key)));
        Assert.Equal(permitted ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    [Fact]
    public async Task FormInCatalog_FillingAndSigningAgain_IsStillPermitted()
    {
        byte[] signed = await Sign(await PreparedWithFormInCatalog(), 2);
        byte[] filled;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
            filled = PdfEngine.Vector.Forms.PdfFormFiller.Apply(doc, signed, new[] { new PdfEngine.Vector.Forms.PdfFieldChange("person.name", Text: "Ada") });
        byte[] twice = await Sign(filled, null);
        var checks = await PdfSignatureValidator.ValidateAsync(twice);
        Assert.Equal(2, checks.Count);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, checks[0].Verdict);
        Assert.Equal(PdfLaterChanges.FormFields | PdfLaterChanges.Signatures, checks[0].LaterChanges);
        Assert.Equal(PdfSignatureVerdict.Valid, checks[1].Verdict);
    }

    // ---- FieldMDP: fields a signature locks ----

    /// <summary>The form with an empty signature field "approver" that locks fields when signed (12.7.5.5, Table 236).</summary>
    private static async Task<byte[]> PreparedWithSignatureField(string? action, params string[] fields)
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var root = (PdfDictionary)doc.Resolver.Resolve(doc.XrefTable.Trailer!["Root"])!;
        int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
        var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
        int field = PdfIncrementalWriter.NextObjectNumber(doc);
        var entries = new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Widget"), ["FT"] = new PdfName("Sig"),
            ["T"] = PdfObjectWriter.TextString("approver"), ["Rect"] = Nums(150, 20, 290, 60), ["F"] = new PdfInteger(4),
            ["P"] = new PdfIndirectRef(pageNo),
        };
        if (action != null)
        {
            var lockEntries = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("SigFieldLock"), ["Action"] = new PdfName(action) };
            if (action != "All") lockEntries["Fields"] = new PdfArray(fields.Select(f => (PdfObject)PdfObjectWriter.TextString(f)).ToArray());
            entries["Lock"] = new PdfDictionary(lockEntries);
        }
        var annots = ((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToArray();
        var formFields = ((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToArray();
        return PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [field] = new PdfDictionary(entries),
            [pageNo] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Annots"] = new PdfArray(annots) }),
            [formNo] = new PdfDictionary(new Dictionary<string, PdfObject>(form.Entries) { ["Fields"] = new PdfArray(formFields) }),
        });
    }

    private static async Task<byte[]> SignField(byte[] pdf, string field, int? level = null)
    {
        using var cert = Cert();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var prepared = PdfSigner.Prepare(doc, pdf, new PdfSignatureRequest { SignerName = "Approver", FieldName = field, CertificationLevel = level });
        return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
    }

    private static async Task<byte[]> Fill(byte[] pdf, string field)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var kind = PdfEngine.Vector.Forms.PdfAcroForm.Read(doc)![field]!.Kind;
        var change = kind == PdfEngine.Vector.Forms.PdfFormFieldKind.CheckBox
            ? new PdfEngine.Vector.Forms.PdfFieldChange(field, Checked: true)
            : new PdfEngine.Vector.Forms.PdfFieldChange(field, Text: "Changed");
        return PdfEngine.Vector.Forms.PdfFormFiller.Apply(doc, pdf, new[] { change });
    }

    [Theory]
    [InlineData("Include", "person.name", "person.name", false)]
    [InlineData("Include", "person", "person.name", false)] // a locked parent locks its kids
    [InlineData("Include", "person.name", "notes", true)]
    [InlineData("All", "", "notes", false)]
    [InlineData("Exclude", "notes", "notes", true)]
    [InlineData("Exclude", "notes", "person.name", false)]
    public async Task LockedFields_MayNotChangeAfterSigning(string action, string locked, string filled, bool permitted)
    {
        byte[] signed = await SignField(await PreparedWithSignatureField(action, locked.Split(',', StringSplitOptions.RemoveEmptyEntries)), "approver");
        Assert.Equal(PdfSignatureVerdict.Valid, Assert.Single(await PdfSignatureValidator.ValidateAsync(signed)).Verdict);
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(await Fill(signed, filled)));
        Assert.Equal(permitted ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.Equal(PdfLaterChanges.FormFields, check.LaterChanges);
        if (!permitted) Assert.Contains("locked", check.Summary);
    }

    [Fact]
    public async Task SigningAnExistingEmptyField_AfterAnotherSignature_IsPermitted()
    {
        byte[] first = await Sign(await PreparedWithSignatureField(null), null);
        byte[] second = await SignField(first, "approver");
        var checks = await PdfSignatureValidator.ValidateAsync(second);
        Assert.Equal(2, checks.Count);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, checks[0].Verdict);
        Assert.Equal(PdfLaterChanges.Signatures, checks[0].LaterChanges);
        Assert.Equal(PdfSignatureVerdict.Valid, checks[1].Verdict);
    }

    // ---- Permitted changes stay permitted ----

    [Fact]
    public async Task StampReusingThePagesFont_IsAPermittedComment()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 3);
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var font = ((PdfDictionary)((PdfDictionary)doc.Resolver.Resolve(page.Dictionary["Resources"])!)["Font"]!)["Helv"]!;
        int ap = PdfIncrementalWriter.NextObjectNumber(doc), annot = ap + 1;
        var annots = ((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(annot)).ToArray();
        byte[] stamped = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [ap] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = Nums(0, 0, 100, 30),
                ["Resources"] = new PdfDictionary(new Dictionary<string, PdfObject>
                {
                    ["Font"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Helv"] = font }),
                }),
            }, Encoding.Latin1.GetBytes("BT /Helv 20 Tf 5 8 Td (APPROVED) Tj ET")),
            [annot] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Annot"), ["Subtype"] = new PdfName("Stamp"), ["Rect"] = Nums(150, 100, 250, 130),
                ["AP"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["N"] = new PdfIndirectRef(ap) }),
            }),
            [pageNo] = new PdfDictionary(new Dictionary<string, PdfObject>(page.Dictionary.Entries) { ["Annots"] = new PdfArray(annots) }),
        });
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(stamped));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.Equal(PdfLaterChanges.Annotations, check.LaterChanges);
        // Annotations may cover what was signed (ISO 32000-2 12.8.2.2): permitted, but said.
        Assert.Contains("cover", check.Summary);
    }
}
