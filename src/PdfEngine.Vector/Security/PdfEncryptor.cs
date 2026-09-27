using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfEngine.Vector.Diagnostics;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Objects;

namespace PdfEngine.Vector.Security;

/// <summary>The cipher a protected copy is written with. RC4 is deliberately not offered.</summary>
public enum PdfEncryptionStrength
{
    /// <summary>AES-256, Standard security handler revision 6 (ISO 32000-2). The default.</summary>
    Aes256,

    /// <summary>AES-128, revision 4 (PDF 1.6): for readers that predate AES-256.</summary>
    Aes128,
}

public enum PdfPrintPermission
{
    None,
    LowResolution,
    HighResolution,
}

/// <summary>What a reader who opens the document with the user password may do (ISO 32000-2 Table 22).</summary>
public sealed record PdfPermissionSet
{
    public PdfPrintPermission Printing { get; init; } = PdfPrintPermission.HighResolution;

    /// <summary>Bit 4: changing the document other than by the operations below.</summary>
    public bool ChangeDocument { get; init; } = true;

    /// <summary>Bit 5: copying or extracting text and graphics.</summary>
    public bool CopyContent { get; init; } = true;

    /// <summary>Bit 10: extracting text and graphics for accessibility.</summary>
    public bool CopyForAccessibility { get; init; } = true;

    /// <summary>Bit 6: adding and changing comments; also allows filling in forms.</summary>
    public bool Comment { get; init; } = true;

    /// <summary>Bit 9: filling in form fields, including signature fields.</summary>
    public bool FillForms { get; init; } = true;

    /// <summary>Bit 11: inserting, rotating and deleting pages, and bookmarks and thumbnails.</summary>
    public bool AssemblePages { get; init; } = true;

    public static PdfPermissionSet All { get; } = new();

    public static PdfPermissionSet None { get; } = new()
    {
        Printing = PdfPrintPermission.None, ChangeDocument = false, CopyContent = false, CopyForAccessibility = false,
        Comment = false, FillForms = false, AssemblePages = false,
    };

    public bool IsUnrestricted => this == All;

    /// <summary>/P for revision 3 and later: bits 1–2 clear, 7–8 and 13–32 set, the rest as chosen.</summary>
    public int ToFlags()
    {
        uint p = 0xFFFFF0C0;
        if (Printing != PdfPrintPermission.None) p |= 1u << 2;
        if (ChangeDocument) p |= 1u << 3;
        if (CopyContent) p |= 1u << 4;
        if (Comment) p |= 1u << 5;
        if (FillForms) p |= 1u << 8;
        if (CopyForAccessibility) p |= 1u << 9;
        if (AssemblePages) p |= 1u << 10;
        if (Printing == PdfPrintPermission.HighResolution) p |= 1u << 11;
        return unchecked((int)p);
    }

    /// <summary>The bits of /P as they are stored (revision 3 and later); bit 6 implying form filling is not folded in.</summary>
    public static PdfPermissionSet FromFlags(int p)
    {
        bool Bit(int n) => (p & (1 << (n - 1))) != 0;
        return new PdfPermissionSet
        {
            Printing = !Bit(3) ? PdfPrintPermission.None : Bit(12) ? PdfPrintPermission.HighResolution : PdfPrintPermission.LowResolution,
            ChangeDocument = Bit(4),
            CopyContent = Bit(5),
            Comment = Bit(6),
            FillForms = Bit(9),
            CopyForAccessibility = Bit(10),
            AssemblePages = Bit(11),
        };
    }
}

/// <summary>How to protect a copy. Passwords are prepared (SASLprep for AES-256) and never stored beyond the write.</summary>
public sealed class PdfEncryptionOptions
{
    /// <summary>The open password; null or empty opens the document without one (permissions still apply).</summary>
    public string? UserPassword { get; init; }

