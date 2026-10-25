---
title: Architecture
permalink: /architecture/
---

# Architecture

The project has one PDF engine, `PdfEngine.Vector`, written in pure C#. The engine turns each page
into a device-independent **display list**. A renderer replays that list on a platform:

| Renderer | Platform | Used by |
| --- | --- | --- |
| `PdfEngine.Vector.Direct2D` | Windows: Direct2D and DirectWrite, GPU or WARP | The Windows app (screen and print) |
| `PdfEngine.Vector.Windows` | Windows: WPF | The Windows app, as the fallback renderer |
| `PdfEngine.Vector.Skia` | Any: SkiaSharp CPU raster | The web demo, the Android app, and library users |

A new platform adds a renderer and uses the engine as it is. The published Windows app contains
no PDFium. Developer and test builds keep PDFium as the reference that the engine is compared
with (see [Build from source](building.md#pdfium-in-developer-and-test-builds)).

## Windows app layers

The app uses the Model-View-ViewModel (MVVM) pattern with `CommunityToolkit.Mvvm`.

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           WPF UI layer (.NET 9)                         │
│  • MainWindow.xaml (menus, toolbar, sidebar, tabs, light and dark)      │
│  • MainViewModel (navigation, search, annotations, zoom)                │
│  • WpfBitmapAdapter (RenderedPage BGRA -> BitmapSource, no copy)        │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │
┌────────────────────────────────────▼────────────────────────────────────┐
│                    PdfViewer.Core (platform services)                   │
│  • DocumentSession (identity, SHA-256 fingerprint, revision, dirty)     │
│  • RenderPriorityScheduler (deduplication, priority queue, cancel)      │
│  • MultiTierCache (memory-budgeted LRU with a byte ceiling)             │
│  • CommandHistory (undo and redo)                                       │
│  • PdfSecurityPolicy (links, JavaScript, execution limits)              │
│  • PdfComparisonService (text and pixel comparison)                     │
│  • DefaultOcrEngine (Windows OCR)                                       │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │ engine contracts
┌────────────────────────────────────▼────────────────────────────────────┐
│               PdfEngine.Abstractions (UI-neutral contracts)             │
│  • IPdfEngine, IPdfDocument, IPdfPage, IPdfRenderer                     │
│  • IPdfTextService, IPdfAnnotationService, IPdfSaveService              │
│  • IPdfPageOrganizerService, IPdfFormService, IPdfSignatureService      │
│  • IPdfRedactionService; typed exceptions (PdfOpenException, ...)       │
└────────────────────────────────────▲────────────────────────────────────┘
                                     │ implementation
┌────────────────────────────────────▼────────────────────────────────────┐
│                PdfEngine.Vector (the PDF engine, managed)               │
│  • parser, xref repair, security handlers (RC4, AES, certificates)      │
│  • fonts, text page, search, structure tree, display lists              │
│  • forms, annotations, signatures, redaction, saving, page assembly     │
│  • drawn by PdfEngine.Vector.Direct2D or PdfEngine.Vector.Windows       │
└─────────────────────────────────────────────────────────────────────────┘
```

## Design points

1. **No intermediate image encoding.** Direct2D draws pages into BGRA buffers, or into a texture
   that is shared with the screen for zoomed detail. The buffers become frozen WPF
   `BitmapSource` objects, with no PNG or JPEG step.
2. **Managed engine, small native surface.** The parser and every document service are managed
   code with resource limits (`PdfSecurityLimits`). Native code is only Windows' own Direct2D,
   DirectWrite and WIC. Their COM objects are released deterministically, also on cancellation
   and on exceptions.
3. **Bounded memory.** `LruPageCache` keeps the rendered bitmaps of recent pages and DPIs, within
   a page count and a byte ceiling. Older pages are evicted, so a 500-page document does not fill
   memory.
4. **Cancellation.** When you zoom or scroll fast, `CancellationTokenSource` cancels the renders
   that are no longer needed.
5. **Sharp at every zoom (25 % to 1600 %).** When a page is zoomed past its bitmap, the visible
   area is drawn again at device resolution as a detail tile. Night mode inverts the page and its
   tiles on the GPU.

## Web demo and Android

`PdfViewer.Web.Engine` is a UI-free layer (open, render, search, layout, fonts) on the engine and
the Skia renderer. The web demo (Blazor WebAssembly) and the Android app (.NET for Android) both
use it. See the
[web README](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Web/README.md) and the
[Android README](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Android/README.md).

## Engine design record

The plan, the decisions (ADRs) and the gates of the move from PDFium to the own engine are in
[docs/pdf-viewer-vector-migration/](pdf-viewer-vector-migration/00_README.md). The current status
and the measured evidence are in
[18_IMPLEMENTATION_STATUS.md](pdf-viewer-vector-migration/18_IMPLEMENTATION_STATUS.md).
