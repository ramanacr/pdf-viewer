---
title: Use the engine as a library
permalink: /developers/
---

# Use the engine as a library

The PDF engine of PDF Viewer is also a set of NuGet packages for .NET 9 (`net9.0`). It is pure C#.
It opens, reads, searches, edits and saves PDF files, and the Skia package draws pages on any
platform that SkiaSharp supports.

The packages are a **preview** (`0.1.0-preview.1`). The API can change before version 1.0.

## Packages

| Package | Contents | Dependencies |
| --- | --- | --- |
| [`PdfEngine.Abstractions`](https://www.nuget.org/packages/PdfEngine.Abstractions) | Contracts and models: documents, pages, geometry, render requests, text and search, annotations, forms, save options | none |
| [`PdfEngine.Vector`](https://www.nuget.org/packages/PdfEngine.Vector) | The engine: parser and repair, display lists, text extraction and search, annotations, forms, digital signatures, redaction, page organisation, saving | `PdfEngine.Abstractions`, CoreJ2K, System.Security.Cryptography.Pkcs |
| [`PdfEngine.Vector.Skia`](https://www.nuget.org/packages/PdfEngine.Vector.Skia) | A portable CPU renderer of the display list | `PdfEngine.Vector`, SkiaSharp 3.116.1 |

Install the renderer. It brings the other two packages:

```shell
dotnet add package PdfEngine.Vector.Skia --prerelease
```

To read text only, without drawing, install `PdfEngine.Vector`.

## Minimal example

Open a PDF, print the text of page 1, search it, and save page 1 as a PNG:

```csharp
using PdfEngine.Rendering;
using PdfEngine.Text;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Skia;
using PdfEngine.Vector.Text;
using SkiaSharp;

using var doc = await PdfVectorDocument.OpenAsync("input.pdf");
Console.WriteLine($"{doc.PageCount} pages");

var text = new PdfVectorTextService();
Console.WriteLine(await text.ExtractPageTextAsync(doc, 1));

foreach (SearchMatch match in await text.SearchTextAsync(doc, "invoice", new SearchOptions { MatchCase = false }))
    Console.WriteLine($"page {match.PageNumber}: {match.ContextSnippet}");

using var renderer = new SkiaVectorRenderer();
using RenderedPage page = await renderer.RenderDisplayListAsync(
    await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 144 });
var info = new SKImageInfo(page.WidthPixels, page.HeightPixels, SKColorType.Bgra8888, SKAlphaType.Premul);
using var image = SKImage.FromPixelCopy(info, page.Pixels.Span, page.Stride);
using var png = File.Create("page1.png");
image.Encode(SKEncodedImageFormat.Png, 100).SaveTo(png);
```

`PdfVectorDocument.OpenAsync` also takes a `byte[]`, a password, and resource limits
(`PdfSecurityLimits`) for untrusted input.

## Platforms

- **Windows and macOS:** SkiaSharp brings its native library.
- **Linux:** also add `SkiaSharp.NativeAssets.Linux` (3.116.1).
- **WebAssembly and Android:** add the matching `SkiaSharp.NativeAssets.*` package. The
  [web demo](https://github.com/ramanacr/pdf-viewer/tree/main/src/PdfViewer.Web) and the
  [Android app](https://github.com/ramanacr/pdf-viewer/tree/main/src/PdfViewer.Android) in the
  repository show how.
- **Fonts.** Text in fonts that a PDF does not embed is drawn with installed fonts. A host
  without installed fonts (for example a browser) passes its own `SkiaFontSource`.

## In a web page

To show PDF files in a web page without writing .NET code, use the [web embed SDK](sdk/index.md): one
script, an API like the Adobe PDF Embed API, and the engine running in the reader's browser.

## Safety

- The engine has no JavaScript interpreter. Document scripts and launch actions are reported,
  never run.
- Parsing is bounded: object counts, recursion, decompression size and execution time have limits.
- The engine opens no network connection itself. A signature timestamp comes through a client
  that your code supplies (`IPdfTimestampClient`).

## API reference

The generated API reference will be at
[ramanacr.github.io/pdf-viewer/api/](https://ramanacr.github.io/pdf-viewer/api/). **Coming soon.**
Until then, the XML documentation comments in the packages show in your IDE, and the
[source](https://github.com/ramanacr/pdf-viewer/tree/main/src) is the reference.

More detail:

- [PdfEngine.Vector.Skia README](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfEngine.Vector.Skia/README.md):
  design, feature coverage, parity with Direct2D and known gaps.
- [Architecture](architecture.md) and the
  [engine design record](pdf-viewer-vector-migration/00_README.md).

## Versions and releases

Package releases have their own git tags: `engine-v0.1.0` gives version `0.1.0`. The
`packages.yml` workflow publishes them to nuget.org through Trusted Publishing. A local
`dotnet pack` gives version `0.0.0-dev` (see [Build from source](building.md#pack-the-engine-libraries)).

## Licence and support

MIT. Report problems and ask questions in the
[issue tracker](https://github.com/ramanacr/pdf-viewer/issues). Report security problems
privately, as [SECURITY.md](https://github.com/ramanacr/pdf-viewer/blob/main/SECURITY.md) says.
