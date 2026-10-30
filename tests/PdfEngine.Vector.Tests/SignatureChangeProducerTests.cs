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
/// Empty signature fields, large legitimate files, the form and catalog updates other producers
/// make when they add validation data, and actions on annotations.
/// </summary>
public partial class SignatureChangeClassificationTests
{
    private static PdfStream Overlay() => PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
    {
        ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = Nums(0, 0, 300, 400),
        ["Resources"] = Dict(("Font", Dict(("Helv", Dict(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")), ("BaseFont", new PdfName("Helvetica"))))))),
    }, Encoding.Latin1.GetBytes("1 1 1 rg 0 0 300 400 re f 0 g BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET"));

    /// <summary>After signing: a signature field with no signature, over the whole page, with an appearance.</summary>
    private static async Task<byte[]> AddEmptySignatureWidget(byte[] signed)
    {
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
        int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
        var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
        int ap = PdfIncrementalWriter.NextObjectNumber(doc), field = ap + 1;
        return PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [ap] = Overlay(),
            [field] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Widget")), ("FT", new PdfName("Sig")),
                ("T", PdfObjectWriter.TextString("overlay")), ("Rect", Nums(0, 0, 300, 400)), ("F", new PdfInteger(4)),
                ("P", new PdfIndirectRef(pageNo)), ("AP", Dict(("N", new PdfIndirectRef(ap))))),
            [pageNo] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
            [formNo] = With(form, "Fields", new PdfArray(((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
        });
    }

    // A signature field counts as signing only when it holds a new signature; an empty one is an annotation.
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task NewEmptySignatureField_IsNotSigning(int level, bool permitted)
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), level);
        var check = await First(await AddEmptySignatureWidget(signed));
        Assert.Equal(permitted ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.Equal(PdfLaterChanges.Annotations, check.LaterChanges);
    }

    [Fact]
    public async Task EmptySignatureFieldGivenAnAppearance_IsNotSigning_ButSigningItIs()
    {
        byte[] certified = await Sign(await PreparedWithSignatureField(null), 2);
        byte[] painted;
        using (var doc = await PdfVectorDocument.OpenAsync(certified))
        {
            int field = PdfEngine.Vector.Forms.PdfAcroForm.Read(doc)!["approver"]!.ObjectNumber;
            int ap = PdfIncrementalWriter.NextObjectNumber(doc);
            painted = PdfIncrementalWriter.Append(certified, doc, new Dictionary<int, PdfObject>
            {
                [ap] = Overlay(),
                [field] = With((PdfDictionary)doc.Resolver.Resolve(field)!, "AP", Dict(("N", new PdfIndirectRef(ap)))),
            });
        }
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await First(painted)).Verdict);

        var signedAgain = await First(await SignField(certified, "approver"));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, signedAgain.Verdict);
        Assert.Equal(PdfLaterChanges.Signatures, signedAgain.LaterChanges);
    }

