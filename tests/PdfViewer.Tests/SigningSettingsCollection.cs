using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Tests that point <c>SigningSettings</c> at a folder of their own. The folder is process-wide, so
/// these run one after another: in parallel, one test would sign with another's settings, or lose
/// its folder when another test finished.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SigningSettingsCollection
{
    public const string Name = "Signing settings";
}
