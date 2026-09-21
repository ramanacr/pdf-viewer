# Security and Robustness

## Threat model

PDF input is untrusted binary data. Treat parser/rendering code as a hostile-input boundary.

## Preserve current product posture

The repository currently states:
- no script engine;
- active actions are not automatically executed;
- local-document privacy;
- explicit link handling;
- safety inspection/sanitization behavior.

The new engine must not weaken these properties.

## Parser safety

Mandatory:
- checked arithmetic for offsets/sizes;
- 64-bit file offsets;
- bounded allocations;
- recursion/depth limits;
- cycle detection;
- decompression limits;
- image pixel limits;
- content command limits;
- timeout/cancellation strategy;
- deterministic disposal;
- no unsafe pointer arithmetic in managed parser code unless isolated and justified.

## Native renderer boundary

Direct2D/DirectWrite COM interop must:
- validate dimensions before resource creation;
- handle HRESULTs;
- recover from device loss;
- never trust PDF values as native buffer sizes without validation;
- isolate unsafe code.

## Fallback boundary

PDFium fallback must receive the minimum necessary request. Do not expose PDFium native handles in `PdfEngine.Abstractions` or the vector IR.

## Active content

Parsing an action for inspection is not executing it. New parser may identify:
- JavaScript actions;
- Launch;
- URI;
- GoTo/GoToR;
- embedded files.

Execution remains governed by existing `PdfSecurityPolicy`. JavaScript execution remains absent.

## Fuzzing

Targets:
- lexer;
- xref parser;
- object parser;
- stream decoder;
- content parser;
- CMap parser;
- image metadata;
- page tree;
- display-list builder.

Seed with minimal valid fixtures plus prior crashers. Every discovered crash becomes a permanent regression test.

## Dependency policy

“PDFium-independent” does not mean “cryptography/image-codec-from-scratch.”

Allowed when justified:
- Windows platform APIs;
- well-maintained decompression/image libraries;
- vetted crypto.

Requirements:
- license review;
- SBOM;
- pinned versions;
- vulnerability monitoring;
- size/performance measurement.

## Sandboxing future option

If native decoder/parser attack surface warrants it, design for a future brokered worker process with low privileges. Do not block M0 on process isolation, but avoid global/static assumptions that make later isolation impossible.