    /// <summary>
    /// The permissions password, which lifts the restrictions and allows changing the security.
    /// When empty the open password doubles as it, which is only allowed with no restrictions.
    /// </summary>
    public string? OwnerPassword { get; init; }

    public PdfPermissionSet Permissions { get; init; } = PdfPermissionSet.All;

    public PdfEncryptionStrength Strength { get; init; } = PdfEncryptionStrength.Aes256;

    /// <summary>False leaves the XMP metadata stream readable (for search engines and indexers).</summary>
    public bool EncryptMetadata { get; init; } = true;

    /// <summary>A rewrite breaks every digital signature; it is refused unless the caller has confirmed that.</summary>
    public bool AllowInvalidatingSignatures { get; init; }

    /// <summary>For a document that is already encrypted: its permissions password, unless it was opened with it.</summary>
    public string? CurrentOwnerPassword { get; init; }
}

/// <summary>A rewritten document and the number of signatures the rewrite invalidated.</summary>
public sealed record PdfSecurityRewriteResult(byte[] Bytes, int SignaturesInvalidated);

/// <summary>
/// Protect with a password and Remove Security: a full rewrite of the document, every object read
/// through the source's security handler (object-stream members as ordinary objects), then
/// encrypted per object with a new Standard security handler (Algorithm 1, AES-256 R6 or AES-128
/// R4) or written in clear. The cross-reference table is written fresh, with a new /ID.
/// Security is only ever changed with the owner password: the author's permissions are not stripped
/// by anyone who merely knows the open password.
/// </summary>
public static class PdfEncryptor
{
    /// <summary>Writes a copy of <paramref name="document"/> protected as <paramref name="options"/> says.</summary>
    /// <exception cref="ArgumentException">The passwords are unusable (both empty, equal, prohibited characters).</exception>
    /// <exception cref="PdfEncryptedDocumentException">The document is encrypted and the owner password is not known.</exception>
    /// <exception cref="InvalidOperationException">The document is signed and <see cref="PdfEncryptionOptions.AllowInvalidatingSignatures"/> is false.</exception>
    public static PdfSecurityRewriteResult Encrypt(PdfVectorDocument document, PdfEncryptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        RequireOwner(document, options.CurrentOwnerPassword, "change its security");

        string user = options.UserPassword ?? string.Empty;
        string owner = options.OwnerPassword ?? string.Empty;
        if (user.Length == 0 && owner.Length == 0)
            throw new ArgumentException("Set an open password, a permissions password, or both.", nameof(options));
        if (owner.Length > 0 && owner == user)
            throw new ArgumentException("The permissions password must differ from the open password.", nameof(options));
        if (owner.Length == 0 && !options.Permissions.IsUnrestricted)
            throw new ArgumentException("Restricting what readers may do needs a permissions password.", nameof(options));
        if (owner.Length == 0)
            owner = user; // Algorithm 3 step a: the owner password defaults to the user password

        int p = options.Permissions.ToFlags();
        byte[] id0 = RandomNumberGenerator.GetBytes(16), id1 = RandomNumberGenerator.GetBytes(16);
        var encrypt = options.Strength == PdfEncryptionStrength.Aes256
            ? Aes256Dictionary(owner, user, p, options.EncryptMetadata)
            : Aes128Dictionary(owner, user, p, id0, options.EncryptMetadata);
        var id = new PdfArray(new PdfObject[] { new PdfString(id0, IsHex: true), new PdfString(id1, IsHex: true) });

        // The handler that encrypts is the one a reader builds: authenticating it proves the entries.
        var handler = PdfStandardSecurityHandler.Create(encrypt, id, o => o);
        if (!handler.Authenticate(user))
            throw new InvalidOperationException("The new encryption dictionary does not authenticate.");

        string version = options.Strength == PdfEncryptionStrength.Aes256 ? "1.7" : "1.6";
        return Rewrite(document, handler, encrypt, id, version, adbeExtensionLevel: options.Strength == PdfEncryptionStrength.Aes256 ? 8 : 0,
            options.AllowInvalidatingSignatures);
    }

