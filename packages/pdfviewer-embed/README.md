# pdfviewer-embed

Show PDF files in your web page with [PDF Viewer Native](https://github.com/ramanacr/pdf-viewer)'s
own engine. The engine runs in the reader's browser as WebAssembly. There is no client ID, no
account, no server and no telemetry: the file goes from your page to the viewer in the same
browser tab, and nowhere else.

The API follows the
[Adobe PDF Embed API](https://developer.adobe.com/document-services/docs/overview/pdf-embed-api/howtos/),
so code written for it needs few changes. Status: **preview**. The API can change before
version 1.0.

- Embed modes: full window, sized container, in line, light box.
- Events and viewer APIs: pages, zoom, search, selection.
- Annotations: highlight, underline, strike-through, note, free text, ink, rectangle and
  ellipse, an annotation manager, XFDF import and export.
- Saving with `SAVE_API`, form filling with calculations (document JavaScript never runs).
- Large files open by range requests, so the first page shows before the whole file downloads.

## Install

```bash
npm install pdfviewer-embed
```

## Use

```html
<div id="viewer" style="height: 600px"></div>
```

```js
import { View } from 'pdfviewer-embed';

const view = new View({ divId: 'viewer' });
view.previewFile(
  { content: { location: { url: '/files/report.pdf' } }, metaData: { fileName: 'report.pdf' } },
  { embedMode: 'SIZED_CONTAINER' });
```

Your page must be served over `http` or `https`. The package works only in a browser; with
server-side rendering, import it in client code only.

## Where the viewer comes from

This package holds the SDK script only. The viewer that it shows in an iframe (the app and the
engine) is not in the package. By default the viewer comes from the project's site,
`https://ramanacr.github.io/pdf-viewer/demo/`, which changes on each deploy.

For production, host your own copy of the viewer, from a release or a commit that you chose,
and pass its address:

```js
new View({ divId: 'viewer', baseUrl: 'https://pdf.example.com/', isolation: 'require' });
```

See [Host the viewer yourself](https://ramanacr.github.io/pdf-viewer/sdk/#host-the-viewer-yourself)
and [Production](https://ramanacr.github.io/pdf-viewer/sdk/#production).

## As a classic script

`pdfviewer-embed/script` is the same SDK as a classic script that defines the global
`PdfViewer`. Loaded by a `<script src>` tag, it finds the viewer in its own folder.

## Documentation

- [SDK guide and API](https://ramanacr.github.io/pdf-viewer/sdk/)
- [Examples](https://ramanacr.github.io/pdf-viewer/sdk/examples/): full window, sized container,
  in line, light box, events and APIs, file picker, annotations, forms, large files, React and
  Angular.

## Licence

MIT. The package is built from
[`src/PdfViewer.Web/wwwroot/pdfviewer-embed.js`](https://github.com/ramanacr/pdf-viewer/blob/main/src/PdfViewer.Web/wwwroot/pdfviewer-embed.js)
and published from GitHub Actions with npm provenance.
