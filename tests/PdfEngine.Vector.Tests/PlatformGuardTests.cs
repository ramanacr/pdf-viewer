using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Editing;
using PdfEngine.Vector.Tests.Fixtures;
using PdfEngine.Vector.Text;
using Xunit;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The guards that let the engine run in browser WebAssembly (src/PdfViewer.Web): no thread of its
/// own and no installed-font lookups there, the same behaviour as before everywhere else. The
/// browser is simulated with <see cref="PdfPlatform.Simulate"/>, which affects the calling test only.
/// </summary>
public class PlatformGuardTests
{
    private static byte[] Pages(int count, Func<int, string> content)
    {
        var b = new VectorPdfBuilder();
        for (int p = 1; p <= count; p++)
            b.AddPage(content(p), "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>", mediaBox: "[0 0 300 300]");
        return b.Build();
    }

    [Fact]
    public void Desktop_KeepsThreadsAndInstalledFonts()
    {
        Assert.True(PdfPlatform.CanStartThreads);
        Assert.True(PdfPlatform.HasInstalledFonts);
        Assert.NotEmpty(SystemFontCatalog.Installed.Faces);
    }

    [Fact]
    public void Simulation_IsUndoneAndStaysInItsFlow()
    {
        bool seenElsewhere = true;
        using (PdfPlatform.Simulate(canStartThreads: false, hasInstalledFonts: false))
        {
            Assert.False(PdfPlatform.CanStartThreads);
            Assert.False(PdfPlatform.HasInstalledFonts);
            // A thread started outside this flow sees the real platform.
            var other = new Thread(() => seenElsewhere = PdfPlatform.CanStartThreads);
            ExecutionContext.SuppressFlow();
            try { other.Start(); }
            finally { ExecutionContext.RestoreFlow(); }
            other.Join();
        }
        Assert.True(seenElsewhere);
        Assert.True(PdfPlatform.CanStartThreads);
        Assert.True(PdfPlatform.HasInstalledFonts);
    }

    [Fact]
    public async Task Prefetch_WithoutThreads_QueuesNothingAndTextIsBuiltOnDemand()
    {
        using var doc = await PdfVectorDocument.OpenAsync(Pages(3, p => $"BT /F1 12 Tf 20 250 Td (Browser page {p}) Tj ET"));
        using (PdfPlatform.Simulate(canStartThreads: false, hasInstalledFonts: false))
        {
            // On a single-threaded platform the worker would throw when started: nothing is queued.
            PdfTextExtractor.Prefetch(doc, 2);
            Thread.Sleep(200);
            Assert.False(PdfTextExtractor.IsLoaded(doc, 2));
            Assert.Equal("Browser page 2", PdfTextExtractor.LoadPage(doc, 2).GetAllText());
            Assert.True(PdfTextExtractor.IsLoaded(doc, 2));
        }
    }

    [Fact]
    public void InstalledFonts_InABrowser_AreAnEmptyCatalog()
    {
        using (PdfPlatform.Simulate(canStartThreads: false, hasInstalledFonts: false))
        {
            var catalog = SystemFontCatalog.Installed;
            Assert.Empty(catalog.Faces);
            Assert.Null(catalog.Match("Helvetica", bold: false, italic: false, serif: false, fixedPitch: false));
            Assert.Null(catalog.FindFamily("Arial", bold: false, italic: false));
        }
        // The machine's catalog is untouched by the simulation.
        Assert.NotEmpty(SystemFontCatalog.Installed.Faces);
    }

