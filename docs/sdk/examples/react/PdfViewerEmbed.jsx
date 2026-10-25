// PDF Viewer embed SDK in a React app. Copy into your app: this file is not run here.
//
// 1. Load the SDK once, in index.html (or with a script loader), from where you serve the viewer:
//      <script src="https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.js"></script>
//    It defines window.PdfViewer. There is no npm package and nothing to install.
// 2. Use the component:
//      <PdfViewerEmbed url="/files/report.pdf" fileName="report.pdf"
//                      config={{ embedMode: 'SIZED_CONTAINER' }} style={{ height: 600 }}
//                      onEvent={e => console.log(e.type, e.data)} />
//
// The component makes one View per mounted element, shows the file again when url changes, and
// removes the viewer when it unmounts.

import { useEffect, useId, useRef } from 'react';

export default function PdfViewerEmbed({ url, fileName = 'document.pdf', config = {}, onEvent, onReady, onError, style }) {
    // An id that is valid in getElementById (useId returns ":r0:" and the like).
    const divId = 'pdfviewer-' + useId().replace(/[^a-zA-Z0-9_-]/g, '');
    const viewRef = useRef(null);
    const onEventRef = useRef(onEvent);
    onEventRef.current = onEvent;

    useEffect(() => {
        const view = new window.PdfViewer.View({ divId });
        view.registerCallback(window.PdfViewer.View.Enum.CallbackType.EVENT_LISTENER, e => onEventRef.current?.(e), {});
        viewRef.current = view;
        return () => {
            view.destroy();
            viewRef.current = null;
        };
    }, [divId]);

    // The config object is compared by its JSON, so an inline object literal does not reopen the file.
    const configKey = JSON.stringify(config);
    useEffect(() => {
        const view = viewRef.current;
        if (!view || !url) return;
        let current = true;
        view.previewFile({ content: { location: { url } }, metaData: { fileName } }, JSON.parse(configKey))
            .then(viewer => viewer.getAPIs())
            .then(apis => { if (current) onReady?.(apis); })
            .catch(error => { if (current && error.code !== 'CLOSED') onError?.(error); });
        return () => { current = false; };
    }, [url, fileName, configKey]);

    return <div id={divId} style={{ height: 600, ...style }} />;
}
