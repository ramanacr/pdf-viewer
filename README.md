# Native Windows PDF Viewer

A high-performance, feature-rich Windows desktop native PDF Viewer application built with **C# / WPF on .NET 9** and powered by its own PDF engine, **`PdfEngine.Vector`**, drawn by Direct2D/DirectWrite (x64). The published application contains no PDFium; developer and test builds keep Google PDFium as the reference the engine is compared with.

The solution is structured using the modern XML-based **`.slnx`** solution format, asynchronous background page rendering with an in-memory **LRU cache**, zero intermediate PNG encoding overhead, comprehensive text searching and selection, vector and ink annotations, bookmark tree exploration, high-DPI image export, native Windows printing, and Light/Dark themes.

---

## Table of Contents

- [Privacy & Safety](#privacy--safety)
- [Optional Components](#optional-components)
- [Architectural Overview](#architectural-overview)
- [Key Features & Capabilities](#key-features--capabilities)
- [PDFium (Developer and Test Builds Only)](#pdfium-developer-and-test-builds-only)
- [Rendering Engines (Vector Migration)](#rendering-engines-vector-migration)
- [Prerequisites & System Requirements](#prerequisites--system-requirements)
- [Project & Directory Structure](#project--directory-structure)
- [Building & Running](#building--running)
  - [Build Solution](#build-solution)
  - [Run Application](#run-application)
  - [Command-Line Arguments](#command-line-arguments)
  - [Publish Single-File Executable](#publish-single-file-executable)
  - [Run Automated Tests](#run-automated-tests)
- [User Guide & Workflows](#user-guide--workflows)
  - [Opening Documents & Drag-and-Drop](#opening-documents--drag-and-drop)
  - [Viewing Modes & Page Navigation](#viewing-modes--page-navigation)
  - [Zooming, Scaling & Panning](#zooming-scaling--panning)
  - [Text Selection, Copying & Highlighting](#text-selection-copying--highlighting)
  - [Searching Text](#searching-text)
  - [Navigating Bookmarks & Thumbnails](#navigating-bookmarks--thumbnails)
  - [Exporting Pages to Images](#exporting-pages-to-images)
  - [Printing Documents](#printing-documents)
  - [Inspecting Document Properties](#inspecting-document-properties)
  - [Switching Themes (Light / Dark)](#switching-themes-light--dark)
  - [Handling Password-Protected PDFs](#handling-password-protected-pdfs)
- [Comprehensive Keyboard Shortcuts](#comprehensive-keyboard-shortcuts)
- [Troubleshooting & FAQ](#troubleshooting--faq)
- [Code signing policy](#code-signing-policy)
- [License & Third-Party Notices](#license--third-party-notices)

---

## Privacy & Safety

A PDF is a program as much as it is a page. The format can carry JavaScript, launch actions,
embedded files and remote references, and the long tail of viewer vulnerabilities lives in
exactly those features. This viewer takes the opposite position from the mainstream readers:
**a document is data, and it is never given a chance to act.**

| Guarantee | What backs it |
| --- | --- |
| No script engine is present | The engine has no JavaScript interpreter: a form's formatting and validation scripts are recognised (the Acrobat `AF*` calls) without being run, and any other script is reported and left inert. No JavaScript runtime (`v8.dll`, `v8_context_snapshot.bin`, `snapshot_blob.bin`, `icudtl.dat`) ships with the application, and a test asserts their absence. |
| Embedded actions are never executed | JavaScript, `/Launch` actions and embedded files are parsed, reported and left inert. Links open only on an explicit click, through `PdfSecurityPolicy`. |
| You are told what is inside | Every document is inspected on open by the engine (`PdfNativeSafetyInspector`). Anything active is named in the status bar and detailed under **Tools → Document Safety**. |
| You can defuse it | **Tools → Save Clean Copy** writes a copy with the script, launch actions and embedded files removed, keeping the pages. The copy is re-inspected before it is handed back, and discarded if it is not actually clean. The original is never modified. Both major competitors put sanitization behind a paid tier; here it is not even behind the feature gate. |
| Attachments come out only when you ask | **File → Embedded Files** lists what the document carries and extracts one to a path you choose. Nothing is unpacked on open, an executable type is flagged before anything is written, the extracted file is written as data and never launched, and the document-supplied name cannot steer the save outside the folder you picked. |
| No telemetry, no analytics, no account | Nothing about you or your documents is recorded or transmitted. There is no sign-in and no cloud storage. |
| Documents stay on this machine | Files are read from and written to local paths you choose. |
| Two possible network requests, both opt-in | An update check against the public GitHub releases page — **off until you allow it**, asked once on first launch, changeable under **Help → Privacy & Safety** — and downloading an [optional component](#optional-components) if you ask for one. Neither sends anything about you or your documents. Decline both and the application never opens a connection. |
| The bill of materials is published | Every release ships CycloneDX and SPDX SBOMs so the above can be checked rather than taken on trust. |

The claims shown in **Help → Privacy & Safety** are derived from the installation at runtime,
not hard-coded, so they cannot quietly stop being true.

---

## Optional Components

Some features are not shipped in the installer, so a user who does not want them does not carry
them. The installer offers each one as a tick box, and the application can fetch one later on
request.

| Component | Size | Contains |
| --- | --- | --- |
| Read Aloud | 669 KB | `System.Speech.dll` — text to speech through the voice built into Windows |

Downloading code that the application will then load is exactly the pattern this product tells
users to be wary of, so it is done on the following terms:

- **Never without being asked.** The installer's box is off by default, and the application
  states what it will fetch, how big it is and where from before anything happens.
- **Fetched over HTTPS from this project's own GitHub release assets**, at the URL for the
  running version — so the component always matches the application that loads it.
- **Checked against a SHA-256 pinned inside the binary.** A file that does not match is deleted,
  not used, and the same check decides whether an already-present file counts as installed, so a
  swapped or truncated one is treated as absent.
- **The build refuses to package** if the file it is about to publish does not match the pinned
  hash, which is what stops the pin and the asset drifting apart.
- **Absence is never a crash.** Every reference to the optional assembly is confined to one
  factory method, because the runtime resolves an assembly when it first compiles a method that
  mentions one; a stray reference on the startup path would turn "not installed" into "will not
  start".

---

## Architectural Overview

The application follows the **Model-View-ViewModel (MVVM)** architectural pattern using `CommunityToolkit.Mvvm`, strictly separating the user interface, business logic, and native rendering engine.

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           WPF UI Layer (.NET 9)                         │
│  • MainWindow.xaml (Ribbon, Menus, Status Bar, Dark/Light Themes)       │
│  • MainViewModel (Controllers: Navigation, Search, Annotations, Zoom)   │
│  • WpfBitmapAdapter (Zero-copy RenderedPage BGRA -> BitmapSource)       │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│                    PdfViewer.Core (Platform Services)                   │
│  • DocumentSession (Identity, SHA-256 Fingerprint, Revision, Dirty)     │
│  • RenderPriorityScheduler (Deduplication, Priority Queue, Cancel)     │
│  • MultiTierCache (Memory-Budgeted LRU Byte Ceiling & Metrics)          │
│  • CommandHistory (Transactional Undo / Redo Stack)                     │
│  • PdfSecurityPolicy (Sandboxing, Links, JavaScript, Execution Limits)  │
│  • PdfComparisonService (Pixel Heatmap Diff & Textual Comparison)       │
│  • DefaultOcrEngine (Word token geometry extraction & OCR engine)       │
│  • FeatureGate (Community, Pro, Enterprise, Developer SDK Tiers)        │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │ Engine Contracts
┌────────────────────────────────────▼────────────────────────────────────┐
│                PdfEngine.Abstractions (UI-Neutral Domain)                │
│  • IPdfEngine, IPdfDocument, IPdfPage, IPdfRenderer                     │
│  • IPdfTextService, IPdfAnnotationService, IPdfSaveService              │
│  • IPdfPageOrganizerService (Rotate, Delete, Insert, Merge, Split)      │
│  • IPdfFormService, IPdfSignatureService, IPdfRedactionService          │
│  • Typed Exception Hierarchy (PdfOpenException, Corrupt, Password, etc) │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │ Implementation
┌────────────────────────────────────▼────────────────────────────────────┐
│                PdfEngine.Vector (the PDF engine, managed)               │
│  • Parser, xref repair, security handlers (RC4, AES, certificates)     │
│  • Fonts, text page, search, structure tree, display lists             │
│  • Forms, annotations, signatures, redaction, saving, page assembly    │
│  • PdfEngine.Vector.Direct2D / .Windows: drawing and printing          │
└─────────────────────────────────────────────────────────────────────────┘
```

Developer and test builds also have `PdfEngine.Pdfium` (PDFium interop and `pdfium.dll`), the
oracle the engine is tested against; the published application does not contain it.

### Key Architectural Strengths

1. **Zero Intermediate Encoding Overhead**:
   Pages are drawn by Direct2D straight into BGRA buffers (or, for zoomed detail, into a texture shared with the screen), which become frozen WPF `BitmapSource` instances without intermediate PNG/JPEG encoding and decoding roundtrips.
2. **Managed Engine, Bounded Native Surface**:
   The PDF parser and every document service are managed code with resource limits (`PdfSecurityLimits`); native code is limited to Windows' own Direct2D, DirectWrite and WIC, whose COM objects are released deterministically even during cancellations or exceptions.
3. **Thread-Safe LRU Memory Cache**:
   The `LruPageCache` maintains an in-memory linked list and hash map of rendered bitmaps for recently viewed pages and DPIs. When navigating large 500+ page documents, older pages are evicted automatically, keeping the application's memory footprint bounded.
4. **Cancellation-Aware Scrolling & Zooming**:
   When zooming or rapid-scrolling occurs, outdated background rendering requests are instantly canceled via `CancellationTokenSource`, preventing unnecessary CPU and memory overhead.

---

## Key Features & Capabilities

- **Two Primary Viewing Modes**:
  - **Continuous Vertical Scrolling**: Pages render sequentially in a smooth vertical scroll viewport with page shadows and separation margins.
  - **Single Page Paginated View**: Focused single-page viewing with rapid pagination controls and jumping.
- **Dynamic Zoom & Scaling**:
  - **Fit to Width** (`Ctrl+1`): Automatically fits the document width to the available viewport width.
  - **Fit to Page** (`Ctrl+0`): Scales the entire page (width and height) to fit inside the visible viewport.
  - **Arbitrary Zoom Range**: 25% up to 500% with slider control, preset increments, and live percentage display.
  - **Mouse Wheel Zoom**: Smooth dynamic zooming using `Ctrl + MouseWheel`.
  - **Interactive Panning**: Hand/Pan tool toggle or Middle-Mouse drag to pan smoothly around zoomed pages.
- **Page Rotation**:
  - Rotate current document view 90° Clockwise (`Ctrl+R`) or Counter-Clockwise (`Ctrl+Shift+R`).
- **Two-Page (Facing) View**: pages side by side the way the document was printed, with an option to keep the cover on its own so the spreads line up with the original. The toolbar button cycles continuous → single page → facing.
- **Read Aloud** (*Tools → Read Aloud*): reads the document from the current page using the voice built into Windows, following along in the viewer and skipping pages that have no text layer. **Optional component** — not shipped in the installer, so you only carry it if you want it; see [Optional Components](#optional-components).
- **Night Mode** (`Ctrl+Shift+N`): inverts the pages themselves, not just the window around them. Deliberately separate from the light/dark theme — dark chrome with an accurate page, or an inverted page on a light desktop, are both reasonable and the choice stays with you.
- **Save** (`Ctrl+S`): writes your annotations into the open document, keeping them editable. The write goes to a temporary file beside the original and only then replaces it, so a failure leaves your file untouched. Unsaved work is marked with an asterisk in the title bar, and closing or opening another document asks first.
- **Full Screen / Presentation Mode** (`F11`, exit with `F11` or `Esc`): the document fills the screen and all chrome gets out of the way.
- **Navigation History** (`Alt+Left` / `Alt+Right`): returns you to the page you jumped away from. Deliberate jumps — bookmarks, search hits, the page box — are recorded; ordinary scrolling is not, so "back" never undoes your reading.
- **Organize Pages** (*File → Organize Pages*): reorder, rotate, duplicate or leave out pages, and save the result as a new document. Applied in a single pass, so the output is always consistent; the original is never modified.
- **Export Text** (*File → Export Text*): writes the document's text layer to a plain text file, and says so plainly when a page has no text layer rather than handing back an empty file.
- **Embedded Files** (*File → Embedded Files*): lists what the document carries, flags attachments Windows would treat as executable, and extracts one only when you ask. Nothing is ever unpacked on open or run.
- **Word-processor text selection** (character-level text layout from the engine's text page: reading order, inferred spaces and line breaks, line-end hyphens):
  - **Click and drag** selects character by character, across lines, columns and pages; the view scrolls when the pointer nears the edge.
  - **Double-click** selects only the word (no trailing space or punctuation; `don't`, `3.14` and `snake_case` stay whole); **double-click and drag** extends by whole words.
  - **Triple-click** selects the paragraph; **Ctrl+click** the sentence.
  - **Shift+click** extends the selection; **Shift+←/→** by character, **Ctrl+Shift+←/→** by word, **Shift+↑/↓** by line (keeping the column), **Shift+Home/End** to the line's ends, **Ctrl+Shift+Home/End** to the page's.
  - The selection is a flat band per line at the line's full height, covering the spaces between words.
  - **Copy (`Ctrl+C`)** keeps line breaks and joins words hyphenated across lines; **Select All (`Ctrl+A`)** selects the current page.
  - **Highlight from selection** covers each selected line (one quad per line, saved as `/QuadPoints` and XFDF `coords`), not the rectangle around them; text under a highlight can still be selected by dragging.
- **Accessibility and Read Mode (`Ctrl+4`)**:
  - **Tagged PDFs** (ISO 32000-2 14.8) are read in structure order: headings, paragraphs, list items, table cells, quotes and code keep their roles, figures are described by their `/Alt` text, `/ActualText` replaces what it stands for, and artifacts (running headers, page numbers) are left out.
  - **Untagged pages** get paragraphs from the text layout, headings from their larger text, list items from their markers, and vertical text read as words.
  - **Read mode** reflows that content (size with `Ctrl+Plus/Minus`, follows the dark theme and night mode). It is a WPF FlowDocument, which UI Automation exposes as a text document, so Narrator, NVDA and JAWS read and navigate it by paragraph, line, word and character. Pages carry accessible names. The document's accessibility-extraction permission is honoured.
- **In-Document Text Search**:
  - Real-time searching over the engine's text page.
  - Case-sensitive / case-insensitive search toggle.
  - Match occurrence counter (e.g. *"Match 2 of 14 (Page 3)"*).
  - Next match (`F3`) and Previous match (`Shift+F3`) navigation.
  - Dedicated **Search Results Tab** in the sidebar listing all occurrences with text snippets and page numbers for double-click navigation.
- **Annotations & Markup**:
  - Highlights, Underlines, Strikethrough, Notes, FreeText, Rectangles, Ellipses, and Freehand Ink.
  - **Multi-Mode Saving**:
    - **Embedded**: Standard PDF annotation objects preserved for editing in Adobe Acrobat and other viewers.
    - **Flattened**: Annotations permanently baked into page graphics.
    - **Export XFDF**: XML-based comments exported without modifying the original document.
    - **Strict Overwrite Protection**: Prohibits destructive accidental overwriting of the source document.
- **Document Bookmarks / Table of Contents**:
  - Automatically parses PDF document outlines into a hierarchical `TreeView`.
  - Click any node in the bookmark tree to jump immediately to the target page.
- **Page Thumbnails Sidebar**:
  - Scrollable thumbnail cards showing mini-previews of every page in the document.
  - Visual highlight indicating the currently active page.
  - Click any thumbnail to jump to that page.
- **Image Export**:
  - Export document pages to high-resolution **PNG** or **JPEG** images.
  - Selectable DPI: 150 DPI (Standard Screen), 300 DPI (High-Resolution Print), or 600 DPI (Ultra Sharp).
  - Export all pages, the current page only, or a specific page range (e.g. pages 2 to 7).
- **Interactive Print & Live Print Preview**:
  - Dedicated **Modern In-App Print & Print Preview Dialog** (`Ctrl+P`).
  - Live interactive document page preview with page navigation (`<<`, `<`, `Page X of Y`, `>`, `>>`).
  - Printer discovery, status inspection, orientation (Portrait/Landscape/Auto), copies, collate, color mode (Color vs Grayscale), and custom page range formatting.
  - Vector printing through Direct2D's print control, or 300 DPI page images (`PageImagePaginator`) drawn by the same renderer as the screen.
- **Windows Shell Document Preview & Thumbnail Integration**:
  - Complete Windows File Explorer integration: `PerceivedType=document`, `Treatment=0`, `ThumbnailCutoff=0`, and `PreviewDetails` metadata registration.
  - CLI thumbnail generator: `PdfViewer.exe --thumbnail <input.pdf> <output.png> [dpi]` for instant headless page rendering.
  - Shell registration commands: `PdfViewer.exe --register` and `PdfViewer.exe --unregister`.
  - In-app thumbnail sidebar with interactive hover tooltip document previews.
- **Page Assembly & Operations**:
  - Rotate individual or all pages (0°, 90°, 180°, 270°).
  - Insert blank pages with custom dimensions.
  - Delete individual pages with boundary guards.
  - Extract arbitrary page ranges (e.g. 1-5, 8, 12) into a standalone PDF.
  - High-speed Multi-Document Merging with live progress reporting.
  - Document Splitting by page count intervals.
  - Transactional Undo/Redo support for page manipulations.
- **Irreversible Content Redaction**:
  - Redaction bounding box markup with custom blackout overlay text.
  - Irreversible vector flattening and underlying text stream destruction.
- **Forms & AcroForm Support**:
  - AcroForm field discovery and inspection (Text, Checkbox, Radio, Choice).
  - Standard XFDF form data import and export.
  - Document form field flattening.
- **Document Comparison & Diff Heatmaps**:
  - Page-by-page textual diffing (Added, Deleted, Modified text tokens).
  - Visual pixel comparison with `VisualSimilarityScore` (0.0 to 1.0) and visual difference heatmap overlay generation (magenta highlighting).
- **Pluggable OCR Engine**:
  - Sub-pixel word token extraction with confidence scoring and searchable overlay generator.
- **High-Performance Architecture & Benchmarks**:
  - 150 DPI Raw Render: **7.95 ms/page** (125.8 pages/sec).
  - Full Text Extraction: **0.10 ms/page** (5 ms for 50 pages).
  - Memory-budgeted priority scheduler strictly maintaining memory under configured limits.
- **Software Bill of Materials (SBOM)**:
  - Automated CycloneDX v1.6 (`sbom.cyclonedx.json`) and SPDX v2.3 (`sbom.spdx.json`) generation for supply chain transparency.

---

## PDFium (Developer and Test Builds Only)

The published application has no PDFium (M10): `scripts/build_publish.ps1` builds with `-p:PdfiumRenderer=false`, so neither `pdfium.dll` nor `PdfEngine.Pdfium` is referenced, copied or bundled, and the PDFium-free release gate checks the payload and starts the app with PDFium disabled. Developer and test builds (`dotnet build`, `dotnet test`, the default `PdfiumRenderer=true`) keep an officially pinned standalone release of Google PDFium from `bblanchon/pdfium-binaries` as the oracle the engine is tested against, and as the `Pdfium` engine mode. Its licences are in `eng/pdfium/` (`LICENSE`, `licenses/`).

- **Release Pin**: `chromium/8021` (`154.0.8021.0`)
- **Architecture**: Windows x64 (Non-V8 / Non-XFA standalone)
- **Verified SHA-256 Checksum**: `ADAC8CE034015427B5DAA81F8EEDDFCC8E84BC2A9F036F007890FF18BD4388C4`
- **Automation Script**: `eng/pdfium/build.ps1` (downloads, verifies checksum, extracts headers and copies `pdfium.dll` to the runtime folder of developer builds).

To re-run or verify the native tooling setup:
```powershell
pwsh -ExecutionPolicy Bypass -File .\eng\pdfium\build.ps1
```

---

## Rendering Engines (Vector Migration)

The viewer has moved from PDFium to its own vector-first engine (`PdfEngine.Vector`, rendered by Direct2D/DirectWrite in `PdfEngine.Vector.Direct2D`, with the WPF renderer `PdfEngine.Vector.Windows` as fallback); the published application is PDFium-free (M10). The plan, decisions and gates live in [`docs/pdf-viewer-vector-migration/`](docs/pdf-viewer-vector-migration/00_README.md); current status and measured evidence are in [`18_IMPLEMENTATION_STATUS.md`](docs/pdf-viewer-vector-migration/18_IMPLEMENTATION_STATUS.md).

**Engine modes** (View → Rendering Engine, or the `PDF_ENGINE_MODE` environment variable: `Auto`, `Hybrid`, `Vector`, and `Pdfium` in developer builds):

| Mode | Behaviour |
|---|---|
| `Auto` / `Hybrid` | Every page, tile, print and export image is drawn by the vector engine. Blend modes, soft masks, transparency groups, knockout and non-isolated groups, tiling patterns, glyph-outline clips, vertical writing, predefined CJK CMaps, all shading types, and JPEG, JPEG 2000, JBIG2 and CCITT images are rendered natively; form fields are drawn from their appearance streams, generated where the form asks for it (`/NeedAppearances`) or a widget has none. Damaged or unsupported content is drawn by best effort, as PDFium draws it: unknown operators are ignored, undecodable streams and images skipped, fonts that cannot be loaded replaced by a substitute for their name. Encrypted documents (RC4 and AES up to 256-bit, user or owner password, and certificate-encrypted files that PDFium cannot open) are decrypted by the vector engine itself, and their permissions (print, copy, edit, annotate, forms) are enforced unless the owner password is given. |
| `Vector` | Strict vector mode for development and CI: what the engine still classifies as unsupported is outlined in orange, and a page that fails to build or render is an error rather than a blank page. |
| `Pdfium` | Every page rendered by PDFium, the reference the engine is compared with. Only in builds made with `PdfiumRenderer=true` (the default for `dotnet build` and `dotnet test`); the published app (`scripts/build_publish.ps1`, `-p:PdfiumRenderer=false`) has no PDFium renderer and treats this mode as `Auto`. |

The status-bar badge shows how the current page was drawn (`⚡ Vector`, `⚡ Vector (N approximated)`, strict mode's `⚡ Vector (N unsupported)`, or `⚠ Unreadable page` for a page the engine cannot read at all, shown blank); its tooltip lists the classified reasons.

**Nothing stays on PDFium.** Opening (passwords, repair, permissions, certificate encryption), drawing, text extraction and search, annotations, forms, attachments, document safety and clean copies, saving, redaction, signatures and page organisation are all the engine's (M9 and M10). **Releases ship no `pdfium.dll` and no `PdfEngine.Pdfium`**; the release gate fails a payload that has either, and starts the published app with `PDF_PDFIUM_DISABLED=1` to open, draw and search a document (`eng/releasegate/README.md`). Removing PDFium took 7.1 MiB off the payload and 3.6 MiB off its compressed form, which the installer carries.

**Sharp at every zoom (25–1600 %):** pages are rendered by Direct2D (GPU, or WARP without one); when a page is zoomed past its bitmap, the visible area is re-rendered at exact device resolution as a detail tile over it. Night mode draws the page and its tiles as by day and inverts them on the GPU (a Direct2D Invert effect). On a GPU in a local session, vector detail tiles go straight from Direct2D to the screen through a shared texture (`D3DImage`), with no copy through the CPU; `PDF_GPU_PRESENT=0` turns that off. `PDF_VECTOR_HOST=wpf` selects the earlier live WPF drawing host. Measure on-screen frame times with `vectorpdf live <file.pdf>` (opens a window).

**Known limitations of the vector path:** the WPF fallback renderer is ~2× slower than PDFium, while Direct2D is faster than PDFium at every zoom (`eng/vectorpdf/baseline-bench.txt`).

**Verification tooling:**
```powershell
dotnet test tests/PdfEngine.Vector.Tests                        # unit, fixture, PDFium-differential and fuzz tests
dotnet test tests/PdfViewer.Tests                               # the viewer, with the PDFium oracle tests
dotnet test tests/PdfViewer.Tests -p:PdfiumRenderer=false       # the viewer as published: no PDFium, oracle tests left out
$env:VECTOR_FUZZ_ITERATIONS=100000; dotnet test tests/PdfEngine.Vector.Tests --filter Fuzz
dotnet run --project eng/vectorpdf/tools/VectorPdf.Tool -c Release -- bench --pages 20
dotnet run --project eng/vectorpdf/tools/VectorPdf.Tool -c Release -- corpus <folder> --out report.jsonl --diff
```
The corpus runner reads files locally and writes one JSON line per file (SHA-256, open outcome for both engines, fallback reasons and area, pixel difference). No document content leaves the machine.

---

## Prerequisites & System Requirements

- **Operating System**: Windows 10 (version 1809 or higher) / Windows 11 (64-bit).
- **.NET Runtime / SDK**: [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (SDK `9.0.100` or higher).
- **IDE (Optional)**: Visual Studio 2022 (v17.12 or higher with `.slnx` support) or Visual Studio Code with *C# Dev Kit*.

---

## Project & Directory Structure

```
d:\Practice\pdf-viewer\
│
├── Directory.Build.props          # Dynamic auto-incremented build numbering
├── PdfViewer.slnx                 # Modern XML-based Solution file (.NET 9+)
├── README.md                      # Complete architecture, benchmark & usage guide
├── THIRD_PARTY_NOTICES.md         # Open-source licenses & notices of what ships
│
├── assets/                        # Multi-resolution application & file icons
├── benchmarks/
│   └── PdfViewer.Benchmarks/      # BenchmarkDotNet & high-throughput memory metrics
├── eng/pdfium/                    # PDFium for developer and test builds (the oracle): pin, headers, licences
├── publish/                       # Output folder for distribution (Setup & Portable EXE, SBOM)
├── samples/                       # Multi-page test documents
├── scripts/
│   ├── build_publish.ps1          # Automated 1-click build, package, SBOM & publish script
│   └── generate_sbom.ps1          # CycloneDX v1.6 and SPDX v2.3 SBOM generation script
│
├── src/
│   ├── PdfEngine.Abstractions/    # UI-neutral domain models, geometry, exceptions & contracts
│   ├── PdfEngine.Pdfium/          # PDFium interop, the oracle of developer and test builds (not shipped)
│   ├── PdfViewer.Core/            # DocumentSession, RenderPriorityScheduler, MultiTierCache & Undo/Redo
│   ├── PdfViewer/                 # Modern WPF MVVM application with Light/Dark themes
│   └── Installer/                 # Windows Graphical Setup Installer & Uninstaller
│
└── tests/
    └── PdfViewer.Tests/           # 63 automated unit, integration, comparison & engine tests
```

---

## Building & Publishing

### 1-Click Automated Build & Package Script

To build the entire solution and generate both the **Setup Installer** and **Standalone Executable** into the `publish/` folder:

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\build_publish.ps1
```

This generates:
- `publish\PdfViewerSetup.exe`: Native Windows Installable Setup wizard with Desktop shortcut, Start Menu shortcut, and Windows Add/Remove Programs uninstaller.
- `publish\PdfViewer.exe`: Portable single-file standalone executable with all native binaries bundled.
- `publish\THIRD_PARTY_NOTICES.md`: Third-party open-source license documentation.
- `publish\SampleDocument.pdf`: Pre-packaged demo document.

### Manual Build

```powershell
dotnet build PdfViewer.slnx -c Release
```

### Run from Source

```powershell
dotnet run --project src\PdfViewer\PdfViewer.csproj
```

Or pass a PDF file directly:
```powershell
dotnet run --project src\PdfViewer\PdfViewer.csproj -- "samples\SampleDocument.pdf"
```

### Run Automated Tests

To execute the automated xUnit test suites covering rendering, bookmarks, search, LRU caching, image export, encrypted PDFs, multithreading, and 500-page handling, with PDFium as the oracle:

```powershell
dotnet test PdfViewer.slnx
dotnet test tests/PdfViewer.Tests -p:PdfiumRenderer=false   # the published configuration, without PDFium
```

---

## User Guide & Workflows

### Opening Documents & Drag-and-Drop

1. **File Dialog**: Click the **Open** icon on the ribbon toolbar or press `Ctrl + O`.
2. **Drag and Drop**: Drag any `.pdf` file from Windows Explorer and drop it onto the viewer window.
3. **Recent Files**: Select **File > Open Recent** from the top menu or choose a document from the *Recently Opened* list on the welcome screen.
4. **Sample Demo**: Click **Open Sample Demo** on the welcome screen or select **File > Open Sample Demo Document**.

### Viewing Modes & Page Navigation

- **Continuous Scrolling**: Scroll through all pages naturally with smooth vertical scrolling.
- **Single Page View**: Focus on one page at a time.
- **Toggle View Mode**: Click the **View Mode** button on the toolbar or select **View > Continuous Scrolling** / **Single Page View** in the menu.
- **Navigation Controls**:
  - `|<` / `>|`: Jump to First / Last Page (or `Home` / `End`).
  - `<` / `>`: Previous / Next Page (or `PageUp` / `PageDown`).
  - **Page Input Box**: Type a page number directly and press `Enter` to jump.

### Zooming, Scaling & Panning

- **Zoom In / Out**: Use the `+` / `-` buttons, the zoom slider, or keyboard shortcuts (`Ctrl + +` / `Ctrl + -`).
- **Dynamic Mouse Zoom**: Hold `Ctrl` and scroll the **Mouse Wheel** to zoom dynamically toward the viewport center.
- **Fit to Width** (`Ctrl + 1`): Scales the page width to fill the viewing area.
- **Fit to Page** (`Ctrl + 0`): Scales the entire page to fit within the viewing area.
- **Pan / Hand Tool**: Click the **Pan Tool** icon on the toolbar or press `H`. Click and drag with the left mouse button, or click and drag with the **Middle Mouse Button** anytime to pan around the page.

### Text Selection, Copying & Highlighting

- Hover over any text to see the I-Beam cursor.
- Click and drag to select character by character (across pages too); Shift+click or Shift+arrows extend it.
- Double-click a word to select just that word; triple-click selects the paragraph, Ctrl+click the sentence.
- Press `Ctrl + C` to copy the selected text to clipboard.
- Right-click or use the toolbar button to convert selected text into a persistent Highlight annotation.

### Searching Text

1. Press `Ctrl + F` or click the **Find** icon on the toolbar to open the search bar.
2. Type your search query and press `Enter` (or click **Search**).
3. Toggle the **Match Case** checkbox if exact casing is required.
4. Use `F3` (Next Match) or `Shift + F3` (Previous Match) to navigate through occurrences.
5. In the left sidebar, click the **Search Results** tab to see a full list of matching snippets with page numbers. Double-click any result to jump directly to it.

### Exporting Pages to Images

1. Click the **Export** icon on the toolbar or select **File > Export to Images...**.
2. Configure your export options:
   - **Output Folder**: Destination directory for images.
   - **File Prefix**: Prefix for filenames (e.g. `report_page_001.png`).
   - **Format**: **PNG (*.png)** or **JPEG (*.jpg)**.
   - **Resolution (DPI)**: **150 DPI** (Standard), **300 DPI** (High Quality), or **600 DPI** (Ultra Sharp).
   - **Page Range**: **All Pages**, **Current Page Only**, or **Custom Range**.
3. Click **Export**. The status bar displays real-time progress.

### Printing Documents

1. Click the **Print** icon on the toolbar or press `Ctrl + P`.
2. The native Windows Print Dialog allows selecting physical printers or Microsoft Print to PDF.
3. High-resolution (300 DPI) rendering with orientation support delivers clean crisp output.

### Inspecting Document Properties

1. Click the **Properties (Info)** icon on the toolbar or press `Ctrl + D`.
2. Displays File Name, Path, File Size, Title, Author, Subject, Keywords, Creator, Producer, Creation Date, Modification Date, PDF Version, Page Count, Dimensions, Encryption, and Engine Status.

---

## Comprehensive Keyboard Shortcuts

| Shortcut | Context | Action |
| :--- | :--- | :--- |
| `Ctrl + O` | Global | Open PDF document via file browser |
| `Ctrl + S` | Annotations Active | Save document with annotations (Embedded / Flattened / XFDF) |
| `Ctrl + P` | Document Loaded | Open native Windows Print dialog |
| `Ctrl + F` | Document Loaded | Toggle in-document Text Search bar |
| `F3` | Search Active | Jump to next search match |
| `Shift + F3` | Search Active | Jump to previous search match |
| `Ctrl + C` | Text Selected | Copy selected text to clipboard |
| `Ctrl + A` | Viewport | Select all text on current page |
| `Ctrl + D` | Document Loaded | Open Document Properties & Metadata dialog |
| `Ctrl + +` / `Ctrl + =` | Document Loaded | Zoom in (+15%) |
| `Ctrl + -` | Document Loaded | Zoom out (-15%) |
| `Ctrl + 0` / `Ctrl + Num0`| Document Loaded | Fit to Page (Fit entire page in viewport) |
| `Ctrl + 1` / `Ctrl + Num1`| Document Loaded | Fit to Width (Fit page width to viewport) |
| `Ctrl + MouseWheel` | Viewport | Dynamic smooth zoom in/out |
| `Ctrl + R` | Document Loaded | Rotate view 90° Clockwise |
| `Ctrl + Shift + R` | Document Loaded | Rotate view 90° Counter-Clockwise |
| `Ctrl + B` | Global | Toggle navigation sidebar visibility |
| `PageDown` / `Right` | Viewport | Go to Next Page |
| `PageUp` / `Left` | Viewport | Go to Previous Page |
| `Home` | Viewport | Jump to First Page |
| `End` | Viewport | Jump to Last Page |
| `Alt + F4` | Global | Exit application |

---

## Troubleshooting & FAQ

### 1. Does the application require any commercial licenses?
No. The application is completely open-source (MIT), with its own PDF engine; the third-party components it ships are open-source too (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)). There are zero commercial license keys, evaluations, or page limits.

### 2. How does the application handle very large PDF files (e.g. 500+ pages)?
The application does not rasterize all pages into memory at once. It uses the `LruPageCache` to maintain only currently visible and nearby pages in memory at a bounded limit (default 60 pages). As you scroll through the document, pages behind you are evicted and new pages are rendered asynchronously on background threads.

### 3. Can I build with Visual Studio?
Yes. Open `PdfViewer.slnx` in **Visual Studio 2022 (v17.12 or newer)**. Visual Studio natively recognizes `.slnx` solution files with full IntelliSense, debugging, and test runner support.

---

## Code signing policy

Release binaries are Authenticode-signed, built only by this repository's release workflow from tagged source. Who commits, reviews and approves releases, how to check a download's signature, and the privacy policy are in [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

---

## License & Third-Party Notices

- **PDF Viewer Application**: Licensed under the [MIT License](LICENSE).
- **PDF Engine**: `PdfEngine.Vector` (this project, MIT); Direct2D access via Vortice.Windows (MIT); predefined CJK CMaps from Adobe cmap-resources (BSD 3-Clause); JPEG 2000 via CoreJ2K (BSD 3-Clause). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full license texts.
