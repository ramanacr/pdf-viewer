# Security policy

PDF files come from anywhere, so the viewer treats every document as untrusted input. We take
reports of security problems seriously.

## Supported versions

Security fixes are made to the latest release. Please check that a problem still occurs in the
[latest release](https://github.com/ramanacr/pdf-viewer/releases/latest) before you report it.

## Reporting a vulnerability

Report it privately through GitHub:
[Report a vulnerability](https://github.com/ramanacr/pdf-viewer/security/advisories/new).
Do not open a public issue, and do not attach a document that contains personal or confidential
data.

Please include:

- the version of the viewer and of Windows;
- what happens (for example a crash, a hang, memory growth, or a file written where it should not
  be) and the steps to make it happen;
- a minimal PDF that shows the problem, if you are allowed to share it.

We confirm a report within 7 days, and we tell you when a fix is released. If you want credit in
the release notes, say so in your report.

## What is in scope

- Crashes, hangs or unbounded memory use caused by a document.
- A document that makes the viewer run code, open a file or network connection, or write a file
  without the user asking.
- Redaction that leaves the removed content in the saved file.
- Signature validation that reports a changed or forged document as valid.
- Password-protected or certificate-encrypted content that can be read without the key.
