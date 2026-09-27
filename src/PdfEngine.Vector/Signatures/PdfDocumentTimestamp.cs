using System;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;

namespace PdfEngine.Vector.Signatures;

/// <summary>
/// A time-stamping authority (RFC 3161): takes a DER TimeStampReq and returns the DER
/// TimeStampResp. The core never implements this with the network; the viewer's implementation
/// goes over HTTP, and only to an authority the user chose, when the user asked for a timestamp.
/// Tests answer from memory.
/// </summary>
public interface IPdfTimestampClient
{
    Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken);
}

/// <summary>Adapters between the timestamp client interface and the <see cref="PdfTimestampClient"/> delegate.</summary>
public static class PdfTimestampClients
{
    public static PdfTimestampClient AsDelegate(this IPdfTimestampClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.RequestAsync;
    }

    public static IPdfTimestampClient FromDelegate(PdfTimestampClient client) => new DelegateClient(client ?? throw new ArgumentNullException(nameof(client)));

    private sealed class DelegateClient : IPdfTimestampClient
    {
        private readonly PdfTimestampClient _client;
        public DelegateClient(PdfTimestampClient client) => _client = client;
        public Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken) => _client(request, cancellationToken);
    }
}

/// <summary>How to add a document timestamp.</summary>
public sealed record PdfDocumentTimestampOptions
{
    public string? Password { get; init; }
    /// <summary>
    /// Where validation data comes from. When set, the validation data of every signature and
    /// earlier document timestamp is added before the new timestamp (so the timestamp protects
    /// it: PAdES-B-LTA, or the renewal of an archive), and the new timestamp's own is added after
    /// it. Null adds the timestamp alone.
    /// </summary>
    public IPdfRevocationSource? ValidationData { get; init; }
    /// <summary>Bytes reserved for the timestamp token; more is reserved and the timestamp asked for again if it does not fit.</summary>
    public int ContentsSize { get; init; } = 16384;
}

/// <summary>What adding a document timestamp did.</summary>
public sealed class PdfDocumentTimestampResult
{
    public required byte[] Bytes { get; init; }
    /// <summary>The time the authority vouched for.</summary>
    public required DateTimeOffset Time { get; init; }
    public string? Authority { get; init; }
    public X509Certificate2? AuthorityCertificate { get; init; }
    /// <summary>The validation data added before the timestamp (the signatures' and earlier timestamps'), when asked for.</summary>
    public PdfLtvResult? ValidationDataBefore { get; init; }
    /// <summary>The validation data added after it (the new timestamp's own authority), when asked for.</summary>
    public PdfLtvResult? ValidationDataAfter { get; init; }
    /// <summary>Every signature and timestamp had its validation data complete (true when none was asked for).</summary>
    public bool IsValidationDataComplete => (ValidationDataBefore?.IsComplete ?? true) && (ValidationDataAfter?.IsComplete ?? true);
}

/// <summary>
/// Document timestamps (ISO 32000-2 12.8.5; ETSI EN 319 142-1 5.5, PAdES-B-LTA): an RFC 3161
/// timestamp token over the whole document, including its Document Security Store, appended as
/// an incremental update in an invisible signature field. It proves the document, and the
/// validation data in it, existed unchanged at that time, which keeps the signatures verifiable
/// after their certificates, their revocation information, or their algorithms have aged.
/// Timestamping again later (with the previous timestamp's validation data added first) renews
/// that protection before the last authority's certificate expires.
/// </summary>
public static class PdfDocumentTimestamp
{
    public static async Task<PdfDocumentTimestampResult> AddAsync(byte[] file, IPdfTimestampClient client, PdfDocumentTimestampOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(client);
        options ??= new PdfDocumentTimestampOptions();

        PdfLtvResult? before = null;
        if (options.ValidationData is { } source && await HasSignaturesAsync(file, options.Password, cancellationToken).ConfigureAwait(false))
        {
            before = await PdfLtv.AddValidationDataAsync(file, source, options.Password, cancellationToken).ConfigureAwait(false);
            file = before.Bytes;
        }

        byte[] stamped;
        Rfc3161TimestampToken token;
        X509Certificate2? tsa;
        using (var doc = await PdfVectorDocument.OpenAsync(file, password: options.Password, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            int size = Math.Max(1024, options.ContentsSize);
            for (int attempt = 0; ; attempt++)
            {
                var prepared = PdfSigner.PrepareDocumentTimestamp(doc, file, size);
                byte[] hash = SHA256.HashData(prepared.SignedBytes());
                (token, tsa) = await RequestAsync(client, hash, cancellationToken).ConfigureAwait(false);
                byte[] encoded = token.AsSignedCms().Encode();
                if (encoded.Length <= size)
                {
                    stamped = prepared.Complete(encoded);
                    break;
                }
                if (attempt >= 3) throw new InvalidOperationException($"The timestamp ({encoded.Length} bytes) is larger than can be reserved for it.");
                size = Math.Max(size * 2, encoded.Length + 4096);
            }
        }

        PdfLtvResult? after = null;
        if (options.ValidationData is { } again)
        {
            after = await PdfLtv.AddValidationDataAsync(stamped, again, options.Password, cancellationToken).ConfigureAwait(false);
            stamped = after.Bytes;
        }
        return new PdfDocumentTimestampResult
        {
            Bytes = stamped, Time = token.TokenInfo.Timestamp, AuthorityCertificate = tsa,
            Authority = tsa?.GetNameInfo(X509NameType.SimpleName, false),
            ValidationDataBefore = before, ValidationDataAfter = after,
        };
    }

    /// <summary>
    /// Asks the authority for a token over <paramref name="hash"/> (SHA-256) with a fresh nonce,
    /// and accepts it only if it is granted, answers this request (imprint and nonce), and its
    /// signature verifies.
    /// </summary>
    internal static async Task<(Rfc3161TimestampToken Token, X509Certificate2? Authority)> RequestAsync(IPdfTimestampClient client, byte[] hash,
        CancellationToken cancellationToken)
    {
        var request = Rfc3161TimestampRequest.CreateFromHash(hash, HashAlgorithmName.SHA256,
            nonce: RandomNumberGenerator.GetBytes(8), requestSignerCertificates: true);
        byte[] response = await client.RequestAsync(request.Encode(), cancellationToken).ConfigureAwait(false);
        Rfc3161TimestampToken token;
        try
        {
            token = request.ProcessResponse(response, out _);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The time-stamping authority did not return a valid timestamp for this document: {ex.Message}", ex);
        }
        if (!token.VerifySignatureForHash(hash, HashAlgorithmName.SHA256, out var authority, null))
            throw new InvalidOperationException("The time-stamping authority's timestamp is not signed correctly.");
        return (token, authority);
    }

    private static async Task<bool> HasSignaturesAsync(byte[] file, string? password, CancellationToken ct)
    {
        using var doc = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: ct).ConfigureAwait(false);
        return PdfSignatureValidator.SignedFields(doc).Count > 0;
    }
}
