# Migration Milestones

## M0 — Baseline and seams

**Goal:** make migration measurable before changing rendering.

Deliver:
- baseline benchmarks;
- engine mode switch;
- fallback reason model;
- vector projects added;
- deterministic fixture runner;
- PDFium reference renderer harness;
- no user-visible behavior change.

Exit:
- existing tests green;
- baseline report committed/generated;
- CI can run `pdfium`, `vector` (expected limited), and `hybrid`.

## M1 — PDF syntax core

Deliver:
- byte source;
- lexer;
- primitives/arrays/dictionaries/indirect refs;
- classic xref/trailer;
- object resolver;
- Flate streams;
- catalog/page tree/basic page boxes.

Exit:
- generated syntax fixtures pass;
- malformed-input limits tested;
- simple uncompressed/compressed PDFs enumerate pages without PDFium.

## M2 — Basic vector graphics

Deliver:
- content tokenizer;
- graphics state;
- matrices;
- paths;
- fill/stroke;
- clipping;
- DeviceGray/RGB/CMYK;
- immutable display list;
- Direct2D replay.

Exit:
- vector-only generated pages render without PDFium;
- 100–1600% zoom does not allocate zoom-sized full-page bitmaps;
- visual differential threshold passes fixture set.

## M3 — Text

Deliver:
- BT/ET and text operators;
- simple fonts;
- embedded TrueType/OpenType;
- glyph runs;
- DirectWrite rendering;
- Unicode mapping separated from glyph rendering.

Exit:
- selection/search behavior preserved on supported fixtures;
- glyph position tests pass;
- text remains sharp at high zoom.

## M4 — XObjects and images

Deliver:
- Form XObjects with recursion limits;
- image XObjects;
- inline images;
- basic filters/color handling;
- image masks.

Exit:
- mixed vector/image pages render;
- image resources are cached and bounded;
- malformed images cannot cause unbounded allocations.

## M5 — Modern PDF structures

Deliver:
- xref streams;
- object streams;
- hybrid refs;
- incremental updates/revisions;
- stronger recovery.

Exit:
- corpus parse-success threshold materially approaches PDFium baseline;
- no known P0 parser crashes.

## M6 — Fonts/color depth

Deliver:
- Type0/CID;
- CMaps/ToUnicode;
- Type1/CFF;
- vertical writing where required;
- indexed/ICCBased/Lab/Cal* according to corpus priorities;
- Type3.

Exit:
- font-related fallback is no longer dominant.

## M7 — Transparency, patterns, shading

Deliver:
- ExtGState alpha;
- blend modes;
- transparency groups;
- soft masks;
- patterns/shadings;
- localized intermediate surfaces.

Exit:
- fallback-area rate meets beta target;
- complex transparency differential suite passes.

## M8 — Hybrid production rollout

Deliver:
- vector preferred by default for eligible pages;
- bounded fallback;
- local diagnostics;
- kill switch to PDFium renderer;
- packaging unchanged (still includes PDFium).

Exit:
- beta corpus and user workflows stable;
- performance no worse than defined budgets.

## M9 — Non-rendering feature migration

Move remaining production dependencies individually:
- encryption;
- annotations;
- forms;
- save/writer;
- page organization;
- signatures;
- redaction;
- attachments/bookmarks as needed.

Each feature gets its own ADR/tests. Do not couple all to one mega-release.

## M10 — PDFium-free production

Exit criteria in `12_ACCEPTANCE_AND_RELEASE_GATES.md`.

Remove PDFium from production packaging only after all gates pass. PDFium may remain in developer differential tooling.
