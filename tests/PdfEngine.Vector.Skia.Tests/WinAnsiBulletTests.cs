using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// ISO 32000-1 Annex D, WinAnsiEncoding footnote: the codes WinAnsi leaves unused above 32 show a
/// bullet. Producers draw list bullets as 0x7F in non-embedded Helvetica; PDFium shows a bullet
/// there, so the Skia renderer must too.
/// </summary>
public class WinAnsiBulletTests
{
    private static byte[] Page(string code, string encoding = "/WinAnsiEncoding")
    {
        var b = new VectorPdfBuilder();
        int helv = b.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding {encoding} >>");
        b.AddPage($"BT /F0 72 Tf 20 20 Td ({code}) Tj ET", $"<< /Font << /F0 {helv} 0 R >> >>", mediaBox: "[0 0 120 120]");
        return b.Build();
    }

    private static async Task<byte[]> Render(byte[] pdf)
    {
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var list = await doc.GetPageDisplayListAsync(1);
        using var page = (await Parity.Skia.RenderAsync(list, new RenderRequest { PageNumber = 1, Dpi = 72 }, null, null, false, CancellationToken.None)).Page;
        return page.Pixels.ToArray();
    }

    private static int Ink(byte[] bgra)
    {
        int dark = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i] < 128 && bgra[i + 1] < 128 && bgra[i + 2] < 128) dark++;
        return dark;
    }

    [Theory]
    [InlineData(@"\177")] // 0x7F
    [InlineData(@"\201")] // 0x81
    [InlineData(@"\215")] // 0x8D
    [InlineData(@"\217")] // 0x8F
    [InlineData(@"\220")] // 0x90
    [InlineData(@"\235")] // 0x9D
    public async Task UndefinedWinAnsiCode_DrawsTheBullet(string code)
    {
        byte[] bullet = await Render(Page(@"\225")); // 0x95, the defined WinAnsi bullet
        byte[] undefined = await Render(Page(code));
        Assert.True(Ink(bullet) > 20, "The reference bullet drew no ink.");
        Assert.Equal(bullet, undefined);
    }

    [Fact]
    public async Task Differences_StillOverrideTheBullet()
    {
        byte[] h = await Render(Page("H"));
        byte[] overridden = await Render(Page(@"\177",
            "<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [127 /H] >>"));
        Assert.True(Ink(h) > 20, "The reference H drew no ink.");
        Assert.Equal(h, overridden);
    }
}
