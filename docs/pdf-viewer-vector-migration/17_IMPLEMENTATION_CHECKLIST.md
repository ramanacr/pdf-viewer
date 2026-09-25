# Implementation Checklist

Status as of 2026-09-25. `[x]` done with tests/evidence, `[~]` partial (see note), `[ ]` open.
Details and evidence: `18_IMPLEMENTATION_STATUS.md`.

## Before coding
- [x] Capture current `main` SHA. (`2e2fdd7` before the second pass)
- [x] Run complete tests.
- [ ] Publish current release artifact and record size.
- [~] Record cold start, open, first page, zoom, scroll, memory. (open/first page/zoom/cancel in `eng/vectorpdf/baseline-bench.txt`; cold start, scroll, memory not yet)
- [ ] Select initial legal test corpus.
- [x] Create migration branch.

## Foundation
- [x] Add `PdfEngine.Vector`.
- [x] Add Windows vector backend project or isolated folder.
- [x] Add engine mode.
- [x] Add fallback reason enum.
- [x] Add diagnostic counters. (`PdfEngineMetrics`, recorded per page, `ToJsonReport()`)
- [x] Add PDFium differential harness. (`DifferentialRenderingTests`, `vectorpdf corpus --diff`)
- [x] Ensure Vector project has no Pdfium reference.

## Parser
- [x] checked arithmetic
- [x] token limits
- [x] nesting limits
- [x] classic xref
- [x] xref streams
- [x] object streams
- [x] incremental updates
- [x] cycle detection
- [x] decompression limits (bounded streaming Flate/LZW)
- [x] damaged-xref reconstruction and object-header validation

## IR
- [x] immutable
- [x] backend-neutral
- [x] glyph-position preserving
- [x] path preserving
- [x] bounds (conservative, page space)
- [x] explicit fallback
- [~] deterministic diagnostics (JSON metrics report; no display-list serializer yet)

## Renderer
- [~] Direct2D paths (WPF retained drawing instead — ADR-011, proposed)
- [~] DirectWrite glyphs (WPF `GlyphRun` from embedded TrueType/OpenType; bare CFF/Type1 fall back)
- [x] images
- [x] clips
- [x] transforms
- [~] dirty region (culling by page-space bounds implemented in the compiler; hosts do not pass a region yet)
- [~] device loss (handled inside WPF; no explicit test)
- [x] cancellation
- [x] resource eviction (display-list LRU, image cache LRU)

## Verification
- [~] 25–1600% zoom (100–1600 % benchmarked; display-list reuse tested)
- [~] no full-page bitmap enlargement (re-rasterized per zoom, never scaled; the page view still holds one bitmap per zoom)
- [x] differential render
- [x] text geometry (selection boxes vs PDFium)
- [x] malformed files
- [x] fuzz regressions
- [ ] long documents
- [~] path-heavy documents (benchmark fixture only)
- [ ] image-heavy documents
- [x] memory ceilings
- [x] existing viewer tests

## Before PDFium removal
- [ ] zero production fallback
- [ ] all feature dependencies inventoried
- [ ] encryption handled
- [ ] save/edit dependencies handled
- [ ] installer clean
- [ ] publish output clean
- [ ] SBOM clean
- [ ] notices updated
- [ ] rollback tested
