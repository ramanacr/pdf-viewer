using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Text boxes, search matches and annotation rectangles are all reported in unrotated page
/// space. The overlay layers are turned into place by a LayoutTransform, but anything that
/// needs a number rather than a laid-out element has to do the same turn in arithmetic -
/// scrolling to a search match, which otherwise aimed at where the match would have been if
/// the page had never been turned.
///
/// A 612x792 page is used throughout; at 90 and 270 degrees the page is displayed 792x612.
/// </summary>
public class PageRotationMappingTests
{
    private static PageViewModel Page(int intrinsic = 0, int applied = 0)
    {
        var page = new PageViewModel(1, 612, 792) { IntrinsicRotation = intrinsic };
        page.UpdateRotation(applied);
        page.UpdateScale(1.0);
        return page;
    }

    [Fact]
    public void TestAnUnrotatedPageLeavesThePointWhereItIs()
    {
        var (x, y) = Page().ToDisplayNormalized(0.25, 0.10);

        Assert.Equal(0.25, x, 6);
        Assert.Equal(0.10, y, 6);
    }

    /// <summary>
    /// Turned a quarter clockwise, the top-left corner of the page swings to the top-right.
    /// Something near the top-left therefore ends up near the top-RIGHT of what is on screen -
    /// which is the whole point: scrolling right rather than staying left.
    /// </summary>
    [Fact]
    public void TestAQuarterTurnClockwiseSendsTheTopLeftToTheTopRight()
    {
        var (x, y) = Page(applied: 90).ToDisplayNormalized(0.10, 0.20);

        Assert.Equal(0.80, x, 6);   // 1 - 0.20
        Assert.Equal(0.10, y, 6);
    }

    [Fact]
    public void TestAHalfTurnMirrorsBothWays()
    {
        var (x, y) = Page(applied: 180).ToDisplayNormalized(0.10, 0.20);

        Assert.Equal(0.90, x, 6);
        Assert.Equal(0.80, y, 6);
    }

    [Fact]
    public void TestAThreeQuarterTurnSendsTheTopLeftToTheBottomLeft()
    {
        var (x, y) = Page(applied: 270).ToDisplayNormalized(0.10, 0.20);

        Assert.Equal(0.20, x, 6);
        Assert.Equal(0.90, y, 6);   // 1 - 0.10
    }

    /// <summary>
    /// The page's own /Rotate counts the same as a turn the reader asked for; a page stored
    /// sideways is displayed sideways whether or not anyone touched the rotate button.
    /// </summary>
    [Fact]
    public void TestThePagesOwnRotationCountsAsMuchAsTheReaders()
    {
        var (intrinsicX, intrinsicY) = Page(intrinsic: 90).ToDisplayNormalized(0.10, 0.20);
        var (appliedX, appliedY) = Page(applied: 90).ToDisplayNormalized(0.10, 0.20);

        Assert.Equal(appliedX, intrinsicX, 6);
        Assert.Equal(appliedY, intrinsicY, 6);
    }

    [Fact]
    public void TestThePagesOwnRotationAndTheReadersAddUp()
    {
        // Stored sideways, then turned another quarter: half a turn in total.
        var page = Page(intrinsic: 90, applied: 90);
        Assert.Equal(180.0, page.OverlayRotationAngle, 6);

        var (x, y) = page.ToDisplayNormalized(0.10, 0.20);
        Assert.Equal(0.90, x, 6);
        Assert.Equal(0.80, y, 6);
    }

    /// <summary>
    /// A full circle has to come back to exactly where it started, or repeated use of the
    /// rotate button would walk a search jump further off target each time round.
    /// </summary>
    [Fact]
    public void TestAFullCircleComesBackToTheSamePoint()
    {
        var page = Page(intrinsic: 180, applied: 180);
        Assert.Equal(0.0, page.OverlayRotationAngle, 6);

        var (x, y) = page.ToDisplayNormalized(0.33, 0.66);
        Assert.Equal(0.33, x, 6);
        Assert.Equal(0.66, y, 6);
    }

    /// <summary>
    /// Four quarter turns must return the point to where it started. This is the property that
    /// says the mapping is a rotation and not just four separate guesses.
    /// </summary>
    [Theory]
    [InlineData(0.10, 0.20)]
    [InlineData(0.75, 0.05)]
    [InlineData(0.5, 0.5)]
    public void TestFourQuarterTurnsReturnThePointToItsStart(double startX, double startY)
    {
        double x = startX, y = startY;

        for (int turn = 90; turn <= 360; turn += 90)
        {
            (x, y) = Page(applied: 90).ToDisplayNormalized(x, y);
        }

        Assert.Equal(startX, x, 6);
        Assert.Equal(startY, y, 6);
    }

    /// <summary>
    /// The displayed frame is the unrotated one with its sides swapped on a quarter turn, so a
    /// point mapped into it and scaled by the displayed size has to stay inside the page.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TestAMappedPointStaysOnThePage(int rotation)
    {
        var page = Page(applied: rotation);
        var (x, y) = page.ToDisplayNormalized(0.9, 0.9);

        Assert.InRange(x * page.DisplayWidth, 0, page.DisplayWidth);
        Assert.InRange(y * page.DisplayHeight, 0, page.DisplayHeight);

        // And the displayed frame really did swap sides on the quarter turns.
        bool sideways = rotation == 90 || rotation == 270;
        Assert.Equal(sideways ? 792.0 : 612.0, page.DisplayWidth, 6);
        Assert.Equal(sideways ? 612.0 : 792.0, page.DisplayHeight, 6);
    }

    /// <summary>
    /// An angle that is not a quarter turn is not something this viewer can produce. Guessing
    /// at one would put the point somewhere arbitrary, so it is left alone instead.
    /// </summary>
    [Fact]
    public void TestAnAngleThatIsNotAQuarterTurnLeavesThePointAlone()
    {
        var page = new PageViewModel(1, 612, 792) { IntrinsicRotation = 45 };
        page.UpdateScale(1.0);

        var (x, y) = page.ToDisplayNormalized(0.25, 0.10);

        Assert.Equal(0.25, x, 6);
        Assert.Equal(0.10, y, 6);
    }
}
