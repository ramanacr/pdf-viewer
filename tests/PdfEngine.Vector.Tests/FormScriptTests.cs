using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Objects;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Form scripts carried out without running JavaScript: Acrobat's AF functions for number,
/// percent, date, time and special formats, range validation, AFSimple_Calculate and
/// simplified field notation, in calculation order; custom code reported, never run.
/// </summary>
public class FormScriptTests
{
    private static async Task<PdfAcroForm> Form(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        return PdfAcroForm.Read(doc)!;
    }

    [Fact]
    public async Task Scripts_AreRecognised_NotRun()
    {
        var form = await Form(ScriptedFormFixture.Build());
        var price = PdfFormScripts.Read(form["price"]!);
        Assert.Equal(PdfFieldFormatKind.Number, price.Format);
        Assert.Equal(2, price.Decimals);
        Assert.Equal("$", price.Currency);
        Assert.Equal(0, price.RangeMin);
        Assert.Equal(1000, price.RangeMax);
        var total = PdfFormScripts.Read(form["total"]!);
        Assert.Equal("PRD", total.CalculateOperation);
        Assert.Equal(new[] { "qty", "price" }, total.CalculateFields);
        Assert.Equal("qty * price + 10", PdfFormScripts.Read(form["subtotal"]!).CalculateExpression);
        Assert.Equal(new[] { "total", "subtotal" }, form.CalculationOrder);
        Assert.Equal(new[] { "F" }, PdfFormScripts.Read(form["custom"]!).Unsupported);
        Assert.Empty(price.Unsupported);
    }

    [Theory]
    [InlineData(1234.5, 2, 0, "1,234.50")]
    [InlineData(1234.5, 2, 1, "1234.50")]
    [InlineData(1234.5, 2, 2, "1.234,50")]
    [InlineData(1234.5, 2, 3, "1234,50")]
    [InlineData(1234567.891, 0, 4, "1'234'568")]
    [InlineData(0.125, 2, 0, "0.13")]
    public void Numbers_AreFormattedLikeAcrobat(double value, int decimals, int sep, string expected)
    {
        Assert.Equal(expected, PdfFormScripts.FormatNumber((decimal)value, decimals, sep));
    }

    [Fact]
    public async Task Display_FollowsEachFormat()
    {
        var form = await Form(ScriptedFormFixture.Build());
        PdfFieldDisplay D(string field, string value) => PdfFormScripts.Display(PdfFormScripts.Read(form[field]!), value);
        Assert.Equal("$1,234.50", D("price", "1234.5").Text);
        Assert.Equal("3", D("qty", "3").Text);
        Assert.Equal("03-May-2026", D("date", "3/5/2026").Text); // read day first, as the pattern orders it
        Assert.Equal("(555) 123-4567", D("phone", "5551234567").Text);
        Assert.Equal("12.5%", D("pct", "0.125").Text);
        var negative = D("balance", "-1234.5");
        Assert.Equal("(1.234,50)", negative.Text);
        Assert.True(negative.Red);
        Assert.Equal("anything", D("custom", "anything").Text); // custom code: shown as typed
    }

    [Fact]
    public async Task Accept_ChecksKeystrokeAndRange()
    {
        var form = await Form(ScriptedFormFixture.Build());
        var price = PdfFormScripts.Read(form["price"]!);
        Assert.Equal(("12.5", (string?)null), PdfFormScripts.Accept(price, "$12.50"));
        Assert.Contains("enter a number", PdfFormScripts.Accept(price, "twelve").Error);
        Assert.Equal("Invalid value: must be greater than or equal to 0 and less than or equal to 1000.", PdfFormScripts.Accept(price, "1500").Error);
        Assert.Null(PdfFormScripts.Accept(PdfFormScripts.Read(form["date"]!), "5 March 2026").Error);
        Assert.NotNull(PdfFormScripts.Accept(PdfFormScripts.Read(form["date"]!), "not a date").Error);
        Assert.NotNull(PdfFormScripts.Accept(PdfFormScripts.Read(form["phone"]!), "12345").Error);
        Assert.False(PdfFormScripts.AllowsCharacter(PdfFormScripts.Read(form["qty"]!), 'x'));
        Assert.True(PdfFormScripts.AllowsCharacter(PdfFormScripts.Read(form["qty"]!), '7'));
    }

