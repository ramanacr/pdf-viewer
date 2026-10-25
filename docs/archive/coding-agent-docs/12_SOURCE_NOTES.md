# Source Notes

## Primary repository

Repository assessed:
`https://github.com/ramanacr/pdf-viewer`

The assessment was based on the repository's current public source and documentation snapshot available on 2026-08-27.

Observed repository characteristics include:
- C# / WPF / .NET 9 Windows application;
- PDFium native engine;
- MVVM;
- asynchronous rendering;
- LRU cache;
- text search and extraction;
- annotations;
- bookmarks;
- printing;
- image export;
- installer/update infrastructure;
- SBOMs;
- automated tests.

## Key files inspected

- `README.md`
- `src/PdfViewer/PdfViewer.csproj`
- `src/PdfViewer/Services/PdfiumDocumentService.cs`
- `src/PdfViewer/ViewModels/MainViewModel.cs`
- `tests/PdfViewer.Tests/PdfServiceTests.cs`
- repository tree and related service/native bridge files.

## External market references

Used for strategic comparison:
- Mozilla/Google PDFium licensing/source information.
- Foxit PDF SDK feature documentation.
- Apryse/PDFTron WebViewer feature documentation.
- Adobe Acrobat product feature documentation.

## Important qualification

This is an engineering/product strategy assessment, not:
- a legal opinion;
- a formal security audit;
- a PDF/A certification;
- a WCAG/accessibility certification;
- a complete code review of every source line;
- a commercial market forecast.

Before commercial launch, perform dedicated legal, security, accessibility and compatibility reviews.
