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

    [Fact]
    public async Task Fields_AreReadWithFullNamesKindsAndValues()
    {
        using var doc = await PdfVectorDocument.OpenAsync(FormPdfFixture.Build());
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
        byte[] original = FormPdfFixture.Build();
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
        byte[] v1 = FormPdfFixture.Build();
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
        byte[] original = FormPdfFixture.Build();
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
