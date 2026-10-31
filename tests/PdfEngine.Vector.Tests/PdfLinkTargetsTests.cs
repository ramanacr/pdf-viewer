using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Services;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// Links for the viewers (PdfLinkTargets): where a link goes, which link is under a point
/// (/Rect, and /QuadPoints when usable), and which URIs may reach a browser. Every PDF is built here.
/// </summary>
public class PdfLinkTargetsTests
{
    private static PdfLink Link(PdfRect rect, params double[][] quads) =>
        new(1, 0, rect, quads, null, new PdfAction(PdfActionKind.Uri, "URI") { Uri = "https://example.org/" });

    // ------------------------------------------------------------------ resolving

    [Fact]
    public async Task Resolve_NamedExplicitUriAndRemoteLinksOfADocument()
    {
        using var doc = await PdfVectorDocument.OpenAsync(NativeDocumentServicesTests.Navigable());
        var links = PdfDocumentInfo.ReadLinks(doc, 1);
        var targets = links.Select(l => PdfLinkTargets.Resolve(l, doc.PageCount)).ToList();

        // /Dest (chap3) through the /Dests name tree: page 3, the whole page.
        Assert.Equal(PdfLinkTargetKind.Page, targets[0].Kind);
        Assert.Equal(2, targets[0].PageIndex);
        Assert.Equal("Go to page 3", targets[0].Description);
        Assert.Null(targets[0].Left);
        Assert.Null(targets[0].Top);
        Assert.True(targets[0].IsInternal);

        // A relative URI with the catalog's /URI /Base: a web link.
        Assert.Equal(PdfLinkTargetKind.WebUri, targets[1].Kind);
        Assert.Equal("https://example.org/relative/path", targets[1].Uri);

        // /A GoTo with an explicit destination.
        Assert.Equal(PdfLinkTargetKind.Page, targets[2].Kind);
        Assert.Equal(1, targets[2].PageIndex);
        Assert.Equal("GoTo", targets[2].ActionType);

        // Another file: never followed.
        Assert.Equal(PdfLinkTargetKind.External, targets[3].Kind);
        Assert.Equal(PdfLinkTargets.OutsideMessage, targets[3].Description);
        Assert.False(targets[3].IsInternal);
    }

    [Theory]
    [InlineData(PdfDestinationFit.XYZ, new double[] { 10, 700, 0 }, 10.0, 700.0)]
    [InlineData(PdfDestinationFit.FitH, new double[] { 500 }, null, 500.0)]
    [InlineData(PdfDestinationFit.FitBH, new double[] { 450 }, null, 450.0)]
    [InlineData(PdfDestinationFit.FitV, new double[] { 72 }, 72.0, null)]
    [InlineData(PdfDestinationFit.FitR, new double[] { 20, 100, 200, 300 }, 20.0, 300.0)]
    [InlineData(PdfDestinationFit.Fit, new double[0], null, null)]
    public void Resolve_DestinationPositions(PdfDestinationFit fit, double[] parameters, double? left, double? top)
    {
        var dest = new PdfDestination(1, fit, parameters.Select(p => (double?)p).ToList());
        var target = PdfLinkTargets.Resolve(dest, null, pageCount: 3);
        Assert.Equal(PdfLinkTargetKind.Page, target.Kind);
        Assert.Equal(1, target.PageIndex);
        Assert.Equal(left, target.Left);
        Assert.Equal(top, target.Top);
    }

    [Fact]
    public void Resolve_XyzWithNullsLeavesThoseCoordinatesUnchanged()
    {
        var dest = new PdfDestination(0, PdfDestinationFit.XYZ, new double?[] { null, 640, null });
        var target = PdfLinkTargets.Resolve(dest, null, 1);
        Assert.Null(target.Left);
        Assert.Equal(640, target.Top);
    }

    [Fact]
    public void Resolve_PageOutsideTheDocumentGoesNowhere()
    {
        var target = PdfLinkTargets.Resolve(new PdfDestination(7, PdfDestinationFit.Fit, Array.Empty<double?>()), null, 3);
        Assert.Equal(PdfLinkTargetKind.None, target.Kind);
        Assert.False(target.IsInternal);
        Assert.Equal(PdfLinkTargetKind.None, PdfLinkTargets.Resolve(null, new PdfAction(PdfActionKind.GoTo, "GoTo"), 3).Kind);
        Assert.Equal(PdfLinkTargetKind.None, PdfLinkTargets.Resolve(null, null, 3).Kind);
    }

