# PDFium Fallback and Exit Plan

## Principle

PDFium is a **temporary compatibility component**, not the canonical engine.

## Modes

### PDFium
Existing behavior. Used for regression baseline.

### Vector
Strict new-engine mode. Unsupported features produce explicit diagnostic failures/placeholders. Used by development/CI.

### Hybrid
Production migration mode. Vector path is preferred; unsupported regions/pages use fallback.

## Fallback granularity

Preferred order:
1. object/region fallback;
2. isolated group fallback;
3. page fallback only when safe localization is impossible.

A single unsupported construct should not automatically force full-page rasterization.

## Fallback reasons

Create a stable enum, e.g.:

```text
UnsupportedFontType
UnsupportedCMap
Type3Font
UnsupportedColorSpace
Pattern
Shading
SoftMask
TransparencyGroup
BlendMode
UnsupportedImageFilter
MalformedContentRecovery
EncryptedContent
UnsupportedAnnotationAppearance
UnknownOperator
InternalCompatibilityGuard
```

Never use free-text-only reasons.

## Telemetry

Local diagnostic telemetry only; preserve the product's no-analytics posture.

Per run/document collect:
- pages parsed;
- pages fully vector;
- fallback commands;
- fallback area / page area;
- fallback reason counts;
- parser recoveries;
- unsupported operators/resources;
- vector build time;
- fallback render time.

No document content is transmitted.

## Fallback budget metric

Use both:
- **page coverage**: percent of pages requiring any fallback;
- **area coverage**: raster fallback pixel/geometry area divided by visible page area.

Area coverage is important because one tiny soft-mask should not classify a page equivalently to full-page fallback.

## Reference oracle

In test builds:
1. render same page through PDFium at fixed DPI;
2. render vector engine to deterministic off-screen target;
3. compare perceptually and structurally;
4. compare text/glyph geometry separately.

Pixel equality is not expected because rasterizers differ.

## Removal gates

PDFium may be removed from production only when all are true:
- corpus parser success target met;
- vector/fallback coverage target met;
- no P0/P1 rendering correctness defects;
- security corpus passes;
- encrypted files supported by new core or explicitly unsupported by product decision;
- annotations/forms/save features have replacement implementations or deliberate independent adapters;
- installer/package test proves no `pdfium.dll`;
- third-party notices/SBOM updated;
- rollback build remains available for at least one release cycle.

## Removal sequence

1. disable PDFium fallback by default in canary/nightly;
2. run corpus and user beta;
3. remove fallback from normal release;
4. retain PDFium oracle in developer/test tooling if useful;
5. remove runtime packaging;
6. later remove build assets only when no test tooling needs them.

Production independence does not require deleting PDFium from developer validation tooling.
