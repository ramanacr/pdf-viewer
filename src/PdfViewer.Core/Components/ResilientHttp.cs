// Explicit usings: this file is also compiled into the installer, which has a different
// implicit-using set (as ComponentDownloader.cs is).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PdfViewer.Core.Components;

/// <summary>
/// HTTP connections that do not stall on one unreachable server address.
///
/// GitHub serves release downloads from several addresses. .NET tries a host's addresses one
/// after another, and Windows waits about 21 seconds before it gives up on one that does not
/// answer. On a network where one of GitHub's addresses was unreachable, the self-update's
/// download timed out before .NET reached a working address. Here the addresses are tried in a
/// staggered race (as browsers and curl do): the next attempt starts after a short delay, each
/// attempt has its own limit, and the first connection that opens wins.
/// </summary>
public static class ResilientHttp
{
    /// <summary>The longest one address may take to accept a connection.</summary>
    internal static TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long an attempt runs alone before the next address is tried too.</summary>
    internal static TimeSpan Stagger { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>A handler for HttpClient that connects through <see cref="ConnectAnyAsync"/>.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            return await ConnectAnyAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
        },
    };

    /// <summary>
    /// Connects to the first of <paramref name="addresses"/> that answers. Throws the last
    /// failure when none does.
    /// </summary>
    internal static async Task<Stream> ConnectAnyAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
    {
        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new List<Task<Socket>>();
        Exception? lastError = null;
        int next = 0;
        try
        {
            while (true)
            {
                if (next < addresses.Count)
                    attempts.Add(AttemptAsync(addresses[next++], port, race.Token));

                // Wait for an attempt to finish, or for the stagger to start the next one.
                var pending = attempts.Where(t => !t.IsCompleted).ToList();
                var finished = attempts.FirstOrDefault(t => t.IsCompletedSuccessfully);
                if (finished == null && pending.Count > 0)
                {
                    Task wait = next < addresses.Count
                        ? Task.WhenAny(Task.WhenAny(pending), Task.Delay(Stagger, ct))
                        : Task.WhenAny(pending);
                    await wait.ConfigureAwait(false);
                    finished = attempts.FirstOrDefault(t => t.IsCompletedSuccessfully);
                }

                if (finished != null)
                {
                    var socket = finished.Result;
                    race.Cancel();
                    foreach (var other in attempts.Where(t => t != finished))
                        _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                    return new NetworkStream(socket, ownsSocket: true);
                }

                foreach (var failed in attempts.Where(t => t.IsFaulted))
                    lastError = failed.Exception?.InnerException ?? lastError;
                ct.ThrowIfCancellationRequested();
                if (next >= addresses.Count && attempts.All(t => t.IsCompleted))
                    throw lastError ?? new SocketException((int)SocketError.TimedOut);
            }
        }
        catch
        {
            race.Cancel();
            throw;
        }
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(AttemptTimeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), limit.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new SocketException((int)SocketError.TimedOut);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
