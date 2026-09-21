# Performance and Memory Budgets

## Performance objective

Vector-first is not successful if it is merely sharper. It must preserve or improve interactive latency and control memory growth.

## Measure separately

1. document open;
2. xref/object parse;
3. page display-list build;
4. first visible render;
5. warm replay;
6. zoom;
7. scroll;
8. fallback rendering;
9. text extraction;
10. memory/cache pressure.

## Initial engineering budgets

These are **gates to tune with baseline measurements**, not marketing claims.

### Interaction
- UI input must not block on page parsing/rendering.
- cancellation of obsolete render work should be observed within 50 ms where the worker is not inside an unavoidable platform call.
- warm vector zoom should reuse the display list and avoid reparsing.
- visible-page work gets priority over prefetch.

### Memory
- retain the existing explicit memory-budgeted cache philosophy;
- display-list cache has its own byte/complexity budget;
- decoded images are separately budgeted;
- device-dependent GPU resources are evictable;
- fallback surfaces are short-lived unless reuse is demonstrably beneficial;
- no cache key should multiply full-page raster memory merely by zoom level in vector mode.

## Complexity budgets

A malicious PDF can create tiny files with huge computational expansion. Add limits for:
- indirect objects;
- object nesting;
- page-tree depth;
- content-stream decoded bytes;
- commands per page;
- path segments;
- glyphs per page;
- image dimensions/pixels;
- pattern recursion;
- Form XObject recursion;
- decompression ratio;
- total decoded resource bytes;
- execution time.

Limits should produce typed failures/diagnostics, not process crashes.

## Caching model

```text
Document cache
  |
  +-- resolved objects
  +-- font resources
  +-- image metadata/decoded resources
  +-- page display lists
  +-- optional spatial index

Device cache
  |
  +-- D2D geometries
  +-- brushes/stroke styles
  +-- font faces
  +-- GPU bitmaps
```

Device loss clears only device cache.

## Spatial culling

Add conservative bounds during IR construction. For complex pages, optionally build a lightweight spatial index after command-count threshold. Benchmark before choosing R-tree/BVH/grid; do not introduce it for small pages.

## Benchmark additions

Extend `PdfViewer.Benchmarks` with:
- parse xref/object;
- build display list;
- replay 1k/10k/100k commands;
- glyph-run rendering;
- path-heavy engineering drawing;
- image-heavy page;
- 100→1600% zoom;
- rapid scroll cancellation;
- hybrid fallback.

Always compare against the current PDFium baseline on the same machine/configuration.
