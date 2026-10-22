using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using PdfViewer.Views;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Undo and redo of every annotation change, on the one history a document has (shared with
/// Edit Text &amp; Images), and closing a window whose annotation changes are thrown away.
/// </summary>
public class AnnotationUndoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AnnotationUndo_" + Guid.NewGuid().ToString("N"));

    public AnnotationUndoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(MainViewModel Vm, string Path)> Open(int pages = 2)
    {
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, $"undo_{Guid.NewGuid():N}.pdf"), pages, "UndoToken");
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return (vm, path);
    }

    private static AnnotationModel Markup(AnnotationType type = AnnotationType.Highlight, int page = 1, string contents = "") => new()
    {
        PageNumber = page,
        Type = type,
        X = 0.1, Y = 0.2, Width = 0.3, Height = 0.05,
        ColorHex = "#FFFF00",
        Opacity = 0.4,
        StrokeThickness = 2,
        Contents = contents,
    };

    private static void AssertOnPage(MainViewModel vm, AnnotationModel annot, int page, int index)
    {
        Assert.Same(annot, vm.Pages[page - 1].AnnotationsOnPage[index]);
        for (int p = 1; p <= vm.Pages.Count; p++)
            if (p != page) Assert.DoesNotContain(annot, vm.Pages[p - 1].AnnotationsOnPage);
    }

    [Fact]
    public async Task UndoingAnAdd_RemovesIt_AndRedoPutsTheSameAnnotationBack()
    {
        var (vm, _) = await Open();
        Assert.False(vm.CanUndo);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("_Undo", vm.UndoHeader);

        var annot = Markup();
        vm.AddAnnotation(annot);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.Equal("_Undo Add Highlight", vm.UndoHeader);

        await vm.UndoAsync();
        Assert.Empty(vm.AllAnnotations);              // the sidebar's list
        Assert.Empty(vm.Pages[0].AnnotationsOnPage);
        Assert.False(vm.HasUnsavedChanges);           // back to the file as opened
        Assert.DoesNotContain("*", vm.WindowTitle);
        Assert.False(vm.CanUndo);
        Assert.True(vm.RedoCommand.CanExecute(null));
        Assert.Equal("Red_o Add Highlight", vm.RedoHeader);

        await vm.RedoAsync();
        Assert.Same(annot, Assert.Single(vm.AllAnnotations));
        AssertOnPage(vm, annot, 1, 0);
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public async Task UndoingADelete_PutsItBackWhereItWas_InTheListAndInTheStackingOrder()
    {
        var (vm, _) = await Open();
        var bottom = Markup(AnnotationType.Rectangle);
        var middle = Markup(AnnotationType.Ellipse, contents: "middle");
        var top = Markup(AnnotationType.Note);
        vm.AddAnnotation(bottom);
        vm.AddAnnotation(middle);
        vm.AddAnnotation(top);
        var inkStrokes = new List<List<Point>> { new() { new(0.1, 0.1), new(0.2, 0.3) } };
        middle.InkStrokes = inkStrokes;

        vm.DeleteAnnotation(middle);
        Assert.Equal(new[] { bottom, top }, vm.AllAnnotations);
        Assert.Equal("_Undo Delete Ellipse", vm.UndoHeader);

        await vm.UndoAsync();
        Assert.Equal(new[] { bottom, middle, top }, vm.AllAnnotations);
        Assert.Equal(new[] { bottom, middle, top }, vm.Pages[0].AnnotationsOnPage);
        Assert.Equal("middle", middle.Contents);
        Assert.Equal(0.3, middle.InkStrokes[0][1].Y);

        await vm.RedoAsync();
        Assert.Equal(new[] { bottom, top }, vm.Pages[0].AnnotationsOnPage);
    }

    [Fact]
    public async Task MovingResizingAndRestyling_AreEachOneStep_UndoneExactly()
    {
        var (vm, _) = await Open();
        var annot = Markup(AnnotationType.Highlight);
        annot.SetQuads(new[] { new Rect(0.1, 0.2, 0.3, 0.02), new Rect(0.1, 0.23, 0.2, 0.02) });
        vm.AddAnnotation(annot);
        var quads = annot.Quads.ToList();
        var modified = annot.ModifiedDate;

        vm.ChangeAnnotation(annot, a => { a.X += 0.1; a.Y += 0.05; }, "Move Highlight");
        Assert.Equal(0.2, annot.X, 6);
        Assert.Equal(quads[1].Y + 0.05, annot.Quads[1].Y, 6); // the lines went with it
        vm.ChangeAnnotation(annot, a => { a.Width = 0.6; a.Height = 0.1; }, "Resize Highlight");
        vm.ChangeAnnotation(annot, a => { a.ColorHex = "#FF0000"; a.Opacity = 0.9; a.StrokeThickness = 5; }, "Change Highlight Properties");
        vm.ChangeAnnotation(annot, a => a.Contents = "reworded", "Edit Comment");
        Assert.Equal("_Undo Edit Comment", vm.UndoHeader);

        await vm.UndoAsync();
        Assert.Equal(string.Empty, annot.Contents);
        Assert.Equal("_Undo Change Highlight Properties", vm.UndoHeader);
        await vm.UndoAsync();
        Assert.Equal("#FFFF00", annot.ColorHex);
        Assert.Equal(0.4, annot.Opacity);
        Assert.Equal(2, annot.StrokeThickness);
        await vm.UndoAsync();
        Assert.Equal(0.3, annot.Width, 6);
        Assert.Equal(0.05, annot.Height, 6);
        await vm.UndoAsync();
        Assert.Equal(0.1, annot.X, 6);
        Assert.Equal(0.2, annot.Y, 6);
        Assert.Equal(quads, annot.Quads);
        Assert.Equal(modified, annot.ModifiedDate);
        Assert.Same(annot, vm.Pages[0].AnnotationsOnPage.Single());

        // And forward again, all the way.
        for (int i = 0; i < 4; i++) await vm.RedoAsync();
        Assert.Equal(0.2, annot.X, 6);
        Assert.Equal(0.6, annot.Width, 6);
        Assert.Equal("#FF0000", annot.ColorHex);
        Assert.Equal("reworded", annot.Contents);
    }

    [Fact]
    public async Task AnEditMadeInPlace_ThenReported_IsUndoneToItsEarlierText()
    {
        // The comment dialog changes the annotation itself and then reports the edit.
        var (vm, _) = await Open();
        var note = Markup(AnnotationType.Note, contents: "first draft");
        vm.AddAnnotation(note);

        note.Contents = "second draft";
        note.ColorHex = "#2979FF";
        vm.NoteAnnotationEdited(note);
        Assert.Equal("_Undo Edit Comment", vm.UndoHeader);

        await vm.UndoAsync();
        Assert.Equal("first draft", note.Contents);
        Assert.Equal("#FFFF00", note.ColorHex);
        await vm.RedoAsync();
        Assert.Equal("second draft", note.Contents);

        // Confirming the dialog without changing anything is not an edit.
        vm.NoteAnnotationEdited(note);
        Assert.Equal("_Undo Edit Comment", vm.UndoHeader);
        await vm.UndoAsync();
        Assert.Equal("first draft", note.Contents);
        Assert.Equal("_Undo Add Note", vm.UndoHeader);
    }

    [Fact]
    public async Task UndoingClearAll_BringsEveryAnnotationBackInOrder()
    {
        var (vm, _) = await Open(pages: 3);
        var a = Markup(page: 1);
        var b = Markup(AnnotationType.Ink, page: 3);
        var c = Markup(AnnotationType.FreeText, page: 1);
        foreach (var x in new[] { a, b, c }) vm.AddAnnotation(x);

        vm.ClearAllAnnotations();
        Assert.Empty(vm.AllAnnotations);
        Assert.All(vm.Pages, p => Assert.Empty(p.AnnotationsOnPage));

        await vm.UndoAsync();
        Assert.Equal(new[] { a, b, c }, vm.AllAnnotations);
        Assert.Equal(new[] { a, c }, vm.Pages[0].AnnotationsOnPage);
        Assert.Equal(new[] { b }, vm.Pages[2].AnnotationsOnPage);
        Assert.Equal("_Undo Add Text Box", vm.UndoHeader);
    }

    [Fact]
    public async Task ANewChange_AfterUndo_ClearsRedo()
    {
        var (vm, _) = await Open();
        vm.AddAnnotation(Markup(AnnotationType.Underline));
        await vm.UndoAsync();
        Assert.True(vm.CanRedo);

        vm.AddAnnotation(Markup(AnnotationType.StrikeOut));
        Assert.False(vm.CanRedo);
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.Equal("Red_o", vm.RedoHeader);
        Assert.Equal("_Undo Add Strikeout", vm.UndoHeader);
        Assert.Equal(AnnotationType.StrikeOut, Assert.Single(vm.AllAnnotations).Type);
    }

    [Fact]
    public async Task UndoingBackToWhatWasSaved_ClearsTheUnsavedFlag_AndPastItSetsItAgain()
    {
        var (vm, path) = await Open();
        var kept = Markup(contents: "kept");
        vm.AddAnnotation(kept);
        await vm.SaveAsync();
        Assert.False(vm.HasUnsavedChanges);

        vm.ChangeAnnotation(kept, a => a.ColorHex = "#00E676", "Change Colour");
        vm.AddAnnotation(Markup(AnnotationType.Rectangle));
        Assert.True(vm.HasUnsavedChanges);

        await vm.UndoAsync();
        Assert.True(vm.HasUnsavedChanges);   // the colour change is still there
        await vm.UndoAsync();
        Assert.False(vm.HasUnsavedChanges);  // exactly what is on disk
        Assert.False(vm.SaveCommand.CanExecute(null));

        await vm.UndoAsync();                // before the save: the file has the highlight, this does not
        Assert.True(vm.HasUnsavedChanges);
        Assert.Empty(vm.AllAnnotations);
        await vm.RedoAsync();
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task UndoingAChangeToALoadedAnnotation_KeepsItAsTheFileHasIt()
    {
        var (first, path) = await Open();
        first.AddAnnotation(Markup(contents: "from the file"));
        await first.SaveAsync();

        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        var loaded = Assert.Single(vm.AllAnnotations);
        string asLoaded = NativeDocumentServices.Fingerprint(loaded);

        vm.ChangeAnnotation(loaded, a => { a.ColorHex = "#FF0000"; a.X += 0.1; }, "Move Highlight");
        vm.DeleteAnnotation(loaded);
        await vm.UndoAsync();
        await vm.UndoAsync();

        // Unchanged as far as a save can tell, so its own appearance is kept rather than drawn again.
        Assert.Equal(asLoaded, NativeDocumentServices.Fingerprint(loaded));
        Assert.Same(loaded, Assert.Single(vm.AllAnnotations));
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task UndoWorksOutsideEditTextAndImages()
    {
        var (vm, _) = await Open();
        Assert.False(vm.IsEditingContent);
        vm.AddAnnotation(Markup());

        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Empty(vm.AllAnnotations);
        await vm.RedoCommand.ExecuteAsync(null);
        Assert.Single(vm.AllAnnotations);
    }

    [Fact]
    public async Task CommentsAndContentEdits_ShareOneHistory()
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.AddPage("BT /F1 14 Tf 40 700 Td (Invoice total: 120 dollars) Tj ET", $"<< /Font << /F1 {font} 0 R >> >>", mediaBox: "[0 0 612 792]");
        string path = Path.Combine(_dir, "edit.pdf");
        File.WriteAllBytes(path, b.Build());
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        byte[] opened = vm.CurrentDocumentBytes()!;

        var highlight = Markup();
        vm.AddAnnotation(highlight);
        vm.IsEditingContent = true;
        await vm.EnsureEditItemsAsync(vm.Pages[0]);
        var text = vm.Pages[0].EditItems.Single(i => i.Kind == ContentEditKind.Text);
        Assert.True(await vm.CommitTextEditAsync(text, "Invoice total: 150 dollars"));
        vm.IsEditingContent = false;
        Assert.Equal("_Undo Edit Text", vm.UndoHeader);

        await vm.UndoAsync();                 // the text edit, the last change
        Assert.Same(opened, vm.CurrentDocumentBytes());
        Assert.Single(vm.AllAnnotations);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal("_Undo Add Highlight", vm.UndoHeader);

        await vm.UndoAsync();                 // then the highlight
        Assert.Empty(vm.AllAnnotations);
        Assert.False(vm.HasUnsavedChanges);

        await vm.RedoAsync();
        await vm.RedoAsync();
        Assert.NotSame(opened, vm.CurrentDocumentBytes());
        Assert.Single(vm.AllAnnotations);
    }

    [Fact]
    public void TheEditMenuShowsWhatUndoAndRedoWillDo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var items = XDocument.Load(Path.Combine(dir!.FullName, "src", "PdfViewer", "Views", "MainWindow.xaml"))
            .Descendants(wpf + "MenuItem").ToList();
        var undo = Assert.Single(items, m => (string?)m.Attribute("InputGestureText") == "Ctrl+Z");
        var redo = Assert.Single(items, m => (string?)m.Attribute("InputGestureText") == "Ctrl+Y");
        Assert.Equal("{Binding UndoCommand}", (string?)undo.Attribute("Command"));
        Assert.Equal("{Binding UndoHeader}", (string?)undo.Attribute("Header"));
        Assert.Equal("{Binding RedoCommand}", (string?)redo.Attribute("Command"));
        Assert.Equal("{Binding RedoHeader}", (string?)redo.Attribute("Header"));
    }

    // ------------------------------------------------------------------ closing

    [Fact]
    public async Task ClosingWithDiscardedAnnotations_IsQuick_AndWritesNothing()
    {
        var shell = new ShellViewModel();
        string path = TestPdfBuilder.CreateSimplePdf(Path.Combine(_dir, "close.pdf"), 12, "CloseToken");
        byte[] original = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var vm = await shell.OpenDocumentAsync(path);
        Assert.NotNull(vm);
        for (int i = 0; i < 12; i++) vm!.AddAnnotation(Markup((AnnotationType)(i % 8), page: 1 + i));
        bool asked = false;
        vm!.ConfirmSaveBeforeClosingFunc = _ => { asked = true; return false; };   // No

        var watch = Stopwatch.StartNew();
        var confirm = shell.ConfirmCloseAllAsync();
        // "No" needs no save, so the answer is there before the call returns - which is why the
        // window must not close itself from inside its Closing event (see ClosePrompt).
        Assert.True(confirm.IsCompleted);
        Assert.True(await confirm);
        shell.ShutdownAll();
        watch.Stop();

        Assert.True(asked);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"closing took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(_dir).Where(f => f != path));   // no scratch file either
    }

    [Theory]
    [InlineData(true, true)]    // "No": the answer arrives while the window is still closing
    [InlineData(true, false)]   // "Yes": the answer arrives after the save
    [InlineData(false, true)]   // "Cancel"
    public void TheWindowClosesOnceTheAnswerAllowsIt_HoweverSoonItComes(bool close, bool synchronous)
    {
        RunOnSta(() =>
        {
            Exception? unhandled = null;
            Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { unhandled = e.Exception; e.Handled = true; };

            var window = new Window { Width = 200, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            bool unsaved = true;
            int asked = 0;
            var prompt = new ClosePrompt(window, () => unsaved, async () =>
            {
                asked++;
                if (!synchronous) await Task.Delay(50);
                return close;
            });
            window.Closing += (_, e) => prompt.OnClosing(e);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();

            var watch = Stopwatch.StartNew();
            window.Close();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
            {
                if (closed || watch.ElapsedMilliseconds > 2000) frame.Continue = false;
            }, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();

            Assert.Null(unhandled);
            Assert.Equal(1, asked);
            Assert.Equal(close, closed);
            if (close) Assert.True(watch.ElapsedMilliseconds < 300 + (synchronous ? 0 : 50), $"closing took {watch.ElapsedMilliseconds} ms");
            if (!closed)
            {
                unsaved = false;   // nothing left to ask about: the next close goes straight through
                window.Close();
                Assert.True(closed);
            }
        });
    }

    private static void RunOnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "test timed out");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
