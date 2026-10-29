# Platform notes

`PdfEngine.Abstractions` and `PdfEngine.Vector` are pure managed code for `net9.0`. They run
wherever .NET 9 runs. `PdfEngine.Vector.Skia` also needs Skia's native library, which SkiaSharp
(3.116.1) supplies per platform.

| Platform | Native Skia library | Fonts for text that a PDF does not embed |
| --- | --- | --- |
| Windows | Included with `SkiaSharp` | The installed fonts (`Windows\Fonts`) |
| macOS | Included with `SkiaSharp` | The .NET fonts folder, then Skia's font manager by family name |
| Linux | Add `SkiaSharp.NativeAssets.Linux` 3.116.1 | The .NET fonts folder, then Skia's font manager by family name |
| Android (`net9.0-android`) | Add `SkiaSharp.NativeAssets.Android` 3.116.1 | `/system/fonts`, with Android stand-ins for the Windows families (Roboto for Arial) |
| Browser (Blazor WebAssembly) | Add `SkiaSharp.NativeAssets.WebAssembly` 3.116.1 and set `WasmBuildNative` | None: pass your own `SkiaFontSource` |

Use the same SkiaSharp version as `PdfEngine.Vector.Skia` for the native assets.

## Fonts

[`SkiaFontSource`](xref:PdfEngine.Vector.Skia.SkiaFontSource) decides where substitute fonts come
from. The default reads `SystemFontCatalog.Installed`. A host without installed fonts, or a host
that wants the same output on every machine, passes a catalog of a font folder that it ships:

```csharp
using PdfEngine.Vector.Fonts;
using PdfEngine.Vector.Skia;

var fonts = new SkiaFontSource(SystemFontCatalog.ForDirectory(fontFolder));
using var renderer = new SkiaVectorRenderer(fonts: fonts);
```

A browser host can derive from `SkiaFontSource` to fetch font files on demand (see `IsPending`).
The [web demo](https://github.com/ramanacr/pdf-viewer/tree/main/src/PdfViewer.Web) does this.

## WebAssembly

The browser demo builds with the `wasm-tools` workload (`dotnet workload install wasm-tools`) and
links Skia into the runtime (`WasmBuildNative`). See `src/PdfViewer.Web` and
`.github/workflows/web-demo.yml` in the repository for a working setup.
