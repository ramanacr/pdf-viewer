using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Signatures;

namespace PdfViewer.Services;

/// <summary>
/// RFC 3161 over HTTP (RFC 3161 section 3.4): the request is POSTed as application/timestamp-query
/// to the time-stamping authority the user chose, and the reply read as application/timestamp-reply.
/// Only a hash is sent (of the signature, or for a document timestamp of the document's bytes),
/// never the document itself. It is used only when the user asked for a timestamp.
/// </summary>
public sealed class HttpTimestampClient : IPdfTimestampClient, IDisposable
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _http;
    private readonly bool _owns;

    public HttpTimestampClient(Uri server, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https"))
            throw new ArgumentException("A timestamp server address starts with http:// or https://.", nameof(server));
        Server = server;
        if (handler != null)
        {
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
            _owns = true;
        }
        else _http = Shared;
    }

    public Uri Server { get; }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var response = await _http.PostAsync(Server, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The timestamp server {Server.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type != null && !type.Equals("application/timestamp-reply", StringComparison.OrdinalIgnoreCase)
                         && !type.Equals("application/timestamp-response", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException($"The timestamp server {Server.Host} did not return a timestamp (it sent {type}).");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_owns) _http.Dispose();
    }
}

/// <summary>The timestamp client as the signing delegate.</summary>
public static class TimestampClient
{
    public static PdfTimestampClient For(Uri server) => new HttpTimestampClient(server).AsDelegate();
}
