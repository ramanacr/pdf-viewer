# Code signing

`PdfViewer.exe` and `PdfViewerSetup.exe` are signed with Authenticode (SHA-256). The app is
signed before it is packed into the installer's payload, so the installed copy carries the
signature too; then the installer itself is signed. `scripts/sign_files.ps1` signs and then
verifies each file: the signature must be intact and made by the expected certificate.

## Development builds and CI: a self-signed certificate

```powershell
./scripts/new_signing_certificate.ps1        # once per machine
./scripts/build_publish.ps1                   # signs with it automatically
```

- The certificate is created with .NET's `CertificateRequest` (RSA 3072, code-signing EKU,
  three years) and stored as `%LOCALAPPDATA%\PdfViewer\signing\codesign.pfx`, with its public
  part in `codesign.cer`. It is **not** installed into any Windows certificate store and nothing
  is trusted.
- The PFX password sits beside it in `codesign.password`, encrypted with DPAPI for the current
  Windows user, so only that account on that machine can sign with it. Nothing goes into the
  repository.
- Windows shows signed-but-self-signed files as from an **unknown publisher**, and SmartScreen
  still warns. `Get-AuthenticodeSignature` reports `UnknownError` (untrusted root) with the
  signer's subject; that is expected. The signature still proves the files were not modified
  after signing and gives every build the same publisher identity.
- Do not tell users to trust the self-signed certificate: installing it as a root would let
  whoever has the key sign anything their machine then trusts.

CI creates a throwaway certificate on every run (`Create a throwaway signing certificate` in
`.github/workflows/ci.yml`), so the signing and verification path is tested on every build.

## GitHub releases: the Certum certificate

Releases after v4.3.0 are signed with the project's Certum code signing certificate
(`CN=Open Source Developer Ramana Reddy Chamakura`, thumbprint
`68572404AE99971D76B907D9452001DCEE7766F1`, valid to 2027-11-25; see `CODE_SIGNING_POLICY.md`).
Its key stays in Certum's cloud (SimplySign) and is never exported, so CI cannot sign with it:
each signing session needs the maintainer's sign-in with a one-time token from the SimplySign phone
app. Releases are therefore built and signed on the maintainer's PC.

One-time setup on the PC:

1. Install SimplySign Desktop (Certum's "Software and libraries" page) and the SimplySign phone
   app, and activate the phone app with Certum's activation e-mail.
2. Install the Windows SDK signing tools (`signtool.exe`) and the GitHub CLI (`gh auth login`).

For each release:

```powershell
git tag v4.4.0; git push origin v4.4.0     # the tag starts the Store package (msix.yml), not a release
git checkout v4.4.0
# Connect SimplySign Desktop: tray icon > Connect, e-mail + token from the phone app.
./scripts/release_signed.ps1 -Tag v4.4.0
```

`release_signed.ps1` checks that HEAD is the pushed tag with no local changes, that the certificate
is available, then runs `build_publish.ps1` with `PDFVIEWER_SIGN_THUMBPRINT`. `sign_files.ps1` then
signs with `signtool sign /sha1 <thumbprint> /s My /fd SHA256 /tr http://time.certum.pl /td SHA256`.
With a store certificate every signature is **timestamped** (Certum's RFC 3161 service unless
`PDFVIEWER_SIGN_TIMESTAMP_URL` names another), so it stays valid after the certificate expires; a
signature without a timestamp, or one that Windows does not report as `Valid`, stops the build.
The script checks both executables again, attaches `PdfViewer-codesign.cer` (the public part), and
creates the release as a **draft**. It never changes a published release. Check the draft on
GitHub, then publish it there.

Nothing here handles the SimplySign password or token: SimplySign Desktop asks for them itself.

## Fallback: the self-signed release workflow

`.github/workflows/release.yml` no longer runs on tags. It can still be started by hand (Actions >
Release > Run workflow) for an existing tag; it signs with the old self-signed release certificate,
which lives only in two repository secrets, `PDFVIEWER_RELEASE_PFX` (base64 of the PFX) and
`PDFVIEWER_RELEASE_PFX_PASSWORD`, and publishes the release. Without them it stops: a release is
never published unsigned. `./scripts/set_release_signing_secret.ps1` created those secrets; it keeps
the certificate in `%LOCALAPPDATA%\PdfViewer\release-signing` with its password DPAPI-encrypted for
your Windows user. Use this only when the Certum certificate cannot be used.

## Other certificates

- **A PFX from a CA**: set `PDFVIEWER_SIGN_PFX` to its path and `PDFVIEWER_SIGN_PASSWORD` to its
  password (never commit either). Set `PDFVIEWER_SIGN_TIMESTAMP_URL` to timestamp; with a PFX it is
  off unless set, because it contacts that server.
- **Any certificate in the Windows store** (a hardware token, another cloud signing service with a
  smart-card driver): set `PDFVIEWER_SIGN_THUMBPRINT`, or pass `-Thumbprint` to `sign_files.ps1`.

`-NoSign` on `build_publish.ps1` builds unsigned.
