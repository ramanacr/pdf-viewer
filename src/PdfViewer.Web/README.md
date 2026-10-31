# PdfViewer.Web: the engine in a browser

A PDF viewer that runs **the project's own C# engine, `PdfEngine.Vector`, inside the browser
as .NET WebAssembly**, and draws pages with its portable Skia backend
(`PdfEngine.Vector.Skia`). It is a standalone Blazor WebAssembly app: static files only, with no
server code.

- **Nothing is uploaded.** A PDF you open is read into the tab's memory and forgotten when you
  close it. The site has no back end to send it to.
- **No telemetry.** No analytics, no cookies, no third-party requests. The page sets a
  Content-Security-Policy of `'self'` only. The bundled fonts, Noto included, come from the
  demo's own site, never from Google Fonts or another host. The theme and side-panel choices are kept in the
  browser's local storage, and the custom stamps you choose to keep in its IndexedDB (on this
  device only); nothing else is stored.
- **Document JavaScript never runs.** The engine has no script interpreter.
- Diagnostics shown on screen are numbers and exception type names, never document text.

**Live demo:** https://ramanacr.github.io/pdf-viewer/demo/ (deployed from `main` by
`.github/workflows/web-demo.yml`).

Status: a demo, and the viewer of the [web embed SDK](#embed-sdk) (`wwwroot/pdfviewer-embed.js`).

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
| Thumbnails | The "Pages" tab of the side panel. Only thumbnails near its view are drawn, after the pages in view. |
| Text selection and copy | Drag with the mouse or a pen, double-click for a word, `Ctrl`+`A` for the page, `Ctrl`+`C` to copy (when the document allows it). One page at a time, with the engine's text layout (`ViewerTextLayout`, shared with the Android app). |
| Annotations | Highlight, underline, strike-through, note, free text, callouts, ink, rectangle, ellipse, line, arrow, polygon, connected lines and stamps; select, move, delete, undo and redo; a comments panel with replies and review statuses. Shapes have a fill, a dash (solid, dashed, dotted) and, for lines, the ten line endings. A callout (the **Callout** tool: click the spot it points to, then click or drag its text box, then type) is free text with `/IT /FreeTextCallout`, a line with a knee and an open arrow (`/CL`, `/LE`) and `/RD` for its text box, as Acrobat writes one. Dragging its box keeps the spot it points to; the selected callout has a handle at that spot to drag it, each one undo step. The edits, their rules and undo live in `ViewerEditSession`, the editing core the Android app shares. Written by the engine's `PdfAnnotationEditor` as an incremental update with appearance streams (`ViewerDocument.ApplyEditsAsync`). |
| Custom stamps | The stamp list's **Custom stamp…**: a PNG or JPEG picture, or a page of a PDF (chosen with a small preview), read in the browser and never uploaded; then click where it goes (2 inches across) or drag its box (it keeps its shape). Written as a `/Stamp` named `Custom` whose appearance draws the picture or page as a form XObject (`ViewerCustomStamp` in the shared editing core, the engine's `PdfStampForms`, as the desktop app writes one): a page stays vector content, a JPEG is embedded as it is. Untrusted input has caps (10 MB and 12 million pixels for a picture, decoded at most 4 million pixels; 20 MB for a PDF, 12 MB for the stamp made), a damaged picture or an encrypted PDF is refused, a JPEG loses its metadata, a PDF page keeps only its content and resources, and nothing copied carries an action, a script or an external link. A stamp placed many times is one copy in the file. **Keep it in this browser's stamp library** saves it in IndexedDB (a small record per stamp, listed without reading the bytes; at most 30 stamps and 64 MB, checked in the write's own transaction; every call guarded, so a private window simply has no library); saved stamps are in the stamp list, and the dialog deletes them. An embedded viewer keeps none (its origin is shared by every host page). The embed SDK has `addCustomStamp`. |
| Forms | Text, check box, radio, combo and list fields filled over the page; values, formats and calculations by the engine (`PdfFormService.Fill`), without running document JavaScript. |
| Download | The document with its annotations and form data as a Blob download (`Ctrl`+`S`); embedded, the bytes as given unless `downloadWithAnnotations`. |
| Print | Pages rendered at 150 dpi and printed as images through a hidden iframe. Up to 500 pages. |
| Encrypted PDFs | Password prompt. RC4 and AES (128 and 256) open, see [crypto](#cryptography). |
| Night pages | The engine's night mode: the page inverted after drawing (`I`). |
| Theme | System, light or dark (`D`), independent of night pages. |
| Bundled fonts | Text in fonts a PDF does not embed is drawn with the Liberation fonts 2.1.5 (Sans, Serif, Mono; regular, bold, italic, bold italic; SIL Open Font License 1.1), shipped in `wwwroot/fonts` with their licence (`OFL.txt`). Characters Liberation lacks come from Noto fonts (Arabic, Hebrew, nine Indic scripts, Math, Symbols 2, and Chinese, Japanese and Korean in Unicode slices; SIL Open Font License 1.1), added to `fonts/noto` when the site is published. Each file is fetched from the demo's own site the first time a page needs it and kept for the session. See [fonts](#fonts). |
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

### Rendering in a worker (opt-in)

`index.html?renderInWorker=1` draws the pages in view in a dedicated Web Worker, so that the
page's thread stays free while a page is interpreted and drawn. Multithreaded WebAssembly needs
`SharedArrayBuffer`, and so cross-origin isolation, which needs COOP/COEP headers that GitHub
Pages cannot send. The worker (`wwwroot/js/render-worker.js`) starts a second, render-only .NET runtime from
the same `_framework` files instead (`dotnet.withConfigSrc('blazor.boot.json').create()`, no
`Main`), and calls `Interop/RenderWorkerExports.cs` through `[JSExport]`. The two runtimes share
nothing: the PDF bytes go to the worker by `postMessage` (a transferred `ArrayBuffer`), and each
page comes back as a transferred `ImageBitmap` that `viewer.js` draws into the page's canvas.

- The worker holds its own copy of the document and opens it again when the bytes change (after
  an edit). Text, search, selection, annotations, thumbnails and print stay on the page's thread.
- It serves complete, unencrypted files drawn with the bundled fonts. Otherwise, and when it does
  not start within 20 s or fails, the pages are drawn on the page's thread, as without the flag.
- `dotnet.js` 9.0 decides when it is evaluated whether a worker is one of the multithreaded
  runtime's own pthread workers: a worker that has a message handler at that moment and no
  `globalThis.dotnetSidecar` flag is taken for one, and its start then waits forever (the loader
  never resolves `coreAssetsInMemory`, after "onRuntimeInitialized"). The worker sets the flag
  before it imports `dotnet.js`.

Measured with headless Chrome on the development machine (loopback server, Release build, three
runs of each mode, interleaved): open the engine tour, then zoom in three times and scroll to the
end and back. Total blocking time is the sum of each long task's time over 50 ms.

| | Main thread | Render worker |
|---|---|---|
| Open to first page: total blocking time (longest task) | 350 to 620 ms (333 to 596 ms) | 67 to 90 ms (101 to 118 ms) |
| Open to first page: time | 641 to 964 ms | 684 to 1172 ms |
| Zoom and scroll: total blocking time (longest task) | 110 to 188 ms (160 to 238 ms) | 0 ms (no task over 50 ms) |
| Longest gap of a 10 ms timer while zooming and scrolling | 166 to 244 ms | 16 to 22 ms |
| Pages drawn while zooming and scrolling | 4 | 6 (drawing does not wait for the view to rest) |

The two long tasks left with the worker come during the open, which still runs on the page's
thread (its own parse of the file, and Blazor's layout of the pages; not broken down further).
The time to the first page is about the same: the worker's runtime is cold at its first page, as
the page's own runtime is. On a busier machine the worker's first page came later (2.1 s against
1.5 s), as the two runtimes then compete for the processor.

The worker's runtime starts in about 0.3 s on loopback, beside the page's own start. It requests
the runtime's files a second time (from the browser's HTTP cache when the server allows caching)
and holds a second copy of the runtime and of the document in memory.

Checked with the flag on, in headless Chrome and in headless Firefox (WebDriver BiDi), both on
Windows:
- the engine tour: pages drawn by the worker;
- night pages: drawn by the worker, inverted (79 % of page 1 dark against under 1 % by day);
- a generated 46 MB file of 320 pages (`docs/sdk/examples/large-pdf.js`): opened, with page 1
  and page 200 drawn by the worker;
- the fallback: with the worker stopped mid-session, the next zoom draws on the page's thread
  (Chrome);
- an edit (Firefox, real pointer input): after a rectangle is drawn on page 1, the worker opens
  the edited bytes and draws page 1 again with the rectangle, also after a zoom.

Not checked yet: Safari and a phone.

### Engine changes for the browser

All guarded so that Windows behaves exactly as before:

| Where | What the browser needs | Change |
|---|---|---|
| `Text/TextPrefetcher` | `new Thread(...).Start()` throws in single-threaded WebAssembly. | No background worker when `PdfPlatform.CanStartThreads` is false. Page text is built on demand. |
| `Fonts/SystemFontCatalog.Installed`, `Text/TextFontInfo` | There is no font folder to scan. | Empty catalog and index when `PdfPlatform.HasInstalledFonts` is false. Hosts pass their own catalog. |
| `Security/PortableCrypto` | Browser .NET 9 has no MD5 and no AES (they throw `CryptographicException`). | The Standard security handler uses the platform's MD5 and AES where they work and managed ones (RFC 1321, FIPS 197) where they do not. |
| `Color/DefaultColorProfiles` | No Windows or Adobe profiles. | No change needed: the core reads no profile itself. It asks the registered `IColorProfileProvider`, and the default has none, so DeviceCMYK uses the built-in SWOP model. Only the Windows desktop app registers the Windows profiles. |
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

The site works without the Noto fonts (text that only they cover is then reported as missing).
To add them as CI does (Python 3.11 or later; about 340 MB of archives are downloaded once into
the cache folder):

```powershell
py -3 -m venv .venv-noto
py -3 -I eng/webdemo/noto_fonts.py requirements > noto-requirements.txt
.venv-noto\Scripts\python -m pip install --require-hashes --no-deps -r noto-requirements.txt
.venv-noto\Scripts\python -I eng/webdemo/noto_fonts.py build --cache ../noto-cache --out publish/web/wwwroot/fonts/noto
```

For `dotnet run`, write them to `src/PdfViewer.Web/wwwroot/fonts/noto` instead (ignored by git).

`eng/webdemo/serve.mjs` is a dependency-free static server (Node 18+) that listens on
127.0.0.1 only. `--base /pdf-viewer/` serves under a project path (rewrite `<base href>` in
`index.html` to match), and `--gzip` compresses as GitHub Pages does, for realistic sizes. It
answers range requests (206), and `--rate <KB/s>` and `--latency <ms>` simulate a slow network
(`--log-ranges` prints the PDF ranges served).

If you have more than one SDK installed, build with a .NET 9 SDK: SDK 10 would need the
separate `wasm-tools-net9` workload for this net9.0 project. On Windows, a very deep checkout
path can make the native link exceed the 260-character path limit; a short path avoids it.

## Publishing

`.github/workflows/web-demo.yml` publishes the app on every pull request and push to `main`, then adds the Noto fonts with `eng/webdemo/noto_fonts.py` (archives cached by the manifest's hash). From `main` it deploys one Pages site: the documentation in `docs/` (home page, privacy policy) built with Jekyll at the site root, and this app under `/pdf-viewer/demo/`. The deploy runs when Pages uses GitHub Actions as its source and the repository variable `WEB_DEMO_PAGES` is `true`.

## Limits

1. **Fonts that a PDF does not embed** (see [fonts](#fonts)). The bundled Liberation fonts
   cover the common case: Helvetica, Arial, Times, Times New Roman, Courier, Courier New and
   unknown fonts by their class (sans-serif, serif, monospaced), in Latin, Greek, Cyrillic and Hebrew.
   The Noto fonts add Arabic, Hebrew, Devanagari, Bengali, Tamil, Telugu, Kannada, Malayalam,
   Gujarati, Gurmukhi, Oriya, math, symbols and dingbats, and CJK. Arabic and Indic words are
   shaped with the face's own tables (see [fonts](#fonts)), but the result is the Noto design
   fitted to the PDF's widths, not the author's font: a word can be narrower or wider than in
   the original, and the shaper does not do everything HarfBuzz does (no Hebrew marks by GPOS,
   no Arabic `mset` or `stch`, no Myanmar, Khmer or Sinhala). Only the
   regular weights are bundled; bold and italic are simulated. Text that is still not covered
   is not drawn, the page says so, and search
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
   add 4.4 MB (Liberation: 12 faces of 280 to 420 KB) and about 21 MB (Noto: 56 files and a
   140 KB index) to the published site, but none of them is part of the first load: a file is
   downloaded only when a page draws text from it.
5. **Speed.** The engine runs in the .NET interpreter, so interpreting a page into a display
   list is slower than on the desktop; Skia's drawing is native code. See the measurements
   below. Trimming is on (the Blazor default for publish) and works. AOT is not enabled: an AOT
   publish could not be completed on the development machine (the AOT step failed on Windows
   path-length and intermediate-file errors under the deep worktree path), so its size and speed
   are unmeasured.
6. **Single thread.** A long page blocks the tab while it is interpreted. Pages are drawn one at
   a time, and the UI yields between them.
7. **Not in the demo:** annotations, forms, tiled rendering beyond 4096 × 4096 pixels. Print
   sends 150 dpi page images, not the PDF, and every sheet has the first page's size.

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
4. **Symbol and ZapfDingbats** have no Liberation equivalent and are not mapped: they keep the
   existing substitute chain, which asks for Symbol (or Segoe UI Symbol for the Dingbats) and
   then for faces that have the characters, Arial among them. So Symbol's Greek letters and
   common math signs draw with Liberation Sans, and dingbats come from Noto Sans Symbols 2.
5. **Noto, character by character.** When no face of the chain has every character of a run,
   the face with the most of them draws what it has, and each other character comes from the
   Noto file that has it (`BundledFontIndex`): Arabic, Hebrew and the Indic faces first, then
   Math and Symbols 2, then CJK. Chinese, Japanese and Korean faces share most characters, so
   the language picks the face: the font's CID ordering (Japan1, GB1, CNS1, Korea1), else kana
   or hangul in the text, else the font's name (MS Mincho, SimSun, MingLiU, Batang and the
   like), else Simplified Chinese.
   Arabic and Indic words in a font that is not embedded are then shaped, with the engine's own
   shaper (the one that shapes new text when editing; no HarfBuzz) and the GSUB and GPOS tables
   of the face that draws them: Arabic joining forms, lam-alef and other ligatures, marks on
   their anchors, Indic pre-base vowel signs, reph and conjuncts. A PDF lays its glyphs out left
   to right, so Arabic is taken as seen and put back in reading order before it is shaped
   (a word whose glyphs go right to left is in reading order already). Each shaped word is
   fitted to the space the PDF's `/Widths` give it, as single glyphs are. Embedded fonts are
   drawn by their own glyphs, unshaped, as before. The desktop app's renderers (Direct2D and
   WPF) shape with the same code, so a page looks the same in both.
6. **Lazy loading.** The Noto index (`fonts/noto/index.json`, 140 KB, about 32 KB with gzip)
   is fetched the first time a character needs it, then only the files that hold the page's
   characters. Each CJK face is cut into slices: a core slice with the common characters of
   the language's national character set (GB 2312 level 1, Big5 level 1, JIS X 0208 level 1,
   KS X 1001 hangul), kana and CJK punctuation, then the face's other characters in code point
   order, 2000 per slice. Typical text needs only the core slice (0.3 to 1.3 MB).
7. **Privacy.** Every font is fetched from the demo's own site (`fonts/…`, same origin, allowed
   by the page's `connect-src 'self'`). There are no requests to fonts.googleapis.com,
   fonts.gstatic.com or any other host.
8. **Where the Noto files come from.** They are not in the repository.
   `eng/webdemo/noto-fonts.json` pins each official release archive (github.com/notofonts) by
   URL and SHA-256, and the SHA-256 of the file taken from it, and pins fontTools for the
   slicing. `eng/webdemo/noto_fonts.py` downloads, checks (it stops on a mismatch), slices and
   writes `fonts/noto` with the licence of each archive (`OFL-<id>.txt`). The web demo workflow
   runs it after `dotnet publish`; see [run locally](#run-locally) to run it yourself.

| Noto file | Characters | Size |
|---|---:|---:|
| NotoSansArabic-Regular.ttf | 1250 | 138 KB |
| NotoSansHebrew-Regular.ttf | 147 | 16 KB |
| NotoSansDevanagari-Regular.ttf | 281 | 179 KB |
| NotoSansBengali-Regular.ttf | 134 | 101 KB |
| NotoSansTamil-Regular.ttf | 147 | 45 KB |
| NotoSansTelugu-Regular.ttf | 165 | 161 KB |
| NotoSansKannada-Regular.ttf | 166 | 132 KB |
| NotoSansMalayalam-Regular.ttf | 187 | 75 KB |
| NotoSansGujarati-Regular.ttf | 114 | 139 KB |
| NotoSansGurmukhi-Regular.ttf | 154 | 35 KB |
| NotoSansOriya-Regular.ttf | 151 | 128 KB |
| NotoSansMath-Regular.ttf | 2919 | 642 KB |
| NotoSansSymbols2-Regular.ttf | 2641 | 655 KB |
| NotoSansSC-Regular-00.otf (core) / 01 to 14 | 4642 / 248 to 2000 each | 844 KB / 64 to 548 KB each |
| NotoSansTC-Regular-00.otf (core) / 01 to 08 | 6177 / 568 to 2000 each | 1334 KB / 171 to 542 KB each |
| NotoSansJP-Regular-00.otf (core) / 01 to 07 | 3690 / 1042 to 2000 each | 672 KB / 192 to 515 KB each |
| NotoSansKR-Regular-00.otf (core) / 01 to 10 | 3512 / 1662 to 2000 each | 343 KB / 205 to 484 KB each |
| index.json | | 140 KB |

The workflow's step summary lists every file.

Verified in the browser for the Liberation fonts (Chromium, the published site under `/pdf-viewer/demo/`, served by
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
  the files from `wwwroot/fonts`, each line where the Windows fonts put it); `NotoFontTests`
  (the index format, the face for a code point by script and CJK language, the Noto files
  fetched on demand with a fake fetch: none for Latin text, the index once, then only the
  files with the page's characters, a failed index reported and not retried; Arabic, Hindi
  and Chinese text that is not drawn with Liberation alone and is drawn with the fallback;
  `RealNotoFonts_DrawArabicDevanagariAndChinese` runs with the real files when
  `NOTO_FONTS_DIR` names the script's output).
- `tests/PdfEngine.Vector.Skia.Tests`: `EmbedSdkTests` (the embed protocol: origins, the frame
  URL, foreign and malformed messages, the config and its errors, the API calls and their
  arguments, the replies; document metadata; mouse selection between two carets);
  `EmbedRangeTests` (range loading: the fetchRange request and range reply checked both ways,
  the reader with a fake host page, a document drawn before the rest arrives, the fallback for a
  damaged file); `ViewerStripRenderTests` (pages drawn in strips); `WebShapeStyleTests` (shape
  fill, dash and line endings: written with appearances and read back, a file's shapes keep what
  is not changed, measurements keep their style, undo, the API's `dashPattern`).
- `tests/PdfEngine.Vector.Tests`: `RangeLoadingTests` (the chunk cache, the planner, opening and
  drawing by ranges, object streams, encryption, the fallback for a damaged cross-reference table).
- `tests/sdk/embed-range.test.mjs` (`node --test`): the SDK script's range sources (URL and
  `linearizationInfo`) and its checks of the viewer's range requests.
- CI: `web-demo.yml` publishes the app on every pull request and checks that the SDK scripts parse.

## Embed SDK

`wwwroot/pdfviewer-embed.js` (and `pdfviewer-embed.mjs`, the same API as an ES module) lets any web
page show a PDF with this viewer, with an API modelled on the Adobe PDF Embed API. The documentation
is [docs/sdk/index.md](../../docs/sdk/index.md) (published at
[ramanacr.github.io/pdf-viewer/sdk/](https://ramanacr.github.io/pdf-viewer/sdk/)); the examples are in
[docs/sdk/examples](../../docs/sdk/examples).

1. The script makes an iframe of `index.html?embed=1&mode=<embedMode>&origin=<host origin>`.
   `theme.js` and `Viewer.ReadFrameUrl` read the URL before the first render, so the demo's
   chrome (brand, Open, Tour, privacy bar, status bar, help, drag and drop) never shows, and
   nothing is read from or written to local storage or IndexedDB (no custom stamp library).
2. The host page fetches the file itself and posts `{ protocol: "pdfviewer-embed", version: 1,
   type: "open", config, data }` with the bytes as a transferred ArrayBuffer. `viewer.js` accepts
   messages only from the parent window and the origin in the URL, and posts only to it;
   `EmbedProtocol` (in `PdfViewer.Web.Engine/Embed`, tested in `EmbedSdkTests`) validates every
   message and builds every reply.
3. `Viewer.Embed.cs` applies the config, answers the API calls (`getPDFMetadata`, `gotoLocation`,
   zoom, `search`, and the others) and sends the events. Selection, thumbnails, download and print
   are in `Viewer.Selection.cs`, `Viewer.Thumbnails.cs` and `Viewer.Output.cs`; the demo has them too.
4. In line (`IN_LINE`), the frame is as tall as the document: `viewer.js` reports the content
   height and the host sizes the frame; pages in view are found against the host page's viewport.
5. Large files: the host page reads a `location` (or Adobe-style `linearizationInfo`) by ranges
   and sends the file's end with the open message; the viewer opens it with
   `ViewerDocument.OpenRangedAsync` over an `EmbedRangeReader`, whose reads are validated
   `fetchRange` messages that the host page answers from the URL it was given. The rest of the
   file follows in the background after the first page (see docs/sdk, "Large files").

To try it locally, put the published `wwwroot` under `demo/` and `docs/sdk/examples` under
`sdk/examples/` of one folder, set the `<base href>` in `demo/index.html` to `/pdf-viewer/demo/`,
and serve the folder with `node eng/webdemo/serve.mjs <folder> --base /pdf-viewer/`.

## Next steps

1. **Rendering off the main thread by default**: the render worker ("Rendering in a worker",
   above) is opt-in for now. Still to do before it is the default: files read by ranges and
   encrypted files, device fonts, thumbnails and print in the worker, and one copy of the
   document instead of two.
2. **Fonts**: bold Noto weights, and Noto Serif for serif text.
3. **AOT** for the engine assembly only, to cut interpretation time, if the size cost is
   acceptable.
4. **Tiles** for deep zoom, selection across pages, annotations display.
