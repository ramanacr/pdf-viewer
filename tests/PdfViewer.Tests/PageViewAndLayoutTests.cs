using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

public class PageViewAndLayoutTests
{
    [Fact]
    public void TestPageViewModelDimensionsAndScaling()
    {
        var page = new PageViewModel(1, 612, 792);

        Assert.Equal(1, page.PageNumber);
        Assert.Equal(612, page.WidthPt);
        Assert.Equal(792, page.HeightPt);
        Assert.Equal(612, page.DisplayWidth);
        Assert.Equal(792, page.DisplayHeight);

        // Update Scale (e.g. 150% zoom)
        page.UpdateScale(1.5);
        Assert.Equal(1.5, page.DisplayScale);
        Assert.Equal(612 * 1.5, page.DisplayWidth);
        Assert.Equal(792 * 1.5, page.DisplayHeight);

        // Update Rotation (90 degrees swaps width and height)
        page.UpdateRotation(90);
        Assert.Equal(90, page.RotationAngle);
        Assert.Equal(792 * 1.5, page.DisplayWidth);
        Assert.Equal(612 * 1.5, page.DisplayHeight);

        // 180 degrees
        page.UpdateRotation(180);
        Assert.Equal(612 * 1.5, page.DisplayWidth);
        Assert.Equal(792 * 1.5, page.DisplayHeight);

        // 270 degrees
        page.UpdateRotation(270);
        Assert.Equal(792 * 1.5, page.DisplayWidth);
        Assert.Equal(612 * 1.5, page.DisplayHeight);
    }

    [Fact]
    public void TestPageViewModelTextSelectionSegments()
    {
        var page = new PageViewModel(1, 612, 792);

        page.TextSegments.Add(new PageTextSegment { Text = "First", SegmentIndex = 0, X = 0.1, Y = 0.1, Width = 0.1, Height = 0.05 });
        page.TextSegments.Add(new PageTextSegment { Text = "Second", SegmentIndex = 1, X = 0.25, Y = 0.1, Width = 0.1, Height = 0.05 });
        page.TextSegments.Add(new PageTextSegment { Text = "Third", SegmentIndex = 2, X = 0.4, Y = 0.1, Width = 0.1, Height = 0.05 });

        // Select segment range
        page.SelectRange(new Point(0.1, 0.1), new Point(0.25, 0.1));
        Assert.Equal(2, page.SelectedSegments.Count);
        Assert.Equal("First Second", page.GetSelectedText());

        // Select all text
        page.SelectAllText();
        Assert.Equal(3, page.SelectedSegments.Count);
        Assert.Equal("First Second Third", page.GetSelectedText());

        // Clear text selection
        page.ClearTextSelection();
        Assert.Empty(page.SelectedSegments);
        Assert.Empty(page.GetSelectedText());
    }

    [Fact]
    public void TestThumbnailViewModelLifecycle()
    {
        var thumb = new ThumbnailViewModel(3);

        Assert.Equal(3, thumb.PageNumber);
        Assert.Null(thumb.ThumbnailImage);
        Assert.False(thumb.IsLoading);
        Assert.False(thumb.IsCurrentPage);

        thumb.IsCurrentPage = true;
        Assert.True(thumb.IsCurrentPage);

        thumb.UnloadThumbnail();
        Assert.Null(thumb.ThumbnailImage);
        Assert.False(thumb.IsLoading);
    }
}
