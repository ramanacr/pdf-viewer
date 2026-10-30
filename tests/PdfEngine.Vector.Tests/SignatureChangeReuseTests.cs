using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Signatures;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// A signature counts as signing only for the one field it signed: not for a field that copies or
/// shares its value, nor for a field changed after it was signed. A resource changed inside the
/// form's default resources, and files that would need too many signature checks, are changes.
/// </summary>
public partial class SignatureChangeClassificationTests
{
    /// <summary>A level 2 certification followed by an approval signature: (file, approval's field, its signature dictionary).</summary>
    private static async Task<(byte[] Pdf, int Field, int Sig)> CertifiedThenApproved()
    {
        byte[] certified = await Sign(FormPdfFixture.Build(), 2);
        byte[] approved = await Sign(certified, null);
        using var doc = await PdfVectorDocument.OpenAsync(approved);
        var approval = PdfSignatureValidator.SignedFields(doc).Single(s => s.Sig["Reference"] == null);
        int sig = ((PdfIndirectRef)((PdfDictionary)doc.Resolver.Resolve(approval.Field.ObjectNumber)!)["V"]!).ObjectNumber;
        return (approved, approval.Field.ObjectNumber, sig);
    }

    /// <summary>After the approval: a field "evil" over the page whose value is the approval's signature, shared or copied.</summary>
    private static async Task<byte[]> AddFieldReusingSignature(byte[] pdf, int sig, bool copy)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
        int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
        var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
        int ap = PdfIncrementalWriter.NextObjectNumber(doc), field = ap + 1, value = ap + 2;
        var objects = new Dictionary<int, PdfObject>
        {
            [ap] = Overlay(),
            [field] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Widget")), ("FT", new PdfName("Sig")),
                ("T", PdfObjectWriter.TextString("evil")), ("Rect", Nums(0, 0, 300, 400)), ("F", new PdfInteger(4)),
                ("P", new PdfIndirectRef(pageNo)), ("AP", Dict(("N", new PdfIndirectRef(ap)))),
                ("V", new PdfIndirectRef(copy ? value : sig))),
            [pageNo] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
            [formNo] = With(form, "Fields", new PdfArray(((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
        };
        if (copy) objects[value] = (PdfDictionary)doc.Resolver.Resolve(sig)!; // same /ByteRange and /Contents
        return PdfIncrementalWriter.Append(pdf, doc, objects);
    }

    // A field reusing a later signature's value (a copy, or the same object) is not signing: the overlay is an annotation.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FieldReusingALaterSignaturesValue_IsNotSigning(bool copy)
    {
        var (approved, _, sig) = await CertifiedThenApproved();
        var before = await PdfSignatureValidator.ValidateAsync(approved);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, before.Single(c => c.CertificationLevel == 2).Verdict);

        var checks = await PdfSignatureValidator.ValidateAsync(await AddFieldReusingSignature(approved, sig, copy));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, checks.Single(c => c.CertificationLevel == 2).Verdict);
    }

