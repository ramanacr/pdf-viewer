using System;
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
    public void Prefetch_WithoutThreads_QueuesNothingAndTextIsBuiltOnDemand()
    {
        using var doc = PdfVectorDocument.OpenAsync(Pages(3, p => $"BT /F1 12 Tf 20 250 Td (Browser page {p}) Tj ET")).AsTask().GetAwaiter().GetResult();
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
}
