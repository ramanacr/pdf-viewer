using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Rendering;
using PdfEngine.Safety;
using PdfViewer.ViewModels;
using PdfViewer.Views.Dialogs;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers the navigation history and the page organizer's own logic - the parts a user drives
/// directly and that no engine test would reach.
/// </summary>
public class NavigationAndOrganizerUiTests : IDisposable
{
    private readonly string _testDir;

    public NavigationAndOrganizerUiTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfNavTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private async Task<MainViewModel> LoadedViewModel(int pageCount)
    {
        string path = TestPdfBuilder.CreateSimplePdf(
            Path.Combine(_testDir, $"nav_{Guid.NewGuid():N}.pdf"), pageCount, "NavToken");

        var vm = new MainViewModel();
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    [Fact]
    public async Task TestBackAndForwardRetraceDeliberateJumps()
    {
        var vm = await LoadedViewModel(10);

        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanGoForward);

        vm.NavigateToPage(5);
        vm.NavigateToPage(9);

        Assert.Equal(9, vm.CurrentPageNumber);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);

        vm.GoBack();
        Assert.Equal(5, vm.CurrentPageNumber);
        Assert.True(vm.CanGoForward);

        vm.GoBack();
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.False(vm.CanGoBack);

        vm.GoForward();
        Assert.Equal(5, vm.CurrentPageNumber);

        vm.GoForward();
        Assert.Equal(9, vm.CurrentPageNumber);
        Assert.False(vm.CanGoForward);
    }

    /// <summary>
    /// Jumping somewhere new after going back discards the forward trail, the same way a
    /// browser does. Keeping it would offer to move you somewhere you never were.
    /// </summary>
    [Fact]
    public async Task TestANewJumpClearsTheForwardTrail()
    {
        var vm = await LoadedViewModel(10);

        vm.NavigateToPage(4);
        vm.NavigateToPage(8);
        vm.GoBack();

        Assert.True(vm.CanGoForward);

        vm.NavigateToPage(2);

        Assert.False(vm.CanGoForward);
        Assert.Equal(2, vm.CurrentPageNumber);
    }

    /// <summary>
    /// Scrolling is not a jump. If it were recorded, "back" would undo reading rather than
    /// returning you to the bookmark you followed.
    /// </summary>
    [Fact]
    public async Task TestScrollingDoesNotFillTheHistory()
    {
        var vm = await LoadedViewModel(10);

        vm.CurrentPageNumber = 4;
        vm.CurrentPageNumber = 5;
        vm.CurrentPageNumber = 6;

        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public async Task TestHistoryDoesNotSurviveClosingTheDocument()
    {
        var vm = await LoadedViewModel(6);

        vm.NavigateToPage(4);
        Assert.True(vm.CanGoBack);

        vm.CloseDocument();

        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public async Task TestNavigatingNowhereIsNotRecorded()
    {
        var vm = await LoadedViewModel(5);

        vm.NavigateToPage(3);
        vm.NavigateToPage(3);
        vm.NavigateToPage(99);   // out of range: ignored
        vm.NavigateToPage(0);    // out of range: ignored

        vm.GoBack();
        Assert.Equal(1, vm.CurrentPageNumber);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public void TestOrganizerBuildsTheArrangementItShows()
    {
        RunOnUiThread(() =>
        {
            var dialog = new OrganizePagesDialog(4, "sample.pdf");

            // Starts as the identity arrangement.
            Assert.Equal(new[] { 1, 2, 3, 4 }, dialog.BuildArrangement().Select(e => e.SourcePageNumber));
            Assert.All(dialog.BuildArrangement(), e => Assert.Equal(PageRotation.Rotate0, e.Rotation));

            // Rotating twice to the right is 180; a third is 270; a fourth is back to zero.
            dialog.Entries[0].QuarterTurns += 2;
            Assert.Equal(PageRotation.Rotate180, dialog.BuildArrangement()[0].Rotation);

            dialog.Entries[0].QuarterTurns += 2;
            Assert.Equal(PageRotation.Rotate0, dialog.BuildArrangement()[0].Rotation);

            // Rotating left from zero wraps to 270 rather than going negative.
            dialog.Entries[1].QuarterTurns -= 1;
            Assert.Equal(PageRotation.Rotate270, dialog.BuildArrangement()[1].Rotation);

            // Removing and reordering are reflected in the arrangement, in order.
            var third = dialog.Entries[2];
            dialog.Entries.RemoveAt(2);
            dialog.Entries.Insert(0, third);

            Assert.Equal(new[] { 3, 1, 2, 4 }, dialog.BuildArrangement().Select(e => e.SourcePageNumber));

            dialog.Entries.RemoveAt(3);
            Assert.Equal(new[] { 3, 1, 2 }, dialog.BuildArrangement().Select(e => e.SourcePageNumber));

            // Positions renumber so the list the user reads matches what will be written.
            Assert.Equal(new[] { 1, 2, 3 }, dialog.Entries.Select(e => e.Position));

            dialog.Close();
        });
    }

    [Fact]
    public void TestOrganizerDialogRefusesToProduceAnEmptyDocument()
    {
        RunOnUiThread(() =>
        {
            var dialog = new OrganizePagesDialog(2, "sample.pdf");

            dialog.Entries.RemoveAt(0);
            dialog.Entries.RemoveAt(0);

            // With no pages left there is nothing valid to save, and the button says so.
            Assert.Empty(dialog.BuildArrangement());
            Assert.False(dialog.SaveButton.IsEnabled);

            dialog.Close();
        });
    }

    /// <summary>
    /// The attachments dialog is the only place a user can pull a payload out of a document,
    /// so its warning column has to be right and its selection has to survive the round trip.
    /// </summary>
    [Fact]
    public void TestAttachmentsDialogShowsRiskAndReturnsTheChosenFile()
    {
        var files = new[]
        {
            new EmbeddedFileInfo { Index = 0, Name = "notes.txt", SizeBytes = 2048 },
            new EmbeddedFileInfo { Index = 1, Name = "setup.exe", SizeBytes = 5 * 1024 * 1024, HasExecutableExtension = true }
        };

        RunOnUiThread(() =>
        {
            var dialog = new AttachmentsDialog(files, "carrier.pdf");

            var rows = dialog.FilesGrid.ItemsSource.Cast<AttachmentsDialog.Row>().ToList();
            Assert.Equal(2, rows.Count);

            Assert.Equal("2.0 KB", rows[0].SizeText);
            Assert.Equal("", rows[0].RiskText);

            Assert.Equal("5.0 MB", rows[1].SizeText);
            Assert.Equal("Executable type", rows[1].RiskText);

            // Nothing is chosen until the user chooses it.
            Assert.Null(dialog.Chosen);
            Assert.Contains("executable", dialog.SubtitleText.Text, StringComparison.OrdinalIgnoreCase);

            dialog.Close();
        });
    }

    private static void RunOnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
