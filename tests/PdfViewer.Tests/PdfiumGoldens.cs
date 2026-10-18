using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>The test project's source folder (where the project file and the goldens are).</summary>
internal static class TestProject
{
    public static string Directory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.Tests.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("The test project folder was not found.");
        }
    }
}

/// <summary>
/// What PDFium, the oracle, gives for the synthetic documents of the differential tests, committed
/// in PdfiumGoldens\ so the PDFium-free build checks the vector engine against the oracle without
/// PDFium. In the developer build each differential test also passes PDFium's live result to
/// <see cref="Verify{T}"/>, which fails when it no longer matches the committed golden (a PDFium
/// upgrade, a changed fixture), so a golden cannot go stale silently. To regenerate them, run the
/// developer build's golden tests with PDFIUM_GOLDENS_UPDATE=1 (scripts/update_pdfium_goldens.ps1),
/// then review the diff.
/// </summary>
internal static class PdfiumGoldens
{
    /// <summary>The trait of every test that checks a golden against PDFium (the regeneration script runs these).</summary>
    public const string Trait = "PdfiumGolden";
    public const string UpdateVariable = "PDFIUM_GOLDENS_UPDATE";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };

    private static string PathOf(string name, string extension) => Path.Combine(TestProject.Directory, "PdfiumGoldens", name + extension);

    private static string Missing(string name) =>
        $"The PDFium golden '{name}' is missing. Regenerate it in the developer build: scripts/update_pdfium_goldens.ps1.";

    /// <summary>The committed golden <paramref name="name"/>.</summary>
    public static T Load<T>(string name)
    {
        string path = PathOf(name, ".json");
        Assert.True(File.Exists(path), Missing(name));
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"The PDFium golden '{name}' is empty.");
    }

    /// <summary>The committed reference image <paramref name="name"/> (BGRA).</summary>
    public static BitmapSource LoadImage(string name)
    {
        string path = PathOf(name, ".png");
        Assert.True(File.Exists(path), Missing(name));
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    /// <summary>True while the goldens are being regenerated (PDFIUM_GOLDENS_UPDATE=1, developer build).</summary>
    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    /// <summary>
    /// A document PDFium wrote from one of the tests' synthetic documents, committed so the
    /// PDFium-free build can read what PDFium writes. Its bytes are not compared on later runs
    /// (PDFium stamps the time it saved); the golden of what PDFium reads from it is.
    /// </summary>
    public static byte[] LoadFile(string fileName)
    {
        string path = Path.Combine(TestProject.Directory, "PdfiumGoldens", fileName);
        Assert.True(File.Exists(path), Missing(fileName));
        return File.ReadAllBytes(path);
    }

    /// <summary>Developer build, while regenerating: <paramref name="bytes"/> becomes the committed file <paramref name="fileName"/>.</summary>
    public static void SaveFile(string fileName, byte[] bytes)
    {
        Assert.True(Updating, "Committed files are written only while regenerating the goldens.");
        string path = Path.Combine(TestProject.Directory, "PdfiumGoldens", fileName);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Developer build: <paramref name="fromPdfium"/> (PDFium's live result) is the committed
    /// golden, or, with PDFIUM_GOLDENS_UPDATE=1, becomes it.
    /// </summary>
    public static void Verify<T>(string name, T fromPdfium)
    {
        string json = JsonSerializer.Serialize(fromPdfium, Options).ReplaceLineEndings("\n") + "\n";
        string path = PathOf(name, ".json");
        if (Updating)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return;
        }
        Assert.True(File.Exists(path), Missing(name));
        string committed = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(committed == json,
            $"PDFium no longer gives the committed golden '{name}'. If the change is expected, regenerate it (scripts/update_pdfium_goldens.ps1) and review the diff.\nPDFium now:\n{json}");
    }

    /// <summary>As <see cref="Verify{T}"/>, for a rendered page: compared pixel for pixel, stored as a PNG.</summary>
    public static void VerifyImage(string name, BitmapSource fromPdfium)
    {
        var bgra = new FormatConvertedBitmap(fromPdfium, PixelFormats.Bgra32, null, 0);
        string path = PathOf(name, ".png");
        if (Updating)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bgra));
            using var file = File.Create(path);
            encoder.Save(file);
            return;
        }
        var committed = LoadImage(name);
        Assert.True(committed.PixelWidth == bgra.PixelWidth && committed.PixelHeight == bgra.PixelHeight,
            $"PDFium's render of '{name}' changed size. Regenerate the golden (scripts/update_pdfium_goldens.ps1) if that is expected.");
        Assert.True(Pixels(committed).AsSpan().SequenceEqual(Pixels(bgra)),
            $"PDFium no longer draws the committed golden '{name}'. Regenerate it (scripts/update_pdfium_goldens.ps1) if that is expected.");
    }

    /// <summary>The BGRA bytes of <paramref name="bitmap"/>, top-down, 4 bytes a pixel.</summary>
    public static byte[] Pixels(BitmapSource bitmap)
    {
        var bgra = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
