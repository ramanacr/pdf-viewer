using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// A test that needs the real Noto fonts: the folder that eng/webdemo/noto_fonts.py wrote, named by
/// NOTO_FONTS_DIR. CI (.github/workflows/ci.yml) builds that folder from the pinned archives and
/// sets NOTO_FONTS_REQUIRED=1, so there the test runs and a missing folder fails it. Locally,
/// without the folder, the test is skipped with the reason.
/// </summary>
internal sealed class NotoFontsFactAttribute : FactAttribute
{
    public static string? Directory => Environment.GetEnvironmentVariable("NOTO_FONTS_DIR");

    public NotoFontsFactAttribute()
    {
        bool present = File.Exists(Path.Combine(Directory ?? "", "index.json"));
        if (!present && Environment.GetEnvironmentVariable("NOTO_FONTS_REQUIRED") != "1")
            Skip = "The Noto fonts are not downloaded: they are not in the repository. Run eng/webdemo/noto_fonts.py build (see src/PdfViewer.Web/README.md) and set NOTO_FONTS_DIR to its output folder.";
    }
}