    [Theory]
    [InlineData("NextPage", PdfLinkTargetKind.NextPage)]
    [InlineData("PrevPage", PdfLinkTargetKind.PreviousPage)]
    [InlineData("FirstPage", PdfLinkTargetKind.FirstPage)]
    [InlineData("LastPage", PdfLinkTargetKind.LastPage)]
    [InlineData("Print", PdfLinkTargetKind.External)]
    [InlineData("GoBack", PdfLinkTargetKind.External)]
    public void Resolve_NamedActions(string name, PdfLinkTargetKind expected)
    {
        var target = PdfLinkTargets.Resolve(null, new PdfAction(PdfActionKind.Named, "Named") { Name = name }, 5);
        Assert.Equal(expected, target.Kind);
    }

    [Theory]
    [InlineData(PdfActionKind.JavaScript, "JavaScript")]
    [InlineData(PdfActionKind.Launch, "Launch")]
    [InlineData(PdfActionKind.GoToR, "GoToR")]
    [InlineData(PdfActionKind.GoToE, "GoToE")]
    [InlineData(PdfActionKind.Other, "SubmitForm")]
    [InlineData(PdfActionKind.Unsupported, "")]
    public void Resolve_ActionsThatLeaveTheDocumentAreNeverFollowed(PdfActionKind kind, string type)
    {
        var target = PdfLinkTargets.Resolve(null, new PdfAction(kind, type) { FilePath = "C:\\Windows\\notepad.exe" }, 2);
        Assert.Equal(PdfLinkTargetKind.External, target.Kind);
        Assert.Null(target.Uri);
        Assert.False(target.IsInternal);
    }

