# Native Windows PDF Viewer

[![CI](https://github.com/ramanacr/pdf-viewer/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ramanacr/pdf-viewer/actions/workflows/ci.yml) [![Latest release](https://img.shields.io/github/v/release/ramanacr/pdf-viewer)](https://github.com/ramanacr/pdf-viewer/releases/latest) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE) [![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/9.0) [![Good first issues](https://img.shields.io/github/issues/ramanacr/pdf-viewer/good%20first%20issue)](https://github.com/ramanacr/pdf-viewer/labels/good%20first%20issue)

A high-performance, feature-rich Windows desktop native PDF Viewer application built with **C# / WPF on .NET 9** and powered by its own PDF engine, **`PdfEngine.Vector`**, drawn by Direct2D/DirectWrite (x64). The published application contains no PDFium; developer and test builds keep Google PDFium as the reference the engine is compared with.

The solution is structured using the modern XML-based **`.slnx`** solution format, asynchronous background page rendering with an in-memory **LRU cache**, zero intermediate PNG encoding overhead, comprehensive text searching and selection, vector and ink annotations, bookmark tree exploration, high-DPI image export, native Windows printing, and Light/Dark themes.

---

## Table of Contents

- [Privacy & Safety](#privacy--safety)
- [Browser Demo (WebAssembly)](#browser-demo-webassembly)
- [Android App (proof of concept)](#android-app-proof-of-concept)
- [Optional Components](#optional-components)
- [Architectural Overview](#architectural-overview)
- [Key Features & Capabilities](#key-features--capabilities)
- [PDFium (Developer and Test Builds Only)](#pdfium-developer-and-test-builds-only)
- [Rendering Engines (Vector Migration)](#rendering-engines-vector-migration)
- [Prerequisites & System Requirements](#prerequisites--system-requirements)
- [Project & Directory Structure](#project--directory-structure)
- [Building & Publishing](#building--publishing)
  - [1-Click Automated Build & Package Script](#1-click-automated-build--package-script)
  - [Manual Build](#manual-build)
  - [Run from Source](#run-from-source)
  - [Run Automated Tests](#run-automated-tests)
- [User Guide & Workflows](#user-guide--workflows)
  - [Opening Documents & Drag-and-Drop](#opening-documents--drag-and-drop)
  - [Viewing Modes & Page Navigation](#viewing-modes--page-navigation)
  - [Zooming, Scaling & Panning](#zooming-scaling--panning)
  - [Text Selection, Copying & Highlighting](#text-selection-copying--highlighting)
  - [Searching Text](#searching-text)
  - [Exporting Pages to Images](#exporting-pages-to-images)
  - [Printing Documents](#printing-documents)
  - [Inspecting Document Properties](#inspecting-document-properties)
- [Comprehensive Keyboard Shortcuts](#comprehensive-keyboard-shortcuts)
- [Troubleshooting & FAQ](#troubleshooting--faq)
- [Code signing policy](#code-signing-policy)
- [Contributing](#contributing)
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

## Browser Demo (WebAssembly)

**Live demo: https://ramanacr.github.io/pdf-viewer/demo/** (runs entirely in your browser; nothing is uploaded).

The same engine, `PdfEngine.Vector`, also runs **inside a web browser**: [`src/PdfViewer.Web`](src/PdfViewer.Web/README.md)
is a Blazor WebAssembly app that parses, renders (with the portable Skia backend) and searches
PDFs entirely in the tab. A file you open is never uploaded, there are no analytics or cookies,
and document JavaScript never runs. It has continuous scrolling, zoom, search with highlights,
the outline, password-protected PDFs, night pages and a dark theme, and a built-in engine tour
document generated in the browser.

Its limits are stated plainly in its README: a browser has no installed fonts, so text in fonts
a PDF does not embed is drawn only when Chrome or Edge lend the page the device's fonts; CMYK
JPEG images are not decoded yet; the engine runs in the .NET interpreter. Run it locally with
`dotnet workload install wasm-tools` and `dotnet run --project src/PdfViewer.Web`.

---

## Android App (proof of concept)

**Status: proof of concept.** [`src/PdfViewer.Android`](src/PdfViewer.Android/README.md) runs the
same C# engine and the same Skia renderer on Android, through the same UI-free engine layer as the
browser demo: one pure-C# engine on Windows, in the browser and on Android. It is a .NET for
Android app (no MAUI) that opens a PDF through the system document picker, scrolls, pinch- and
double-tap-zooms, searches with highlights, opens password-protected PDFs, and has night pages
and a dark theme. Text in fonts a PDF does not embed is drawn with the phone's own fonts
(Roboto, Noto Serif, Droid Sans Mono, Noto CJK and others in `/system/fonts`).

The app asks for **no permission at all**: no internet access, no storage access. There is no
telemetry. It has been run on an Android 13 emulator; it has not yet been run on a physical
phone. Build it with `dotnet workload install android` and
`dotnet build src/PdfViewer.Android -f net9.0-android -c Release`; CI (`android.yml`) uploads the APK.

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
│  • MainWindow.xaml (Menus, Toolbar, Sidebar, Tabs, Dark/Light Themes)   │
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
  - **Arbitrary Zoom Range**: 25% up to 1600% with slider control, preset increments, and live percentage display. Pages stay sharp at every zoom because they are redrawn as vectors, never scaled up from a bitmap.
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
- **Export to Office** (*File → Export*), written by the app itself with no Office components needed; each export shows progress page by page and stops when chosen again, leaving no file behind:
  - **Word Document (.docx)**: editable text that flows with its fonts and layout, headings, lists, tables, images, links, headers and footers.
  - **Excel Workbook (.xlsx)**: each table on a sheet of its own ("Page 3 Table 1"), with merged cells, the PDF's column widths and row heights, fonts, borders and shading. Numbers (thousands separators, decimals, negatives in parentheses), currency, percentages and dates become values Excel can compute with, keeping how they looked. The text around the tables comes along as rows above and below them.
  - **PowerPoint Presentation (.pptx)**: a slide a page, at the page's size, with the text in editable text boxes where it is (fonts, sizes, colours, line spacing, turned text turned), tables as tables, images as pictures, and rectangles and lines as shapes. Charts and other drawings come over as pictures.
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
- **Measuring** (*Tools → Measure*): distance, perimeter and area.
  - Click points on the page; the result shows live in an info panel: each segment's length, the total, the area, the angle of the last segment, and the pointer's x and y in the chosen units.
  - Points snap to the ends, middles and lines of the page's drawing (hold `Shift` to place a point freely). A double-click or `Enter` finishes a perimeter or area, `Backspace` takes back a point, `Esc` stops.
  - The scale comes from the page when it has measuring viewports (`/VP` with `/Measure`, ISO 32000-2 12.9); otherwise you set it (for example 1 in = 10 ft, in pt, in, mm, cm, m, ft, km or mi), and it is remembered for that document on your computer.
  - A measurement is saved as the annotation Acrobat writes: a Line, PolyLine or Polygon with `/IT /LineDimension`, `/PolyLineDimension` or `/PolygonDimension`, a `/Measure` dictionary, the result as its comment, and a drawn appearance. Adding, deleting and changing one is undoable.
- **Stamps** (*Comment → Stamp*):
  - The standard business stamps (Approved, Completed, Confidential, Draft, Final, For Comment, Not Approved, Received, Reviewed, Revised, Void and more) and dynamic stamps that add your name and the date and time, all drawn as clean vector appearances of our own design. Your name is asked for once and kept in the viewer's settings, never sent anywhere.
  - Custom stamps from a PNG or JPEG picture or a page of a PDF, kept in a local stamp library under `%LOCALAPPDATA%\PdfViewerNative`.
  - Click to place a stamp at its usual size or drag its box; *Rotate New Stamps* turns it. It is a Stamp annotation with `/Name` and its own appearance, so Acrobat and other readers show it the same; it survives saving, flattening, PDF/A conversion and printing, and is undoable.
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
  - Mark selected text, an area or every search result, then apply. The text, image pixels and drawings under the marks are removed from the file itself, not just covered, and the copy keeps no earlier revision.
  - Optional overlay text on each redaction.
- **Forms & AcroForm Support**:
  - Fill fields on the page (text, check boxes, radio buttons, lists, combo boxes) with tab order, formatting, validation and calculations. The Acrobat `AF*` functions are recognised; no script is ever run.
  - **Tools → Import / Export Form Data** (XFDF and FDF), **Reset Form** and **Flatten Form**.
- **Document Comparison & Diff Heatmaps**:
  - Page-by-page textual diffing (Added, Deleted, Modified text tokens).
  - Visual pixel comparison with `VisualSimilarityScore` (0.0 to 1.0) and visual difference heatmap overlay generation (magenta highlighting).
  - **Tools → Compare With Document** shows both versions side by side, with synchronised scrolling and next/previous change.
- **Searchable Scans (OCR)**: **Tools → Make Scanned Pages Searchable** adds an invisible text layer to pages that have none, using the OCR built into Windows, so scans can be searched, selected and copied. Nothing is uploaded.
- **Edit Text & Images** (`Ctrl+E`): edit page text in place, in its own font, size, colour and alignment, with paragraphs reflowed; move, resize, rotate, replace or delete images; add new text and images. Right-to-left (Arabic, Hebrew) and Indic scripts are shaped, and vertical Japanese and Chinese columns keep their sideways Latin and upright digit pairs. Every edit can be undone, and changes are saved as an incremental update.
- **Page Marks** (*Edit → Page Marks*): headers and footers, watermarks, backgrounds and Bates numbering, added or removed as a set.
- **Export to Word** (*File → Export → Word Document*): text with its fonts and styles, headings, lists, real tables, images, drawings as shapes or pictures, and headers and footers with page numbers. The Word document usually keeps the page count of the PDF.
- **Digital Signatures**: sign with a certificate (a `.pfx` file or the Windows certificate store) as a visible or invisible signature, optionally with a picture and an RFC 3161 timestamp, with long-term validation (PAdES-B-LT and B-LTA). **Tools → Verify Digital Signatures** checks signatures and what changed after signing.
- **Password Protection**: **Tools → Protect with Password** (AES-256, with print, copy, edit and form permissions) and **Remove Security**. Encrypted files open with either password, and their permissions are honoured.
- **Create PDF** (*File → Create PDF*), all on this computer, nothing uploaded:
  - **From Images**: JPEG, PNG, TIFF (every page), BMP, GIF, and WebP or HEIC when Windows has their decoders, a page each. Pages fit the picture or go on A4, Letter or Legal with margins; EXIF orientation is honoured. JPEGs are kept byte for byte; other pictures are lossless unless you choose JPEG compression. Greyscale, text recognition (Windows OCR) and PDF/A-2b are options.
  - **From Scanner**: Windows Image Acquisition, from the glass or the document feeder (both sides when the scanner can), in colour, grey or black and white, at the resolution you choose. Scan as often as you like, then create the PDF, searchable if you like. No driver is installed for you.
  - **From File**: Word, Excel and PowerPoint files, RTF and HTML through Microsoft Office's own PDF export (read-only, in a hidden instance, no settings changed); text files are laid out by the app itself when Office is not installed.
  - **Combine Files**: PDFs, pictures and Office files in one PDF, in the order you drag them to, with a bookmark for each file.
- **Reduce File Size**: recompresses images, removes unused objects and subsets fonts. Encrypted documents keep their passwords and permissions.
- **PDF/A** (*Tools → PDF/A: Check and Convert*): validates and converts to PDF/A-1b, 2b and 3b, flattening transparency for PDF/A-1b.
- **Accessibility Check** (*Tools → Accessibility → Accessibility Check*): checks PDF/UA-1, lists each issue with its Matterhorn checkpoint and shows where it is on the page.
- **Make Accessible** (*Tools → Accessibility → Make Accessible*): tags an untagged PDF in a new copy. It finds headings, paragraphs, lists, simple grid tables, figures (with placeholder alternate text to review), links, and headers and footers as artifacts, in reading order (columns and turned pages included). The pages read and draw exactly as before. It then reports the PDF/UA checks that still need a person.
- **Output Preview** (*Tools → Print Production → Output Preview*): shows the page's process and spot plates, separated by the vector engine. You can turn plates on and off, simulate overprinting (`/OP`, `/op`, `/OPM`), highlight total ink over a limit (300 % by default) and read each plate's ink under the mouse. DeviceCMYK goes on the plates as it is. Gray goes on black only. RGB is separated through the engine's own SWOP model, so its plate values are approximate (there is no ICC device link). The preview opens in its own window and does not change the main view.
- **Preflight** (*Tools → Print Production → Preflight*): checks a document against *Digital printing (basic)* or *PDF/X-1a readiness*. It checks fonts, image resolution, RGB colour, total ink, hairlines, transparency, spot colours, page boxes and the output intent. The results are grouped by severity, and you can show each one on its page. Reports export as text or JSON, with page numbers, object numbers and measures, but never page text. Preflight only checks: it does not convert anything to PDF/X.
- **Colour Management**: ICC profiles in the document are honoured, and CMYK colour is shown the way Acrobat shows it.
- **Document Tabs**: several documents open at once (`Ctrl+Tab` / `Ctrl+Shift+Tab` to switch, `Ctrl+W` to close).
- **Memory-Budgeted Rendering**: a priority scheduler and byte-limited caches keep memory bounded on very large documents.
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
├── docs/pdf-viewer-vector-migration/  # Engine plan, decisions, gates and implementation status
├── eng/
│   ├── pdfium/                    # PDFium for developer and test builds (the oracle): pin, headers, licences
│   ├── releasegate/               # Startup, payload-size and PDFium-free release gates
│   ├── signing/                   # Code-signing scripts and notes
│   ├── webdemo/                   # Static server for trying the published web demo locally
│   └── vectorpdf/                 # Corpus tooling (vectorpdf), baselines and the git-ignored test corpus
├── publish/                       # Output folder for distribution (Setup & Portable EXE, SBOM)
├── samples/                       # Multi-page test documents
├── scripts/
│   ├── build_publish.ps1          # Automated 1-click build, package, SBOM & publish script
│   └── generate_sbom.ps1          # CycloneDX v1.6 and SPDX v2.3 SBOM generation script
│
├── src/
│   ├── PdfEngine.Abstractions/    # UI-neutral domain models, geometry, exceptions & contracts
│   ├── PdfEngine.Vector/          # The PDF engine: parser, fonts, text, forms, signatures, editing, export
│   ├── PdfEngine.Vector.Direct2D/ # Direct2D/DirectWrite renderer and printing
│   ├── PdfEngine.Vector.Skia/     # Portable Skia renderer (web and Android)
│   ├── PdfEngine.Vector.Windows/  # WPF fallback renderer
│   ├── PdfEngine.Pdfium/          # PDFium interop, the oracle of developer and test builds (not shipped)
│   ├── PdfViewer.Core/            # DocumentSession, RenderPriorityScheduler, MultiTierCache & Undo/Redo
│   ├── PdfViewer/                 # Modern WPF MVVM application with Light/Dark themes
│   ├── PdfViewer.Web.Engine/      # UI-free engine layer of the browser and Android viewers (open, render, search, layout)
│   ├── PdfViewer.Web/             # Browser demo: Blazor WebAssembly, the engine running in the tab
│   ├── PdfViewer.Android/         # Android proof of concept: .NET for Android, the same engine and Skia renderer
│   └── Installer/                 # Windows Graphical Setup Installer & Uninstaller
│
└── tests/
    ├── PdfEngine.Vector.Tests/    # Engine tests: unit, fixtures, PDFium-differential and fuzz (about 1,060)
    ├── PdfEngine.Vector.Skia.Tests/ # Skia backend against Direct2D; the web engine layer
    └── PdfViewer.Tests/           # Viewer tests: view models, windows, services, release gates (about 590)
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
  - **First / Last Page** toolbar buttons (or `Home` / `End`).
  - **Previous / Next Page** toolbar buttons (or `PageUp` / `PageDown`).
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
| `Ctrl + E` | Document Loaded | Edit text & images |
| `Ctrl + 4` | Document Loaded | Read mode (reflowed, screen-reader friendly) |
| `Ctrl + Shift + N` | Document Loaded | Night mode (invert pages) |
| `Ctrl + Shift + S` | Document Loaded | Save annotated copy as |
| `Ctrl + W` | Document Loaded | Close document |
| `Ctrl + Tab` / `Ctrl + Shift + Tab` | Several documents | Next / previous document tab |
| `Alt + Left` / `Alt + Right` | Document Loaded | Back / forward through jumps |
| `F11` | Document Loaded | Full screen (`Esc` or `F11` to exit) |
| `Enter` / double-click | Measuring | Finish a perimeter or area |
| `Backspace` | Measuring | Take back the last point |
| `Shift` (held) | Measuring | Place a point without snapping |
| `Esc` | Measuring / placing a stamp | Drop the measurement being drawn (again: stop measuring) / cancel the stamp |
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

Releases are signed with the project's own self-signed certificate for now. The privacy policy is also published at https://ramanacr.github.io/pdf-viewer/privacy/.

---

## Contributing

Contributions are welcome: bug reports, test files you may share, documentation and code. Read [CONTRIBUTING.md](CONTRIBUTING.md) for the set-up, the project rules and how to run the tests. Report security problems privately, as [SECURITY.md](SECURITY.md) describes.

---

## License & Third-Party Notices

- **PDF Viewer Application**: Licensed under the [MIT License](LICENSE).
- **PDF Engine**: `PdfEngine.Vector` (this project, MIT); Direct2D access via Vortice.Windows (MIT); predefined CJK CMaps from Adobe cmap-resources (BSD 3-Clause); JPEG 2000 via CoreJ2K (BSD 3-Clause). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for full license texts.
