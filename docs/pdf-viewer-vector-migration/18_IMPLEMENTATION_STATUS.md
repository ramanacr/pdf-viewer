# Implementation Status and Audit

**Last updated:** 2026-09-25 · branch `feat/vector-migration-gaps`
**Scope of this document:** what the first implementation pass (commits `b0ab63c` … `2e2fdd7`, 2026-09-21) delivered against this bundle, what it got wrong, what the second pass fixed, and what remains — with evidence, not claims.

---

## 1. Audit of the first pass

### What it did well

- **The seams are right.** `PdfEngine.Vector` and `PdfEngine.Vector.Windows` exist, are in `PdfViewer.slnx`, and `PdfEngine.Vector` has no reference to `PdfEngine.Pdfium` (ADR-007). PDFium is referenced only by the test project, as an oracle.
- **Canonical IR exists.** An immutable `PdfDisplayList` with a typed command hierarchy, a stable `PdfFallbackReason` enum and a `PdfFallbackToken` carrying no PDFium types (04, 06).
- **Engine selection is centralized** in `PdfDocumentServiceFactory` with `PDF_ENGINE_MODE`, and the UI exposes Auto / Vector / PDFium plus a per-page engine badge.
- **Parser foundations:** lexer, parser, classic xref + xref streams + object streams, `/Prev` chains with cycle guard, page-tree inheritance, Flate/ASCIIHex/ASCII85/RunLength with predictors, a `PdfSecurityLimits` class, outline extraction.
- **Non-rendering features left on PDFium** as ADR-008 requires; existing viewer tests were kept green.

### What it got wrong (severity-ordered)

| # | Gap | Why it matters | Plan reference |
|---|---|---|---|
| 1 | **Silent wrong output in the default mode.** `Auto` rendered through the vector path whenever the display list had no fallback tokens, but many constructs were silently dropped or approximated without a token: Form XObjects were a stub (`// Execute nested form stream` — never executed), inline images were tokenized as garbage, text render mode 3 (invisible OCR text) was painted, soft masks and blend modes only set a feature flag, unknown colour spaces defaulted to DeviceRGB, unsupported filters returned raw bytes, annotation appearances were never drawn (PDFium renders them). | "vector mode produces silent wrong output" is a stop condition. | 13 stop conditions; 06; 11 "Error policy" |
| 2 | **Text was re-laid-out Unicode.** Glyphs were drawn with `FormattedText` in a guessed system font, ignoring embedded font programs and glyph IDs. | Violates ADR-006 directly. | 04 "Text rule", ADR-006 |
| 3 | **"Direct2D / DirectWrite" was claimed but not built.** The renderer is WPF `DrawingVisual` + `RenderTargetBitmap` (software rasterizer). Tooltips and the showcase PDF advertised DirectX/Direct2D/GPU tessellation. | Misleading product claims. | 12 "Product claims", ADR-005 |
| 4 | **Rendering bugs:** image drawn with the CTM applied twice and vertically flipped; only raw RGB24 images handled (gray, CMYK, indexed, 1–16 bpc, `/Decode`, masks, SMask ignored); page `/Rotate` ignored (only viewer rotation applied); axial `Extend false` and radial focal semantics wrong; hairlines and dash patterns wrong under scaled CTMs; one new thread per page render. | Visible corruption on ordinary files. | 04, 05 |
| 5 | **Fallback was page-only and artificial.** Any token ⇒ whole page to PDFium; the showcase "demonstrated" fallback using `ri`, a valid standard operator. No region compositing existed; `IPdfFallbackProvider` was declared but never implemented. | Area coverage metric meaningless; "full-page fallback hides lack of progress". | 06 "Fallback granularity", 13 |
| 6 | **Security/robustness holes:** Flate decompression allocated the whole output before the size check (bomb not bounded); `MaxObjectsCount`, recursion, per-page budgets not enforced; no encryption detection (encrypted files with an empty user password parsed ciphertext); vector renders bypassed `PdfSecurityPolicy.EnsureRenderDimensionsAllowed`; resolver shared a positional byte source across threads without locking; xref offsets were trusted without checking the object header. | "memory limit can be bypassed" is a stop condition. | 09, 08 |
| 7 | **Semantics bugs:** font cache keyed by resource name (`/F1` on two pages ⇒ same font), composite fonts read as fixed 2-byte codes regardless of CMap, `Tw` not scaled by `Tz`, standard-14 widths approximated, `/Differences` and encodings ignored, ToUnicode parser line-based, command bounds in mixed coordinate spaces (unusable for culling), selection boxes ignored the CTM and crop origin. | Wrong text positions, wrong selection/search boxes. | 04, 05 "Fonts roadmap" |
| 8 | **Verification gaps:** 13 vector tests, no differential tests despite the oracle reference, no fuzzing, no corpus tooling, no benchmarks of the vector path, CI did not run the vector test project at all. Commits for M6/M7/M9 were titled as delivered with a single happy-path test each. | "A feature is not supported merely because one sample renders." | 07, 11 "Definition of done" |
| 9 | **Diagnostics unused:** `PdfEngineMetrics` never recorded; `TotalPagesParsed` never incremented; no report format. | Backlog A5/J4/J5 not met. | 06 "Telemetry" |

