# Native Windows PDF Viewer

A high-performance, feature-rich Windows desktop native PDF Viewer application built with **C# / WPF on .NET 9** and powered by the open-source **Google PDFium** native engine (x64).

The solution is structured using the modern XML-based **`.slnx`** solution format, utilizing a version-pinned, checksum-verified native PDFium binary (`chromium/8021` / `154.0.8021.0`), asynchronous background page rendering with an in-memory **LRU cache**, zero intermediate PNG encoding overhead, comprehensive text searching and selection, vector and ink annotations, bookmark tree exploration, high-DPI image export, native Windows printing, and Light/Dark themes.

---

## Table of Contents

- [Architectural Overview](#architectural-overview)
- [Key Features & Capabilities](#key-features--capabilities)
- [Native Engine & PDFium Tooling](#native-engine--pdfium-tooling)
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
- [License & Third-Party Notices](#license--third-party-notices)

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
                                     │ Implementation Adapter
┌────────────────────────────────────▼────────────────────────────────────┐
│                PdfEngine.Pdfium (Native Interop Layer)                  │
│  • SafeHandles (SafeDocumentHandle, SafePageHandle, SafeTextPageHandle) │
│  • PdfiumNativeBridge (C ABI P/Invoke bindings pinned to Chromium 8021) │
│  • Zero-Copy NativeMemoryOwner (IMemoryOwner<byte> BGRA Pixel Buffers)  │
│  • Native Adapters: Renderer, Text, Annotations, Forms, Redaction, etc. │
│  • pdfium.dll (x64 Native Engine: FreeType, libjpeg-turbo, OpenJPEG)    │
└─────────────────────────────────────────────────────────────────────────┘
```

### Key Architectural Strengths

1. **Zero Intermediate Encoding Overhead**:
   Pages render directly into native BGRA memory buffers (`FPDFBitmap_CreateEx`), which construct frozen WPF `BitmapSource` instances without intermediate PNG/JPEG disk encoding and decoding roundtrips.
2. **Deterministic Native Memory & SafeHandles**:
   All unmanaged PDFium objects (`FPDF_DOCUMENT`, `FPDF_PAGE`, `FPDF_TEXTPAGE`, `FPDF_SCHHANDLE`, `FPDF_ANNOTATION`) are wrapped in specialized `.NET` `SafeHandle` classes ensuring leak-free disposal even during cancellations or exceptions.
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
- **Interactive Text Selection & Clipboard Copying**:
  - **Accurate Glyph Extraction**: Extracts characters and words directly from PDF text streams with sub-pixel bounding box accuracy.
  - **I-Beam Cursor**: Dynamic cursor detection when hovering over selectable text.
  - **Mouse Drag & Multi-Line Selection**: Click and drag across lines or paragraphs to highlight text in translucent blue accent.
  - **Double-Click Word Selection**: Double-click any word to highlight it instantly.
  - **Clipboard Copying (`Ctrl+C`)**: Copy formatted text with natural paragraph line breaks to the Windows clipboard.
  - **Select All on Page (`Ctrl+A`)**: Instantly select all text across the current page.
  - **Convert Selection to Highlight**: Right-click or use Edit menu to turn selected text into a permanent vector annotation.
- **In-Document Text Search**:
  - Real-time searching powered by PDFium native text search APIs.
  - Case-sensitive / case-insensitive search toggle.
  - Match occurrence counter (e.g. *"Match 2 of 14 (Page 3)"*).
  - Next match (`F3`) and Previous match (`Shift+F3`) navigation.
  - Dedicated **Search Results Tab** in the sidebar listing all occurrences with text snippets and page numbers for double-click navigation.
- **Annotations & Markup**:
  - Highlights, Underlines, Strikethrough, Notes, FreeText, Rectangles, Ellipses, and Freehand Ink.
  - **Multi-Mode Saving**:
    - **Embedded**: Standard PDF annotation objects preserved for editing in Adobe Acrobat / PDFium.
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
  - High-resolution (300 DPI) paginated physical/virtual printing via `PdfiumPdfPaginator`.
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

## Native Engine & PDFium Tooling

The application uses an officially pinned standalone release of Google PDFium from `bblanchon/pdfium-binaries`.

- **Release Pin**: `chromium/8021` (`154.0.8021.0`)
- **Architecture**: Windows x64 (Non-V8 / Non-XFA standalone)
- **Verified SHA-256 Checksum**: `ADAC8CE034015427B5DAA81F8EEDDFCC8E84BC2A9F036F007890FF18BD4388C4`
- **Automation Script**: `eng/pdfium/build.ps1` (downloads, verifies checksum, extracts headers, copies `pdfium.dll` to runtime folders, and updates `THIRD_PARTY_NOTICES.md`).

To re-run or verify the native tooling setup:
```powershell
pwsh -ExecutionPolicy Bypass -File .\eng\pdfium\build.ps1
```

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
├── THIRD_PARTY_NOTICES.md         # Open-source licenses & notices (Google PDFium)
│
├── assets/                        # Multi-resolution application & file icons
├── benchmarks/
│   └── PdfViewer.Benchmarks/      # BenchmarkDotNet & high-throughput memory metrics
├── eng/pdfium/                    # Native PDFium configuration & header definitions
├── publish/                       # Output folder for distribution (Setup & Portable EXE, SBOM)
├── samples/                       # Multi-page test documents
├── scripts/
│   ├── build_publish.ps1          # Automated 1-click build, package, SBOM & publish script
│   └── generate_sbom.ps1          # CycloneDX v1.6 and SPDX v2.3 SBOM generation script
│
├── src/
│   ├── PdfEngine.Abstractions/    # UI-neutral domain models, geometry, exceptions & contracts
│   ├── PdfEngine.Pdfium/          # Native PDFium safe interop engine, adapters & memory manager
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

To execute the automated xUnit test suite covering native PDFium initialization, rendering, bookmarks, search, LRU caching, image export, encrypted PDFs, multithreading, and 500-page handling:

```powershell
dotnet test PdfViewer.slnx
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
- Click and drag across words or paragraphs to select text.
- Double-click any word to select it.
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
No. The application is completely open-source and powered by the Google PDFium native engine (BSD 3-Clause / Apache 2.0). There are zero commercial license keys, evaluations, or page limits.

### 2. How does the application handle very large PDF files (e.g. 500+ pages)?
The application does not rasterize all pages into memory at once. It uses the `LruPageCache` to maintain only currently visible and nearby pages in memory at a bounded limit (default 60 pages). As you scroll through the document, pages behind you are evicted and new pages are rendered asynchronously on background threads.

### 3. Can I build with Visual Studio?
Yes. Open `PdfViewer.slnx` in **Visual Studio 2022 (v17.12 or newer)**. Visual Studio natively recognizes `.slnx` solution files with full IntelliSense, debugging, and test runner support.

---

## License & Third-Party Notices

- **PDF Viewer Application**: Licensed under the MIT License.
- **PDF Engine**: Google PDFium (BSD 3-Clause / Apache 2.0). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full license text.
