using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PdfEngine.Vector.Annotations;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Forms;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// samples/SampleDocument.pdf, the document the app opens as its sample and the store screenshots
/// show. It is written by <c>vectorpdf sampledoc</c> (eng/vectorpdf/tools/VectorPdf.Tool/SampleDocument.cs);
/// these tests keep it what the generator promises: seven pages with an outline, a form, comments and
/// searchable text, branded PDF Viewer Native.
/// </summary>
public class SampleDocumentTests
{
    private static string SamplePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
        return Path.Combine(root, "samples", "SampleDocument.pdf");
    }

    [Fact]
    public async Task SampleDocument_HasItsSectionsFormCommentsAndSearchableText()
    {
        byte[] bytes = await File.ReadAllBytesAsync(SamplePath());
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        Assert.Equal(7, doc.PageCount);

        var outline = (await doc.GetBookmarksAsync()).Select(b => b.Title).ToArray();
        Assert.Equal(new[] { "Cover", "Features at a glance", "Charts and shapes", "Keyboard shortcuts", "Fill in a form", "About PDF Viewer Native", "Comments and markup" }, outline);

        var text = new PdfVectorTextService();
        var all = new StringBuilder();
        for (int p = 1; p <= doc.PageCount; p++) all.AppendLine(await text.ExtractPageTextAsync(doc, p));
        string extracted = all.ToString();
        Assert.Contains("PDF Viewer Native", await text.ExtractPageTextAsync(doc, 1));
        Assert.Contains("•", extracted); // list bullets are real bullets (WinAnsi 0x95)
        Assert.DoesNotContain("Aspose", extracted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Aspose", Encoding.Latin1.GetString(bytes), StringComparison.OrdinalIgnoreCase);

        var form = PdfAcroForm.Read(doc);
        Assert.NotNull(form);
        var kinds = form!.Fields.ToDictionary(f => f.FullName, f => f.Kind);
        Assert.Equal(PdfFormFieldKind.Text, kinds["name"]);
        Assert.Equal(PdfFormFieldKind.ComboBox, kinds["feature"]);
        Assert.Equal(PdfFormFieldKind.CheckBox, kinds["windows"]);
        Assert.True(form.Fields.Single(f => f.FullName == "comments").IsMultiline);

        var comments = PdfAnnotationReader.Read(doc, 7);
        Assert.Equal(new[] { "Circle", "Highlight", "Square", "Text" }, comments.Select(a => a.Subtype).OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(comments, a => Assert.True(a.HasAppearance, $"{a.Subtype} has no appearance stream"));
        Assert.Equal(6, PdfAnnotationReader.Read(doc, 1).Count(a => a.Subtype == "Link"));
    }

    /// <summary>An installed app has no samples folder: it writes the copy it carries, which is the same file.</summary>
    [Fact]
    public void SamplePdfGenerator_WritesTheEmbeddedCopyOfTheSampleDocument()
    {
        string path = Path.Combine(Path.GetTempPath(), "PdfViewerTests-" + Guid.NewGuid().ToString("N"), "SampleDocument.pdf");
        try
        {
            Assert.Equal(path, SamplePdfGenerator.GenerateSamplePdf(path));
            Assert.Equal(File.ReadAllBytes(SamplePath()), File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
