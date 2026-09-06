using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers the page cache's byte ceiling.
///
/// Counting entries does not bound memory. A rendered A4 page is a few megabytes at 150 DPI
/// and around 33 MB at the 300 DPI this application uses past 2x zoom, so a sixty-entry cache
/// held anywhere between 60 MB and roughly 2 GB depending only on how far the user had zoomed.
/// These tests are about the ceiling that fixes that, and about it holding when several
/// documents share one budget.
/// </summary>
public class PageCacheCeilingTests
{
    /// <summary>A bitmap of a known, exact byte size: BGRA32 is 4 bytes per pixel.</summary>
    private static BitmapSource BitmapOfBytes(long bytes)
    {
        int pixels = (int)(bytes / 4);
        int width = Math.Max(1, pixels);

        int stride = width * 4;
        var image = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, new byte[stride], stride);
        image.Freeze();
        return image;
    }

    private const long OneMb = 1024 * 1024;

    [Fact]
    public void TestMeasureBytesMatchesTheDecodedSize()
    {
        var image = BitmapOfBytes(4 * OneMb);
        Assert.Equal(4 * OneMb, LruPageCache.MeasureBytes(image));
    }

    /// <summary>
    /// The whole point: many pages that each fit the entry count but together do not fit the
    /// budget must not all be retained.
    /// </summary>
    [Fact]
    public void TestTheCacheStopsGrowingAtItsByteCeiling()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 64 * OneMb);

        for (int page = 1; page <= 40; page++)
        {
            cache.Add(page, dpi: 300, rotation: 0, BitmapOfBytes(8 * OneMb));
        }

        // Forty 8 MB pages is 320 MB; the entry count would have allowed all of them.
        Assert.True(cache.CurrentBytes <= 64 * OneMb,
            $"Cache held {cache.CurrentBytes / OneMb} MB against a 64 MB ceiling.");
        Assert.True(cache.Count <= 8, $"Expected about 8 entries at 8 MB each, found {cache.Count}.");
    }

    [Fact]
    public void TestTheEntryCountStillAppliesWhenPagesAreSmall()
    {
        var cache = new LruPageCache(capacity: 5, byteCeiling: 256 * OneMb);

        for (int page = 1; page <= 20; page++)
        {
            cache.Add(page, dpi: 96, rotation: 0, BitmapOfBytes(OneMb));
        }

        Assert.Equal(5, cache.Count);
    }

    [Fact]
    public void TestTheOldestPagesAreTheOnesDropped()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 24 * OneMb);

        cache.Add(1, 150, 0, BitmapOfBytes(8 * OneMb));
        cache.Add(2, 150, 0, BitmapOfBytes(8 * OneMb));
        cache.Add(3, 150, 0, BitmapOfBytes(8 * OneMb));

        // Touch page 1 so page 2 becomes the least recently used.
        Assert.True(cache.TryGet(1, 150, 0, out _));

        cache.Add(4, 150, 0, BitmapOfBytes(8 * OneMb));

        Assert.True(cache.TryGet(1, 150, 0, out _), "The page just used should have been kept.");
        Assert.False(cache.TryGet(2, 150, 0, out _), "The least recently used page should have gone.");
        Assert.True(cache.TryGet(4, 150, 0, out _));
    }

    /// <summary>
    /// A page larger than the entire budget cannot be held without evicting everything and
    /// still not fitting, so it is simply not retained - the caller already has it.
    /// </summary>
    [Fact]
    public void TestAPageBiggerThanTheBudgetIsNotCachedAndEvictsNothing()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: LruPageCache.MinimumByteCeiling);

        cache.Add(1, 150, 0, BitmapOfBytes(4 * OneMb));
        long before = cache.CurrentBytes;

        cache.Add(2, 300, 0, BitmapOfBytes(LruPageCache.MinimumByteCeiling + OneMb));

        Assert.False(cache.TryGet(2, 300, 0, out _));
        Assert.True(cache.TryGet(1, 150, 0, out _), "An oversized page must not evict what was already there.");
        Assert.Equal(before, cache.CurrentBytes);
    }

    [Fact]
    public void TestReplacingAPageDoesNotDoubleCountIt()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 64 * OneMb);

        cache.Add(1, 150, 0, BitmapOfBytes(8 * OneMb));
        cache.Add(1, 150, 0, BitmapOfBytes(8 * OneMb));

        Assert.Equal(1, cache.Count);
        Assert.Equal(8 * OneMb, cache.CurrentBytes);
    }

    [Fact]
    public void TestClearingReleasesTheAccounting()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 64 * OneMb);

        cache.Add(1, 150, 0, BitmapOfBytes(8 * OneMb));
        cache.Add(2, 150, 0, BitmapOfBytes(8 * OneMb));
        Assert.True(cache.CurrentBytes > 0);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    /// <summary>
    /// When documents share one budget, opening another has to shrink the others straight
    /// away rather than only bounding what they take in future.
    /// </summary>
    [Fact]
    public void TestLoweringTheCeilingEvictsImmediately()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 256 * OneMb);

        for (int page = 1; page <= 10; page++)
        {
            cache.Add(page, 150, 0, BitmapOfBytes(8 * OneMb));
        }

        Assert.Equal(80 * OneMb, cache.CurrentBytes);

        cache.SetByteCeiling(64 * OneMb);

        Assert.True(cache.CurrentBytes <= 64 * OneMb,
            $"Still holding {cache.CurrentBytes / OneMb} MB after the ceiling dropped to 64 MB.");
    }

    /// <summary>
    /// The cache holds what it was told to hold. An earlier version quietly raised any
    /// request up to a floor, which made it hold more than a caller dividing a shared budget
    /// had allowed - and made a test asserting the smaller figure pass for the wrong reason.
    /// </summary>
    [Fact]
    public void TestTheCeilingIsExactlyWhatWasAskedFor()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 12 * OneMb);
        Assert.Equal(12 * OneMb, cache.ByteCeiling);

        cache.SetByteCeiling(5 * OneMb);
        Assert.Equal(5 * OneMb, cache.ByteCeiling);
    }

    /// <summary>
    /// The floor belongs to the budget split, where starving a document would mean every
    /// scroll re-renders. Splitting is where tabs will get their share.
    /// </summary>
    [Fact]
    public void TestTheSharedBudgetIsDividedButNeverBelowTheFloor()
    {
        Assert.Equal(LruPageCache.DefaultTotalByteCeiling, LruPageCache.ShareOfTotal(1));
        Assert.Equal(LruPageCache.DefaultTotalByteCeiling / 2, LruPageCache.ShareOfTotal(2));
        Assert.Equal(LruPageCache.DefaultTotalByteCeiling / 4, LruPageCache.ShareOfTotal(4));

        // Past the point where an even split would starve each document, the floor wins.
        Assert.Equal(LruPageCache.MinimumByteCeiling, LruPageCache.ShareOfTotal(64));

        // And a nonsensical count does not produce a divide-by-zero or a negative share.
        Assert.Equal(LruPageCache.DefaultTotalByteCeiling, LruPageCache.ShareOfTotal(0));
        Assert.True(LruPageCache.ShareOfTotal(-5) > 0);
    }

    [Fact]
    public void TestNothingIsCachedForANullImage()
    {
        var cache = new LruPageCache(capacity: 60, byteCeiling: 64 * OneMb);

        cache.Add(1, 150, 0, null!);

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }
}
