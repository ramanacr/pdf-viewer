# Code signing policy

`PdfViewerSetup.exe` and `PdfViewer.exe` in this project's
[GitHub releases](https://github.com/ramanacr/pdf-viewer/releases) are Authenticode-signed
(SHA-256). Only binaries built by this repository's release workflow
(`.github/workflows/release.yml`), from the tagged source in this repository, are signed.
Bundled third-party components (for example the Vortice.Windows assemblies) are included as their
projects publish them and are not signed with this project's certificate.

Other distributions:

- **Microsoft Store package** (not live yet): built by `.github/workflows/msix.yml` or
  `scripts/build_msix.ps1` from tagged source. The Microsoft Store signs it when it is submitted;
  this project's certificate is not used.
- **NuGet packages** (`PdfEngine.Abstractions`, `PdfEngine.Vector`, `PdfEngine.Vector.Skia`):
  built and pushed to nuget.org only by `.github/workflows/packages.yml`, from an `engine-v*` tag
  or a manual run of that workflow, through nuget.org Trusted Publishing (no stored API key).
  They are not signed with this project's certificate.

## Certificate

Releases are signed today with the project's own **self-signed** release certificate, so Windows
shows the publisher as unknown. Each release states the certificate's thumbprint and attaches its
public part (`PdfViewer-codesign.cer`) so a download can be checked:

```powershell
(Get-AuthenticodeSignature .\PdfViewerSetup.exe).SignerCertificate.Thumbprint
```

The certificate is self-signed, so Windows reports the publisher as unknown and SmartScreen can
show a warning. A certificate from a public certification authority will replace it later; this
policy will name that authority when it does.

## Team roles

- Committers and reviewers: [@ramanacr](https://github.com/ramanacr) (repository owner). Changes
  proposed by anyone else are reviewed before they are merged.
- Approvers (who approve each release for signing): [@ramanacr](https://github.com/ramanacr).

Everyone in these roles uses multi-factor authentication for GitHub and for the signing service.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

In detail: it has no analytics, telemetry or account, and never uploads documents. It makes a
network request only when the user asks for one: the update check (off until the user allows it
on first start, and switchable in Help > Privacy & Safety), downloading an optional component,
and, when signing or validating signatures, contacting the timestamp server or certificate
authorities the user chooses. The full policy is at
https://ramanacr.github.io/pdf-viewer/privacy/ and in the in-app privacy statement.
