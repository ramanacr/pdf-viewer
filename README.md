# Native Windows PDF Viewer

A high-performance, feature-rich Windows desktop native PDF Viewer application built with **C# / WPF on .NET 9** and powered by the **Aspose.Pdf for .NET** engine.

The solution is structured using the modern XML-based **`.slnx`** solution format and includes automatic **`Aspose.Total.lic`** license discovery, asynchronous background page rendering with an in-memory **LRU cache**, comprehensive text searching, bookmark tree exploration, high-DPI image export, native Windows printing, and Light/Dark themes.

---

## Table of Contents

- [Architectural Overview](#architectural-overview)
- [Key Features & Capabilities](#key-features--capabilities)
- [Aspose License Configuration](#aspose-license-configuration)
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
  - [Searching Text](#searching-text)
  - [Navigating Bookmarks & Thumbnails](#navigating-bookmarks--thumbnails)
  - [Exporting Pages to Images](#exporting-pages-to-images)
  - [Printing Documents](#printing-documents)
  - [Inspecting Document Properties](#inspecting-document-properties)
  - [Switching Themes (Light / Dark)](#switching-themes-light--dark)
  - [Handling Password-Protected PDFs](#handling-password-protected-pdfs)
- [Comprehensive Keyboard Shortcuts](#comprehensive-keyboard-shortcuts)
- [Troubleshooting & FAQ](#troubleshooting--faq)

---

## Architectural Overview

The application follows the **Model-View-ViewModel (MVVM)** architectural pattern using `CommunityToolkit.Mvvm`, separating the user interface, business logic, and rendering engine.

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           WPF UI Layer (.NET 9)                         │
│   ┌─────────────────────────────────────────────────────────────────┐   │
│   │ MainWindow.xaml (Ribbon Toolbar, Menus, Status Bar, Themes)    │   │
│   ├───────────────────────────────┬─────────────────────────────────┤   │
│   │ Left Sidebar (TabControl)     │ Central Document Viewport       │   │
│   │  • Pages (Thumbnails)         │  • Continuous Virtualized View  │   │
│   │  • Bookmarks (TreeView)       │  • Single Page Paginated View   │   │
│   │  • Search Results (ListBox)   │  • Pan & Zoom Gesture Engine    │   │
│   └───────────────────────────────┴─────────────────────────────────┘   │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │ Data Binding & RelayCommands
┌────────────────────────────────────▼────────────────────────────────────┐
│                        MVVM ViewModels Layer                            │
│  • MainViewModel (State, Navigation, Search, File I/O, Zoom, Rotation) │
│  • PageViewModel (Per-Page Dimensions, Scale, Render Binding, Cache)   │
│  • ThumbnailViewModel (Asynchronous Sidebar Thumbnail Loader)          │
│  • ThemeManager (Dynamic Light/Dark ResourceDictionary Switching)       │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │ Async Pipeline & CancellationToken
┌────────────────────────────────────▼────────────────────────────────────┐
│                       Core Services & Engine                            │
│  ┌───────────────────────┐  ┌────────────────────────────────────────┐  │
│  │    LruPageCache.cs    │  │          AsyncPageRenderer.cs          │  │
│  │ (Thread-Safe Bitmap   │◄─┤ (Background Thread Pool Worker Queue,  │  │
│  │  Memory LRU Cache)    │  │  CancellationToken Cancellation)       │  │
│  └───────────────────────┘  └───────────────────┬────────────────────┘  │
│                                                 ▼                       │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                       PdfDocumentService.cs                       │  │
│  │  • Document Loading & Decryption (Aspose.Pdf.Document)            │  │
│  │  • Rendering (PngDevice, Resolution, Frozen BitmapSource)         │  │
│  │  • Text Search (Aspose.Pdf.Text.TextFragmentAbsorber)             │  │
│  │  • Outline Extraction (OutlineCollection / FitExplicitDestination) │  │
│  │  • Image Export (PngDevice / JpegDevice Batch Exporter)           │  │
│  │  • Document Printing (WPF PrintDialog + AsposePdfPaginator)       │  │
│  └───────────────────────────────────┬───────────────────────────────┘  │
│                                      ▼                                  │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                         LicenseService.cs                         │  │
│  │  • Auto-detects Aspose.Total.lic (Working Dir / Parent / Embedded)│  │
│  │  • Activates Aspose.Pdf.License on Application Startup            │  │
│  └───────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────┘
```

### Key Architectural Strengths

1. **Non-Blocking Background Page Rasterization**:
   Pages are rendered using `Aspose.Pdf.Devices.PngDevice` inside worker threads. Each generated `BitmapImage` is immediately **frozen** (`bitmap.Freeze()`), making it thread-safe and transferable to the WPF UI thread with zero dispatch latency.
2. **Thread-Safe LRU Memory Cache**:
   The `LruPageCache` maintains an in-memory linked list and hash map of rendered bitmaps for recently viewed pages and DPIs. When navigating large 500+ page documents, older pages are evicted automatically, keeping the application's memory footprint bounded.
3. **Cancellation-Aware Scrolling & Zooming**:
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
- **In-Document Text Search**:
  - Real-time searching powered by `Aspose.Pdf.Text.TextFragmentAbsorber`.
  - Case-sensitive / case-insensitive search toggle.
  - Match occurrence counter (e.g. *"Match 2 of 14 (Page 3)"*).
  - Next match (`F3`) and Previous match (`Shift+F3`) navigation.
  - Dedicated **Search Results Tab** in the sidebar listing all occurrences with text snippets and page numbers for double-click navigation.
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
- **Native Windows Printing**:
  - Integrated with the native WPF `PrintDialog` (`Ctrl+P`).
  - High-resolution (300 DPI) paginated printing to physical printers or Microsoft Print to PDF.
  - Supports All Pages or Custom Page Ranges.
- **Document Properties & Metadata**:
  - Detailed inspector dialog (`Ctrl+D`) displaying File Name, Full Path, File Size, Title, Author, Subject, Keywords, Creator, Producer, Creation Date, Modification Date, PDF Version, Page Count, Page Dimensions (points and inches), Encryption Status, Linearization, and Aspose License Status.
- **Encrypted PDF Support**:
  - Automatically detects password-protected documents and prompts the user with a clean unlock dialog.
- **Modern Light & Dark Themes**:
  - Instant theme switching between Modern Clean Light mode and Dark mode.
- **Drag-and-Drop & Recent Files**:
  - Drag and drop any `.pdf` file from Windows Explorer into the application window to open immediately.
  - Automatically records and persists recently opened documents across application restarts.

---

## Aspose License Configuration

The application includes full support for the provided `Aspose.Total.lic` license file.

### How License Discovery Works

The `LicenseService` initializes on startup (`App.xaml.cs`) and searches for the license in the following order:

1. Application runtime directory (`AppDomain.CurrentDomain.BaseDirectory\Aspose.Total.lic`).
2. Repository / Workspace root directory.
3. Embedded Assembly Resource (the project embeds `Aspose.Total.lic` directly into `PdfViewer.dll`).
4. Working directory fallback.

When a valid license is detected:
- Evaluation watermarks and 4-page limits are **completely lifted**.
- License status is displayed in the bottom status bar, Document Properties dialog, and About dialog.

---

## Prerequisites & System Requirements

- **Operating System**: Windows 10 (version 1809 or higher) / Windows 11 (64-bit).
- **.NET Runtime / SDK**: [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (SDK `9.0.100` or higher; .NET 10 preview also supported).
- **IDE (Optional)**: Visual Studio 2022 (v17.12 or higher with modern `.slnx` support) or Visual Studio Code with the *C# Dev Kit* extension.

---

## Project & Directory Structure

```
d:\Practice\pdf-viewer\
│
├── Aspose.Total.lic               # Aspose.Total license file
├── PdfViewer.slnx                 # Modern XML-based Solution file (.NET 9+)
├── README.md                      # Complete documentation and usage guide
│
├── publish/                       # Output folder for distribution
│   ├── PdfViewerSetup.exe         # Windows Installable Setup Executable (with inbuilt license)
│   ├── PdfViewer.exe              # Standalone Portable Single-File Executable (with inbuilt license)
│   └── SampleDocument.pdf         # Demo Test Document
│
├── samples/
│   └── SampleDocument.pdf         # Multi-page test document with bookmarks and tables
│
├── scripts/
│   └── build_publish.ps1          # Automated 1-click build, package & publish script
│
├── src/
│   ├── Installer/                 # Windows Graphical Setup Installer & Uninstaller
│   │   ├── PdfViewerInstaller.csproj # Setup project packaging Payload.zip
│   │   ├── App.xaml / App.xaml.cs   # Silent / GUI / Uninstall CLI dispatcher
│   │   ├── InstallService.cs        # Extraction, Shortcuts, Registry & File associations
│   │   └── InstallerWindow.xaml (.cs) # Modern WPF installer interface
│   │
│   └── PdfViewer/
│       ├── PdfViewer.csproj       # WPF Application project file (net9.0-windows)
│       ├── App.xaml               # Application entry point & resource definitions
│       ├── App.xaml.cs            # License startup initialization & CLI arguments
│       ├── SamplePdfGenerator.cs  # Demo PDF generator for test verification
│       │
│       ├── Converters/            # Data-binding converters
│       │   └── CommonConverters.cs # BoolToVis, NullToVis, EnumToBool, EnumToVis
│       │
│       ├── Models/                # Data structures
│       │   ├── BookmarkItem.cs    # Bookmark/outline tree node model
│       │   ├── DocumentMetadata.cs# PDF metadata & technical properties
│       │   ├── PageRenderResult.cs# Rendered bitmap payload & dimensions
│       │   └── SearchMatch.cs     # Text search match coordinates & snippet
│       │
│       ├── Services/              # Engine & backend services
│       │   ├── AsyncPageRenderer.cs # Multi-threaded background render coordinator
│       │   ├── LicenseService.cs  # Aspose license loader and status reporter
│       │   ├── LruPageCache.cs    # Thread-safe LRU bitmap cache
│       │   ├── PdfDocumentService.cs # Aspose.Pdf engine wrapper (Render, Search, Print, Export)
│       │   ├── RecentFilesService.cs# Local JSON persistence for recent files
│       │   └── ThemeManager.cs    # Light/Dark dynamic theme switcher
│       │
│       ├── Themes/                # XAML styles and themes
│       │   ├── Controls.xaml      # Modern buttons, scrollbars, tabs, sliders
│       │   ├── DarkTheme.xaml     # Dark theme color palette
│       │   ├── LightTheme.xaml    # Light theme color palette
│       │   └── VectorIcons.xaml   # Fluent/Material vector path geometries
│       │
│       ├── ViewModels/            # MVVM ViewModels
│       │   ├── MainViewModel.cs   # Main application state & command handlers
│       │   ├── PageViewModel.cs   # Individual viewport page representation
│       │   └── ThumbnailViewModel.cs # Sidebar thumbnail item representation
│       │
│       └── Views/                 # WPF User Interface Views & Dialogs
│           ├── MainWindow.xaml    # Main window with ribbon, sidebar, and viewport
│           ├── MainWindow.xaml.cs # View interactions (panning, zoom, drag-drop)
│           └── Dialogs/
│               ├── ExportImagesDialog.xaml (.cs) # Image export configuration
│               ├── PasswordDialog.xaml (.cs)     # Password prompt dialog
│               └── PropertiesDialog.xaml (.cs)   # Document metadata dialog
│
└── tests/
    └── PdfViewer.Tests/
        ├── PdfViewer.Tests.csproj # xUnit test project
        └── PdfServiceTests.cs     # 10 comprehensive unit & integration tests
```

---

## Building & Publishing

### 1-Click Automated Build & Package Script

To build the entire solution, bundle the inbuilt Aspose license, and generate both the **Setup Installer** and **Standalone Executable** into the `publish/` folder:

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\build_publish.ps1
```

This generates:
- `publish\PdfViewerSetup.exe`: Native Windows Installable Setup wizard with Desktop shortcut, Start Menu shortcut, and Windows Add/Remove Programs uninstaller.
- `publish\PdfViewer.exe`: Portable single-file standalone executable with all binaries and inbuilt Aspose license.
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

To execute the automated xUnit test suite covering Aspose licensing (including embedded resource verification), page rendering, bookmarks, search, LRU caching, image export, and encrypted PDFs:

```powershell
dotnet test PdfViewer.slnx
```

---

## User Guide & Workflows

### Opening Documents & Drag-and-Drop

1. **File Dialog**: Click the **Open** icon on the ribbon toolbar or press `Ctrl + O`.
2. **Drag and Drop**: Drag any `.pdf` file from Windows Explorer and drop it onto the viewer window.
3. **Recent Files**: Select **File > Open Recent** from the top menu or choose a document from the *Recently Opened* list on the welcome screen.
4. **Sample Demo**: Click **Open Sample Demo** on the welcome screen or select **File > Open Sample Demo Document** to test all features on a pre-packaged sample document.

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

### Searching Text

1. Press `Ctrl + F` or click the **Find** icon on the toolbar to open the search bar.
2. Type your search query and press `Enter` (or click **Search**).
3. Toggle the **Match Case** checkbox if exact casing is required.
4. Use `F3` (Next Match) or `Shift + F3` (Previous Match) to navigate through occurrences.
5. In the left sidebar, click the **Search Results** tab to see a full list of matching snippets with page numbers. Double-click any result to jump directly to it.

### Navigating Bookmarks & Thumbnails

- **Sidebar Toggle**: Click the **Sidebar** icon or press `Ctrl + B` to collapse or expand the navigation panel.
- **Pages Tab (Thumbnails)**: Displays mini-previews of every page. The active page is highlighted with an accent border. Click any thumbnail to jump to that page.
- **Bookmarks Tab (TOC)**: Displays the document outline hierarchy. Expand/collapse tree branches and click any section title to jump to the corresponding page.

### Exporting Pages to Images

1. Click the **Export** icon on the toolbar or select **File > Export to Images...**.
2. Configure your export options in the dialog:
   - **Output Folder**: Choose where to save the images.
   - **File Prefix**: Set a prefix for the generated files (e.g. `report_page_001.png`).
   - **Format**: Select **PNG (*.png)** or **JPEG (*.jpg)**.
   - **Resolution (DPI)**: Choose **150 DPI** (Standard), **300 DPI** (High Quality), or **600 DPI** (Ultra Sharp).
   - **Page Range**: Choose **All Pages**, **Current Page Only**, or a **Custom Range** (e.g. pages 1 to 5).
3. Click **Export**. The bottom status bar shows live progress percentage until completion.

### Printing Documents

1. Click the **Print** icon on the toolbar or press `Ctrl + P`.
2. The native Windows Print Dialog will appear, allowing you to select a physical printer or *Microsoft Print to PDF*.
3. Choose to print **All Pages** or a **Page Range**.
4. Click **Print** to send the high-resolution vector/raster pages to the print spooler.

### Inspecting Document Properties

1. Click the **Properties (Info)** icon on the toolbar or press `Ctrl + D`.
2. The Properties Dialog provides comprehensive technical details:
   - **File Details**: Name, location, formatted file size.
   - **Document Info**: Title, author, subject, keywords, creator application, producer engine, creation & modification timestamps.
   - **Page Specs**: Page count, point dimensions, and inch dimensions.
   - **Engine Details**: PDF format version, encryption flag, Fast Web View (linearized) flag, and Aspose.Total license status.

### Switching Themes (Light / Dark)

- Click the **Theme** moon/sun icon on the toolbar or select **View > Toggle Light / Dark Theme**.
- The entire interface (toolbar, sidebar, menus, dialogs, background viewport) dynamically updates its resource brushes with high-contrast readable styling.

### Handling Password-Protected PDFs

- When opening an encrypted document, the viewer automatically detects security locks and displays the **Password Required** dialog.
- Enter the password and click **Open**. If the password is correct, the document unlocks and renders normally.

---

## Comprehensive Keyboard Shortcuts

| Shortcut | Context | Action |
| :--- | :--- | :--- |
| `Ctrl + O` | Global | Open PDF document via file browser |
| `Ctrl + P` | Document Loaded | Open native Windows Print dialog |
| `Ctrl + F` | Document Loaded | Toggle in-document Text Search bar |
| `F3` | Search Active | Jump to next search match |
| `Shift + F3` | Search Active | Jump to previous search match |
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

### 1. Where should I put my Aspose.Total license?
Place `Aspose.Total.lic` directly in the project root (`d:\Practice\pdf-viewer\Aspose.Total.lic`) or in the output folder next to `PdfViewer.exe`. The project file is also pre-configured to embed `Aspose.Total.lic` as a resource inside `PdfViewer.dll`, so the license remains bundled even if the executable is moved.

### 2. How can I verify that the license is active?
Open any PDF in the application and look at the bottom right corner of the status bar. It displays `Aspose.Total license active`. You can also press `Ctrl + D` to inspect the license status line in the Properties dialog, or select **Help > Aspose License & About**.

### 3. How does the application handle very large PDF files (e.g. 500+ pages)?
The application does not rasterize all pages into memory at once. It uses the `LruPageCache` to maintain only currently visible and nearby pages in memory at a bounded limit (default 60 pages). As you scroll through the document, pages behind you are evicted and new pages are rendered asynchronously on background threads.

### 4. Can I build with Visual Studio?
Yes. Simply open `PdfViewer.slnx` in **Visual Studio 2022 (v17.12 or newer)**. Visual Studio natively recognizes `.slnx` solution files with full IntelliSense, debugging, and test runner support.

---

## License

This application is built for native Windows PDF viewing and requires a valid Aspose license for unrestricted commercial use. Powered by **Aspose.Pdf for .NET** and **.NET 9**.
