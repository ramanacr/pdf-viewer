# PDF Viewer — Commercialization & Engineering Roadmap Agent Bundle

## Purpose

This bundle is a production-oriented assessment and implementation roadmap for:

`https://github.com/ramanacr/pdf-viewer`

It is written so that a capable coding agent can consume the documents as project instructions and progressively evolve the repository from a strong Windows-native PDF viewer into a commercially viable PDF product/platform.

## Repository snapshot assessed

- Assessment date: 2026-08-27
- Repository: `ramanacr/pdf-viewer`
- Default branch: `main`
- Application: native Windows desktop PDF viewer
- UI: C# / WPF
- Runtime: .NET 9 / Windows
- PDF engine: Google PDFium, Windows x64 native DLL
- Architecture: MVVM + service layer + native P/Invoke bridge
- Rendering: direct BGRA bitmap rendering into WPF
- Async rendering: background tasks + cancellation
- Cache: in-memory LRU page cache
- Current documented capabilities include viewing, zooming, rotation, text selection/search, bookmarks, thumbnails, annotations, image export, printing, metadata, password-protected PDFs, themes, recent files, installer/update support and SBOM generation.
- Repository currently has a relatively small public footprint: 20 commits, no open issues and no open pull requests at the assessed snapshot.

## Executive verdict

The repository is **commercially promising as a technical foundation**, but it is **not yet a commercial-grade PDF product**.

The strongest asset is the rendering foundation: PDFium is a serious native PDF engine, and the application already demonstrates several production-minded choices such as SafeHandle wrappers, direct BGRA rendering, cancellation-aware rendering, LRU caching, SBOMs and installer/update mechanics.

The biggest strategic mistake would be to continue adding toolbar features directly into the current WPF application without first creating a reusable domain/core architecture.

The recommended direction is:

1. Stabilize the current Windows viewer.
2. Extract a reusable PDF Core SDK boundary.
3. Build a first-class document model and operation/command system.
4. Add forms, signatures, redaction, page manipulation, document editing and accessibility.
5. Add OCR/AI as modular services rather than coupling them to the viewer.
6. Add telemetry/diagnostics only with explicit privacy controls.
7. Build commercial packaging, licensing, enterprise deployment and support infrastructure.
8. Only then expand to Web/macOS/Linux/mobile or an SDK offering.

## Recommended product ladder

### Product A — Free/Open Viewer

A polished Windows PDF reader:
- viewing
- search
- bookmarks
- annotations
- forms
- printing
- export
- accessibility
- secure document handling

### Product B — Pro Desktop

Paid individual/professional application:
- PDF editing
- page organization
- merge/split
- OCR
- redaction
- digital signatures
- document comparison
- optimization
- advanced export
- automation
- productivity features

### Product C — Business/Enterprise

Commercial enterprise product:
- MSI/MSIX/Intune deployment
- centralized policies
- offline licensing
- SSO/identity integration where appropriate
- audit logging
- controlled updates
- enterprise configuration
- DLP-friendly operation
- support/SLA
- signed releases
- security response process

### Product D — PDF SDK

The most defensible long-term commercial opportunity:
- reusable .NET API
- Windows native SDK
- optional WebAssembly/server engine
- viewer component
- annotation/form APIs
- document processing APIs
- licensing for embedding into third-party products

## Agent operating rule

Do not attempt a "big bang rewrite".

Use incremental vertical slices. Each roadmap phase must leave the application buildable and testable.

Before implementing a feature:
1. identify the domain capability;
2. identify the PDFium API required;
3. define an engine-neutral interface;
4. implement the PDFium adapter;
5. add unit/integration tests;
6. add UI only after the core operation works;
7. add persistence/export/import behavior;
8. add performance/security tests;
9. document the public API;
10. update release notes and SBOM if dependencies changed.

## Priority legend

- P0 = release blocker / correctness / security
- P1 = essential for commercial readiness
- P2 = high-value product capability
- P3 = strategic expansion
- P4 = optional differentiation

See the other documents for detailed implementation instructions.
