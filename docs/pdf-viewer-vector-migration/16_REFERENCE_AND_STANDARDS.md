# References and Standards

## Normative direction

Implement PDF behavior against the current PDF specification and published errata, not by reverse-engineering PDFium behavior alone.

Primary references:
- **ISO 32000-2:2020 — PDF 2.0.** ISO lists this as the current second edition and notes it was reviewed/confirmed in 2026.
- **PDF Association PDF specification archive and errata.** Use current errata when interpreting ambiguous/corrected requirements.
- Legacy PDF 1.7 references remain useful for files authored to older versions.

## Windows rendering references

Microsoft documents Direct2D as a hardware-accelerated immediate-mode 2D API for geometry, bitmaps, and text, with interoperability with Direct3D and DirectWrite. DirectWrite supports glyph-level rendering through Direct2D. These characteristics are why the Windows backend is based on Direct2D/DirectWrite.

Useful official pages:
- Microsoft Learn: Direct2D overview
- Microsoft Learn: Direct2D and DirectWrite text rendering
- Microsoft Learn: Introducing DirectWrite
- Microsoft Learn: Direct2D API overview

## Reference policy

For each implemented operator or PDF feature:
1. cite the relevant PDF specification clause in code/design notes where behavior is non-obvious;
2. check current errata;
3. use PDFium only as a differential implementation, not as normative authority;
4. where PDFium and the specification disagree, investigate and document the decision.

## Repository baseline

This bundle was prepared against the repository structure visible on `main` on 2026-09-21, including `.slnx`, `PdfEngine.Abstractions`, `PdfEngine.Pdfium`, `PdfViewer.Core`, `PdfViewer`, tests, benchmarks, `eng/pdfium`, and the shipped x64 PDFium runtime.
