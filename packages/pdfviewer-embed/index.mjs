// PDF Viewer embed SDK from npm:
//
//   import { View } from 'pdfviewer-embed';
//   const view = new View({ divId: 'viewer' });
//
// A bundler moves this file, so its own URL does not say where the viewer is. The viewer files
// are not in the package: by default they come from the project's site, at the same address as
// <script src=".../demo/pdfviewer-embed.js">. Pass baseUrl to View to use a copy you host.
import './pdfviewer-embed.js';

const PdfViewer = globalThis.PdfViewer;
PdfViewer._setScriptUrl('https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.js');

export const View = PdfViewer.View;
export const PdfViewerError = PdfViewer.PdfViewerError;
export const version = PdfViewer.version;
export default PdfViewer;
