using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Change classification on unusual but valid structures: deep nesting, direct page tree nodes,
/// objects that play two parts, shared objects, fields added or edited rather than filled, and
/// signatures inside the field tree.
/// </summary>
public partial class SignatureChangeClassificationTests
{
    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries) =>
        new(entries.ToDictionary(e => e.Key, e => e.Value));

    private static PdfDictionary With(PdfDictionary d, string key, PdfObject value) =>
        new(new Dictionary<string, PdfObject>(d.Entries) { [key] = value });

    private static PdfStream Image(byte grey) => PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
    {
        ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Image"), ["Width"] = new PdfInteger(1), ["Height"] = new PdfInteger(1),
        ["ColorSpace"] = new PdfName("DeviceGray"), ["BitsPerComponent"] = new PdfInteger(8),
    }, new[] { grey });

    private static int RootNumber(PdfVectorDocument doc) => ((PdfIndirectRef)doc.XrefTable.Trailer!["Root"]!).ObjectNumber;

    /// <summary>
    /// The form, with page 1 drawing an image /Im through resources that are their own object.
    /// <paramref name="extra"/> may add to the update (doc, the update's objects, resources number).
    /// </summary>
    private static async Task<(byte[] Pdf, int Image, int Resources)> PreparedWithImage(Action<PdfVectorDocument, Dictionary<int, PdfObject>, int>? extra = null)
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var resources = (PdfDictionary)doc.Resolver.Resolve(page.Dictionary["Resources"])!;
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        int image = next, res = next + 1, contents = next + 2;
        var objects = new Dictionary<int, PdfObject>
        {
            [image] = Image(0x80),
            [res] = With(resources, "XObject", Dict(("Im", new PdfIndirectRef(image)))),
            [contents] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("q 100 0 0 100 100 50 cm /Im Do Q")),
            [pageNo] = With(With(page.Dictionary, "Resources", new PdfIndirectRef(res)), "Contents",
                new PdfArray(new PdfObject[] { page.Dictionary["Contents"]!, new PdfIndirectRef(contents) })),
        };
        extra?.Invoke(doc, objects, res);
        return (PdfIncrementalWriter.Append(pdf, doc, objects), image, res);
    }

    private static async Task<byte[]> Rewrite(byte[] signed, int number, PdfObject value)
    {
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        return PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject> { [number] = value });
    }

    private static async Task<PdfSignatureCheck> First(byte[] pdf) => (await PdfSignatureValidator.ValidateAsync(pdf))[0];

    [Fact]
    public async Task ImageRewritten_IsAChange()
    {
        var (prepared, image, _) = await PreparedWithImage();
        byte[] signed = await Sign(prepared, 1);
        Assert.Equal(PdfSignatureVerdict.Valid, (await First(signed)).Verdict);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await First(await Rewrite(signed, image, Image(0x00)))).Verdict);
    }

    // The resources are first reached deep inside an unrelated entry of the page tree root.
    [Fact]
    public async Task ResourcesFirstReachedDeepInTheTree_AreStillPageContent()
    {
        var (prepared, image, _) = await PreparedWithImage((doc, objects, res) =>
        {
            int pagesNo = ((PdfIndirectRef)((PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!)["Pages"]!).ObjectNumber;
            var pages = (PdfDictionary)doc.Resolver.Resolve(pagesNo)!;
            // A chain of objects, each referring to the next, the last to the resources.
            PdfObject junk = new PdfIndirectRef(res);
            int next = objects.Keys.Max() + 1;
            for (int i = 0; i < 32; i++)
            {
                objects[next] = new PdfArray(new[] { junk });
                junk = new PdfIndirectRef(next++);
            }
            objects[pagesNo] = With(pages, "AJunk", junk);
        });
        byte[] signed = await Sign(prepared, 1);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await First(await Rewrite(signed, image, Image(0x00)))).Verdict);
    }

    /// <summary>The form with a second page written directly: in the catalog's /Pages (itself direct), or in /Kids of an indirect /Pages.</summary>
    private static async Task<(byte[] Pdf, int Contents)> PreparedWithDirectPage(bool directRoot)
    {
        // An empty signature field to sign: the signer places new fields only on pages that are their own objects.
        byte[] pdf = await PreparedWithSignatureField(null);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        int rootNo = RootNumber(doc);
        var root = (PdfDictionary)doc.Resolver.Resolve(rootNo)!;
        var pagesRef = (PdfIndirectRef)root["Pages"]!;
        var pages = (PdfDictionary)doc.Resolver.Resolve(pagesRef)!;
        var page1 = doc.PageTree.Pages[0].Dictionary;
        int contents = PdfIncrementalWriter.NextObjectNumber(doc);
        var page2 = Dict(("Type", new PdfName("Page")), ("MediaBox", Nums(0, 0, 300, 400)), ("Resources", page1["Resources"]!),
            ("Contents", new PdfIndirectRef(contents)));
        var newPages = With(With(pages, "Kids", new PdfArray(new PdfObject[] { new PdfIndirectRef(PageObject(doc)), page2 })), "Count", new PdfInteger(2));
        var objects = new Dictionary<int, PdfObject>
        {
            [contents] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("0 0 1 rg 10 10 50 50 re f")),
        };
        if (directRoot) objects[rootNo] = With(root, "Pages", newPages);
        else objects[pagesRef.ObjectNumber] = newPages;
        return (PdfIncrementalWriter.Append(pdf, doc, objects), contents);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    public async Task DirectPageNodes_ContentChanges_AreChanges(bool directRoot, int level)
    {
        var (prepared, contents) = await PreparedWithDirectPage(directRoot);
        using (var doc = await PdfVectorDocument.OpenAsync(prepared))
            Assert.Equal(2, doc.PageCount); // the renderer reads the direct page
        byte[] signed = await SignField(prepared, "approver", level);
        Assert.Equal(PdfSignatureVerdict.Valid, (await First(signed)).Verdict);
        var forged = await Rewrite(signed, contents, PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>(), Encoding.Latin1.GetBytes("BT /Helv 30 Tf 20 200 Td (FORGED) Tj ET")));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await First(forged)).Verdict);
    }

    [Fact]
    public async Task DirectPageNodes_FillingIsStillPermitted()
    {
        var (prepared, _) = await PreparedWithDirectPage(directRoot: true);
        byte[] signed = await SignField(prepared, "approver", 2);
        var check = await First(await Fill(signed, "person.name"));
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.Equal(PdfLaterChanges.FormFields, check.LaterChanges);
    }

    // Page resources that are also listed as a form field, or as a widget annotation: page content wins.
    [Theory]
    [InlineData("Fields", 2)]
    [InlineData("Annots", 3)]
    [InlineData("Annots", null)]
    public async Task ResourcesAlsoListedAsAFieldOrWidget_ArePageContent(string list, int? level)
    {
        var (prepared, image, _) = await PreparedWithImage((doc, objects, res) =>
        {
            if (list == "Fields")
            {
                var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
                int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
                var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
                objects[formNo] = With(form, "Fields", new PdfArray(((PdfArray)form["Fields"]!).Items.Append(new PdfIndirectRef(res)).ToArray()));
            }
            else
            {
                int pageNo = PageObject(doc);
                var page = (PdfDictionary)objects[pageNo];
                var annots = ((PdfArray)doc.Resolver.Resolve(page["Annots"])!).Items.Append(new PdfIndirectRef(res)).ToArray();
                objects[pageNo] = With(page, "Annots", new PdfArray(annots));
                objects[res] = With(With((PdfDictionary)objects[res], "Subtype", new PdfName("Widget")), "Rect", Nums(0, 0, 0, 0));
            }
        });
        byte[] signed = await Sign(prepared, level);
        Assert.Equal(PdfSignatureVerdict.Valid, (await First(signed)).Verdict);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await First(await Rewrite(signed, image, Image(0x00)))).Verdict);
    }

    // An object of the signed revision that nothing is known to use is not assumed harmless when it changes.
    [Fact]
    public async Task UnusedSignedObjectRewritten_IsAChange()
    {
        int orphan = 0;
        var (prepared, _, _) = await PreparedWithImage((doc, objects, _) =>
        {
            orphan = objects.Keys.Max() + 1;
            objects[orphan] = Dict(("Note", PdfObjectWriter.TextString("unused")));
        });
        byte[] signed = await Sign(prepared, null);
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, (await First(await Rewrite(signed, orphan, Dict(("Note", PdfObjectWriter.TextString("changed")))))).Verdict);
    }

    // Many annotations sharing one large array: checked in linear time, and still permitted at level 3.
    [Fact]
    public async Task ManyAnnotationsSharingObjects_AreCheckedQuickly()
    {
        const int n = 2000, m = 2000;
        byte[] signed = await Sign(FormPdfFixture.Build(), 3);
        byte[] commented;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
        {
            var page = doc.PageTree.Pages[0];
            int pageNo = PageObject(doc);
            int next = PdfIncrementalWriter.NextObjectNumber(doc);
            int shared = next++;
            var objects = new Dictionary<int, PdfObject>();
            var items = new List<PdfObject>();
            for (int i = 0; i < m; i++)
            {
                objects[next] = Dict(("I", new PdfInteger(i)));
                items.Add(new PdfIndirectRef(next++));
            }
            objects[shared] = new PdfArray(items);
            var annots = ((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.ToList();
            for (int i = 0; i < n; i++)
            {
                objects[next] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Square")), ("Rect", Nums(10, 10, 20, 20)),
                    ("Data", new PdfIndirectRef(shared)));
                annots.Add(new PdfIndirectRef(next++));
            }
            objects[pageNo] = With(page.Dictionary, "Annots", new PdfArray(annots));
            commented = PdfIncrementalWriter.Append(signed, doc, objects);
        }
        var clock = Stopwatch.StartNew();
        var check = await First(commented);
        clock.Stop();
        Assert.Equal(PdfSignatureVerdict.ValidWithPermittedChanges, check.Verdict);
        Assert.Equal(PdfLaterChanges.Annotations, check.LaterChanges);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    /// <summary>After signing: a new text field with its own widget and appearance on page 1.</summary>
    private static async Task<byte[]> AddTextField(byte[] signed)
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
            [ap] = PdfObjectWriter.NewStream(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("XObject"), ["Subtype"] = new PdfName("Form"), ["BBox"] = Nums(0, 0, 300, 400),
            }, Encoding.Latin1.GetBytes("1 1 1 rg 0 0 300 400 re f")),
            [field] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Widget")), ("FT", new PdfName("Tx")),
                ("T", PdfObjectWriter.TextString("cover")), ("Rect", Nums(0, 0, 300, 400)), ("P", new PdfIndirectRef(pageNo)),
                ("AP", Dict(("N", new PdfIndirectRef(ap))))),
            [pageNo] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
            [formNo] = With(form, "Fields", new PdfArray(((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
        });
    }

    // Level 2 permits filling in existing fields and signing; adding a field is creating an annotation (level 3).
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task NewTextField_IsNotFillingIn(int level, bool permitted)
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), level);
        var check = await First(await AddTextField(signed));
        Assert.Equal(permitted ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
        Assert.True(check.LaterChanges.HasFlag(PdfLaterChanges.Annotations));
    }

    /// <summary>After signing: the widget of field "notes" with one entry changed.</summary>
    private static async Task<byte[]> EditNotesWidget(byte[] signed, string key, PdfObject value)
    {
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var field = PdfEngine.Vector.Forms.PdfAcroForm.Read(doc)!["notes"]!;
        int widget = field.Widgets[0].ObjectNumber;
        return PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [widget] = With((PdfDictionary)doc.Resolver.Resolve(widget)!, key, value),
        });
    }

    private static PdfDictionary JavaScript() => Dict(("S", new PdfName("JavaScript")), ("JS", PdfObjectWriter.TextString("app.alert(1)")));

    // Filling changes a field's value and appearance; moving, resizing or restyling it is editing an annotation; actions change the document.
    [Theory]
    [InlineData("Rect", 2, false)]
    [InlineData("Rect", 3, true)]
    [InlineData("F", 2, false)]
    [InlineData("AA", 3, false)]
    [InlineData("AA", null, false)]
    public async Task EditingAFieldsWidget_IsNotFillingIn(string key, int? level, bool permitted)
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), level);
        PdfObject value = key switch
        {
            "Rect" => Nums(0, 0, 300, 400),
            "F" => new PdfInteger(2), // hidden
            _ => Dict(("Fo", JavaScript())),
        };
        var check = await First(await EditNotesWidget(signed, key, value));
        Assert.Equal(permitted ? PdfSignatureVerdict.ValidWithPermittedChanges : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    /// <summary>The form with a field "group" (no type) whose kid is an empty signature field "group.s".</summary>
    private static async Task<(byte[] Pdf, int Group)> PreparedWithNestedSignatureField()
    {
        byte[] pdf = FormPdfFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
        int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
        var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
        int group = PdfIncrementalWriter.NextObjectNumber(doc), sig = group + 1;
        return (PdfIncrementalWriter.Append(pdf, doc, new Dictionary<int, PdfObject>
        {
            [group] = Dict(("T", PdfObjectWriter.TextString("group")), ("Kids", new PdfArray(new PdfObject[] { new PdfIndirectRef(sig) }))),
            [sig] = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Widget")), ("FT", new PdfName("Sig")),
                ("T", PdfObjectWriter.TextString("s")), ("Parent", new PdfIndirectRef(group)), ("Rect", Nums(150, 20, 290, 60)),
                ("P", new PdfIndirectRef(pageNo))),
            [pageNo] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(sig)).ToArray())),
            [formNo] = With(form, "Fields", new PdfArray(((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(group)).ToArray())),
        }), group);
    }

    // A signature lower in the field tree, removed from its parent's /Kids, is a signature removed.
    [Fact]
    public async Task SignatureRemovedFromTheFieldTree_IsAChange()
    {
        var (prepared, group) = await PreparedWithNestedSignatureField();
        byte[] first = await SignField(prepared, "group.s");
        byte[] second = await Sign(first, null);
        var checks = await PdfSignatureValidator.ValidateAsync(second);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(c.Verdict is PdfSignatureVerdict.Valid or PdfSignatureVerdict.ValidWithPermittedChanges, c.Summary));

        byte[] removed;
        using (var doc = await PdfVectorDocument.OpenAsync(second))
            removed = PdfIncrementalWriter.Append(second, doc, new Dictionary<int, PdfObject>
            {
                [group] = With((PdfDictionary)doc.Resolver.Resolve(group)!, "Kids", new PdfArray(Array.Empty<PdfObject>())),
            });
        var left = Assert.Single(await PdfSignatureValidator.ValidateAsync(removed));
        Assert.Equal(PdfSignatureVerdict.ModifiedAfterSigning, left.Verdict);
    }

    /// <summary>After signing: an invisible signature field holding a (placeholder) document timestamp, with or without an action.</summary>
    private static async Task<byte[]> AddInvisibleTimestampField(byte[] signed, bool withAction)
    {
        using var doc = await PdfVectorDocument.OpenAsync(signed);
        var page = doc.PageTree.Pages[0];
        int pageNo = PageObject(doc);
        var root = (PdfDictionary)doc.Resolver.Resolve(RootNumber(doc))!;
        int formNo = ((PdfIndirectRef)root["AcroForm"]!).ObjectNumber;
        var form = (PdfDictionary)doc.Resolver.Resolve(formNo)!;
        int value = PdfIncrementalWriter.NextObjectNumber(doc), field = value + 1;
        var fieldDict = Dict(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Widget")), ("FT", new PdfName("Sig")),
            ("T", PdfObjectWriter.TextString("ts")), ("Rect", Nums(0, 0, 0, 0)), ("F", new PdfInteger(2)), ("P", new PdfIndirectRef(pageNo)),
            ("V", new PdfIndirectRef(value)));
        if (withAction) fieldDict = With(fieldDict, "AA", Dict(("PO", JavaScript())));
        return PdfIncrementalWriter.Append(signed, doc, new Dictionary<int, PdfObject>
        {
            [value] = Dict(("Type", new PdfName("DocTimeStamp")), ("SubFilter", new PdfName("ETSI.RFC3161")),
                ("ByteRange", Nums(0, 0, 0, 0)), ("Contents", new PdfString(new byte[8], IsHex: true))),
            [field] = fieldDict,
            [pageNo] = With(page.Dictionary, "Annots", new PdfArray(((PdfArray)doc.Resolver.Resolve(page.Dictionary["Annots"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
            [formNo] = With(form, "Fields", new PdfArray(((PdfArray)doc.Resolver.Resolve(form["Fields"])!).Items.Append(new PdfIndirectRef(field)).ToArray())),
        });
    }

    // Only the timestamp itself is permitted at level 1: an action on its field is not.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task InvisibleTimestampField_WithAnAction_IsNotPermittedAtLevel1(bool withAction, bool permitted)
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 1);
        var checks = await PdfSignatureValidator.ValidateAsync(await AddInvisibleTimestampField(signed, withAction));
        var check = checks.Single(c => c.FieldName != "ts"); // the placeholder timestamp itself is not a valid one
        Assert.Equal(permitted ? PdfSignatureVerdict.Valid : PdfSignatureVerdict.ModifiedAfterSigning, check.Verdict);
    }

    // A later revision whose cross-reference entry points at the wrong object: a result, not an exception.
    [Fact]
    public async Task DamagedLaterCrossReference_GivesAResult()
    {
        byte[] signed = await Sign(FormPdfFixture.Build(), 1);
        int contents;
        using (var doc = await PdfVectorDocument.OpenAsync(signed))
            contents = ((PdfIndirectRef)doc.PageTree.Pages[0].Dictionary["Contents"]!).ObjectNumber;
        string text = Encoding.Latin1.GetString(signed);
        int sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        long prev = long.Parse(text[(sx + 9)..].Trim().Split('\n', '\r')[0].Trim());
        int tr = text.LastIndexOf("trailer", StringComparison.Ordinal);
        string root = System.Text.RegularExpressions.Regex.Match(text[tr..], @"/Root\s+(\d+ \d+ R)").Groups[1].Value;
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(text[tr..], @"/Size\s+(\d+)").Groups[1].Value);
        long wrong = text.IndexOf(" 0 obj", StringComparison.Ordinal) - 1; // the first object of the file, not this one
        var sb = new StringBuilder(text);
        if (!text.EndsWith('\n')) sb.Append('\n');
        long xref = sb.Length;
        sb.Append($"xref\n{contents} 1\n{wrong:D10} 00000 n \ntrailer\n<< /Size {size} /Root {root} /Prev {prev} >>\nstartxref\n{xref}\n%%EOF\n");
        var checks = await PdfSignatureValidator.ValidateAsync(Encoding.Latin1.GetBytes(sb.ToString()));
        var check = Assert.Single(checks);
        Assert.True(check.IntegrityValid);
        Assert.NotEqual(PdfSignatureVerdict.Invalid, check.Verdict);
    }
}
