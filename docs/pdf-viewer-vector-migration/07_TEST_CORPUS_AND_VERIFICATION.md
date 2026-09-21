# Test Corpus and Verification Strategy

## Test pyramid

### Unit
Parser tokens, objects, xref, filters, matrices, graphics state, operators, font mappings, bounds.

### Generated fixtures
Use/extend the existing `TestPdfBuilder` and fixture generation approach to create minimal PDFs that isolate one feature.

### Golden structural tests
Assert display-list commands, resources, transforms, glyph IDs/positions, clipping, and fallback reasons.

### Differential rendering
Vector engine vs PDFium reference.

### Viewer regression
Run the existing `PdfViewer.Tests` unchanged where possible.

### Corpus
Thousands of heterogeneous PDFs from legally usable/public test suites and generated/fuzzed files.

### Fuzzing
Parser and content interpreter are primary fuzz targets.

## Corpus taxonomy

Tag each PDF with:
- PDF version;
- linearized;
- xref table/stream/hybrid;
- object streams;
- encrypted;
- page count;
- font types;
- color spaces;
- images/codecs;
- transparency;
- patterns/shadings;
- forms;
- annotations;
- malformed/repaired;
- producer;
- scanned vs born-digital.

## Golden comparison

For each page collect:
- render at 96, 144, 300 DPI equivalents;
- selected high zoom regions;
- text extraction;
- glyph bounding boxes;
- display-list feature summary;
- fallback summary.

Use perceptual thresholds and edge-aware comparison. Anti-aliasing differences must not create false failures.

## Zoom-specific verification

Test at:
`25%, 50%, 100%, 200%, 500%, 800%, 1600%`

Assertions:
- vector geometry remains sharp;
- no full-page bitmap is simply enlarged in vector mode;
- glyph positions remain stable;
- clipping remains correct;
- viewport culling does not omit content;
- memory does not scale quadratically with zoom for vector-only pages.

## Existing tests to protect

Current repository tests include:
- `PdfEngineCoreTests`
- `PdfServiceTests`
- `ComparisonAndOcrTests`
- `PageCacheCeilingTests`
- `TextSelectionAndSearchEventTests`
- annotation fidelity/workflow tests
- sanitization/security tests
- save-in-place tests
- page/layout/navigation tests.

Do not weaken these to make the migration pass.

## Fail-first workflow

For each PDF operator/feature:
1. add minimal fixture;
2. assert parser/IR behavior and fail;
3. add reference render/golden if applicable;
4. implement;
5. add malformed variants;
6. benchmark if hot path;
7. record feature coverage.

## CI tiers

### PR
- unit/generated fixtures;
- existing viewer regression;
- small deterministic differential set.

### Nightly
- medium corpus;
- rendering diffs;
- performance budgets;
- fallback trend.

### Scheduled/release
- full corpus;
- fuzz regression;
- packaging/SBOM;
- security corpus;
- long-document stress;
- GPU/software rendering checks.

## Reproducibility

Every corpus failure must report:
- SHA-256;
- corpus ID/path;
- page;
- engine version/commit;
- feature/fallback summary;
- deterministic render parameters;
- diff artifact path.