---

## 2. What the second pass changed

Every item below has tests in `tests/PdfEngine.Vector.Tests` or `tests/PdfViewer.Tests`.

### Core (`PdfEngine.Vector`)
- **Interpreter rewrite** (`Content/PdfContentInterpreter.cs`): Form XObjects (matrix, BBox clip, recursion + self-reference guard, transparency groups with correct alpha reset), Type3 fonts (glyph procedures executed, `d1` uncoloured), inline images (`BI/ID/EI`, abbreviations, exact-length and scan recovery), image XObjects, optional content (`/OCProperties /D`, `BDC /OC`, OCMD policies, XObject `/OC`), annotation normal appearances (12.5.5 algorithm, hidden/NoView flags), shading patterns and `sh` with function-sampled stops, text render modes (3/7 kept invisible for selection, 1/2/5/6 stroked, 4–7 classified), `TJ` as one run, `Tw`/`Tz` per spec, operand stack taken from the end, `BX/EX`.
- **Classified fallback everywhere** with conservative **page-space bounds**: soft masks, non-Normal blend modes, knockout groups, tiling patterns, pattern strokes, mesh/function shadings, unsupported colour spaces, JPX/JBIG2/CCITT, text clipping, resource limits, unknown operators (outside `BX/EX` only).
- **Budgets:** q-depth, operators, path segments, glyphs, image pixels (per image, per page), Type3 depth, wall-clock per page, cancellation every 256 operators — all produce typed `PdfResourceLimitException` → classified `ResourceLimit` fallback.
- **Typed errors** (`Diagnostics/PdfVectorExceptions.cs`): syntax, object resolution, unsupported feature, resource limit, encryption; every document-open data error surfaces as `PdfSyntaxException`.
- **Document:** encryption gate (`PdfEncryptedDocumentException`), xref reconstruction by scanning (damaged/missing `startxref`, xref-stream-only files, catalog in object streams), hybrid `/XRefStm`, object-header validation with one rebuild, object-count limit, cached object-stream index, serialized resolver, indirect `/Length`, off-thread single-flight display-list builds, **bounded LRU display-list cache** (`MaxCachedDisplayListCommands`), crop box clipped to media box, `/Rotate` snapped to 90°.
- **Images** (`Images/PdfImageSource.cs`): lazy decode (display lists don't pin pixels), any colour space, 1/2/4/8/16 bpc, `/Decode`, stencil masks with fill colour, `/SMask`, stencil and colour-key `/Mask`, JPEG passthrough with Adobe-CMYK handling.
- **Streams, functions, colour** (merged from a parallel workstream): bounded streaming Flate, LZW with `/EarlyChange`, typed unknown-filter errors, image-filter stop (`DecodeImageStream`); PDF functions types 0/2/3/4 with bounds; CalGray/CalRGB/Lab/ICCBased (via `/N` or `/Alternate`), Indexed over any base, Separation/DeviceN tint transforms, Pattern spaces, flagged unknowns.
- **Fonts** (merged from a parallel workstream): identity-keyed font cache, Standard/WinAnsi/MacRoman/Differences + AGL, token-based ToUnicode (arrays, surrogates, caps), real Standard-14 widths, code→CID CMaps (Identity, embedded, variable-length), CIDToGIDMap, sfnt `cmap` 0/4/6/12 lookup for embedded TrueType, Type3 model, descriptor metrics.

### Windows backend (`PdfEngine.Vector.Windows`)
- `WpfDisplayListCompiler` → frozen, resolution-independent `DrawingGroup`; `WindowsVectorRenderer` rasterizes it and **composites fallback regions last** from an `IPdfFallbackProvider` (hybrid) or outlines them (strict).
- **Glyph runs from the embedded TrueType/OpenType program by glyph ID** (ADR-006). Without a loadable program: a metric-compatible system substitute placed at the PDF-computed positions; if the substitute lacks a glyph, or the program is bare Type1/CFF, the run becomes a **backend fallback region** instead of `.notdef` boxes.
- Correct image orientation, `/Rotate` + viewer rotation, shading extend bands and radial clip, hairline and dash handling in page units, byte-bounded image cache, one long-lived STA render thread, `AnalyzeAsync` and `BuildPageDrawingAsync` for hosts.

### Composition root (`PdfViewer`)
- `PdfiumRegionFallbackProvider` (one PDFium raster per page+dpi, cropped per token, unrotated to crop space).
- `HybridVectorDocumentService`: vector path honours `EnsureRenderDimensionsAllowed`; ≥ 50 % fallback area ⇒ full PDFium page; engine badge `Vector` / `Hybrid (N PDFium regions)` / `PDFium (Fallback)`; per-page `PdfEngineMetrics` (once per page, no document text) with `ToJsonReport()`; selection boxes from `TextToPage` normalized to the unrotated crop box (matches PDFium); lazy vector open after an engine switch.
- Showcase PDF and its test now demonstrate a genuine region fallback (Multiply blend) and no longer claim DirectX/Direct2D.
- A race where a late text-extraction result overwrote already-populated segments is fixed (`PageViewModel`).

### Verification & tooling
- `InterpreterCoverageTests` (fail-first fixtures per gap), `DifferentialRenderingTests` (PDFium oracle, perceptual budget), `FuzzRegressionTests` (mutation fuzzing, typed-errors-only; found and fixed two untyped escapes), `HybridVectorServiceTests`, plus the parallel workstreams' stream/function/colour/font suites.
- `eng/vectorpdf/tools/VectorPdf.Tool`: `bench` (vector vs PDFium) and `corpus` (JSONL report).
- CI runs the vector suite (2 000 fuzz iterations per PR, 100 000 nightly), the corpus report and the benchmark.

---

## 3. Evidence

| Measure | Value | Source |
|---|---|---|
| Vector engine tests | 244 passing (13 before) | `dotnet test tests/PdfEngine.Vector.Tests` |
| Viewer regression tests | 307 passing (300 before; none weakened — the showcase test's `ri` expectation was *corrected*, see §1 #5) | `dotnet test tests/PdfViewer.Tests` |
| Differential vs PDFium, geometry fixtures (paths, curves, clip, dash, alpha, inline image) | mean channel diff ≤ 0.37/255, 0 % pixels off by > 64 (72 and 144 dpi) | `DifferentialRenderingTests`; budgets: mean ≤ 1.5, ≤ 1 % |
| Differential, axial shading | mean 1.17–1.43/255, 0 % | budget: mean ≤ 3.0, ≤ 1 % |
| Differential, Helvetica text (substituted) | mean 1.08/255, 0.49 % | budget: mean ≤ 3.0, ≤ 2 % |
| Fuzzing | 30 000 mutations clean locally; 2 escapes found and fixed | `FuzzRegressionTests` |
| Samples corpus | 2/2 files open in both engines; 10/11 pages fully vector; 1 page hybrid (by design); mean pixel diff 1.38 and 2.11 | `eng/vectorpdf/baseline-corpus-samples.jsonl` |
| First render, 450-command page, 100 % | vector 98.7 ms vs PDFium 41.0 ms (**2.4× slower**) | `eng/vectorpdf/baseline-bench.txt` |
| Render at 1600 % | vector 2 353 ms vs PDFium 1 103 ms | same |
| Where vector time goes | compile 2–14 ms; rasterization 77 ms (100 %) / 2 569 ms (1 600 %) | same |
| Cancellation observed | 14–32 ms (budget 50 ms) | same |

---

## 4. Gate status

### M0 gate (12)
- [x] Existing solution builds. · [x] Existing tests pass. · [x] New projects compile. · [x] Engine selection is centralized.
- [~] Baseline captured: vector-vs-PDFium timing baseline committed; package-size and memory baselines **not** captured yet.
- [ ] **"No user-visible behaviour change" is not met:** the first pass made `Auto` (vector-first) the default. With this pass `Auto` is region-safe, but the plan puts vector-by-default at M8. **Decision needed** (§6).

### Vector foundation gate (12)
- [x] Simple vector PDF opens without PDFium parsing.
- [~] Paths render — through WPF, not Direct2D (ADR-011, proposed).
- [x] Text renders as glyph runs, not re-laid-out Unicode (embedded TrueType/OpenType by glyph ID; otherwise positioned substitutes or classified fallback).
- [x] Embedded raster images remain images.
- [~] Zoom to 1600 % never scales a cached bitmap — each zoom re-rasterizes the vectors — but the page view still holds a full-page bitmap per zoom level (memory grows with zoom²). Needs the vector host (§5 #1).
- [~] Device loss: WPF recovers internally (software fallback); no explicit test.
- [x] Cancellation works (observed ≤ 32 ms).

### Hybrid beta gate (12)
- [ ] Corpus thresholds (≥ 99.5 % open, ≥ 99 % pages classified, ≥ 95 % area vector): **no legal corpus exists yet** — only `samples/`. The runner is ready.
- [ ] **≤ 10 % first-visible-page latency regression: fails** (2.4× slower). Root cause is the software rasterizer, not interpretation.
- [x] Memory ceilings enforced (display-list cache, image cache, render dimensions, per-page budgets).
- [x] Existing viewer workflows pass.

### PDFium-free release gate
Not started by design (M9/M10). PDFium still ships and is required.

---

## 5. Remaining work, in priority order

1. **Vector page host (E8)** — display `WindowsVectorRenderer.BuildPageDrawingAsync` output directly (`DrawingImage`/`DrawingVisual`) so zoom and scroll compose vectors on the GPU through WPF instead of re-rasterizing full pages. Fixes the latency gate and the zoom² memory growth. Touches page cache, night-mode effect, overlays and thumbnails — deserves its own PR and tests.
2. **Decide the Windows backend (ADR-011):** keep WPF retained drawing, or implement Direct2D/DirectWrite per ADR-005 (needs a COM interop layer or a vetted package, plus device-loss tests).
3. **Legal corpus + nightly corpus job** (07): select rights-cleared suites, add `eng/vectorpdf/corpus/manifest.json` entries, track open/coverage/diff trends.
4. **Font coverage:** wrap bare CFF/Type1C (and convert Type1) into OpenType so the backend can render them — today they fall back per region, which is correct but makes most LaTeX/InDesign output PDFium-composited; CJK predefined CMaps; vertical writing (currently classified `UnsupportedCMap`).
5. **Transparency (M7 remainder):** soft masks, blend modes, knockout groups, tiling patterns, mesh shadings via bounded intermediate surfaces — currently classified fallback.
6. **Encryption (L1):** Standard Security Handler with platform crypto; today encrypted documents are PDFium-only.
7. **Package/memory baselines** (A1), SBOM/package test asserting what ships (K7).
8. Differential fixtures for radial shading, Type3, stencil/SMask images, rotated crop boxes, and text in embedded TrueType fonts.

---

## 6. Decisions for the product owner

1. **Default engine mode.** The plan (M0, M8) says PDFium stays default until the hybrid beta gate passes; the first pass shipped `Auto`. `Auto` is now region-safe (no known silent-wrong constructs), but it is 2.4× slower on first render. Options: keep `Auto`; or default to `Pdfium` and make `Auto` opt-in until §5 #1 lands.
2. **ADR-011** (WPF interim backend vs Direct2D now).
