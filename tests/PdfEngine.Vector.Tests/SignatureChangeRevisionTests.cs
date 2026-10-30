using System;
using System.Collections.Generic;
using System.Linq;
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
/// Signing is judged per revision: a field counts as signed only by the revision whose signature
/// covers exactly that revision and holds that field, as it was signed then. Objects planted
/// earlier and brought in later, and many signatures one after another, are each judged right.
/// </summary>
public partial class SignatureChangeClassificationTests
{
    /// <summary>An xref section that only frees <paramref name="number"/> (generation 1), appended to <paramref name="pdf"/>.</summary>
    private static byte[] AppendFree(byte[] pdf, int number)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text[(sx + 9)..].Trim().Split('\n', '\r')[0].Trim());
        int tr = text.LastIndexOf("trailer", StringComparison.Ordinal);
        string root = System.Text.RegularExpressions.Regex.Match(text[tr..], @"/Root\s+(\d+ \d+ R)").Groups[1].Value;
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(text[tr..], @"/Size\s+(\d+)").Groups[1].Value);
        var sb = new StringBuilder(text);
        if (!text.EndsWith('\n')) sb.Append('\n');
        long xref = sb.Length;
        sb.Append($"xref\n{number} 1\n0000000000 00001 f \ntrailer\n<< /Size {size} /Root {root} /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // Objects planted in the prepared file (a full-page signature field and a signature dictionary whose /ByteRange is
    // an object defined later), the dictionary freed, and after a real approval, all brought in: not signing.
    [Fact]
    public async Task PlantedSignatureObjectsBroughtInLater_AreNotSigning()
    {
        byte[] pdf = FormPdfFixture.Build();
        int field, ap, value, byteRange;
        byte[] planted;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            ap = PdfIncrementalWriter.NextObjectNumber(doc);
            field = ap + 1;
            value = ap + 2;
            byteRange = ap + 50; // not defined yet
            planted = PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
            {
                [ap] = Overlay(),
                [field] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Widget")), ("FT", new PdfName("Sig")),
                    ("T", PdfObjectWriter.TextString("planted")), ("Rect", Nums(0, 0, 300, 400)), ("F", new PdfInteger(4)),
                    ("AP", Dict(("N", new PdfIndirectRef(ap)))), ("V", new PdfIndirectRef(value))),
                [value] = Dict(("Type", new PdfName("Sig")), ("Filter", new PdfName("Adobe.PPKLite")), ("SubFilter", new PdfName("ETSI.CAdES.detached")),
                    ("ByteRange", new PdfIndirectRef(byteRange)), ("Contents", new PdfString(new byte[8], IsHex: true))),
            });
        }
        long valueOffset = Encoding.Latin1.GetString(planted).LastIndexOf($"\n{value} 0 obj", StringComparison.Ordinal) + 1;
        byte[] prepared = AppendFree(planted, value);

        byte[] certified = await Sign(prepared, 2);
        byte[] approved = await Sign(certified, null);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, (await PdfSignatureValidator.ValidateAsync(approved)).Single(c => c.CertificationLevel == 2).Verdict);

        byte[] attacked;
        using (var doc = await PdfVectorDocument.OpenAsync(approved))
        {
            var approval = PdfSignatureValidator.SignedFields(doc).Single(s => s.Sig["Reference"] == null);
            var page = doc.PageTree.Pages[0];
            var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
            int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
            var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
            attacked = PdfIncrementalWriter.Append(approved, doc, new Dictionary<int, PdfObject>
            {
                [byteRange] = new PdfArray(approval.Range.Select(v => (PdfObject)new PdfInteger(v)).ToArray()),
                [PageObject(doc)] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
                [formNo] = With(form, "Fields", new PdfArray(((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
            });
        }
        // The planted signature dictionary is in use again, read from its old bytes.
        string text = Encoding.Latin1.GetString(attacked);
        int xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + "\nxref\n".Length;
        attacked = Encoding.Latin1.GetBytes(text.Insert(xref, $"{value} 1\n{valueOffset:D10} 00000 n \n"));
        using (var check = await PdfVectorDocument.OpenAsync(attacked))
            Assert.IsType<PdfDictionary>(check.Resolver.Resolve(value));

        var checks = await PdfSignatureValidator.ValidateAsync(attacked);
        var certification = checks.Single(c => c.CertificationLevel == 2);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, certification.Verdict);
        Assert.True(certification.LaterChanges.HasFlag(PdfLaterChanges.Annotations));
    }

    // A later producer writing a signed field again, unchanged, into an object stream: still the same signing.
    [Fact]
    public async Task SignedFieldRewrittenUnchangedIntoAnObjectStream_IsStillSigning()
    {
        var (approved, field, _) = await CertifiedThenApproved();
        byte[] rewritten;
        using (var doc = await PdfVectorDocument.OpenAsync(approved))
        {
            int stm = PdfIncrementalWriter.NextObjectNumber(doc);
            rewritten = AppendWithXrefStream(approved, doc,
                new[] { (stm, ObjectStream((field, Serialize(doc.Resolver.Resolve(field)!)))) },
                new[] { (field, stm, 0) });
        }
        var certification = (await PdfSignatureValidator.ValidateAsync(rewritten)).Single(c => c.CertificationLevel == 2);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, certification.Verdict);
        Assert.Equal(PdfLaterChanges.Signatures, certification.LaterChanges);
    }

    // A petition: a certification and 95 approvals, one after another. Every signature validates. (The reader
    // follows at most 100 cross-reference sections, PdfSecurityLimits.MaxTrailerChainDepth, so 97 revisions here.)
    [Fact]
    public async Task ManySequentialApprovals_AllValidate()
    {
        const int approvals = 95;
        using var cert = Cert();
        async Task<byte[]> SignOnce(byte[] input, int? level, string name)
        {
            using var doc = await PdfVectorDocument.OpenAsync(input);
            var prepared = PdfSigner.Prepare(doc, input, new PdfSignatureRequest { SignerName = name, CertificationLevel = level, ContentsSize = 6144 });
            return prepared.Complete(await PdfCmsSigner.SignAsync(prepared.SignedBytes(), cert));
        }
        byte[] pdf = await SignOnce(FormPdfFixture.Build(), 2, "Author");
        for (int i = 0; i < approvals; i++)
        {
            try { pdf = await SignOnce(pdf, null, $"Signer {i}"); }
            catch (Exception ex) { throw new InvalidOperationException($"approval {i}, {pdf.Length} bytes", ex); }
        }

        var checks = await PdfSignatureValidator.ValidateAsync(pdf);
        Assert.Equal(approvals + 1, checks.Count);
        Assert.All(checks.Take(approvals), c =>
        {
            Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, c.Verdict);
            Assert.Equal(PdfLaterChanges.Signatures, c.LaterChanges);
        });
        Assert.Equal(PdfSignatureVerdict.Valid, checks[^1].Verdict);
    }
}
