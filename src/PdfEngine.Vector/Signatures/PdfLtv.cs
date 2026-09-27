using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Signatures;

/// <summary>What adding long-term validation data did for one signature.</summary>
public sealed class PdfLtvSignatureReport
{
    public required string FieldName { get; init; }
    /// <summary>Every certificate in the signer's chain (and any timestamp authority's) now has revocation information in the document.</summary>
    public bool IsComplete => Problems.Count == 0;
    /// <summary>What could not be obtained or verified, one sentence each.</summary>
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    /// <summary>Certificates the fetched information shows as revoked.</summary>
    public IReadOnlyList<string> Revoked { get; init; } = Array.Empty<string>();
}

/// <summary>The document with its validation data, and what was found for each signature.</summary>
public sealed class PdfLtvResult
{
    public required byte[] Bytes { get; init; }
    /// <summary>False when there was nothing new to add (the document is returned as it was).</summary>
    public bool Changed { get; init; }
    public required IReadOnlyList<PdfLtvSignatureReport> Signatures { get; init; }
    public bool IsComplete => Signatures.All(s => s.IsComplete);
}

/// <summary>
/// The Document Security Store (ISO 32000-2 12.8.4.3) as a document holds it: the certificates,
/// OCSP responses and CRLs (decoded), the object each is stored in, and the per-signature VRI
/// entries keyed by the uppercase hex SHA-1 of the signature's /Contents.
/// </summary>
internal sealed class PdfDss
{
    internal sealed class Item
    {
        public required byte[] Data { get; init; }
        public PdfIndirectRef? Ref { get; set; }
        public bool IsNew { get; init; }
    }

    internal sealed record Vri(List<Item> Certs, List<Item> Ocsps, List<Item> Crls);

