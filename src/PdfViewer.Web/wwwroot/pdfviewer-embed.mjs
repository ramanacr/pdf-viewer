// PDF Viewer embed SDK as an ES module:
//
//   import { View } from 'https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.mjs';
//   const view = new View({ divId: 'viewer' });
//
// pdfviewer-embed.js does the work (it is also a classic script that defines window.PdfViewer);
// this module tells it where it was loaded from, the default place of the viewer files.
import './pdfviewer-embed.js';

const PdfViewer = globalThis.PdfViewer;
PdfViewer._setScriptUrl(import.meta.url);

export const View = PdfViewer.View;
export const PdfViewerError = PdfViewer.PdfViewerError;
export const version = PdfViewer.version;
export default PdfViewer;
