# Security and limits

The engine is written for untrusted input. It parses PDF data in managed code only, and it stops
work that grows beyond fixed ceilings.

## Hostile input: `PdfSecurityLimits`

[`PdfSecurityLimits`](xref:PdfEngine.Vector.Limits.PdfSecurityLimits) holds the ceilings. Every
`PdfVectorDocument.Open*` method takes an optional instance; null uses
`PdfSecurityLimits.Default`.

| Limit | Default | Protects against |
| --- | --- | --- |
| `MaxDecodedStreamBytes` | 128 MB per stream | Decompression bombs |
| `MaxDecompressionRatio` | 500 | Decompression bombs |
| `MaxObjectsCount` | 500,000 | Oversized object tables |
| `MaxNestingDepth`, `MaxPageTreeDepth` | 64 | Deep recursion |
| `MaxFormXObjectDepth` | 40 | Recursive forms |
| `MaxOperatorsPerPage` | 5,000,000 | Content that never ends |
| `MaxCommandsPerPage` | 500,000 | Huge display lists |
| `MaxImagePixels` | 100,000,000 | Huge images |
| `MaxPageBuildTime` | 20 seconds | Slow pages |

The type has more limits; see its reference page. To change some limits, create an instance:

```csharp
var limits = new PdfSecurityLimits { MaxPageBuildTime = TimeSpan.FromSeconds(5), MaxImagePixels = 50_000_000 };
using var doc = await PdfVectorDocument.OpenAsync("input.pdf", limits);
```

## Exceptions

The vector engine raises typed exceptions. Decide what to do from the type and its
[`Kind`](xref:PdfEngine.Vector.Diagnostics.PdfVectorErrorKind), not from the message text.

| Exception | When |
| --- | --- |
| [`PdfSyntaxException`](xref:PdfEngine.Vector.Diagnostics.PdfSyntaxException) | The data is not a PDF, or repair failed. |
| [`PdfEncryptedDocumentException`](xref:PdfEngine.Vector.Diagnostics.PdfEncryptedDocumentException) | The document cannot be decrypted. |
| [`PdfResourceLimitException`](xref:PdfEngine.Vector.Diagnostics.PdfResourceLimitException) | A limit was hit. `LimitName` names it, for example `MaxImagePixels`. |
| [`PdfUnsupportedFeatureException`](xref:PdfEngine.Vector.Diagnostics.PdfUnsupportedFeatureException) | A construct is not supported. `Reason` gives a stable code. |

All four derive from [`PdfVectorException`](xref:PdfEngine.Vector.Diagnostics.PdfVectorException).

A limit hit while a page is interpreted does not throw. The page keeps what was drawn before the
limit, as PDFium does, and its display list has `PdfFeatureSet.Recovered` in `Features`. Limits
hit while the document opens (for example `MaxObjectsCount` or `MaxPageTreeDepth`) throw from
`OpenAsync`.

## Render size

The Skia renderer allocates one buffer of `width x height x 4` bytes for each render. It has no
ceiling of its own. With input from users, cap the resolution, or render regions
(`SkiaVectorRenderer.RenderAsync` with a `SkiaPixelRegion`). Use `SkiaVectorRenderer.OutputSize`
to check the size before you render.

The Windows application's Direct2D backend (not a package) refuses a render above
`MaxRenderPixelCount` (`int.MaxValue / 4` pixels) with a `PdfResourceLimitException`. Use the same
rule in your host if you need a fixed ceiling.

## Encryption

The engine opens encrypted documents:

- Standard security handler: revisions 2 to 4 (RC4 40 to 128 bit, AES-128) and 5 to 6 (AES-256).
  Give the user or the owner password:

  ```csharp
  try
  {
      using var doc = await PdfVectorDocument.OpenAsync("secret.pdf", password: password);
  }
  catch (PdfEncryptedDocumentException ex) when (ex.PasswordRequired)
  {
      // Ask for the password again.
  }
  ```

- Public-key security handler: pass `certificates` (with private keys). Null tries the personal
  certificate stores. `CertificateRequired` is true when none of them is a recipient.

`PdfVectorDocument.IsEncrypted` and `PdfVectorDocument.Security` report the encryption of an open
document. `Security.Permissions` holds the permission bits of the file (`/P`, ISO 32000-2 Table 22),
and `Security.IsOwner` is true when the owner password opened it. The engine reports permissions;
your application decides whether to enforce them.

## Report a vulnerability

See the [security policy](https://github.com/ramanacr/pdf-viewer/security/policy).
