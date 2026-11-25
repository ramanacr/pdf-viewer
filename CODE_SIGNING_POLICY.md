# Code signing policy

`PdfViewerSetup.exe` and `PdfViewer.exe` in this project's
[GitHub releases](https://github.com/ramanacr/pdf-viewer/releases) are Authenticode-signed
(SHA-256) and timestamped. Only binaries built from the tagged source in this repository, by
`scripts/release_signed.ps1` (the maintainer's release script) or the fallback release workflow
(`.github/workflows/release.yml`), are signed.
Bundled third-party components (for example the Vortice.Windows assemblies) are included as their
projects publish them and are not signed with this project's certificate.

Other distributions:

- **Microsoft Store package** (live:
  [apps.microsoft.com/detail/9N6QDM774R62](https://apps.microsoft.com/detail/9N6QDM774R62)):
  built by `.github/workflows/msix.yml` or `scripts/build_msix.ps1` from tagged source. Microsoft
  signs the package when it is published through the Store; this project's certificate is not
  used. The Store installs its updates, so the Store package has no GitHub update check. For most
  users the Store is the recommended way to get the Windows app.
- **NuGet packages** (`PdfEngine.Abstractions`, `PdfEngine.Vector`, `PdfEngine.Vector.Skia`):
  built and pushed to nuget.org only by `.github/workflows/packages.yml`, from an `engine-v*` tag
  or a manual run of that workflow, through nuget.org Trusted Publishing (no stored API key).
  They are not signed with this project's certificate.

## Certificate

GitHub release binaries after v4.3.0 are signed with a code signing certificate issued by
**Certum** (Asseco Data Systems S.A.), a public certification authority, under its Open Source Code
Signing programme:

- Subject: `CN=Open Source Developer Ramana Reddy Chamakura, O=Open Source Developer, L=Hyderabad, S=Telangana, C=IN`
- Issuer: `Certum Code Signing 2021 CA`
- Thumbprint (SHA-1): `68572404AE99971D76B907D9452001DCEE7766F1`
- Valid from 2026-11-25 to 2027-11-25. Every signature is timestamped by Certum's timestamp
  service, so signatures stay valid after the certificate expires.

The private key is kept in Certum's cloud (SimplySign) and is never exported. Each signing session
needs the maintainer's sign-in with a one-time token from the SimplySign phone app, so releases are
signed on the maintainer's PC, not in CI. Each release states the thumbprint and attaches the
certificate's public part (`PdfViewer-codesign.cer`). To check a download:

```powershell
Get-AuthenticodeSignature .\PdfViewerSetup.exe | Format-List Status, @{n='Thumbprint';e={$_.SignerCertificate.Thumbprint}}
```

`Status` must be `Valid` and the thumbprint must match. A new certificate builds its SmartScreen
reputation over time, so SmartScreen can still show a warning at first.

v4.3.0 and earlier were signed with the project's own **self-signed** certificate, so Windows shows
their publisher as unknown; each of those releases states its thumbprint. To avoid any warning,
install the app from the [Microsoft Store](https://apps.microsoft.com/detail/9N6QDM774R62), where
Microsoft signs the package.

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
on first start, and switchable in Help > Privacy & Safety; the Microsoft Store package has no
update check, because the Store updates it), downloading an optional component,
and, when signing or validating signatures, contacting the timestamp server or certificate
authorities the user chooses. The full policy is at
https://ramanacr.github.io/pdf-viewer/privacy/ and in the in-app privacy statement.
