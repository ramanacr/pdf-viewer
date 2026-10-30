using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Models;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// A text highlight read from a file is drawn by the page from its appearance stream (with its
/// Multiply blend mode, so the text under it stays readable), not by the WPF overlay, which can
/// only blend: drawn over the page with the highlight's /CA of 1, the overlay hid the text. A
/// highlight the overlay draws (made here, or changed) is never more opaque than
/// <see cref="AnnotationModel.MaxHighlightOverlayOpacity"/>.
/// </summary>
[Collection(SigningSettingsCollection.Name)]
public class HighlightOverlayViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HighlightOverlay_" + Guid.NewGuid().ToString("N"));

    public HighlightOverlayViewerTests()
    {
        Directory.CreateDirectory(_dir);
        SigningSettings.SetDirectoryForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        return vm;
    }

    /// <summary>
    /// A page with a pink highlight as another program writes it: /C, no /CA, and an appearance
    /// whose graphics state sets /BM /Multiply and alpha 0.6. Made here, not taken from anywhere.
    /// </summary>
    private string HighlightedFile()
    {
        var b = new VectorPdfBuilder();
        int ap = b.AddStream(
            "/Type /XObject /Subtype /Form /BBox [0 0 200 20] " +
            "/Resources << /ExtGState << /GS0 << /BM /Multiply /ca 0.6 /CA 0.6 >> >> >>",
            "/GS0 gs 0.9569 0.7647 0.7961 rg 0 0 200 20 re f");
        int annot = b.Add(
            "<< /Type /Annot /Subtype /Highlight /Rect [100 600 300 620] /F 4 /C [0.9569 0.7647 0.7961] " +
            $"/QuadPoints [100 620 300 620 100 600 300 600] /AP << /N {ap} 0 R >> >>");
        b.AddPage("0 g 110 605 180 8 re f", mediaBox: "[0 0 612 792]", extra: $"/Annots [{annot} 0 R]");
        string path = Path.Combine(_dir, "highlighted.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public async Task LoadedHighlight_IsDrawnByThePage_UntilItChanges()
    {
        using var vm = await Open(HighlightedFile());
        var highlight = Assert.Single(vm.AllAnnotations);
        Assert.Equal(AnnotationType.Highlight, highlight.Type);
        Assert.Equal(1.0, highlight.Opacity, 3); // no /CA: fully opaque as the file says
        Assert.True(highlight.HasOwnAppearance);
        Assert.True(highlight.IsDrawnByPage); // the page draws it, multiplied: the overlay does not cover the text

        vm.SelectAnnotation(highlight);
        Assert.True(vm.NudgeSelectedAnnotation(1, 0, large: true));
        Assert.False(highlight.IsDrawnByPage); // changed: the overlay draws it until it is saved...
        Assert.Equal(AnnotationModel.MaxHighlightOverlayOpacity, highlight.HighlightOverlayOpacity, 3); // ...never opaque

        await vm.UndoAsync();
        Assert.True(highlight.IsDrawnByPage);
    }

    [Fact]
    public async Task HighlightMadeHere_IsDrawnByTheOverlay_ThenByThePageOnceSaved()
    {
        string path = HighlightedFile();
        using (var vm = await Open(path))
        {
            var made = new AnnotationModel
            {
                PageNumber = 1, Type = AnnotationType.Highlight, X = 0.2, Y = 0.5, Width = 0.3, Height = 0.03,
                ColorHex = "#FFFF00", Opacity = 0.4,
            };
            vm.AddAnnotation(made);
            Assert.False(made.IsDrawnByPage);
            Assert.Equal(0.4, made.HighlightOverlayOpacity, 3); // as it is saved: the same tint over white paper
            made.Opacity = 1;
            Assert.Equal(AnnotationModel.MaxHighlightOverlayOpacity, made.HighlightOverlayOpacity, 3);
            made.Opacity = 0.4;
            await vm.SaveAsync();
        }

        using var reopened = await Open(path);
        Assert.Equal(2, reopened.AllAnnotations.Count);
        Assert.All(reopened.AllAnnotations, a => Assert.True(a.IsDrawnByPage));
    }
}
