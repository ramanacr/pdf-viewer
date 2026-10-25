# Target Architecture

## Objective

Transform the current application into a reusable PDF platform while preserving the existing WPF application.

## Target solution structure

```text
PdfViewer.slnx
|
+-- src/
|   +-- PdfEngine.Abstractions/
|   |   +-- Documents/
|   |   +-- Pages/
|   |   +-- Text/
|   |   +-- Rendering/
|   |   +-- Annotations/
|   |   +-- Forms/
|   |   +-- Signatures/
|   |   +-- Editing/
|   |   +-- Security/
|   |
|   +-- PdfEngine/
|   |   +-- DocumentSession
|   |   +-- Rendering
|   |   +-- Text
|   |   +-- Pages
|   |   +-- Annotations
|   |   +-- Forms
|   |   +-- Save
|   |
|   +-- PdfEngine.Pdfium/
|   |   +-- NativeBridge
|   |   +-- SafeHandles
|   |   +-- PdfiumDocumentAdapter
|   |
|   +-- PdfViewer.Core/
|   |   +-- DocumentSession
|   |   +-- Commands
|   |   +-- UndoRedo
|   |   +-- Search
|   |   +-- Workspace
|   |   +-- Preferences
|   |
|   +-- PdfViewer.Wpf/
|   |   +-- Views
|   |   +-- ViewModels
|   |   +-- Controls
|   |   +-- RenderingAdapters
|   |
|   +-- PdfViewer.App/
|   |
|   +-- PdfViewer.Installer/
|
+-- tests/
|   +-- PdfEngine.UnitTests/
|   +-- PdfEngine.PdfiumTests/
|   +-- PdfViewer.CoreTests/
|   +-- PdfViewer.WpfTests/
|   +-- PdfCompatibilityTests/
|   +-- PdfPerformanceTests/
|   +-- PdfSecurityTests/
|   +-- PdfFuzzRegressionTests/
|
+-- eng/
|   +-- pdfium/
|   +-- packaging/
|   +-- signing/
|   +-- compliance/
```

## Layering rules

### Layer 1 — Abstractions

Must not reference:
- WPF;
- Windows UI;
- PDFium;
- MessageBox;
- PrintDialog.

Examples:

```csharp
public interface IPdfDocument
{
    DocumentMetadata Metadata { get; }
    int PageCount { get; }
    ValueTask<PageInfo> GetPageInfoAsync(int pageNumber, CancellationToken ct);
}
```

```csharp
public interface IPdfRenderer
{
    ValueTask<RenderedPage> RenderAsync(
        RenderRequest request,
        CancellationToken cancellationToken);
}
```

### Layer 2 — Core engine

Responsible for:
- document sessions;
- page operations;
- text extraction;
- search;
- rendering requests;
- annotation operations;
- save operations;
- form operations;
- document state.

### Layer 3 — PDFium adapter

Only layer allowed to know the native ABI.

All P/Invoke declarations, native constants and SafeHandles belong here.

### Layer 4 — Application core

Responsible for:
- workspace;
- commands;
- undo/redo;
- user preferences;
- navigation;
- recent documents;
- application policies.

### Layer 5 — UI

WPF-specific implementation.

The UI should consume application/core APIs rather than call PDFium directly.

## Document session

Create a first-class `DocumentSession`.

Suggested state:

```text
DocumentSession
 ├── DocumentIdentity
 ├── Source
 ├── Metadata
 ├── PageCatalog
 ├── ViewState
 ├── AnnotationState
 ├── FormState
 ├── DirtyState
 ├── Revision
 ├── Permissions
 └── EngineSession
```

Every mutation increments a document revision.

Cache keys should include revision when output can change.

## Command architecture

Every mutating action should be represented as a command.

Examples:

```text
AddAnnotationCommand
DeleteAnnotationCommand
MoveAnnotationCommand
RotatePageCommand
DeletePageCommand
InsertPageCommand
MergeDocumentCommand
SplitDocumentCommand
FillFormFieldCommand
RedactContentCommand
SetMetadataCommand
```

Each command should provide:
- validation;
- execution;
- undo where meaningful;
- serialization for audit/recovery where appropriate;
- affected pages;
- dirty-state information.

## Rendering pipeline

Recommended pipeline:

```text
Viewport
  |
  v
RenderPriorityScheduler
  |
  +-- visible pages
  +-- near-viewport pages
  +-- thumbnails
  +-- prefetch
  |
  v
RenderRequest
  |
  v
IPdfRenderer
  |
  v
PDFium Adapter
  |
  v
Native pixel buffer
  |
  v
RenderedPage
  |
  +-- memory cache
  +-- optional disk cache
  |
  v
UI renderer adapter
```

Use priorities:
1. current visible page;
2. adjacent visible pages;
3. user-selected page;
4. near viewport;
5. thumbnails;
6. background prefetch.

## Search architecture

Do not scan every page on every query in the UI.

Introduce:

```text
ITextIndex
ITextExtractor
ISearchService
```

For large documents:
- lazily build text index;
- persist optional index;
- invalidate by document fingerprint;
- support incremental indexing;
- allow cancellation.

## Annotation architecture

Use an engine-neutral model:

```text
Annotation
  Id
  PageNumber
  Type
  Bounds
  Appearance
  Author
  Contents
  CreatedUtc
  ModifiedUtc
  QuadPoints
  InkPaths
  Flags
```

Separate:
- visual annotation;
- PDF-native annotation;
- review/comment metadata.

This allows future collaboration without corrupting PDF semantics.

## Save architecture

Introduce:

```text
IPdfSaveService
```

with explicit modes:

```text
Incremental
FullRewrite
Flattened
Optimized
Redacted
Signed
```

Do not allow arbitrary save behavior from UI code.

## Security architecture

Add:

```text
PdfSecurityPolicy
 ├── AllowJavaScript
 ├── AllowExternalLinks
 ├── AllowAttachments
 ├── AllowEmbeddedFiles
 ├── AllowLaunchActions
 ├── AllowNetworkRequests
 └── MaxDocumentSize
```

The default commercial profile should be conservative.

## Cross-platform strategy

Do not port the WPF application directly.

Instead:

```text
Portable PDF Core
      |
      +-- WPF UI
      +-- WinUI UI
      +-- WebAssembly adapter (future)
      +-- CLI processor (future)
      +-- server worker (future)
```

This is the architectural path that makes an SDK possible.
