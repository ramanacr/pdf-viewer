# Security, Quality and Compliance Program

## 1. Threat model

Treat every PDF as hostile input.

A PDF may contain:
- malformed objects;
- embedded files;
- JavaScript;
- external actions;
- unusual fonts;
- enormous images;
- decompression bombs;
- corrupt cross-reference tables;
- crafted annotation data;
- malformed form data;
- unusual encryption;
- malicious payloads hidden in attachments.

## 2. Security policy

Introduce an explicit policy object.

Default profile:

```text
JavaScript: disabled
External launch actions: disabled
Embedded executable content: disabled
Network access from document: disabled
Embedded file extraction: user-confirmed
Attachments: visible but controlled
Maximum file size: configurable
Maximum rendered dimensions: configurable
```

## 3. Native engine hardening

- Keep PDFium pinned.
- Maintain SHA-256 verification.
- Track source release.
- Maintain a reproducible update process.
- Document all local modifications.
- Track CVEs/security advisories.
- Update PDFium regularly.
- Test each PDFium update against the regression corpus.

## 4. Fuzzing

Build a fuzz/regression pipeline around:
- open;
- metadata extraction;
- page loading;
- rendering;
- text extraction;
- annotation loading;
- save;
- form loading;
- signature parsing.

Any crash becomes:
1. minimized corpus input;
2. regression test;
3. fixed before release.

## 5. Golden rendering tests

Create deterministic rendering fixtures.

For each PDF:
- render selected pages;
- compare against baseline;
- use tolerance for expected platform variance;
- track rendering differences.

Test:
- fonts;
- transparency;
- images;
- gradients;
- clipping;
- annotations;
- rotated pages;
- encrypted documents;
- large pages.

## 6. Security release gate

A release must not ship if:
- native dependency checksum fails;
- known critical vulnerability remains unreviewed;
- installer is unsigned;
- update metadata cannot be authenticated;
- compatibility regression exceeds threshold;
- fuzz regression crashes.

## 7. Privacy

Default architecture should be local-first.

Do not collect:
- document content;
- document text;
- document filenames;
- paths;
- annotations;
- extracted metadata

unless explicitly required, disclosed and consented to.

Diagnostics should use:
- anonymous crash IDs;
- application version;
- OS/runtime version;
- non-content error codes.

## 8. Compliance roadmap

Evaluate:
- PDF/A;
- accessibility/Tagged PDF;
- WCAG/desktop accessibility requirements as applicable;
- enterprise privacy requirements;
- data residency where cloud services exist;
- software supply-chain requirements;
- SBOM requirements;
- code signing.

Do not claim formal compliance without an actual validation program.

## 9. Supply chain

Maintain:
- SBOM in CycloneDX;
- SPDX SBOM;
- dependency lock/verification where practical;
- native dependency checksums;
- release provenance;
- signed artifacts;
- third-party notices.

## 10. Commercial legal workstream

Before commercial distribution:
- EULA;
- privacy policy;
- third-party notices;
- open-source attribution;
- license compatibility review;
- trademark review;
- security contact;
- vulnerability disclosure process;
- support terms;
- SDK redistribution terms.

The current PDFium licensing is permissive, but the complete product dependency graph must be reviewed before commercial launch.
