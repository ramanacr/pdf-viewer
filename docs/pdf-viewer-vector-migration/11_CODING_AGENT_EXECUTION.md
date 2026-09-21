# Coding Agent Execution Instructions

## Role model

Product owner supplies WHY / WHAT / constraints / acceptance.  
Coding agents supply evidence / design / implementation / verification.

## Mandatory execution loop

For every work item:

```text
1. Inspect existing implementation and tests
2. State exact files/contracts affected
3. Add fail-first tests
4. Implement smallest vertical slice
5. Run focused tests
6. Run full relevant suite
7. Run benchmark/differential checks when rendering/parser hot path changes
8. Update coverage/fallback diagnostics
9. Review for security, bounds, cancellation, disposal
10. Produce PR summary with evidence
```

## No speculative rewrites

Agents must not:
- rewrite the viewer UI;
- delete PDFium early;
- bypass `PdfEngine.Abstractions` with UI-to-engine implementation coupling;
- add a second competing abstraction for an existing concept without justification;
- weaken tests because the vector output differs;
- silently rasterize the whole page to claim feature support;
- implement cryptography from scratch;
- execute PDF JavaScript/actions;
- introduce cloud telemetry.

## Parallel workstreams

Safe parallelism after M0 contracts stabilize:

### A — parser
lexer, objects, xref, streams.

### B — graphics interpreter
operators, state, paths, clipping.

### C — Windows backend
Direct2D/DirectWrite target, device lifecycle.

### D — fonts/text
font resources, CMaps, glyph mapping.

### E — test/corpus
fixture generation, diff harness, corpus runner.

### F — performance/security
limits, fuzzing, benchmarks, cache metrics.

Avoid parallel edits to shared public contracts without an assigned contract owner.

## PR size

Prefer one feature/operator family per PR. Each PR should be independently revertible.

## Required PR evidence

- tests added;
- tests run;
- before/after fallback counts where relevant;
- before/after benchmark where relevant;
- representative render diff;
- security-limit impact;
- known unsupported cases;
- no unexpected package-size increase.

## Error policy

Use typed errors:
- syntax;
- xref/object resolution;
- unsupported feature;
- resource limit;
- password/encryption;
- render backend/device;
- cancellation.

Do not catch-all and silently invoke PDFium. Fallback must be a deliberate classified outcome.

## Logging

Diagnostics must not log:
- passwords;
- document text by default;
- embedded-file contents;
- personally identifying document metadata unless explicitly requested for debugging.

Log IDs/hashes/operator names/page/object references sufficient for reproduction.

## Commit order recommendation

1. contracts/diagnostics;
2. tests/fixtures;
3. implementation;
4. integration;
5. docs/benchmarks.

## Definition of done

A feature is not “supported” merely because one sample renders. It needs:
- spec-grounded behavior;
- generated fixture;
- malformed fixture;
- differential/golden test;
- bounds/resource-limit test where relevant;
- corpus evidence.
