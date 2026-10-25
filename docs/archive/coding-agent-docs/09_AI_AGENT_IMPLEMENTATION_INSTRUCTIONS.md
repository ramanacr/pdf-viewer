# Coding Agent Implementation Instructions

## Mission

Evolve `ramanacr/pdf-viewer` into a production-grade, commercially viable PDF platform without destabilizing the current application.

## Non-negotiable rules

1. Never perform a big-bang rewrite.
2. Keep the repository buildable after every meaningful change.
3. Preserve existing working viewer functionality.
4. Do not bypass `IPdfDocumentService` or the future engine abstraction.
5. Never place PDFium P/Invoke code in ViewModels or Views.
6. Do not introduce WPF references into portable core projects.
7. Every native handle must have deterministic ownership.
8. Every async operation must support cancellation where meaningful.
9. Treat PDFs as hostile input.
10. Do not silently upload document content.
11. Do not add AI before security/document abstractions are ready.
12. Add tests before or with implementation.
13. Prefer vertical slices over horizontal refactors.
14. Avoid speculative abstractions with no immediate use.
15. Preserve backwards compatibility for existing public APIs unless explicitly versioning them.

## Required workflow for every task

### Step 1 — Inspect

Read:
- solution;
- project files;
- affected source;
- tests;
- native bridge;
- README;
- third-party notices.

Search for:
- all references to affected types;
- P/Invoke methods;
- WPF dependencies;
- save operations;
- threading/locking;
- UI callbacks.

### Step 2 — Design

Produce a short internal design:
- current flow;
- target flow;
- files to modify;
- interfaces;
- migration strategy;
- tests;
- risks.

### Step 3 — Implement

Implement the smallest production-ready slice.

### Step 4 — Validate

Run:
- build;
- unit tests;
- integration tests;
- targeted performance tests where relevant.

### Step 5 — Review

Check:
- disposal;
- cancellation;
- thread safety;
- exceptions;
- security;
- accessibility;
- localization;
- logging;
- compatibility.

### Step 6 — Document

Update:
- architecture docs;
- public API docs;
- README where user-visible;
- changelog/release notes.

## Refactoring order

Do not refactor everything at once.

Recommended order:

```text
1. PdfEngine.Abstractions
2. PdfEngine.Pdfium
3. Rendering model
4. DocumentSession
5. Render scheduler
6. Search service
7. Annotation service
8. Command/undo system
9. Forms
10. Signatures
11. Page operations
12. Redaction
13. OCR
14. Accessibility
15. Licensing
16. SDK
17. AI
```

## Coding standards

Use:
- nullable reference types;
- explicit cancellation;
- `IAsyncEnumerable<T>` for streaming result sets where useful;
- immutable request/response records where practical;
- dependency injection;
- structured exceptions;
- `ConfigureAwait(false)` in library code where appropriate;
- no synchronous blocking on async;
- no UI-thread native PDF work.

Avoid:
- static mutable global state;
- service locator;
- UI callbacks inside core;
- `async void` except event handlers;
- broad `catch (Exception)` without a deliberate boundary;
- unbounded concurrency;
- unbounded caches.

## Native bridge rules

All PDFium calls:
- live in `PdfEngine.Pdfium`;
- have a documented ownership model;
- use SafeHandle or equivalent;
- have tests for invalid handles;
- have tests for disposal;
- are version-checked where necessary.

## Error taxonomy

Use typed errors:

```text
PdfOpenException
PdfPasswordRequiredException
PdfCorruptDocumentException
PdfUnsupportedFeatureException
PdfNativeEngineException
PdfSaveException
PdfSecurityPolicyException
PdfSignatureException
PdfRedactionException
```

Do not make application logic parse exception message strings.

## Testing strategy

### Unit tests
Pure logic:
- coordinate transforms;
- cache keys;
- commands;
- licensing;
- page ranges;
- search models.

### Integration tests
Real PDFium:
- open;
- render;
- text;
- annotations;
- save;
- forms;
- signatures.

### Compatibility tests
Large corpus:
- generated PDFs;
- public fixture PDFs;
- customer-approved anonymized PDFs;
- malformed PDFs.

### Regression tests
Every production bug gets a fixture.

## Pull-request quality gate

A feature is not complete if any of these are missing:

- core API;
- implementation;
- tests;
- error handling;
- cancellation;
- security review;
- documentation.

## Commercialization gate

Before marking a release "commercial":

```text
P0 security issues = 0
Known native crashes = 0 without explicit waiver
Release artifacts signed = yes
SBOM generated = yes
Dependency review = yes
Compatibility corpus = pass
Large-document benchmark = pass
Installer test = pass
Update rollback test = pass
Privacy review = pass
License review = pass
```

## Agent behavior for uncertain PDFium APIs

Never invent a PDFium function.

If an API is not present in the pinned headers:
1. inspect the exact header shipped in the repository;
2. verify the native export;
3. update the PDFium version only as a deliberate dependency task;
4. add a wrapper;
5. test it.

## Agent behavior for unsupported features

If PDFium does not provide a capability cleanly:
- document the limitation;
- isolate the requirement behind an interface;
- evaluate a secondary implementation;
- do not contaminate the core architecture.

This is particularly important for:
- XFA;
- advanced digital signatures;
- OCR;
- PDF/A;
- content editing;
- comparison.

## Final architectural goal

The coding agent should converge on:

```text
Application
    |
PdfViewer.Core
    |
PdfEngine.Abstractions
    |
+-------------------+
| PdfEngine.Pdfium  |
+-------------------+
    |
PDFium
```

with future adapters:

```text
PdfEngine.Pdfium
PdfEngine.Wasm
PdfEngine.Server
PdfEngine.OptionalCommercial
```

The UI should not know which engine is underneath.
