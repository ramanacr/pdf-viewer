---
title: Build from source
permalink: /building/
---

# Build from source

This page tells you how to build, run, test and package the project. To take part, also read
[CONTRIBUTING.md](https://github.com/ramanacr/pdf-viewer/blob/main/CONTRIBUTING.md).

## Prerequisites

| What | Version | Needed for |
| --- | --- | --- |
| Windows | Windows 10 version 2004 (build 19041) or later, or Windows 11, x64 | The Windows app and its tests |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/9.0) | 9.0 | Everything |
| PowerShell | 7 (`pwsh`) | `scripts/build_publish.ps1` and `scripts/build_msix.ps1` |
| Visual Studio 2022 | 17.12 or later (optional) | Opening `PdfViewer.slnx` |
| `wasm-tools` workload | .NET 9 | The web demo only |
| `android` workload, Android SDK 35, JDK 17 | .NET 9 | The Android app only |
| Windows SDK | any recent | The Microsoft Store package only |

The solution file is `PdfViewer.slnx` (the XML solution format).

## Build and run

```powershell
git clone https://github.com/ramanacr/pdf-viewer.git
cd pdf-viewer
dotnet build PdfViewer.slnx -c Release
dotnet run --project src/PdfViewer/PdfViewer.csproj
dotnet run --project src/PdfViewer/PdfViewer.csproj -- "samples/SampleDocument.pdf"
```

