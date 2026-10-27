using PdfEngine.Vector.Color;
using Xunit;

namespace PdfEngine.Vector.Skia.Tests;

/// <summary>
/// The Skia renderer is the one the web and Android apps use, and they register no colour profile
/// provider. So these tests, unlike PdfEngine.Vector.Tests, do not register the Windows provider: they
/// run with the built-in SWOP model even on Windows, as those apps do. The Direct2D renderer they
/// compare with runs in the same process, so both see the same colours.
/// </summary>
public class ColorProfileTests
{
    [Fact]
    public void TheSkiaTests_UseTheBuiltInProvider_AsTheWebAndAndroidAppsDo()
    {
        Assert.Same(BuiltInColorProfileProvider.Instance, ColorProfiles.Provider);
    }
}
