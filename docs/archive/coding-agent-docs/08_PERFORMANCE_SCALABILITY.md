# Performance and Scalability Plan

## Current strengths

The current application already has:
- direct BGRA rendering;
- asynchronous rendering;
- cancellation;
- LRU caching;
- thumbnails;
- continuous/single-page modes.

These are good foundations.

## Performance budgets

Establish measurable targets instead of subjective "fast".

Example initial targets:

### Startup
- cold start: < 2.0 s on reference hardware
- warm start: < 1.0 s

### Open
- metadata visible: < 500 ms for ordinary PDFs
- first visible page: < 1.0 s for ordinary PDFs

### Interaction
- navigation command acknowledgment: < 50 ms
- zoom input processing: < 16 ms UI work
- no UI thread PDF rendering

### Memory
Define budgets by document class.

Example:

```text
Small:  < 50 MB document
Medium: 50–250 MB
Large:  250 MB–1 GB
Huge:   > 1 GB
```

The application must degrade gracefully instead of trying to cache the entire document.

## Render scheduler

Use a priority queue.

```text
Priority 0: currently visible page
Priority 1: next/previous visible pages
Priority 2: selected page
Priority 3: near viewport
Priority 4: thumbnails
Priority 5: background prefetch
```

Cancel stale work when:
- user changes page;
- zoom changes;
- rotation changes;
- document closes;
- document revision changes.

## Render deduplication

Requests with identical:

```text
document fingerprint
revision
page
dpi bucket
rotation
render flags
```

should share the same in-flight task.

## DPI buckets

Avoid rendering arbitrary DPI values for every slider movement.

Use buckets such as:

```text
72
96
120
150
180
220
300
```

Interpolate visually where appropriate.

## Memory cache

Cache size should be based on memory budget, not just item count.

For example:

```text
Budget = min(25% of process memory ceiling, configurable maximum)
```

Track:
- bytes;
- hit rate;
- miss rate;
- eviction rate;
- average render time.

## Large-document strategy

Do not instantiate heavy ViewModels for thousands of pages.

Replace eager:

```text
for every page:
    create PageViewModel
    create ThumbnailViewModel
```

with:
- lightweight page catalog;
- virtualized page descriptors;
- lazy page ViewModels;
- virtualized thumbnails.

This becomes important for 5,000+ page documents.

## Text index

For large documents, indexing should be incremental.

Persist:

```text
document fingerprint
engine version
index version
page text
geometry
```

Invalidate when the document or extraction version changes.

## Thumbnail generation

Use:
- small fixed DPI;
- low priority;
- bounded concurrency;
- disk cache optionally.

Never allow thumbnail generation to starve visible-page rendering.

## Headless processing

Create a CLI:

```text
pdfctl info input.pdf
pdfctl render input.pdf --page 1
pdfctl text input.pdf
pdfctl merge a.pdf b.pdf -o merged.pdf
pdfctl split input.pdf --pages 1-10
pdfctl redact input.pdf --config redaction.json
```

The CLI becomes a valuable testing and automation surface.

## Benchmark suite

Track:
- first render latency;
- average page render;
- 95th percentile render;
- search throughput;
- text extraction throughput;
- annotation load/save;
- merge/split throughput;
- memory peak;
- cache hit rate.

Run benchmarks on fixed reference hardware.

## Scalability ceiling

A well-designed local viewer can handle very large PDFs, but the product should distinguish:

```text
Interactive viewer
Document processor
Batch processor
Server worker
```

Do not use one UI process as the architecture for all four workloads.