    [Fact]
    public void Android_ReadsSystemFontsWithAndroidAliases_AndTheDesktopIsUnchanged()
    {
        Assert.False(PdfPlatform.IsAndroid);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), PdfPlatform.InstalledFontDirectory);
        using (PdfPlatform.Simulate(canStartThreads: true, hasInstalledFonts: true, android: true))
        {
            Assert.True(PdfPlatform.IsAndroid);
            Assert.True(PdfPlatform.CanStartThreads); // Android has threads, unlike a browser
            Assert.Equal("/system/fonts", PdfPlatform.InstalledFontDirectory);
            Assert.Same(SystemFontCatalog.ForAndroid(), SystemFontCatalog.Installed);
        }
        Assert.False(PdfPlatform.IsAndroid);
        // The Windows catalog has no aliases: a missing family stays missing.
        Assert.Null(SystemFontCatalog.Installed.FindAliasedFamily("Arial", 400, false));
    }

    /// <summary>A folder holding copies of a few Windows fonts (never committed), standing in for an Android font folder.</summary>
    private static string FontFolder(params string[] files)
    {
        string dir = Directory.CreateTempSubdirectory("android-fonts").FullName;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var f in files)
            File.Copy(Path.Combine(windows, f), Path.Combine(dir, f));
        return dir;
    }

    [Fact]
    public void FamilyAliases_StandInForMissingFamilies_InMatchFallbacksAndFindFamily()
    {
        string dir = FontFolder("georgia.ttf", "georgiab.ttf", "consola.ttf");
        try
        {
            var plain = SystemFontCatalog.ForDirectory(dir);
            Assert.Null(plain.Match("Helvetica", bold: false, italic: false, serif: false, fixedPitch: false));

            // As on Android, where Roboto stands in for Arial: here Georgia does, and Consolas for Courier New.
            var aliased = plain.WithFamilyAliases(new Dictionary<string, string[]>
            {
                ["Arial"] = new[] { "Not Installed", "Georgia" },
                ["Courier New"] = new[] { "Consolas" },
            });
            Assert.Same(plain.Faces, aliased.Faces);
            Assert.Equal("Georgia", aliased.Match("Helvetica", bold: false, italic: false, serif: false, fixedPitch: false)?.Family);
            var bold = aliased.Match("Helvetica-Bold", bold: false, italic: false, serif: false, fixedPitch: false);
            Assert.True(bold is { Family: "Georgia", Bold: true });
            Assert.Equal("Consolas", aliased.Match("Courier", bold: false, italic: false, serif: false, fixedPitch: true)?.Family);
            Assert.Equal("Georgia", aliased.FindFamily("Arial", bold: false, italic: false)?.Family);
            Assert.Equal("Georgia", aliased.Fallbacks(false, false).First().Family);
            // A family that is there is found as itself; an alias is only for missing ones.
            Assert.Equal("Georgia", aliased.FindFamily("Georgia", bold: false, italic: false)?.Family);
            Assert.Null(aliased.FindAliasedFamily("Georgia", 400, false));
            Assert.Null(aliased.FindFamily("Times New Roman", bold: false, italic: false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AndroidAliases_CoverEveryFamilySubstitutionAsksFor()
    {
        // The generic families of SystemFontCatalog.Match and Fallbacks, the Windows designs it maps
        // PostScript names to, and the Skia backend's class and Unicode fallback families.
        string[] asked =
        {
            "Arial", "Times New Roman", "Courier New", "Segoe UI", "Monotype Corsiva", "Segoe Script", "Segoe UI Symbol", "Nirmala UI",
            "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Microsoft JhengHei", "Ebrima", "Leelawadee UI", "Segoe UI Historic", "Cambria Math",
            "Arial Narrow", "Palatino Linotype", "Century Gothic", "Bookman Old Style", "Century Schoolbook", "Gill Sans MT", "Garamond", "Cambria",
            "Lucida Sans Unicode", "Tahoma", "Verdana", "Georgia", "Book Antiqua",
        };
        var aliases = SystemFontCatalog.AndroidFamilyAliases;
        Assert.All(asked, family => Assert.True(aliases.TryGetValue(family, out var standIns) && standIns.Length > 0, family));
        Assert.Equal("Roboto", aliases["Arial"][0]);
        Assert.Equal("Noto Serif", aliases["Times New Roman"][0]);
        Assert.Equal("Droid Sans Mono", aliases["Courier New"][0]);
        // Where there is no such folder, the Android catalog is empty, not an error.
        Assert.Empty(SystemFontCatalog.ForAndroid(Path.Combine(Path.GetTempPath(), "no-such-android-fonts")).Faces);
    }
}
