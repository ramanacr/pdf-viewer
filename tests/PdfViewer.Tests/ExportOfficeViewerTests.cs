using System;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// File > Export > Excel Workbook and PowerPoint Presentation: the file is written where chosen,
/// opened only when the user says so, and a cancelled choice writes nothing. The menu has both.
/// </summary>
public class ExportOfficeViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ExportOffice_" + Guid.NewGuid().ToString("N"));
    public ExportOfficeViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Document()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var content = new System.Text.StringBuilder("BT /F1 14 Tf 72 740 Td (Exported words) Tj ET\n0.5 w\n");
        foreach (int y in new[] { 700, 680, 660, 640 }) content.Append($"72 {y} m 372 {y} l S\n");
        foreach (int x in new[] { 72, 222, 372 }) content.Append($"{x} 700 m {x} 640 l S\n");
        string[,] cells = { { "Item", "Cost" }, { "Paper", "$12.50" }, { "Ink", "$30.00" } };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 2; c++)
                content.Append($"BT /F1 10 Tf {76 + 150 * c} {686 - 20 * r} Td ({cells[r, c]}) Tj ET\n");
        b.AddPage(content.ToString(), $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportExcel_WritesTheWorkbook_AndOffersToOpenIt(bool open)
    {
        string target = Path.Combine(_dir, "out.xlsx");
        string? suggested = null, opened = null, asked = null;
        using var vm = new MainViewModel
        {
            ShowMessageBoxAction = (_, _, _, _) => { },
            ChooseExcelTargetFunc = s => { suggested = s; return target; },
            ConfirmFunc = (message, _) => { asked = message; return open; },
            OpenWithDefaultAppAction = p => opened = p,
        };
        await vm.LoadDocumentAsync(Document());
        await vm.ExportExcelCommand.ExecuteAsync(null);

        Assert.Equal("doc.xlsx", suggested);
        Assert.True(File.Exists(target));
        using (var package = Package.Open(target, FileMode.Open, FileAccess.Read))
        {
            Assert.True(package.PartExists(new Uri("/xl/workbook.xml", UriKind.Relative)));
            Assert.True(package.PartExists(new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative)));
        }
        Assert.Contains("Open it now?", asked);
        Assert.Equal(open ? target : null, opened);
        Assert.False(vm.IsExportingExcel);
        Assert.Contains("Excel", vm.StatusText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExportPowerPoint_WritesThePresentation_AndOffersToOpenIt(bool open)
    {
        string target = Path.Combine(_dir, "out.pptx");
        string? suggested = null, opened = null;
        using var vm = new MainViewModel
        {
            ShowMessageBoxAction = (_, _, _, _) => { },
            ChoosePowerPointTargetFunc = s => { suggested = s; return target; },
            ConfirmFunc = (_, _) => open,
            OpenWithDefaultAppAction = p => opened = p,
        };
        await vm.LoadDocumentAsync(Document());
        await vm.ExportPowerPointCommand.ExecuteAsync(null);

        Assert.Equal("doc.pptx", suggested);
        using (var package = Package.Open(target, FileMode.Open, FileAccess.Read))
            Assert.True(package.PartExists(new Uri("/ppt/slides/slide1.xml", UriKind.Relative)));
        Assert.Equal(open ? target : null, opened);
        Assert.False(vm.IsExportingPowerPoint);
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing()
    {
        using var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { }, ChooseExcelTargetFunc = _ => null, ChoosePowerPointTargetFunc = _ => null };
        await vm.LoadDocumentAsync(Document());
        await vm.ExportExcelCommand.ExecuteAsync(null);
        await vm.ExportPowerPointCommand.ExecuteAsync(null);
        Assert.Empty(Directory.GetFiles(_dir, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(_dir, "*.pptx"));
    }

    [Fact]
    public void ExportMenu_HasExcelAndPowerPoint_WithIcons()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "PdfViewer.slnx")) && Directory.GetParent(root) is { } parent) root = parent.FullName;
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var xaml = XDocument.Load(Path.Combine(root, "src", "PdfViewer", "Views", "MainWindow.xaml"));
        var export = xaml.Descendants(wpf + "MenuItem").Single(m => (string?)m.Attribute("Header") == "E_xport");
        var items = export.Elements(wpf + "MenuItem").ToDictionary(m => (string)m.Attribute("Command")!, m => m);
        Assert.Contains("{Binding ExportWordCommand}", items.Keys);
        foreach (var (command, glyph) in new[] { ("{Binding ExportExcelCommand}", "IconGlyphs.ExcelWorkbook"), ("{Binding ExportPowerPointCommand}", "IconGlyphs.PowerPointPresentation") })
        {
            var item = items[command];
            Assert.Contains(glyph, (string)item.Element(wpf + "MenuItem.Icon")!.Elements().Single().Attribute("Text")!);
        }
    }
}
