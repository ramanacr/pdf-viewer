---
title: Web embed SDK
permalink: /sdk/
---

# Web embed SDK

Show PDF files in your web page with PDF Viewer Native's own engine. The engine runs in the
reader's browser as WebAssembly. There is no client ID, no account, no server and no telemetry:
the file goes from your page to the viewer in the same browser tab, and nowhere else.

The JavaScript API follows the
[Adobe PDF Embed API](https://developer.adobe.com/document-services/docs/overview/pdf-embed-api/howtos/),
so code written for it needs few changes. Status: **phase 3, preview** (viewing, annotations,
forms, saving, and large files by range requests). The API can change before version 1.0.

**[Examples](examples/)**: full window, sized container, in line, light box, events and APIs,
file picker, annotations and saving, forms, large files, React and Angular.

## Compared with the Adobe PDF Embed API

{% include adobe-compare.md %}

## Quick start

```html
<div id="viewer" style="height: 600px"></div>
<script src="https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.js"
        integrity="sha384-REPLACE_ME" crossorigin="anonymous"></script>
<script>
  const view = new PdfViewer.View({ divId: 'viewer' });
  view.previewFile(
    { content: { location: { url: '/files/report.pdf' } }, metaData: { fileName: 'report.pdf' } },
    { embedMode: 'SIZED_CONTAINER' });
</script>
```

As an ES module:

```js
import { View } from 'https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.mjs';
const view = new View({ divId: 'viewer' });
```

Your page must be served over `http` or `https` (not opened as a `file:`).

From npm, for a bundler:

```bash
npm install pdfviewer-embed
```

```js
import { View } from 'pdfviewer-embed';
const view = new View({ divId: 'viewer' });
```

The package holds the SDK script only. The viewer comes from this site by default; pass `baseUrl`
to use your own copy (see [Production](#production)).

Replace `sha384-REPLACE_ME` with the current value: copy it from
<https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.integrity.json> (each deploy writes it).
With `integrity`, the browser runs the script only when its bytes match the hash. The value changes
when the script changes, and then the old value stops the script from loading: for a page that must
keep working, see [Production](#production). For the ES module, `import` takes no `integrity`
attribute; an [import map](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/script/type/importmap)
with an `integrity` entry does, in the browsers that support it.

### Host the viewer yourself

The SDK script lives next to the viewer: `pdfviewer-embed.js` is in the viewer's folder (`/demo/`
on this site). To host everything yourself, publish `src/PdfViewer.Web`
(see its [README](https://github.com/ramanacr/pdf-viewer/tree/main/src/PdfViewer.Web#run-locally)),
serve the `wwwroot` folder as static files, set its `<base href>` to the folder's path, and load
`pdfviewer-embed.js` from there. `baseUrl` is only needed when the script is not in the viewer's
folder:

```js
new PdfViewer.View({ divId: 'viewer', baseUrl: 'https://cdn.example.com/pdf-viewer/' });
```

### Production

The files on this site change on each deploy, without notice. `integrity` on the script tag pins
only the SDK script: the viewer that it loads in the frame (`index.html`, the app and the engine)
is not pinned by it. For production:

1. Host a copy of the viewer and the SDK yourself, from a release or a commit that you chose
   (see [Host the viewer yourself](#host-the-viewer-yourself)). Update it when you decide to.
2. Serve the viewer from another origin than your pages, for example `https://pdf.example.com`
   for pages on `https://www.example.com` (see [Same-origin viewer](#same-origin-viewer)), and
   set `isolation: 'require'`.
3. Send a `frame-ancestors` header for the viewer (see [Who can frame the viewer](#who-can-frame-the-viewer)).
4. Load the SDK script with `integrity`, the hash of your copy.

## `new PdfViewer.View(options)`

| Option | Meaning |
| --- | --- |
| `divId` | The id of the element that holds the viewer. Required. |
| `baseUrl` | Where the viewer files are. Default: the folder of `pdfviewer-embed.js`. |
| `locale` | Accepted. The viewer is in English only for now. |
| `isolation` | `'warn'` (default) or `'require'`: what to do when the viewer has the same origin as your page. `'warn'` writes one console warning; `'require'` throws `INVALID_ARGUMENT`. See [Same-origin viewer](#same-origin-viewer). |
| `renderInWorker` | `true` to draw pages in a Web Worker, so the reader's main thread stays free; `false` (default) draws them on the main thread. Preview: see [Rendering in a worker](#rendering-in-a-worker). |

`view.destroy()` removes the viewer from the page (not in the Adobe API).

## `view.previewFile(file, config)`

Returns a Promise. It resolves with a viewer object when the document is open, and rejects with
a `PdfViewer.PdfViewerError` when it cannot be shown. Call it again to show another file in the
same View.

### File

```js
{
  content: { location: { url, headers: [{ key, value }], credentials, rangeRequests } }   // or
           { promise, linearizationInfo }                    // promise resolves to an ArrayBuffer, typed array or Blob
           { arrayBuffer },                                  // an ArrayBuffer, typed array or Blob
  metaData: { fileName, id, hasReadOnlyAccess },
}
```

- **`location`.** Your page fetches the URL, with `fetch`, so your page's own rules apply: the
  headers you give, cookies for your own site, and the server's CORS rules for another site.
  Add `credentials: 'include'` to `location` to send cookies to another site. The viewer itself
  never fetches the file: its Content-Security-Policy allows only its own files. When the server
  answers range requests, the file is read by ranges (see [large files](#large-files));
  `rangeRequests: false` reads it whole.
- **`linearizationInfo`** (with `promise`): read the file by ranges through your own functions,
  as in Adobe's API. See [large files](#large-files).
- **`promise` and `arrayBuffer`.** The SDK copies the bytes and transfers the copy to the
  viewer, so your buffer stays usable.
- **`metaData.fileName`** is shown in the viewer and used for Download and Save. **`id`** comes
  back in `getPDFMetadata()` and as `target.source` of annotations. **`hasReadOnlyAccess: true`**
  removes the annotation tools and form filling; the annotation manager can read, but its
  changes reject with `NOT_ALLOWED`.
- Largest file: 512 MB. For `location`, the SDK counts the bytes as they arrive and stops the
  download (`TOO_LARGE`) as soon as there are more; a `Content-Length` that is already too large
  fails at once. A Blob is checked by its size before it is read.

### Config

| Key | Values | Default | Notes |
| --- | --- | --- | --- |
| `embedMode` | `FULL_WINDOW`, `SIZED_CONTAINER`, `IN_LINE`, `LIGHT_BOX` | `FULL_WINDOW` | See [embed modes](#embed-modes). |
| `defaultViewMode` | `FIT_WIDTH`, `FIT_PAGE`, `TWO_COLUMN`, `TWO_COLUMN_FIT_PAGE` | `FIT_WIDTH` | In line, fit page is fit width. |
| `showDownloadPDF` | `true`, `false` | `true` | Download saves the bytes you gave, or the annotated document with `downloadWithAnnotations`. |
| `showPrintPDF` | `true`, `false` | `true` | See [printing](#printing). Disabled when the document forbids printing. |
| `showZoomControl` | `true`, `false` | `true` | |
| `showBookmarks` | `true`, `false` | `true` | The outline tab of the side panel (full window, light box). |
| `showThumbnails` | `true`, `false` | `true` | The pages tab of the side panel (full window, light box). |
| `showFullScreen` | `true`, `false` | `true` | Sized container only. |
| `showAnnotationTools` | `true`, `false` | `true` | The annotation toolbar, in full window and light box. See [annotations](#annotations). |
| `enableAnnotationAPIs` | `true`, `false` | `false` | Needed for `getAnnotationManager()` and its events. |
| `includePDFAnnotations` | `true`, `false` | `false` | The file's own annotations of the [supported types](#annotation-json) are listed and can be changed. With `false` they are drawn but not managed. |
| `enableFormFilling` | `true`, `false` | `true` in full window and light box, else `false` | See [forms](#forms). |
| `downloadWithAnnotations` | `true`, `false` | `false` | Download saves the document with the reader's annotations and form data. |
| `printWithAnnotations` | `true`, `false` | `false` | Print prints the annotations and form data. With `false`, the file as you gave it. |
| `focusOnRendering` | `true`, `false` | `true` for full window and light box | Moves the keyboard focus to the viewer when the file opens. |
| `exitPDFViewerType` | `CLOSE`, `RETURN` | `CLOSE` | The light box's button says "Close" or "Return". |
| `enableSearchAPIs` | `true`, `false` | `false` | Needed for `search()`. |
| `theme` | `system`, `light`, `dark` | `system` | The viewer's colours; pages keep theirs. |

An unknown key is ignored. A known key with a wrong value rejects the Promise with
`INVALID_ARGUMENT` and a message that names the key.

### Embed modes

| Mode | The viewer |
| --- | --- |
| `FULL_WINDOW` | Fills the element (give it the size of the page). Full toolbar with the file name, side panel with outline, page thumbnails and search results. |
| `SIZED_CONTAINER` | Fills an element that you size. Compact toolbar, no side panel, a full-screen button. |
| `IN_LINE` | Shows every page in the flow of your page. The frame grows to the document's height and your page scrolls; pages are drawn as they come into view. |
| `LIGHT_BOX` | Covers your page, with the file name and a Close (or Return) button above it. Escape closes it and gives the focus back. |

## Large files

A large file does not have to arrive whole before its first page shows. The viewer asks your
page for the byte ranges it needs, as Adobe's and pdf.js's viewers do: the end of the file (the
cross-reference table), the catalog and the root of the page tree, the tree nodes on the path to
page 1, then the objects of each page when it is drawn. The rest
of the file follows in the background, one request at a time, after the first page is on screen.

### With a URL

```js
view.previewFile({ content: { location: { url: 'https://files.example.com/big.pdf' } }, metaData: { fileName: 'big.pdf' } });
```

1. The SDK asks for one byte (`Range: bytes=0-0`). An answer `206 Partial Content` with
   `Content-Range: bytes 0-0/<length>` turns range loading on; any other answer is read as the
   whole file, as before.
2. It reads the last 1 MB and sends it to the viewer with the open message.
3. The viewer asks for ranges with `fetchRange` messages (at most 4 MB each, at most 6 waiting);
   the SDK fetches them from **the URL you gave and no other**, with your headers and
   `credentials`, and checks that each answer is `206` with exactly the bytes asked for.

Files of 1 MB or less are read whole. `rangeRequests: false` turns range loading off.

**Server requirements.** One-range requests (`Range: bytes=a-b`) answered with `206` and
`Content-Range`, as GitHub Pages, S3, Azure Blob Storage, nginx and Apache do for static files.
The file must not change while it is read (the SDK compares the length in every `Content-Range`).
For a file on another origin, CORS must allow your page and **expose `Content-Range`**:

```
Access-Control-Allow-Origin: https://www.example.com
Access-Control-Expose-Headers: Content-Range, Accept-Ranges, Content-Length
```

`Range: bytes=a-b` is a CORS-safelisted header, so no preflight request is needed. Without the
exposed `Content-Range` header the SDK cannot read the length and reads the file whole.
Serve PDFs without `Content-Encoding`: the SDK checks that each answer has exactly the bytes asked for.

### With `linearizationInfo`

As in Adobe's PDF Embed API, give functions that read the file, for example from your own
storage or a service that needs signed requests:

```js
view.previewFile({
  content: {
    promise: fetchWholeFile(),          // used only when the viewer needs the whole file at once
    linearizationInfo: {
      getInfo: () => Promise.resolve({ fileSize }),
      getInitialBuffer: () => readBytes(0, 65535),          // optional: the start of the file
      getFileBufferRanges: ranges =>                         // [{ start, end }], end inclusive
        Promise.all(ranges.map(r => readBytes(r.start, r.end))).then(bufferList => ({ bufferList })),
    },
  },
  metaData: { fileName: 'big.pdf' },
});
```

- `getInfo()` resolves with `{ fileSize }` (a whole number of bytes, at most 512 MB).
- `getInitialBuffer()`, optional, resolves with the first bytes of the file (an `ArrayBuffer` or
  typed array), or `{ buffer, start }` for bytes from another offset.
- `getFileBufferRanges(ranges)` gets `[{ start, end }]` (`end` inclusive, as in an HTTP `Range`
  header) and resolves with `{ bufferList: [ArrayBuffer, …] }` or an array of buffers, one per
  range, each exactly `end - start + 1` bytes long.
- `content.promise` is read only when `linearizationInfo` fails (then the file is read whole from
  it, as without `linearizationInfo`). To avoid reading the file twice, give a promise that starts
  its download only when it is awaited (a `then`-able object), as the large-file example does.

Only the shape of these functions follows Adobe's documentation; the details above (end
inclusive, `bufferList`) are this SDK's and were not tested against Adobe's viewer.

### While the rest of the file loads

- Pages draw and their text can be selected; scrolling to a page fetches its ranges first.
- Calls that need the whole file wait for it: `getPDFMetadata()`, `search()`, the annotation
  manager, Download, Save. The annotation tools, form fields and the outline appear when the
  file is complete. A password-protected file asks for its password as usual.
- `APP_RENDERING_DONE` (see [events](#events)) tells when the first page is on screen and how
  many bytes the viewer had read by then.

### Fallbacks

- A server without range support, an unreadable `Content-Range`, or a file of 1 MB or less: the
  file is read whole, as before.
- A damaged file whose cross-reference table must be rebuilt by scanning the whole file: the
  viewer reads it whole at once, then opens it as usual.
- A page that still needs bytes after its objects were fetched (for example an object the
  cross-reference table places wrongly): the viewer fetches them and draws the page again; after
  12 rounds it reads the whole file.
- A range request that fails (network, HTTP error, timeout after 60 s): at open, `previewFile`
  rejects with `NETWORK_ERROR`; later, the page is left undrawn (the viewer's status names the
  error) and a zoom change draws it again. If the background read of the rest fails, it tries
  again after 2, 4, 8 and 16 s (when a page is drawn), from where it stopped; after five failures
  the viewer shows a **Try again** button. The host page aborts its own fetches after 60 s without
  an answer, and when the viewer is closed or another file is opened.
- An object whose bytes, by the cross-reference table, would pass a cap (one page needs more than
  256 MB, the document's structure or one round of missing bytes more than 32 MB): the cap is
  checked before the bytes are asked for, and the viewer reads the whole file instead.
- A page tree whose `/Count` values do not match their kids, that has a loop, or that is too deep
  to walk lazily (a page not found after 12 rounds): the viewer reads the whole page tree, as
  before this change, and stays on range requests. If the real page count differs from the
  root's `/Count`, the page list is corrected.
- Two files opened one after the other: only the second shows, even when the first finishes last;
  the first `previewFile` rejects with `CLOSED`.

### How it works

The engine (`PdfEngine.Vector`, shared with the Windows viewer) reads PDFs through a synchronous
byte source. `PdfRangedDocument` puts a 4 KB block cache under it (`PdfChunkCache`, least
recently used first, 64 MB by default, the current page's blocks always kept; while a document
opens, the cache passes its cap by at most the 32 MB a round may fetch). Before a page is
parsed, `PdfRangePlanner` walks its objects level by level from the page dictionary (resources,
fonts, images, annotations; not `/Parent` or links to other pages) and fetches each level's byte
ranges (an object runs from its cross-reference offset to the next object's). Then the page is
parsed and drawn synchronously from the cache. A read of a block that is not there records the
miss; the operation is repeated on a fresh parse after the missing bytes arrive. Documents
opened from memory or from a file do not use any of this.

The page tree is lazy (`PdfPageTree.IsLazy`, `PdfRangedOptions.LazyPageTree`). The page count is
the root's `/Count`, checked against the number of objects in the file before anything is sized
by it. A page is found when it is first needed: from the root, the engine goes down to the kid
whose `/Count` holds the page, one level per round of requests. A node with one kid per page
goes straight to the kid; in other nodes every kid is read (at most 64 more per round), and the
kids' pages must add up to the node's `/Count`. A loop, a kid listed twice, a tree deeper than
`PdfSecurityLimits.MaxPageTreeDepth` or a count that does not add up turns the lazy tree off (see
fallbacks). Pages not read yet are laid out at the first page's size. When a page of another
size is read (before it is drawn) or when the whole file is in, the layout is corrected
(`ViewerDocument.PagesChanged`). Below a node, the engine checks only the counts on the path to
a page. A wrong `/Count` in a node beside that path can put pages in the wrong order until the
whole file is read; the viewer then draws again the pages it drew.

### Measured

A generated file of 320 pages, each with a 224 × 224 image stored uncompressed (46.2 MB, not
linearized, objects written page by page), served by `eng/webdemo/serve.mjs` on the same machine,
headless Chrome 155, time from the host page's start to the first page on screen (it includes the
viewer's own start, about 1 s), three runs:

| File | Network | Whole file | By ranges | Read before the first page |
| --- | --- | --- | --- | --- |
| Page objects spread through the file | loopback | 2.4 to 3.1 s | 1.6 to 1.7 s | 1.2 MB (2.5 %), 6 requests |
| The same | 50 Mbit/s, 40 ms (`serve.mjs`) | 10.7 to 10.9 s | 2.4 to 2.7 s | 1.2 MB (2.5 %), 6 requests |
| The same | 50 Mbit/s, 40 ms (in the page) | | 1.5 to 1.6 s | 1.2 MB (2.5 %), 4 requests |
| The same, with object streams (`PdfOptimizer`) | loopback | 1.5 to 2.3 s | 1.5 to 2.3 s | 1.2 MB (2.5 %), 5 requests |
| The same, with object streams | 50 Mbit/s, 40 ms | 9.3 to 10.4 s | 2.9 to 3.1 s | 1.2 MB (2.5 %), 5 requests |

Before the lazy page tree, the file with spread-out page objects needed one request per page to
open (2.4 MB in 318 requests): 2.5 to 2.9 s on loopback, 21.4 to 21.6 s at 50 Mbit/s and 40 ms
through `serve.mjs` (Chrome sent the requests for the same URL almost one at a time), and 5.1
to 5.4 s with the network simulated in the page. Now the first page needs the end of the file,
page 1's dictionary and page 1's objects, whatever the number of pages. The object-stream rows
were measured before the change; such files open in a few requests either way. The spread-out
rows were measured on 2026-10-27 with warm browser caches (the viewer itself was not
downloaded again). The [large-file example](examples/large-file.html) repeats the measurement
in your browser.

## Events

The annotation manager has its own events: see [annotation events](#annotation-events).

```js
view.registerCallback(PdfViewer.View.Enum.CallbackType.EVENT_LISTENER,
  event => console.log(event.type, event.data),
  { listenOn: ['PAGE_VIEW', 'TEXT_COPY'] });   // leave listenOn out to hear every event
```

| Event | `data` | When |
| --- | --- | --- |
| `DOCUMENT_OPEN` | `fileName`, `numPages` | The document is open. |
| `PAGE_VIEW` | `pageNumber` | The current page changes (also once when the document opens). |
| `ZOOM_CHANGE` | `zoom` (1 is 100 %) | The zoom changes, by the reader, an API or a fit. |
| `TEXT_SEARCH` | `searchedText` | A search starts, from the search box or `search()`. |
| `TEXT_COPY` | `copiedText` | The reader copies selected text (`Ctrl`+`C`). |
| `DOCUMENT_DOWNLOAD` | `fileName` | The reader downloads the file. |
| `DOCUMENT_PRINT` | `fileName` | The pages are ready and the print dialog opens. |
| `PREVIEW_ERROR` | `code`, `message` | `previewFile` failed (the Promise rejects too). |
| `APP_RENDERING_DONE` | `pageNumber`, `ms`, `bytesRead`, `fileSize`, `ranged` | The first page of a file is on screen. `ms` counts from the viewer's open; `bytesRead` is what it had read of the file by then. |

Events go only to your callbacks, in your page. `enablePDFAnalytics` and similar options do not
exist: there is nothing to send events to.

## APIs

```js
const viewer = await view.previewFile(file, config);
const apis = await viewer.getAPIs();
```

Every method returns a Promise.

| Method | Result |
| --- | --- |
| `getPDFMetadata()` | `{ numPages, pdfTitle, author, subject, keywords, creator, producer, creationDate, modDate, pdfVersion, isEncrypted, isTagged, canCopy, canPrint, wasRepaired, isSigned, certificationLevel, fileSize, fileName, id }`. Dates are `"YYYY-MM-DDTHH:mm:ss"` as written in the file, or `null`. `isSigned` is `true` when the file has a signed signature field or a certification signature; `certificationLevel` is the certification signature's DocMDP level (1, 2 or 3), or `null`. See [signed and certified files](#signed-and-certified-files). |
| `getCurrentPage()` | The page number. |
| `gotoLocation(pageNumber, x, y)` | Scrolls the point (x, y) of the page to the top-left of the view. **x and y are PDF points from the top-left corner of the page as shown** (Adobe's are from the bottom-left). Both default to 0. |
| `getSelectedContent()` | `{ type: 'text', data }`: the selected text, `''` when none. Rejects with `NOT_ALLOWED` when the document forbids copying (see [Copy protection](#copy-protection)). |
| `getZoomAPIs()` | `{ zoomIn(), zoomOut(), setZoomLevel(level), getZoomLimits() }`. The first three resolve with the new zoom; the limits are `{ minZoom: 0.1, maxZoom: 8 }`. |
| `search(term)` | Needs `enableSearchAPIs: true`. Resolves with a search object: `onResultsUpdate(callback)`, `next()`, `previous()`, `clear()`. Works in documents that forbid copying too (see [Copy protection](#copy-protection)). |

The search callback receives `{ currentResult: { pageNumber, index }, totalResults, status }`.
`index` counts from 1 over the whole document; `status` is `IN_PROGRESS` while pages are read and
`COMPLETED` after. A new search (from the API or the search box) ends the previous one: its
methods then reject with `SEARCH_ENDED`.

A viewer object belongs to one document. After `previewFile` shows another file, its methods
reject with `NO_DOCUMENT`.

### Error codes

Other enums: `PdfViewer.View.Enum.CallbackType` (`EVENT_LISTENER`, `SAVE_API`),
`AnnotationEvents`, `ApiResponseCode` (`SUCCESS`, `FAIL`).

`PdfViewer.View.Enum.ErrorCode`: `INVALID_ARGUMENT`, `FILE_NOT_FOUND` (HTTP 404 or 410),
`HTTP_ERROR` (another HTTP error; `detail.status`), `NETWORK_ERROR` (the network failed or CORS
does not let your page read the file: browsers do not say which), `NOT_A_PDF`, `TOO_LARGE`,
`PASSWORD_REQUIRED` and `WRONG_PASSWORD` (the reader closed the password prompt),
`CERTIFICATE_REQUIRED`, `ENCRYPTION_NOT_SUPPORTED`, `VIEWER_LOAD_FAILED` (the viewer did not start
in 60 seconds), `NO_DOCUMENT`, `NOT_ENABLED`, `NOT_ALLOWED`, `SEARCH_ENDED`, `CLOSED` (the viewer
was removed or the light box closed), `VIEWER_ERROR`.

Password-protected files: the viewer asks the reader for the password. Your page never sees it.

## Annotations

With `showAnnotationTools` (full window and light box), a second toolbar shows these tools:

| Tool | How |
| --- | --- |
| Select | Select text (drag), or click an annotation to select it; drag it to move it; `Delete` deletes it. Click a selected note or text box to edit its text. |
| Highlight, underline, strike-through | Drag over text. With text already selected, the button marks it at once. |
| Note | Click where the note goes, then type the comment. |
| Text box (free text) | Click, or drag a box, then type. An empty new text box is not kept. |
| Pen (ink) | Draw with the mouse or a pen. Each stroke is one annotation. |
| Rectangle, ellipse | Drag the shape. |
| Line, arrow | Drag from the start to the end. The arrow has an open arrowhead at its end. |
| Polygon, connected lines | Click each point. Double-click or press `Enter` to end; `Esc` cancels. |
| Stamp | Pick a stamp (Approved, Draft, Confidential and the other standard ones) and click where it goes. |

The colour buttons set the colour of the next annotation, or change the selected one (a stamp
keeps the colour of its kind).

For a rectangle, ellipse, line, arrow, polygon or connected lines, lists next to the colour
buttons set the **shape style** of the next shape of that tool, or change the selected shape:

- **Fill** (`/IC`): none, or one of the toolbar colours. It fills a rectangle, ellipse or
  polygon, and the closed line endings. A fill from the file that is not in the list shows as
  "Current fill".
- **Line style** (`/BS` with `/S /D` and `/D`): solid, dashed (`[4 3]`) or dotted (`[1 2]`).
  Another dash from the file shows as "Current dash".
- **Line start** and **Line end** (lines and connected lines, `/LE`): the ten endings of
  ISO 32000-2 Table 179. The arrow tool starts with an open arrow at the end. The shape's
  box grows to hold new endings.

Each change is one undo step. A shape from the file keeps its own fill, dash, line endings and
`/BS` until that value is changed: no defaults are added to it. A measurement (an annotation
with `/Measure`) shows no shape style and keeps its own. A file's shape moved to another page
(with `updateAnnotation`) is written there as a new annotation with its own entries, its
`/Measure` too. While a list has the focus, it keeps its letters, arrow keys and `Delete`;
`Ctrl`+`Z` and `Ctrl`+`Y` still undo and redo. Undo and redo (`Ctrl`+`Z`, `Ctrl`+`Y`) cover every annotation
change, the host page's too. The side panel's **Comments** tab lists the annotations as
threads: each reply is under the comment it answers. Click one to go to it; with a comment
selected, **Reply** adds a reply. Deleting a comment deletes its replies, as in Acrobat.

Every change is written into the document by the engine (the same `PdfAnnotationEditor` as the
Windows viewer): real annotation dictionaries with appearance streams, as an incremental update
of the file you gave. The pages are drawn from that document, so what the reader sees is what
Save writes, and Acrobat and other readers show the same annotations.

### The annotation manager

```js
const viewer = await view.previewFile(file, { enableAnnotationAPIs: true, includePDFAnnotations: true });
const manager = await viewer.getAnnotationManager();
```

`getAnnotationManager()` rejects with `NOT_ENABLED` without `enableAnnotationAPIs: true`. Every
method returns a Promise.

| Method | Result |
| --- | --- |
| `addAnnotations(list)` | Adds 1 to 1000 annotations (all or none). Resolves with them as stored: an `id` is made when you give none, the author and dates are filled in. An `id` in use rejects. |
| `getAnnotations(filter?)` | The annotations, all or those of the filter. |
| `selectAnnotation(id)` | Selects it and scrolls to it; resolves with it. |
| `unselectAnnotation()` | |
| `updateAnnotation(annotation)` | Replaces the annotation with the same `id` (its `subtype` cannot change); resolves with it. `modified` becomes now. |
| `deleteAnnotations(filter)` | Deletes the annotations of the filter (`{}` deletes all); resolves with the deleted ones. |
| `setConfig(config)` | `{ showToolbar, showCommentsPanel, downloadWithAnnotations, printWithAnnotations, authorName }`, each optional. `authorName` is the author of the reader's new annotations (default `Guest`). |
| `exportXfdf()` | The annotations as an XFDF document (ISO 19444-1), a string. |
| `importXfdf(xml)` | Adds the annotations of an XFDF document (the supported types; others are skipped); resolves with them. Long texts are cut to the limits below; an annotation with more than 1000 quadrilaterals is skipped, and the viewer's status line counts the skipped ones. |
| `registerEventListener(handler, { listenOn })` | See [annotation events](#annotation-events). |

A filter is `{ annotationIds: ['id1', …], pageRange: { startPage, endPage } }` (pages from 1),
each part optional. Changes reject with `NOT_ALLOWED` when the file was opened with
`hasReadOnlyAccess` or the document forbids annotations (its permissions, or a
[signature](#signed-and-certified-files)), and with `INVALID_ARGUMENT` (and a message that
says what) for an annotation that is not valid.

### Annotation JSON

Close to the Adobe PDF Embed API's format (W3C Web Annotation style):

```js
{
  id: 'b1c3…',                       // 1 to 200 characters; made when missing
  type: 'Annotation',
  motivation: 'highlighting',        // 'highlighting' for text markup, 'commenting' for the others (written by the viewer)
  bodyValue: 'The comment',          // the text: note, free text, or the comment of a markup; at most 10 000 characters
  target: {
    source: 'report-q3',             // metaData.id, else the file name (written by the viewer)
    selector: {
      node: { index: 0 },            // the page, counted from 0
      subtype: 'highlight',          // highlight, underline, strikeout, note, freetext, ink, square, circle,
                                     // line, polygon, polyline, stamp
      boundingBox: [x1, y1, x2, y2], // the annotation's /Rect
      quadPoints: [x1, y1, x2, y2, x3, y3, x4, y4, …],  // text markup: 8 numbers per line
      inkList: [[x, y, x, y, …], …], // ink: one array per stroke
      vertices: [x1, y1, x2, y2, …], // line: its two points (/L); polygon (3 or more), polyline (2 or more): /Vertices
      lineEndings: ['None', 'OpenArrow'],  // line and polyline: the start and end (/LE, see below)
      stampName: 'Approved',         // stamp: its /Name (see below)
      strokeColor: '#FFD400',        // the line colour; the text colour of free text
      fillColor: '#CCE5FF',          // inside of square, circle and polygon; closed line endings; free text background (optional)
      dashPattern: [4, 3],           // square, circle, line, polygon, polyline: dash and gap lengths in points (optional, see below)
      strokeWidth: 2,                // ink, square, circle, line, polygon, polyline, the free text border (0 to 100)
      opacity: 1,                    // 0 to 1
      fontSize: 12,                  // free text, 4 to 144
    },
  },
  creator: { type: 'Person', name: 'Ada' },
  created: '2026-10-26T10:00:00Z',  // ISO 8601; made when missing
  modified: '2026-10-26T10:05:00Z',
}
```

**Coordinates are PDF points in the page's user space: the origin is the lower-left corner of
the page's coordinate system (usually the lower-left corner of the page) and y goes up**, as in
the PDF file and in Adobe's API. The page's `/Rotate` is not applied. (`gotoLocation` takes
points from the top-left of the page as shown.) `quadPoints` give each quadrilateral as
upper-left, upper-right, lower-left, lower-right. For markup, `boundingBox` can be left out
(the box around `quadPoints`); for ink too (the box around the strokes), and for lines,
polygons and polylines (the box around `vertices`, grown to hold the line endings and cut to
the page).

`lineEndings` are names of ISO 32000-2 Table 179: `None`, `Square`, `Circle`, `Diamond`,
`OpenArrow`, `ClosedArrow`, `Butt`, `ROpenArrow`, `RClosedArrow`, `Slash`. `stampName` is one
of `Approved`, `SBCompleted`, `Confidential`, `Draft`, `Final`, `ForComment`, `ForPublicRelease`,
`SBInformationOnly`, `NotApproved`, `NotForPublicRelease`, `SBPreliminaryResults`, `Received`,
`SBReviewed`, `SBRevised`, `SBVoid`, `Expired`, `TopSecret`, `AsIs`, `Experimental`, `Sold`,
`Departmental`; the engine draws the stamp with its label in the colour of its kind.

`dashPattern` makes the line of a square, circle, line, polygon or polyline dashed (`/BS /S /D`
with this `/D`): 1 to 16 numbers from 0 to 1000, not all 0. The toolbar's presets are `[4, 3]`
(dashed) and `[1, 2]` (dotted). The viewer gives it only for a dashed shape, with the file's
numbers as they are (not rounded).

- `addAnnotations`: leave it out, or give `null` or `[]`, for a solid line.
- `updateAnnotation`: **leave it out to keep the shape's dash** (so a page written before
  `dashPattern` existed does not change it). Give `null` or `[]` to make the line solid. A dash
  within 0.1 % of the current one is not a change: a file's shape then keeps its own `/D`.
  `updateAnnotation` replaces the other values, so give the `fillColor` and `lineEndings` that the
  shape should keep.
- A measurement (`/Measure`) keeps its fill, dash and line endings: values given for them are
  ignored, and no error is returned.
- A polygon has no line endings: `lineEndings` given for one are ignored.

A **reply** answers another annotation (a note with `/IRT`, ISO 32000-2 12.5.6.4). It has no
selector: its `target` is the `id` of the annotation it answers, and it takes that one's page:

```js
{ id: 'r1', type: 'Annotation', motivation: 'replying', bodyValue: 'Agreed.',
  target: 'b1c3…', creator: { type: 'Person', name: 'Bo' }, created, modified }
```

A reply can answer a reply, up to 64 levels. What a reply answers cannot change with
`updateAnnotation`. `deleteAnnotations` deletes the replies of every annotation it deletes,
and resolves with them too. A reply is written with an empty appearance, so other readers list
it in their comments and do not draw a second note on the page.

Every value is checked: types, string lengths, colours (`#RRGGBB`), finite numbers, and the
geometry: on a page of the document and inside its media box (2 points of tolerance plus the
line width). Limits: 1000 annotations per call, 10 000 per document, 1000 quadrilaterals per
markup, 100 strokes and 10 000 points per stroke (50 000 points) per ink annotation, 10 000
vertices per polygon or polyline, and **1 000 000 ink, quadrilateral and vertex points per
document** (a quadrilateral has 4), counted when
annotations are added and when an update makes one larger: past it, `INVALID_ARGUMENT`. Texts:
`bodyValue` 10 000 characters, `creator.name` 256.

XFDF (`importXfdf`) is read as a stream: no DTD, no external entities, at most 8 MB of text,
**100 000 elements** and 64 attributes per element (past them, `INVALID_ARGUMENT`); unknown
elements are skipped without being built. Its texts are cut to the same limits as the
annotation JSON (title, subject and name 256 characters, contents 10 000), so one long field
does not refuse the whole import. A dash pattern must have 1 to 16 numbers, none negative and
not all zero, else it is `[3]`. Lines, polygons and polylines need finite points (a polygon or
polyline with more than 10 000 vertices is skipped, not cut); a line ending name that is not
in Table 179 is read as `None`. A reply's `inreplyto` names the annotation it answers: one of
the same import (before or after it), else one the document has. A reply whose parent is in
neither, and an annotation with `inreplyto` that is not a note reply (a group member, for
example), is added on its own. `exportXfdf` names a parent that has no `/NM` (`obj-<number>`),
so its replies keep their thread.

The file's annotations of these types are managed with `includePDFAnnotations` (not hidden
ones or other types: links, carets, file attachments, form fields and the like stay as they
are). Replies are managed when they are notes and their `/IRT` chain reaches a managed
annotation within 64 levels; a chain that loops, a group member (`/RT /Group`) and a reply of
another type stay as they are. A stamp from the file keeps its own appearance when it is moved.
Their `id` is their `/NM`, or `pdf-<page>-<index>` when they have none. New and changed
annotations are written with `/NM` set to their `id`, so ids survive a save.

### Annotation events

```js
manager.registerEventListener(event => console.log(event.type, event.data),
  { listenOn: ['ANNOTATION_ADDED', 'ANNOTATION_DELETED'] });   // leave listenOn out to hear all
```

| Event | When |
| --- | --- |
| `ANNOTATION_ADDED` | The reader adds an annotation (or undoes a deletion). |
| `ANNOTATION_UPDATED` | The reader moves or changes one (colour, text), or undo changes it back. |
| `ANNOTATION_DELETED` | The reader deletes one (or undoes its addition). |
| `ANNOTATION_SELECTED`, `ANNOTATION_UNSELECTED` | The reader selects one, or the selection ends. |
| `ANNOTATION_CLICKED` | The reader clicks one with the select tool. |

`event.data` is the annotation (the JSON above). Events report what the reader does; the
manager's own calls do not fire them, so a page that keeps annotations on its server gets no
echo of its own changes.

## Saving (SAVE_API)

```js
view.registerCallback(PdfViewer.View.Enum.CallbackType.SAVE_API,
  async (metaData, content, options) => {
    await fetch('/api/files/' + metaData.id, { method: 'PUT', body: content });   // your server
    return { code: PdfViewer.View.Enum.ApiResponseCode.SUCCESS, data: { metaData } };
  },
  { autoSaveFrequency: 0, enableFocusPolling: false, showSaveButton: true });
```

Register it before `previewFile` (or after: it applies to the open document). With it, the
viewer shows a **Save** button (`showSaveButton`, default `true`), and saves when the reader
presses it or `Ctrl`+`S`, every `autoSaveFrequency` seconds (0, the default, is never; at most
86400) when something changed, and, with `enableFocusPolling: true`, when the viewer loses the
focus (the reader goes back to your page) with changes not saved.

- `metaData` is `{ fileName, id }`. `content` is an `ArrayBuffer`: the PDF with the reader's
  annotations and form data, as an incremental update of the file you gave (earlier revisions
  and their signatures stay as they were). It is transferred to your page, not copied.
- `options` is `{ reason }`: `button`, `shortcut`, `autosave` or `focus`.
- Resolve with `{ code: 'SUCCESS' }` and the viewer shows "Saved" and counts the document as
  saved (the version it posted: changes made while your callback ran stay unsaved); anything
  else (`FAIL`, a throw, a rejection) shows "The file was not saved." and keeps the changes as
  unsaved. `data.metaData.fileName` renames the file in the viewer.
- One save runs at a time. The button, `Ctrl`+`S`, autosave and focus polling do not start a
  second one while a save waits for the changes to be written or for your answer, and the SDK
  calls your callback for one save after another (never two at once), so an older, slower save
  cannot finish after a newer one. A save stays in flight until your callback answers or the
  viewer closes: after 120 seconds the viewer only says "Still saving…".
- If the viewer cannot write the reader's latest changes into the document, it shows an error,
  keeps the changes as unsaved and does not call your callback: it never posts a file without
  the changes it shows.

Without a `SAVE_API` callback, `Ctrl`+`S` downloads the annotated document (when
`showDownloadPDF` is on). Nothing is sent anywhere but to your callback.

## Forms

With `enableFormFilling` (and unless `hasReadOnlyAccess`), the reader fills the document's
AcroForm fields: text (single and multi-line, password, maximum length), check boxes, radio
buttons, combo boxes and list boxes. Read-only fields, push buttons and signature fields are
not filled. A value goes to the engine, which writes it with a new appearance and runs the
form's calculations, formats and validations as the Windows viewer does: the standard Acrobat
functions (`AFSimple_Calculate`, `AFNumber_Format`, `AFRange_Validate`, …) and simplified
field notation are recognised, **never run: document JavaScript is never executed**. A value
that a validation refuses is not kept, and the viewer says why. Saving (SAVE_API, or Download
with `downloadWithAnnotations`) writes the values into the PDF. XFA forms are not supported.

Values are checked as the reader enters them: a combo box (unless it is editable) and a list
box take only their options (export value or display text), a text field takes at most its
`/MaxLen` characters, every value at most 10 000 characters, and at most 10 000 fields change at
once. A refused value is not kept, and the viewer shows why.

**There is no API to set form values from your page in this version:** form fields take the
reader's input only. The saved file (SAVE_API) has the values; read them on your server.

### Signed and certified files

The viewer follows Acrobat's rules for a file with signatures, so that its changes never
break them silently:

| The file | Annotations | Form filling |
| --- | --- | --- |
| Certified, DocMDP `P=1` (no changes) | No | No |
| Certified, `P=2` (form filling and signing) | No | Yes |
| Certified, `P=3` (also annotations) | Yes | Yes |
| Signed, not certified | Yes | Yes |
| Signed (or certified) and damaged, so that a save would rewrite the whole file | No | No |

When changes are allowed, the viewer tells the reader once, at the first change, that
changes are added as a new revision: existing signatures stay valid, but they show that the
document was modified after signing. Saves are incremental updates, so the signed bytes stay
as they were (they are a prefix of the saved file). When changes are not allowed, the viewer
says why, the tools stay hidden, and the annotation manager's changes reject with
`NOT_ALLOWED`. `getPDFMetadata()` reports `isSigned` and `certificationLevel`. The viewer does
not check signatures (the Windows viewer does).

## In the viewer

- **Text selection:** drag with the mouse or a pen over a page, double-click for a word,
  `Ctrl`+`A` for the page, `Ctrl`+`C` to copy. One page at a time. Touch scrolls.
- **Search** with the box in the toolbar, the results in the side panel (full window).
- **Thumbnails** and the **outline** in the side panel. Night pages (inverted colours).
- The same engine as the Windows viewer: see the [web viewer's limits](https://github.com/ramanacr/pdf-viewer/tree/main/src/PdfViewer.Web#limits)
  (fonts a PDF does not embed, CMYK JPEG images, certificate encryption).

### Printing

Print renders every page to a 150 dpi image and prints the images through a hidden frame. The
printout is a picture of each page: text is sharp enough to read but not as crisp as a PDF
application's print, and it cannot be selected. Every sheet has the size of the first page.
Documents of more than 500 pages are not printed from the browser: download them and print them
from a PDF application.

## Security and privacy

- **Isolation.** The viewer runs in an `iframe` with
  `sandbox="allow-scripts allow-same-origin allow-downloads allow-modals"`: scripts (the engine),
  its own origin (its files and fonts), downloads (Download) and modal dialogs (Print). It cannot
  navigate your page, open pop-ups or submit forms, as long as it has another origin than your
  page (see [Same-origin viewer](#same-origin-viewer)).
- **Messages.** The two sides talk only by `postMessage`, with a versioned protocol
  (`{ protocol: 'pdfviewer-embed', version: 1, type, ... }`):
  - The SDK accepts messages only from its own frame (`event.source`) and the viewer's origin
    (`event.origin`), and posts only to that origin.
  - The frame's URL carries your page's origin (`?embed=1&mode=…&origin=…`). The viewer accepts
    messages only from its parent window and that origin, and posts only to that origin. If
    another site frames the viewer and names your origin, the browser delivers nothing to it.
  - The viewer checks every message (`EmbedProtocol` in `src/PdfViewer.Web.Engine/Embed`, with
    unit tests): unknown protocols, versions and types are ignored; bad arguments are refused
    with a code.
  - The file goes to the viewer as a transferred `ArrayBuffer`, and a saved file comes back the
    same way, only to the SAVE_API callback.
  - Annotations, filters, XFDF and save results from your page are untrusted data to the viewer:
    every value is checked for type, length and range (see [annotation JSON](#annotation-json)),
    and an XFDF document is read as a stream without a DTD or external entities, with element,
    attribute and length limits.
  - A message longer than 2 MB of JSON (the file's bytes do not count) is refused with
    `INVALID_ARGUMENT` before it is parsed.
  - Range loading (see [large files](#large-files)): the viewer lists `range` in the `features`
    of its `ready` message, and the SDK uses range loading only then. A `fetchRange` request
    carries only numbers (`file`, `requestId`, `start`, `end`), never a URL. The SDK serves it
    only for the file it opened last, only for a new request id, only for whole numbers inside
    the file with `end - start` at most 4 MB, and only while fewer than 6 requests wait; it
    fetches the bytes from the URL or `linearizationInfo` you gave. The viewer checks each
    answer: the request must be waiting, of that file, with exactly the bytes asked for.
- **No storage.** The embedded viewer keeps nothing in the browser's storage (the stand-alone
  demo keeps its theme and side-panel choice).
- **No telemetry, no uploads.** The viewer requests only its own files (the app, and fonts when
  a page needs them). Document JavaScript is never run.

### Same-origin viewer

The sandbox of the frame keeps the viewer apart from your page only when the two have different
origins. With `allow-scripts allow-same-origin` on the same origin, the viewer's scripts could
reach your page (its DOM, cookies and storage), so a fault in the viewer would be a fault in your
page. The viewer needs both flags: it runs WebAssembly and loads its own files and fonts.

When the viewer has the origin of your page, the SDK writes one warning to the console. The
examples on this site do this: they load the viewer from `/pdf-viewer/demo/` on the same origin.
For production, serve the viewer from another origin, for example a subdomain
(`https://pdf.example.com` for pages on `https://www.example.com`), and make the SDK refuse a
same-origin viewer:

```js
new PdfViewer.View({ divId: 'viewer', baseUrl: 'https://pdf.example.com/', isolation: 'require' });
```

With `isolation: 'require'`, the constructor throws a `PdfViewerError` with the code
`INVALID_ARGUMENT` when the origins are the same.

### Who can frame the viewer

Any site can put the viewer in a frame. The viewer still talks only to the origin that its frame
URL names, and the browser delivers its messages only to that origin, so another site gets only
the files that it sends itself. But it can show them under the viewer's address, and it can lay
its own content over the frame (clickjacking).

A Content-Security-Policy `<meta>` tag cannot set `frame-ancestors` (browsers ignore it there), and
GitHub Pages cannot send response headers. If you host the viewer, send this header with the
viewer's `index.html`, and name the sites that may embed it:

```
Content-Security-Policy: frame-ancestors https://www.example.com https://intranet.example.com
```

For example, in nginx:
`add_header Content-Security-Policy "frame-ancestors https://www.example.com" always;`.
The header adds to the `<meta>` policy of `index.html`; the browser applies both.

For the public demo on this site, the impact is low: it has no accounts, no cookies and no stored
documents, it shows only the files that the embedding page sends, and it is open source, so anyone
can host the same viewer anyway.

### Copy protection

A PDF can forbid copying its text. In such a document:

- `getSelectedContent()` rejects with `NOT_ALLOWED`.
- `Ctrl`+`C` copies nothing, and no `TEXT_COPY` event is sent.
- Search still works, from the search box and from `search()`, as in Adobe Acrobat, which allows
  search in copy-protected documents. `search()` gives your page only page numbers and counts, not
  the text of the document.

## Browser support

Browsers with WebAssembly SIMD and exception handling: current Chrome, Edge, Firefox and
Safari. Tested on Chromium (Chrome headless and the Chromium of the Claude desktop app).
Firefox and Safari are not tested yet.

## Limits

| | |
| --- | --- |
| First load | The viewer is about 5.6 MB with Brotli, about 6.6 MB with gzip (GitHub Pages), then cached by the browser. |
| File size | 512 MB. |
| Pages | Drawn one at a time on the main thread, in strips; a heavy page's interpretation still blocks the viewer for a moment (see [responsiveness](#responsiveness)). |
| Loading | By ranges when the server allows it (see [large files](#large-files)); otherwise the whole file is read before the first page shows. While a ranged file loads, calls that need all of it wait. |
| Selection | One page at a time; dragging does not scroll. |
| Annotations | The types above and replies; no callouts, carets, file attachments, custom stamps or reply states (/State) yet, and a line or polygon is moved as a whole (its points have no handles yet). Each change writes the document again from the file as opened: on a very large file a change takes a moment. Undo: 100 steps, annotations only (form fields have the browser's own undo while typing). |
| Forms | AcroForm only (no XFA); no document JavaScript, so custom scripts do nothing; free text and stamps embed the bundled Liberation Sans. No API to set form values from the host page yet. Simplified field notation nested deeper than 64 levels is not calculated. |
| Language | The viewer is in English. |

## Responsiveness

Blazor WebAssembly runs .NET on the browser's main thread, so the work of a page (interpreting
its content into a display list, then drawing it with Skia) blocks input while it runs. The
viewer keeps scrolling responsive this way:

1. Pages are drawn in horizontal strips of about 256 K pixels, with a yield to the browser
   between strips (`ViewerDocument.RenderPageInStripsAsync`).
2. A page that leaves the view, or a zoom or night-mode change, stops its remaining strips; the
   display list stays cached, so the page costs only its drawing when it comes back.
3. While the view scrolls, no new page or thumbnail starts; work starts when the view has rested
   for 120 ms. Pages nearest the current one go first.

Measured in headless Chrome (viewer frame, `PerformanceObserver` long tasks over 50 ms, while a
script scrolls 350 px every 100 ms for 4 s), 40 generated pages with an image and thousands of
filled rectangles each:

| Pages | Before: long tasks while scrolling | After | Scroll-tick delay, 90th percentile: before → after |
| --- | --- | --- | --- |
| 3,000 rectangles (interpret 170 to 370 ms, draw 120 to 230 ms per page) | 13 to 14, 5.7 to 6.1 s in all, longest 0.57 s | 5 to 6, 1.0 to 1.1 s in all, longest 0.44 s | 0.39 to 0.43 s → 0.02 to 0.04 s |
| 15,000 rectangles (interpret 750 to 940 ms, draw 390 to 460 ms) | 12 to 13, 17 to 22 s in all, longest 2.0 s | 6, 2.4 s in all, longest 1.0 s | 1.8 to 1.9 s → 0.03 to 0.04 s |

What remains: the interpretation of one page is one task (here up to 1 s), when it runs.

**Not possible on GitHub Pages (or any host that cannot send headers):**

- **.NET multithreading** (`WasmEnableThreads`, still experimental in .NET 9) needs
  `SharedArrayBuffer`, so the page must be cross-origin isolated with the response headers
  `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp`.
  GitHub Pages cannot send response headers, and an embedded viewer would also need the host page
  to be isolated (or to frame it with `allow="cross-origin-isolated"`).
- **A second .NET runtime in a Web Worker** needs no headers. The viewer has one, as an option:
  see the next section.

### Rendering in a worker

With `renderInWorker: true` (or `index.html?renderInWorker=1` for the viewer on its own), the
pages in view are interpreted and drawn by a second, render-only .NET runtime in a dedicated Web
Worker. The viewer's own runtime keeps the text, search, selection, annotations, forms,
thumbnails and printing.

```js
const view = new PdfViewer.View({ divId: 'viewer', renderInWorker: true });
```

- **Measured** in headless Chrome, engine tour, zoom and scroll: total blocking time 110 to 188 ms
  on the main thread, 0 ms with the worker; the longest gap of a 10 ms timer 166 to 244 ms, against
  16 to 22 ms. The time to the first page is about the same.
- **Cost:** the worker requests the runtime's files a second time (from the HTTP cache when the
  server allows caching) and holds a second copy of the runtime and of the document in memory.
- **Scope:** the worker draws complete, unencrypted files with the bundled fonts. Files still
  loading by ranges, encrypted files and fonts lent by the host are drawn on the main thread.
- **Fallback:** while the worker starts, and when it fails, stops answering or cannot draw a page,
  the pages are drawn on the main thread as without the option.
- **Checked** in Chrome and Firefox on Windows; not yet in Safari or on a phone, so it is off by
  default.

## Planned

- Rendering in a worker by default, once it is checked in Safari and on phones.
- Later: handles for the points of lines and polygons, reply states (accepted, rejected), callouts and custom stamps.
