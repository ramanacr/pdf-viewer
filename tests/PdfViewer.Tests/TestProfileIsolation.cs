using System;
using System.IO;
using System.Runtime.CompilerServices;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Keeps the test run out of the user's profile. Opening a document adds it to the recent files,
/// and the viewer keeps its privacy, signing, theme, stamp and scale settings under
/// %LOCALAPPDATA%\PdfViewerNative. Before any test runs, every one of those stores is pointed at a
/// folder of this run's own under the temp folder, which is deleted when the run ends. The .pdf
/// file association goes under an HKCU key of the run's own for the same reason: registering
/// and unregistering it under Software\Classes deleted the user's real association. Tests that
/// need a folder of their own still set one.
/// </summary>
internal static class TestProfileIsolation
{
    internal static readonly string Directory = Path.Combine(Path.GetTempPath(), "PdfViewerTests_" + Guid.NewGuid().ToString("N"));

    private static readonly string RegistryRoot = @"Software\PdfViewerTests\" + Guid.NewGuid().ToString("N");

#pragma warning disable CA2255 // a test assembly: this must run before the first test touches a store
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Isolate()
    {
        System.IO.Directory.CreateDirectory(Directory);
        RecentFilesService.SetSettingsDirectoryForTests(Directory);
        PrivacySettings.SetSettingsDirectoryForTests(Directory);
        SigningSettings.SetDirectoryForTests(Directory);
        AppearanceSettings.SetSettingsDirectoryForTests(Directory);
        ShellIntegrationService.SetClassesRootForTests(RegistryRoot + @"\Classes");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { System.IO.Directory.Delete(Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistryRoot, throwOnMissingSubKey: false); } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        };
    }
}

public class TestProfileIsolationTests
{
    private static readonly string Profile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfViewerNative");

    [Fact]
    public void NoSettingsStorePointsAtTheUsersProfile()
    {
        Assert.False(IsUnderProfile(RecentFilesService.CurrentSettingsDirectory));
        Assert.False(IsUnderProfile(PrivacySettings.CurrentSettingsDirectory));
        Assert.False(IsUnderProfile(SigningSettings.Directory));
        Assert.False(IsUnderProfile(AppearanceSettings.CurrentSettingsDirectory));
        Assert.False(ShellIntegrationService.ClassesRoot.Equals(@"Software\Classes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheViewerGivesTheEngineTheWindowsColourProfiles()
    {
        // The engine core reads no colour profile itself (issue #80); the viewer registers Windows' own.
        Assert.Same(PdfEngine.Vector.Windows.WindowsColorProfileProvider.Instance, PdfEngine.Vector.Color.ColorProfiles.Provider);
    }

    private static bool IsUnderProfile(string directory) =>
        Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Profile), StringComparison.OrdinalIgnoreCase);
}
