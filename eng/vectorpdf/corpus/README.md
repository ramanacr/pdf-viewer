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

## Nightly gate

CI's `corpus` job (nightly, and on demand through *Run workflow*) fetches and hash-verifies the
suites (cached between runs), runs `corpus --diff --software` (runners have no GPU, so Direct2D
renders on WARP) and compares the summary with `eng/vectorpdf/baseline-corpus-summary.warp.json`:

```powershell
pwsh eng/vectorpdf/corpus/compare-summary.ps1 -Baseline eng/vectorpdf/baseline-corpus-summary.warp.json -Current corpus-summary.json
```

It fails when fewer files open, untyped errors appear, the fully-vector page share drops by more
than 0.2 % (area share 0.1 %), or the worst pixel difference grows by more than 1/255. New fallback
reasons and improvements are listed in the job summary. The summary and the per-file report are
kept as a run artifact for 90 days, which is the trend record. When a change improves the corpus,
regenerate the baseline with the same command locally (`--software`) and commit it.

## Manifest

`manifest.schema.json` describes entries in `manifest.json`. Tags follow the taxonomy in 07.
