# Contributing to PDF Viewer

Thank you for your interest. This project is a Windows PDF viewer with its own PDF engine, written
in C# and released under the [MIT licence](LICENSE). Contributions of all sizes are welcome: bug
reports, test files you are allowed to share, documentation fixes and code.

## Before you start

- For a small fix (a typo, a clear bug), open a pull request directly.
- For a new feature or a large change, open an issue first, so that we can agree on the approach
  before you spend time on it.
- Issues labelled [`good first issue`](https://github.com/ramanacr/pdf-viewer/labels/good%20first%20issue)
  are small and self-contained: a good place to start.

## Project rules

These rules protect the people who use the viewer. A pull request that breaks one is not merged.

1. **No telemetry.** The application sends nothing about its users or their documents. The only
   network requests are the ones listed in the [privacy policy](docs/privacy.md), and each one is
   started by the user.
2. **No document text in diagnostics.** Logs, error messages and reports may contain page numbers,
   object numbers and sizes, but never text from a document.
3. **Document scripts never run.** The viewer does not execute JavaScript or launch actions from a
   PDF.
4. **No third-party PDFs in the repository.** Test documents are built by code in the test
   projects, or fetched into the git-ignored corpus cache by `eng/vectorpdf/corpus/fetch-corpus.ps1`
   with their source and licence recorded. Do not commit a PDF you did not create.
5. **No new dependency without a reason.** Explain in the pull request why a package is needed,
   state its licence, and update `THIRD_PARTY_NOTICES.md` and the SBOM (a test checks this).

## Set up

You need Windows 10 (1809 or later) or Windows 11, and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/ramanacr/pdf-viewer.git
cd pdf-viewer
dotnet build PdfViewer.slnx
dotnet run --project src/PdfViewer
```

## Project layout

| Project | What it contains |
|---|---|
| `src/PdfEngine.Vector` | The PDF engine: parsing, fonts, text, forms, signatures, redaction, PDF/A. Plain `net9.0`, no native code. |
| `src/PdfEngine.Abstractions` | Contracts shared by the engine and its renderers, including the display list. |
| `src/PdfEngine.Vector.Direct2D` | The Windows renderer (Direct2D). |
| `src/PdfEngine.Vector.Windows` | The WPF fallback renderer. |
| `src/PdfViewer`, `src/PdfViewer.Core` | The WPF application and its view models. |
| `src/PdfEngine.Pdfium` | PDFium, used only in development and test builds as a reference. |
| `tests/` | xUnit test projects. |
| `eng/` | Build, release and corpus tools. |

The engine turns each page into a device-independent display list; a renderer replays that list.
New platforms add a renderer and reuse the engine unchanged.

## Tests

Run the tests that cover your change while you work, and the full suites before you open the pull
request:

```powershell
dotnet test tests/PdfEngine.Vector.Tests
dotnet test tests/PdfViewer.Tests
dotnet test tests/PdfViewer.Tests -p:PdfiumRenderer=false   # the configuration that is published
```

A bug fix comes with a test that fails without the fix. A rendering change is compared with the
reference renderer in the existing differential tests, with a stated tolerance.

## Pull requests

- Keep one change per pull request, and describe what changed and why.
- Match the style of the code around your change.
- CI must pass. A maintainer reviews every pull request before it is merged.
- By opening a pull request, you agree that your contribution is released under the MIT licence.

## Reporting a security problem

Do not open a public issue for a security problem. Follow [SECURITY.md](SECURITY.md).

## Code of conduct

Everyone taking part in this project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