    // Hundreds of pages, each with a CID font whose /W array holds thousands of numbers: still checked.
    [Fact]
    public async Task LargeFileWithBigWidthArrays_IsChecked()
    {
        var b = new VectorPdfBuilder();
        var widths = new StringBuilder();
        for (int i = 0; i < 1000; i++) widths.Append(i * 3 + 1).Append(" [500 600 700] ");
        for (int p = 0; p < 300; p++)
        {
            int cid = b.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /CJK /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /W [" + widths + "] >>");
            int font = b.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /CJK /Encoding /Identity-H /DescendantFonts [{cid} 0 R] >>");
            b.AddPage("0.9 g 0 0 100 100 re f", $"<< /Font << /F1 {font} 0 R >> >>");
        }
        byte[] pdf = b.Build();
        Assert.True(pdf.Length > 4_000_000);
        byte[] signed = await Sign(pdf, 3);
        byte[] commented;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
        {
            var page = doc.PageTree.Pages[0];
            int annot = PdfIncrementalWriter.NextObjectNumber(doc);
            var annots = (doc.Resolver.Resolve(page.Dictionary["Annots"]) as PdfArray)?.Items.ToList() ?? new List<PdfObject>();
            annots.Add(new PdfIndirectRef(annot));
            commented = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
            {
                [annot] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Square")), ("Rect", Nums(10, 10, 20, 20))),
                [PageObject(doc)] = With(page.Dictionary, "Annots", new PdfArray(annots)),
            });
        }
        var check = await First(commented);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.Equal(PdfLaterChanges.Annotations, check.LaterChanges);
    }

    /// <summary>After signing: the form's /DR or /NeedAppearances changed as another producer would, then "notes" filled in.</summary>
    private static async Task<byte[]> ChangeFormThenFillNotes(byte[] signed, string change)
    {
        byte[] changed;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
        {
            var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
            int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
            var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
            var dr = (PdfDictionary)form["DR"]!;
            var fonts = (PdfDictionary)dr["Font"]!;
            int font = PdfIncrementalWriter.NextObjectNumber(doc);
            var objects = new Dictionary<int, PdfObject>
            {
                [font] = Dict(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")), ("BaseFont", new PdfName("Courier"))),
            };
            objects[formNo] = change switch
            {
                "AddFont" => With(form, "DR", With(dr, "Font", With(fonts, "Cour", new PdfIndirectRef(font)))),
                "ReplaceFont" => With(form, "DR", With(dr, "Font", With(fonts, "Helv", new PdfIndirectRef(font)))),
                "NeedAppearances" => With(form, "NeedAppearances", new PdfBoolean(true)),
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
            changed = PdfIncrementalWriter.Append(signed, doc, objects);
        }
        return await Fill(changed, "notes");
    }

    // FieldMDP: new default resources and asking for appearances leave a locked field as it was; replacing its font does not.
    [Theory]
    [InlineData("AddFont", true)]
    [InlineData("NeedAppearances", true)]
    [InlineData("ReplaceFont", false)]
    public async Task FormUpdates_AndFieldLocks(string change, bool permitted)
    {
        byte[] signed = await SignField(await PreparedWithSignatureField("Include", "person.name"), "approver");
        var check = Assert.Single(await PdfSignatureValidator.ValidateAsync(await ChangeFormThenFillNotes(signed, change)));
        Assert.Equal(permitted ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.Equal(PdfLaterChanges.FormFields, check.LaterChanges);
    }

    private static async Task<byte[]> WithCatalogEntry(byte[] pdf, string key, PdfObject value)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        int rootNo = RootNumber(doc);
        return PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [rootNo] = With((PdfDictionary)doc.Resolver.Resolve(rootNo)!, key, value),
        });
    }

    // Validation data updates may add developer extensions and raise the version; lowering it is a change.
    [Theory]
    [InlineData("Extensions", true)]
    [InlineData("VersionUp", true)]
    [InlineData("VersionDown", false)]
    public async Task CatalogUpdatesWithValidationData_AtLevel1(string update, bool permitted)
    {
        byte[] prepared = await WithCatalogEntry(FormPdfFixture.Build(), "Version", new PdfName("1.7"));
        byte[] certified = await Sign(prepared, 1);
        byte[] updated = update switch
        {
            "Extensions" => await WithCatalogEntry(certified, "Extensions", Dict(
                ("ADBE", Dict(("BaseVersion", new PdfName("1.7")), ("ExtensionLevel", new PdfInteger(8)))),
                ("ESIC", Dict(("BaseVersion", new PdfName("1.7")), ("ExtensionLevel", new PdfInteger(1)))))),
            "VersionUp" => await WithCatalogEntry(certified, "Version", new PdfName("2.0")),
            _ => await WithCatalogEntry(certified, "Version", new PdfName("1.4")),
        };
        var check = await First(updated);
        Assert.Equal(permitted ? PdfSignatureVerdict.Valid : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        if (permitted) Assert.Equal(PdfLaterChanges.ValidationData, check.LaterChanges);
    }

    // A signed signature field written directly in a parent's /Kids, removed: a signature removed.
    [Fact]
    public async Task DirectSignedFieldRemovedFromItsParent_IsAChange()
    {
        byte[] pdf = FormPdfFixture.Build();
        int group;
        byte[] prepared;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
            int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
            var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
            int value = PdfIncrementalWriter.NextObjectNumber(doc);
            group = value + 1;
            var kid = Dict(("FT", new PdfName("Sig")), ("T", PdfObjectWriter.TextString("earlier")), ("V", new PdfIndirectRef(value)));
            prepared = PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
            {
                [value] = Dict(("Type", new PdfName("Sig")), ("Filter", new PdfName("Adobe.PPKLite")), ("SubFilter", new PdfName("ETSI.CAdES.detached")),
                    ("ByteRange", Nums(0, 0, 0, 0)), ("Contents", new PdfString(new byte[8], IsHex: true))),
                [group] = Dict(("T", PdfObjectWriter.TextString("group")), ("Kids", new PdfArray(new PdfObject[] { kid }))),
                [formNo] = With(form, "Fields", new PdfArray(((PdfArray)form["Fields"]!).Items.Append(new PdfIndirectRef(group)).ToArray())),
            });
        }
        byte[] signed = await Sign(prepared, null);
        PdfSignatureCheck Real(IReadOnlyList<PdfSignatureCheck> checks) => checks.Single(c => !c.FieldName.StartsWith("group", StringComparison.Ordinal));
        Assert.Equal(PdfSignatureVerdict.Valid, Real(await PdfSignatureValidator.ValidateAsync(signed)).Verdict);
        byte[] removed = await Rewrite(signed, group, Dict(("T", PdfObjectWriter.TextString("group")), ("Kids", new PdfArray(Array.Empty<PdfObject>()))));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, Real(await PdfSignatureValidator.ValidateAsync(removed)).Verdict);
    }

    private static PdfDictionary Link(string uri) => Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Link")), ("Rect", Nums(10, 10, 60, 30)),
        ("A", Dict(("S", new PdfName("URI")), ("URI", PdfObjectWriter.TextString(uri)))));

    private static async Task<(byte[] Pdf, int Annot)> AddLink(byte[] pdf, string uri)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int annot = PdfIncrementalWriter.NextObjectNumber(doc);
        return (PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [annot] = Link(uri),
            [PageObject(doc)] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(annot)).ToArray())),
        }), annot);
    }

    // A new link (with its action) is commenting at level 3; changing an existing annotation's action is a change of the document.
    [Fact]
    public async Task LinkActions_AtLevel3()
    {
        var (prepared, link) = await AddLink(FormPdfFixture.Build(), "https://example.org/a");
        byte[] signed = await Sign(prepared, 3);
        var added = await First((await AddLink(signed, "https://example.org/b")).Pdf);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, added.Verdict);
        Assert.Equal(PdfLaterChanges.Annotations, added.LaterChanges);
        var redirected = await First(await Rewrite(signed, link, Link("https://example.org/elsewhere")));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, redirected.Verdict);
    }

    // Many annotations written directly in /Annots are compared within the work budget, and one more is still a comment.
    [Fact]
    public async Task ManyDirectAnnotations_AreCompared()
    {
        byte[] pdf = FormPdfFixture.Build();
        byte[] prepared;
        using (var doc = await PdfVectorDocument.OpenAsync(pdf))
        {
            var page = doc.PageTree.Pages[0];
            var annots = ((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.ToList();
            for (int i = 0; i < 400; i++)
                annots.Add(Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Square")), ("Rect", Nums(i % 290, 0, i % 290 + 5, 5))));
            prepared = PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
            {
                [PageObject(doc)] = With(page.Dictionary, "Annots", new PdfArray(annots)),
            });
        }
        byte[] signed = await Sign(prepared, 3);
        byte[] more;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
        {
            var page = doc.PageTree.Pages[0];
            var annots = ((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.ToList();
            annots.Add(Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Circle")), ("Rect", Nums(100, 100, 120, 120))));
            more = PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
            {
                [PageObject(doc)] = With(page.Dictionary, "Annots", new PdfArray(annots)),
            });
        }
        var check = await First(more);
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.Equal(PdfLaterChanges.Annotations, check.LaterChanges);
    }
}
