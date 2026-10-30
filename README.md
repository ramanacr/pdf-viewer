# PDF Viewer Native

[![CI](https://github.com/ramanacr/pdf-viewer/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ramanacr/pdf-viewer/actions/workflows/ci.yml) [![Latest release](https://img.shields.io/github/v/release/ramanacr/pdf-viewer)](https://github.com/ramanacr/pdf-viewer/releases/latest) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE) [![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/9.0) [![Good first issues](https://img.shields.io/github/issues/ramanacr/pdf-viewer/good%20first%20issue)](https://github.com/ramanacr/pdf-viewer/labels/good%20first%20issue)

A private, script-free PDF viewer and editor for Windows, with its own open-source PDF engine.
The engine, `PdfEngine.Vector`, is pure C# (.NET 9). On Windows, Direct2D draws its pages. The
same engine also runs in a web browser and on Android, and you can use it as a NuGet library.
The published app contains no PDFium.

**[Get it from Microsoft Store](https://apps.microsoft.com/detail/9N6QDM774R62)** ·
**[Portable app or installer](https://github.com/ramanacr/pdf-viewer/releases/latest)** ·
**[Try it in your browser](https://ramanacr.github.io/pdf-viewer/demo/)** ·
**[Use the engine as a library](docs/developers.md)** ·
**[Features](docs/features.md)**

---

## Get it

| Platform | Status | How |
| --- | --- | --- |
| **Windows app** | Released on the Microsoft Store | [Get it from Microsoft Store](https://apps.microsoft.com/detail/9N6QDM774R62) (Store ID 9N6QDM774R62). Microsoft signs the package, and the Store installs updates. See [Microsoft Store build](docs/microsoft-store.md). |
| **Windows, without the Store** | Released (latest: [GitHub releases](https://github.com/ramanacr/pdf-viewer/releases/latest)) | For a machine without the Store: `PdfViewer.exe` (portable app) or `PdfViewerSetup.exe` (installer). Windows 10 version 2004 or later, or Windows 11, x64. Needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0). The files are signed with the project's self-signed certificate, so SmartScreen can show a warning. See the [code signing policy](CODE_SIGNING_POLICY.md). |
| **Web demo** | Live | [ramanacr.github.io/pdf-viewer/demo/](https://ramanacr.github.io/pdf-viewer/demo/). Runs in the tab as WebAssembly; nothing is uploaded. |
| **Android** | Preview, in a Google Play closed test | Build from source, or take the APK artifact of the `android.yml` workflow. See [its README](src/PdfViewer.Android/README.md). |
| **NuGet packages** | Preview (`0.2.0-preview.1`) | `dotnet add package PdfEngine.Vector.Skia --prerelease`. See [Use the engine as a library](docs/developers.md). The source on `main` also targets `net10.0` and renames some API before 1.0: see [Changes in 1.0](docs/developers.md#changes-in-10). |
| **Web embed SDK** | Preview (npm [`pdfviewer-embed`](https://www.npmjs.com/package/pdfviewer-embed)) | `npm install pdfviewer-embed`. An API like the Adobe PDF Embed API. See [Web embed SDK](https://ramanacr.github.io/pdf-viewer/sdk/). |

Release binaries are Authenticode-signed with the project's own self-signed certificate, so
Windows can show "unknown publisher". Each release states the certificate thumbprint. See the
[code signing policy](CODE_SIGNING_POLICY.md).

## Privacy and safety

A PDF can carry JavaScript, launch actions, embedded files and remote references. Many viewer
vulnerabilities come from these features. This viewer treats **a document as data, and never
lets it act.**

| Guarantee | What backs it |
| --- | --- |
| No script engine | The engine has no JavaScript interpreter. It recognises the Acrobat `AF*` form functions without running them. A test checks that no JavaScript runtime ships with the app. |
| Active content is never run | JavaScript, `/Launch` actions and embedded files are parsed, reported and left inert. Links open only when you click them. |
| You see what is inside | Every document is inspected when it opens. The status bar names active content, and **Tools → Document Safety** shows the details. |
| You can remove it | **Tools → Save Clean Copy** writes a copy without scripts, launch actions and embedded files. The copy is inspected again, and the original is not changed. |
| No telemetry, no account | Nothing about you or your documents is recorded or sent. There is no sign-in and no cloud storage. |
| Network only when you ask | The update check is off until you allow it. Optional components download only when you ask, and are checked against a SHA-256 hash in the app. |
| Published bill of materials | Every release ships CycloneDX and SPDX SBOMs. |

The full statement is in the [privacy policy](docs/privacy.md).

## Features

The Windows app has these groups of features. The full list, a user guide and the keyboard
shortcuts are in [docs/features.md](docs/features.md).

- **View:** continuous, single-page and facing views; zoom from 25 % to 1600 %, sharp at every
  zoom; tabs, bookmarks, thumbnails, night mode, full screen and navigation history.
- **Read:** read mode that screen readers can navigate, tagged-PDF reading order, Read Aloud,
  PDF/UA-1 accessibility check, and **Make Accessible** (auto-tagging).
- **Select and search:** word-processor text selection, search with a results list, and OCR for
  scanned pages (Windows OCR, on this computer).
- **Comment and measure:** highlights, notes, ink, and shapes (rectangle, ellipse, line, arrow,
  polygon, connected lines) with fill, dash and line endings; distance, perimeter and area
  measuring with snapping; standard, dynamic and custom stamps; annotation edit handles.
- **Edit:** text and images in place, page organisation, merge and split, headers, footers,
  watermarks and Bates numbers, reduce file size, compare documents.
- **Forms, signatures and security:** fill and flatten forms, sign (PAdES-B-LT and B-LTA) and
  verify signatures, password protection (AES-256), true redaction.
- **Create and convert:** PDF from images, a scanner or Office files; PDF/A; export to Word,
  Excel, PowerPoint, text and images.
- **Print production:** print with live preview, Output Preview (separations, overprint, total
  ink) and Preflight.
- **Text in fonts that a PDF does not embed:** Arabic and Indic shaping, a fallback font for each
  missing character, and the right Chinese, Japanese or Korean font for the language. Installed
  OpenType (`.otf`) fonts and fonts installed for the current user only are used too.

## Platforms

- **Windows app** (`src/PdfViewer`): WPF on .NET 9. Direct2D and DirectWrite draw the pages; a
  WPF renderer is the fallback.
- **Web demo** (`src/PdfViewer.Web`): Blazor WebAssembly with the portable Skia renderer. It has
  continuous scrolling, zoom, search, the outline, password-protected PDFs, night pages and a
  dark theme. Fonts that a PDF does not embed come from self-hosted Liberation and Noto fonts,
  with Arabic and Indic shaping. There are no cookies, no analytics and no third-party requests.
  See [its README](src/PdfViewer.Web/README.md) for features and limits.
- **Android app** (`src/PdfViewer.Android`, preview): .NET for Android with the same engine and
  Skia renderer. It reads and fills forms. It annotates with highlight, underline, strike-through,
  ink, notes, rectangles, ellipses, lines, arrows, text boxes (Chinese, Japanese and Korean too)
  and standard and dynamic stamps. It saves into the opened file or saves a copy. It asks for no
  permission. See [its README](src/PdfViewer.Android/README.md).

All three use the same engine. See [Architecture](docs/architecture.md).

## Use the engine as a library

```shell
dotnet add package PdfEngine.Vector.Skia --prerelease
```

```csharp
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Text;

using var doc = await PdfVectorDocument.OpenAsync("input.pdf");
Console.WriteLine(await new PdfVectorTextService().ExtractPageTextAsync(doc, 1));
```

The packages are `PdfEngine.Abstractions`, `PdfEngine.Vector` and `PdfEngine.Vector.Skia`. The
API can change before version 1.0. The changes made so far on `main` are in
[Changes in 1.0](docs/developers.md#changes-in-10). A full example (search, and a page saved as PNG) is in
[Use the engine as a library](docs/developers.md).

## Build from source

You need Windows 10 version 2004 or later, the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
and, for the packaging scripts, PowerShell 7.

```powershell
git clone https://github.com/ramanacr/pdf-viewer.git
cd pdf-viewer
dotnet build PdfViewer.slnx -c Release
dotnet run --project src/PdfViewer/PdfViewer.csproj
dotnet test PdfViewer.slnx
pwsh -ExecutionPolicy Bypass -File .\scripts\build_publish.ps1   # installer and portable app in publish\
```

Tests, engine modes, the PDFium reference in developer builds, the Store package, the web and
Android builds, CI and releases: see [Build from source](docs/building.md).

## Contributing

Contributions are welcome: bug reports, test files you may share, documentation and code. Read
[CONTRIBUTING.md](CONTRIBUTING.md) for the set-up, the project rules and the tests. Issues
labelled [good first issue](https://github.com/ramanacr/pdf-viewer/labels/good%20first%20issue)
are a good start. Everyone follows the [Code of Conduct](CODE_OF_CONDUCT.md).

Report security problems privately, as [SECURITY.md](SECURITY.md) describes.

## Documentation

| Page | Contents |
| --- | --- |
| [docs/features.md](docs/features.md) | All features, user guide, keyboard shortcuts |
| [docs/developers.md](docs/developers.md) | NuGet packages, example, [API reference](https://ramanacr.github.io/pdf-viewer/api/) |
| [docs/building.md](docs/building.md) | Build, test, package, CI, releases |
| [docs/architecture.md](docs/architecture.md) | Engine, renderers, app layers |
| [docs/privacy.md](docs/privacy.md) | Privacy policy |
| [docs/microsoft-store.md](docs/microsoft-store.md) | Microsoft Store package |
| [docs/pdf-viewer-vector-migration/](docs/pdf-viewer-vector-migration/00_README.md) | Engine design record and implementation status |

The same pages are on the project site, [ramanacr.github.io/pdf-viewer](https://ramanacr.github.io/pdf-viewer/).

## FAQ

**Do I need a commercial licence?** No. The app and the engine are MIT. The third-party components
that ship are open source too (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)). There are
no licence keys, trials or page limits.

**Does it handle large PDFs?** Yes. It keeps only the visible and nearby pages in memory, and
renders the others on background threads when you scroll to them.

**Can I build it in Visual Studio?** Yes. Open `PdfViewer.slnx` in Visual Studio 2022 17.12 or
later.

## Licence

[MIT](LICENSE). The engine uses Vortice.Windows (MIT) for Direct2D, Adobe cmap-resources
(BSD-3-Clause) for the predefined CJK CMaps, the Adobe Glyph List, and CoreJ2K (BSD-3-Clause) for
JPEG 2000. The web demo bundles Liberation and Noto fonts (SIL Open Font License 1.1). See
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for every licence text.
