using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// PDF/UA-1 (ISO 14289-1): the checker reports each machine-checkable Matterhorn failure condition
/// under its checkpoint and clause, with the page, object and structure path it is about. Each test
/// starts from a conforming document and breaks one thing.
/// </summary>
public class PdfUaTests
{
    private const string Helv = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>A tagged figure (a filled square) and a decorative artifact line.</summary>
    private const string FigureContent = "/Figure <</MCID 0>> BDC 0 0 1 rg 20 20 50 50 re f EMC /Artifact BMC 0 0 0 RG 0 0 m 200 0 l S EMC";

    private static string Xmp(string part = "1", string? title = "Checker fixture", string prefix = "pdfuaid") =>
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        $"<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:{prefix}=\"http://www.aiim.org/pdfua/ns/id/\">" +
        (title == null ? "" : $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>") +
        (part == "" ? "" : $"<{prefix}:part>{part}</{prefix}:part>") +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>What a fixture varies: the page, the Document element's kids, and the catalog.</summary>
    private sealed class Spec
    {
        public string Content = FigureContent;
        public string Resources = "<< >>";
        public string PageExtra = "/Tabs /S";
        /// <summary>Adds the structure elements (given the page object) and returns the Document element's /K items.</summary>
        public Func<VectorPdfBuilder, int, string> Kids = (b, page) => $"{b.Add($"<< /Type /StructElem /S /Figure /Pg {page} 0 R /K 0 /Alt (A blue square) >>")} 0 R";
        public string RootExtra = string.Empty;
        public string? Xmp = PdfUaTests.Xmp();
        public string Lang = "/Lang (en-US)";
        public string MarkInfo = "/MarkInfo << /Marked true >>";
        public string ViewerPreferences = "/ViewerPreferences << /DisplayDocTitle true >>";
        public string CatalogExtra = string.Empty;
        public bool StructTree = true;
        public PdfTestEncryption? Encryption;
        /// <summary>For the annotation fixtures: the annotation, and the element that tags it.</summary>
        public string? Annot;
        public Func<VectorPdfBuilder, int, int, string>? AnnotTag;
    }

    private static byte[] Build(Spec s)
    {
        var b = new VectorPdfBuilder();
        int page = b.AddPage(s.Content, s.Resources, extra: "/StructParents 0 " + s.PageExtra);
        string kids = s.Kids(b, page);
        int doc = b.Add($"<< /Type /StructElem /S /Document /K [{kids}] >>");
        int root = b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R {s.RootExtra} >>");
        string meta = s.Xmp == null ? string.Empty : $"/Metadata {b.AddStream("/Type /Metadata /Subtype /XML", s.Xmp)} 0 R";
        b.WithCatalog($"{s.MarkInfo} {(s.StructTree ? $"/StructTreeRoot {root} 0 R" : "")} {s.Lang} {s.ViewerPreferences} {meta} {s.CatalogExtra}");
        return s.Encryption != null ? b.Build(s.Encryption) : b.Build();
    }

    private static async Task<PdfUaReport> Check(Spec s)
    {
        byte[] pdf = Build(s);
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var report = PdfUaValidator.Validate(doc, pdf);
        // Every issue explains itself and how to fix it.
        Assert.All(report.Issues, i => Assert.False(string.IsNullOrEmpty(i.Fix), $"{i.Checkpoint} has no fix text"));
        return report;
    }

    private static PdfUaIssue Single(PdfUaReport report, string checkpoint) =>
        Assert.Single(report.Errors, i => i.Checkpoint == checkpoint);

    private static void Has(PdfUaReport report, string checkpoint) =>
        Assert.Contains(report.Errors, i => i.Checkpoint == checkpoint);

    private static string Elem(VectorPdfBuilder b, string type, int page, string rest = "") =>
        $"{b.Add($"<< /Type /StructElem /S /{type} /Pg {page} 0 R {rest} >>")} 0 R";

    [Fact]
    public async Task ConformingDocument_HasNoErrors()
    {
        var report = await Check(new Spec());
        Assert.True(report.IsCompliant, string.Join("; ", report.Errors));
        Assert.True(report.ClaimsPdfUa);
    }

    // ------------------------------------------------------------------ document

    [Fact]
    public async Task Document_TaggingMetadataAndTitle()
    {
        Has(await Check(new Spec { MarkInfo = "" }), "01-005");
        Has(await Check(new Spec { StructTree = false }), "01-005");
        Has(await Check(new Spec { MarkInfo = "/MarkInfo << /Marked true /Suspects true >>" }), "01-007");
        Has(await Check(new Spec { Xmp = null }), "06-001");
        Has(await Check(new Spec { Xmp = Xmp(part: "") }), "06-002");
        Has(await Check(new Spec { Xmp = Xmp(part: "2") }), "06-002");
        Has(await Check(new Spec { Xmp = Xmp(prefix: "pdfuaia") }), "06-002");
        Has(await Check(new Spec { Xmp = Xmp(title: null) }), "06-003");
        Has(await Check(new Spec { ViewerPreferences = "" }), "07-001");
        Has(await Check(new Spec { ViewerPreferences = "/ViewerPreferences << /DisplayDocTitle false >>" }), "07-002");
        var noMeta = await Check(new Spec { Xmp = null });
        Assert.All(noMeta.Issues.Where(i => i.Checkpoint.StartsWith("06", StringComparison.Ordinal)), i => Assert.Equal(PdfUaCategory.Document, i.Category));
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("portugue", true)]
    [InlineData("x-1234abcd", true)]
    [InlineData("", false)]
    [InlineData("portugues", false)]
    [InlineData("1en", false)]
    [InlineData("en-1234abcde", false)]
    [InlineData("пт", false)]
    public void LanguageTags(string tag, bool valid) => Assert.Equal(valid, PdfUaValidator.IsValidLanguageTag(tag));

    [Fact]
    public async Task Language_MustBeDeterminableAndValid()
    {
        var invalid = await Check(new Spec { Lang = "/Lang (en_US)" });
        Assert.Equal("7.2", Single(invalid, "11-001").Clause);

        // Without a document language, the figure's alternate text has none, unless the figure gives one.
        var none = await Check(new Spec { Lang = "" });
        Has(none, "11-002");
        Assert.Contains(none.Warnings, i => i.Checkpoint == "11-001");
        var own = await Check(new Spec { Lang = "", Kids = (b, p) => Elem(b, "Figure", p, "/K 0 /Alt (A square) /Lang (en-GB)") });
        Assert.DoesNotContain(own.Errors, i => i.Checkpoint == "11-002");

        // Text on the page needs a language too.
        var text = await Check(new Spec
        {
            Lang = "",
            Content = "/P <</MCID 0>> BDC BT /F1 12 Tf 20 100 Td (Hello) Tj ET EMC",
            Resources = $"<< /Font << /F1 {Helv} >> >>",
            Kids = (b, p) => Elem(b, "P", p, "/K 0"),
        });
        var page = Assert.Single(text.Errors, i => i.Checkpoint == "11-001");
        Assert.Equal(1, page.Page);
        Assert.Equal(PdfUaCategory.PageContent, page.Category);
    }

    [Fact]
    public async Task Security_AccessibilityPermissionIsRequired()
    {
        var blocked = await Check(new Spec { Encryption = new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner", permissions: -4 & ~(1 << 9)) });
        Assert.Equal("7.16", Single(blocked, "26-002").Clause);
        var allowed = await Check(new Spec { Encryption = new PdfTestEncryption(TestEncryptionKind.Aes128_R4, "", "owner", permissions: -4) });
        Assert.DoesNotContain(allowed.Errors, i => i.Checkpoint.StartsWith("26", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptionalContent_EmbeddedFiles_Xfa()
    {
        Has(await Check(new Spec { CatalogExtra = "/OCProperties << /OCGs [] /D << /Order [] >> >>" }), "20-002");
        Has(await Check(new Spec { CatalogExtra = "/OCProperties << /OCGs [] /D << /Name (Default) /AS [] >> >>" }), "20-003");
        Has(await Check(new Spec { CatalogExtra = "/OCProperties << /OCGs [] /D << /Name (Default) >> /Configs [<< /Order [] >>] >>" }), "20-001");
        Assert.True((await Check(new Spec { CatalogExtra = "/OCProperties << /OCGs [] /D << /Name (Default) >> >>" })).IsCompliant);

        var spec = new Spec();
        spec.Kids = (b, p) =>
        {
            int file = b.AddStream("/Type /EmbeddedFile", "hello");
            b.Add($"<< /Type /Filespec /F (a.txt) /EF << /F {file} 0 R >> >>");
            return Elem(b, "Figure", p, "/K 0 /Alt (A blue square)");
        };
        Assert.Equal("7.11", Single(await Check(spec), "21-001").Clause);

        var xfa = new Spec();
        // The catalog is written after the kids, so the kids can add the XFA stream it refers to.
        xfa.Kids = (b, p) =>
        {
            int config = b.AddStream(string.Empty, "<config><acrobat><acrobat7><dynamicRender\n>required</dynamicRender\n></acrobat7></acrobat></config>");
            xfa.CatalogExtra = $"/AcroForm << /Fields [] /XFA [(config) {config} 0 R] >>";
            return Elem(b, "Figure", p, "/K 0 /Alt (A blue square)");
        };
        var dynamic = await Check(xfa);
        Assert.Equal(PdfUaCategory.Forms, Single(dynamic, "25-001").Category);
    }

    // ------------------------------------------------------------------ page content

    [Fact]
    public async Task Content_UntaggedIsFoundWithItsBounds()
    {
        var report = await Check(new Spec { Content = FigureContent + " 1 0 0 rg 100 120 40 30 re f" });
        var issue = Single(report, "01-005");
        Assert.Equal(1, issue.Page);
        Assert.Equal(PdfUaCategory.PageContent, issue.Category);
        var b = Assert.NotNull(issue.Bounds);
        Assert.Equal(100, b.X, 3);
        Assert.Equal(120, b.Y, 3);
        Assert.Equal(40, b.Width, 3);
        // Normalized from the crop box's top-left: the box's top is at 150 of 200.
        var n = Assert.NotNull(issue.NormalizedBounds);
        Assert.Equal(0.5, n.X, 3);
        Assert.Equal(0.25, n.Y, 3);
    }

    [Fact]
    public async Task Content_ArtifactsAndTagsDoNotNest()
    {
        Has(await Check(new Spec { Content = "/Figure <</MCID 0>> BDC /Artifact BMC 0 0 m 10 10 l S EMC 20 20 50 50 re f EMC" }), "01-003");
        Has(await Check(new Spec { Content = "/Artifact BMC /Figure <</MCID 0>> BDC 20 20 50 50 re f EMC EMC" }), "01-004");
        // An MCID the structure tree does not know is not tagged.
        var orphan = await Check(new Spec { Content = FigureContent + " /P <</MCID 7>> BDC 5 5 5 5 re f EMC" });
        Assert.Contains("MCID 7", Single(orphan, "01-005").Message);
        // A sequence without an MCID is neither: artifacts inside it are fine.
        Assert.True((await Check(new Spec { Content = FigureContent + " /P BMC /Artifact BMC 5 5 5 5 re f EMC EMC" })).IsCompliant);
    }

    [Fact]
    public async Task Content_FormXObjects()
    {
        var tagged = await CheckWithForm("/Figure <</MCID 0>> BDC 0 0 10 10 re f EMC", "q /X Do Q q 1 0 0 1 50 0 cm /X Do Q", withRef: false);
        Has(tagged, "30-002");
        var once = await CheckWithForm("0 0 10 10 re f", "/Figure <</MCID 0>> BDC /X Do EMC", withRef: false);
        Assert.True(once.IsCompliant, string.Join("; ", once.Errors));
        Has(await CheckWithForm("0 0 10 10 re f", "/Figure <</MCID 0>> BDC /X Do EMC", withRef: true), "30-001");
    }

    /// <summary>A page that draws the form XObject /X with <paramref name="pageContent"/>; a form with an MCID is tagged by a Figure.</summary>
    private static async Task<PdfUaReport> CheckWithForm(string formContent, string pageContent, bool withRef)
    {
        var b = new VectorPdfBuilder();
        int form = b.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 10 10] {(withRef ? "/Ref << /F (other.pdf) /Page 0 >>" : "")}", formContent);
        int page = b.AddPage(pageContent, $"<< /XObject << /X {form} 0 R >> >>", extra: "/StructParents 0 /Tabs /S");
        string mcr = formContent.Contains("MCID") ? $"<< /Type /MCR /MCID 0 /Stm {form} 0 R /Pg {page} 0 R >>" : "0";
        int fig = b.Add($"<< /Type /StructElem /S /Figure /Pg {page} 0 R /K {mcr} /Alt (A square) >>");
        int doc = b.Add($"<< /Type /StructElem /S /Document /K [{fig} 0 R] >>");
        int root = b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R >>");
        int meta = b.AddStream("/Type /Metadata /Subtype /XML", Xmp());
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R /Lang (en) /ViewerPreferences << /DisplayDocTitle true >> /Metadata {meta} 0 R");
        byte[] pdf = b.Build();
        using var doc2 = await PdfVectorDocument.OpenAsync(pdf);
        return PdfUaValidator.Validate(doc2, pdf);
    }

    // ------------------------------------------------------------------ structure

    [Fact]
    public async Task RoleMap_MustEndAtStandardTypesWithoutCycles()
    {
        Spec With(string type, string roleMap) => new() { RootExtra = $"/RoleMap << {roleMap} >>", Kids = (b, p) => Elem(b, type, p, "/K 0 /Alt (A blue square)") };
        Assert.True((await Check(With("Picture", "/Picture /Graphic /Graphic /Figure"))).IsCompliant);
        Has(await Check(With("Picture", "/Picture /picture2")), "02-001");
        Has(await Check(With("Picture", "/Picture /Graphic /Graphic /Picture")), "02-003");
        Has(await Check(With("Figure", "/Figure /Picture /Picture /Figure")), "02-004");
        Has(await Check(With("Picture", "")), "02-001");
    }

    [Fact]
    public async Task Figures_And_Formulas_NeedAlternateText()
    {
        var figure = Single(await Check(new Spec { Kids = (b, p) => Elem(b, "Figure", p, "/K 0") }), "13-004");
        Assert.Equal(PdfUaCategory.AlternateText, figure.Category);
        Assert.Equal("Document > Figure", figure.StructurePath);
        Assert.Equal(1, figure.Page);
        Assert.NotNull(figure.ObjectNumber);
        Assert.NotNull(figure.NormalizedBounds);
        Has(await Check(new Spec { Kids = (b, p) => Elem(b, "Figure", p, "/K 0 /Alt ()") }), "13-004");
        Assert.True((await Check(new Spec { Kids = (b, p) => Elem(b, "Figure", p, "/K 0 /ActualText ()") })).IsCompliant);
        Assert.Equal("7.7", Single(await Check(new Spec { Kids = (b, p) => Elem(b, "Formula", p, "/K 0") }), "17-002").Clause);
    }

    [Fact]
    public async Task Notes_NeedUniqueIds()
    {
        Spec Notes(string a, string b) => new()
        {
            Kids = (x, p) => $"{Elem(x, "Figure", p, "/K 0 /Alt (A blue square)")} {Elem(x, "Note", p, a)} {Elem(x, "Note", p, b)}",
        };
        Assert.True((await Check(Notes("/ID (n1)", "/ID (n2)"))).IsCompliant);
        Has(await Check(Notes("/ID (n1)", "")), "19-003");
        Has(await Check(Notes("/ID (n1)", "/ID (n1)")), "19-004");
    }

    /// <summary>A two by two table: a header row of <paramref name="headerScope"/> TH cells, then a row of TD cells.</summary>
    private static Spec Table(string headerScope, string secondRow = "TD", string extraRow = "")
    {
        return new Spec
        {
            Kids = (b, p) =>
            {
                string scope = headerScope.Length == 0 ? "" : $"/A << /O /Table /Scope /{headerScope} >>";
                string cell(string t, string a = "") => Elem(b, t, p, $"/K [] {a}");
                string tr1 = Elem(b, "TR", p, $"/K [{Elem(b, "TH", p, $"/K 0 {scope}")} {cell("TH", scope)}]");
                string tr2 = Elem(b, "TR", p, $"/K [{Elem(b, secondRow, p, "/K 1")} {Elem(b, secondRow, p, "/K 2")}]");
                return $"{Elem(b, "Table", p, $"/K [{tr1} {tr2} {extraRow}]")}";
            },
            Content = "/TH <</MCID 0>> BDC 10 10 5 5 re f EMC /TD <</MCID 1>> BDC 20 10 5 5 re f EMC /TD <</MCID 2>> BDC 30 10 5 5 re f EMC",
        };
    }

    [Fact]
    public async Task Tables_CellsNeedHeadersAndRegularRows()
    {
        Assert.True((await Check(Table("Column"))).IsCompliant, string.Join("; ", (await Check(Table("Column"))).Errors));
        var noScope = await Check(Table(""));
        var cell = noScope.Errors.First(i => i.Checkpoint == "15-005");
        Assert.Equal(PdfUaCategory.Tables, cell.Category);
        Assert.Equal("Document > Table > TR[2] > TD", cell.StructurePath);
        Has(noScope, "15-003");
        // Rows of different widths.
        Has(await Check(new Spec
        {
            Content = Table("Column").Content,
            Kids = (b, p) => Elem(b, "Table", p,
                $"/K [{Elem(b, "TR", p, $"/K [{Elem(b, "TH", p, "/K 0 /A << /O /Table /Scope /Column >>")}]")} {Elem(b, "TR", p, $"/K [{Elem(b, "TD", p, "/K 1")} {Elem(b, "TD", p, "/K 2")}]")}]"),
        }), "09-004");
        // Structure: a row outside any table, and a paragraph inside one.
        Has(await Check(new Spec { Kids = (b, p) => $"{Elem(b, "Figure", p, "/K 0 /Alt (A blue square)")} {Elem(b, "TR", p, "/K []")}" }), "09-004");
        Has(await Check(new Spec { Kids = (b, p) => Elem(b, "Table", p, $"/K [{Elem(b, "P", p, "/K 0")}]") }), "09-004");
    }

    [Fact]
    public async Task Lists_FollowTheListSyntax()
    {
        const string content = "/Lbl <</MCID 0>> BDC 10 10 5 5 re f EMC /LBody <</MCID 1>> BDC 20 10 5 5 re f EMC";
        string Item(VectorPdfBuilder b, int p) => Elem(b, "LI", p, $"/K [{Elem(b, "Lbl", p, "/K 0")} {Elem(b, "LBody", p, "/K 1")}]");
        Assert.True((await Check(new Spec { Content = content, Kids = (b, p) => Elem(b, "L", p, $"/K [{Item(b, p)}]") })).IsCompliant);
        var loose = await Check(new Spec { Content = content, Kids = (b, p) => Item(b, p) });
        var issue = Single(loose, "09-005");
        Assert.Equal(PdfUaCategory.Lists, issue.Category);
        Assert.Equal("Document > LI", issue.StructurePath);
        Has(await Check(new Spec { Content = content, Kids = (b, p) => Elem(b, "L", p, $"/K [{Elem(b, "LBody", p, "/K [0 1]")}]") }), "09-005");
        Has(await Check(new Spec { Content = content, Kids = (b, p) => Elem(b, "L", p, $"/K [{Item(b, p)} {Elem(b, "Caption", p, "/K []")}]") }), "09-005");
    }

    [Fact]
    public async Task Headings_StartAtH1AndSkipNoLevel()
    {
        Spec Headings(params string[] types) => new()
        {
            Kids = (b, p) => Elem(b, "Figure", p, "/K 0 /Alt (A blue square)") + " " + string.Join(" ", types.Select(t => Elem(b, t, p, "/K []"))),
        };
        Assert.True((await Check(Headings("H1", "H2", "H3", "H2", "H3"))).IsCompliant);
        var first = Single(await Check(Headings("H2", "H3")), "14-002");
        Assert.Equal(PdfUaCategory.Headings, first.Category);
        Assert.Equal("7.4.2", first.Clause);
        Has(await Check(Headings("H1", "H3")), "14-003");
        Has(await Check(Headings("H", "H1")), "14-007");
        Has(await Check(Headings("H", "H")), "14-006");
    }

    // ------------------------------------------------------------------ fonts

    [Fact]
    public async Task Fonts_MustBeEmbedded()
    {
        var report = await Check(new Spec
        {
            Content = "/P <</MCID 0>> BDC BT /F1 12 Tf 20 100 Td (Hello) Tj ET EMC",
            Resources = $"<< /Font << /F1 {Helv} >> >>",
            Kids = (b, p) => Elem(b, "P", p, "/K 0"),
        });
        var font = Single(report, "31-008");
        Assert.Equal(PdfUaCategory.Fonts, font.Category);
        Assert.Equal("7.21.4.1", font.Clause);
        Assert.Equal(1, font.Page);
    }

    // ------------------------------------------------------------------ annotations and forms

    /// <summary>The conforming page with one annotation, tagged by <paramref name="tag"/> (given the page and annotation objects) or not at all.</summary>
    private static Spec Annotated(string annot, Func<VectorPdfBuilder, int, int, string>? tag = null, string tabs = "/Tabs /S") =>
        new() { Annot = annot, AnnotTag = tag, PageExtra = tabs };

    [Fact]
    public async Task Annotations_AreTaggedAndDescribed()
    {
        const string note = "<< /Type /Annot /Subtype /Text /Rect [100 100 120 120] /Contents (Remember) /StructParent 1 >>";
        string InAnnot(VectorPdfBuilder b, int p, int a) => Elem(b, "Annot", p, $"/K << /Type /OBJR /Obj {a} 0 R >>");
        Assert.True((await CheckAnnotated(Annotated(note, InAnnot))).IsCompliant);
        var untagged = await CheckAnnotated(Annotated(note));
        var issue = Single(untagged, "28-002");
        Assert.Equal(PdfUaCategory.Annotations, issue.Category);
        Assert.Equal(1, issue.Page);
        Assert.NotNull(issue.ObjectNumber);
        Assert.Equal(100, Assert.NotNull(issue.Bounds).X, 3);
        Has(await CheckAnnotated(Annotated(note.Replace("/Contents (Remember)", ""), InAnnot)), "28-004");
        Has(await CheckAnnotated(Annotated(note, InAnnot, tabs: "")), "28-008");
        Has(await CheckAnnotated(Annotated(note, InAnnot, tabs: "/Tabs /R")), "28-009");
        // Hidden annotations are not presented.
        Assert.True((await CheckAnnotated(Annotated(note.Replace("/Contents (Remember)", "/F 2"), InAnnot))).IsCompliant);
        Has(await CheckAnnotated(Annotated("<< /Type /Annot /Subtype /TrapNet /Rect [0 0 10 10] >>")), "28-007");
    }

    [Fact]
    public async Task Links_AreInLinkElementsWithDescriptions()
    {
        const string link = "<< /Type /Annot /Subtype /Link /Rect [10 10 60 30] /Contents (Opens the index) /A << /S /URI /URI (https://example.org) >> >>";
        string InLink(VectorPdfBuilder b, int p, int a) => Elem(b, "Link", p, $"/K << /Type /OBJR /Obj {a} 0 R >>");
        Assert.True((await CheckAnnotated(Annotated(link, InLink))).IsCompliant);
        Has(await CheckAnnotated(Annotated(link, (b, p, a) => Elem(b, "P", p, $"/K << /Type /OBJR /Obj {a} 0 R >>"))), "28-011");
        Assert.Equal("7.18.5", Single(await CheckAnnotated(Annotated(link.Replace("/Contents (Opens the index)", ""), InLink)), "28-012").Clause);
    }

    [Fact]
    public async Task FormFields_AreInFormElementsWithTooltips()
    {
        const string field = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /TU (Your name) /Rect [10 10 110 30] >>";
        string InForm(VectorPdfBuilder b, int p, int a) => Elem(b, "Form", p, $"/K << /Type /OBJR /Obj {a} 0 R >>");
        Assert.True((await CheckAnnotated(Annotated(field, InForm))).IsCompliant);
        var untagged = Single(await CheckAnnotated(Annotated(field)), "28-010");
        Assert.Equal(PdfUaCategory.Forms, untagged.Category);
        Has(await CheckAnnotated(Annotated(field.Replace("/TU (Your name)", ""), InForm)), "28-005");
    }

    private static async Task<PdfUaReport> CheckAnnotated(Spec spec)
    {
        // The annotation comes first so that the page can list it and the structure can refer to both.
        var b = new VectorPdfBuilder();
        int content = b.AddStream(string.Empty, spec.Content);
        int annot = b.Add(spec.Annot!);
        int page = b.AddPageObject($"<< /Type /Page /Parent {{0}} 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents {content} 0 R /StructParents 0 /Annots [{annot} 0 R] {spec.PageExtra} >>");
        string kids = Elem(b, "Figure", page, "/K 0 /Alt (A blue square)") + (spec.AnnotTag == null ? "" : " " + spec.AnnotTag(b, page, annot));
        int doc = b.Add($"<< /Type /StructElem /S /Document /K [{kids}] >>");
        int root = b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R >>");
        int meta = b.AddStream("/Type /Metadata /Subtype /XML", Xmp());
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R /Lang (en) /ViewerPreferences << /DisplayDocTitle true >> /Metadata {meta} 0 R");
        byte[] pdf = b.Build();
        using var d = await PdfVectorDocument.OpenAsync(pdf);
        var report = PdfUaValidator.Validate(d, pdf);
        Assert.All(report.Issues, i => Assert.False(string.IsNullOrEmpty(i.Fix)));
        return report;
    }

    // ------------------------------------------------------------------ privacy

    [Fact]
    public async Task Messages_NeverQuoteDocumentText()
    {
        var report = await Check(new Spec
        {
            Lang = "",
            Content = "/P <</MCID 0>> BDC BT /F1 12 Tf 20 100 Td (Confidential salary) Tj ET EMC 5 5 5 5 re f",
            Resources = $"<< /Font << /F1 {Helv} >> >>",
            Kids = (b, p) => $"{Elem(b, "P", p, "/K 0 /Alt (Confidential salary)")} {Elem(b, "Figure", p, "/K [] /ActualText (Confidential salary)")}",
            Xmp = Xmp(title: "Confidential salary"),
        });
        Assert.NotEmpty(report.Issues);
        Assert.All(report.Issues, i => Assert.DoesNotContain("Confidential", i.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
