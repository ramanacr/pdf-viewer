---
title: PDF Viewer
---

# PDF Viewer

A native Windows PDF viewer and editor with its own open-source PDF engine (C# / .NET 9,
drawn by Direct2D). It views, searches, annotates, fills forms, signs, redacts, edits text and
images, converts to PDF/A, and exports to Word, Excel and PowerPoint.

- **Private by design:** no telemetry, no account, documents never leave your computer.
  See the [privacy policy](privacy/).
- **Safe by design:** document JavaScript and launch actions are never run.
- **Open source:** MIT licence. Every release ships CycloneDX and SPDX bills of materials.

## Download

Get the latest installer (`PdfViewerSetup.exe`) or the portable `PdfViewer.exe` from the
[releases page](https://github.com/ramanacr/pdf-viewer/releases).

## Code signing

Release binaries are Authenticode-signed, built only by this repository's release workflow from
tagged source. An application for free code signing by the
[SignPath Foundation](https://signpath.org) has been made; until it is accepted, releases are
signed with the project's own certificate, whose thumbprint is published with each release. See
the [code signing policy](https://github.com/ramanacr/pdf-viewer/blob/main/CODE_SIGNING_POLICY.md).

## Source and documentation

- [Repository](https://github.com/ramanacr/pdf-viewer)
- [README](https://github.com/ramanacr/pdf-viewer#readme)
- [Engine design and status](pdf-viewer-vector-migration/00_README.html)