    [Theory]
    [InlineData("https://example.org/a?b=c#d", true)]
    [InlineData("http://example.org", true)]
    [InlineData("  https://example.org/padded  ", true)]
    [InlineData("mailto:someone@example.org", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("\\\\server\\share\\x.exe", false)]
    [InlineData("ftp://example.org/x", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("https://exa mple.org/", false)]
    [InlineData("https://example.org/\u0007", false)]
    [InlineData("relative/path", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    public void TryGetWebUri_OnlyHttpHttpsAndMailto(string text, bool allowed)
    {
        Assert.Equal(allowed, PdfLinkTargets.TryGetWebUri(text, out _));
        var target = PdfLinkTargets.Resolve(null, new PdfAction(PdfActionKind.Uri, "URI") { Uri = text }, 1);
        Assert.Equal(allowed ? PdfLinkTargetKind.WebUri : PdfLinkTargetKind.BlockedUri, target.Kind);
        Assert.Equal(text.Trim(), target.Uri);
    }

    [Fact]
    public void TryGetWebUri_RefusesOverlongUris()
    {
        Assert.False(PdfLinkTargets.TryGetWebUri("https://example.org/" + new string('a', PdfLinkTargets.MaxUriLength), out _));
    }

    [Fact]
    public void DisplayHost_ShowsThePunycodeOfAnInternationalHostAndTheAddressOfAMailLink()
    {
        Assert.True(PdfLinkTargets.TryGetWebUri("https://example.org/path", out var plain));
        Assert.Equal("example.org", PdfLinkTargets.DisplayHost(plain));
        Assert.True(PdfLinkTargets.TryGetWebUri("https://b\u00fccher.example/", out var idn));
        Assert.Contains("xn--", PdfLinkTargets.DisplayHost(idn));
        Assert.True(PdfLinkTargets.TryGetWebUri("https://bank.example@evil.example/", out var userInfo));
        Assert.Equal("evil.example", PdfLinkTargets.DisplayHost(userInfo));
        Assert.True(PdfLinkTargets.TryGetWebUri("mailto:someone@example.org", out var mail));
        Assert.Equal("someone@example.org", PdfLinkTargets.DisplayHost(mail));
    }

    // ------------------------------------------------------------------ hit testing

    [Fact]
    public async Task HitTest_UsesTheRectanglesOfTheDocumentsLinks()
    {
        using var doc = await PdfVectorDocument.OpenAsync(NativeDocumentServicesTests.Navigable());
        var links = PdfDocumentInfo.ReadLinks(doc, 1);
        Assert.Same(links[0], PdfLinkTargets.HitTest(links, 20, 20));
        Assert.Same(links[1], PdfLinkTargets.HitTest(links, 100, 25));
        Assert.Same(links[2], PdfLinkTargets.HitTest(links, 150, 20)); // its /Rect is written upside down
        Assert.Same(links[3], PdfLinkTargets.HitTest(links, 30, 60));
        Assert.Null(PdfLinkTargets.HitTest(links, 5, 5));
        Assert.Null(PdfLinkTargets.HitTest(links, 250, 300));
    }

    [Fact]
    public void HitTest_TheLastOfOverlappingLinksWins()
    {
        var under = Link(new PdfRect(0, 0, 100, 100));
        var over = Link(new PdfRect(50, 50, 100, 100));
        Assert.Same(over, PdfLinkTargets.HitTest(new[] { under, over }, 75, 75));
        Assert.Same(under, PdfLinkTargets.HitTest(new[] { under, over }, 25, 25));
    }

    [Fact]
    public void HitTest_SlackReachesALinkJustOutside()
    {
        var link = Link(new PdfRect(100, 100, 50, 10));
        Assert.Null(PdfLinkTargets.HitTest(new[] { link }, 98, 105));
        Assert.Same(link, PdfLinkTargets.HitTest(new[] { link }, 98, 105, slack: 3));
    }

    [Fact]
    public void Contains_UsesQuadPointsWhenTheyAreInsideTheRect()
    {
        // Two lines of a wrapped link: the top line's right half and the bottom line's left half.
        // QuadPoints in the usual writers' order: top-left, top-right, bottom-left, bottom-right.
        var link = Link(new PdfRect(0, 0, 200, 40),
            new double[] { 100, 40, 200, 40, 100, 20, 200, 20 },
            new double[] { 0, 20, 100, 20, 0, 0, 100, 0 });
        Assert.True(PdfLinkTargets.Contains(link, 150, 30));
        Assert.True(PdfLinkTargets.Contains(link, 50, 10));
        Assert.False(PdfLinkTargets.Contains(link, 50, 30)); // in the rect, but on no quadrilateral
        Assert.False(PdfLinkTargets.Contains(link, 150, 10));
        Assert.Equal(2, PdfLinkTargets.Areas(link).Count);
    }

    [Fact]
    public void Contains_TakesQuadPointsInEitherOrder()
    {
        // The standard's counter-clockwise order, and a rotated (diamond) quadrilateral.
        var counterClockwise = Link(new PdfRect(0, 0, 100, 100), new double[] { 0, 0, 100, 0, 100, 50, 0, 50 });
        Assert.True(PdfLinkTargets.Contains(counterClockwise, 50, 25));
        Assert.False(PdfLinkTargets.Contains(counterClockwise, 50, 75));
        var diamond = Link(new PdfRect(0, 0, 100, 100), new double[] { 50, 100, 100, 50, 0, 50, 50, 0 });
        Assert.True(PdfLinkTargets.Contains(diamond, 50, 50));
        Assert.False(PdfLinkTargets.Contains(diamond, 5, 5));
        Assert.False(PdfLinkTargets.Contains(diamond, 95, 95));
    }

    [Fact]
    public void Contains_IgnoresQuadPointsOutsideTheRect()
    {
        var link = Link(new PdfRect(0, 0, 100, 20), new double[] { 300, 300, 400, 300, 300, 280, 400, 280 });
        Assert.True(PdfLinkTargets.Contains(link, 50, 10));
        Assert.False(PdfLinkTargets.Contains(link, 350, 290));
        Assert.Equal(new PdfRect(0, 0, 100, 20), Assert.Single(PdfLinkTargets.Areas(link)));
    }

    [Fact]
    public void Contains_DegenerateQuadIsNoArea()
    {
        var link = Link(new PdfRect(0, 0, 100, 100), new double[] { 10, 10, 10, 10, 10, 10, 10, 10 });
        Assert.False(PdfLinkTargets.Contains(link, 60, 60));
    }

    [Fact]
    public void Contains_EmptyRectIsNoLink()
    {
        var link = Link(PdfRect.Empty);
        Assert.False(PdfLinkTargets.Contains(link, 0, 0));
        Assert.Empty(PdfLinkTargets.Areas(link));
    }

    [Fact]
    public async Task ReadLinks_QuadPointsOfALinkInAFileAreUsed()
    {
        var bytes = NativeDocumentServicesTests.Raw(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 200 40] /QuadPoints [100 40 200 40 100 20 200 20 0 20 100 20 0 0 100 0] " +
            "/A << /S /Named /N /NextPage >> >>");
        using var doc = await PdfVectorDocument.OpenAsync(bytes);
        var links = PdfDocumentInfo.ReadLinks(doc, 1);
        Assert.NotNull(PdfLinkTargets.HitTest(links, 150, 30));
        Assert.Null(PdfLinkTargets.HitTest(links, 50, 30));
        Assert.Equal(PdfLinkTargetKind.NextPage, PdfLinkTargets.Resolve(links[0], doc.PageCount).Kind);
    }
}
