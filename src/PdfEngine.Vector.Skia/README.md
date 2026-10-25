# PdfEngine.Vector.Skia

A portable renderer for the vector engine's display list (`IPdfDisplayList`), built on
[SkiaSharp](https://github.com/mono/SkiaSharp) 3.116.1 (MIT). It targets plain `net9.0` and draws on
Skia's CPU raster canvas, so it has no dependency on Windows, Direct2D or a GPU. It is step 1 of a
web viewer (Blazor WebAssembly) and of the Android proof of concept (src/PdfViewer.Android). The WPF
application does not use it yet.

The reference is the Direct2D backend (`src/PdfEngine.Vector.Direct2D`). The Skia backend replays
the same display list with the same semantics, and its tests compare every fixture with a
Direct2D render of the same page.

## Use

```csharp
using var renderer = new SkiaVectorRenderer();
using RenderedPage page = await renderer.RenderDisplayListAsync(displayList,
    new RenderRequest { PageNumber = 1, Dpi = 96 });
// page.Pixels: BGRA, premultiplied, opaque (white paper), page.Stride bytes per row.
```

`RenderAsync(list, request, provider, region, invertColors, ct)` renders a pixel window of the page
(`SkiaPixelRegion`) and night mode, and returns the fallback tokens it found. `AnalyzeAsync` lists
the regions that this backend cannot draw.

## Design

1. **Replay.** `SkiaReplayer` keeps the current transformation matrix itself and sets
   `user space → device` on the canvas before each draw, as the Direct2D replayer sets its context
   transform. Paths are built once per page and kept with the replayer. The renderer keeps the
   replayers of the last six pages.
2. **Clips** are canvas saves with an antialiased `ClipPath`. They cost no offscreen pixels.
   Opacity groups that contain no blending are Skia layers (`SaveLayer`).
3. **Compositing groups** (blend mode, group alpha, soft mask, knockout) are drawn into an
   offscreen bitmap of the group's device rectangle and composited under the enclosing clips with
   `SKBlendMode`. Skia's 15 non-Normal blend modes are the W3C and PDF formulas. Soft masks,
   non-isolated groups (backdrop removal, ISO 32000-2 11.4.8) and knockout elements are composited
   per pixel on the CPU with the formulas of the Direct2D backend.
4. **Strokes** follow PDF: the width is in user space. A stroke narrower than one device pixel is a
   one-pixel hairline. A stroke of exactly one device pixel is widened by 0.2 %: Skia otherwise
   draws it as a hairline through the pixel centres, which leaves out the neighbouring pixels that
   the true one-pixel band covers.
5. **Text.** An embedded TrueType or CFF program (as the display list carries it, an sfnt) is
   loaded with `SKTypeface.FromData`, from memory. A font that is not embedded gets the Direct2D
   backend's substitute chain: `SystemFontCatalog.Match`, then the class family, then Unicode
   fallback families, with each glyph fitted to the document's `/Widths`. Such text reaches the
   backend as characters, so Arabic and Indic words are shaped with the substitute's GSUB and
   GPOS (`SkiaReplayer.Shaping.cs`, with the editing shaper `TextShaper`): joining forms,
   ligatures, marks, pre-base vowel signs, reph and conjuncts, each word fitted to its widths.
   Arabic is taken in the order it is seen and put back in reading order once before it is
   shaped. Embedded programs are not shaped. Glyphs are filled as
   outlines (no hinting), as Direct2D's outline rendering mode draws them. Text clips and stroking
   render modes use the same outlines.
6. **Fonts on other platforms.** Substitutes come from a `SkiaFontSource`. The default reads the
   installed fonts. A host without installed fonts (a browser) passes a catalog of the fonts it
   bundles (`SystemFontCatalog.ForDirectory`) or overrides `Load` and `MatchFamily`. A source
   that fetches its faces on demand also overrides `IsPending`: a run whose substitute is still
   being fetched waits for it (it is reported as not drawn, and the host draws the page again
   once the face is there) instead of falling through to the next face of the chain. The web
   demo's `OnDemandFontSource` serves the Liberation fonts this way. A substitute that has none
   of a run's glyphs is not used as a best effort, so the run stays reported as not drawn.
7. **Images.** The engine decodes most images to BGRA (masks and `/Decode` applied). JPEG data is
   decoded with Skia's codec (`SKCodec`). For a JPEG whose colours the core must map, the raw gray
   or RGB samples go to `MapJpegComponents`. JPEG soft masks are decoded as gray. Sampling matches
   Direct2D: mipmapped linear when reduced, nearest when enlarged without `/Interpolate`.
8. **Shadings.** Axial and focal radial shadings are Skia gradients. Two-circle radials, function
   shadings (type 1) and mesh shadings (types 4 to 7) are evaluated per device pixel.
   `SkiaShadingRasterizer` is the same code as Direct2D's `ShadingRasterizer`.
9. **Tiling patterns.** One period of the cell is rasterized at device resolution and repeated with
   an image shader.
10. **Night mode** inverts the finished page, so it is exactly the inverted day page.
11. **Fallback regions.** Provider pixels are drawn last over their region. With
    `OutlineUnsupportedRegions`, a region without pixels is tinted and outlined.

## Feature coverage

| Display list command | Skia backend |
|---|---|
| `SaveState`, `RestoreState`, `ConcatTransform` | Yes |
| `FillPath` | Yes: nonzero and even-odd, antialiased |
| `StrokePath` | Yes: caps, joins, miter limit, dashes (odd arrays repeat), device-pixel hairlines |
| `PushClip`, `PopClip` | Yes |
| `PushStrokeClip` | Yes: stroke outline from `SKPaint.GetFillPath` |
| `PushTextClip` | Yes: union of glyph outlines; Type 3 glyphs add nothing |
| `DrawGlyphRun` | Yes: embedded TrueType and CFF (with glyph matrix), substitutes, render modes 0 to 7 |
| `DrawImage` | Yes, except four-component (CMYK, YCCK) JPEG data, which is classified (see gaps) |
| `DrawShading` | Yes: axial, radial, two-circle radial, function, free-form, lattice, Coons and tensor meshes |
| `DrawTilingPattern` | Yes: coloured and uncoloured, fills and strokes |
| `BeginTransparencyGroup` / `End…` | Yes |
| `BeginCompositingGroup` / `End…` | Yes: 16 blend modes, group alpha, luminosity and alpha soft masks (backdrop colour, `/TR`), isolated, non-isolated and knockout groups (knockout up to 256 elements, as in Direct2D) |
| `DrawFallbackRegion` | Provider pixels composited last; strict-mode outline |
| Night mode, page and viewer rotation, crop box, pixel regions | Yes |

## Parity with Direct2D

All numbers are channel differences on a 0 to 255 scale, measured on Windows (Direct2D on the
development machine's default device). "Bad" is the share of pixels with a channel off by
more than 64, the measure of the engine's PDFium differential tests.

**Fixtures** (`tests/PdfEngine.Vector.Skia.Tests`, 219 renders, mostly at 72 and
144 dpi): 198 renders have a mean difference of at most 0.5, 211 of at most 1.0, and 197 have no
bad pixel. Each test states its own budget. By area:

| Area | Mean difference | Bad pixels |
|---|---|---|
| Paths, strokes, clips, transforms, rotation | 0.00 to 1.29 (hairlines worst) | at most 0.44 % |
| Images | 0.00 to 1.54 | at most 2.13 % (an enlarged, rotated checker without interpolation) |
| Shadings (axial, radial, two-circle, function, mesh) | 0.00 to 0.30 | none |
| Text (embedded, substituted, CFF, render modes, clips) | 0.00 to 1.26 at 72 dpi, up to 2.05 for body text at 96 dpi | at most 0.91 % |
| Blend modes, soft masks, groups, knockout | 0.00 to 0.36 | none |
| Tiling patterns | 0.05 to 1.24 | none |
| Night mode | 0.18 | none |

**Corpus sample** (`CorpusParityReport`, first page of 200 files of the local corpus checkout,
seeded random sample, 96 dpi, one process): mean difference per page averages 0.26, median 0.01,
95th percentile 1.41, largest 14.28. 185 pages have a mean of at most 1 and 196 of at most 3.
Bad pixels average 0.14 % per page. The worst page (mean 14.28) holds a CMYK JPEG that this
backend classifies for fallback, so with no fallback provider the image area stays blank. The next
pages (means 1.5 to 3.8) are text pages, where the text-weight difference below shows. The Skia
backend classified content on 4 of the 200 pages. Median render time per page: Skia 2.56 ms,
Direct2D 6.00 ms (these pages are small; this is not a benchmark).

To run the sample: set `SKIA_PARITY_CORPUS` to the corpus folder (`eng/vectorpdf/corpus/cache`),
optionally `SKIA_PARITY_PAGES` (at most 200) and `SKIA_PARITY_OUT` (summary file), and run the
`CorpusParityReport` test. The summary holds file paths and numbers, no document text.

## Known gaps

1. **CMYK and YCCK JPEG.** Skia gives no raw CMYK samples for the core's colour mapping, and
   SkiaSharp 3.116.1 decoded the CMYK and YCCK JPEGs of the corpus as solid black. The backend
   therefore classifies four-component JPEG data as `ImageDecode` fallback. A portable JPEG decoder
   would close this gap.
2. **Text weight.** Direct2D draws thin features with more ink than Skia: 26 % more on the
   body-text fixture at 96 dpi, and hairlines are heavier too. Large shapes match. Small text
   therefore differs by a level or two along stems (mean 2.05 at 96 dpi, 0.84 at 150 dpi).
3. **Fonts outside Windows.** The substitute and Unicode fallback family names are Windows fonts.
   A web or Android host must supply its own fonts through `SkiaFontSource` (the web demo
   bundles Liberation, which stands in by `SystemFontCatalog.LiberationFamilyAliases`). Bare Type 1 and CFF
   programs that the engine does not wrap as sfnt are substituted, as in Direct2D.
4. **Only tested on Windows.** The library is portable, but it has run only with
   `SkiaSharp.NativeAssets.Win32`. WebAssembly and Android need their own SkiaSharp native assets,
   which are not referenced yet.
5. **Not in scope here:** printing, shared GPU textures and render preparation (Direct2D-only
   features), and tiling of very large outputs: a render allocates the whole region at once.
6. **Cost.** Everything is CPU work. Groups, soft masks and knockout elements allocate offscreen
   bitmaps per render; nothing is cached across renders except paths, glyph outlines, decoded
   images (256 MB budget by default) and tiling cells.
