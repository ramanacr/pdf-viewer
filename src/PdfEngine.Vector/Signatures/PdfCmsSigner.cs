using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEngine.Vector.Signatures;

/// <summary>
/// Sends an RFC 3161 timestamp request (DER) to a time-stamping authority and returns its
/// response (DER). The viewer supplies this; the core never touches the network.
/// </summary>
public delegate Task<byte[]> PdfTimestampClient(byte[] request, CancellationToken cancellationToken);

/// <summary>
/// Builds the CMS for a PAdES baseline signature (ETSI EN 319 142-1, /SubFilter
/// /ETSI.CAdES.detached): detached SignedData over the byte ranges, SHA-256, the signer's chain,
/// the ESS signing-certificate-v2 attribute and no signing-time attribute (the time is /M in the
/// signature dictionary). With a timestamp client it adds a signature timestamp as an unsigned
/// attribute, which makes it PAdES-B-T.
/// </summary>
public static class PdfCmsSigner
{
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string SignatureTimeStampOid = "1.2.840.113549.1.9.16.2.14";

    public static async Task<byte[]> SignAsync(byte[] signedBytes, X509Certificate2 certificate, PdfTimestampClient? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The certificate has no private key to sign with.", nameof(certificate));

        var cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.WholeChain,
        };
        signer.SignedAttributes.Add(new AsnEncodedData(SigningCertificateV2Oid, SigningCertificateV2(certificate)));
        cms.ComputeSignature(signer, silent: true);

        if (timestamp != null)
        {
            var signerInfo = cms.SignerInfos[0];
            byte[] hash = SHA256.HashData(signerInfo.GetSignature());
            var request = Rfc3161TimestampRequest.CreateFromHash(hash, HashAlgorithmName.SHA256,
                nonce: RandomNumberGenerator.GetBytes(8), requestSignerCertificates: true);
            byte[] response = await timestamp(request.Encode(), cancellationToken).ConfigureAwait(false);
            var token = request.ProcessResponse(response, out _);
            signerInfo.AddUnsignedAttribute(new AsnEncodedData(SignatureTimeStampOid, token.AsSignedCms().Encode()));
        }
        return cms.Encode();
    }

    /// <summary>SigningCertificateV2 (RFC 5035): the signer certificate's SHA-256 and issuer/serial.</summary>
    internal static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())                 // SigningCertificateV2
        using (w.PushSequence())                 //   certs SEQUENCE OF ESSCertIDv2
        using (w.PushSequence())                 //     ESSCertIDv2 (hashAlgorithm omitted: SHA-256 is the default)
        {
            w.WriteOctetString(SHA256.HashData(certificate.RawData));
            using (w.PushSequence())             //       IssuerSerial
            {
                using (w.PushSequence())         //         GeneralNames
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true))) // directoryName [4]
                    w.WriteEncodedValue(certificate.IssuerName.RawData);
                WriteSerial(w, certificate.SerialNumberBytes.Span);
            }
        }
        return w.Encode();
    }

    /// <summary>
    /// The serial number INTEGER with the certificate's own content bytes. AsnWriter.WriteInteger
    /// rejects non-minimal encodings, which some CAs issue; the ESS reference must match the
    /// certificate byte for byte either way.
    /// </summary>
    internal static void WriteSerial(AsnWriter w, ReadOnlySpan<byte> serial)
    {
        try
        {
            w.WriteInteger(serial);
        }
        catch (ArgumentException)
        {
            var raw = new List<byte> { 0x02 };
            int n = serial.Length;
            if (n < 0x80) raw.Add((byte)n);
            else if (n <= 0xFF) { raw.Add(0x81); raw.Add((byte)n); }
            else { raw.Add(0x82); raw.Add((byte)(n >> 8)); raw.Add((byte)n); }
            raw.AddRange(serial.ToArray());
            w.WriteEncodedValue(raw.ToArray());
        }
    }
}