    /// <summary>
    /// Writes an unencrypted copy. Allowed when the document was opened with its owner password
    /// (or as a public-key recipient with every permission), or when <paramref name="ownerPassword"/>
    /// proves the owner password now.
    /// </summary>
    /// <exception cref="PdfEncryptedDocumentException">The owner password is not known or is wrong.</exception>
    public static PdfSecurityRewriteResult RemoveSecurity(PdfVectorDocument document, string? ownerPassword = null, bool allowInvalidatingSignatures = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsEncrypted)
            throw new InvalidOperationException("The document is not encrypted.");
        RequireOwner(document, ownerPassword, "remove its security");

        var trailer = document.XrefTable.Trailer!;
        var oldId = document.Resolver.Resolve(trailer["ID"]) as PdfArray;
        PdfObject id0 = oldId is { Count: > 0 } ? oldId[0] : new PdfString(RandomNumberGenerator.GetBytes(16), IsHex: true);
        var id = new PdfArray(new[] { id0, new PdfString(RandomNumberGenerator.GetBytes(16), IsHex: true) });
        return Rewrite(document, null, null, id, "1.4", 0, allowInvalidatingSignatures);
    }

    /// <summary>
    /// True when the security of <paramref name="document"/> may be changed: it is not encrypted, it
    /// was opened with full rights, or <paramref name="ownerPassword"/> is its owner password.
    /// </summary>
    public static bool IsOwnerKnown(PdfVectorDocument document, string? ownerPassword)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Security is not { } security || security.IsOwner)
            return true;
        if (security is not PdfStandardSecurityHandler || string.IsNullOrEmpty(ownerPassword))
            return false;
        var trailer = document.XrefTable.Trailer;
        if (trailer == null || document.Resolver.Resolve(trailer["Encrypt"]) is not PdfDictionary encrypt)
            return false;
        // A second handler, so the open document's key and IsOwner stay as they are.
        var probe = PdfStandardSecurityHandler.Create(encrypt, document.Resolver.Resolve(trailer["ID"]) as PdfArray, document.Resolver.Resolve);
        return probe.AuthenticateOwner(ownerPassword);
    }

    /// <summary>Signature dictionaries (with a /ByteRange) in the document: each is invalidated by a rewrite.</summary>
    public static int CountSignatures(PdfVectorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        int count = 0;
        foreach (var (number, entry) in document.XrefTable.Entries)
            if (entry.IsInUse && number != 0 && document.Resolver.Resolve(number) is PdfDictionary d && IsSignature(d))
                count++;
        return count;
    }

    private static bool IsSignature(PdfDictionary d) => d["ByteRange"] != null && PdfSecurityHandler.IsSignatureDictionary(d);

    private static void RequireOwner(PdfVectorDocument document, string? ownerPassword, string action)
    {
        if (IsOwnerKnown(document, ownerPassword))
            return;
        bool standard = document.Security is PdfStandardSecurityHandler;
        throw new PdfEncryptedDocumentException(
            !standard ? $"This document is encrypted for specific recipients without full rights, so you cannot {action}."
            : string.IsNullOrEmpty(ownerPassword) ? $"The document's permissions password is needed to {action}."
            : "The permissions password is incorrect.",
            passwordRequired: standard);
    }

    // ------------------------------------------------------------------ encryption dictionaries

    private static PdfDictionary Aes256Dictionary(string owner, string user, int p, bool encryptMetadata)
    {
        byte[] ownerBytes = PdfPasswordPreparation.PrepareForEncryption(owner);
        byte[] userBytes = PdfPasswordPreparation.PrepareForEncryption(user);
        try
        {
            var (o, u, oe, ue, perms) = PdfStandardSecurityHandler.CreateR6Entries(ownerBytes, userBytes, p, encryptMetadata);
            return new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Filter"] = new PdfName("Standard"),
                ["V"] = new PdfInteger(5),
                ["R"] = new PdfInteger(6),
                ["Length"] = new PdfInteger(256),
                ["CF"] = CryptFilters("AESV3", 32),
                ["StmF"] = new PdfName("StdCF"),
                ["StrF"] = new PdfName("StdCF"),
                ["O"] = new PdfString(o, IsHex: true),
                ["U"] = new PdfString(u, IsHex: true),
                ["OE"] = new PdfString(oe, IsHex: true),
                ["UE"] = new PdfString(ue, IsHex: true),
                ["P"] = new PdfInteger(p),
                ["Perms"] = new PdfString(perms, IsHex: true),
                ["EncryptMetadata"] = new PdfBoolean(encryptMetadata),
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownerBytes);
            CryptographicOperations.ZeroMemory(userBytes);
        }
    }

    private static PdfDictionary Aes128Dictionary(string owner, string user, int p, byte[] id0, bool encryptMetadata)
    {
        if (!PdfStandardSecurityHandler.IsLatin1Password(owner) || !PdfStandardSecurityHandler.IsLatin1Password(user))
            throw new ArgumentException("AES-128 passwords are limited to Latin letters, digits and symbols. Choose AES-256 for other characters.");
        byte[] ownerBytes = PdfStandardSecurityHandler.Latin1PasswordBytes(owner);
        byte[] userBytes = PdfStandardSecurityHandler.Latin1PasswordBytes(user);
        try
        {
            var (o, u) = PdfStandardSecurityHandler.CreateR4Entries(ownerBytes, userBytes, p, id0, encryptMetadata);
            return new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Filter"] = new PdfName("Standard"),
                ["V"] = new PdfInteger(4),
                ["R"] = new PdfInteger(4),
                ["Length"] = new PdfInteger(128),
                ["CF"] = CryptFilters("AESV2", 16),
                ["StmF"] = new PdfName("StdCF"),
                ["StrF"] = new PdfName("StdCF"),
                ["O"] = new PdfString(o, IsHex: true),
                ["U"] = new PdfString(u, IsHex: true),
                ["P"] = new PdfInteger(p),
                ["EncryptMetadata"] = new PdfBoolean(encryptMetadata),
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownerBytes);
            CryptographicOperations.ZeroMemory(userBytes);
        }
    }

    private static PdfDictionary CryptFilters(string method, int keyBytes) => new(new Dictionary<string, PdfObject>
    {
        ["StdCF"] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("CryptFilter"),
            ["AuthEvent"] = new PdfName("DocOpen"),
            ["CFM"] = new PdfName(method),
            ["Length"] = new PdfInteger(keyBytes),
        }),
    });

    // ------------------------------------------------------------------ the rewrite

    private static PdfSecurityRewriteResult Rewrite(PdfVectorDocument document, PdfSecurityHandler? handler, PdfDictionary? encrypt,
        PdfArray id, string minimumVersion, int adbeExtensionLevel, bool allowInvalidatingSignatures)
    {
        var xref = document.XrefTable;
        var resolver = document.Resolver;
        var trailer = xref.Trailer ?? throw new InvalidOperationException("The document has no trailer.");
        int oldEncrypt = trailer["Encrypt"] is PdfIndirectRef er ? er.ObjectNumber : -1;
        int root = trailer["Root"] is PdfIndirectRef rr ? rr.ObjectNumber : -1;

        int signatures = CountSignatures(document);
        if (signatures > 0 && !allowInvalidatingSignatures)
            throw new InvalidOperationException(
                $"The document has {signatures} digital signature(s). Rewriting it invalidates them; confirm before changing its security.");

        string version = MaxVersion(document.Metadata.PdfVersion, minimumVersion);
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W($"%PDF-{version}\n%âãÏÓ\n");

        int maxNumber = xref.Entries.Keys.DefaultIfEmpty(0).Max();
        var offsets = new Dictionary<int, (long Offset, int Generation)>();
        foreach (var (number, entry) in xref.Entries.OrderBy(e => e.Key))
        {
            if (!entry.IsInUse || number == oldEncrypt || number == 0)
                continue;
            var obj = resolver.Resolve(number);
            if (obj == null)
                continue;
            if (obj is PdfStream st)
            {
                if (st.Dictionary.GetName("Type") is "XRef" or "ObjStm")
                    continue; // replaced by the classic table and by writing members individually
                // Whatever /Crypt said applied to the old encryption; the new one uses its default filter.
                obj = st with { Dictionary = PdfObjectWriter.WithoutCryptFilter(st.Dictionary, decrypted: true) };
            }
            if (number == root && adbeExtensionLevel > 0 && version == "1.7" && obj is PdfDictionary catalog)
                obj = WithAdbeExtension(catalog, resolver, adbeExtensionLevel);
            int generation = entry.IsCompressed ? 0 : entry.GenerationNumber;
            if (handler != null)
                obj = handler.EncryptObject(obj, number, generation);
            offsets[number] = (ms.Position, generation);
            W($"{number} {generation} obj\n");
            PdfObjectWriter.Write(ms, obj);
            W("\nendobj\n");
        }

        int size = maxNumber + 1;
        if (encrypt != null)
        {
            int number = size++;
            offsets[number] = (ms.Position, 0);
            W($"{number} 0 obj\n");
            PdfObjectWriter.Write(ms, encrypt); // the encryption dictionary itself is never encrypted
            W("\nendobj\n");
        }

        long xrefPos = ms.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int n = 1; n < size; n++)
        {
            W(offsets.TryGetValue(n, out var o)
                ? $"{o.Offset.ToString("D10", CultureInfo.InvariantCulture)} {o.Generation:D5} n \n"
                : "0000000000 00000 f \n");
        }
        var trailerEntries = new Dictionary<string, PdfObject> { ["Size"] = new PdfInteger(size) };
        foreach (var key in new[] { "Root", "Info" })
            if (trailer[key] is PdfIndirectRef v) trailerEntries[key] = v;
        if (encrypt != null) trailerEntries["Encrypt"] = new PdfIndirectRef(size - 1);
        trailerEntries["ID"] = id; // never encrypted (7.6.1)
        W("trailer\n");
        PdfObjectWriter.Write(ms, new PdfDictionary(trailerEntries));
        W($"\nstartxref\n{xrefPos}\n%%EOF\n");
        return new PdfSecurityRewriteResult(ms.ToArray(), signatures);
    }

    /// <summary>AES-256 in a PDF 1.7 file is Adobe extension level 8 (the header stays 1.7 for older readers).</summary>
    private static PdfDictionary WithAdbeExtension(PdfDictionary catalog, Parsing.PdfObjectResolver resolver, int level)
    {
        var extensions = resolver.Resolve(catalog["Extensions"]) is PdfDictionary existing
            ? new Dictionary<string, PdfObject>(existing.Entries)
            : new Dictionary<string, PdfObject>();
        if (resolver.Resolve(extensions.GetValueOrDefault("ADBE")) is PdfDictionary adbe && (adbe.GetInteger("ExtensionLevel") ?? 0) >= level)
            return catalog;
        extensions["ADBE"] = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["BaseVersion"] = new PdfName("1.7"),
            ["ExtensionLevel"] = new PdfInteger(level),
        });
        return new PdfDictionary(new Dictionary<string, PdfObject>(catalog.Entries) { ["Extensions"] = new PdfDictionary(extensions) });
    }

    private static string MaxVersion(string? source, string minimum)
    {
        static double Parse(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
        double s = Parse(source), m = Parse(minimum);
        return s > m && s <= 2.0 ? s.ToString("0.0", CultureInfo.InvariantCulture) : minimum;
    }
}
