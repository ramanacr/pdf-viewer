# Acceptance and Release Gates

## M0 gate
- [ ] Existing solution builds.
- [ ] Existing tests pass.
- [ ] Current PDFium performance/memory/package baseline captured.
- [ ] New projects compile without changing default behavior.
- [ ] Engine selection is centralized.

## Vector foundation gate
- [ ] Simple vector PDF opens without PDFium parsing.
- [ ] Paths render via Direct2D.
- [ ] Text renders as glyph runs, not re-laid-out Unicode.
- [ ] Embedded raster images remain images.
- [ ] Zoom to 1600% does not scale a full-page cached bitmap.
- [ ] Device loss is recoverable.
- [ ] cancellation works during scroll/zoom.

## Hybrid beta gate
Suggested initial thresholds; tune only with documented evidence:
- [ ] ≥99.5% corpus documents open without process failure.
- [ ] ≥99% corpus pages produce a display list or classified fallback.
- [ ] ≥95% page area across representative born-digital corpus renders natively/vector-first.
- [ ] no known P0/P1 security or corruption bugs.
- [ ] no >10% regression in median first-visible-page latency vs baseline unless justified by materially better fidelity and approved.
- [ ] warm zoom/scroll demonstrably improves or meets baseline.
- [ ] memory ceilings remain enforced.
- [ ] existing viewer workflows pass.

## PDFium-free release gate

All of:
- [ ] parser/open success target agreed from full corpus and met.
- [ ] fallback to PDFium is zero in production mode.
- [ ] all current product features that call PDFium have replacement paths or have been explicitly removed by product decision.
- [ ] encrypted-document requirements satisfied.
- [ ] annotations/forms/save/page organization/signatures/redaction requirements satisfied.
- [ ] PDFium DLL absent from publish output.
- [ ] `eng/pdfium` not required for production build.
- [ ] installer does not package PDFium.
- [ ] SBOM contains no production PDFium component.
- [ ] third-party notices updated.
- [ ] cold/warm performance gates pass.
- [ ] fuzz/security corpus passes.
- [ ] rollback plan tested.

## Rendering correctness gate

For supported features:
- no missing visible objects;
- no wrong page transforms/rotation;
- clipping correct;
- glyph placement correct within defined tolerance;
- colors within defined conversion tolerance;
- transparency/blending within defined perceptual tolerance;
- annotations/form appearances preserved according to feature ownership.

## Product claims

Before final gate, describe releases as **vector-first/hybrid** and disclose PDFium fallback internally/documentation as appropriate.

Only after package verification may the project claim **no PDFium runtime dependency**.
