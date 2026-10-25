# Product Roadmap

## Overall horizon

The roadmap should be capability-driven rather than calendar-driven.

## Phase 0 — Stabilize the foundation

Goal: make the current viewer a dependable 1.0-class reader.

Deliver:
- native crash containment;
- document lifecycle correctness;
- deterministic disposal;
- large-document tests;
- PDF compatibility corpus;
- performance benchmarks;
- signed releases;
- secure update verification;
- installer hardening;
- accessibility baseline;
- localization infrastructure;
- structured logging;
- privacy-safe diagnostics;
- CI release gates.

Exit criteria:
- no known P0 defects;
- repeatable release build;
- signed installer;
- compatibility corpus passing;
- memory/performance budgets established.

## Phase 1 — Professional Reader

Goal: credible alternative to lightweight commercial readers.

Features:
- multi-tab workspace;
- advanced search;
- search history;
- bookmarks editing;
- stamps;
- headers/footers;
- watermarks;
- page labels;
- attachments;
- layers;
- better metadata editing;
- improved annotation tooling;
- annotation import/export;
- robust undo/redo;
- document recovery.

## Phase 2 — PDF Productivity Suite

Goal: move from reader to editor.

Features:
- page insert/delete/reorder;
- rotate persisted page geometry;
- merge PDFs;
- split PDFs;
- extract pages;
- replace pages;
- blank page insertion;
- document assembly;
- content editing;
- image insertion/replacement;
- text editing where technically reliable;
- optimization/compression;
- batch operations.

## Phase 3 — Forms + Signatures

Features:
- AcroForm discovery;
- field rendering;
- field filling;
- field appearance;
- form reset;
- import/export FDF/XFDF;
- signature fields;
- digital signing;
- certificate stores;
- signature verification;
- trust configuration;
- timestamping;
- PAdES/LTV strategy.

Treat signing as a security subsystem, not a UI feature.

## Phase 4 — Enterprise Document Security

Features:
- redaction;
- redaction audit;
- content sanitization;
- metadata removal;
- embedded-file inspection;
- JavaScript policy;
- external-link policy;
- certificate trust policy;
- enterprise configuration;
- MSI/MSIX;
- Intune deployment;
- offline activation;
- device/user licensing;
- policy locking.

## Phase 5 — OCR + Intelligent Documents

Features:
- OCR provider abstraction;
- local OCR;
- optional cloud OCR;
- searchable scanned PDFs;
- language packs;
- table extraction;
- document classification;
- key-value extraction.

## Phase 6 — AI Document Assistant

Only after the security and document model are mature.

Capabilities:
- summarize;
- ask questions about document;
- explain selected text;
- find references;
- compare clauses;
- extract structured fields;
- generate review checklists;
- locate risks;
- create annotation suggestions.

Privacy modes:

```text
Local Only
Enterprise Private
Cloud Optional
```

AI must never silently upload document contents.

## Phase 7 — SDK

Expose stable APIs:

```text
PdfEngine.Abstractions
PdfEngine.Pdfium
PdfViewer.Controls
PdfDocumentProcessor
PdfAnnotationModel
PdfFormModel
PdfSignatureModel
```

Offer:
- .NET SDK;
- Windows SDK;
- embeddable viewer control;
- headless processing CLI;
- licensing API.

## Phase 8 — Cross-platform expansion

Only after the portable core is stable.

Possible:
- WinUI;
- macOS;
- Linux;
- WebAssembly;
- browser viewer;
- server document processing.

Do not make cross-platform support the first commercial milestone.

## Phase prioritization

### Must-have before paid launch

- rendering stability;
- search;
- annotations;
- forms;
- page operations;
- merge/split;
- signatures;
- redaction;
- accessibility baseline;
- security hardening;
- installer/update security;
- support/documentation;
- licensing.

### Strong differentiators

- privacy-first local processing;
- excellent large-document performance;
- AI that works locally;
- enterprise offline deployment;
- transparent open-source engine foundation;
- SDK offering;
- developer-friendly automation API.

### Avoid early

- cloud document storage;
- team collaboration backend;
- social features;
- excessive account requirements;
- forced telemetry;
- broad office-format editing before PDF quality is excellent.

## Recommended first commercial wedge

Do not compete head-on with Acrobat on every feature.

Position around:

**Fast, private, Windows-native professional PDF work with local processing and developer-grade automation.**

Potential customer segments:
- software development organizations;
- legal teams needing local redaction/review;
- government/regulated environments;
- engineering/document-heavy organizations;
- enterprises that prohibit cloud upload;
- ISVs wanting an embeddable PDF engine/viewer.
