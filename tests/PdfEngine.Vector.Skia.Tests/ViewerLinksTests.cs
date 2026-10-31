using System.Globalization;
using System.Text;
using PdfEngine.Vector.Services;
using PdfViewer.Web.Engine;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// Links in the engine layer the web and Android viewers share (ViewerDocument.GetLinksAsync,
/// ViewerLinks, ViewerHistory): areas on the shown (turned) page, hit testing from a shown point,
/// and where a link's destination is on the shown page. The PDF is built here.
/// </summary>
public class ViewerLinksTests
{
    private static byte[] Raw(params string[] objects)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// Three 300 x 400 pages; the first is turned (/Rotate 90) and carries the links: to an XYZ
    /// place on page 3, to the next page (a Named action), to a javascript: URI, to a FitH place
    /// on itself, and a web link.
    /// </summary>
    private static byte[] Linked() => Raw(
        "<< /Type /Catalog /Pages 2 0 R >>",                                                                   // 1
        "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 300 400] >>",                      // 2
        "<< /Type /Page /Parent 2 0 R /Rotate 90 /Annots [6 0 R 7 0 R 8 0 R 9 0 R 10 0 R] >>",                 // 3
        "<< /Type /Page /Parent 2 0 R >>",                                                                     // 4
        "<< /Type /Page /Parent 2 0 R >>",                                                                     // 5
        "<< /Type /Annot /Subtype /Link /Rect [10 10 60 30] /Dest [5 0 R /XYZ 50 350 0] >>",                    // 6
        "<< /Type /Annot /Subtype /Link /Rect [100 100 150 120] /A << /S /Named /N /NextPage >> >>",           // 7
        "<< /Type /Annot /Subtype /Link /Rect [100 200 150 220] /A << /S /URI /URI (javascript:alert\\(1\\)) >> >>", // 8
        "<< /Type /Annot /Subtype /Link /Rect [200 300 250 320] /Dest [3 0 R /FitH 300] >>",                    // 9
        "<< /Type /Annot /Subtype /Link /Rect [200 10 250 30] /A << /S /URI /URI (https://example.org/docs) >> >>"); // 10

    [Fact]
    public async Task GetLinks_AreasAreOnTheTurnedPage()
    {
        await using var doc = await ViewerDocument.OpenAsync(Linked());
        Assert.Equal((400.0, 300.0), (doc.Pages[0].Width, doc.Pages[0].Height));
        var links = await doc.GetLinksAsync(1);
        Assert.Equal(5, links.Count);
        // On a page turned 90 degrees, user space (x, y) shows at (y, x) from the top-left corner.
        var area = Assert.Single(links[0].Areas);
        Assert.Equal(10, area.X, 6);
        Assert.Equal(10, area.Y, 6);
        Assert.Equal(20, area.Width, 6);
        Assert.Equal(50, area.Height, 6);
        Assert.Equal("Go to page 3", links[0].Description);
        Assert.Empty(await doc.GetLinksAsync(2));
    }

    [Fact]
    public async Task At_FindsTheLinkUnderAShownPoint()
    {
        await using var doc = await ViewerDocument.OpenAsync(Linked());
        var links = await doc.GetLinksAsync(1);
        var g = doc.Geometry[0];
        Assert.Same(links[0], ViewerLinks.At(links, g, new ViewerPoint(20, 30)));
        Assert.Same(links[1], ViewerLinks.At(links, g, new ViewerPoint(110, 125)));
        Assert.Null(ViewerLinks.At(links, g, new ViewerPoint(5, 5)));
        // A finger's slack reaches a link a few points away.
        Assert.Null(ViewerLinks.At(links, g, new ViewerPoint(33, 30)));
        Assert.Same(links[0], ViewerLinks.At(links, g, new ViewerPoint(33, 30), slack: 4));
    }

    [Fact]
    public async Task Destination_IsThePlaceOnTheShownTargetPage()
    {
        await using var doc = await ViewerDocument.OpenAsync(Linked());
        var links = await doc.GetLinksAsync(1);

        // XYZ 50 350 on the upright page 3: 50 from the left, 400 - 350 from the top.
        Assert.Equal(new ViewerLocation(3, 50, 50), ViewerLinks.Destination(links[0].Target, 1, doc.Geometry));

        // NextPage from page 1, and from the last page (it stays).
        Assert.Equal(PdfLinkTargetKind.NextPage, links[1].Target.Kind);
        Assert.Equal(new ViewerLocation(2), ViewerLinks.Destination(links[1].Target, 1, doc.Geometry));
        Assert.Equal(new ViewerLocation(3), ViewerLinks.Destination(links[1].Target, 3, doc.Geometry));

        // A javascript: URI is shown, never opened, and goes nowhere in the document.
        Assert.Equal(PdfLinkTargetKind.BlockedUri, links[2].Target.Kind);
        Assert.Null(ViewerLinks.Destination(links[2].Target, 1, doc.Geometry));

        // FitH 300 on the turned page 1: user-space y runs across the shown page, so it gives x only.
        var turned = ViewerLinks.Destination(links[3].Target, 1, doc.Geometry);
        Assert.Equal(new ViewerLocation(1, 300, null), turned);

        Assert.Equal(PdfLinkTargetKind.WebUri, links[4].Target.Kind);
        Assert.Equal("https://example.org/docs", links[4].Target.Uri);
        Assert.Null(ViewerLinks.Destination(links[4].Target, 1, doc.Geometry));
    }

    [Fact]
    public async Task GetLinks_RejectsAPageOutsideTheDocument()
    {
        await using var doc = await ViewerDocument.OpenAsync(Linked());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.GetLinksAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => doc.GetLinksAsync(4));
    }

    [Fact]
    public void History_IsBoundedAndNewestFirst()
    {
        var history = new ViewerHistory<int>(capacity: 2);
        Assert.False(history.CanGoBack);
        history.Push(1);
        history.Push(2);
        history.Push(3);
        Assert.Equal(2, history.Count);
        Assert.True(history.TryPop(out int a));
        Assert.True(history.TryPop(out int b));
        Assert.False(history.TryPop(out _));
        Assert.Equal((3, 2), (a, b));
    }
}
