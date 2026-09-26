using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Selection on real documents through PDFium's text page: characters, the spaces and line
/// breaks it infers, word double-click, copy, and multi-line highlights saved as one quad per line.
/// </summary>
public class TextSelectionEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TextSelectionE2E_" + Guid.NewGuid().ToString("N"));

    public TextSelectionEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string TwoLinePdf()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.AddPage("BT /F1 14 Tf 20 160 Td (The quick brown fox,) Tj 0 -20 Td (jumps over the lazy dog.) Tj ET",
            $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 300 200]");
        string path = Path.Combine(_dir, "two-lines.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<(MainViewModel Vm, PageViewModel Page)> Open(string path)
    {
        var vm = new MainViewModel();
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(path);
        var page = vm.Pages[0];
        await page.LoadTextSegmentsAsync(vm.DocumentService);
        return (vm, page);
    }

    private static Point CenterOf(PageViewModel page, int glyph)
    {
        var b = page.TextLayout.Glyphs[glyph].Box;
        return new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    [Fact]
    public async Task TheLayout_HasCharactersSpacesAndALineBreak()
    {
        var (_, page) = await Open(TwoLinePdf());
        var layout = page.TextLayout;
        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal("The quick brown fox," + Environment.NewLine + "jumps over the lazy dog.", layout.GetText(0, layout.Length));
        // Every character of a line shares the line's height (loose boxes): even selection bands.
        var line = layout.Lines[0];
        var heights = Enumerable.Range(line.Start, line.End - line.Start).Select(i => Math.Round(layout.Glyphs[i].Box.Height, 4)).Distinct();
        Assert.Single(heights);
    }

    [Fact]
    public async Task DoubleClick_SelectsOnlyTheWord_WithoutPunctuationOrSpace()
    {
        var (vm, page) = await Open(TwoLinePdf());
        int fox = page.TextLayout.GetText(0, page.TextLayout.Length).IndexOf("fox", StringComparison.Ordinal);
        vm.TextSelection.PointerDown(page, CenterOf(page, fox + 1), 2, false, false);
        Assert.Equal("fox", vm.SelectedText);
    }

    [Fact]
    public async Task ADragAcrossLines_CopiesWithALineBreak_AndHighlightsLineByLine()
    {
        string path = TwoLinePdf();
        var (vm, page) = await Open(path);
        string all = page.TextLayout.GetText(0, page.TextLayout.Length);
        int brown = all.IndexOf("brown", StringComparison.Ordinal);
        int over = all.IndexOf("over", StringComparison.Ordinal) - Environment.NewLine.Length + 1; // glyph index (break is one glyph)
        var start = page.TextLayout.Glyphs[brown].Box;
        var end = page.TextLayout.Glyphs[over + 3].Box;
        vm.TextSelection.PointerDown(page, new Point(start.Left + start.Width * 0.1, start.Y + start.Height / 2), 1, false, false);
        vm.TextSelection.PointerMove(page, new Point(end.Right - end.Width * 0.1, end.Y + end.Height / 2));
        Assert.Equal("brown fox," + Environment.NewLine + "jumps over", vm.SelectedText);
        Assert.Equal(2, page.SelectedSegments.Count);

        vm.HighlightSelectedText();
        var highlight = Assert.Single(vm.AllAnnotations);
        Assert.Equal(2, highlight.Quads.Count);
        Assert.True(highlight.HasQuads);
        Assert.NotNull(highlight.HighlightGeometry);

        // Saved as two quads and read back as two.
        await vm.SaveAsync();
        var (reopened, _) = await Open(path);
        var loaded = Assert.Single(reopened.AllAnnotations);
        Assert.Equal(2, loaded.Quads.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(highlight.Quads[i].X, loaded.Quads[i].X, 3);
            Assert.Equal(highlight.Quads[i].Y, loaded.Quads[i].Y, 3);
            Assert.Equal(highlight.Quads[i].Width, loaded.Quads[i].Width, 3);
        }
    }

    [Fact]
    public void MovingAMultiLineHighlight_CarriesItsLines()
    {
        var a = new AnnotationModel { Type = AnnotationType.Highlight };
        a.SetQuads(new[] { new Rect(0.1, 0.1, 0.5, 0.02), new Rect(0.1, 0.13, 0.3, 0.02) });
        Assert.Equal(0.1, a.X, 6);
        Assert.Equal(0.05, a.Height, 6);
        a.X += 0.1;
        a.Y += 0.2;
        Assert.Equal(0.2, a.Quads[1].X, 6);
        Assert.Equal(0.33, a.Quads[1].Y, 6);
    }
}
