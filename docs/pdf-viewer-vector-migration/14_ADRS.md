# Architecture Decision Records

## ADR-001 — Own the canonical PDF representation

**Status:** Accepted

**Decision:** `PdfEngine.Vector` owns parsed PDF objects, page interpretation, and display-list IR. PDFium is not the canonical page model.

**Rationale:** makes PDFium removable and allows engine-specific optimization.

**Consequence:** more initial implementation work.

---

## ADR-002 — Transitional PDFium fallback

**Status:** Accepted

**Decision:** retain PDFium during migration as fallback and reference renderer.

**Rationale:** PDF compatibility has a long tail; this permits incremental production migration without incorrect rendering.

**Exit:** see PDFium-free release gate.

---

## ADR-003 — Vector-first, not vector-only

**Status:** Accepted

**Decision:** preserve vector/text primitives where possible; use bounded raster surfaces when required by PDF semantics or migration fallback.

**Rationale:** some PDF constructs and embedded images are inherently raster/compositing-oriented.

---

## ADR-004 — Keep WPF during engine migration

**Status:** Accepted

**Decision:** do not combine engine replacement with native-shell rewrite.

**Rationale:** isolates regressions and preserves mature functionality.

---

## ADR-005 — Direct2D + DirectWrite Windows backend

**Status:** Accepted

**Decision:** use Direct2D for 2D geometry/composition and DirectWrite for glyph-level text rendering; integrate Direct3D/DXGI only where needed.

**Rationale:** native Windows hardware-accelerated rendering, high-quality geometry/text, and appropriate interoperability.

---

## ADR-006 — Glyph rendering is not Unicode re-layout

**Status:** Accepted

**Decision:** preserve PDF glyph codes/IDs, positioning, text matrices, and rendering modes. Unicode mapping is semantic metadata.

**Rationale:** PDF text layout is authored positioning; normal UI text layout can change metrics and fidelity.

---

## ADR-007 — No direct Vector → PDFium dependency

**Status:** Accepted

**Decision:** fallback is injected through abstractions/composition. `PdfEngine.Vector` must compile without `PdfEngine.Pdfium`.

**Rationale:** prevents accidental permanent coupling.

---

## ADR-008 — Reader first, writer later

**Status:** Accepted

**Decision:** first migrate parsing/rendering. Existing PDFium save/edit services remain until dedicated writer milestones.

**Rationale:** keeps scope tractable and protects current features.

---

## ADR-009 — Dependency independence is pragmatic

**Status:** Accepted

**Decision:** remove mandatory PDFium dependency, but allow vetted platform/libraries for codecs, compression, and cryptography.

**Rationale:** rebuilding security-critical commodity primitives adds risk without differentiation.

---

## ADR-010 — Corpus-driven compatibility

**Status:** Accepted

**Decision:** prioritize features by representative corpus/fallback telemetry, not by easiest implementation order after foundation.

**Rationale:** maximizes real-world coverage and creates objective PDFium exit evidence.

---

## ADR-011 — WPF retained drawing as the interim Windows backend

**Status:** Proposed (needs product-owner decision; see 18_IMPLEMENTATION_STATUS.md §6)

**Context:** ADR-005 selects Direct2D + DirectWrite. The first implementation pass built the backend on WPF (`DrawingVisual`, `StreamGeometry`, `GlyphRun`, `RenderTargetBitmap`) while describing it as Direct2D. WPF's composition engine is Direct3D-based, but `RenderTargetBitmap` rasterizes in software, and measured first-render latency is 2.4× PDFium's (eng/vectorpdf/baseline-bench.txt).

**Decision (proposed):** keep WPF retained drawing as the interim backend, behind `IPdfVectorRenderer`, and make the vector *host* display the frozen page `Drawing` directly (hardware-composed, no full-page raster per zoom) before investing in Direct2D. Revisit Direct2D when (a) the host is in place and latency still misses the gate, or (b) a non-WPF shell is started.

**Rationale:** the host removes the dominant cost (software rasterization) for on-screen viewing without a COM interop layer; the display-list/IR boundary keeps a later Direct2D backend a replacement of one project, not a rewrite.

**Consequences:** product text must not claim Direct2D/DirectWrite; glyph rendering uses WPF `GlyphTypeface` (TrueType/OpenType only — bare CFF/Type1 need wrapping or fall back per region); device loss is handled by WPF, not by our code; print/export still need an off-screen rasterizer.

