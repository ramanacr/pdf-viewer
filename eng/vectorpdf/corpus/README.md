# Vector engine test corpus

The corpus drives compatibility priorities and the PDFium exit evidence (ADR-010,
`docs/pdf-viewer-vector-migration/07_TEST_CORPUS_AND_VERIFICATION.md`).

## Rules

- **Do not commit third-party PDFs** without confirmed redistribution rights. Commit an entry in
  `manifest.json` (hash, source URL, licence, tags) and fetch the file outside the repository.
- Generated fixtures belong in the test projects (`VectorPdfBuilder`, `TestPdfBuilder`), not here.
- Every file is identified by SHA-256; a failure report must include it, the page, the commit and
  the render parameters (07 "Reproducibility").

## Running

```powershell
dotnet run --project eng/vectorpdf/tools/VectorPdf.Tool -c Release -- corpus <folder> --out report.jsonl --diff
```

One JSON line per file: `sha256`, `pdfiumPages`/`pdfiumError`, `vectorPages`/`vectorError`,
`repaired`, `fallbackReasons` (counts per `PdfFallbackReason`), `fallbackAreaMean`,
`meanPixelDiff` (fully-vector pages only, 72 dpi) and `vectorMs`. Nothing leaves the machine.

`eng/vectorpdf/baseline-corpus-samples.jsonl` is the report for `samples/`.

## Manifest

`manifest.schema.json` describes entries in `manifest.json`. Tags follow the taxonomy in 07.