A developer build also contains PDFium as the reference renderer (see [PDFium](#pdfium-in-developer-and-test-builds)).
The published app does not.

## Run the tests

```powershell
dotnet test tests/PdfEngine.Vector.Tests                        # engine: unit, fixture, PDFium-differential and fuzz tests
dotnet test tests/PdfEngine.Vector.Skia.Tests                   # Skia backend against Direct2D; the web and Android engine layer
dotnet test tests/PdfViewer.Tests                               # the viewer, with the PDFium oracle tests
dotnet test tests/PdfViewer.Tests -p:PdfiumRenderer=false       # the viewer as published: no PDFium
```

More fuzzing, the benchmark and the corpus report:

```powershell
$env:VECTOR_FUZZ_ITERATIONS=100000; dotnet test tests/PdfEngine.Vector.Tests --filter Fuzz
dotnet run --project eng/vectorpdf/tools/VectorPdf.Tool -c Release -- bench --pages 20
dotnet run --project eng/vectorpdf/tools/VectorPdf.Tool -c Release -- corpus <folder> --out report.jsonl --diff
```

The corpus runner reads files locally. It writes one JSON line per file (SHA-256, the result of
both engines, fallback reasons, pixel difference). No document content leaves the machine. See
[eng/vectorpdf/corpus/README.md](https://github.com/ramanacr/pdf-viewer/blob/main/eng/vectorpdf/corpus/README.md).

## Package the Windows app

Use PowerShell 7. The script stops under Windows PowerShell 5.1.

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\build_publish.ps1
```

The script writes these files to `publish\`:

| File | What it is |
| --- | --- |
| `PdfViewerSetup.exe` | The installer: Start menu and optional desktop shortcut, an uninstaller in Add or Remove Programs, and the optional components as tick boxes |
| `PdfViewer.exe` | The portable app: one x64 file. It needs the .NET 9 Desktop Runtime. |
| `System.Speech.dll` | The Read Aloud optional component |
| `sbom.cyclonedx.json`, `sbom.spdx.json` | The software bill of materials |
| `THIRD_PARTY_NOTICES.md` | The licences of what ships |
| `SampleDocument.pdf` | A sample document |

The script builds with `-p:PdfiumRenderer=false`, so the payload has no PDFium. It signs the app
and the installer when a signing certificate is present (see
[eng/signing/README.md](https://github.com/ramanacr/pdf-viewer/blob/main/eng/signing/README.md));
`-NoSign` builds unsigned. `-DryRun` publishes and checks the payload without an installer, as CI
does. The release gates (payload size, cold start, PDFium-free) are in
[eng/releasegate/README.md](https://github.com/ramanacr/pdf-viewer/blob/main/eng/releasegate/README.md).

## Package for the Microsoft Store

`scripts/build_msix.ps1` makes an MSIX package from the same app. It is self-contained and has
Read Aloud inside it. The Store signs and updates the package, so this version has no update
check, and its package manifest declares the .pdf association. The Store identity is in
`eng/msix/identity.json`. The Store listing is live:
[apps.microsoft.com/detail/9N6QDM774R62](https://apps.microsoft.com/detail/9N6QDM774R62).

```powershell
pwsh -File .\scripts\build_msix.ps1                # with the identity in eng/msix/identity.json
pwsh -File .\scripts\build_msix.ps1 -TestIdentity  # a local test package that cannot be submitted
```

The owner's steps and all differences from the installer build are in
[Microsoft Store build](microsoft-store.md).

## Build the web demo and the Android app

- **Web demo:** `dotnet workload install wasm-tools`, then
  `dotnet run --project src/PdfViewer.Web`. To add the Noto fonts and serve the published site,
  see [src/PdfViewer.Web/README.md](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Web/README.md).
- **Android:** `dotnet workload install android`, then
  `dotnet build src/PdfViewer.Android -f net9.0-android -c Release`. See
  [src/PdfViewer.Android/README.md](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Android/README.md).

## Pack the engine libraries

```powershell
dotnet pack src/PdfEngine.Abstractions/PdfEngine.Abstractions.csproj -o packages
dotnet pack src/PdfEngine.Vector/PdfEngine.Vector.csproj -o packages
dotnet pack src/PdfEngine.Vector.Skia/PdfEngine.Vector.Skia.csproj -o packages
```

A local pack has the version `0.0.0-dev`. With the .NET 9 SDK it has only `net9.0`; to get
`net10.0` as well, as the released packages have, pack with the .NET 10 SDK. See
[Use the engine as a library](developers.md).

## Engine modes

Select the engine mode under *View → Rendering Engine*, or with the `PDF_ENGINE_MODE`
environment variable.

| Mode | What it does |
| --- | --- |
| `Auto` / `Hybrid` | The default. The vector engine draws every page, tile, print and export. Damaged or unsupported content is drawn by best effort, as PDFium draws it. |
| `Vector` | Strict mode for development and CI. Content that the engine classifies as unsupported is outlined in orange, and a page that fails is an error, not a blank page. |
| `Pdfium` | PDFium draws every page. Only in developer builds (`PdfiumRenderer=true`). The published app treats it as `Auto`. |

The status bar badge shows how the current page was drawn (`⚡ Vector`,
`⚡ Vector (N approximated)`, `⚡ Vector (N unsupported)` in strict mode, or `⚠ Unreadable page`).
Its tooltip lists the reasons.

Other switches:

- `PDF_GPU_PRESENT=0` turns off the shared GPU texture for zoomed detail tiles.
- `PDF_VECTOR_HOST=wpf` selects the earlier WPF drawing host.
- `vectorpdf live <file.pdf>` opens a window and measures frame times.

## PDFium in developer and test builds

The published app has no PDFium. Developer and test builds (`dotnet build`, `dotnet test`, the
default `PdfiumRenderer=true`) keep a pinned release of Google PDFium as the oracle that the
engine is tested against.

- **Release:** `chromium/8021` (`154.0.8021.0`) from `bblanchon/pdfium-binaries`, Windows x64,
  without V8 and XFA.
- **SHA-256:** `ADAC8CE034015427B5DAA81F8EEDDFCC8E84BC2A9F036F007890FF18BD4388C4`
  (`eng/pdfium/version.json`).
- **Script:** `pwsh -ExecutionPolicy Bypass -File .\eng\pdfium\build.ps1` downloads the archive,
  checks the hash, and copies `pdfium.dll` to the runtime folder of developer builds.
- **Licences:** `eng/pdfium/LICENSE` and `eng/pdfium/licenses/`.

## Project layout

```
PdfViewer.slnx                 the solution
Directory.Build.props          shared build settings; the app version comes from the v* git tag
assets/                        icons
benchmarks/PdfViewer.Benchmarks/
docs/                          this site; engine design and status in docs/pdf-viewer-vector-migration/
eng/
  msix/                        Microsoft Store manifest template and identity
  pdfium/                      PDFium for developer and test builds (the oracle)
  releasegate/                 payload-size, cold-start and PDFium-free release gates
  signing/                     code-signing notes
  vectorpdf/                   corpus tooling (vectorpdf), baselines, the git-ignored corpus
  webdemo/                     static server, Noto font pins and build script for the web demo
samples/                       sample documents
scripts/                       build_publish.ps1, build_msix.ps1, generate_sbom.ps1, signing scripts
src/
  PdfEngine.Abstractions/      UI-neutral contracts and models (NuGet package)
  PdfEngine.Vector/            the PDF engine in pure C# (NuGet package)
  PdfEngine.Vector.Skia/       portable Skia renderer (NuGet package; web and Android)
  PdfEngine.Vector.Direct2D/   Direct2D and DirectWrite renderer and printing (Windows app)
  PdfEngine.Vector.Windows/    WPF fallback renderer (Windows app)
  PdfEngine.Pdfium/            PDFium interop, the oracle of developer and test builds (not shipped)
  PdfViewer.Core/              document session, render scheduler, caches, undo and redo
  PdfViewer/                   the WPF app
  PdfViewer.Web.Engine/        UI-free engine layer of the web demo and the Android app
  PdfViewer.Web/               the web demo (Blazor WebAssembly)
  PdfViewer.Android/           the Android proof of concept (.NET for Android)
  Installer/                   the setup and uninstall program
tests/
  PdfEngine.Vector.Tests/      engine tests
  PdfEngine.Vector.Skia.Tests/ Skia backend, web and Android engine layer tests
  PdfViewer.Tests/             viewer tests: view models, windows, services, release gates
```

## Continuous integration

| Workflow | When | What it does |
| --- | --- | --- |
| `ci.yml` (job `build-and-test`) | Every pull request and push to `main`; nightly | Builds, runs all test suites (with and without PDFium), the corpus report, benchmarks, SBOMs, a signed dry-run publish and the release gates. The nightly run adds deep fuzzing and the full corpus job. |
| `web-demo.yml` | Every pull request and push to `main` | Builds the web demo with the Noto fonts. From `main`, deploys the GitHub Pages site: `docs/` at the root and the demo under `/demo/`. |
| `android.yml` | Changes to the Android app or the code it uses | Builds the APK, checks that it asks for no permission, and uploads it as an artifact. |
| `packages.yml` | Every pull request and push to `main`; `engine-v*` tags | Packs the three engine packages. An `engine-v*` tag publishes them to nuget.org through Trusted Publishing (no stored API key). |
| `npm.yml` | Pull requests that change the SDK, pushes to `main`; `sdk-v*` tags | Packs the embed SDK as the npm package `pdfviewer-embed`. An `sdk-v*` tag publishes it to npmjs.com through Trusted Publishing, with provenance. |
| `release.yml` | `v*` tags | Builds, signs and publishes a GitHub release of the Windows app. |
| `msix.yml` | `v*` tags | Builds the Microsoft Store package and uploads it as an artifact. It publishes nothing. |

The `main` branch is protected: every change comes through a pull request, and the
`build-and-test` check must pass.

## Releases and versions

- **The Windows app:** push a tag `v<major>.<minor>.<patch>` (for example `v4.1.2`). The app
  version comes only from these tags.
- **The engine packages:** push a tag `engine-v<version>` (for example
  `engine-v0.1.0-preview.1`). These tags do not change the app version and do not start an app
  release.
- **The embed SDK on npm:** set `SDK_VERSION` in `pdfviewer-embed.js` and `version` in
  `packages/pdfviewer-embed/package.json` to the same value, then push a tag `sdk-v<version>`
  (for example `sdk-v0.3.0`). The workflow refuses a tag that does not match both.
