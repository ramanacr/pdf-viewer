# Getting started

## Install

The packages target `net9.0`. `PdfEngine.Vector.Skia` brings the other two packages with it.

```shell
dotnet add package PdfEngine.Vector.Skia --prerelease
```

Until the first release on nuget.org, the packages are a build artifact of the
[Engine Packages workflow](https://github.com/ramanacr/pdf-viewer/actions/workflows/packages.yml).
To make them yourself:

```shell
dotnet pack src/PdfEngine.Vector.Skia/PdfEngine.Vector.Skia.csproj -o packages
```

Do the same for `PdfEngine.Abstractions` and `PdfEngine.Vector`. A local pack is version `0.0.0-dev`.

## Open a PDF, extract text, save a page as PNG

```csharp
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Skia;
using PdfEngine.Vector.Text;
using SkiaSharp;

using var doc = await PdfVectorDocument.OpenAsync("input.pdf");
Console.WriteLine(await new PdfVectorTextService().ExtractPageTextAsync(doc, 1));

using var renderer = new SkiaVectorRenderer();
using RenderedPage page = await renderer.RenderDisplayListAsync(
    await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 144 });
var info = new SKImageInfo(page.WidthPixels, page.HeightPixels, SKColorType.Bgra8888, SKAlphaType.Premul);
using var image = SKImage.FromPixelCopy(info, page.Pixels.Span, page.Stride);
using var png = File.Create("page1.png");
image.Encode(SKEncodedImageFormat.Png, 100).SaveTo(png);
```

What the code does:

1. [`PdfVectorDocument.OpenAsync`](xref:PdfEngine.Vector.Document.PdfVectorDocument) reads the
   file and parses its structure. Page numbers start at 1.
2. [`PdfVectorTextService`](xref:PdfEngine.Vector.Text.PdfVectorTextService) extracts the text of
   page 1.
3. `GetPageDisplayListAsync` interprets page 1 into an immutable display list.
4. [`SkiaVectorRenderer`](xref:PdfEngine.Vector.Skia.SkiaVectorRenderer) draws the list into BGRA
   pixels at 144 pixels per inch.
5. SkiaSharp encodes the pixels as PNG.

On Linux, also add `SkiaSharp.NativeAssets.Linux`. See [Platform notes](platforms.md).

## Next steps

- [Rendering](rendering.md)
- [Text extraction and search](text.md)
- [Security and limits](security.md)
