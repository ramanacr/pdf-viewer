using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdfEngine.Geometry;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.PdfA;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Windows;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The engine core reads no colour profile from the machine (issue #80): a provider gives it the CMYK
/// profiles to use. The default has none, so the core behaves the same on every platform; Windows
/// hosts register <see cref="WindowsColorProfileProvider"/>, which reads what the core read before.
/// </summary>
public class ColorProfileProviderTests
{
    /// <summary>A provider that gives fixed profiles, as a host on any platform could.</summary>
    private sealed class FixedProfiles(byte[]? display, byte[]? outputIntent) : IColorProfileProvider
    {
        public byte[]? GetDisplayCmykProfile() => display;
        public byte[]? GetOutputIntentCmykProfile() => outputIntent;
    }

    private static byte[] ToyCmykProfile()
    {
        var corners = new double[16][];
        for (int i = 0; i < 16; i++)
        {
            int c = i >> 3 & 1, m = i >> 2 & 1, y = i >> 1 & 1, k = i & 1;
            corners[i] = [Math.Max(95 - 30 * c - 35 * m - 8 * y - 40 * k, 5), -35 * c + 60 * m - 5 * y, -45 * c - 5 * m + 80 * y];
        }
        return IccProfileBuilder.TwoPointCmyk(corners);
    }

    private static byte[] CmykDocument()
    {
        var b = new VectorPdfBuilder();
        b.AddPage("1 0 0 0 k 40 40 100 100 re f", mediaBox: "[0 0 200 200]");
        return b.Build();
    }

    [Fact]
    public void BuiltInProvider_HasNoProfiles()
    {
        Assert.Null(BuiltInColorProfileProvider.Instance.GetDisplayCmykProfile());
        Assert.Null(BuiltInColorProfileProvider.Instance.GetOutputIntentCmykProfile());
    }

    [Fact]
    public async Task WithNoSystemProfile_DeviceCmykUsesTheBuiltInSwopModel()
    {
        using var simulated = ColorProfiles.Simulate(BuiltInColorProfileProvider.Instance);
        Assert.False(DefaultCmyk.UsesInstalledProfile);

        var rgb = new double[3];
        foreach (var cmyk in new double[][] { [1, 0, 0, 0], [0, 0.5, 0.2, 0.1], [0, 0, 0, 1] })
        {
            SwopApproximation.Instance.ToSrgb(cmyk, rgb);
            var c = PdfColorSpace.DeviceCmyk.ToRgbColor(cmyk);
            Assert.Equal(rgb[0], c.R, 4);
            Assert.Equal(rgb[1], c.G, 4);
            Assert.Equal(rgb[2], c.B, 4);
        }

        // A page with CMYK colour builds, and its fill has the built-in model's colour.
        using var doc = await PdfVectorDocument.OpenAsync(CmykDocument());
        var list = await doc.GetPageDisplayListAsync(1);
        var fill = Assert.Single(list.Commands.OfType<FillPath>());
        SwopApproximation.Instance.ToSrgb([1, 0, 0, 0], rgb);
        Assert.Equal(rgb[0], fill.Paint.Color.R, 4);
        Assert.Equal(rgb[2], fill.Paint.Color.B, 4);
    }

    [Fact]
    public async Task WithNoSystemProfile_PdfAConversionStillRuns_AndReportsTheUnprofiledCmyk()
    {
        using var simulated = ColorProfiles.Simulate(BuiltInColorProfileProvider.Instance);
        byte[] pdf = CmykDocument();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);

        var result = PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavour = PdfAFlavour.A2b });

        Assert.False(result.Report.IsCompliant);
        Assert.DoesNotContain(result.Changes, c => c.Contains("Gave CMYK colour an ICC profile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegisteredProvider_GivesTheDisplayAndOutputIntentProfiles()
    {
        byte[] profile = ToyCmykProfile();
        using var simulated = ColorProfiles.Simulate(new FixedProfiles(profile, profile));

        Assert.True(DefaultCmyk.UsesInstalledProfile);
        var expected = new double[3];
        IccTransform.Create(IccProfile.TryParse(profile, out _)!, PdfColorSpace.DefaultCmykIntent).ToSrgb([1, 0, 0, 0], expected);
        var c = PdfColorSpace.DeviceCmyk.ToRgbColor([1, 0, 0, 0]);
        Assert.Equal(expected[0], c.R, 4);
        Assert.Equal(expected[2], c.B, 4);

        byte[] pdf = CmykDocument();
        using var doc = await PdfVectorDocument.OpenAsync(pdf);
        var result = PdfAConverter.Convert(doc, pdf, new PdfAConversionOptions { Flavour = PdfAFlavour.A2b });
        Assert.True(result.Report.IsCompliant, string.Join("\n", result.Report.Issues));
    }

    /// <summary>A host provider that fails.</summary>
    private sealed class FailingProfiles : IColorProfileProvider
    {
        public int Calls;
        public byte[]? GetDisplayCmykProfile() { Calls++; throw new IOException("profile store unavailable"); }
        public byte[]? GetOutputIntentCmykProfile() => null;
    }

    [Fact]
    public void AFailingProvider_FallsBackToTheBuiltInModel_Once()
    {
        var failing = new FailingProfiles();
        using var simulated = ColorProfiles.Simulate(failing);

        var rgb = new double[3];
        SwopApproximation.Instance.ToSrgb([1, 0, 0, 0], rgb);
        var c = PdfColorSpace.DeviceCmyk.ToRgbColor([1, 0, 0, 0]);
        Assert.Equal(rgb[0], c.R, 4);
        Assert.False(DefaultCmyk.UsesInstalledProfile);
        PdfColorSpace.DeviceCmyk.ToRgbColor([0, 1, 0, 0]);
        Assert.Equal(1, failing.Calls); // the fallback is cached for that provider
    }

    [Fact]
    public void Separations_InvertTheConversionOfTheProviderInUse()
    {
        // Built-in model and a toy profile give different CMYK for the same sRGB colour: each has its own table.
        var builtIn = new double[4];
        var toy = new double[4];
        using (ColorProfiles.Simulate(BuiltInColorProfileProvider.Instance))
            CmykSeparation.FromSrgb(0.2, 0.5, 0.8, builtIn);
        byte[] profile = ToyCmykProfile();
        using (ColorProfiles.Simulate(new FixedProfiles(profile, null)))
            CmykSeparation.FromSrgb(0.2, 0.5, 0.8, toy);
        Assert.True(Enumerable.Range(0, 4).Any(i => Math.Abs(builtIn[i] - toy[i]) > 0.01),
            $"built-in {string.Join(",", builtIn)} toy {string.Join(",", toy)}");
    }

    [Fact]
    public void TheWindowsProvider_ReadsTheProfilesTheCoreReadBefore()
    {
        // RSWOP.icm for PDF/A output intents; Adobe's U.S. Web Coated (SWOP) v2 for display, when installed.
        string rswop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "RSWOP.icm");
        byte[]? output = WindowsColorProfileProvider.Instance.GetOutputIntentCmykProfile();
        if (File.Exists(rswop))
            Assert.Equal(File.ReadAllBytes(rswop), output);
        else
            Assert.Null(output);

        bool adobe = new[] { Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Any(root => root.Length > 0 && File.Exists(Path.Combine(root, "Adobe", "Color", "Profiles", "Recommended", "USWebCoatedSWOP.icc")));
        Assert.Equal(adobe, WindowsColorProfileProvider.Instance.GetDisplayCmykProfile() != null);
    }

    [Fact]
    public void TheTestRun_UsesTheWindowsProvider_AsTheWindowsAppDoes()
    {
        Assert.Same(WindowsColorProfileProvider.Instance, ColorProfiles.Provider);
    }
}
