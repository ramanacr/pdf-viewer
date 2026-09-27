using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Signatures;

namespace PdfViewer.Services;

/// <summary>
/// RFC 3161 over HTTP (RFC 3161 section 3.4): the request is POSTed as application/timestamp-query
/// and the reply read as application/timestamp-reply. Only the hash of the signature is sent,
/// never the document.
/// </summary>
public static class TimestampClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static PdfTimestampClient For(Uri server)
    {
        if (server.Scheme is not ("http" or "https"))
            throw new ArgumentException("A timestamp server address starts with http:// or https://.", nameof(server));
        return async (request, ct) =>
        {
            using var content = new ByteArrayContent(request);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            using var response = await Http.PostAsync(server, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The timestamp server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (type != null && !type.Equals("application/timestamp-reply", StringComparison.OrdinalIgnoreCase)
                             && !type.Equals("application/timestamp-response", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException($"The timestamp server did not return a timestamp (it sent {type}).");
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        };
    }
}
