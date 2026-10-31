using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PdfEngine.Vector.Services;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Links on pages in the desktop viewer: hit testing in the overlay space, following a link to a
/// place (with Back returning to the exact view), the named page actions, the web-link question
/// (through a fake opener: no browser is ever started), and links that are never followed.
/// </summary>
public class PageLinkViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PdfLinkTests_" + Guid.NewGuid().ToString("N"));

    public PageLinkViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeOpener : IExternalLinkOpener
    {
        public bool Answer { get; set; }
        public List<string> Asked { get; } = new();
        public List<Uri> Opened { get; } = new();

        public bool Confirm(Uri uri)
        {
            Asked.Add(uri.AbsoluteUri);
            return Answer;
        }

        public void Open(Uri uri) => Opened.Add(uri);
    }

    /// <summary>
    /// Four 300 x 400 pages; page 2 is turned (/Rotate 90). Page 1's links, in order: XYZ on page 3,
    /// XYZ on the turned page 2, NextPage, LastPage, a web link, a javascript: URI, another file
    /// (GoToR), a program (Launch), document JavaScript, a FitH place on page 1 itself.
    /// </summary>
    private string LinkedPdf()
    {
        string[] annots =
        {
            "/Rect [10 350 110 370] /Dest [5 0 R /XYZ 50 350 0]",
            "/Rect [10 320 110 340] /A << /S /GoTo /D [4 0 R /XYZ 50 350 0] >>",
            "/Rect [10 290 110 310] /A << /S /Named /N /NextPage >>",
            "/Rect [10 260 110 280] /A << /S /Named /N /LastPage >>",
            "/Rect [10 230 110 250] /A << /S /URI /URI (https://example.org/docs?page=2) >>",
            "/Rect [10 200 110 220] /A << /S /URI /URI (javascript:app.alert\\(1\\)) >>",
            "/Rect [10 170 110 190] /A << /S /GoToR /F (other.pdf) /D [0 /Fit] >>",
            "/Rect [10 140 110 160] /A << /S /Launch /F (C:\\\\Windows\\\\notepad.exe) >>",
            "/Rect [10 110 110 130] /A << /S /JavaScript /JS (app.alert\\(1\\)) >>",
            "/Rect [10 80 110 100] /Dest [3 0 R /FitH 100]",
        };
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",                                                         // 1
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R 6 0 R] /Count 4 /MediaBox [0 0 300 400] >>",      // 2
            "<< /Type /Page /Parent 2 0 R /Annots [" + string.Join(" ", AnnotRefs(annots.Length)) + "] >>", // 3
            "<< /Type /Page /Parent 2 0 R /Rotate 90 >>",                                                // 4
            "<< /Type /Page /Parent 2 0 R >>",                                                           // 5
            "<< /Type /Page /Parent 2 0 R >>",                                                           // 6
        };
        foreach (var a in annots) objects.Add("<< /Type /Annot /Subtype /Link " + a + " >>");
        string path = Path.Combine(_dir, $"links_{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Raw(objects));
        return path;

        static IEnumerable<string> AnnotRefs(int count)
        {
            for (int i = 0; i < count; i++) yield return $"{7 + i} 0 R";
        }
    }

    private static byte[] Raw(IReadOnlyList<string> objects)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private async Task<(MainViewModel Vm, FakeOpener Opener, List<(int Page, double? X, double Y)> Scrolls)> Open()
    {
        var vm = new MainViewModel();
        var opener = new FakeOpener();
        var scrolls = new List<(int, double?, double)>();
        vm.LinkOpener = opener;
        vm.ScrollToLocationAction = (page, x, y) => scrolls.Add((page, x, y));
        vm.ShowMessageBoxAction = (_, _, _, _) => { };
        await vm.LoadDocumentAsync(LinkedPdf());
        return (vm, opener, scrolls);
    }

    /// <summary>The middle of a link's first area, in overlay space.</summary>
    private static Point Middle(PageLink link)
    {
        var r = link.Areas[0];
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    [Fact]
    public async Task Links_AreReadAndHitTestedInOverlaySpace()
    {
        var (vm, _, _) = await Open();
        using var _vm = vm;
        var links = vm.GetPageLinks(1);
        Assert.Equal(10, links.Count);
        Assert.Empty(vm.GetPageLinks(2));

        // /Rect [10 350 110 370] on a 300 x 400 page, from the top-left corner.
        var area = links[0].Areas[0];
        Assert.Equal(10 / 300.0, area.X, 6);
        Assert.Equal(30 / 400.0, area.Y, 6);
        Assert.Same(links[0], vm.LinkAt(1, Middle(links[0])));
        Assert.Null(vm.LinkAt(1, new Point(0.9, 0.9)));
        Assert.Equal("Link: Go to page 3", links[0].AccessibleName);
        Assert.Equal("Link: https://example.org/docs?page=2", links[4].AccessibleName);
        Assert.Equal("Link outside the document", links[6].AccessibleName);
    }

    [Fact]
    public async Task GoToLink_ScrollsToThePlaceAndBackReturnsToTheExactView()
    {
        var (vm, _, scrolls) = await Open();
        using var _vm = vm;
        vm.CaptureViewFunc = () => (vm.CurrentPageNumber, 0.0, 0.25);
        Assert.False(vm.CanGoBack);

        vm.ActivateLink(vm.GetPageLinks(1)[0]);

        Assert.Equal(3, vm.CurrentPageNumber);
        var (page, x, y) = Assert.Single(scrolls);
        Assert.Equal(3, page);
        Assert.Equal(50 / 300.0, x!.Value, 6);
        Assert.Equal(1 - 350 / 400.0, y, 6);
        Assert.True(vm.CanGoBack);

        vm.CaptureViewFunc = () => (vm.CurrentPageNumber, 0.0, 0.125);
        vm.GoBack();
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.Equal((1, (double?)0.0, 0.25), scrolls[^1]);

        // Forward returns to the link's target view.
        vm.GoForward();
        Assert.Equal(3, vm.CurrentPageNumber);
        Assert.Equal((3, (double?)0.0, 0.125), scrolls[^1]);
    }

    [Fact]
    public async Task GoToLink_OnATurnedPageScrollsToThePlaceAsShown()
    {
        var (vm, _, scrolls) = await Open();
        using var _vm = vm;
        Assert.Equal(90, vm.Pages[1].IntrinsicRotation);

        vm.ActivateLink(vm.GetPageLinks(1)[1]);

        Assert.Equal(2, vm.CurrentPageNumber);
        var (page, x, y) = Assert.Single(scrolls);
        // Stored (50/300, 1 - 350/400) turned a quarter clockwise: (1 - y, x).
        Assert.Equal(2, page);
        Assert.Equal(1 - (1 - 350 / 400.0), x!.Value, 6);
        Assert.Equal(50 / 300.0, y, 6);
    }

    [Fact]
    public async Task GoToLink_ToAPlaceOnTheSamePageIsRecordedForBack()
    {
        var (vm, _, scrolls) = await Open();
        using var _vm = vm;
        vm.ActivateLink(vm.GetPageLinks(1)[9]); // FitH 100 on page 1

        Assert.Equal(1, vm.CurrentPageNumber);
        var (page, x, y) = Assert.Single(scrolls);
        Assert.Equal(1, page);
        Assert.Null(x); // FitH leaves the horizontal scroll alone
        Assert.Equal(1 - 100 / 400.0, y, 6);
        Assert.True(vm.CanGoBack);
    }

    [Fact]
    public async Task NamedActions_GoToTheNextAndLastPage()
    {
        var (vm, _, _) = await Open();
        using var _vm = vm;
        vm.ActivateLink(vm.GetPageLinks(1)[2]);
        Assert.Equal(2, vm.CurrentPageNumber);
        vm.ActivateLink(vm.GetPageLinks(1)[3]);
        Assert.Equal(4, vm.CurrentPageNumber);
        vm.GoBack();
        Assert.Equal(2, vm.CurrentPageNumber);
    }

    [Fact]
    public async Task WebLink_OpensOnlyAfterTheReaderAgrees()
    {
        var (vm, opener, _) = await Open();
        using var _vm = vm;
        var link = vm.GetPageLinks(1)[4];

        opener.Answer = false;
        vm.ActivateLink(link);
        Assert.Equal(new[] { "https://example.org/docs?page=2" }, opener.Asked);
        Assert.Empty(opener.Opened);

        opener.Answer = true;
        vm.ActivateLink(link);
        var opened = Assert.Single(opener.Opened);
        Assert.Equal("https", opened.Scheme);
        Assert.Equal("example.org", opened.Host);
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public async Task OtherSchemesAreShownAndNeverOpened()
    {
        var (vm, opener, _) = await Open();
        using var _vm = vm;
        string? shown = null;
        vm.ShowMessageBoxAction = (message, _, _, _) => shown = message;
        opener.Answer = true;

        vm.ActivateLink(vm.GetPageLinks(1)[5]);

        Assert.Empty(opener.Asked);
        Assert.Empty(opener.Opened);
        Assert.Contains("javascript:app.alert(1)", shown);
        Assert.Equal(1, vm.CurrentPageNumber);
    }

    [Theory]
    [InlineData(6)] // GoToR: another file
    [InlineData(7)] // Launch: a program
    [InlineData(8)] // document JavaScript
    public async Task LinksOutsideTheDocumentAreNeverFollowed(int index)
    {
        var (vm, opener, scrolls) = await Open();
        using var _vm = vm;
        opener.Answer = true;
        var link = vm.GetPageLinks(1)[index];
        Assert.Equal(PdfLinkTargetKind.External, link.Target.Kind);

        vm.ActivateLink(link);

        Assert.Empty(opener.Asked);
        Assert.Empty(opener.Opened);
        Assert.Empty(scrolls);
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.False(vm.CanGoBack);
        Assert.Equal(link.Description, vm.StatusText);
    }

    [Fact]
    public async Task SampleDocument_ContentsBoxLinksGoToPagesTwoToSeven()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var vm = new MainViewModel { LinkOpener = new FakeOpener() };
        await vm.LoadDocumentAsync(Path.Combine(dir!.FullName, "samples", "SampleDocument.pdf"));

        var links = vm.GetPageLinks(1);
        Assert.Equal(6, links.Count);
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, links.Select(l => l.Target.PageIndex + 1).OrderBy(p => p));
        foreach (var link in links)
        {
            vm.NavigateToPage(1);
            Assert.Same(link, vm.LinkAt(1, Middle(link)));
            vm.ActivateLink(link);
            Assert.Equal(link.Target.PageIndex + 1, vm.CurrentPageNumber);
            vm.GoBack();
            Assert.Equal(1, vm.CurrentPageNumber);
        }
    }

    [Fact]
    public async Task LinksAreNotFollowedWhileADrawingToolIsOn()
    {
        var (vm, opener, scrolls) = await Open();
        using var _vm = vm;
        opener.Answer = true;
        Assert.True(vm.CanFollowLinks);
        vm.IsPanningEnabled = true;
        Assert.True(vm.CanFollowLinks); // the hand tool follows links on a click
        vm.ActiveAnnotationTool = AnnotationType.Rectangle;
        Assert.False(vm.CanFollowLinks);

        // Every way in (mouse, keyboard, UI Automation Invoke) goes through ActivateLink: all blocked.
        var links = vm.GetPageLinks(1);
        Assert.False(vm.ActivateLink(links[0]));
        Assert.False(vm.ActivateLink(links[2]));
        Assert.False(vm.ActivateLink(links[4]));
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.Empty(scrolls);
        Assert.Empty(opener.Asked);
        Assert.False(vm.CanGoBack);

        vm.ActiveAnnotationTool = null;
        Assert.True(vm.CanFollowLinks);
        Assert.True(vm.ActivateLink(links[2]));
        Assert.Equal(2, vm.CurrentPageNumber);
    }

    [Fact]
    public async Task WebLink_TheDialogShowsAndTheOpenerOpensTheSameNormalizedUri()
    {
        string path = Path.Combine(_dir, "normalized.pdf");
        File.WriteAllBytes(path, Raw(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [10 10 110 30] /A << /S /URI /URI (  HTTPS://EXAMPLE.org:443/a/../docs  ) >> >>",
        }));
        var opener = new FakeOpener { Answer = true };
        using var vm = new MainViewModel { LinkOpener = opener };
        await vm.LoadDocumentAsync(path);

        var link = Assert.Single(vm.GetPageLinks(1));
        Assert.Equal("https://example.org/docs", link.Description);
        vm.ActivateLink(link);
        Assert.Equal(new[] { "https://example.org/docs" }, opener.Asked);
        Assert.Equal("https://example.org/docs", Assert.Single(opener.Opened).AbsoluteUri);
    }
}
