using System.Security.Cryptography;

namespace PdfViewer.Web.Engine;

/// <summary>One cryptographic primitive the engine uses, and whether this runtime provides it.</summary>
/// <param name="Failure">The exception type when the runtime lacks it (for example PlatformNotSupportedException).</param>
/// <param name="EngineFallback">True when the engine has a managed implementation of its own for a runtime that lacks it.</param>
public sealed record CryptoPrimitive(string Name, string UsedFor, bool Available, string? Failure, bool EngineFallback);

/// <summary>
/// Which of the primitives that PDF decryption needs this runtime provides, found by trying each
/// once. Browser WebAssembly builds of .NET 9 lack some of them; the engine has managed MD5 and AES
/// for that case (PdfEngine.Vector's PortableCrypto), but no RSA, so certificate-encrypted
/// documents cannot be opened there.
/// </summary>
public static class PlatformCrypto
{
    private static readonly Lazy<IReadOnlyList<CryptoPrimitive>> Probed = new(Probe);

    public static IReadOnlyList<CryptoPrimitive> Primitives => Probed.Value;

    /// <summary>True when password-encrypted (Standard security handler) documents can be opened: every primitive is there or has a fallback.</summary>
    public static bool CanDecrypt => Primitives.Where(p => p.Name != "RSA").All(p => p.Available || p.EngineFallback);

    private static IReadOnlyList<CryptoPrimitive> Probe()
    {
        byte[] data = new byte[32];
        return
        [
            Try("MD5", "RC4 and AES-128 keys (PDF security revisions 2 to 4)", fallback: true, () => MD5.HashData(data)),
            Try("SHA-256", "AES-256 keys (revisions 5 and 6), font and colour-profile caches", fallback: false, () => SHA256.HashData(data)),
            Try("AES", "decrypting AES-128 and AES-256 documents", fallback: true, () =>
            {
                using var aes = Aes.Create();
                aes.Key = new byte[16];
                aes.DecryptCbc(new byte[16], new byte[16], PaddingMode.None);
            }),
            Try("RSA", "certificate (public-key) encryption and signatures", fallback: false, () =>
            {
                using var rsa = RSA.Create(1024);
            }),
        ];
    }

    private static CryptoPrimitive Try(string name, string usedFor, bool fallback, Action probe)
    {
        try
        {
            probe();
            return new CryptoPrimitive(name, usedFor, true, null, fallback);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException or NotSupportedException or TypeInitializationException)
        {
            return new CryptoPrimitive(name, usedFor, false, ex.GetType().Name, fallback);
        }
    }
}
