---
title: Use the engine as a library
permalink: /developers/
---

# Use the engine as a library

The PDF engine of PDF Viewer is also a set of NuGet packages for .NET 9 and .NET 10 (`net9.0` and `net10.0`). It is pure C#.
It opens, reads, searches, edits and saves PDF files, and the Skia package draws pages on any
platform that SkiaSharp supports.

The packages are a **preview** (`0.2.0-preview.1`). The API can change before version 1.0.

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
It is also on npm as [`pdfviewer-embed`](https://www.npmjs.com/package/pdfviewer-embed).

## Safety

- The engine has no JavaScript interpreter. Document scripts and launch actions are reported,
  never run.
- Parsing is bounded: object counts, recursion, decompression size and execution time have limits.
- The engine opens no network connection itself. A signature timestamp comes through a client
  that your code supplies (`IPdfTimestampClient`).

## API reference

The generated API reference is at
[ramanacr.github.io/pdf-viewer/api/](https://ramanacr.github.io/pdf-viewer/api/), with short
guides. The XML documentation comments in the packages also show in your IDE.

More detail:

- [PdfEngine.Vector.Skia README](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfEngine.Vector.Skia/README.md):
  design, feature coverage, parity with Direct2D and known gaps.
- [Architecture](architecture.md) and the
  [engine design record](pdf-viewer-vector-migration/00_README.md).

## Versions and releases

Package releases have their own git tags: `engine-v0.1.0` gives version `0.1.0`. The
`packages.yml` workflow publishes them to nuget.org through Trusted Publishing. A local
`dotnet pack` gives version `0.0.0-dev` (see [Build from source](building.md#pack-the-engine-libraries)).

The package build uses the .NET 10 SDK, so each package has `net9.0` and `net10.0` assemblies. A
build with the .NET 9 SDK makes only `net9.0`. Each pack also runs package validation: the
`net10.0` assemblies must have all the API of the `net9.0` ones. From version 1.0, the pack also
compares the API with the last released package and fails on a breaking change.

## Changes in 1.0

Version 1.0 changes the API of `0.2.0-preview.1`. Update your code as follows.

### Renamed types

| Before | After |
| --- | --- |
| `PdfEngine.Vector.Forms.PdfFieldBehaviour` | `PdfEngine.Vector.Forms.PdfFieldBehavior` |
| `PdfEngine.Vector.PdfA.PdfAFlavour` | `PdfEngine.Vector.PdfA.PdfAFlavor` |
| `PdfEngine.Vector.Forms.PdfFormFiller.Appearance` (nested) | `PdfEngine.Vector.Forms.PdfDefaultAppearance` (top-level) |
| `PdfEngine.Vector.Text.TextRect` | `PdfEngine.Vector.Text.PdfTextRect` |
| `PdfEngine.Vector.Parsing.MemoryByteSource` | `PdfEngine.Vector.Parsing.PdfMemoryByteSource` |
| `PdfEngine.Vector.Parsing.FileByteSource` | `PdfEngine.Vector.Parsing.PdfFileByteSource` |
| `PdfEngine.Vector.Parsing.MemoryRangeReader` | `PdfEngine.Vector.Parsing.PdfMemoryRangeReader` |

### Renamed members

| Before | After |
| --- | --- |
| `PdfFormFiller.ParseDA` | `PdfFormFiller.ParseDefaultAppearance` |
| `PdfFormFiller.Appearance.ColourOps` | `PdfDefaultAppearance.ColorOps` |
| `PdfAConversionOptions.Flavour` | `PdfAConversionOptions.Flavor` |
| `PdfAReport.Flavour` | `PdfAReport.Flavor` |
| `PdfAValidator.ClaimedFlavour` | `PdfAValidator.ClaimedFlavor` |
| `PdfAValidator.Validate` parameter `flavour` | parameter `flavor` |
| `PdfPreflightProfile.DeviceIndependentColour` | `PdfPreflightProfile.DeviceIndependentColor` |
| `PdfPreflightProfile.ListSpotColours` | `PdfPreflightProfile.ListSpotColors` |
| `PdfStamps.RotatedBounds` parameter `centre` | parameter `center` |

### Moved to a different namespace

| Type | Before | After |
| --- | --- | --- |
| `SystemFontCatalog`, `SystemFontFace` | `PdfEngine.Vector.Editing` | `PdfEngine.Vector.Fonts` |
| `PdfAnnotation`, `PdfAnnotationReader` | `PdfEngine.Vector.Services` | `PdfEngine.Vector.Annotations` |
| `PdfTextStrings` | `PdfEngine.Vector.Services` | `PdfEngine.Vector.Objects` |

### Other changes

- `PdfFieldValidationException`, `PdfDataNotLoadedException` and `PdfAConversionRefusedException`
  now derive from `PdfVectorException`. Their `Kind` is one of the new `PdfVectorErrorKind` values
  `FieldValidation`, `DataNotLoaded` and `ConversionRefused`.
- `PdfAConversionRefusedException` no longer derives from `InvalidOperationException`. If you catch
  `InvalidOperationException` around `PdfAConverter.Convert`, also catch `PdfAConversionRefusedException`.
- If you catch `PdfVectorException` while you read a document that loads by HTTP ranges
  (`PdfRangedDocument`), let `PdfDataNotLoadedException` through. `PdfRangedDocument` catches it,
  loads the missing bytes and tries again.
- `PdfSignatureValidator.RevisionEnds` returns `IReadOnlyList<long>`. Before, it returned `List<long>`.
- New: `PdfObjectResolver.DecodeStream(PdfStream)` returns the decoded data of a stream. Use it
  instead of the stream decoder, which is now internal.

### Types and members that are now internal

These were implementation details. They are not part of the 1.0 API:

- Parsing: the lexer, the parser and their tokens, the byte-range loading internals and the
  cross-reference entries. The constructors and loaders of `PdfXrefTable`, `PdfObjectResolver`,
  `PdfPageTree` and `PdfPageNode` are internal. Their read members stay public, for example
  `PdfObjectResolver.Resolve` and `PdfXrefTable.Trailer`.
- Streams: `PdfStreamDecoder`, `PdfDecodedStream` and `PredictorDecoder`.
  Use `PdfObjectResolver.DecodeStream`.
- PDF functions, colour spaces (`PdfColorSpace` and its nested types), the graphics state and the
  content interpreter.
- Fonts: `PdfFont`, the font resolver, font program preparation, the standard 14 fonts tables and
  `TrueTypeFontFile`. `SystemFontCatalog.Load` is internal.
- PDF/A and XMP internals, the incremental writer, the page tree editor and other writing helpers.
- The paint observer (`IPdfPaintObserver`, `PdfPaintEvent`, `PdfPaintKind`) and `PdfVectorPage`
  (use `IPdfPage`).
- Security handlers: `Create`, `DecryptObject` and `EncryptObject`. You cannot derive your own
  security handler.
- `SkiaJpeg` in `PdfEngine.Vector.Skia` and `IPdfVectorPageVisual` in `PdfEngine.Abstractions`.

## Licence and support

MIT. Report problems and ask questions in the
[issue tracker](https://github.com/ramanacr/pdf-viewer/issues). Report security problems
privately, as [SECURITY.md](https://github.com/ramanacr/pdf-viewer/blob/main/SECURITY.md) says.
