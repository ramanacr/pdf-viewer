using PdfEngine.Vector.Create;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Text set in a system font with CFF outlines (Android's Noto Sans CJK is one) is saved with the
/// font as a CID-keyed CFF subset (FontFile3 /CIDFontType0C), and the viewer draws it from that program.
/// </summary>
public class CffSubstituteRenderTests
{
    [Fact]
    public async Task TextInACffFont_IsDrawnFromTheEmbeddedSubset()
    {
        string dir = Directory.CreateTempSubdirectory("cff-fonts").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "SyntheticCjk-Regular.otf"), CffOpenTypeFont.Font());
            using var ms = new MemoryStream();
            // 48 pt on a small page: 'A' is a 400 x 700 unit box, '中' an 800 x 800 one (1000 units per em).
            PdfTextDocument.Write("A中", ms, new PdfTextLayoutOptions { FontFamily = CffOpenTypeFont.Family, FontSize = 48, PageWidth = 200, PageHeight = 120, Margin = 20 },
                catalog: SystemFontCatalog.ForAndroid(dir));

            await using var doc = await ViewerDocument.OpenAsync(ms.ToArray());
            var bitmap = await doc.RenderPageAsync(1, 1.0);
            Assert.Equal(0, bitmap.TextRegionsWithoutFont);
            int ink = 0;
            var px = bitmap.Pixels.AsSpan();
            for (int i = 0; i < px.Length; i += 4)
                if (px[i] + px[i + 1] + px[i + 2] < 3 * 128) ink++;
            // The boxes are 19 x 34 and 38 x 38 pixels: about 2 100 pixels of ink.
            Assert.InRange(ink, 1700, 2600);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
