# Implementation Status and Audit

**Last updated:** 2026-09-26 (Direct2D backend and page host, blend modes, soft masks, tiling patterns, vertical writing, glyph clips, two-circle radials, predefined CJK CMaps, JPEG 2000, mesh shadings, CCITT, JBIG2, knockout and non-isolated groups, encryption, certificate encryption, permissions) · branch `feat/vector-migration-gaps` · PR ramanacr/pdf-viewer#1
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
- Showcase PDF and its test demonstrate a genuine region fallback (a knockout transparency group, which is still outside the native compositor; it used a Multiply blend until blend modes became native).
- A race where a late text-extraction result overwrote already-populated segments is fixed (`PageViewModel`).

### Vector page host (E8) — decided: keep `Auto` default, build the host
- `WindowsVectorRenderer.BuildPageSurfaceAsync` builds a frozen `DrawingImage`: white page, vector content, fallback regions as PDFium crops at 200 dpi, sized in points after `/Rotate` + viewer rotation.
- `IPdfDocumentService.GetVectorPageSurfaceAsync` (default: none). `HybridVectorDocumentService` returns a surface unless the page needs ≥ 50 % fallback, has more than 40 000 commands (live drawings re-render every frame), or the fallback raster would breach the render-dimension policy; surfaces are cached per page and rotation (LRU 24), cleared on open/close/engine switch.
- `PageViewModel.VectorSurface` / `PageSurface` / `HasSurface`: the page view binds `PageSurface`. A zoom change does not rebuild a surface and no page bitmap is held; PDFium and very dense pages stay on the bitmap path.
- **Night mode is live too:** the page is compiled with every paint, gradient stop, image and fallback crop colour-inverted on a black page. Inversion commutes with source-over compositing, so this equals the pixel-inverted page (test: ≤ 1/255 mean difference) without a shader.
- **Scroll cost:** `Views/PageSurfaceCache` gives a page image a `BitmapCache` at scale 1 while it fits 4096 device pixels, so scrolling composes a GPU texture and the vectors re-render only when the size changes (zoom). Larger (high-zoom) pages draw live.
- Tests: `VectorPageHostTests` (surface reuse across zoom, rotation, night surface = inverted day surface, bitmap path for PDFium/dense pages, hybrid region page stays live, hard edge at 16× zoom, cache rule). Smoke: the real viewer opened EngineShowcase and a path-heavy file on the live path, stayed responsive, badge `⚡ Vector`.

### Corpus and embedded font programs
- **Corpus:** `eng/vectorpdf/corpus/fetch-corpus.ps1` fetches the veraPDF corpus (CC BY 4.0) and the PDF Association PDF 2.0 examples (CC BY-SA 4.0) at pinned commits into a git-ignored cache and writes `manifest.json` (SHA-256, source, licence per file; 2 913 files). PDFs are never committed.
- **Corpus runner** reports the hybrid-beta gate inputs (`--summary`), per-detail fallback counts and worst pixel differences; `vectorpdf diffpage` writes vector | PDFium | difference images and dumps commands, content and per-font load status.
- **Font programs (`Fonts/Programs/`):** every embedded kind is normalized into an sfnt the backend loads — TrueType subsets get `name`/`OS/2`/`post`/`cmap` (and `hhea`/`hmtx` when inconsistent); bare CFF (Type1C, CIDFontType0C) is wrapped in OpenType; Type 1 (`FontFile`) is converted to CFF (eexec/charstring decryption, subrs inlined, flex, hint replacement, seac, FontMatrix baked into outlines). Glyph indices come from the CFF charset / built-in encoding / sfnt cmap per kind. A program the backend still rejects becomes a classified fallback region — never a silent substitute.
- **Root cause found in the first pass's renderer:** WPF's font cache throws for a second font file loaded from a folder it has already read, and for a folder that is deleted and recreated. All embedded fonts were written to one folder, so **only the first embedded font per process ever rendered with its own glyphs**; the rest silently used system substitutes. Fonts now get one folder each under a per-renderer root.
- **Page boxes:** only an explicitly specified `/CropBox` is inherited, and it is clipped to the media box at the leaf (two corpus files rendered at the wrong size).

