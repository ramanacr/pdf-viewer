using System.Runtime.CompilerServices;
using PdfEngine.Vector.Color;
using PdfEngine.Vector.Windows;

namespace PdfEngine.Vector.Tests;

/// <summary>
/// The engine core reads no colour profile itself. These tests run on Windows, against the results the
/// desktop app gets, so before any test runs they register the Windows provider as the app does.
/// </summary>
internal static class WindowsColorProfiles
{
#pragma warning disable CA2255 // a test assembly: this must run before the first test converts a colour
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => ColorProfiles.Register(WindowsColorProfileProvider.Instance);
}
