using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using PdfEngine.Vector;
using PdfViewer.Services;
using PdfViewer.ViewModels;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The two builds of the viewer (M10). The developer and test build (PdfiumRenderer=true, the
/// default) has PDFium as its oracle; the published build (PdfiumRenderer=false, run in CI as its
/// own test job) has none of it: no PdfEngine.Pdfium, no pdfium.dll, no PDFium mode.
/// </summary>
public class PdfiumFreeConfigurationTests
{
    private static string TestProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.Tests.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The test project folder was not found.");
    }

    /// <summary>
    /// The test files the PDFium-free build leaves out (PdfiumOnlyTest in the project file) exist:
    /// a renamed or deleted file must leave the list too, or the list stops saying what is excluded.
    /// </summary>
    [Fact]
    public void ThePdfiumOnlyTestList_NamesFilesThatExist()
    {
        string dir = TestProjectDirectory();
        var project = XDocument.Load(Path.Combine(dir, "PdfViewer.Tests.csproj"));
        var listed = project.Descendants().Where(e => e.Name.LocalName == "PdfiumOnlyTest")
            .Select(e => e.Attribute("Include")!.Value).ToList();

        Assert.NotEmpty(listed);
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(listed, f => Assert.True(File.Exists(Path.Combine(dir, f)), $"{f} is listed as PDFium-only but does not exist."));
        Assert.DoesNotContain(listed, f => f.StartsWith("PdfiumFree", StringComparison.OrdinalIgnoreCase)); // the gate tests always run

        // Each says why it needs PDFium (the CI job summary shows it).
        Assert.All(project.Descendants().Where(e => e.Name.LocalName == "PdfiumOnlyTest"),
            e => Assert.False(string.IsNullOrWhiteSpace(e.Attribute("Reason")?.Value), $"{e.Attribute("Include")!.Value} is listed as PDFium-only without a Reason."));
    }

    /// <summary>Every committed PDFium golden is read by a test, so none is left behind unchecked.</summary>
    [Fact]
    public void EveryPdfiumGolden_IsUsedByATest()
    {
        string dir = TestProjectDirectory();
        string sources = string.Concat(Directory.EnumerateFiles(dir, "*.cs").Select(File.ReadAllText));
        foreach (var golden in Directory.EnumerateFiles(Path.Combine(dir, "PdfiumGoldens")))
        {
            string name = Path.GetFileName(golden);
            string stem = Path.GetFileNameWithoutExtension(golden);
            Assert.True(sources.Contains($"\"{name}\"") || sources.Contains($"\"{stem}\"") || sources.Contains($"\"{stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')}{{"),
                $"PdfiumGoldens\\{name} is not read by any test.");
        }
    }

#if PDFIUM_RENDERER
    [Fact]
    public void TheDeveloperBuild_HasThePdfiumOracle()
    {
        Assert.True(PdfDocumentServiceFactory.IsPdfiumRendererAvailable);
        Assert.Contains(typeof(MainViewModel).Assembly.GetReferencedAssemblies(), a => a.Name == "PdfEngine.Pdfium");
        Assert.Equal(PdfEngineMode.Pdfium, PdfDocumentServiceFactory.Normalize(PdfEngineMode.Pdfium));
    }
#else
    [Fact]
    public void ThePdfiumFreeBuild_HasNoPdfium()
    {
        Assert.False(PdfDocumentServiceFactory.IsPdfiumRendererAvailable);

        // Not referenced, not compiled in, not beside the application.
        var viewer = typeof(MainViewModel).Assembly;
        Assert.DoesNotContain(viewer.GetReferencedAssemblies(), a => a.Name!.Contains("Pdfium", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(viewer.GetTypes(), t => t.Name.StartsWith("Pdfium", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(AppContext.BaseDirectory, "*pdfium*", SearchOption.AllDirectories));
        Assert.Throws<FileNotFoundException>(() => Assembly.Load("PdfEngine.Pdfium"));
    }

    [Fact]
    public void PdfiumMode_IsAutoInThePdfiumFreeBuild()
    {
        Assert.Equal(PdfEngineMode.Auto, PdfDocumentServiceFactory.Normalize(PdfEngineMode.Pdfium));
        using var service = PdfDocumentServiceFactory.CreateService();
        Assert.IsType<HybridVectorDocumentService>(service);
        service.EngineMode = PdfEngineMode.Pdfium;
        Assert.Equal(PdfEngineMode.Auto, service.EngineMode);
        Assert.False(((HybridVectorDocumentService)service).IsPdfiumDocumentOpen);
    }
#endif
}
