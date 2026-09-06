using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfViewer.Core.Components;
using PdfViewer.Services;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// Covers the optional-component machinery. The application downloads a file and then loads it
/// as code, which is exactly the pattern this product warns users about, so the checks around
/// it are the part that has to be right.
/// </summary>
public class OptionalComponentTests : IDisposable
{
    private readonly string _testDir;

    public OptionalComponentTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "PdfComponentTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
        catch { }
    }

    /// <summary>Serves fixed bytes so a download can be tested without a network.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly HttpStatusCode _status;

        public StubHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUri = request.RequestUri;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }

    private static OptionalComponent ComponentFor(byte[] payload) => new()
    {
        DisplayName = "Test Component",
        FileName = "Test.Component.dll",
        Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
        SizeBytes = payload.LongLength
    };

    [Fact]
    public async Task TestAComponentThatMatchesItsChecksumIsInstalled()
    {
        byte[] payload = Encoding.UTF8.GetBytes("the genuine component bytes");
        var component = ComponentFor(payload);

        using var http = new HttpClient(new StubHandler(payload));
        var downloader = new ComponentDownloader(http);

        string path = await downloader.InstallAsync(component, "9.9.9", _testDir);

        Assert.True(File.Exists(path));
        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.True(component.IsInstalled(_testDir));
    }

    /// <summary>
    /// The whole point of pinning. A file that is not the expected one must never reach the
    /// install directory, because the application would load it as code.
    /// </summary>
    [Fact]
    public async Task TestATamperedDownloadIsDiscardedAndNothingIsWritten()
    {
        byte[] expected = Encoding.UTF8.GetBytes("the genuine component bytes");
        byte[] served = Encoding.UTF8.GetBytes("something else entirely");

        var component = ComponentFor(expected);

        using var http = new HttpClient(new StubHandler(served));
        var downloader = new ComponentDownloader(http);

        var ex = await Assert.ThrowsAsync<ComponentInstallException>(
            async () => await downloader.InstallAsync(component, "9.9.9", _testDir));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(_testDir, component.FileName)));
        Assert.False(component.IsInstalled(_testDir));

        // And no staging leftovers that a later run might mistake for the real thing.
        Assert.Empty(Directory.GetFiles(_testDir));
    }

    [Fact]
    public async Task TestAFailedDownloadInstallsNothing()
    {
        byte[] payload = Encoding.UTF8.GetBytes("never served");
        var component = ComponentFor(payload);

        using var http = new HttpClient(new StubHandler(Array.Empty<byte>(), HttpStatusCode.NotFound));
        var downloader = new ComponentDownloader(http);

        await Assert.ThrowsAsync<ComponentInstallException>(
            async () => await downloader.InstallAsync(component, "9.9.9", _testDir));

        Assert.Empty(Directory.GetFiles(_testDir));
    }

    [Fact]
    public async Task TestAnAlreadyInstalledComponentIsNotDownloadedAgain()
    {
        byte[] payload = Encoding.UTF8.GetBytes("already here");
        var component = ComponentFor(payload);

        File.WriteAllBytes(Path.Combine(_testDir, component.FileName), payload);

        var handler = new StubHandler(payload);
        using var http = new HttpClient(handler);

        await new ComponentDownloader(http).InstallAsync(component, "9.9.9", _testDir);

        Assert.Equal(0, handler.Requests);
    }

    /// <summary>
    /// A file that is present but corrupt must count as absent, or the application would try
    /// to load it and fail in a far more confusing way.
    /// </summary>
    [Fact]
    public void TestACorruptComponentCountsAsNotInstalled()
    {
        byte[] payload = Encoding.UTF8.GetBytes("the genuine component bytes");
        var component = ComponentFor(payload);

        string path = Path.Combine(_testDir, component.FileName);

        File.WriteAllBytes(path, payload);
        Assert.True(component.IsInstalled(_testDir));

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("truncated"));
        Assert.False(component.IsInstalled(_testDir));

        File.Delete(path);
        Assert.False(component.IsInstalled(_testDir));
    }

    [Fact]
    public async Task TestTheDownloadComesFromTheProjectsOwnReleaseAssetsOverHttps()
    {
        byte[] payload = Encoding.UTF8.GetBytes("bytes");
        var component = ComponentFor(payload);

        var handler = new StubHandler(payload);
        using var http = new HttpClient(handler);

        await new ComponentDownloader(http).InstallAsync(component, "3.4.0", _testDir);

        Assert.NotNull(handler.LastUri);
        Assert.Equal("https", handler.LastUri!.Scheme);
        Assert.Equal("github.com", handler.LastUri.Host);
        Assert.Contains("/releases/download/v3.4.0/", handler.LastUri.AbsolutePath);
        Assert.EndsWith(component.FileName, handler.LastUri.AbsolutePath);
    }

    /// <summary>
    /// The version in the URL has to be the running application's, so a copy of the component
    /// built for a different release is never picked up.
    /// </summary>
    [Fact]
    public void TestTheDownloadUrlIsVersioned()
    {
        var component = OptionalComponents.ReadAloud;

        Assert.Contains("/v3.3.0/", component.DownloadUrl("3.3.0"));
        Assert.Contains("/v4.0.0/", component.DownloadUrl("4.0.0"));
        Assert.StartsWith("https://", component.DownloadUrl("1.0.0"));
    }

    /// <summary>
    /// The application must run, and simply lack the feature, when the component is absent.
    /// Resolving a missing assembly at the wrong moment would turn that into a crash.
    /// </summary>
    [Fact]
    public void TestReadAloudReportsItselfUnavailableWithoutTheComponent()
    {
        Assert.False(TextReaderFactory.IsComponentInstalled(_testDir));
        Assert.Null(TextReaderFactory.TryCreate(_testDir));

        // A wrong file under the right name is still "not installed".
        File.WriteAllBytes(
            Path.Combine(_testDir, OptionalComponents.ReadAloud.FileName),
            Encoding.UTF8.GetBytes("not the real assembly"));

        Assert.False(TextReaderFactory.IsComponentInstalled(_testDir));
        Assert.Null(TextReaderFactory.TryCreate(_testDir));
    }

    [Fact]
    public void TestTheShippedComponentDefinitionIsSelfConsistent()
    {
        var component = OptionalComponents.ReadAloud;

        Assert.Equal(64, component.Sha256.Length);
        Assert.True(component.Sha256.All(Uri.IsHexDigit));
        Assert.True(component.SizeBytes > 0);
        Assert.EndsWith(".dll", component.FileName);
        Assert.Contains(component, OptionalComponents.All);
    }
}