    // A signed field changed after its own signature (here, a new appearance) no longer counts as that signing.
    [Fact]
    public async Task LaterSignatureFieldGivenANewAppearance_IsNotSigning()
    {
        var (approved, field, _) = await CertifiedThenApproved();
        byte[] reskinned;
        using (var doc = await PdfVectorDocument.OpenAsync(approved))
        {
            int ap = PdfIncrementalWriter.NextObjectNumber(doc);
            reskinned = PdfIncrementalWriter.Append(approved, doc, new Dictionary<int, PdfObject>
            {
                [ap] = Overlay(),
                [field] = With((PdfDictionary)doc.Resolver.Resolve(field)!, "AP", Dict(("N", new PdfIndirectRef(ap)))),
            });
        }
        var checks = await PdfSignatureValidator.ValidateAsync(reskinned);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, checks.Single(c => c.CertificationLevel == 2).Verdict);
        Assert.All(checks, c => Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, c.Verdict));
    }

    // A font used only through the form's default resources, changed inside (its /Encoding): a locked field using it may look different.
    [Fact]
    public async Task ResourceChangedInsideTheDefaultResources_BreaksALock()
    {
        byte[] prepared = await PreparedWithSignatureField("Include", "zip");
        using (var doc = await PdfVectorDocument.OpenAsync(prepared))
        {
            var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
            int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
            var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
            var dr = (PdfDictionary)form["DR"]!;
            int zip = PdfEngine.Vector.Forms.PdfAcroForm.Read(doc)!["zip"]!.ObjectNumber;
            int font = PdfIncrementalWriter.NextObjectNumber(doc);
            prepared = PdfIncrementalWriter.Append(prepared, doc, new Dictionary<int, PdfObject>
            {
                [font] = Dict(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")), ("BaseFont", new PdfName("Helvetica"))),
                [formNo] = With(form, "DR", With(dr, "Font", With((PdfDictionary)dr["Font"]!, "Hx", new PdfIndirectRef(font)))),
                [zip] = With(With((PdfDictionary)doc.Resolver.Resolve(zip)!, "DA", PdfObjectWriter.TextString("/Hx 12 Tf 0 g")), "V", PdfObjectWriter.TextString("11111")),
            });
        }
        byte[] signed = await SignField(prepared, "approver");
        Assert.Equal(PdfSignatureVerdict.Valid, Assert.Single(await PdfSignatureValidator.ValidateAsync(signed)).Verdict);
        byte[] changed;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
        {
            var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
            var form = (PdfDictionary)doc.Resolver.Resolve(root["AcroForm"])!;
            int font = ((PdfIndirectRef)((PdfDictionary)((PdfDictionary)form["DR"]!)["Font"]!)["Hx"]!).ObjectNumber;
            changed = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
            {
                [font] = With((PdfDictionary)doc.Resolver.Resolve(font)!, "Encoding",
                    Dict(("Differences", new PdfArray(new PdfObject[] { new PdfInteger(49), new PdfName("nine") })))),
            });
        }
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(changed));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // Many signature fields with distinct values, each needing a check over the whole file: past the cap, the changes are unverifiable.
    [Fact]
    public async Task TooManySignaturesToCheck_IsUnverifiable_NotValid()
    {
        const int fakes = SignatureCheckCacheMax + 6;
        byte[] signed = await Sign(FormPdfFixture.Build(), 2);
        byte[] forged;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
        {
            var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
            int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
            var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
            int next = PdfIncrementalWriter.NextObjectNumber(doc);
            var objects = new Dictionary<int, PdfObject>();
            var fields = ((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.ToList();
            for (int i = 0; i < fakes; i++)
            {
                int sig = next++, field = next++;
                objects[sig] = Dict(("Type", new PdfName("Sig")), ("Filter", new PdfName("Adobe.PPKLite")), ("SubFilter", new PdfName("ETSI.CAdES.detached")),
                    ("ByteRange", new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(9_999_999_999) })),
                    ("Contents", new PdfString(BitConverter.GetBytes(i), IsHex: true)));
                objects[field] = Dict(("FT", new PdfName("Sig")), ("T", PdfObjectWriter.TextString($"fake{i}")), ("V", new PdfIndirectRef(sig)));
                fields.Add(new PdfIndirectRef(field));
            }
            objects[formNo] = With(form, "Fields", new PdfArray(fields));
            forged = PdfIncrementalWriter.Append(signed, doc, objects);
        }
        // Each byte range covers the whole file but its first byte: patched in place, same width.
        string text = Encoding.Latin1.GetString(forged).Replace("9999999999", (forged.Length - 1).ToString("D10"));
        forged = Encoding.Latin1.GetBytes(text);
        var check = (await PdfSignatureValidator.ValidateAsync(forged)).Single(c => c.CertificationLevel == 2);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.Contains("could not be checked", check.Summary);
    }

    private const int SignatureCheckCacheMax = 64;

    [Fact]
    public void SignatureCheckCap_IsTheOneTested() => Assert.Equal(SignatureCheckCacheMax, PdfSignatureValidator.SignatureCheckCache.MaxChecks);
}
