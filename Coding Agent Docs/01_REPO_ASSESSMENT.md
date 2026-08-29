# Repository Assessment

## 1. What is already good

The current repository is substantially beyond a toy PDF viewer.

### Rendering foundation

The application uses PDFium through a dedicated native bridge and service abstraction. The service directly renders PDFium BGRA buffers into frozen WPF `BitmapSource` instances rather than encoding intermediate PNG/JPEG files.

This is a good performance-oriented design.

### Resource lifetime

The code uses dedicated SafeHandle-style wrappers for native PDFium resources. This is the correct direction for a native engine embedded in .NET.

### Rendering pipeline

The project already has:
- asynchronous page rendering;
- cancellation tokens;
- an LRU page cache;
- viewport-oriented rendering;
- thumbnail loading;
- single-page and continuous modes.

This gives the product a credible foundation for large-document navigation.

### Search/text model

The current implementation extracts text, character boxes and normalized coordinates. Search results contain page and geometry information, which creates a useful basis for:
- search highlighting;
- text selection;
- annotation creation;
- future document intelligence.

### Annotation support

The viewer already has a meaningful annotation layer:
- highlight;
- underline;
- strikeout;
- note;
- free text;
- rectangle;
- ellipse;
- ink.

The service also supports loading existing annotations and multiple save modes.

### Productization already started

The repository includes:
- a graphical installer;
- a portable executable path;
- update checking;
- SBOM files;
- third-party notices;
- PDFium version/checksum tooling;
- build/publish scripts;
- application icons;
- sample PDF;
- automated tests.

Those are unusually useful foundations for an early-stage project.

## 2. Architecture observed

Current high-level structure:

WPF UI
→ ViewModels
→ service abstractions
→ PDFium document service
→ native bridge
→ PDFium DLL

The main application ViewModel currently owns a large amount of orchestration:
- document loading;
- page navigation;
- zoom;
- search;
- annotations;
- recent files;
- updates;
- rendering coordination;
- UI callback delegates.

This is acceptable for a prototype/product v0.x, but it will become a scaling constraint.

## 3. Current architecture strengths

### Strength 1 — engine boundary exists

`IPdfDocumentService` is a valuable seam.

Preserve it, but evolve it into a more granular engine abstraction.

### Strength 2 — native bridge is isolated

The P/Invoke/native-library concern is separated from most UI code.

Keep all ABI knowledge below the core engine boundary.

### Strength 3 — rendering and cache are explicit services

`AsyncPageRenderer` and `LruPageCache` make the rendering subsystem easier to optimize independently.

### Strength 4 — tests exercise real PDFium behavior

The tests cover document loading, metadata, bookmarks, text search, rendering, rotations, multipage rendering, cache behavior, export, corruption handling and update/version behavior.

That is a good base for a PDF regression suite.

## 4. Main architectural liabilities

### Liability A — WPF types leak into the core service

The PDF document service returns `BitmapSource` and accepts WPF `PrintDialog`.

This prevents the core PDF capability from becoming:
- reusable by WinUI;
- reusable by another desktop UI;
- usable by a server process;
- testable without WPF;
- exposed as a clean SDK.

**Required direction:**

Core PDF services should return engine-neutral representations such as:

```text
RenderedPage
  Width
  Height
  PixelFormat
  Stride
  MemoryOwner<byte>
  DPI
```

The WPF adapter converts that representation to `BitmapSource`.

### Liability B — MainViewModel is becoming a god object

The ViewModel contains too many responsibilities.

Split into:
- DocumentSessionService
- NavigationController
- SearchController
- AnnotationController
- RenderCoordinator
- RecentDocumentsService
- UpdateService
- UserPreferencesService
- Command/Undo service
- Workspace/session state

The ViewModel should mostly compose observable state.

### Liability C — synchronization is too coarse

The PDFium service uses a document lock around substantial operations. Rendering/searching/extraction can therefore serialize work.

For a commercial application, introduce a document-session concurrency model.

Recommended first implementation:
- one isolated PDFium document session per open document;
- serialized mutation operations;
- controlled concurrent read/render operations if verified safe for the chosen PDFium build;
- separate thumbnail/render queues;
- cancellation and priority scheduling.

Do not assume PDFium thread safety. Validate the exact native build and API usage.

### Liability D — entire PDFs are loaded into memory

`File.ReadAllBytes` plus an unmanaged copy means a document can exist in multiple memory representations.

For ordinary files this is acceptable, but it becomes problematic for:
- very large PDFs;
- huge portfolios;
- enterprise batch processing;
- memory-constrained systems.

The roadmap should introduce streaming/file-backed loading where the engine supports it, or a bounded native data provider.

### Liability E — cache is based primarily on rendered bitmaps

A commercial renderer needs multiple cache tiers:

1. document/page metadata cache;
2. text cache;
3. thumbnail cache;
4. rendered bitmap cache;
5. optional disk cache;
6. invalidation keyed by document generation + page + zoom bucket + rotation + color mode.

### Liability F — UI and domain state are tightly coupled

Future features such as forms, signatures, redaction, comparison and editing will become difficult if every operation is implemented directly through UI event handlers.

Introduce domain commands and immutable operation descriptors.

## 5. Important current limitations

The repository should not be marketed as an Acrobat/Foxit-class editor yet.

Major missing or incomplete product areas include:

- AcroForm editing/filling;
- XFA forms;
- robust form field appearance handling;
- digital signature creation;
- signature validation;
- certificate management;
- PAdES/LTV workflows;
- redaction with verification;
- PDF content editing;
- page insert/delete/reorder/rotate persistence as a product workflow;
- merge/split;
- attachment management;
- layer management;
- document comparison;
- OCR;
- PDF/A validation/conversion;
- accessibility/tag-tree workflows;
- reflow;
- advanced metadata editing;
- header/footer;
- watermarks;
- stamps;
- measurement tools;
- optimization/compression;
- JavaScript policy controls;
- enterprise policy management;
- robust crash reporting/diagnostics;
- localization;
- high-confidence accessibility testing;
- broad document compatibility corpus;
- x86/ARM64 strategy;
- cross-platform core;
- commercial licensing infrastructure.

These should be treated as product roadmap gaps, not necessarily defects in the current viewer.

## 6. Strategic conclusion

The repository should evolve from:

`WPF Application containing PDF functionality`

into:

`PDF Platform Core + PDFium Adapter + UI Applications + Optional Commercial Services`

That architectural shift is the key to commercialization.
