using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Fit to width. The fitted page and its margins must fit in the viewport that remains after
/// the vertical scroll bar, so no horizontal scroll bar shows, and the page must sit in the
/// middle. Before, the page area's 24 px margin sat outside a grid already as wide as the
/// viewport: the content was always 48 px too wide, and the page sat 24 px right of centre.
/// </summary>
public class FitWidthLayoutTests : IDisposable
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly string _testDir;

    public FitWidthLayoutTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfFitWidth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); }
        catch { }
    }

    [Theory]
    [InlineData(1, 10.0)] // continuous: each page has 10 px each side
    [InlineData(2, 10.0)] // facing
    [InlineData(1, 0.0)]  // single page: no page margin
    public void TestFittedPagesAndMarginsNeverExceedTheViewport(int across, double sideMargin)
    {
        foreach (double viewport in new[] { 420.0, 700, 800, 1000, 1366, 1919.5 })
        foreach (double bar in new[] { 0.0, 10, 17 })
        foreach (double pageWidth in new[] { 595.0, 612, 842, 1191 })
        {
            double zoom = MainViewModel.FitWidthZoom(viewport, bar, pageWidth, across, sideMargin);
            double used = (pageWidth * zoom * across) + MainViewModel.FitChromeWidth(across, sideMargin);

            Assert.True(used <= viewport - bar, $"{used} > {viewport - bar} at {viewport}/{bar}/{pageWidth}");
            Assert.True(used >= viewport - bar - 2, $"{used} leaves a gap at {viewport}/{bar}/{pageWidth}");
        }
    }

    [Fact]
    public void TestNothingFitsInATinyViewport()
    {
        Assert.Equal(0, MainViewModel.FitWidthZoom(60, 10, 612, 1, 10));
    }

    [Fact]
    public async Task TestFitWidthKeepsTheScrollBarAndMarginsFree()
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_testDir, "fit.pdf"), 3, "FitToken");
        using var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        vm.VerticalScrollBarWidth = 10;

        foreach (double width in new[] { 760.0, 1200 }) // with the sidebar shown, and hidden
        foreach (var mode in new[] { ViewLayoutMode.Continuous, ViewLayoutMode.Facing, ViewLayoutMode.SinglePage })
        {
            vm.GetViewportSizeFunc = () => (width, 700);
            vm.SetViewMode(mode);
            vm.FitWidth();

            int across = vm.PagesPerRow;
            double side = vm.IsMultiPageLayout ? MainViewModel.PageSideMargin : 0;
            double used = (vm.Pages[0].DisplayWidth * across) + MainViewModel.FitChromeWidth(across, side);
            Assert.True(used <= width - 10, $"{mode} at {width}: {used} > {width - 10}");
            Assert.True(used >= width - 12, $"{mode} at {width}: {used} leaves a gap");
        }
    }

    /// <summary>The real page area in MainWindow.xaml has the structure the layout test below builds.</summary>
    [Fact]
    public void TestThePageAreaKeepsItsMarginInsideTheViewportWideGrid()
    {
        var root = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", "Views", "MainWindow.xaml"));
        var viewer = root.Descendants(Wpf + "ScrollViewer").Single(e => (string?)e.Attribute(Xaml + "Name") == "DocumentScrollViewer");
        Assert.Equal("Auto", (string?)viewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)viewer.Attribute("VerticalScrollBarVisibility"));

        var outer = viewer.Elements(Wpf + "Grid").Single();
        Assert.Contains("ViewportWidth", (string?)outer.Attribute("MinWidth"));
        Assert.Equal("Left", (string?)outer.Attribute("HorizontalAlignment"));
        Assert.Null(outer.Attribute("Margin"));

        var inner = outer.Elements(Wpf + "Grid").Single();
        Assert.Equal("Center", (string?)inner.Attribute("HorizontalAlignment"));
        Assert.Equal(MainViewModel.DocumentMargin.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)inner.Attribute("Margin"));

        // Each continuous page keeps the side margin the fit allows for.
        var pageBorder = inner.Descendants(Wpf + "Border").First(b => ((string?)b.Attribute("AutomationProperties.Name") ?? "").Contains("Page {0}"));
        Assert.Equal($"{MainViewModel.PageSideMargin},0,{MainViewModel.PageSideMargin},{MainViewModel.PageGap}", (string?)pageBorder.Attribute("Margin"));
    }

    /// <summary>
    /// Real WPF layout of that structure: at the fit zoom no horizontal bar shows (with the
    /// vertical one showing) and the margins left and right of the page are equal; a larger
    /// zoom scrolls sideways; a smaller one stays in the middle.
    /// </summary>
    [Theory]
    [InlineData(760.0, true)]
    [InlineData(1200.0, true)]
    [InlineData(760.0, false)]
    [InlineData(1200.0, false)]
    public void TestTheFittedPageIsCentredWithNoHorizontalScrollBar(double viewportWidth, bool continuous)
    {
        RunOnSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadDictionary("Themes/LightTheme.xaml"));
            resources.MergedDictionaries.Add(LoadDictionary("Themes/Controls.xaml"));

            var viewer = new ScrollViewer
            {
                Width = viewportWidth,
                Height = 600,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var outer = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            outer.SetBinding(FrameworkElement.MinWidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth)) { Source = viewer });
            var inner = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(MainViewModel.DocumentMargin) };
            var pages = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            double side = continuous ? MainViewModel.PageSideMargin : 0;
            var borders = Enumerable.Range(0, continuous ? 3 : 1)
                .Select(_ => new Border { Background = Brushes.White, Margin = new Thickness(side, 0, side, continuous ? MainViewModel.PageGap : 0) })
                .ToList();
            borders.ForEach(b => pages.Children.Add(b));
            inner.Children.Add(pages);
            outer.Children.Add(inner);
            viewer.Content = outer;

            var window = new Window
            {
                Resources = resources,
                Content = viewer,
                SizeToContent = SizeToContent.WidthAndHeight,
                Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            };
            window.Show();
            try
            {
                const double pageW = 612, pageH = 792;
                void Layout(double zoom)
                {
                    foreach (var b in borders) { b.Width = pageW * zoom; b.Height = pageH * zoom; }
                    window.UpdateLayout();
                    window.UpdateLayout(); // the grid's MinWidth follows the viewport one pass later
                }

                double barWidth = 10; // the vertical bar's width in Controls.xaml
                double fit = MainViewModel.FitWidthZoom(viewer.ActualWidth, barWidth, pageW, 1, side);
                Layout(fit);

                Assert.Equal(Visibility.Visible, viewer.ComputedVerticalScrollBarVisibility);
                Assert.Equal(viewportWidth - barWidth, viewer.ViewportWidth, 3);
                Assert.Equal(Visibility.Collapsed, viewer.ComputedHorizontalScrollBarVisibility);
                AssertCentred(viewer, borders[0]);

                Layout(fit * 2);
                Assert.Equal(Visibility.Visible, viewer.ComputedHorizontalScrollBarVisibility);

                Layout(fit / 2);
                Assert.Equal(Visibility.Collapsed, viewer.ComputedHorizontalScrollBarVisibility);
                AssertCentred(viewer, borders[0]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertCentred(ScrollViewer viewer, FrameworkElement page)
    {
        var topLeft = page.TransformToAncestor(viewer).Transform(new Point(0, 0));
        double left = topLeft.X;
        double right = viewer.ViewportWidth - (topLeft.X + page.ActualWidth);
        Assert.True(left > 0, $"page starts at {left}");
        Assert.Equal(left, right, 1);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Loose XAML from the source tree: a pack:// URI needs a running Application.</summary>
    private static ResourceDictionary LoadDictionary(string path)
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "PdfViewer", path))
            .Replace("clr-namespace:PdfViewer.Themes\"", "clr-namespace:PdfViewer.Themes;assembly=PdfViewer\"");
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "test timed out");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
