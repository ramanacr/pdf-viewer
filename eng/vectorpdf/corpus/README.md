# Vector engine test corpus

The corpus drives compatibility priorities and the PDFium exit evidence (ADR-010,
`docs/pdf-viewer-vector-migration/07_TEST_CORPUS_AND_VERIFICATION.md`).

## Rules

- **Do not commit third-party PDFs** without confirmed redistribution rights. Commit an entry in
  `manifest.json` (hash, source URL, licence, tags) and fetch the file outside the repository.
- Generated fixtures belong in the test projects (`VectorPdfBuilder`, `TestPdfBuilder`), not here.
- Every file is identified by SHA-256; a failure report must include it, the page, the commit and
  the render parameters (07 "Reproducibility").

## Fetching

```powershell
pwsh eng/vectorpdf/corpus/fetch-corpus.ps1          # download pinned suites into cache/ and rewrite manifest.json
pwsh eng/vectorpdf/corpus/fetch-corpus.ps1 -Verify  # fail if cached files no longer match the manifest
```

Suites: [veraPDF corpus](https://github.com/veraPDF/veraPDF-corpus) (CC BY 4.0) and
[PDF 2.0 examples](https://github.com/pdf-association/pdf20examples) (CC BY-SA 4.0), each at a pinned
commit. `cache/` is git-ignored.

## Running

```powershell
dotnet run --project eng/vectorpdf/tools/VectorPdf.Tool -c Release -- corpus <folder> --out report.jsonl --diff
```

One JSON line per file: `sha256`, `pdfiumPages`/`pdfiumError`, `vectorPages`/`vectorError`,
`repaired`, `fallbackReasons` (counts per `PdfFallbackReason`), `fallbackAreaMean`,
`meanPixelDiff` (fully-vector pages only, 72 dpi) and `vectorMs`. Nothing leaves the machine.

`eng/vectorpdf/baseline-corpus-samples.jsonl` is the report for `samples/`; `eng/vectorpdf/baseline-corpus-summary.json`
is the gate summary for the fetched corpus (`--summary`). Investigate a page with
`vectorpdf diffpage <file> <page> <out.png> [--dump] [--content]`.

## Manifest

`manifest.schema.json` describes entries in `manifest.json`. Tags follow the taxonomy in 07.
