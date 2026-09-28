# Code signing policy

`PdfViewerSetup.exe` and `PdfViewer.exe` in this project's
[GitHub releases](https://github.com/ramanacr/pdf-viewer/releases) are Authenticode-signed
(SHA-256). Only binaries built by this repository's release workflow
(`.github/workflows/release.yml`), from the tagged source in this repository, are signed.
Bundled third-party components (for example PDFium's `pdfium.dll`) are included as their
projects publish them and are not signed with this project's certificate.

## Certificate

Releases are signed today with the project's own **self-signed** release certificate, so Windows
shows the publisher as unknown. Each release states the certificate's thumbprint and attaches its
public part (`PdfViewer-codesign.cer`) so a download can be checked:

```powershell
(Get-AuthenticodeSignature .\PdfViewerSetup.exe).SignerCertificate.Thumbprint
```

An application for free code signing by SignPath Foundation has been made. Once it is accepted,
this section will read: *Free code signing provided by [SignPath.io](https://signpath.io),
certificate by [SignPath Foundation](https://signpath.org).*

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
authorities the user chooses. See the in-app privacy statement.
