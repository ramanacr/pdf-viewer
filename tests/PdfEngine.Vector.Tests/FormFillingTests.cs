using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Pdfium;
using PdfEngine.Rendering;
using PdfEngine.Vector.Direct2D;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Interactive forms in the core: the field tree read with inheritance and full names; fields
/// filled with regenerated appearances; the result an incremental update that PDFium and this
/// engine both read back and draw alike.
/// </summary>
public class FormFillingTests
{
    private readonly ITestOutputHelper _output;
    public FormFillingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A page with: text field "person.name" (a parent with a widget kid), a multi-line text,
    /// a comb field, a check box with Yes/Off appearances, a two-button radio group, a combo box
    /// and a list box.
    /// </summary>
    internal static byte[] FormPdf()
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        int zadb = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>");
        // A drawn check mark (the state switch is what is tested, not the Dingbats font).
        int yes = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 14 14]", "q 0 g 2 7 m 6 3 l 12 12 l 10.5 13 l 6 5.5 l 3.5 8.5 l f Q");
        int off = b.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 14 14]", "");
        // Object numbers follow the page (added next): fields and widgets.
        int page = b.AddPage("0.9 g 0 0 300 400 re f", $"<< /Font << /Helv {helv} 0 R >> >>", mediaBox: "[0 0 300 400]",
            extra: "/Annots [PLACEHOLDER]");
        int person = page + 1, nameW = page + 2, notes = page + 3, zip = page + 4, agree = page + 5, colour = page + 6, red = page + 7, blue = page + 8, city = page + 9, fruit = page + 10, acro = page + 11;
        b.Add($"<< /FT /Tx /T (person) /Kids [{nameW} 0 R] >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /Parent {person} 0 R /T (name) /Rect [20 360 280 380] /P {page} 0 R /DA (/Helv 12 Tf 0 g) /MK << /BG [1] /BC [0] >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (notes) /Ff {1 << 12} /Rect [20 300 280 350] /P {page} 0 R /DA (/Helv 0 Tf 0 0 1 rg) >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (zip) /Ff {1 << 24} /MaxLen 5 /Rect [20 270 120 290] /P {page} 0 R /DA (/Helv 12 Tf 0 g) /Q 1 >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [20 240 34 254] /P {page} 0 R /V /Off /AS /Off /AP << /N << /Yes {yes} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /FT /Btn /Ff {1 << 15} /T (colour) /V /Off /Kids [{red} 0 R {blue} 0 R] >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /Parent {colour} 0 R /Rect [20 210 34 224] /P {page} 0 R /AS /Off /AP << /N << /Red {yes} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /Parent {colour} 0 R /Rect [50 210 64 224] /P {page} 0 R /AS /Off /AP << /N << /Blue {yes} 0 R /Off {off} 0 R >> >> >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff {1 << 17} /T (city) /Opt [[(PAR) (Paris)] [(ROM) (Rome)]] /V (PAR) /Rect [20 180 180 200] /P {page} 0 R /DA (/Helv 11 Tf 0 g) >>");
        b.Add($"<< /Type /Annot /Subtype /Widget /FT /Ch /T (fruit) /Opt [(Apple) (Banana) (Cherry)] /Rect [20 110 180 170] /P {page} 0 R /DA (/Helv 11 Tf 0 g) >>");
        b.Add($"<< /Fields [{person} 0 R {notes} 0 R {zip} 0 R {agree} 0 R {colour} 0 R {city} 0 R {fruit} 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {helv} 0 R /ZaDb {zadb} 0 R >> >> >>");
        b.WithCatalog($"/AcroForm {acro} 0 R");
        var pdf = b.Build();
        // The page's /Annots is patched once the widget numbers are known, and the cross-reference table rebuilt.
        string annots = $"{nameW} 0 R {notes} 0 R {zip} 0 R {agree} 0 R {red} 0 R {blue} 0 R {city} 0 R {fruit} 0 R";
        return Rebuild(pdf, annots);
    }

    /// <summary>Replaces the /Annots placeholder and rebuilds the cross-reference table.</summary>
    private static byte[] Rebuild(byte[] pdf, string annots)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        text = text.Replace("/Annots [PLACEHOLDER]", $"/Annots [{annots}]");
        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal) + 1;
        var body = text[..xref];
        var offsets = new System.Collections.Generic.List<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"(?m)^(\d+) 0 obj"))
            offsets.Add(m.Index);
        var sb = new System.Text.StringBuilder(body);
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        var trailerStart = text.IndexOf("trailer", xref, StringComparison.Ordinal);
        var trailer = text[trailerStart..text.IndexOf("startxref", trailerStart, StringComparison.Ordinal)];
        sb.Append(trailer).Append($"startxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public async Task Fields_AreReadWithFullNamesKindsAndValues()
    {
        using var doc = await PdfVectorDocument.OpenAsync(FormPdf());
        var form = PdfAcroForm.Read(doc)!;
        Assert.Equal(new[] { "person.name", "notes", "zip", "agree", "colour", "city", "fruit" }, form.Fields.Select(f => f.FullName));
        Assert.Equal(PdfFormFieldKind.Text, form["person.name"]!.Kind);
        Assert.True(form["notes"]!.IsMultiline);
        Assert.True(form["zip"]!.IsComb);
        Assert.Equal(PdfFormFieldKind.CheckBox, form["agree"]!.Kind);
        Assert.False(form["agree"]!.IsChecked);
        var colour = form["colour"]!;
        Assert.Equal(PdfFormFieldKind.RadioGroup, colour.Kind);
        Assert.Equal(new[] { "Red", "Blue" }, colour.Widgets.Select(w => w.OnState));
        Assert.Equal("PAR", form["city"]!.Value);
        Assert.Equal(PdfFormFieldKind.ComboBox, form["city"]!.Kind);
        Assert.Equal(3, form["fruit"]!.Options.Count);
        Assert.All(form.Fields.SelectMany(f => f.Widgets), w => Assert.Equal(1, w.PageNumber));
    }

    [Fact]
    public async Task Filling_WritesAnIncrementalUpdate_ThatBothEnginesReadBack()
    {
        byte[] original = FormPdf();
        byte[] filled;
        using (var doc = await PdfVectorDocument.OpenAsync(original))
        {
            filled = PdfFormFiller.Apply(doc, original, new[]
            {
                new PdfFieldChange("person.name", Text: "Ada Lovelace"),
                new PdfFieldChange("notes", Text: "First line of notes that is long enough to wrap onto a second line.\nAnd an explicit break."),
                new PdfFieldChange("zip", Text: "75001"),
                new PdfFieldChange("agree", Checked: true),
                new PdfFieldChange("colour", Choice: "Blue"),
                new PdfFieldChange("city", Choice: "ROM"),
                new PdfFieldChange("fruit", Selections: new[] { "Banana" }),
            });
        }
        // Incremental: the original bytes are a prefix of the result.
        Assert.True(filled.AsSpan(0, original.Length).SequenceEqual(original));

        using (var doc = await PdfVectorDocument.OpenAsync(filled))
        {
            Assert.False(doc.WasRepaired);
            var form = PdfAcroForm.Read(doc)!;
            Assert.Equal("Ada Lovelace", form["person.name"]!.Value);
            Assert.StartsWith("First line", form["notes"]!.Value);
            Assert.Equal("75001", form["zip"]!.Value);
            Assert.True(form["agree"]!.IsChecked);
            Assert.Equal("Blue", form["colour"]!.Value);
            Assert.Equal(new[] { "Off", "Blue" }, form["colour"]!.Widgets.Select(w => w.AppearanceState));
            Assert.Equal("ROM", form["city"]!.Value);
            Assert.Equal("Banana", form["fruit"]!.Value);
        }

        // PDFium reads the same values back. (PDFium's page rendering never draws widgets - only
        // FPDF_FFLDraw with a form environment does - so the drawing is checked on this engine.)
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(filled);
        var fields = await engine.FormService.GetFormFieldsAsync(pdoc, 1);
        Assert.Contains(fields, f => f.Value == "Ada Lovelace");

        using var vdoc = await PdfVectorDocument.OpenAsync(filled);
        var list = await vdoc.GetPageDisplayListAsync(1);
        using var renderer = new Direct2DVectorRenderer();
        var result = await renderer.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, CancellationToken.None);
        using var page = result.Page;
        int Ink(int x0, int y0Pdf, int x1, int y1Pdf)
        {
            // PDF y up (page 400 high) to image rows.
            int dark = 0;
            var span = page.Pixels.Span;
            for (int y = 400 - y1Pdf; y < 400 - y0Pdf; y++)
                for (int x = x0; x < x1; x++)
                    if (span[y * page.Stride + x * 4 + 1] < 100) dark++;
            return dark;
        }
        Assert.True(Ink(22, 362, 278, 378) > 60, "the name is drawn in its field");
        Assert.True(Ink(21, 241, 33, 253) > 10, "the check box shows its check");
        Assert.True(Ink(51, 211, 63, 223) > 10, "the chosen radio button shows its mark");
        Assert.True(Ink(21, 211, 33, 223) < 5, "the other radio button is off");
        _output.WriteLine($"name ink {Ink(22, 362, 278, 378)}, check {Ink(21, 241, 33, 253)}");
    }

    [Fact]
    public async Task UpdatesChain_AndTheLatestValueWins()
    {
        byte[] v1 = FormPdf();
        byte[] v2, v3;
        using (var doc = await PdfVectorDocument.OpenAsync(v1))
            v2 = PdfFormFiller.Apply(doc, v1, new[] { new PdfFieldChange("person.name", Text: "First") });
        using (var doc = await PdfVectorDocument.OpenAsync(v2))
            v3 = PdfFormFiller.Apply(doc, v2, new[] { new PdfFieldChange("person.name", Text: "Second (with parentheses) \\ and ü"), new PdfFieldChange("agree", Checked: true) });
        using var last = await PdfVectorDocument.OpenAsync(v3);
        var form = PdfAcroForm.Read(last)!;
        Assert.Equal("Second (with parentheses) \\ and ü", form["person.name"]!.Value);
        Assert.True(form["agree"]!.IsChecked);
        using var engine = new PdfiumEngine();
        await using var pdoc = await engine.OpenDocumentAsync(v3);
        Assert.Equal(1, pdoc.PageCount);
    }

    [Fact]
    public async Task ReadOnlyFields_AndUnknownRadioOptions_AreRefused()
    {
        byte[] original = FormPdf();
        using var doc = await PdfVectorDocument.OpenAsync(original);
        Assert.Throws<ArgumentException>(() => PdfFormFiller.Apply(doc, original, new[] { new PdfFieldChange("colour", Choice: "Green") }));
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => PdfFormFiller.Apply(doc, original, new[] { new PdfFieldChange("nope", Text: "x") }));
    }

    [Fact]
    public void Layout_WrapsWordsAndBreaksLongWords()
    {
        var metrics = new PdfFormFiller.FontMetrics(_ => 500, 800, -200);
        var lines = PdfFormFiller.Wrap("aaaa bbbb cccccccccccc", metrics, 10, 25);
        Assert.Equal(new[] { "aaaa", "bbbb", "ccccc", "ccccc", "cc" }, lines);
        var da = PdfFormFiller.ParseDA("/TiRo 9 Tf 0 0 1 rg");
        Assert.Equal(("TiRo", 9.0, "0 0 1 rg"), (da.FontName, da.Size, da.ColourOps));
    }
}
