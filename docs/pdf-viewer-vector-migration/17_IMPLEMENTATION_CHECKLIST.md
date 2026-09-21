# Implementation Checklist

## Before coding
- [ ] Capture current `main` SHA.
- [ ] Run complete tests.
- [ ] Publish current release artifact and record size.
- [ ] Record cold start, open, first page, zoom, scroll, memory.
- [ ] Select initial legal test corpus.
- [ ] Create migration branch.

## Foundation
- [ ] Add `PdfEngine.Vector`.
- [ ] Add Windows vector backend project or isolated folder.
- [ ] Add engine mode.
- [ ] Add fallback reason enum.
- [ ] Add diagnostic counters.
- [ ] Add PDFium differential harness.
- [ ] Ensure Vector project has no Pdfium reference.

## Parser
- [ ] checked arithmetic
- [ ] token limits
- [ ] nesting limits
- [ ] classic xref
- [ ] xref streams
- [ ] object streams
- [ ] incremental updates
- [ ] cycle detection
- [ ] decompression limits

## IR
- [ ] immutable
- [ ] backend-neutral
- [ ] glyph-position preserving
- [ ] path preserving
- [ ] bounds
- [ ] explicit fallback
- [ ] deterministic diagnostics

## Renderer
- [ ] Direct2D paths
- [ ] DirectWrite glyphs
- [ ] images
- [ ] clips
- [ ] transforms
- [ ] dirty region
- [ ] device loss
- [ ] cancellation
- [ ] resource eviction

## Verification
- [ ] 25–1600% zoom
- [ ] no full-page bitmap enlargement
- [ ] differential render
- [ ] text geometry
- [ ] malformed files
- [ ] fuzz regressions
- [ ] long documents
- [ ] path-heavy documents
- [ ] image-heavy documents
- [ ] memory ceilings
- [ ] existing viewer tests

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
