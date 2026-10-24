using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PdfEngine.Vector.Accessibility;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.ViewModels;
using PdfViewer.Views.Dialogs;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Tools > Accessibility > Make Accessible: the dialog takes a language and, for a tagged document,
/// whether to replace its tags; it saves a tagged copy under a new name (never over the original),
/// then lists what was tagged, what to review and what still fails; the copy opens and checks
/// clean of tagging failures. The dialog is usable from the keyboard and named for screen readers.
/// </summary>
public class MakeAccessibleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MakeAccessible_" + Guid.NewGuid().ToString("N"));
    public MakeAccessibleTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Fonts = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> " +
                                 "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >> >> >>";

    private string Untagged(string catalog = "")
    {
        var b = new VectorPdfBuilder();
        b.AddPage("BT /F2 20 Tf 40 160 Td (Quarterly figures) Tj ET BT /F1 10 Tf 40 130 Td (Sales rose in every region.) Tj ET", Fonts);
        if (catalog.Length > 0) b.WithCatalog(catalog);
        string path = Path.Combine(_dir, "report.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private string Tagged()
    {
        var b = new VectorPdfBuilder();
        int page = b.AddPage("/P <</MCID 0>> BDC BT /F1 10 Tf 40 130 Td (Tagged text.) Tj ET EMC", Fonts, extra: "/StructParents 0");
        int p = b.Add($"<< /Type /StructElem /S /P /Pg {page} 0 R /K 0 >>");
        int doc = b.Add($"<< /Type /StructElem /S /Document /K [{p} 0 R] >>");
        int root = b.Add($"<< /Type /StructTreeRoot /K {doc} 0 R /ParentTree << /Nums [0 [{p} 0 R]] >> >>");
        b.WithCatalog($"/MarkInfo << /Marked true >> /StructTreeRoot {root} 0 R");
        string path = Path.Combine(_dir, "tagged.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<(MainViewModel Main, MakeAccessibleViewModel Dialog)> Open(string path)
    {
        var main = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await main.LoadDocumentAsync(path);
        var dialog = main.CreateMakeAccessibleViewModel()!;
        await dialog.LoadAsync();
        return (main, dialog);
    }

    [Fact]
    public async Task Untagged_IsTaggedAndSavedAsACopy_ThenOpensAndChecksWithoutTaggingFailures()
    {
        string original = Untagged();
        byte[] before = File.ReadAllBytes(original);
        var (main, dialog) = await Open(original);
        using var mainScope = main;
        Assert.False(dialog.IsTagged);
        Assert.Equal("The document is not tagged.", dialog.TaggedText);
        Assert.Equal("en", dialog.Language);
        Assert.True(dialog.CanTag);
        Assert.False(dialog.HasResult);

        // Never over the original.
        Assert.False(await dialog.SaveAsAsync(original));
        Assert.Contains("original is not overwritten", dialog.Summary, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(original));

        dialog.Language = "en-GB";
        string output = Path.Combine(_dir, "report_accessible.pdf");
        Assert.Equal(output, dialog.SuggestedOutputPath);
        Assert.True(await dialog.SaveAsAsync(output));
        Assert.True(dialog.HasResult);
        Assert.Equal(output, dialog.SavedPath);
        Assert.StartsWith("Saved report_accessible.pdf.", dialog.Summary, StringComparison.Ordinal);
        Assert.Contains("Headings: 1", dialog.Found);
        Assert.Contains("Paragraphs: 1", dialog.Found);
        Assert.Contains(dialog.Found, f => f.StartsWith("Figures needing alternate text: 0", StringComparison.Ordinal));
        Assert.Contains(dialog.Review, r => r.Contains("reading order", StringComparison.Ordinal));
        // What remains needs a person or is not about tags (Helvetica is not embedded).
        Assert.Contains(dialog.Remaining, r => r.Checkpoint == "06-002" && r.Kind == "needs a person");
        Assert.Contains(dialog.Remaining, r => r.Kind == "not about tags");
        Assert.DoesNotContain(dialog.Remaining, r => r.Kind == "not fixed");
        Assert.All(dialog.Remaining, r => Assert.Contains(r.Message, r.AutomationName, StringComparison.Ordinal));
        // Diagnostics never quote the document.
        Assert.DoesNotContain(dialog.Remaining, r => r.Message.Contains("Quarterly", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(original));

        // The copy is tagged in the chosen language; opened and checked, nothing about tags fails.
        using (var copy = await PdfVectorDocument.OpenAsync(File.ReadAllBytes(output)))
        {
            Assert.True(PdfAutoTagger.IsTagged(copy));
            Assert.Equal("en-GB", PdfAutoTagger.DocumentLanguage(copy));
        }
        using var reopened = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await reopened.LoadDocumentAsync(output);
        var check = reopened.CreateAccessibilityCheckViewModel()!;
        await check.CheckAsync();
        Assert.DoesNotContain(check.AllIssues, i => i.Checkpoint is "01-005" or "06-001" or "07-001" or "11-001" or "14-002");
    }

    [Fact]
    public async Task DocumentLanguage_IsOffered_AndAnInvalidOneIsRefused()
    {
        var (main, dialog) = await Open(Untagged("/Lang (de-CH)"));
        using var mainScope = main;
        Assert.Equal("de-CH", dialog.Language);
        Assert.Empty(dialog.LanguageError);
        dialog.Language = "de_CH";
        Assert.False(dialog.CanTag);
        Assert.False(string.IsNullOrEmpty(dialog.LanguageError));
        Assert.False(await dialog.SaveAsAsync(Path.Combine(_dir, "x.pdf")));
        Assert.False(File.Exists(Path.Combine(_dir, "x.pdf")));
    }

    [Fact]
    public async Task TaggedDocument_NeedsTheReplaceOption()
    {
        var (main, dialog) = await Open(Tagged());
        using var mainScope = main;
        Assert.True(dialog.IsTagged);
        Assert.False(dialog.CanTag);
        Assert.Contains("already tagged", dialog.TaggedText, StringComparison.Ordinal);
        dialog.Retag = true;
        Assert.True(dialog.CanTag);
        string output = Path.Combine(_dir, "retagged.pdf");
        Assert.True(await dialog.SaveAsAsync(output));
        Assert.Contains("Paragraphs: 1", dialog.Found);
    }

    [Fact]
    public async Task Command_ShowsTheDialog()
    {
        using var main = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        MakeAccessibleViewModel? shown = null;
        main.ShowMakeAccessibleAction = vm => shown = vm;
        main.ShowMakeAccessibleCommand.Execute(null);
        Assert.Null(shown); // nothing open
        await main.LoadDocumentAsync(Untagged());
        main.ShowMakeAccessibleCommand.Execute(null);
        Assert.NotNull(shown);
        Assert.Equal("report.pdf", shown!.FileName);
    }

    [Fact]
    public async Task Window_IsKeyboardUsableAndNamedForScreenReaders()
    {
        using var main = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await main.LoadDocumentAsync(Untagged());
        string output = Path.Combine(_dir, "window.pdf");
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Continuations come back to this thread, as they do to the application's.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var vm = main.CreateMakeAccessibleViewModel()!;
                var window = new MakeAccessibleWindow(vm, null) { Left = -10000, ShowActivated = false };
                window.Show();
                void Pump(Func<bool> done)
                {
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (!done() && DateTime.UtcNow < until)
                    {
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                        Thread.Sleep(10);
                    }
                }
                Pump(() => vm.Summary.Length > 0);
                window.UpdateLayout();

                var language = (TextBox)window.FindName("LanguageBox");
                Assert.Equal("Document language", AutomationProperties.GetName(language));
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetHelpText(language)));
                Assert.True(language.IsKeyboardFocusWithin || language.IsFocused || FocusManager.GetFocusedElement(window) == language);
                var summary = (TextBlock)window.FindName("SummaryText");
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(summary));
                Assert.False(((CheckBox)window.FindName("RetagBox")).IsEnabled); // nothing to replace
                var save = (Button)window.FindName("SaveButton");
                Assert.True(save.IsEnabled);
                Assert.True(save.IsDefault);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("OpenButton")).Visibility);

                // An invalid language disables saving and says why.
                language.Text = "not a tag";
                window.UpdateLayout();
                Assert.False(save.IsEnabled);
                Assert.False(string.IsNullOrEmpty(((TextBlock)window.FindName("LanguageErrorText")).Text));
                language.Text = "en";
                window.UpdateLayout();
                Assert.True(save.IsEnabled);

                var task = vm.SaveAsAsync(output);
                Pump(() => task.IsCompleted);
                Assert.True(task.Result);
                window.UpdateLayout();
                var remaining = (ListView)window.FindName("RemainingList");
                Assert.Equal("PDF/UA checks the tagged copy still fails", AutomationProperties.GetName(remaining));
                Assert.True(remaining.IsVisible);
                var first = (ListViewItem)remaining.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.Equal(vm.Remaining[0].AutomationName, AutomationProperties.GetName(first));
                Assert.Equal("What was tagged", AutomationProperties.GetName((ListBox)window.FindName("FoundList")));
                Assert.Equal("What a person should review", AutomationProperties.GetName((ListBox)window.FindName("ReviewList")));
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
        Assert.True(File.Exists(output));
    }
}
