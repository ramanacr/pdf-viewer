using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Signatures;

namespace PdfViewer.Services;

/// <summary>
/// Revocation information over HTTP, used only after the user has agreed to contact the
/// certificate authorities: OCSP (RFC 6960 A.1, the request POSTed as application/ocsp-request),
/// CRLs from the distribution points, and issuer certificates from the caIssuers addresses.
/// What goes out identifies certificates (a serial number, hashes of the issuer's name and key),
/// never the document. Each address is tried once with a short timeout, in the order the
/// certificate lists them, and the first answer wins; there are no retries. LDAP and other
/// schemes are skipped. Responses are size-limited, and nothing fetched is trusted here: the
/// engine verifies every signature on it.
/// </summary>
public sealed class HttpRevocationSource : IPdfRevocationSource, IDisposable
{
    private const int MaxOcspBytes = 1 << 20;
    private const int MaxCrlBytes = 32 << 20;   // large CAs publish big lists
    private const int MaxIssuerBytes = 1 << 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;

    public HttpRevocationSource()
        : this(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
        }, disposeHandler: true)
    {
    }

    /// <summary>With a handler of the caller's (tests use one that answers from memory).</summary>
    public HttpRevocationSource(HttpMessageHandler handler, bool disposeHandler = false)
    {
        _http = new HttpClient(handler, disposeHandler) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PdfViewer-Revocation/1.0");
    }

    public async Task<byte[]?> GetOcspResponseAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken)
    {
        var addresses = Web(PdfCertificateUrls.Ocsp(certificate));
        if (addresses.Count == 0) return null;
        byte[] request = PdfOcspRequest.Create(certificate, issuer);
        return await FirstAnswerAsync(addresses, "OCSP responder", async uri =>
        {
            using var content = new ByteArrayContent(request);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ocsp-response"));
            return await SendAsync(message, MaxOcspBytes, "application/ocsp-response", cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<byte[]>> GetCrlsAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken)
    {
        var addresses = Web(PdfCertificateUrls.CrlDistributionPoints(certificate));
        if (addresses.Count == 0) return Array.Empty<byte[]>();
        // The distribution points are mirrors of one list: the first that answers is enough.
        byte[]? crl = await FirstAnswerAsync(addresses, "revocation list server", uri =>
            SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), MaxCrlBytes, null, cancellationToken), cancellationToken).ConfigureAwait(false);
        return crl != null ? new[] { crl } : Array.Empty<byte[]>();
    }

    public async Task<IReadOnlyList<X509Certificate2>> GetIssuersAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        var addresses = Web(PdfCertificateUrls.CaIssuers(certificate));
        if (addresses.Count == 0) return Array.Empty<X509Certificate2>();
        byte[]? data = await FirstAnswerAsync(addresses, "issuer certificate server", uri =>
            SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), MaxIssuerBytes, null, cancellationToken), cancellationToken).ConfigureAwait(false);
        return data != null ? Certificates(data) : Array.Empty<X509Certificate2>();
    }

    /// <summary>A DER certificate, or a certs-only PKCS #7 bundle (.p7c), as caIssuers addresses serve them.</summary>
    internal static IReadOnlyList<X509Certificate2> Certificates(byte[] data)
    {
        try
        {
            return new[] { X509CertificateLoader.LoadCertificate(data) };
        }
        catch (CryptographicException)
        {
        }
        try
        {
            var bundle = new SignedCms();
            bundle.Decode(data);
            return bundle.Certificates.Cast<X509Certificate2>().ToList();
        }
        catch (CryptographicException)
        {
            return Array.Empty<X509Certificate2>();
        }
    }

    private static List<Uri> Web(IReadOnlyList<Uri> addresses) =>
        addresses.Where(u => u.Scheme is "http" or "https").Distinct().ToList();

    /// <summary>Asks each address once, in order; the first answer wins. When none answers, the last failure explains why.</summary>
    private static async Task<byte[]?> FirstAnswerAsync(IReadOnlyList<Uri> addresses, string what, Func<Uri, Task<byte[]>> ask, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var uri in addresses)
        {
            try
            {
                return await ask(uri).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                last = new HttpRequestException($"The {what} at {uri.Host} did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                last = new HttpRequestException($"The {what} at {uri.Host} could not be reached: {ex.Message}", ex);
            }
        }
        throw last ?? new HttpRequestException($"No {what} answered.");
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage message, int maxBytes, string? expectedType, CancellationToken ct)
    {
        using (message)
        using (var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"it answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (expectedType != null && type != null && !type.Equals(expectedType, StringComparison.OrdinalIgnoreCase)
                && !type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException($"it sent {type}, not {expectedType}.");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new HttpRequestException("its answer is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new HttpRequestException("its answer is too large.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    public void Dispose() => _http.Dispose();
}
