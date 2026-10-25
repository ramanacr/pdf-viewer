using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfEngine.Vector.PrintProduction;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using PdfViewer.Views.Dialogs;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Tools > Print Production: Output Preview separates the page through the engine (plates on and off,
/// overprint, total ink warning, readout under the mouse) without touching the main view; Preflight lists
/// results by severity, shows a result on its page, and exports reports without page text.
/// </summary>
public class PrintProductionViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PrintProduction_" + Guid.NewGuid().ToString("N"));
    public PrintProductionViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Spot = "[/Separation /Gold /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0.2 1 0.1] /N 1 >>]";

    private string Sample()
    {
        var b = new VectorPdfBuilder();
        // Page 1: cyan background, overprinting magenta, a 50 % spot, rich black (400 %), secret text.
        b.AddPage("0 0 0 0 k 1 0 0 0 k 0 0 200 200 re f /OP1 gs 0 1 0 0 k 50 50 50 50 re f /G0 gs " +
                  "/CS1 cs 0.5 scn 120 120 40 40 re f 1 1 1 1 k 0 0 20 20 re f BT /F1 12 Tf 10 180 Td (SECRET WORDS) Tj ET",
            $"<< /ColorSpace << /CS1 {Spot} >> /ExtGState << /OP1 << /OP true /op true /OPM 1 >> /G0 << /OP false >> >> " +
            "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");
        b.AddPage("1 0 0 rg 10 10 100 100 re f");
        string path = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var main = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await main.LoadDocumentAsync(path);
        return main;
    }

    /// <summary>The preview pixel of a page point (y up) on a 200-point page.</summary>
    private static (int X, int Y) Pixel(OutputPreviewViewModel vm, double x, double y) =>
        ((int)(x / 200 * vm.PreviewWidth), (int)((200 - y) / 200 * vm.PreviewHeight));

    [Fact]
    public async Task OutputPreview_ListsPlates_ReadsInk_TogglesPlatesAndOverprint()
    {
        using var main = await Open(Sample());
        using var vm = main.CreateOutputPreviewViewModel()!;
        await vm.RenderAsync();

        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "Gold"], vm.Colorants.Select(c => c.Name));
        Assert.Equal(OutputPreviewViewModel.PreviewPixels, vm.PreviewWidth);
        Assert.NotNull(vm.Preview);
        Assert.Contains("1 spot colour", vm.Summary);
        Assert.Contains("total ink up to 400 %", vm.Summary);

        // Inside the overprinting magenta square cyan is kept.
        var (x, y) = Pixel(vm, 75, 75);
        vm.ReadAt(x, y);
        Assert.Equal("100 %", vm.Colorants[0].Readout);
        Assert.Equal("100 %", vm.Colorants[1].Readout);
        Assert.Contains("Total 200 %", vm.Readout);
        var (sx, sy) = Pixel(vm, 140, 140);
        vm.ReadAt(sx, sy);
        Assert.Equal("50 %", vm.Colorants[4].Readout);
        Assert.Equal("0 %", vm.Colorants[0].Readout); // the spot knocks out (no overprint there)

        // Hiding a plate recomposes without a new render.
        var separation = vm.Separation;
        var before = vm.Preview;
        vm.Colorants[0].IsVisible = false;
        Assert.Same(separation, vm.Separation);
        Assert.NotSame(before, vm.Preview);
        Assert.Equal(0b11110UL, vm.VisibleMask);

        // Total ink warning: the 400 % square is highlighted in green.
        vm.Colorants[0].IsVisible = true;
        vm.ShowInkWarning = true;
        var (bx, by) = Pixel(vm, 10, 10);
        var pixel = new byte[4];
        vm.Preview!.CopyPixels(new Int32Rect(bx, by, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 0, 255, 0 }, pixel[..3]);

        // Without overprint simulation the magenta knocks the cyan out.
        vm.SimulateOverprint = false;
        await vm.RenderAsync();
        vm.ReadAt(x, y);
        Assert.Equal("0 %", vm.Colorants[0].Readout);
        Assert.Contains("not simulated", vm.Summary);

        // Page 2 is RGB: no spot plate, process plates through the SWOP model.
        vm.PageNumber = 2;
        await vm.RenderAsync();
        Assert.Equal(4, vm.Colorants.Count);
        vm.ReadAt(Pixel(vm, 50, 50).X, Pixel(vm, 50, 50).Y);
        Assert.NotEqual("0 %", vm.Colorants[1].Readout);
    }

    [Fact]
    public async Task OutputPreview_LeavesTheMainViewAlone()
    {
        using var main = await Open(Sample());
        var page = main.Pages[0];
        int matches = page.MatchesOnPage.Count;
        using var vm = main.CreateOutputPreviewViewModel()!;
        await vm.RenderAsync();
        Assert.Equal(matches, page.MatchesOnPage.Count);
        Assert.Equal(1, main.CurrentPageNumber);
    }

    [Fact]
    public async Task Preflight_GroupsBySeverity_ShowsOnPage_AndExportsWithoutPageText()
    {
        using var main = await Open(Sample());
        var vm = main.CreatePreflightViewModel()!;
        Assert.Equal(PdfPreflightProfile.DigitalPrinting, vm.SelectedProfile);
        await vm.RunAsync();
        Assert.Equal(["Error", "Warning", "Information"], vm.Groups.Select(g => PreflightViewModel.SeverityName(g.Severity)));
        Assert.Contains(vm.AllFindings, f => f.RuleId == "PP-1.1");
        var ink = vm.AllFindings.First(f => f.RuleId == "PP-4.1");
        Assert.Equal(1, ink.Page);
        Assert.Contains("Gold", string.Join(" ", vm.AllFindings.Where(f => f.RuleId == "PP-3.3").Select(f => f.Message)));

        // Selecting a result goes to its page and highlights it there.
        var rgb = vm.AllFindings.First(f => f.RuleId == "PP-3.1");
        Assert.Equal(2, rgb.Page);
        vm.SelectedFinding = rgb;
        Assert.Equal(2, main.CurrentPageNumber);
        Assert.True(main.HasAccessibilityHighlight(2));
        var highlight = main.Pages[1].MatchesOnPage.Single(m => m.ContextSnippet == "Preflight finding");
        Assert.Equal(0.05, highlight.X, 3);
        Assert.Equal(0.45, highlight.Y, 3); // 100 points from the top of 200
        Assert.Equal(0.5, highlight.Width, 3);

        foreach (bool json in new[] { false, true })
        {
            string report = vm.Export(json)!;
            Assert.DoesNotContain("SECRET", report);
            Assert.Contains("PP-4.1", report);
        }
        Assert.EndsWith("print_preflight.json", vm.SuggestedReportName(json: true));

        vm.SelectedProfile = PdfPreflightProfile.PdfX1aReadiness;
        await vm.RunAsync();
        Assert.Contains(vm.AllFindings, f => f.RuleId == "PP-8.1");
        Assert.Contains("nothing is converted", vm.ProfileDescription);
    }

    private static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public async Task Windows_AreNamedForScreenReaders()
    {
        using var main = await Open(Sample());
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var preview = main.CreateOutputPreviewViewModel()!;
                var window = new OutputPreviewWindow(preview) { Left = -10000, ShowActivated = false };
                window.Show();
                Pump(() => preview.Preview != null && !preview.IsBusy);
                window.UpdateLayout();
                Assert.NotNull(preview.Preview);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting((TextBlock)window.FindName("SummaryText")));
                var plates = (ListBox)window.FindName("PlateList");
                Assert.Equal("Plates", AutomationProperties.GetName(plates));
                var first = (ListBoxItem)plates.ItemContainerGenerator.ContainerFromIndex(0);
                // The name carries the ink under the mouse when the pointer is over the preview.
                Assert.StartsWith("Cyan, process plate", AutomationProperties.GetName(first));
                window.ShowPage(2);
                Pump(() => preview.PageNumber == 2 && !preview.IsBusy && preview.Colorants.Count == 4);
                Assert.Equal(4, preview.Colorants.Count);
                window.Close();

                var preflight = main.CreatePreflightViewModel()!;
                var pw = new PreflightWindow(preflight, null) { Left = -10000, ShowActivated = false };
                pw.Show();
                var run = preflight.RunAsync();
                Pump(() => run.IsCompleted);
                pw.UpdateLayout();
                Assert.Equal("Preflight profile", AutomationProperties.GetName((ComboBox)pw.FindName("ProfileBox")));
                Assert.Equal("Preflight results by severity", AutomationProperties.GetName((TreeView)pw.FindName("FindingTree")));
                Assert.True(preflight.HasReport);
                pw.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
