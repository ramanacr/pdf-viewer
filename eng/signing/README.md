# Code signing

`PdfViewer.exe` and `PdfViewerSetup.exe` are signed with Authenticode (SHA-256). The app is
signed before it is packed into the installer's payload, so the installed copy carries the
signature too; then the installer itself is signed. `scripts/sign_files.ps1` signs and then
verifies each file: the signature must be intact and made by the expected certificate.

## Today: a self-signed certificate

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

## Moving to a trusted certificate

Nothing in the build has to change except where the key comes from:

- **A PFX from a CA** (OV or EV code signing): set `PDFVIEWER_SIGN_PFX` to its path and
  `PDFVIEWER_SIGN_PASSWORD` to its password (in CI, from repository secrets; never commit either).
- **Timestamping**: set `PDFVIEWER_SIGN_TIMESTAMP_URL` (e.g. the CA's RFC 3161 service), so
  signatures stay valid after the certificate expires. It contacts that server, so it is off
  unless set.
- A hardware token or a cloud signing service (for example Azure Trusted Signing) needs
  `sign_files.ps1` to call signtool with `/sha1 <thumbprint>` or the service's dlib instead of
  `/f`; the verification step stays as it is.

`-NoSign` on `build_publish.ps1` builds unsigned.
