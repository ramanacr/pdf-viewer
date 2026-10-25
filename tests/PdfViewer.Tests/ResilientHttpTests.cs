using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PdfViewer.Core.Components;
using Xunit;

namespace PdfViewer.Tests;

/// <summary>
/// The self-update stalled when one of GitHub's addresses did not answer: .NET tried the
/// addresses one after another and Windows waited about 21 seconds on the dead one, past the
/// download's limit. ResilientHttp races the addresses instead.
/// </summary>
public class ResilientHttpTests
{
    // TEST-NET-1 (RFC 5737): never routed, so a connection to it fails or hangs.
    private static readonly IPAddress Unreachable = IPAddress.Parse("192.0.2.1");

    [Fact]
    public async Task AnUnreachableFirstAddress_DoesNotStallTheConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var watch = Stopwatch.StartNew();
        await using var stream = await ResilientHttp.ConnectAnyAsync(new[] { Unreachable, IPAddress.Loopback }, port, CancellationToken.None);
        watch.Stop();

        Assert.True(stream.CanWrite);
        // The second address starts after the stagger (300 ms), long before one attempt's limit.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Connecting took {watch.Elapsed}.");
    }

    [Fact]
    public async Task WhenNoAddressAnswers_TheLastFailureIsThrown()
    {
        // A port nothing listens on refuses the connection at once.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Assert.ThrowsAsync<SocketException>(() => ResilientHttp.ConnectAnyAsync(new[] { IPAddress.Loopback }, port, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_StopsTheAttempts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResilientHttp.ConnectAnyAsync(new[] { Unreachable }, 443, cts.Token));
    }
}
