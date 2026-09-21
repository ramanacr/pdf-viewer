# Repository Change Plan

## Add projects

Recommended:

```text
src/
  PdfEngine.Vector/
    PdfEngine.Vector.csproj
    Parsing/
    Objects/
    Xref/
    Streams/
    Document/
    Content/
    Graphics/
    Text/
    Fonts/
    Images/
    Color/
    DisplayList/
    Fallback/
    Diagnostics/
    Limits/

  PdfEngine.Vector.Windows/
    PdfEngine.Vector.Windows.csproj
    Direct2D/
    DirectWrite/
    Imaging/
    Composition/
    Interop/
```

If the Windows backend is very small in M0/M1, it may temporarily live under `PdfEngine.Vector/Rendering/Windows`, but split it before public SDK contracts stabilize.

## Add test projects or folders

Prefer separation once test volume grows:

```text
tests/
  PdfEngine.Vector.Tests/
    Parser/
    Xref/
    Objects/
    Content/
    DisplayList/
    Security/
    FuzzRegression/

  PdfEngine.Vector.Rendering.Tests/
    Golden/
    Differential/
    Zoom/
    Fonts/
    Transparency/
```

Keep existing `PdfViewer.Tests` for viewer regression tests.

## Add corpus tooling

```text
eng/vectorpdf/
  corpus/
    README.md
    manifest.schema.json
  tools/
    generate-fixtures.*
    render-diff.*
    corpus-runner.*
```

Do not commit third-party PDFs without confirmed redistribution rights. Store hashes/URLs/metadata when redistribution is not allowed.

## Solution updates

Add the new projects to `PdfViewer.slnx`.

Dependency direction:

```text
PdfEngine.Vector ---------> PdfEngine.Abstractions
PdfEngine.Vector.Windows -> PdfEngine.Vector + Abstractions
PdfEngine.Pdfium ---------> PdfEngine.Abstractions
PdfViewer.Core -----------> PdfEngine.Abstractions
PdfViewer ---------------> Core + Abstractions + engine composition projects
```

Avoid `PdfEngine.Vector -> PdfEngine.Pdfium`. Fallback orchestration should depend on abstractions/capability interfaces so PDFium remains detachable.

## Composition root

Create one engine-selection/composition location in the application startup. Example configuration:

```text
PDF_ENGINE_MODE=pdfium
PDF_ENGINE_MODE=vector
PDF_ENGINE_MODE=hybrid
```

Use a proper application setting/diagnostic switch rather than environment variables if the repo already has a settings mechanism. CI should exercise all applicable modes.

## Preserve existing paths

Do not move or delete:
- `src/PdfEngine.Pdfium`
- `eng/pdfium`
- `src/PdfViewer/runtimes/win-x64/native/pdfium.dll`

until the final PDFium exit gate.

## README changes during migration

Document:
- engine mode;
- vector coverage;
- fallback behavior;
- known limitations;
- how to run differential tests;
- whether a release still ships PDFium.

Do not claim “PDFium-free” until packaging tests prove `pdfium.dll` and its notices/assets are absent from the production artifact.
