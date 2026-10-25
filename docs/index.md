---
title: PDF Viewer
---

# PDF Viewer Native

A private, script-free PDF viewer and editor for Windows, with its own open-source PDF engine
(pure C# on .NET 9, drawn by Direct2D). It views, searches, annotates, measures, stamps, fills
forms, signs, redacts, edits text and images, tags for accessibility, converts to PDF/A, checks
print output, and exports to Word, Excel and PowerPoint.

- **Private by design:** no telemetry, no account, and documents never leave your computer.
  See the [privacy policy](privacy/).
- **Safe by design:** document JavaScript and launch actions are never run.
- **Open source:** MIT licence. Every release ships CycloneDX and SPDX bills of materials.

## Try it in your browser

**[Open the web demo](demo/).** The same engine runs in the page as WebAssembly. Nothing is
uploaded, and the fonts come from this site only.

To show PDF files in your own web page with this viewer, use the [web embed SDK](sdk/): a script
with an API like the Adobe PDF Embed API, but with no client ID, no server and no telemetry.

## Download

| Where | Status |
| --- | --- |
| [GitHub releases](https://github.com/ramanacr/pdf-viewer/releases/latest) | The installer (`PdfViewerSetup.exe`) or the portable `PdfViewer.exe`. Windows 10 version 2004 or later, x64, with the .NET 9 Desktop Runtime. |
| Microsoft Store | Coming soon |
| Android | Proof of concept: [build it from source](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Android/README.md) |

## For developers

The engine is also a set of NuGet packages (preview):

```shell
dotnet add package PdfEngine.Vector.Skia --prerelease
```

- [Use the engine as a library](developers/): packages and an example.
- [API reference](api/): coming soon.
- [Build from source](building/) and [architecture](architecture/).

## Documentation

- [Features, user guide and keyboard shortcuts](features/)
- [Privacy policy](privacy/)
- [Microsoft Store build](microsoft-store/)
- [Engine design and status](pdf-viewer-vector-migration/00_README.html)

## Code signing

Release binaries are Authenticode-signed, built only by this repository's release workflow from
tagged source. For now, releases are signed with the project's own self-signed certificate,
whose thumbprint is published with each release. See the
[code signing policy](https://github.com/ramanacr/pdf-viewer/blob/main/CODE_SIGNING_POLICY.md).

## Source and contributing

- [Repository](https://github.com/ramanacr/pdf-viewer)
- [Contributing guide](https://github.com/ramanacr/pdf-viewer/blob/main/CONTRIBUTING.md)
- [Security policy](https://github.com/ramanacr/pdf-viewer/blob/main/SECURITY.md)
