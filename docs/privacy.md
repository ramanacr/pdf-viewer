---
title: Privacy Policy
permalink: /privacy/
---

# Privacy Policy

*PDF Viewer — last updated 25 October 2026*

PDF Viewer is a desktop application for Windows. It does not collect, store or transmit
personal data, and it does not transfer any information to other networked systems unless the
person using, installing or operating it asks for that. The same applies to the web demo and the
Android app (see [other versions](#other-versions)).

## What the application does not do

- It has **no telemetry, analytics or crash reporting**. Nothing about you, your computer or your
  documents is recorded or sent anywhere.
- It has **no account and no sign-in**, and no cloud storage.
- It **never uploads documents**. Files are read from and written to local paths you choose.
- It **never runs scripts** contained in documents (JavaScript, launch actions), and it never
  opens embedded files on its own.

## The only network requests, and when they happen

The application connects to the network only in these cases, each started by you:

1. **Update check.** Off until you allow it when the application first starts; you can switch
   it on or off at any time under **Help → Privacy & Safety**. It asks this project's public
   GitHub releases page whether a newer version exists. It sends nothing about you or your
   documents; like any web request, GitHub receives your IP address.
2. **Optional components.** When you ask to download an optional component (for example Read
   Aloud), it is fetched from this project's GitHub release assets and checked against a
   SHA-256 hash built into the application. It is never downloaded without your request.
3. **Signing and signature validation.** When you sign a document with a timestamp, or check
   signatures online, the application contacts the timestamp server or certificate authorities
   you choose. It sends only a fingerprint (hash) of the signature or the identifiers of the
   certificates being checked, never the document.

If you decline the update check and never use the options above, the application never opens a
network connection.

## What is stored on your computer

The application keeps its settings on your computer only, in your Windows user profile
(`%LOCALAPPDATA%\PdfViewerNative`): your privacy choice for the update check, the list of
recently opened files, your signing preferences (such as the timestamp server and how a
signature looks), and, only if you tick the option, your signature picture. It also keeps the
name that dynamic stamps show, your custom stamps, and the measuring scale you set for a
document. Nothing of this leaves your computer, and deleting that folder removes it.

## Other versions

- **Microsoft Store version.** It has no update check: the Microsoft Store updates it.
  Everything else on this page applies. The Store itself is a Microsoft service.
- **Web demo** ([ramanacr.github.io/pdf-viewer/demo/](https://ramanacr.github.io/pdf-viewer/demo/)).
  A PDF you open stays in the browser tab and is never uploaded. The page has no analytics, no
  cookies and no third-party requests: the app and its fonts come from the demo's own site. The
  browser's local storage keeps only your theme and side-panel choices. The site is hosted on
  GitHub Pages, which receives your IP address like any web server.
- **Android app** (proof of concept). It asks for no permission, including no internet access.
  It opens only the file you choose in the system document picker.

## Code signing

Release binaries are code-signed. How releases are built, signed and approved is described in
the [code signing policy](https://github.com/ramanacr/pdf-viewer/blob/main/CODE_SIGNING_POLICY.md).

## Contact

Questions about this policy can be raised as an issue in the
[project repository](https://github.com/ramanacr/pdf-viewer/issues).