    [Theory]
    [InlineData("5/3/2026", "dd-mm-yyyy", 2026, 3, 5)]  // day first when the pattern says so
    [InlineData("3/5/26", "mm/dd/yy", 2026, 3, 5)]
    [InlineData("March 5, 2026", "mmmm d, yyyy", 2026, 3, 5)]
    [InlineData("5-mar-49", "d-mmm-yy", 2049, 3, 5)]
    [InlineData("5-mar-50", "d-mmm-yy", 1950, 3, 5)]
    public void Dates_AreReadInThePatternsOrder(string input, string pattern, int y, int m, int d)
    {
        Assert.Equal(new DateTime(y, m, d), PdfFormScripts.ParseDate(input, pattern));
    }

    [Fact]
    public void Times_AndDates_Format()
    {
        var t = new DateTime(2026, 3, 5, 14, 7, 9);
        Assert.Equal("2:07 pm", PdfFormScripts.FormatDate(t, "h:MM tt"));
        Assert.Equal("Thursday, March 05, 2026", PdfFormScripts.FormatDate(t, "dddd, mmmm dd, yyyy"));
        Assert.Equal(new DateTime(2000, 1, 1, 14, 7, 0), PdfFormScripts.ParseDate("2:07 pm", "h:MM tt"));
    }

    [Fact]
    public async Task Calculation_RunsInOrder_WithExactArithmetic()
    {
        var form = await Form(ScriptedFormFixture.Build());
        var values = new Dictionary<string, string> { ["qty"] = "3", ["price"] = "0.1" };
        var changes = PdfFormScripts.Recalculate(form, values).ToDictionary(c => c.Field, c => c.Value);
        Assert.Equal("0.3", changes["total"]);        // decimal, not 0.30000000000000004
        Assert.Equal("10.3", changes["subtotal"]);
        Assert.Null(PdfFormScripts.Evaluate("qty / 0", _ => new[] { 1m })); // Acrobat shows nothing
        Assert.Equal(7m, PdfFormScripts.Evaluate("(a + b) * 2 - 1", n => new[] { n == "a" ? 1m : 3m }));
        Assert.Equal(4m, PdfFormScripts.Evaluate(@"my\ field + 1", n => n == "my field" ? new[] { 3m } : Array.Empty<decimal>()));
    }

    [Fact]
    public async Task Prepare_ValidatesFormatsAndRecalculates_InOneRevision()
    {
        byte[] pdf = ScriptedFormFixture.Build();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var form = PdfAcroForm.Read(doc)!;
        Assert.Throws<PdfFieldValidationException>(() => PdfFormScripts.Prepare(form, new[] { new PdfFieldChange("price", Text: "5000") }));

        var prepared = PdfFormScripts.Prepare(form, new[] { new PdfFieldChange("qty", Text: "4"), new PdfFieldChange("price", Text: "$250.25") });
        Assert.Contains(prepared, c => c.FullName == "price" && c.Text == "250.25" && c.Display == "$250.25");
        Assert.Contains(prepared, c => c.FullName == "total" && c.Text == "1001" && c.Display == "$1,001.00");
        Assert.Contains(prepared, c => c.FullName == "subtotal" && c.Text == "1011");

        byte[] filled = PdfFormFiller.Apply(doc, pdf, prepared);
        using var after = await PdfVectorDocument.OpenAsync(filled);
        var read = PdfAcroForm.Read(after)!;
        Assert.Equal("1001", read["total"]!.Value);
        Assert.Equal("250.25", read["price"]!.Value);
        // The appearance shows the formatted value.
        var widget = (PdfDictionary)after.Resolver.Resolve(read["total"]!.Widgets[0].ObjectNumber)!;
        var ap = (PdfStream)after.Resolver.Resolve(((PdfDictionary)after.Resolver.Resolve(widget["AP"])!)["N"])!;
        Assert.Contains("($1,001.00) Tj", Encoding.Latin1.GetString(ap.GetRawBytes().Span));
    }

    [Fact]
    public void ParseCall_AcceptsOnlyAPlainCall()
    {
        Assert.NotNull(PdfFormScripts.ParseCall("AFNumber_Format(2,0,0,0,\"€\",false); // generated"));
        Assert.NotNull(PdfFormScripts.ParseCall("AFSimple_Calculate('SUM', ['a', 'b.c'])"));
        Assert.Null(PdfFormScripts.ParseCall("AFNumber_Format(2,0,0,0,\"\",true); app.alert('hi');"));
        Assert.Null(PdfFormScripts.ParseCall("this.getField('x').value = 1;"));
    }
}
