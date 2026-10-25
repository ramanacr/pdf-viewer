using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using PdfViewer.Services;
using PdfViewer.Views.Dialogs;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The Microsoft Store build (docs/microsoft-store.md): what the application does differently
/// when it runs from an MSIX package, and the manifest template scripts/build_msix.ps1 fills.
/// </summary>
public class PackagedAppTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "PdfViewerPackagedTests_" + Guid.NewGuid().ToString("N"));

    public PackagedAppTests() => Directory.CreateDirectory(_testDir);

    public void Dispose()
    {
        PackagedApp.SetIsPackagedForTests(null);
        try { Directory.Delete(_testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfViewer.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public void TheTestHostIsNotPackaged()
    {
        Assert.False(PackagedApp.IsPackaged);
    }

    [Fact]
    public void APackagedInstallWritesNoFileAssociation()
    {
        string classes = ShellIntegrationService.ClassesRoot;
        Assert.NotEqual(@"Software\Classes", classes);
        string fakeExe = Path.Combine(_testDir, "PdfViewer.exe");
        File.WriteAllText(fakeExe, "fake binary");

        PackagedApp.SetIsPackagedForTests(true);
        ShellIntegrationService.RegisterShellAssociation(fakeExe);

        using (var progKey = Registry.CurrentUser.OpenSubKey($@"{classes}\PdfViewer.Document"))
        {
            Assert.Null(progKey);
        }
        Assert.True(ShellIntegrationService.UnregisterShellAssociation());
    }

    [Fact]
    public void APackagedInstallDoesNotOfferTheUpdateCheckOrADownload()
    {
        PackagedApp.SetIsPackagedForTests(true);
        var packaged = PrivacyDialog.BuildGuarantees();
        Assert.Contains(packaged, g => g.Claim == "Nothing is downloaded.");
        Assert.DoesNotContain(packaged, g => g.Evidence.Contains("GitHub releases", StringComparison.Ordinal));

        PackagedApp.SetIsPackagedForTests(false);
        var installed = PrivacyDialog.BuildGuarantees();
        Assert.Contains(installed, g => g.Claim == "Optional parts are downloaded only if you ask.");
    }

    [Fact]
    public void TheManifestTemplateDeclaresADesktopAppWithThePdfAssociation()
    {
        string root = RepoRoot();
        var manifest = XDocument.Load(Path.Combine(root, "eng", "msix", "AppxManifest.xml"));
        XNamespace f = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        XNamespace uap3 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        // The identity is filled from Partner Center, never written into the template.
        var identity = manifest.Root!.Element(f + "Identity")!;
        Assert.Equal("{IdentityName}", identity.Attribute("Name")!.Value);
        Assert.Equal("{Publisher}", identity.Attribute("Publisher")!.Value);
        Assert.Equal("{PublisherDisplayName}", manifest.Root.Element(f + "Properties")!.Element(f + "PublisherDisplayName")!.Value);

        var app = manifest.Descendants(f + "Application").Single();
        Assert.Equal("PdfViewer.exe", app.Attribute("Executable")!.Value);
        Assert.Equal("Windows.FullTrustApplication", app.Attribute("EntryPoint")!.Value);
        Assert.Contains(manifest.Descendants(rescap + "Capability"), c => c.Attribute("Name")!.Value == "runFullTrust");

        // The same Windows floor as the application.
        string csproj = File.ReadAllText(Path.Combine(root, "src", "PdfViewer", "PdfViewer.csproj"));
        string floor = XDocument.Parse(csproj).Descendants("SupportedOSPlatformVersion").Single().Value;
        var family = manifest.Descendants(f + "TargetDeviceFamily").Single();
        Assert.Equal("Windows.Desktop", family.Attribute("Name")!.Value);
        Assert.Equal(floor, family.Attribute("MinVersion")!.Value);

        var association = manifest.Descendants(uap3 + "FileTypeAssociation").Single();
        Assert.Equal(".pdf", association.Descendants(uap + "FileType").Single().Value);
        Assert.Equal(@"Assets\PdfFile.png", association.Element(uap + "Logo")!.Value);
    }

    [Fact]
    public void TheCommittedIdentityIsEmptyOrAPartnerCenterValue()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "eng", "msix", "identity.json")));
        string publisher = json.RootElement.GetProperty("publisher").GetString() ?? string.Empty;
        string name = json.RootElement.GetProperty("identityName").GetString() ?? string.Empty;

        // Empty until the owner registers; then the exact Partner Center values.
        Assert.True(publisher.Length == 0 || publisher.StartsWith("CN=", StringComparison.Ordinal), publisher);
        Assert.True(name.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9.\-]{3,50}$"), name);
    }
}
