# PdfViewer.Web: the engine in a browser

A PDF viewer that runs **the project's own C# engine, `PdfEngine.Vector`, inside the browser
as .NET WebAssembly**, and draws pages with its portable Skia backend
(`PdfEngine.Vector.Skia`). It is a standalone Blazor WebAssembly app: static files only, with no
server code.

- **Nothing is uploaded.** A PDF you open is read into the tab's memory and forgotten when you
  close it. The site has no back end to send it to.
- **No telemetry.** No analytics, no cookies, no third-party requests. The page sets a
  Content-Security-Policy of `'self'` only. The theme and side-panel choices are kept in the
  browser's local storage; nothing else is stored.
- **Document JavaScript never runs.** The engine has no script interpreter.
- Diagnostics shown on screen are numbers and exception type names, never document text.

Status: a demo, and the base of an embeddable viewer SDK (see [next steps](#next-steps)).

## Features

| Feature | Notes |
|---|---|
| Open a local PDF | File picker (`Ctrl+O`) or drag and drop anywhere on the page. Up to 512 MB. |
| Built-in sample | "Engine tour": four pages written by `DemoDocument` in the browser when asked for, so the repository ships no binary sample. Its text uses a Type 3 stroke font drawn for this project, so it needs no font file. |
| Rendering | `SkiaVectorRenderer` into an HTML canvas, at the device pixel ratio. |
| Continuous scroll | All pages laid out at their size; only pages near the view are drawn, nearest the current page first, one at a time. |
| Memory bounds | Rendered bitmaps: 160 MB LRU cache. Canvases more than 4 pages away are emptied. Engine: display lists bounded by command count, decoded images 64 MB, text pages by character count. Largest bitmap 4096 × 4096 pixels. |
| Zoom | Fit width, fit page, 50 % to 400 % presets, `Ctrl` `+` / `-`. Pages show the stretched old bitmap until the new one is drawn. |
| Search | The engine's text extraction and its PDFium-compatible search rules. Highlights per line, a results list with context, `Enter` / `F3` / `Shift+F3`, match case. |
| Outline | The document's bookmarks, with the current page marked. |
| Encrypted PDFs | Password prompt. RC4 and AES (128 and 256) open, see [crypto](#cryptography). |
| Night pages | The engine's night mode: the page inverted after drawing (`I`). |
| Theme | System, light or dark (`D`), independent of night pages. |
| Bundled fonts | Text in fonts a PDF does not embed is drawn with the Liberation fonts 2.1.5 (Sans, Serif, Mono; regular, bold, italic, bold italic; SIL Open Font License 1.1), shipped in `wwwroot/fonts` with their licence (`OFL.txt`). Each face is fetched the first time a page needs it and kept for the session. See [fonts](#fonts). |
| Device fonts | Chromium browsers can lend the page the fonts installed on the device (Local Font Access API, after the browser asks), for text the bundled fonts do not cover. |
| Accessibility | Toolbar with labels and keyboard access, live status for search and page changes, focus outlines, reduced motion respected, each page labelled "Page n of m". |
| Layout | Responsive: below 760 px the toolbar compacts and the side panel overlays the pages. |

Keyboard shortcuts are listed in the app (`?`).

## Architecture

```
 Browser tab
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ wwwroot/js/viewer.js     canvas drawing (BGRA → RGBA), file reading,     │
 │                          drag and drop, IntersectionObserver, keyboard   │
 ├──────────────────────────── JS interop ──────────────────────────────────┤
 │ PdfViewer.Web            Blazor UI: Viewer.razor (toolbar, side panel,   │
 │ (Blazor WebAssembly)     pages, status), render scheduling, cache        │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ PdfViewer.Web.Engine     ViewerDocument: open, render, search, outline,  │
 │ (net9.0, no UI)          fonts; PageBitmapCache; DemoDocument;           │
 │                          PlatformCrypto                                  │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ PdfEngine.Vector         parser, content interpreter → display list,     │
 │ PdfEngine.Vector.Skia    text extraction; Skia replay of the display     │
 │                          list (libSkiaSharp linked into dotnet.native.wasm) │
 └──────────────────────────────────────────────────────────────────────────┘
```

1. **The engine-facing layer is UI-free.** `src/PdfViewer.Web.Engine` knows nothing about Blazor
   or the DOM. It is tested on Windows in `tests/PdfEngine.Vector.Skia.Tests`
   (`WebViewerEngineTests`) and is the part an embeddable SDK reuses.
2. **One engine operation at a time.** Browser .NET runs on one thread, and a blocking wait
   throws there. `ViewerDocument` serializes every call with an async gate, so the engine's
   internal locks are never contended. Pages render one by one, and the UI yields to the
   browser between pages so that it can paint.
3. **Pixels cross into JavaScript once.** The renderer writes BGRA straight into a managed array;
   `[JSImport]` hands the canvas script a view of that memory, which copies it into an
   `ImageData` and swaps two channels.
4. **Skia is linked statically.** `SkiaSharp.NativeAssets.WebAssembly` 3.116.1 (the same pinned
   version as the rest of the solution) provides `libSkiaSharp.a`, linked into the runtime by
   the `wasm-tools` workload (Emscripten). There is no GPU or WebGL use: Skia's CPU raster.

### Engine changes for the browser

All guarded so that Windows behaves exactly as before:

| Where | What the browser needs | Change |
|---|---|---|
| `Text/TextPrefetcher` | `new Thread(...).Start()` throws in single-threaded WebAssembly. | No background worker when `PdfPlatform.CanStartThreads` is false. Page text is built on demand. |
| `Editing/SystemFontCatalog.Installed`, `Text/TextFontInfo` | There is no font folder to scan. | Empty catalog and index when `PdfPlatform.HasInstalledFonts` is false. Hosts pass their own catalog. |
| `Security/PortableCrypto` | Browser .NET 9 has no MD5 and no AES (they throw `CryptographicException`). | The Standard security handler uses the platform's MD5 and AES where they work and managed ones (RFC 1321, FIPS 197) where they do not. |
| `Color/DefaultColorProfiles` | No Windows or Adobe profiles. | No change needed: it checks `OperatingSystem.IsWindows()` and falls back to the built-in SWOP model. |
| `Images/PdfImageSource` (JPEG 2000) | — | No thread of its own in this branch: CoreJ2K decodes on the calling thread. Not exercised in the browser yet (no JPX sample). |
| `Parallel.For` (colour transforms, shadings) | — | Runs inline on one thread. No change. |

`PdfPlatform.Simulate(...)` and `PortableCrypto.UseManagedImplementations()` let tests exercise
the browser paths on Windows (`PlatformGuardTests`, `PortableCryptoTests`).

## Run locally

Prerequisites: the .NET 9 SDK and its `wasm-tools` workload.

```powershell
dotnet workload install wasm-tools
dotnet run --project src/PdfViewer.Web          # development server with hot reload
```

To try the published site, as GitHub Pages serves it:

```powershell
dotnet publish src/PdfViewer.Web -c Release -o publish/web
node eng/webdemo/serve.mjs publish/web/wwwroot --port 8080 --gzip
# open http://127.0.0.1:8080/
```

`eng/webdemo/serve.mjs` is a dependency-free static server (Node 18+) that listens on
127.0.0.1 only. `--base /pdf-viewer/` serves under a project path (rewrite `<base href>` in
`index.html` to match), and `--gzip` compresses as GitHub Pages does, for realistic sizes.

If you have more than one SDK installed, build with a .NET 9 SDK: SDK 10 would need the
separate `wasm-tools-net9` workload for this net9.0 project. On Windows, a very deep checkout
path can make the native link exceed the 260-character path limit; a short path avoids it.

## Publishing

`.github/workflows/web-demo.yml` publishes the app on every pull request and push to `main`, and
deploys `main` to GitHub Pages under `/pdf-viewer/`. The deploy job runs only after a one-time
switch by the repository owner:

1. **Settings → Pages → Source: GitHub Actions.**
2. **Settings → Secrets and variables → Actions → Variables:** `WEB_DEMO_PAGES` = `true`.

Until then the workflow builds, rewrites the base href, reports the sizes and stops, and the
existing site (Pages from `main` `/docs`) is untouched. After the switch, Pages serves only what
the workflow deploys. To keep the `/docs` pages as well, copy `docs/` into the artifact next to
the app (one more step in the workflow) or move the demo to a subfolder of it.

## Limits

1. **Fonts that a PDF does not embed** (see [fonts](#fonts)). The bundled Liberation fonts
   cover the common case: Helvetica, Arial, Times, Times New Roman, Courier, Courier New and
   unknown fonts by their class (sans-serif, serif, monospaced), in Latin, Greek, Cyrillic and Hebrew.
   They do not cover Symbol and ZapfDingbats (Liberation has no equivalent), CJK, Indic and
   other scripts. Text that is still not covered is not drawn, the page says so, and search
   still finds it, because text extraction does not need the font file. In Chrome and Edge the
   page can then ask to read the device's fonts (Local Font Access); the substitutes then come
   from there, through the same `SystemFontCatalog` matching as on Windows, instead of the
   bundled ones. Other browsers have no such API. (The device-font path was verified in
   headless Chrome with the API stubbed to hand over one installed face, because a headless or
   embedded browser does not grant the permission; the substitute text then draws.) Liberation
   has the metrics of Arial, Times New Roman and Courier New, not their design: shapes differ
   slightly from the Windows viewer, and Liberation Mono is darker than Courier New.
2. **CMYK and YCCK JPEG images** are not decoded by the Skia backend (see its README) and stay
   blank. The page says so.
3. **Cryptography.** See below. Certificate-encrypted (public-key) PDFs cannot be opened.
4. **Size.** The site is about 15 MB uncompressed, about 6.6 MB over the wire with gzip, cached
   by the browser after the first visit. Largest pieces: `dotnet.native.wasm` with Skia 5.8 MB,
   the engine `PdfEngine.Vector.wasm` 3.0 MB, `System.Private.CoreLib` 1.7 MB. The bundled fonts
   add 4.4 MB to the published site (12 faces of 280 to 420 KB), but none of them is part of
   the first load: a face is downloaded only when a page draws text in it.
5. **Speed.** The engine runs in the .NET interpreter, so interpreting a page into a display
   list is slower than on the desktop; Skia's drawing is native code. See the measurements
   below. Trimming is on (the Blazor default for publish) and works. AOT is not enabled: an AOT
   publish could not be completed on the development machine (the AOT step failed on Windows
   path-length and intermediate-file errors under the deep worktree path), so its size and speed
   are unmeasured.
6. **Single thread.** A long page blocks the tab while it is interpreted. Pages are drawn one at
   a time, and the UI yields between them.
7. **Not in the demo:** annotations, forms, printing, text selection and copy, thumbnails, tiled
   rendering beyond 4096 × 4096 pixels.

### Fonts

The browser has no installed fonts, so the app ships its own substitutes for fonts a PDF does
not embed:

| Bundled | Stands in for |
|---|---|
| Liberation Sans | Helvetica, Arial (and Arial Narrow, Segoe UI, Calibri, Verdana, Tahoma and other sans-serif families), unknown sans-serif fonts |
| Liberation Serif | Times, Times New Roman (and Georgia, Cambria, Palatino, Garamond and other serif families), unknown serif fonts |
| Liberation Mono | Courier, Courier New (and Consolas, Lucida Console), unknown monospaced fonts |

1. **Source.** Liberation fonts 2.1.5, `liberation-fonts-ttf-2.1.5.tar.gz` from the upstream
   release (https://github.com/liberationfonts/liberation-fonts/releases/tag/2.1.5), SIL Open
   Font License 1.1. The licence ships next to the fonts as `wwwroot/fonts/OFL.txt`, and the
   notice is in [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md), with the SHA-256 of each file.
2. **Matching.** The substitution is the engine's own (`SystemFontCatalog.Match`, then the
   Skia backend's class family), as on Windows and Android. The Liberation families stand in
   for the Windows families by `SystemFontCatalog.LiberationFamilyAliases`, as Android's own
   fonts do by `AndroidFamilyAliases`. Bold, italic and bold italic come from the name
   (`Helvetica-BoldOblique`, `Arial,Bold`, `TimesNewRomanPS-ItalicMT`) and the descriptor
   flags; each glyph is still placed by the document's `/Widths`.
3. **Loading on demand.** `OnDemandFontSource` (in `PdfViewer.Web.Engine`) describes the 12
   faces up front. When a page asks for a face that is not loaded, the text waits for it,
   `ViewerDocument` fetches it (`fonts/<file>.ttf`, same origin) and draws the page again. Only
   the face a run is drawn with is fetched, not the fallbacks after it. Fetched faces are kept
   for the session (and in the browser's HTTP cache). A PDF that embeds all its fonts fetches
   nothing.
4. **Not covered.** Symbol and ZapfDingbats have no Liberation equivalent and are not mapped:
   they keep the existing substitute chain, which asks for Symbol (or Segoe UI Symbol for the
   Dingbats) and then for faces that have the characters, Arial among them. So Symbol's Greek
   letters and common math signs draw with Liberation Sans; dingbats and other characters it
   lacks are not drawn, and the page says so. The same holds for CJK, Arabic, Indic and other
   scripts that Liberation does not cover.

Verified in the browser (Chromium, the published site under `/pdf-viewer/demo/`, served by
`eng/webdemo/serve.mjs --gzip`): the first load and the engine tour fetch no font; a generated
page in non-embedded Helvetica, Helvetica-Bold, Times-Roman, Times-Italic, Courier,
Courier-BoldOblique and ZapfDingbats fetched exactly those six Liberation faces (2.2 MB; about
1.2 MB if the server compresses fonts) and drew the text; the banner appeared for the
ZapfDingbats line only, and not at all for the same page without it. A second document in the
same session fetched nothing more.

### Cryptography

Measured in the browser (Chromium, .NET 9.0.21 WebAssembly), shown in the app under `?`:

| Primitive | Browser .NET 9 | Used for | In this demo |
|---|---|---|---|
| SHA-1, SHA-256/384/512 | Available | AES-256 keys (revisions 5 and 6), font and profile caches | Platform implementation |
| MD5 | Missing (`CryptographicException`) | RC4 and AES-128 keys (revisions 2 to 4), object keys | Managed fallback in the engine |
| AES | Missing (`CryptographicException`) | AES-128 and AES-256 decryption | Managed fallback in the engine |
| RSA, X.509 store | Missing | Public-key security handler, signatures | Not supported: the viewer says so |

The managed MD5 and AES exist only so that encrypted documents can be read. They are checked
against the platform implementations on random input and against the RFC 1321 and FIPS 197
vectors on every test run. They are not constant-time and are used for nothing else. Verified
in the browser: the AES-128 test fixture (`tests/PdfViewer.Tests/Fixtures/encrypted.pdf`) asks
for its password, refuses a wrong one, opens with the right one, renders and is searchable.

### Measurements

Measured on the development machine (Windows 11; Chromium, both the desktop app's browser pane
and headless Chrome; loopback static server with gzip), Release build, interpreter (no AOT):

| What | Result |
|---|---|
| First load, empty cache | 72 requests, 6.6 MB transferred (15.2 MB decoded), viewer ready in 0.8 to 0.9 s on loopback |
| Six government reports from the local test corpus (24 to 59 pages, 0.2 to 1.4 MB) | open in 11 to 82 ms; pages at 968 × 1253 px in 13 to 430 ms, interpret 10 to 50 ms, draw 3 to 400 ms (image-heavy pages draw longest) |
| Open the engine tour (4 pages, generated in the tab) | 26 ms |
| Tour page 1, first render at 992 × 1284 px (includes runtime warm-up) | 457 ms: interpret 366 ms, draw 91 ms |
| Tour pages 2 to 4 at 697 × 902 px | 159 to 216 ms each: interpret 93 to 129 ms, draw 66 to 83 ms |
| Same page again (display list cached, new zoom) | 10 to 72 ms (draw only) |
| Search "shading", 4 pages, cold text | 98 ms |
| AES-128 fixture: open, first render | 16 ms, 104 ms (interpret 60 ms, draw 44 ms) |

Real documents vary widely: a page with many images, shadings or a large content stream takes
longer, and the first page of a session pays the interpreter's warm-up.

## Tests

- `tests/PdfEngine.Vector.Tests`: `PlatformGuardTests` (prefetcher and font catalog under a
  simulated browser), `PortableCryptoTests` (managed MD5 and AES against vectors and the
  platform; RC4, AES-128 and AES-256 documents opened with the fallback forced).
- `tests/PdfEngine.Vector.Skia.Tests`: `WebViewerEngineTests` (the tour document, rendering,
  night mode, the bitmap cap, search highlights on rotated pages, open failures, a browser-like
  font source with and without lent fonts, the page cache); `WebFontTests` (the 12 bundled
  files are the faces the catalog describes, the standard 14 and common names map to the right
  Liberation face, Symbol families stay unmapped, faces are fetched on demand with a fake fetch:
  nothing for embedded fonts, only the face a run is drawn with, once per session, a failed
  fetch reported and not retried; a page in non-embedded Helvetica, Times and Courier drawn with
  the files from `wwwroot/fonts`, each line where the Windows fonts put it).
- CI: `web-demo.yml` publishes the app on every pull request.

## Next steps

Toward an embeddable SDK in the style of the Adobe PDF Embed API:

1. **A JavaScript API over `PdfViewer.Web.Engine`**: `PdfViewerSdk.open(element, { url | bytes })`
   returning a viewer object with `goToPage`, `search`, `setZoom`, events for page change and
   search results. The engine layer is already free of UI; the work is a thin `[JSExport]`
   surface and a packaged build of the Blazor runtime as a custom element.
2. **Rendering off the main thread**: run the engine in a Web Worker (or multi-threaded
   WebAssembly once it is stable) so a heavy page never blocks scrolling.
3. **Fonts** beyond Liberation: a symbol and dingbats face and Noto for other scripts, on
   demand as the Liberation faces are.
4. **AOT** for the engine assembly only, to cut interpretation time, if the size cost is
   acceptable.
5. **Tiles** for deep zoom, text selection and copy, thumbnails, annotations display.