### Direct2D / DirectWrite backend (ADR-005; ADR-011 decided)
- `PdfEngine.Vector.Direct2D` (Vortice.Windows 3.8.3, MIT): D3D11 device (hardware, WARP fallback), Direct2D 1.1 device context, DirectWrite fonts from memory (custom font-file loader, no temp files), WIC JPEG decode. Clips are geometric-mask layers; hairlines use `StrokeTransformType.Hairline`; output larger than 2 048 px is tiled, and geometry realizations are shared across tiles; device loss recreates the device without reparsing (`SimulateDeviceLoss` test).
- **Default page host:** the page bitmap comes from Direct2D, and when the page is zoomed past that bitmap a **viewport detail tile** (visible area plus a margin, ≤ 4 096 px a side) is rendered at exact device resolution and laid over it; scrolling inside the margin reuses the tile. Zoom range 25–1 600 %. `PDF_VECTOR_HOST=wpf` selects the earlier WPF drawing host; the WPF renderer also stands in when no Direct3D device exists.
- **Host tuning (`vectorpdf tiles`, `eng/vectorpdf/baseline-tiles.txt`):** rectangular clips (`re W n`) are axis-aligned clips instead of offscreen layers, and other clip layers are bounded to their mask (a 58-command text page with 13 nested clips: 516 → 44 ms at 300 dpi). GPU tiles are 4 096 px (one replay per viewport tile instead of four). Replayers, with their tessellations, are cached per page, scale and colour mode, so scrolling at a fixed zoom reuses them (path-heavy fixture at 1 600 %: next tile 136 ms, versus 1.2 s before). The detail tile shows the visible area first, then the margin tile replaces it. `forceSoftware` measures the WARP tier (RDP, VMs).
- **Zero-copy detail tiles:** on a hardware tier in a local session, a detail tile is rendered into a shared Direct3D 11 texture and shown by a WPF `D3DImage` through a Direct3D 9Ex surface (`D3D9ExInterop`, four COM calls, no extra package): the pixels never leave the GPU. Next tile on light corpus pages p50 ≈ 30–40 ms against 130–175 ms for readback plus WPF bitmap (before WPF's upload). Software tier, RDP and `PDF_GPU_PRESENT=0` keep bitmaps; a lost display device drops the tile and the page bitmap shows until the next one.
- PDFium pages get the same detail tiles (`RenderPageRegion`: FPDF_RenderPageBitmap with a negative origin), and region fallback asks PDFium only for each token's region.

### Transparency and patterns (M7)
- **IR:** `BeginCompositingGroup(bounds, alpha, blend, softMask, isolated, knockout)` / `EndCompositingGroup`, `PdfSoftMask` (luminosity or alpha, the mask group as its own display list, backdrop colour, sampled `/TR`), `DrawTilingPattern` + `PdfTilingPattern` (one cell as its own display list), `PushStrokeClip` (pattern-painted strokes).
- **Interpreter:** an object painted under a non-Normal blend mode or a soft mask is wrapped in a group of its own (the specified single-object result); transparency-group XObjects painted with a blend mode or soft mask become one compositing group; the soft-mask group is recorded when the ExtGState is set, with the CTM of that moment (cached per mask and CTM, depth-limited). Tiling cells are recorded once in pattern space (cached per pattern and, for uncoloured patterns, per colour; self-reference and depth are guarded); uncoloured patterns take their colour from `scn`. A mask or cell that itself needs fallback keeps the whole object on the fallback path.
- **Direct2D:** a group renders into an offscreen bitmap on its own device context; soft masks are rendered over their backdrop colour, turned into alpha with a luminosity colour matrix and `/TR` via a table transfer, and applied with an alpha-mask effect; blend modes use the Blend effect against a backdrop copied from the surface after its layers are popped (enclosing clips are re-applied to the group content), written back with a clipped source-copy. Opacity groups that contain a blend group are composited offscreen as a whole. Night mode blends true colours and inverts the result, because blend modes do not commute with inversion. Tiling patterns rasterize one period at device resolution (every overlapping tile contributes) and fill with a wrapping bitmap brush, so the period is exact at any zoom.
- **WPF backend:** compositing groups, tiling patterns, glyph-outline clips and two-circle radials are classified as backend fallback (PDFium pixels) — WPF has no blend modes.
- **Knockout groups (exact):** each element is composited as G·(1 − f) + E + (f − αE)·C0, where E is the element rendered alone with the current clips, f its shape from a shape-mode render (every paint opaque, images as their parallelogram, pattern fills as their area) and C0 the initial backdrop of a non-isolated group. Knockouts of opaque, Normal-blend elements are downgraded to ordinary groups.
- **Non-isolated groups (exact on opaque backdrops):** a group whose content blends (or knocks out) starts from a copy of the backdrop, and the backdrop is removed again with R = Cn − C0·(1 − αg) (αg from an isolated render of the same content) before the group's alpha, soft mask and blend mode apply. PDFium composites non-isolated groups as isolated and ignores knockout, so these are verified against hand-computed values from 11.4.
- **Glyph-outline clips:** text rendering modes 4–7 and pattern-filled text become `PushTextClip`; Direct2D unions the DirectWrite outlines of the runs into a layer mask. Type 3 glyphs add nothing to the clip and modes 3/7 draw nothing, as ISO 32000-2 9.3.6 specifies (PDFium agrees).
- **Radial shadings between arbitrary circles** (and holed concentric ones): Direct2D evaluates the largest t per pixel (sampled at pixel centres; PDFium samples at pixel corners, a half-pixel phase difference of ≤ 3/255).
- **Mesh and function shadings:** types 4–7 are decoded by `PdfMeshDecoder` (bit widths, `/Decode`, shared-edge flags, Coons interior points) and rasterized by Direct2D as Gouraud triangles, with patches subdivided at device resolution in the specified fold order; parametric meshes interpolate t and then map it through the function. Type 1 shadings sample f(x, y) at device pixels. Gradient stops are sampled at 257 points.
- Still classified: nothing in these areas for valid files; groups over transparent backdrops are composited as isolated.

### Bilevel image codecs
- **CCITTFaxDecode:** T.4 Modified Huffman (K = 0), mixed MR (K > 0) and T.6 MMR (K < 0) with EOL/RTC/EOFB, `/EncodedByteAlign`, `/BlackIs1`, `/Rows` and damaged-row recovery; bit-exact on Windows-encoded G3/G4 streams and a hand-coded MR stream.
- **JBIG2Decode:** MQ arithmetic decoder, IAx/IAID, the standard Huffman tables B.1–B.15 (canonical codes checked against Annex B) and custom tables; generic regions (templates 0–3, TPGDON, AT, MMR, unknown length), refinement regions (TPGRON), symbol dictionaries (arithmetic, Huffman, refinement/aggregation, context reuse), text regions (all corners, transposition, strips, refinement, symbol-ID run codes), pattern dictionaries, halftone regions and `/JBIG2Globals`. A test-side T.88 encoder produces streams that PDFium must decode identically (all cases bit-exact).
- **JPEG:** bytes before the SOI marker are skipped, as libjpeg does.
- **Text extraction:** CJK fonts without `/ToUnicode` map CIDs of the Adobe collections to Unicode through the inverted `Uni*-UTF16-H` CMaps.

### Predefined CJK CMaps and JPEG 2000
- **CMaps:** the 59 predefined CMaps of ISO 32000-2 Table 116 come from Adobe cmap-resources (BSD-3-Clause) at a pinned commit, fetched by `eng/vectorpdf/cmaps/build-cmaps.ps1` (SHA-256 per file in `cmaps-manifest.json`) and embedded deflated (843 KB). Named encodings and `usecmap` chains resolve through `PredefinedCMaps`; only names outside Table 116 (e.g. a collection name such as `Adobe-Korea1-2`, which veraPDF uses in its "fail" files) are still classified.
- **JPEG 2000:** `JPXDecode` images decode in the core with CoreJ2K (managed, BSD-3-Clause): codestream dimensions and bit depths win, `/ColorSpace` is optional (component count selects Gray/RGB/CMYK), `/SMaskInData` takes opacity from the extra channel, `/Decode` is ignored. The SIZ marker is checked against the pixel limits before the decoder allocates; decoder failures are classified `ImageDecode` (200-mutation fuzz test).

### Encryption (Standard and public-key security handlers)
- `PdfStandardSecurityHandler` (ISO 32000-2 7.6.4): revisions 2–4 (RC4 40–128 bit; crypt filters `V2`/`AESV2` with `/StmF`, `/StrF` and per-stream `/Crypt` filters) and 5–6 (AES-256, `AESV3`, Algorithm 2.B hashing, SASLprep via NFKC). User and owner passwords (Algorithms 2, 4–7, 2.A); the empty user password opens owner-only protected files without a prompt.
- The resolver decrypts each object read from the file (strings recursively, stream data once) with its object key (Algorithm 1); objects inside object streams are covered by their stream; cross-reference streams, the `/Encrypt` dictionary and — with `/EncryptMetadata false` — metadata streams stay plain. A missing or wrong password is a typed `PdfEncryptedDocumentException { PasswordRequired }`.
- `PdfSecurityHandler` is the shared base (crypt filters, Algorithm 1 object keys, RC4, AES-CBC); `PdfSecurityHandler.Create` dispatches `/Standard` and `/Adobe.PubSec`.
- **Certificate (public-key) encryption** — which PDFium cannot open at all — is handled by `PdfPublicKeySecurityHandler` (ISO 32000-2 7.6.5): sub-filters `adbe.pkcs7.s3`, `s4` and `s5` (recipients in `/Recipients` or in the crypt filter). `CmsEnvelopedData` decrypts each PKCS#7 envelope with in-box .NET only: key transport RSA PKCS#1 v1.5 or OAEP (hash from the parameters), recipient by issuer+serial or subject key identifier, content cipher AES-CBC, 3DES, RC2 or RC4. The file key is SHA-1 (SHA-256 for AES-256) of the 20-byte seed, every recipient blob and `FFFFFFFF` when metadata is plain; the recipient's permissions are bytes 21–24. Certificates come from the CurrentUser and LocalMachine `My` stores (with a private key); no match is a typed `PdfEncryptedDocumentException { CertificateRequired }`.
- **Decrypted copy for PDFium:** `PdfDecryptedCopyWriter` serializes every in-use object of a decrypted document into an in-memory unencrypted PDF (no `/Encrypt`, `/Crypt` filters removed, lengths recomputed, classic xref), so text search, selection, printing and annotations keep working through PDFium on certificate-encrypted files. The copy never touches disk: Save is disabled for it, file-path operations explain why they are unavailable, and Save As warns that the result is not encrypted. The writer round-trips all 2 913 corpus files (same page count, first page equal under PDFium).
- **Permissions enforcement (Table 22):** `PdfDocumentPermissions` (Abstractions) decodes `/P` per revision (R2: bits 9–12 follow bits 5/6) or the recipient's permissions; the owner password or an unrestricted recipient lifts everything. The view model refuses print, copy, export text/images, OCR text, annotate, form fill, edit/redact, organize/merge/split/extract and read aloud (accessibility bit) with an explanation; the status bar shows a lock with the restriction summary; printing without the high-quality bit is limited to 150 dpi.
- The viewer passes the password PDFium accepted to the vector document, including the lazy open after an engine switch; it is held for the open document only.
- Tests: a test-side encryptor (independent writer implementation) produces RC4-40/R2, RC4-128/R3, RC4-128/R4, AES-128/R4, AES-256/R5 and AES-256/R6 files; each opens with user and owner passwords, is refused without one, renders identically to the unencrypted document and like PDFium opened with the same password, and survives mutation fuzzing. The same encryptor builds public-key files (s4 RC4, s5 AES-128/256, several recipients, SKI and OAEP recipients, restricted and unrestricted); each opens only with a recipient certificate, renders identically to the unencrypted document, and its decrypted copy opens in PDFium. `SecuredDocumentTests` cover the viewer rules end to end.

### Vertical writing
- Identity-V and `/WMode 1` CMaps (named or embedded): each glyph's position vector (`/W2`, `/DW2`) is placed on the current point, the point advances by W1y (TJ adjustments apply vertically), glyph Y offsets reach both backends, and selection boxes follow the column.

### Verification & tooling
- `InterpreterCoverageTests` (fail-first fixtures per gap), `DifferentialRenderingTests` (PDFium oracle, perceptual budget), `FuzzRegressionTests` (mutation fuzzing, typed-errors-only; found and fixed two untyped escapes), `HybridVectorServiceTests`, plus the parallel workstreams' stream/function/colour/font suites.
- `eng/vectorpdf/tools/VectorPdf.Tool`: `bench` (vector vs PDFium) and `corpus` (JSONL report).
- CI runs the vector suite (2 000 fuzz iterations per PR, 100 000 nightly), the corpus report and the benchmark.

---

## 3. Evidence

| Measure | Value | Source |
|---|---|---|
| Vector engine tests | 422 passing (13 before the second pass) | `dotnet test tests/PdfEngine.Vector.Tests` |
| Viewer regression tests | 371 passing (300 before; none weakened — the showcase test's `ri` expectation was *corrected*, see §1 #5) | `dotnet test tests/PdfViewer.Tests` |
| Differential vs PDFium, geometry fixtures (paths, curves, clip, dash, alpha, inline image) | mean channel diff ≤ 0.37/255, 0 % pixels off by > 64 (72 and 144 dpi) | `DifferentialRenderingTests`; budgets: mean ≤ 1.5, ≤ 1 % |
| Differential, axial shading | mean 1.17–1.43/255, 0 % | budget: mean ≤ 3.0, ≤ 1 % |
| Differential, Helvetica text (substituted) | mean 1.08/255, 0.49 % | budget: mean ≤ 3.0, ≤ 2 % |
| Fuzzing | 30 000 mutations clean locally; 2 escapes found and fixed | `FuzzRegressionTests` |
| Samples corpus | 2/2 files open in both engines; 10/11 pages fully vector; 1 page hybrid (by design); mean pixel diff 1.38 and 2.11 | `eng/vectorpdf/baseline-corpus-samples.jsonl` |
| First render, 450-command page, 100 % | vector 98.7 ms vs PDFium 41.0 ms (**2.4× slower**) | `eng/vectorpdf/baseline-bench.txt` |
| Render at 1600 % | vector 2 353 ms vs PDFium 1 103 ms | same |
| Where vector time goes | compile 2–14 ms; rasterization 77 ms (100 %) / 2 569 ms (1 600 %) | same |
| Cancellation observed | 8–32 ms (budget 50 ms) | same |
| Time to on-screen page (live surface) | 15.9 ms per page vs PDFium 68 ms raster at 100 % in the same run; zoom afterwards costs no re-render | `vector.surface.build` in `baseline-bench.txt` |
| Viewer tests after the Direct2D host | 320 passing | `dotnet test tests/PdfViewer.Tests` |
| Corpus: documents open (2 913 files) | PDFium 2 913; vector **2 913** (the 4 encrypted files now open with the Standard security handler and render fully vector, mean diff ≤ 0.29/255); 0 untyped errors | `eng/vectorpdf/baseline-corpus-summary.json` |
| Encryption vs PDFium | RC4 40/128, AES-128, AES-256 (R5, R6), user/owner/empty passwords: decrypted renders equal the unencrypted document and match PDFium | `EncryptionTests` (33), `HybridVectorServiceTests` |
| Certificate encryption and permissions | s4 RC4, s5 AES-128/256, multiple recipients, SKI/OAEP: only recipients open, renders equal the unencrypted document, decrypted copy opens in PDFium; Table 22 rules enforced in the viewer | `EncryptionTests`, `SecuredDocumentTests` (8) |
| Decrypted-copy writer round trip | 2 913 / 2 913 corpus files: same page count, first page mean diff ≤ 1/255 under PDFium | one-off corpus run |
| Corpus: pages with a display list or classified fallback | 100 % (3 003 pages sampled, ≤ 50 per file) | same |
| Corpus: pages fully vector / vector page area | **95.0 % / 98.67 %** (before the font work: 89.2 % / 98.65 %, with embedded fonts silently substituted) | same |
| Corpus: fidelity of fully-vector pages vs PDFium (72 dpi) | median mean-channel diff 0.009/255; 12 files > 5/255 — inspected: text antialiasing on text-dense pages, no missing content | `vectorpdf corpus --diff` |
| Corpus: remaining fallback reasons | BlendMode 76, Pattern 53, UnsupportedCMap 37, SoftMask 17, TransparencyGroup 11, UnsupportedImageFilter 9 (JPX), UnsupportedFontType 6 (deliberately broken PDF/A "fail" samples) | same |
| Direct2D vs PDFium, all 15 blend modes, luminosity/alpha soft masks (with `/BC`, `/TR`), blended and nested groups, soft-masked image | mean channel diff 0.00–0.44/255, 0 % pixels off by > 64 | `CompositingTests` (25 tests) |
| Direct2D vs PDFium, tiling patterns (coloured, uncoloured, rotated `/Matrix`, overlapping cells, alpha, pattern strokes) | mean 0.00–1.08/255, ≤ 1.08 % off (72 and 144 dpi; at fractional cell sizes PDFium snaps cells to whole pixels, so the exact period is asserted separately) | `TilingPatternTests` |
| Corpus with the Direct2D backend (2 913 files) | pages fully vector **99.17 %** (95.07 % before this phase), vector page area **99.72 %** (98.67 %); BlendMode, SoftMask, TransparencyGroup, Shading, JPX and TextClipping 0; Pattern 53 → 2 (a degenerate YStep of −1.2·10⁻³⁸, which PDFium paints as nothing — the fallback shows exactly that); UnsupportedCMap 37 → 12 (invalid encoding names). The rest are deliberately broken fonts, unknown operators and misspelt filters in veraPDF/Isartor "fail" files | `eng/vectorpdf/baseline-corpus-summary.json` |
| Mesh and function shadings vs PDFium | mean ≤ 1.2/255 at 144 dpi (types 1, 4 incl. shared edges, 4-bit data and a function, 5, 6 with a shared edge, 7) | `MeshShadingTests` |
| CCITT and JBIG2 | bit-exact decodes; renders equal PDFium (mean 0.00/255) | `CcittFaxTests`, `Jbig2Tests` (29 cases) |
| Knockout and non-isolated groups | within 1/255 of hand-computed results | `CompositingTests` |
| JPEG 2000 vs PDFium (OpenJPEG) | mean 0.43–2.23/255 (gray without `/ColorSpace`, RGB, JP2 RGBA with `/SMaskInData`) | `Jpeg2000Tests` |
| Vertical writing vs PDFium (Identity-V, `/W2`, embedded WMode 1 CMap) | mean 0.54–0.71/255 on Direct2D and WPF | `VerticalTextTests` |
| Glyph-outline clips and pattern-filled text vs PDFium | mean 0.10–0.18/255 | `TextClipTests` |
| Two-circle radial shadings vs PDFium | mean 0.9–2.9/255 (half-pixel sampling phase) | `RadialShadingTests` |
| Direct2D first render vs PDFium (450-command page) | faster at every zoom: 100 % 26 ms vs 35 ms, 400 % 90 ms vs 194 ms, 1 600 % 0.55 s vs 1.16 s | `eng/vectorpdf/baseline-bench.txt` |
| Embedded fonts by kind (corpus) | Type 1: 11 files, max diff 0.27/255; CFF: 140 files, 0 font fallbacks; TrueType: 665 files, median diff 0.105/255 | same |
| On-screen scrolling, text document, live surface | 60 fps (p50 16.7 ms, p95 ≤ 17.1 ms) at 100–1600 %, same as PDFium bitmaps | `vectorpdf live`, `eng/vectorpdf/baseline-live.txt` |
| On-screen scrolling, path-heavy document (400 page-spanning curves/page) | uncached live: 27–41 fps at 100–400 %; **with `PageSurfaceCache`: 60 fps at 100–400 %**, p95 33 ms at 800 % (above the cache limit), 60 fps at 1600 % | same |

---

## 4. Gate status

### M0 gate (12)
- [x] Existing solution builds. · [x] Existing tests pass. · [x] New projects compile. · [x] Engine selection is centralized.
- [~] Baseline captured: vector-vs-PDFium timing baseline committed; package-size and memory baselines **not** captured yet.
- [ ] **"No user-visible behaviour change" is not met:** the first pass made `Auto` (vector-first) the default. With this pass `Auto` is region-safe, but the plan puts vector-by-default at M8. **Decision needed** (§6).

### Vector foundation gate (12)
- [x] Simple vector PDF opens without PDFium parsing.
- [x] Paths render through Direct2D (ADR-005; ADR-011 decided 2026-09-26). WPF remains the fallback renderer.
- [x] Text renders as glyph runs, not re-laid-out Unicode (embedded TrueType/OpenType by glyph ID; otherwise positioned substitutes or classified fallback).
- [x] Embedded raster images remain images.
- [x] Zoom to 1600 % never scales a cached bitmap: the page view shows a live vector surface (dense pages over 40 000 commands, night mode and PDFium pages still use bitmaps).
- [x] Device loss: the Direct2D device is recreated and the page re-rendered from the cached display list (`Direct2D_SurvivesDeviceLoss_WithoutReparsing`).
- [x] Cancellation works (observed ≤ 32 ms).

### Hybrid beta gate (12)
- [x] Corpus thresholds on the rights-cleared corpus (2 913 files): open 100 % (≥ 99.5 %), pages classified 100 % (≥ 99 %), vector area 98.67 % (≥ 95 %). The corpus is standards-conformance heavy; broaden with real-world producers before the beta decision.
- [x] ≤ 10 % first-visible-page latency (on-screen path): surface built in ~16 ms vs PDFium's ~68 ms raster in the same run; scrolling holds 60 fps on text and, with `PageSurfaceCache`, on the path-heavy fixture up to 400 %. Off-screen rasterization (bench `vector.render.*`) is still ~2× PDFium, which matters only if print/export move to the vector path.
- [x] Memory ceilings enforced (display-list cache, image cache, render dimensions, per-page budgets).
- [x] Existing viewer workflows pass.

### PDFium-free release gate
Not started by design (M9/M10). PDFium still ships and is required.

---

## 5. Remaining work, in priority order

1. **Live host tuning, remainder:** the first tile at a new deep zoom still tessellates the whole page (≈ 0.8 s on the 400-curve fixture); building the realizations in the background while the zoom settles would hide it. Page bitmaps (not detail tiles) still take the readback path; they are cached and reused, so this matters less.
3. **Nightly corpus job:** run `fetch-corpus.ps1` + `vectorpdf corpus --summary` in the scheduled CI tier and track trends; add real-world producer PDFs (LaTeX, InDesign, CAD, scans) under their licences.
4. **Remaining font coverage:** bare CFF with a non-uniform FontMatrix (classified).
5. **Transparency remainder:** non-isolated groups nested inside isolated groups with transparent backdrops (composited as isolated); JBIG2 colour extension and 12-pixel extended templates.
6. **Real-world corpus:** the rights-cleared corpus is conformance-heavy; add scanned, CAD and publishing samples under their licences.
7. **Encryption remainder:** certificates on smart cards/HSMs that need a PIN prompt are untested (the Windows CNG provider shows its own prompt); re-encrypting a saved copy for the same recipients.
8. **Package/memory baselines** (A1), SBOM/package test asserting what ships (K7).
9. Differential fixtures for Type3, stencil/SMask images, rotated crop boxes, and text in embedded TrueType fonts.

---

## 6. Decisions for the product owner

1. **Default engine mode — decided 2026-09-25: keep `Auto`.** Deviation from M0/M8 accepted by the product owner; mitigated by region-safe fallback and the live vector host.
2. **ADR-011 — decided 2026-09-26: Direct2D.** The product owner asked for the best viewer and allowed replacing WPF where it is the limit; Direct2D renders pages and detail tiles, WPF remains the application shell and the fallback renderer.
