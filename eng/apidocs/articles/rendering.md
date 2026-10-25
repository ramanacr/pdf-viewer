# Rendering

## Display lists

The engine does not draw pixels itself. It interprets a page into a display list
([`IPdfDisplayList`](xref:PdfEngine.Vector.IPdfDisplayList)): an immutable, device-independent
list of drawing commands (paths, glyph runs, images, shadings, groups). A renderer replays the list.

```csharp
IPdfDisplayList list = await doc.GetPageDisplayListAsync(pageNumber);
```

- The document caches recent display lists. A second call for the same page is cheap.
- You can render one list many times, at any resolution or rotation.
- The list includes the annotations and form fields that the page shows on screen.
  `PdfVectorDocument.GetPagePrintDisplayListAsync` gives the page as it prints.

## Resolution and size

[`RenderRequest`](xref:PdfEngine.Rendering.RenderRequest) sets the output:

| Property | Meaning |
| --- | --- |
| `PageNumber` | The page, starting at 1. |
| `Dpi` | Pixels per inch (default 96). A page of W x H points gives W x Dpi / 72 by H x Dpi / 72 pixels. |
| `TargetWidthPixels`, `TargetHeightPixels` | A fixed output size. 0 computes the size from `Dpi`. |
| `Rotation` | A viewer rotation, added to the page's own `/Rotate`. |

The output is the crop box of the page, turned by the page's `/Rotate` and the viewer rotation.

## Pixels

[`RenderedPage`](xref:PdfEngine.Rendering.RenderedPage) holds the result:

- `Pixels`: 32-bit BGRA, premultiplied alpha, rows from top to bottom.
- `Stride`: bytes per row. Use it; do not assume `WidthPixels * 4`.
- The page is opaque: the renderer starts from white paper.
- Dispose the `RenderedPage` to release its buffer.

## The Skia renderer

[`SkiaVectorRenderer`](xref:PdfEngine.Vector.Skia.SkiaVectorRenderer) draws on Skia's CPU raster
canvas. It needs no GPU.

- Keep one instance for many renders. It caches paths, decoded images and typefaces between
  renders of the same page. Dispose it when you are done.
- Renders on one instance run one at a time. The instance is thread-safe.
- `RenderAsync` renders only a pixel window of the page
  ([`SkiaPixelRegion`](xref:PdfEngine.Vector.Skia.SkiaPixelRegion)), and can produce a night-mode
  (inverted) page. Use a region for deep zoom: the full page at a high resolution can need a very
  large buffer.
- `OutputSize` gives the full output size for a request before you render.

```csharp
var (width, height) = SkiaVectorRenderer.OutputSize(list, request);
SkiaRenderResult result = await renderer.RenderAsync(
    list, request, provider: null, region: new SkiaPixelRegion(0, 0, 1024, 1024),
    invertColors: false, CancellationToken.None);
using RenderedPage tile = result.Page;
```

## Fallback regions

Some content cannot be expressed as vector commands, or a backend cannot draw it faithfully (for
example a font program it cannot load). The engine marks these areas with
[`PdfFallbackToken`](xref:PdfEngine.Vector.PdfFallbackToken) entries.

- `IPdfDisplayList.FallbackTokens` lists the regions that the engine found.
- `SkiaVectorRenderer.AnalyzeAsync` adds the regions that only this backend finds.
- Pass an [`IPdfFallbackProvider`](xref:PdfEngine.Vector.IPdfFallbackProvider) to draw these
  regions with another renderer. Without a provider, the renderer draws them by best effort.
- `SkiaVectorRenderer.OutlineUnsupportedRegions` outlines regions that no provider drew.
