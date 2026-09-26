# PDF Viewer — Vector-First Engine Migration Bundle

**Repository:** `ramanacr/pdf-viewer`  
**Baseline reviewed:** `main`, commit/tree observed 2026-09-21  
**Migration strategy:** Own PDF core + vector IR + native Windows renderer; retain PDFium as a temporary compatibility fallback and development oracle; remove it from production only after measurable compatibility gates are met.

## Product intent

The existing viewer is retained. This is **not a viewer rewrite**. The migration replaces the rendering/interpretation heart incrementally while preserving current user-facing capabilities and regression coverage.

The target is:

> A small, native-oriented, vector-first PDF engine in which PDF text and graphics remain semantic/vector primitives as long as possible; rasterization is localized to content that inherently requires it or is temporarily unsupported.

The term **vector-first** does not mean “never rasterize.” Embedded raster images, transparency groups, masks, certain blend operations, and fallback regions may legitimately use intermediate raster surfaces.

## Current repository facts used by this bundle

The repository currently contains:
- `PdfEngine.Abstractions`
- `PdfEngine.Pdfium`
- `PdfViewer.Core`
- WPF `PdfViewer`
- installer, tests, and benchmarks
- pinned PDFium runtime under `src/PdfViewer/runtimes/win-x64/native/pdfium.dll`
- PDFium engineering assets under `eng/pdfium`
- existing tests including engine, comparison/OCR, cache, annotations, security/sanitization, save, layout, and text-selection workflows
- existing `Coding Agent Docs`

The current viewer is .NET 9/WPF. Keep it during the engine migration. UI/native-shell replacement is a separate future program.

## Read order

1. `01_DECISIONS_AND_SCOPE.md`
2. `02_CURRENT_TO_TARGET_ARCHITECTURE.md`
3. `03_REPOSITORY_CHANGE_PLAN.md`
4. `04_VECTOR_IR_AND_RENDERING_CONTRACT.md`
5. `05_PDF_CORE_IMPLEMENTATION_PLAN.md`
6. `06_PDFIUM_FALLBACK_AND_EXIT_PLAN.md`
7. `07_TEST_CORPUS_AND_VERIFICATION.md`
8. `08_PERFORMANCE_MEMORY_BUDGETS.md`
9. `09_SECURITY_ROBUSTNESS.md`
10. `10_MIGRATION_MILESTONES.md`
11. `11_CODING_AGENT_EXECUTION.md`
12. `12_ACCEPTANCE_AND_RELEASE_GATES.md`
13. `13_RISK_REGISTER.md`
14. `14_ADRS.md`
15. `15_BACKLOG.md`
16. `16_REFERENCE_AND_STANDARDS.md`
17. `17_IMPLEMENTATION_CHECKLIST.md`
18. `18_IMPLEMENTATION_STATUS.md` — audit, evidence, gate status and remaining work (keep current)

## Non-negotiable principles

1. Existing viewer behavior must continue to work throughout migration.
2. `PdfEngine.Abstractions` remains UI-neutral.
3. New code must not expose PDFium handles/types above the PDFium adapter.
4. The canonical page representation for the new engine is a display list/vector IR, not a page bitmap.
5. Zoom must not mean scaling a cached full-page bitmap.
6. Unsupported constructs must be observable and classified.
7. PDFium fallback is temporary, explicit, measurable, and removable.
8. Parsing untrusted PDFs is hostile-input processing: bounded allocations, recursion, decompression, object counts, and execution time are mandatory.
9. No JavaScript execution or automatic active-content execution is introduced.
10. Every milestone begins with fail-first tests and ends with measurable gates.

## Desired end state

```text
PdfViewer (existing WPF shell)
        |
PdfViewer.Core
        |
PdfEngine.Abstractions
        |
        +---------------------------+
        |                           |
PdfEngine.Vector               PdfEngine.Pdfium
(primary)                      (temporary fallback/oracle)
        |
  PDF Core -> Display List -> Windows Vector Backend
                          -> localized raster surfaces where required
```

Final production state removes the right-hand PDFium branch after compatibility gates are achieved.
