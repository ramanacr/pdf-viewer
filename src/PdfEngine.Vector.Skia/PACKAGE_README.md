# PdfEngine.Vector.Skia

A portable renderer for [PdfEngine.Vector](https://www.nuget.org/packages/PdfEngine.Vector). It
replays a page's display list on a [SkiaSharp](https://github.com/mono/SkiaSharp) CPU raster canvas
and returns BGRA pixels. It needs no GPU and no Windows API.

```csharp
using PdfEngine.Rendering;
using PdfEngine.Vector.Document;
using PdfEngine.Vector.Skia;

using var doc = await PdfVectorDocument.OpenAsync("input.pdf");
using var renderer = new SkiaVectorRenderer();
using RenderedPage page = await renderer.RenderDisplayListAsync(
    await doc.GetPageDisplayListAsync(1), new RenderRequest { PageNumber = 1, Dpi = 144 });
// page.Pixels: BGRA, premultiplied, page.Stride bytes per row.
```

SkiaSharp brings the native Skia library for Windows and macOS. On Linux, add
`SkiaSharp.NativeAssets.Linux` (3.116.1); for WebAssembly or Android, the matching
`SkiaSharp.NativeAssets.*` package. Fonts that a PDF does not embed are taken from the installed
fonts; a host without installed fonts passes its own `SkiaFontSource`.

The API is not stable before version 1.0. Licence: MIT.
