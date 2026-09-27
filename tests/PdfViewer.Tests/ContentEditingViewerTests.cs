using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfEngine.Pdfium;
using PdfEngine.Vector.Tests.Fixtures;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Editing text and images in the viewer: the edit mode offers each page's paragraphs and
/// images; edits become an in-memory revision that renders, searches and saves; undo and redo
/// step between revisions; pictures from files are placed losslessly with their transparency.
/// </summary>
public class ContentEditingViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ContentEditViewer_" + Guid.NewGuid().ToString("N"));
    public ContentEditingViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Document(int rotate = 0)
    {
        var b = new VectorPdfBuilder();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var red = new byte[4 * 4 * 3];
        for (int i = 0; i < 16; i++) red[i * 3] = 255;
        int image = b.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8", red, flate: true);
        b.AddPage("BT /F1 14 Tf 40 700 Td (Invoice total: 120 dollars) Tj ET q 100 0 0 100 40 500 cm /Im1 Do Q",
            $"<< /Font << /F1 {font} 0 R >> /XObject << /Im1 {image} 0 R >> >>", mediaBox: "[0 0 612 792]",
            extra: rotate != 0 ? $"/Rotate {rotate}" : string.Empty, flate: true);
        string path = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    private static async Task<MainViewModel> Open(string path)
    {
        var vm = new MainViewModel { ShowMessageBoxAction = (_, _, _, _) => { } };
        await vm.LoadDocumentAsync(path);
        vm.IsEditingContent = true;
        await vm.EnsureEditItemsAsync(vm.Pages[0]);
        return vm;
    }

    private static async Task<string> Text(byte[] pdf)
    {
        using var engine = new PdfiumEngine();
        await using var doc = await engine.OpenDocumentAsync(pdf, null);
        return await engine.TextService.ExtractPageTextAsync(doc, 1);
    }

    [Fact]
    public async Task EditMode_OffersThePagesParagraphsAndImages_WhereTheyAre()
    {
        var vm = await Open(Document());
        var items = vm.Pages[0].EditItems;
        var text = Assert.Single(items, i => i.Kind == ContentEditKind.Text);
        Assert.Equal("Invoice total: 120 dollars", text.Text);
        Assert.Equal(14, text.FontSize, 2);
        Assert.InRange(text.Bounds.X, 40 / 612.0 - 0.01, 40 / 612.0 + 0.01);
        var image = Assert.Single(items, i => i.Kind == ContentEditKind.Image);
        Assert.Equal(new Rect(40 / 612.0, 1 - 600 / 792.0, 100 / 612.0, 100 / 792.0).Round(), image.Bounds.Round());

        vm.IsEditingContent = false;
        Assert.Empty(vm.Pages[0].EditItems);
    }

    [Fact]
    public async Task EditingText_IsARevision_ThatUndoesRedoesAndSaves()
    {
        string path = Document();
        byte[] original = File.ReadAllBytes(path);
        var vm = await Open(path);
        var text = vm.Pages[0].EditItems.Single(i => i.Kind == ContentEditKind.Text);
        Assert.True(await vm.CommitTextEditAsync(text, "Invoice total: 150 dollars"));
        Assert.True(vm.HasUnsavedChanges);
        Assert.Contains("150 dollars", await Text(vm.CurrentDocumentBytes()));
        // The page's items were read again from the new revision.
        Assert.Equal("Invoice total: 150 dollars", vm.Pages[0].EditItems.Single(i => i.Kind == ContentEditKind.Text).Text);

        await vm.UndoContentEditAsync();
        Assert.Contains("120 dollars", await Text(vm.CurrentDocumentBytes()));
        await vm.RedoContentEditAsync();
        Assert.Contains("150 dollars", await Text(vm.CurrentDocumentBytes()));

        await vm.SaveAsync();
        byte[] saved = File.ReadAllBytes(path);
        Assert.Contains("150 dollars", await Text(saved));
        Assert.Equal(original, saved.AsSpan(0, original.Length).ToArray()); // an incremental update: the original bytes come first
    }

    [Fact]
    public async Task Images_Move_Resize_Turn_AndGo()
    {
        var vm = await Open(Document());
        ContentEditItem Image() => vm.Pages[0].EditItems.Single(i => i.Kind == ContentEditKind.Image);
        var before = Image().Bounds;
        Assert.True(await vm.MoveEditItemAsync(Image(), new Vector(0.1, 0.05)));
        Assert.Equal(before.X + 0.1, Image().Bounds.X, 3);
        Assert.Equal(before.Y + 0.05, Image().Bounds.Y, 3);

        var moved = Image().Bounds;
        Assert.True(await vm.ResizeEditItemAsync(Image(), new Rect(moved.X, moved.Y, moved.Width * 2, moved.Height)));
        Assert.Equal(moved.Width * 2, Image().Bounds.Width, 3);

        var wide = Image().Bounds;
        Assert.True(await vm.RotateEditItemAsync(Image(), 90));
        // A quarter turn about the centre: the width and height (in points) swap.
        Assert.Equal(wide.Width * 612, Image().Bounds.Height * 792, 1);
        Assert.Equal(wide.Height * 792, Image().Bounds.Width * 612, 1);

        Assert.True(await vm.DeleteEditItemAsync(Image()));
        Assert.DoesNotContain(vm.Pages[0].EditItems, i => i.Kind == ContentEditKind.Image);
    }

    [Fact]
    public async Task NewText_IsUpright_OnARotatedPage()
    {
        var vm = await Open(Document(rotate: 90));
        Assert.True(await vm.AddTextAtAsync(1, new Point(0.2, 0.2), "Approved"));
        var added = vm.Pages[0].EditItems.Single(i => i.Kind == ContentEditKind.Text && i.Text == "Approved");
        Assert.Equal(90, added.AngleDegrees, 1); // runs along the page's rotation, so it reads upright
        Assert.Contains("Approved", await Text(vm.CurrentDocumentBytes()));
    }

    [Fact]
    public async Task PicturesFromFiles_ArePlaced_JpegAsItIs_PngWithTransparency()
    {
        string png = Path.Combine(_dir, "stamp.png");
        var pixels = new byte[20 * 10 * 4];
        for (int i = 0; i < 200; i++) { pixels[i * 4 + 2] = 255; pixels[i * 4 + 3] = (byte)(i % 20 < 10 ? 255 : 0); }
        Save(new PngBitmapEncoder(), BitmapSource.Create(20, 10, 96, 96, PixelFormats.Bgra32, null, pixels, 80), png);
        var loaded = EditImageLoader.Load(png);
        Assert.Equal(PdfEngine.Vector.Editing.PdfImageEncoding.Rgb, loaded.Encoding);
        Assert.NotNull(loaded.Alpha);
        Assert.Equal(255, loaded.Data[0]); // red

        string jpg = Path.Combine(_dir, "photo.jpg");
        var rgb = new byte[16 * 16 * 3];
        Save(new JpegBitmapEncoder { QualityLevel = 90 }, BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr24, null, rgb, 48), jpg);
        var jpeg = EditImageLoader.Load(jpg);
        Assert.Equal(PdfEngine.Vector.Editing.PdfImageEncoding.Jpeg, jpeg.Encoding);
        Assert.Equal(File.ReadAllBytes(jpg), jpeg.Data); // embedded unchanged: no second compression

        var vm = await Open(Document());
        Assert.True(await vm.AddImageFromFileAsync(1, png));
        Assert.Equal(2, vm.Pages[0].EditItems.Count(i => i.Kind == ContentEditKind.Image));
        var first = vm.Pages[0].EditItems.First(i => i.Kind == ContentEditKind.Image);
        Assert.True(await vm.ReplaceImageAsync(first, jpg));
    }

    [Fact]
    public async Task TheEditLayer_OutlinesItems_ShowsHandles_AndEditsInPlace()
    {
        var vm = await Open(Document());
        var page = vm.Pages[0];
        var image = page.EditItems.Single(i => i.Kind == ContentEditKind.Image);
        var text = page.EditItems.Single(i => i.Kind == ContentEditKind.Text);
        vm.SelectedEditItem = image;
        byte[]? pageImage = null;
        int pw = 0, ph = 0, stride = 0;
        using (var engine = new PdfiumEngine())
        await using (var doc = await engine.OpenDocumentAsync(vm.CurrentDocumentBytes()!, null))
        {
            var rendered = await engine.Renderer.RenderPageAsync(doc, new PdfEngine.Rendering.RenderRequest { PageNumber = 1, Dpi = 72 * page.DisplayScale });
            pageImage = rendered.Pixels.ToArray();
            (pw, ph, stride) = (rendered.WidthPixels, rendered.HeightPixels, rendered.Stride);
            rendered.Dispose();
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var layer = new PdfViewer.Views.ContentEditLayer { Page = page, Width = page.UnrotatedDisplayWidth, Height = page.UnrotatedDisplayHeight };
                Assert.True(layer.IsHitTestVisible);
                var shapes = layer.Children.OfType<System.Windows.Shapes.Rectangle>().ToList();
                Assert.Equal(2 + 4, shapes.Count); // two outlines, four corner handles on the selected image
                Assert.Equal(4, shapes.Count(r => System.Windows.Automation.AutomationProperties.GetName(r) == "Resize handle"));

                layer.BeginEdit(text);
                var editor = Assert.Single(layer.Children.OfType<System.Windows.Controls.TextBox>());
                Assert.Equal(text.Text, editor.Text);
                Assert.Equal(14 * page.DisplayScale, editor.FontSize, 3);

                if (Environment.GetEnvironmentVariable("VIEWER_SNAPSHOT_DIR") is { Length: > 0 } dir)
                {
                    int w = (int)Math.Ceiling(page.UnrotatedDisplayWidth), h = (int)Math.Ceiling(page.UnrotatedDisplayHeight);
                    var grid = new System.Windows.Controls.Grid { Width = w, Height = h, Background = Brushes.White };
                    var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, pageImage!, stride);
                    grid.Children.Add(new System.Windows.Controls.Image { Source = bmp, Stretch = Stretch.Fill });
                    grid.Children.Add(layer);
                    grid.Measure(new Size(w, h));
                    grid.Arrange(new Rect(0, 0, w, h));
                    grid.UpdateLayout();
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(grid);
                    Save(new PngBitmapEncoder(), rtb, Path.Combine(dir, "content-edit-layer.png"));
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (error != null) throw error;
    }

    private static void Save(BitmapEncoder encoder, BitmapSource source, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}

internal static class RectRounding
{
    public static Rect Round(this Rect r) => new(Math.Round(r.X, 3), Math.Round(r.Y, 3), Math.Round(r.Width, 3), Math.Round(r.Height, 3));
}
