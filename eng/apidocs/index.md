---
_layout: landing
---

# PdfEngine

PdfEngine is a PDF engine in pure C# for .NET 9. It is the engine of
[PDF Viewer](https://github.com/ramanacr/pdf-viewer). This site is the API reference of its NuGet
packages, with short guides.

| Package | Contents |
| --- | --- |
| `PdfEngine.Abstractions` | Contracts and models: documents, pages, render requests, rendered pages, text, search, annotations, forms, save options. No dependencies. |
| `PdfEngine.Vector` | The engine: parser and repair, document model, display lists, text extraction and search, editing, forms, signatures, redaction, saving. No native code. |
| `PdfEngine.Vector.Skia` | A portable CPU renderer of the display list, built on SkiaSharp. |

## Start here

1. [Getting started](articles/getting-started.md): install, open a PDF, extract text, save a page as PNG.
2. [Rendering](articles/rendering.md): display lists, resolution, regions, fallback regions.
3. [Text extraction and search](articles/text.md): words, page text, coordinates, search options.
4. [Security and limits](articles/security.md): hostile input, `PdfSecurityLimits`, encryption.
5. [Platform notes](articles/platforms.md): SkiaSharp native assets and fonts per platform.
6. [Versioning](articles/versioning.md): package versions and `engine-v*` tags.

The [API reference](xref:PdfEngine.Vector.Document.PdfVectorDocument) starts at
`PdfVectorDocument`, the entry point of the engine.

The API is not stable before version 1.0. Licence: MIT.