    public List<Item> Certs { get; } = new();
    public List<Item> Ocsps { get; } = new();
    public List<Item> Crls { get; } = new();
    public Dictionary<string, Vri> VriEntries { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The existing /VRI values, as written, so an update keeps entries it does not replace.</summary>
    public Dictionary<string, PdfObject> RawVri { get; } = new(StringComparer.Ordinal);
    public int? ObjectNumber { get; private set; }
    public bool Exists { get; private set; }

    public static PdfDss Read(PdfVectorDocument doc)
    {
        var dss = new PdfDss();
        var r = doc.Resolver;
        if (r.Resolve(doc.XrefTable.Trailer?["Root"]) is not PdfDictionary root) return dss;
        var dssObj = root["DSS"];
        if (r.Resolve(dssObj) is not PdfDictionary dict) return dss;
        dss.Exists = true;
        dss.ObjectNumber = (dssObj as PdfIndirectRef)?.ObjectNumber;
        var byRef = new Dictionary<int, Item>();

        List<Item> Items(PdfObject? list)
        {
            var result = new List<Item>();
            if (r.Resolve(list) is not PdfArray array) return result;
            foreach (var entry in array)
            {
                if (entry is PdfIndirectRef ir && byRef.TryGetValue(ir.ObjectNumber, out var known)) { result.Add(known); continue; }
                if (r.Resolve(entry) is not PdfStream stream) continue;
                byte[] data;
                try { data = new Streams.PdfStreamDecoder(null, r.Resolve).DecodeStream(stream); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
                var item = new Item { Data = data, Ref = entry as PdfIndirectRef };
                if (entry is PdfIndirectRef named) byRef[named.ObjectNumber] = item;
                result.Add(item);
            }
            return result;
        }

        dss.Certs.AddRange(Items(dict["Certs"]));
        dss.Ocsps.AddRange(Items(dict["OCSPs"]));
        dss.Crls.AddRange(Items(dict["CRLs"]));
        if (r.Resolve(dict["VRI"]) is PdfDictionary vri)
            foreach (var (key, value) in vri.Entries)
            {
                dss.RawVri[key] = value;
                if (r.Resolve(value) is not PdfDictionary entry) continue;
                dss.VriEntries[key] = new Vri(Items(entry["Cert"]), Items(entry["OCSP"]), Items(entry["CRL"]));
            }
        return dss;
    }
}

/// <summary>
/// Adds long-term validation data to a signed document (PAdES-B-LT, ETSI EN 319 142-1 5.4): for
/// every signature, the certificates of the signer's chain and of any timestamp authority's,
/// with an OCSP response or CRL for each, stored in the Document Security Store with a VRI
/// entry per signature. It is an incremental update of validation data only, which signatures
/// and even no-changes certification permit (ISO 32000-2 12.8.2.2.2), so every signature stays
/// valid; and it lets them be validated after their certificates expire or their CAs go away.
/// What comes from outside comes only through the <see cref="IPdfRevocationSource"/> given.
/// </summary>
public static class PdfLtv
{
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    public static async Task<PdfLtvResult> AddValidationDataAsync(byte[] file, IPdfRevocationSource source, string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var doc = await PdfVectorDocument.OpenAsync(file, password: password, cancellationToken: cancellationToken).ConfigureAwait(false);
        var trailer = doc.XrefTable.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        if (trailer["Root"] is not PdfIndirectRef rootRef || doc.Resolver.Resolve(rootRef) is not PdfDictionary root)
            throw new InvalidOperationException("The document has no catalog.");
        var signatures = PdfSignatureValidator.SignedFields(doc);
        if (signatures.Count == 0) throw new InvalidOperationException("The document has no signatures.");

        var dss = PdfDss.Read(doc);
        var fetch = new Fetcher(source, cancellationToken);
        // Everything the document already carries helps build chains; fetched issuers join it.
        var pool = new List<X509Certificate2>();
        foreach (var item in dss.Certs) TryAddCertificate(pool, item.Data);

        var byHash = new Dictionary<string, PdfDss.Item>(StringComparer.Ordinal);
        foreach (var item in dss.Certs.Concat(dss.Ocsps).Concat(dss.Crls)) byHash.TryAdd(Convert.ToHexString(SHA256.HashData(item.Data)), item);
        PdfDss.Item Add(List<PdfDss.Item> list, byte[] data)
        {
            string hash = Convert.ToHexString(SHA256.HashData(data));
            if (byHash.TryGetValue(hash, out var existing))
            {
                if (!list.Contains(existing)) list.Add(existing);
                return existing;
            }
            var item = new PdfDss.Item { Data = data, IsNew = true };
            byHash[hash] = item;
            list.Add(item);
            return item;
        }

        var reports = new List<PdfLtvSignatureReport>();
        var newVri = new Dictionary<string, PdfDss.Vri>(StringComparer.Ordinal);
        foreach (var (field, sig, range) in signatures)
        {
            var problems = new List<string>();
            var revoked = new List<string>();
            if (range.Length != 4 || range.Any(v => v < 0) || range[2] > file.Length
                || PdfSignatureValidator.ContentsFromGap(file, range[1], range[2], trim: false) is not { Length: > 0 } contents)
            {
                reports.Add(new PdfLtvSignatureReport { FieldName = field.FullName, Problems = new[] { "The signature's value could not be read." } });
                continue;
            }
            var subjects = Subjects(PdfSignatureValidator.ContentsFromGap(file, range[1], range[2], trim: true)!, sig);
            if (subjects.Count == 0)
            {
                reports.Add(new PdfLtvSignatureReport { FieldName = field.FullName, Problems = new[] { "The signature carries no signer certificate." } });
                continue;
            }
            string key = Convert.ToHexString(SHA1.HashData(contents));
            var earlier = dss.VriEntries.GetValueOrDefault(key);
            var vri = new PdfDss.Vri(new List<PdfDss.Item>(), new List<PdfDss.Item>(), new List<PdfDss.Item>());
            foreach (var subject in subjects)
            {
                foreach (var c in subject.Included) if (!pool.Any(p => PdfX509.SameCertificate(p, c))) pool.Add(c);
                var path = await BuildPathAsync(subject.Certificate, pool, fetch, problems).ConfigureAwait(false);
                foreach (var c in path) vri.Certs.Add(Add(dss.Certs, c.RawData));
                for (int i = 0; i < path.Count; i++)
                {
                    var cert = path[i];
                    if (i == path.Count - 1)
                    {
                        if (!PdfX509.IsSelfSigned(cert))
                            problems.Add($"The certificate that issued \"{PdfX509.Name(cert)}\" could not be found.");
                        break;
                    }
                    await GatherAsync(cert, path[i + 1], subject.Time, pool, fetch, dss, earlier, vri, Add, problems, revoked, depth: 0).ConfigureAwait(false);
                }
            }
            newVri[key] = vri;
            reports.Add(new PdfLtvSignatureReport { FieldName = field.FullName, Problems = problems.Distinct().ToList(), Revoked = revoked.Distinct().ToList() });
        }

        bool anyNew = byHash.Values.Any(i => i.IsNew) || newVri.Any(v => !dss.VriEntries.ContainsKey(v.Key) || !SameVri(dss.VriEntries[v.Key], v.Value));
        if (!anyNew) return new PdfLtvResult { Bytes = file, Changed = false, Signatures = reports };

        // Lay out the update: new streams, the DSS (merged with what was there), and the catalog when the DSS is new.
        var objects = new Dictionary<int, PdfObject>();
        int next = PdfIncrementalWriter.NextObjectNumber(doc);
        foreach (var item in dss.Certs.Concat(dss.Ocsps).Concat(dss.Crls).Where(i => i.IsNew && i.Ref == null))
        {
            item.Ref = new PdfIndirectRef(next++);
            objects[item.Ref.ObjectNumber] = Compressed(item.Data);
        }
        PdfArray Refs(IEnumerable<PdfDss.Item> items) => new(items.Where(i => i.Ref != null).Select(i => (PdfObject)i.Ref!).Distinct().ToList());

        var vriEntries = new Dictionary<string, PdfObject>(dss.RawVri);
        string now = PdfSigner.PdfDate(DateTimeOffset.Now);
        foreach (var (key, vri) in newVri)
        {
            var entry = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("VRI") };
            if (vri.Certs.Count > 0) entry["Cert"] = Refs(vri.Certs);
            if (vri.Ocsps.Count > 0) entry["OCSP"] = Refs(vri.Ocsps);
            if (vri.Crls.Count > 0) entry["CRL"] = Refs(vri.Crls);
            entry["TU"] = PdfObjectWriter.TextString(now);
            vriEntries[key] = new PdfDictionary(entry);
        }
        var dssEntries = new Dictionary<string, PdfObject> { ["Type"] = new PdfName("DSS") };
        if (dss.Certs.Count > 0) dssEntries["Certs"] = Refs(dss.Certs);
        if (dss.Ocsps.Count > 0) dssEntries["OCSPs"] = Refs(dss.Ocsps);
        if (dss.Crls.Count > 0) dssEntries["CRLs"] = Refs(dss.Crls);
        if (vriEntries.Count > 0) dssEntries["VRI"] = new PdfDictionary(vriEntries);

        if (dss.ObjectNumber is int dssNumber)
            objects[dssNumber] = new PdfDictionary(dssEntries);
        else
        {
            int number = next++;
            objects[number] = new PdfDictionary(dssEntries);
            objects[rootRef.ObjectNumber] = new PdfDictionary(new Dictionary<string, PdfObject>(root.Entries) { ["DSS"] = new PdfIndirectRef(number) });
        }
        byte[] bytes = PdfIncrementalWriter.Append(file, doc, objects);
        return new PdfLtvResult { Bytes = bytes, Changed = true, Signatures = reports };
    }

    private static bool SameVri(PdfDss.Vri a, PdfDss.Vri b)
    {
        static bool Same(List<PdfDss.Item> x, List<PdfDss.Item> y) => x.Count == y.Count && x.All(y.Contains);
        return Same(a.Certs, b.Certs) && Same(a.Ocsps, b.Ocsps) && Same(a.Crls, b.Crls);
    }

    /// <summary>A certificate whose status the signature depends on, the time that status matters at, and the certificates that came with it.</summary>
    internal sealed record Subject(X509Certificate2 Certificate, DateTimeOffset Time, IReadOnlyList<X509Certificate2> Included);

    /// <summary>
    /// The certificates a signature's validity rests on: its signer (the time-stamping authority,
    /// for a document timestamp) at the signing time, and the authority of each signature
    /// timestamp at the time it vouched for.
    /// </summary>
    internal static List<Subject> Subjects(byte[] cmsBytes, PdfDictionary sig)
    {
        var result = new List<Subject>();
        SignedCms cms;
        try
        {
            cms = new SignedCms();
            cms.Decode(cmsBytes);
        }
        catch (CryptographicException)
        {
            return result;
        }
        if (cms.SignerInfos.Count == 0) return result;
        var signerInfo = cms.SignerInfos[0];
        var included = cms.Certificates.Cast<X509Certificate2>().ToList();
        DateTimeOffset? tokenTime = null;
        var tokens = new List<Subject>();
        if (Rfc3161TimestampToken.TryDecode(cmsBytes, out var docToken, out _) && docToken != null)
            tokenTime = docToken.TokenInfo.Timestamp; // a document timestamp: the signer is the TSA
        foreach (CryptographicAttributeObject attr in signerInfo.UnsignedAttributes)
        {
            if (attr.Oid.Value != TimestampTokenOid || attr.Values.Count == 0) continue;
            if (!Rfc3161TimestampToken.TryDecode(attr.Values[0].RawData, out var token, out _) || token == null) continue;
            var tokenCms = token.AsSignedCms();
            var tsa = tokenCms.SignerInfos.Count > 0 ? tokenCms.SignerInfos[0].Certificate : null;
            if (tsa != null)
                tokens.Add(new Subject(tsa, token.TokenInfo.Timestamp, tokenCms.Certificates.Cast<X509Certificate2>().ToList()));
            tokenTime ??= token.TokenInfo.Timestamp;
        }
        if (signerInfo.Certificate is { } signer)
        {
            var time = tokenTime ?? PdfSignatureValidator.ParsePdfDate((sig["M"] as PdfString)?.AsDecodedString()) ?? DateTimeOffset.Now;
            result.Add(new Subject(signer, time, included));
        }
        result.AddRange(tokens);
        return result;
    }

    /// <summary>
    /// The chain from <paramref name="certificate"/> towards a self-signed root: issuers from the
    /// pool first, then from the certificate's caIssuers addresses. Each link is checked by its
    /// signature, so a wrong certificate offered by a server is never taken for the issuer.
    /// </summary>
    internal static async Task<List<X509Certificate2>> BuildPathAsync(X509Certificate2 certificate, List<X509Certificate2> pool,
        Fetcher? fetch, List<string> problems)
    {
        var path = new List<X509Certificate2> { certificate };
        var current = certificate;
        for (int depth = 0; depth < 10 && !PdfX509.IsSelfSigned(current); depth++)
        {
            var issuer = pool.FirstOrDefault(c => !PdfX509.SameCertificate(c, current) && PdfX509.IsIssuedBy(current, c));
            if (issuer == null && fetch != null)
            {
                foreach (var candidate in await fetch.IssuersAsync(current, problems).ConfigureAwait(false))
                    if (PdfX509.IsIssuedBy(current, candidate)) { issuer = candidate; break; }
                if (issuer != null && !pool.Any(p => PdfX509.SameCertificate(p, issuer))) pool.Add(issuer);
            }
            if (issuer == null || path.Any(p => PdfX509.SameCertificate(p, issuer))) break;
            path.Add(issuer);
            current = issuer;
        }
        return path;
    }

    private static async Task GatherAsync(X509Certificate2 cert, X509Certificate2 issuer, DateTimeOffset at, List<X509Certificate2> pool,
        Fetcher fetch, PdfDss dss, PdfDss.Vri? earlier, PdfDss.Vri vri, Func<List<PdfDss.Item>, byte[], PdfDss.Item> add, List<string> problems,
        List<string> revoked, int depth)
    {
        string name = PdfX509.Name(cert);
        bool obtained = false;
        var notes = new List<string>(); // reasons one source failed, which matter only if no other succeeds
        if (depth == 0 && await fetch.OcspAsync(cert, issuer, notes).ConfigureAwait(false) is { } ocspBytes)
        {
            var response = PdfOcspResponse.TryParse(ocspBytes);
            var finding = response == null ? null : PdfRevocationChecker.FromOcsp(response, cert, issuer, pool, at, online: true);
            if (response != null && finding is { Status: PdfRevocationStatus.Good or PdfRevocationStatus.Revoked })
            {
                vri.Ocsps.Add(add(dss.Ocsps, response.Encoded));
                obtained = true;
                if (PdfRevocationChecker.CountsAgainst(finding, at, trustedTime: true)) revoked.Add(name);
                if (finding.Responder is { } responder && !PdfX509.SameCertificate(responder, issuer))
                {
                    // A delegated responder: its certificate goes in too, and its own status unless it is exempt (id-pkix-ocsp-nocheck).
                    vri.Certs.Add(add(dss.Certs, responder.RawData));
                    if (responder.Extensions[PdfX509.OcspNoCheckOid] == null)
                        await GatherAsync(responder, issuer, response.ProducedAt, pool, fetch, dss, earlier, vri, add, problems, revoked, depth + 1).ConfigureAwait(false);
                }
            }
            else notes.Add($"The OCSP response about \"{name}\" could not be verified.");
        }
        if (!obtained)
        {
            foreach (var crlBytes in await fetch.CrlsAsync(cert, issuer, notes).ConfigureAwait(false))
            {
                if (PdfCrl.TryParse(crlBytes) is not { } crl) continue;
                if (PdfRevocationChecker.FromCrl(crl, cert, issuer, at, online: true) is not { } finding) continue;
                vri.Crls.Add(add(dss.Crls, crl.Encoded));
                obtained = true;
                if (PdfRevocationChecker.CountsAgainst(finding, at, trustedTime: true)) revoked.Add(name);
                break;
            }
        }
        if (!obtained)
        {
            // Nothing new; what the document already holds may still answer for it (this
            // signature's own VRI entry first, so an unchanged answer leaves the entry unchanged).
            var none = new List<PdfDss.Item>();
            foreach (var item in (earlier?.Ocsps ?? none).Concat(dss.Ocsps))
                if (PdfOcspResponse.TryParse(item.Data) is { } r && PdfRevocationChecker.FromOcsp(r, cert, issuer, pool, at, false) is { Status: not PdfRevocationStatus.Unknown })
                { vri.Ocsps.Add(item); obtained = true; break; }
            if (!obtained)
                foreach (var item in (earlier?.Crls ?? none).Concat(dss.Crls))
                    if (PdfCrl.TryParse(item.Data) is { } c && PdfRevocationChecker.FromCrl(c, cert, issuer, at, false) != null)
                    { vri.Crls.Add(item); obtained = true; break; }
        }
        if (!obtained)
        {
            problems.AddRange(notes);
            problems.Add($"No revocation information could be obtained for \"{name}\".");
        }
    }

    private static void TryAddCertificate(List<X509Certificate2> pool, byte[] data)
    {
        try { pool.Add(X509CertificateLoader.LoadCertificate(data)); }
        catch (CryptographicException) { }
    }

    /// <summary>A DSS stream: the data Flate-compressed, as other writers store it.</summary>
    private static PdfStream Compressed(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return PdfObjectWriter.NewStream(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") }, ms.ToArray());
    }

    /// <summary>
    /// Asks the revocation source once per certificate: a chain shared by several signatures, or
    /// a responder that does not answer, costs one request, not one per signature. Failures
    /// (including a source's own timeout) become problems in the report, not exceptions; only
    /// the caller's cancellation stops the work.
    /// </summary>
    internal sealed class Fetcher
    {
        private readonly IPdfRevocationSource _source;
        private readonly CancellationToken _ct;
        private readonly Dictionary<string, byte[]?> _ocsp = new();
        private readonly Dictionary<string, IReadOnlyList<byte[]>> _crls = new();
        private readonly Dictionary<string, IReadOnlyList<X509Certificate2>> _issuers = new();

        public Fetcher(IPdfRevocationSource source, CancellationToken ct)
        {
            _source = source;
            _ct = ct;
        }

        private static string Key(X509Certificate2 a, X509Certificate2? b = null) => a.Thumbprint + "/" + b?.Thumbprint;

        public async Task<byte[]?> OcspAsync(X509Certificate2 cert, X509Certificate2 issuer, List<string> problems)
        {
            string key = Key(cert, issuer);
            if (_ocsp.TryGetValue(key, out var known)) return known;
            byte[]? result = await Guard(() => _source.GetOcspResponseAsync(cert, issuer, _ct), $"The OCSP responder for \"{PdfX509.Name(cert)}\"", problems).ConfigureAwait(false);
            _ocsp[key] = result;
            return result;
        }

        public async Task<IReadOnlyList<byte[]>> CrlsAsync(X509Certificate2 cert, X509Certificate2 issuer, List<string> problems)
        {
            string key = Key(cert, issuer);
            if (_crls.TryGetValue(key, out var known)) return known;
            var result = await Guard(() => _source.GetCrlsAsync(cert, issuer, _ct), $"The revocation list for \"{PdfX509.Name(cert)}\"", problems).ConfigureAwait(false)
                         ?? (IReadOnlyList<byte[]>)Array.Empty<byte[]>();
            _crls[key] = result;
            return result;
        }

        public async Task<IReadOnlyList<X509Certificate2>> IssuersAsync(X509Certificate2 cert, List<string> problems)
        {
            string key = Key(cert);
            if (_issuers.TryGetValue(key, out var known)) return known;
            var result = await Guard(() => _source.GetIssuersAsync(cert, _ct), $"The issuer certificate of \"{PdfX509.Name(cert)}\"", problems).ConfigureAwait(false)
                         ?? (IReadOnlyList<X509Certificate2>)Array.Empty<X509Certificate2>();
            _issuers[key] = result;
            return result;
        }

        private async Task<T?> Guard<T>(Func<Task<T>> call, string what, List<string> problems) where T : class?
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
            {
                problems.Add($"{what} could not be fetched: the server did not answer in time.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                problems.Add($"{what} could not be fetched: {ex.Message}");
            }
            return null;
        }
    }
}
