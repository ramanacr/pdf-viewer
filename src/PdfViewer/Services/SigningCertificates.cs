using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfViewer.Services;

/// <summary>
/// Certificates the user can sign with: the personal store's certificates with a private key
/// (including smart cards and tokens, whose keys stay on the device), or a .pfx/.p12 file.
/// </summary>
public static class SigningCertificates
{
    // Extended key usages that allow document signing. A certificate without an EKU extension
    // is unrestricted.
    private static readonly HashSet<string> SigningUsages = new(StringComparer.Ordinal)
    {
        "2.5.29.37.0",               // any extended key usage
        "1.3.6.1.5.5.7.3.4",         // email protection (S/MIME certificates, the usual personal ID)
        "1.3.6.1.5.5.7.3.36",        // document signing (RFC 9336)
        "1.3.6.1.4.1.311.10.3.12",   // Microsoft document signing
        "1.2.840.113583.1.1.5",      // Adobe Authentic Documents Trust
        "1.3.6.1.5.5.7.3.2",         // client authentication (smart-card identities)
        "1.3.6.1.4.1.311.20.2.2",    // smart-card logon
    };

    /// <summary>Why the certificate cannot sign, or null when it can.</summary>
    public static string? WhyNotUsable(X509Certificate2 certificate, DateTimeOffset now)
    {
        if (!certificate.HasPrivateKey)
            return "There is no private key for this certificate on this computer.";
        if (now < certificate.NotBefore)
            return $"This certificate is not valid until {certificate.NotBefore:d}.";
        if (now > certificate.NotAfter)
            return $"This certificate expired on {certificate.NotAfter:d}.";
        if (certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } ku &&
            (ku.KeyUsages & (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation)) == 0)
            return "This certificate is not issued for digital signatures.";
        if (certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } eku &&
            !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value != null && SigningUsages.Contains(o.Value)))
            return "This certificate is not issued for signing documents.";
        if (certificate.GetRSAPublicKey() == null && certificate.GetECDsaPublicKey() == null)
            return "Only RSA and ECDSA keys can sign.";
        return null;
    }

    /// <summary>The current user's personal certificates that can sign, soonest-expiring last.</summary>
    public static List<X509Certificate2> FromStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTimeOffset.Now;
        var usable = new List<X509Certificate2>();
        foreach (var cert in store.Certificates)
        {
            if (WhyNotUsable(cert, now) == null) usable.Add(cert);
            else cert.Dispose();
        }
        return usable.OrderByDescending(c => c.NotAfter).ToList();
    }

    /// <summary>Loads a signing identity from a PKCS#12 file. The key is kept in memory only.</summary>
    public static X509Certificate2 FromFile(string path, string? password)
    {
        var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        var withKey = all.Where(c => c.HasPrivateKey).ToList();
        foreach (var c in all.Where(c => !c.HasPrivateKey)) c.Dispose();
        if (withKey.Count == 0)
            throw new CryptographicException("The file holds no certificate with a private key.");
        // The end-entity certificate: the one that issued no other certificate in the file.
        var chosen = withKey.FirstOrDefault(c => !withKey.Any(o => o != c && o.Issuer == c.Subject && o.Subject != c.Subject)) ?? withKey[0];
        foreach (var c in withKey.Where(c => c != chosen)) c.Dispose();
        return chosen;
    }

    /// <summary>The name shown and recorded as the signer.</summary>
    public static string SignerName(X509Certificate2 certificate)
    {
        string name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? certificate.Subject : name;
    }

    /// <summary>"Issued by X, valid until date" for the certificate list.</summary>
    public static string Describe(X509Certificate2 certificate) =>
        $"Issued by {certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true)}, valid until {certificate.NotAfter:d}";
}
