---
title: Getting started
description: Get PDF Viewer Native for Windows, try it in your browser, or use its engine in your own app.
permalink: /getting-started/
---

# Getting started

PDF Viewer Native is a private, script-free PDF viewer and editor for Windows, with its own
open-source PDF engine. This page tells you where to get each part.

## Get the Windows app

| Where | How |
| --- | --- |
| [Microsoft Store]({{ site.links.store }}) | Install it from the Store. The Store updates it. |
| [GitHub releases]({{ site.links.releases }}) | `PdfViewerSetup.exe` (installer) or `PdfViewer.exe` (portable). Windows 10 version 2004 or later, or Windows 11, x64. Needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). |

Release binaries are Authenticode-signed with the project's own self-signed certificate, so
Windows can show "unknown publisher". Each release states the certificate thumbprint. See the
[code signing policy]({{ site.links.repo }}/blob/main/CODE_SIGNING_POLICY.md).

Then read the [features and user guide](features.md).

## Try it in your browser

[Open the web demo]({{ '/demo/' | relative_url }}). The same engine runs in the page as WebAssembly. Nothing is
uploaded.

## Show PDF files in your web page

Use the [web embed SDK](sdk/index.md): a script with an API like the Adobe PDF Embed API, but with no
client ID, no server and no telemetry.

```bash
npm install pdfviewer-embed
```

See the [examples]({{ '/sdk/examples/' | relative_url }}).

## Use the engine in a .NET app

The engine is also a set of NuGet packages (preview):

```shell
dotnet add package PdfEngine.Vector.Skia --prerelease
```

Read [Use the engine as a library](developers.md) and the [API reference]({{ '/api/' | relative_url }}).

## Android

The Android app is a proof of concept. Build it from source, or take the APK artifact of the
`android.yml` workflow. See [its README]({{ site.links.repo }}/blob/main/src/PdfViewer.Android/README.md).

## Build it yourself

See [Build from source](building.md) and [Architecture](architecture.md).
