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
so code written for it needs few changes. Status: **phase 1, preview.** The API can change
before version 1.0.

**[Examples](examples/)**: full window, sized container, in line, light box, events and APIs,
file picker, React and Angular.

## Compared with the Adobe PDF Embed API

| | This SDK | Adobe PDF Embed API |
| --- | --- | --- |
| Credentials | None | A client ID, registered for your domain |
| Where the viewer comes from | Your choice: this site, or your own copy of the files (static files only) | Adobe's servers |
| Where the PDF is rendered | In the reader's browser (WebAssembly) | In the reader's browser |
| Events | Only to the callbacks of your page; nothing is sent anywhere | To your callbacks; optional Adobe Analytics |
| Embed modes | Full window, sized container, in line, light box | The same |
| Annotations, comments, save | Planned (phase 2) | Available |
| Licence | MIT, open source | Adobe terms |

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

`view.destroy()` removes the viewer from the page (not in the Adobe API).

## `view.previewFile(file, config)`

Returns a Promise. It resolves with a viewer object when the document is open, and rejects with
a `PdfViewer.PdfViewerError` when it cannot be shown. Call it again to show another file in the
same View.

### File

```js
{
  content: { location: { url, headers: [{ key, value }] } }   // or
           { promise }                                       // resolves to an ArrayBuffer, typed array or Blob
           { arrayBuffer },                                  // an ArrayBuffer, typed array or Blob
  metaData: { fileName, id, hasReadOnlyAccess },
}
```

- **`location`.** Your page fetches the URL, with `fetch`, so your page's own rules apply: the
  headers you give, cookies for your own site, and the server's CORS rules for another site.
  Add `credentials: 'include'` to `location` to send cookies to another site. The viewer itself
  never fetches the file: its Content-Security-Policy allows only its own files.
- **`promise` and `arrayBuffer`.** The SDK copies the bytes and transfers the copy to the
  viewer, so your buffer stays usable.
- **`metaData.fileName`** is shown in the viewer and used for Download. **`id`** comes back in
  `getPDFMetadata()`. **`hasReadOnlyAccess`** is accepted; the viewer has no editing tools yet.
- Largest file: 512 MB. For `location`, the SDK counts the bytes as they arrive and stops the
  download (`TOO_LARGE`) as soon as there are more; a `Content-Length` that is already too large
  fails at once. A Blob is checked by its size before it is read.

### Config

| Key | Values | Default | Notes |
| --- | --- | --- | --- |
| `embedMode` | `FULL_WINDOW`, `SIZED_CONTAINER`, `IN_LINE`, `LIGHT_BOX` | `FULL_WINDOW` | See [embed modes](#embed-modes). |
| `defaultViewMode` | `FIT_WIDTH`, `FIT_PAGE`, `TWO_COLUMN`, `TWO_COLUMN_FIT_PAGE` | `FIT_WIDTH` | In line, fit page is fit width. |
| `showDownloadPDF` | `true`, `false` | `true` | Download saves the original bytes. |
| `showPrintPDF` | `true`, `false` | `true` | See [printing](#printing). Disabled when the document forbids printing. |
| `showZoomControl` | `true`, `false` | `true` | |
| `showBookmarks` | `true`, `false` | `true` | The outline tab of the side panel (full window, light box). |
| `showThumbnails` | `true`, `false` | `true` | The pages tab of the side panel (full window, light box). |
| `showFullScreen` | `true`, `false` | `true` | Sized container only. |
| `showAnnotationTools` | `true`, `false` | `true` | Accepted; no annotation tools yet (phase 2). |
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

## Events

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
| `getPDFMetadata()` | `{ numPages, pdfTitle, author, subject, keywords, creator, producer, creationDate, modDate, pdfVersion, isEncrypted, isTagged, canCopy, canPrint, wasRepaired, fileSize, fileName, id }`. Dates are `"YYYY-MM-DDTHH:mm:ss"` as written in the file, or `null`. |
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

`PdfViewer.View.Enum.ErrorCode`: `INVALID_ARGUMENT`, `FILE_NOT_FOUND` (HTTP 404 or 410),
`HTTP_ERROR` (another HTTP error; `detail.status`), `NETWORK_ERROR` (the network failed or CORS
does not let your page read the file: browsers do not say which), `NOT_A_PDF`, `TOO_LARGE`,
`PASSWORD_REQUIRED` and `WRONG_PASSWORD` (the reader closed the password prompt),
`CERTIFICATE_REQUIRED`, `ENCRYPTION_NOT_SUPPORTED`, `VIEWER_LOAD_FAILED` (the viewer did not start
in 60 seconds), `NO_DOCUMENT`, `NOT_ENABLED`, `NOT_ALLOWED`, `SEARCH_ENDED`, `CLOSED` (the viewer
was removed or the light box closed), `VIEWER_ERROR`.

Password-protected files: the viewer asks the reader for the password. Your page never sees it.

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
  - The file goes to the viewer as a transferred `ArrayBuffer`.
  - A message longer than 2 MB of JSON (the file's bytes do not count) is refused with
    `INVALID_ARGUMENT` before it is parsed.
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
| Pages | Drawn one at a time on the main thread; a heavy page can block the viewer for a moment. |
| Loading | The whole file is read before the first page shows. |
| Selection | One page at a time; dragging does not scroll. |
| Language | The viewer is in English. |

## Planned

- **Phase 2:** an annotation manager (`getAnnotationManager`), `SAVE_API` callback,
  annotation tools.
- **Phase 3:** linearized and range loading (show the first page before the whole file has
  arrived), rendering in a Web Worker.
