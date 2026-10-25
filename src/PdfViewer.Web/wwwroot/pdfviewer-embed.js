/*
 * PDF Viewer embed SDK: shows a PDF in your web page with PDF Viewer Native's own engine, which
 * runs in the reader's browser as WebAssembly. No client ID, no server, no telemetry: the file
 * is read in the browser and never uploaded, and events go only to the callbacks of this page.
 *
 *   <div id="viewer" style="height: 600px"></div>
 *   <script src="https://ramanacr.github.io/pdf-viewer/demo/pdfviewer-embed.js"></script>
 *   <script>
 *     const view = new PdfViewer.View({ divId: 'viewer' });
 *     view.previewFile({ content: { location: { url: 'report.pdf' } }, metaData: { fileName: 'report.pdf' } },
 *                      { embedMode: 'SIZED_CONTAINER' });
 *   </script>
 *
 * This file is a classic script that defines the global PdfViewer. It has no imports or exports,
 * so it also runs as a module; pdfviewer-embed.mjs next to it exports the same API. The viewer
 * itself is in an iframe next to this file (or at baseUrl) and talks to this script only by
 * postMessage, with the protocol described in docs/sdk/index.md.
 *
 * MIT License. https://github.com/ramanacr/pdf-viewer
 */
(function (global) {
    'use strict';

    if (global.PdfViewer && global.PdfViewer.View) return;   // loaded twice: keep the first

    const PROTOCOL = 'pdfviewer-embed';
    const PROTOCOL_VERSION = 1;
    const SDK_VERSION = '0.2.0';
    const MAX_FILE_BYTES = 512 * 1024 * 1024;
    const BOOT_TIMEOUT_MS = 60000;
    const ISOLATION_DOCS = 'https://ramanacr.github.io/pdf-viewer/sdk/#same-origin-viewer';

    const EmbedMode = Object.freeze({ FULL_WINDOW: 'FULL_WINDOW', SIZED_CONTAINER: 'SIZED_CONTAINER', IN_LINE: 'IN_LINE', LIGHT_BOX: 'LIGHT_BOX' });
    const ViewMode = Object.freeze({ FIT_PAGE: 'FIT_PAGE', FIT_WIDTH: 'FIT_WIDTH', TWO_COLUMN: 'TWO_COLUMN', TWO_COLUMN_FIT_PAGE: 'TWO_COLUMN_FIT_PAGE' });
    const CallbackType = Object.freeze({ EVENT_LISTENER: 'EVENT_LISTENER', SAVE_API: 'SAVE_API' });
    const ApiResponseCode = Object.freeze({ SUCCESS: 'SUCCESS', FAIL: 'FAIL' });
    const AnnotationEvents = Object.freeze({
        ANNOTATION_ADDED: 'ANNOTATION_ADDED', ANNOTATION_UPDATED: 'ANNOTATION_UPDATED', ANNOTATION_DELETED: 'ANNOTATION_DELETED',
        ANNOTATION_SELECTED: 'ANNOTATION_SELECTED', ANNOTATION_UNSELECTED: 'ANNOTATION_UNSELECTED', ANNOTATION_CLICKED: 'ANNOTATION_CLICKED',
    });
    const MAX_AUTOSAVE_SECONDS = 86400;
    const Events = Object.freeze({
        DOCUMENT_OPEN: 'DOCUMENT_OPEN', PAGE_VIEW: 'PAGE_VIEW', TEXT_COPY: 'TEXT_COPY', TEXT_SEARCH: 'TEXT_SEARCH',
        ZOOM_CHANGE: 'ZOOM_CHANGE', DOCUMENT_DOWNLOAD: 'DOCUMENT_DOWNLOAD', DOCUMENT_PRINT: 'DOCUMENT_PRINT', PREVIEW_ERROR: 'PREVIEW_ERROR',
    });
    const ErrorCode = Object.freeze({
        INVALID_ARGUMENT: 'INVALID_ARGUMENT',
        FILE_NOT_FOUND: 'FILE_NOT_FOUND',
        HTTP_ERROR: 'HTTP_ERROR',
        NETWORK_ERROR: 'NETWORK_ERROR',
        NOT_A_PDF: 'NOT_A_PDF',
        TOO_LARGE: 'TOO_LARGE',
        PASSWORD_REQUIRED: 'PASSWORD_REQUIRED',
        WRONG_PASSWORD: 'WRONG_PASSWORD',
        CERTIFICATE_REQUIRED: 'CERTIFICATE_REQUIRED',
        ENCRYPTION_NOT_SUPPORTED: 'ENCRYPTION_NOT_SUPPORTED',
        VIEWER_LOAD_FAILED: 'VIEWER_LOAD_FAILED',
        NO_DOCUMENT: 'NO_DOCUMENT',
        NOT_ENABLED: 'NOT_ENABLED',
        SEARCH_ENDED: 'SEARCH_ENDED',
        NOT_ALLOWED: 'NOT_ALLOWED',
        CLOSED: 'CLOSED',
        VIEWER_ERROR: 'VIEWER_ERROR',
    });

    /** Every failure of this SDK: `code` is one of PdfViewer.View.Enum.ErrorCode. */
    class PdfViewerError extends Error {
        constructor(code, message, detail) {
            super(message);
            this.name = 'PdfViewerError';
            this.code = code;
            if (detail !== undefined) this.detail = detail;
        }
    }

    const fail = (code, message, detail) => new PdfViewerError(code, message, detail);

    // The folder of this script: the default place of the viewer files. A module import sets it
    // through pdfviewer-embed.mjs, because document.currentScript is null in a module.
    let scriptBase = null;
    try {
        const current = global.document && global.document.currentScript;
        if (current && current.src) scriptBase = new URL('.', current.src).href;
    } catch { /* not in a browser page */ }

    function viewerBase(baseUrl) {
        const raw = baseUrl !== undefined && baseUrl !== null ? baseUrl : scriptBase;
        if (typeof raw !== 'string' && !(raw instanceof URL)) {
            throw fail(ErrorCode.INVALID_ARGUMENT, 'baseUrl is required when the SDK is not loaded by a <script src> tag or by pdfviewer-embed.mjs.');
        }
        let url;
        try {
            url = new URL(String(raw), global.document.baseURI);
        } catch {
            throw fail(ErrorCode.INVALID_ARGUMENT, 'baseUrl is not a valid URL.');
        }
        if (url.protocol !== 'https:' && url.protocol !== 'http:') throw fail(ErrorCode.INVALID_ARGUMENT, 'baseUrl must be an http or https URL.');
        url.search = '';
        url.hash = '';
        if (!url.pathname.endsWith('/')) url.pathname += '/';
        return url;
    }

    /** A copy of the caller's value that postMessage can clone: plain data only, functions dropped. */
    function plainData(value, what) {
        if (value === undefined || value === null) return {};
        if (typeof value !== 'object' || Array.isArray(value)) throw fail(ErrorCode.INVALID_ARGUMENT, `${what} must be an object.`);
        try {
            return JSON.parse(JSON.stringify(value));
        } catch {
            throw fail(ErrorCode.INVALID_ARGUMENT, `${what} must be plain data.`);
        }
    }

    /** The file's bytes as an ArrayBuffer that this SDK owns (it is transferred to the viewer). */
    async function readContent(content) {
        if (!content || typeof content !== 'object') throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content must be an object with location, promise or arrayBuffer.');
        const given = ['location', 'promise', 'arrayBuffer'].filter(k => content[k] !== undefined && content[k] !== null);
        if (given.length !== 1) throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content must have exactly one of location, promise and arrayBuffer.');
        if (given[0] === 'location') return fetchLocation(content.location);
        const value = given[0] === 'promise' ? await content.promise : content.arrayBuffer;
        return ownBuffer(value);
    }

    /** A copy, so that transferring it to the viewer never empties the caller's own buffer. */
    async function ownBuffer(value) {
        if (value instanceof ArrayBuffer) return value.slice(0);
        if (ArrayBuffer.isView(value)) return value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength);
        if (typeof Blob !== 'undefined' && value instanceof Blob) {
            // Check the size before reading, so that a huge Blob is never read into memory.
            if (value.size > MAX_FILE_BYTES) throw tooLarge();
            return value.arrayBuffer();
        }
        throw fail(ErrorCode.INVALID_ARGUMENT, 'The file content must be an ArrayBuffer, a typed array or a Blob.');
    }

    const tooLarge = () => fail(ErrorCode.TOO_LARGE, `The file is larger than ${MAX_FILE_BYTES / (1024 * 1024)} MB, the most the viewer opens.`);

    /**
     * Fetches the file in this page, so that this page's cookies, the headers given and the
     * server's CORS rules apply, exactly as for any other fetch of this page.
     */
    async function fetchLocation(location) {
        if (!location || typeof location.url !== 'string' || location.url.length === 0) {
            throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content.location.url must be a non-empty string.');
        }
        const headers = new Headers();
        if (location.headers !== undefined) {
            if (!Array.isArray(location.headers)) throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content.location.headers must be an array of { key, value }.');
            for (const h of location.headers) {
                if (!h || typeof h.key !== 'string' || typeof h.value !== 'string') {
                    throw fail(ErrorCode.INVALID_ARGUMENT, 'Each header must be { key: string, value: string }.');
                }
                try {
                    headers.append(h.key, h.value);
                } catch {
                    throw fail(ErrorCode.INVALID_ARGUMENT, `The header "${h.key}" is not valid.`);
                }
            }
        }
        let url;
        try {
            url = new URL(location.url, global.document.baseURI);
        } catch {
            throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content.location.url is not a valid URL.');
        }
        let response;
        try {
            response = await fetch(url.href, { headers, credentials: location.credentials === 'include' ? 'include' : 'same-origin' });
        } catch (e) {
            throw fail(ErrorCode.NETWORK_ERROR,
                'The file could not be fetched: the network failed, or the server does not let this page read it (CORS).', String(e && e.message || e));
        }
        if (response.status === 404 || response.status === 410) throw fail(ErrorCode.FILE_NOT_FOUND, `The file was not found (HTTP ${response.status}).`, { status: response.status });
        if (!response.ok) throw fail(ErrorCode.HTTP_ERROR, `The server answered HTTP ${response.status}.`, { status: response.status });
        // A readable Content-Length that is too large fails at once. A missing or bad one is
        // unknown, not 0: readCapped counts the bytes as they arrive in all cases.
        const header = response.headers.get('Content-Length');
        const length = header !== null && /^\s*\d+\s*$/.test(header) ? Number(header) : NaN;
        if (Number.isFinite(length) && length > MAX_FILE_BYTES) {
            if (response.body) response.body.cancel().catch(() => { });
            throw tooLarge();
        }
        return readCapped(response, MAX_FILE_BYTES);
    }

    /**
     * Reads a response body, but stops (cancels the stream) and rejects with TOO_LARGE as soon as
     * more than `limit` bytes have arrived. Content-Length can be missing or wrong (compressed or
     * chunked responses), so only the bytes counted here are trusted.
     */
    async function readCapped(response, limit) {
        if (!response.body || typeof response.body.getReader !== 'function') {
            const buffer = await response.arrayBuffer();   // no stream in this browser
            if (buffer.byteLength > limit) throw tooLarge();
            return buffer;
        }
        const reader = response.body.getReader();
        const chunks = [];
        let total = 0;
        for (;;) {
            let step;
            try {
                step = await reader.read();
            } catch (e) {
                throw fail(ErrorCode.NETWORK_ERROR, 'The file could not be read: the network failed.', String(e && e.message || e));
            }
            if (step.done) break;
            const chunk = step.value;
            total += chunk.byteLength;
            if (total > limit) {
                reader.cancel().catch(() => { });
                throw tooLarge();
            }
            chunks.push(chunk);
        }
        const bytes = new Uint8Array(total);
        let offset = 0;
        for (const chunk of chunks) {
            bytes.set(chunk, offset);
            offset += chunk.byteLength;
        }
        return bytes.buffer;
    }

    function checkConfig(config) {
        const mode = config.embedMode === undefined ? EmbedMode.FULL_WINDOW : config.embedMode;
        if (!Object.prototype.hasOwnProperty.call(EmbedMode, mode)) {
            throw fail(ErrorCode.INVALID_ARGUMENT, `config.embedMode must be one of ${Object.keys(EmbedMode).join(', ')}.`);
        }
        return mode;
    }

    let lightBoxStyleAdded = false;

    function addLightBoxStyle(document) {
        if (lightBoxStyleAdded) return;
        lightBoxStyleAdded = true;
        const style = document.createElement('style');
        style.textContent = `
.pdfviewer-lightbox { position: fixed; inset: 0; z-index: 2147483000; display: flex; flex-direction: column; background: rgba(20, 20, 24, 0.72); }
.pdfviewer-lightbox-bar { display: flex; align-items: center; gap: 12px; padding: 8px 12px; color: #fff; font: 14px/1.4 system-ui, sans-serif; }
.pdfviewer-lightbox-title { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.pdfviewer-lightbox-bar button { font: inherit; color: inherit; background: rgba(255, 255, 255, 0.14); border: 1px solid rgba(255, 255, 255, 0.4); border-radius: 6px; padding: 5px 12px; cursor: pointer; }
.pdfviewer-lightbox-bar button:focus-visible { outline: 2px solid #fff; outline-offset: 2px; }
.pdfviewer-lightbox iframe { flex: 1; min-height: 0; }`;
        document.head.appendChild(style);
    }

    /** The search a viewer runs for search(): results arrive as the pages are read. */
    class SearchObject {
        constructor(view, searchId) {
            this._view = view;
            this._id = searchId;
            this._callbacks = [];
            this._last = null;
        }

        /** Calls back with { currentResult: { pageNumber, index }, totalResults, status: 'IN_PROGRESS' | 'COMPLETED' }. */
        onResultsUpdate(callback) {
            if (typeof callback !== 'function') return Promise.reject(fail(ErrorCode.INVALID_ARGUMENT, 'onResultsUpdate needs a function.'));
            this._callbacks.push(callback);
            if (this._last) safeCall(callback, this._last);
            return Promise.resolve(true);
        }

        next() { return this._view._call('searchNext', [this._id]); }
        previous() { return this._view._call('searchPrevious', [this._id]); }
        clear() { return this._view._call('searchClear', [this._id]); }

        _update(result) {
            this._last = result;
            for (const callback of this._callbacks) safeCall(callback, result);
        }
    }

    function safeCall(fn, arg) {
        try {
            fn(arg);
        } catch (e) {
            console.error('PdfViewer: a callback threw', e);
        }
    }

    /** A copy that postMessage can clone: plain JSON data only (an array or an object). */
    function jsonData(value, what) {
        try {
            return JSON.parse(JSON.stringify(value));
        } catch {
            throw fail(ErrorCode.INVALID_ARGUMENT, `${what} must be plain data.`);
        }
    }

    /**
     * The annotation manager of one document (viewer.getAnnotationManager()), modelled on the Adobe
     * PDF Embed API's. Annotations are JSON objects (see docs/sdk/index.md); the viewer checks
     * every one. Events reach registerEventListener handlers only, in this page.
     */
    class AnnotationManager {
        constructor(view, documentId) {
            this._view = view;
            this._document = documentId;
            this._listeners = [];
        }

        _call(method, args) { return this._view._call(method, args, this._document); }

        /** Adds annotations (an array, at most 1000); resolves with them as the viewer keeps them (ids made where missing). */
        addAnnotations(list) {
            if (!Array.isArray(list)) return Promise.reject(fail(ErrorCode.INVALID_ARGUMENT, 'addAnnotations needs an array of annotations.'));
            return this._call('addAnnotations', [jsonData(list, 'The annotations')]);
        }

        /** Resolves with the annotations, all or those of { annotationIds, pageRange: { startPage, endPage } }. */
        getAnnotations(filter) {
            return this._call('getAnnotations', [filter === undefined ? null : jsonData(filter, 'The filter')]);
        }

        /** Selects an annotation and scrolls to it. */
        selectAnnotation(id) { return this._call('selectAnnotation', [id]).then(list => (Array.isArray(list) ? list[0] : list)); }

        unselectAnnotation() { return this._call('unselectAnnotation'); }

        /** Deletes the annotations of the filter ({} deletes all); resolves with the deleted ones. */
        deleteAnnotations(filter) {
            if (!filter || typeof filter !== 'object') return Promise.reject(fail(ErrorCode.INVALID_ARGUMENT, 'deleteAnnotations needs a filter ({} deletes every annotation).'));
            return this._call('deleteAnnotations', [jsonData(filter, 'The filter')]);
        }

        /** Replaces the annotation with the same id; resolves with it. */
        updateAnnotation(annotation) {
            if (!annotation || typeof annotation !== 'object') return Promise.reject(fail(ErrorCode.INVALID_ARGUMENT, 'updateAnnotation needs an annotation.'));
            return this._call('updateAnnotation', [jsonData(annotation, 'The annotation')]).then(list => (Array.isArray(list) ? list[0] : list));
        }

        /** { showToolbar, showCommentsPanel, downloadWithAnnotations, printWithAnnotations, authorName }. */
        setConfig(config) {
            if (!config || typeof config !== 'object') return Promise.reject(fail(ErrorCode.INVALID_ARGUMENT, 'setConfig needs an object.'));
            return this._call('setAnnotationConfig', [jsonData(config, 'The config')]);
        }

        /** Resolves with the annotations as an XFDF document (a string). */
        exportXfdf() { return this._call('exportXfdf'); }

        /** Adds the annotations of an XFDF document; resolves with them. */
        importXfdf(xml) {
            if (typeof xml !== 'string') return Promise.reject(fail(ErrorCode.INVALID_ARGUMENT, 'importXfdf needs the XFDF document as a string.'));
            return this._call('importXfdf', [xml]);
        }

        /** registerEventListener(handler, { listenOn: [...] }): handler({ type, data }), data the annotation. */
        registerEventListener(handler, options) {
            if (typeof handler !== 'function') throw fail(ErrorCode.INVALID_ARGUMENT, 'The handler must be a function.');
            const listenOn = options && Array.isArray(options.listenOn) && options.listenOn.length > 0 ? new Set(options.listenOn) : null;
            this._listeners.push({ handler, listenOn });
        }

        _emit(event) {
            for (const { handler, listenOn } of this._listeners) {
                if (!listenOn || listenOn.has(event.type)) safeCall(handler, event);
            }
        }
    }

    /** The SAVE_API options as the viewer takes them. */
    function saveOptions(options) {
        const o = options || {};
        if (typeof o !== 'object') throw fail(ErrorCode.INVALID_ARGUMENT, 'The SAVE_API options must be an object.');
        const seconds = o.autoSaveFrequency === undefined || o.autoSaveFrequency === null ? 0 : o.autoSaveFrequency;
        if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0 || seconds > MAX_AUTOSAVE_SECONDS) {
            throw fail(ErrorCode.INVALID_ARGUMENT, `autoSaveFrequency must be a number of seconds from 0 to ${MAX_AUTOSAVE_SECONDS}.`);
        }
        for (const key of ['enableFocusPolling', 'showSaveButton']) {
            if (o[key] !== undefined && typeof o[key] !== 'boolean') throw fail(ErrorCode.INVALID_ARGUMENT, `${key} must be true or false.`);
        }
        return { enabled: true, autoSaveFrequency: seconds, enableFocusPolling: o.enableFocusPolling === true, showSaveButton: o.showSaveButton !== false };
    }

    let sameOriginWarned = false;

    /**
     * The sandbox of the frame keeps the viewer apart from the page only when their origins
     * differ. On the same origin, 'warn' (the default) writes one console warning per page and
     * 'require' refuses the View.
     */
    function checkIsolation(viewerOrigin, isolation) {
        if (viewerOrigin !== global.location.origin) return;
        if (isolation === 'require') {
            throw fail(ErrorCode.INVALID_ARGUMENT,
                `The viewer (${viewerOrigin}) has the origin of this page, so the iframe sandbox does not isolate it, and isolation is 'require'. Serve the viewer from another origin. See ${ISOLATION_DOCS}`);
        }
        if (sameOriginWarned) return;
        sameOriginWarned = true;
        console.warn(`PdfViewer: the viewer (${viewerOrigin}) has the same origin as this page. The iframe sandbox then does not isolate it: a fault in the viewer could reach this page. For production, serve the viewer from another origin (for example a subdomain). See ${ISOLATION_DOCS}`);
    }

    /** One viewer in one element of the page: new PdfViewer.View({ divId, locale, baseUrl, isolation }). */
    class View {
        constructor(options) {
            const { divId, locale = 'en-US', baseUrl, isolation = 'warn' } = options || {};
            if (isolation !== 'warn' && isolation !== 'require') throw fail(ErrorCode.INVALID_ARGUMENT, "isolation must be 'warn' or 'require'.");
            const document = global.document;
            const container = typeof divId === 'string' ? document.getElementById(divId) : null;
            if (!container) throw fail(ErrorCode.INVALID_ARGUMENT, `No element has the id "${divId}".`);
            if (global.location.origin === 'null' || !/^https?:$/.test(global.location.protocol)) {
                throw fail(ErrorCode.INVALID_ARGUMENT, 'The page must be served over http or https (not opened from a file) to embed the viewer.');
            }
            this._container = container;
            this._locale = typeof locale === 'string' ? locale : 'en-US';
            this._base = viewerBase(baseUrl);
            this._viewerOrigin = this._base.origin;
            checkIsolation(this._viewerOrigin, isolation);
            this._listeners = [];
            this._managers = [];
            this._saveHandler = null;
            this._saveOptions = null;
            // SAVE_API calls run one after another, so an older slow save cannot finish after a newer one.
            this._saveChain = Promise.resolve();
            this._pending = new Map();
            this._searches = new Map();
            this._nextId = 1;
            this._frame = null;
            this._frameMode = null;
            this._frameReady = null;
            this._lightBox = null;
            this._exitLabel = 'Close';
            this._document = 0;
            this._onMessage = this._onMessage.bind(this);
            global.addEventListener('message', this._onMessage);
        }

        /**
         * registerCallback(PdfViewer.View.Enum.CallbackType.EVENT_LISTENER, handler, { listenOn: [...] }).
         * The handler gets { type, data }. Events stay in this page: nothing is sent anywhere.
         */
        registerCallback(type, handler, options) {
            if (type !== CallbackType.EVENT_LISTENER && type !== CallbackType.SAVE_API) {
                throw fail(ErrorCode.INVALID_ARGUMENT, `Unknown callback type "${type}". Use PdfViewer.View.Enum.CallbackType.EVENT_LISTENER or SAVE_API.`);
            }
            if (typeof handler !== 'function') throw fail(ErrorCode.INVALID_ARGUMENT, 'The handler must be a function.');
            if (type === CallbackType.SAVE_API) {
                // handler(metaData, content, options) resolves with { code: 'SUCCESS' | 'FAIL', data: { metaData } }.
                this._saveOptions = saveOptions(options);
                this._saveHandler = handler;
                if (this._frame && this._document !== 0) this._call('setSaveOptions', [this._saveOptions]).catch(() => { });
                return;
            }
            const listenOn = options && Array.isArray(options.listenOn) && options.listenOn.length > 0 ? new Set(options.listenOn) : null;
            this._listeners.push({ handler, listenOn });
        }

        /**
         * Shows a PDF. Resolves with a viewer ({ getAPIs() }) when the document is open, or rejects
         * with a PdfViewerError (its code says why).
         */
        previewFile(file, config) {
            return this._preview(file, config).catch(error => {
                const e = error instanceof PdfViewerError ? error : fail(ErrorCode.VIEWER_LOAD_FAILED, String(error && error.message || error));
                this._emit({ type: Events.PREVIEW_ERROR, data: { code: e.code, message: e.message } });
                throw e;
            });
        }

        async _preview(file, config) {
            if (!file || typeof file !== 'object') throw fail(ErrorCode.INVALID_ARGUMENT, 'previewFile needs { content, metaData }.');
            const meta = plainData(file.metaData, 'file.metaData');
            const fileName = typeof meta.fileName === 'string' && meta.fileName.trim() ? meta.fileName.trim() : 'document.pdf';
            const settings = plainData(config, 'config');
            const mode = checkConfig(settings);

            this._exitLabel = settings.exitPDFViewerType === 'RETURN' ? 'Return' : 'Close';
            const ready = this._ensureFrame(mode, fileName);
            const bytes = readContent(file.content);
            // Both run at once: the viewer boots while the file downloads.
            const [buffer] = await Promise.all([bytes, ready]);
            if (buffer.byteLength > MAX_FILE_BYTES) throw tooLarge();

            const requestId = this._nextId++;
            const opened = this._request(requestId);
            this._post({
                type: 'open', requestId, fileName,
                fileId: typeof meta.id === 'string' ? meta.id : null,
                readOnly: meta.hasReadOnlyAccess === true,
                locale: this._locale,
                config: settings,
                save: this._saveOptions,
                data: buffer,
            }, [buffer]);
            const result = await opened;
            this._document = requestId;
            this._searches.clear();
            this._managers = this._managers.filter(m => m._document === requestId);
            if (result && result.focus && this._frame) this._frame.focus();
            return this._viewerObject(requestId);
        }

        /** Removes the viewer from the page. The View can show a file again afterwards. */
        destroy() {
            this._closeFrame();
        }

        _viewerObject(documentId) {
            const call = (method, args) => this._call(method, args, documentId);
            const zoom = Object.freeze({
                zoomIn: () => call('zoomIn'),
                zoomOut: () => call('zoomOut'),
                setZoomLevel: level => call('setZoomLevel', [level]),
                getZoomLimits: () => call('getZoomLimits'),
            });
            const apis = Object.freeze({
                getPDFMetadata: () => call('getPDFMetadata'),
                getCurrentPage: () => call('getCurrentPage'),
                gotoLocation: (pageNumber, x, y) => call('gotoLocation', [pageNumber, x === undefined ? 0 : x, y === undefined ? 0 : y]),
                getSelectedContent: () => call('getSelectedContent'),
                getZoomAPIs: () => zoom,
                search: term => call('search', [term]).then(r => {
                    const search = new SearchObject(this, r.searchId);
                    this._searches.set(r.searchId, search);
                    return search;
                }),
            });
            let manager = null;
            const getAnnotationManager = () => call('getAnnotationManager').then(() => {
                if (!manager) {
                    manager = new AnnotationManager(this, documentId);
                    this._managers.push(manager);
                }
                return manager;
            });
            return Object.freeze({ getAPIs: () => Promise.resolve(apis), getAnnotationManager });
        }

        _ensureFrame(mode, fileName) {
            if (this._frame && this._frameMode === mode) {
                this._frame.title = `PDF viewer: ${fileName}`;
                if (this._lightBox) {
                    this._lightBox.title.textContent = fileName;
                    this._lightBox.button.textContent = this._exitLabel;
                }
                return this._frameReady;
            }
            this._closeFrame();
            const document = global.document;
            const frame = document.createElement('iframe');
            const src = new URL('index.html', this._base);
            src.searchParams.set('embed', String(PROTOCOL_VERSION));
            src.searchParams.set('mode', mode);
            src.searchParams.set('origin', global.location.origin);
            frame.src = src.href;
            frame.title = `PDF viewer: ${fileName}`;
            // As strict as the viewer allows: scripts (the engine), its own origin (its files and
            // fonts), downloads (Download), modals (Print). No top navigation, popups or forms.
            frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-downloads allow-modals');
            frame.setAttribute('allow', 'fullscreen');
            frame.setAttribute('referrerpolicy', 'no-referrer');
            frame.style.border = '0';
            frame.style.display = 'block';
            frame.style.width = '100%';
            frame.style.height = mode === EmbedMode.IN_LINE ? '480px' : '100%';
            if (mode === EmbedMode.LIGHT_BOX) {
                this._lightBox = this._openLightBox(frame, fileName);
            } else {
                this._container.replaceChildren(frame);
            }
            this._frame = frame;
            this._frameMode = mode;
            this._frameReady = new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(fail(ErrorCode.VIEWER_LOAD_FAILED, 'The viewer did not start. Check baseUrl and that the viewer files are served.')), BOOT_TIMEOUT_MS);
                this._resolveReady = () => { clearTimeout(timer); resolve(); };
                this._rejectReady = e => { clearTimeout(timer); reject(e); };
            });
            return this._frameReady;
        }

        _openLightBox(frame, fileName) {
            const document = global.document;
            addLightBoxStyle(document);
            const box = document.createElement('div');
            box.className = 'pdfviewer-lightbox';
            box.setAttribute('role', 'dialog');
            box.setAttribute('aria-modal', 'true');
            box.setAttribute('aria-label', fileName);
            const bar = document.createElement('div');
            bar.className = 'pdfviewer-lightbox-bar';
            const title = document.createElement('span');
            title.className = 'pdfviewer-lightbox-title';
            title.textContent = fileName;
            const button = document.createElement('button');
            button.type = 'button';
            button.textContent = this._exitLabel || 'Close';
            button.addEventListener('click', () => this._closeFrame());
            bar.append(title, button);
            box.append(bar, frame);
            const returnFocus = document.activeElement;
            const overflow = document.documentElement.style.overflow;
            document.documentElement.style.overflow = 'hidden';
            const onKey = e => { if (e.key === 'Escape') this._closeFrame(); };
            document.addEventListener('keydown', onKey);
            document.body.appendChild(box);
            return {
                box, title, button,
                close: () => {
                    document.removeEventListener('keydown', onKey);
                    document.documentElement.style.overflow = overflow;
                    box.remove();
                    if (returnFocus && typeof returnFocus.focus === 'function') returnFocus.focus();
                },
            };
        }

        _closeFrame() {
            if (!this._frame) return;
            if (this._lightBox) this._lightBox.close();
            else this._frame.remove();
            this._lightBox = null;
            this._frame = null;
            this._frameMode = null;
            this._document = 0;
            if (this._rejectReady) this._rejectReady(fail(ErrorCode.CLOSED, 'The viewer was closed.'));
            for (const pending of this._pending.values()) pending.reject(fail(ErrorCode.CLOSED, 'The viewer was closed.'));
            this._pending.clear();
            this._searches.clear();
            // A handler still running for the closed frame does not hold up the next one.
            this._saveChain = Promise.resolve();
        }

        _post(message, transfer) {
            if (!this._frame || !this._frame.contentWindow) throw fail(ErrorCode.CLOSED, 'The viewer was closed.');
            this._frame.contentWindow.postMessage({ protocol: PROTOCOL, version: PROTOCOL_VERSION, ...message }, this._viewerOrigin, transfer || []);
        }

        _request(requestId) {
            return new Promise((resolve, reject) => this._pending.set(requestId, { resolve, reject }));
        }

        _call(method, args, documentId) {
            if (!this._frame || this._document === 0) return Promise.reject(fail(ErrorCode.NO_DOCUMENT, 'No document is open.'));
            if (documentId !== undefined && documentId !== this._document) {
                return Promise.reject(fail(ErrorCode.NO_DOCUMENT, 'This viewer object belongs to a document that is no longer open.'));
            }
            const requestId = this._nextId++;
            const result = this._request(requestId);
            this._post({ type: 'call', requestId, document: this._document, method, args: args || [] });
            return result;
        }

        _emit(event) {
            for (const { handler, listenOn } of this._listeners) {
                if (!listenOn || listenOn.has(event.type)) safeCall(handler, event);
            }
        }

        /** The reader saved: the save waits for the ones before it to finish (the chain never rejects). */
        _queueSave(m) {
            const documentId = this._document, frame = this._frame;
            this._saveChain = this._saveChain.then(() => this._save(m, documentId, frame)).catch(() => { });
        }

        /** The SAVE_API handler gets the file; its answer goes back to the viewer that asked, if it is still open. */
        async _save(m, documentId, frame) {
            // Another document (or no frame) by the time this save's turn came: it is not handed over.
            if (this._frame !== frame || this._document !== documentId) return;
            const metaData = { fileName: typeof m.fileName === 'string' ? m.fileName : 'document.pdf', id: typeof m.fileId === 'string' ? m.fileId : undefined };
            let code = ApiResponseCode.FAIL, fileName = null;
            try {
                const result = this._saveHandler ? await this._saveHandler(metaData, m.data, { reason: typeof m.reason === 'string' ? m.reason : 'button' }) : null;
                if (result && result.code === ApiResponseCode.SUCCESS) code = ApiResponseCode.SUCCESS;
                const name = result && result.data && result.data.metaData && result.data.metaData.fileName;
                if (typeof name === 'string' && name.length > 0 && name.length <= 1024) fileName = name;
            } catch (e) {
                console.error('PdfViewer: the SAVE_API callback threw', e);
            }
            if (this._frame === frame && this._document === documentId) this._call('saveResult', [m.saveId, code, fileName]).catch(() => { });
        }

        /** Only the viewer's own frame, from the viewer's origin, with this protocol, is heard. */
        _onMessage(e) {
            const frame = this._frame;
            if (!frame || e.source !== frame.contentWindow || e.origin !== this._viewerOrigin) return;
            const m = e.data;
            if (!m || typeof m !== 'object' || m.protocol !== PROTOCOL || m.version !== PROTOCOL_VERSION || typeof m.type !== 'string') return;
            switch (m.type) {
                case 'ready':
                    if (this._resolveReady) this._resolveReady();
                    break;
                case 'response': {
                    const pending = this._pending.get(m.requestId);
                    if (!pending) break;
                    this._pending.delete(m.requestId);
                    if (m.ok) pending.resolve(m.value === undefined ? null : m.value);
                    else pending.reject(fail(m.error && typeof m.error.code === 'string' ? m.error.code : ErrorCode.VIEWER_LOAD_FAILED,
                        m.error && typeof m.error.message === 'string' ? m.error.message : 'The viewer failed.'));
                    break;
                }
                case 'event':
                    if (m.event && typeof m.event.type === 'string' && Object.prototype.hasOwnProperty.call(Events, m.event.type)) {
                        this._emit({ type: m.event.type, data: m.event.data && typeof m.event.data === 'object' ? m.event.data : {} });
                    } else if (m.event && typeof m.event.type === 'string' && Object.prototype.hasOwnProperty.call(AnnotationEvents, m.event.type)) {
                        const event = { type: m.event.type, data: m.event.data && typeof m.event.data === 'object' ? m.event.data : {} };
                        for (const manager of this._managers) if (manager._document === this._document) manager._emit(event);
                    }
                    break;
                case 'save':
                    if (Number.isInteger(m.saveId) && m.saveId > 0 && m.data instanceof ArrayBuffer) this._queueSave(m);
                    break;
                case 'search': {
                    const search = this._searches.get(m.searchId);
                    if (search && m.result && typeof m.result === 'object') search._update(m.result);
                    break;
                }
                case 'size':
                    if (this._frameMode === EmbedMode.IN_LINE && Number.isFinite(m.height) && m.height > 0) {
                        frame.style.height = `${Math.min(Math.ceil(m.height), 10000000)}px`;
                    }
                    break;
                case 'scroll':
                    if (this._frameMode === EmbedMode.IN_LINE && Number.isFinite(m.top)) {
                        const top = frame.getBoundingClientRect().top + global.scrollY + Math.max(0, m.top);
                        global.scrollTo({ top, behavior: 'auto' });
                    }
                    break;
                case 'close':
                    if (this._frameMode === EmbedMode.LIGHT_BOX) this._closeFrame();
                    break;
            }
        }
    }

    View.Enum = Object.freeze({ CallbackType, Events, AnnotationEvents, ApiResponseCode, EmbedMode, ViewMode, ErrorCode });

    const PdfViewer = {
        View,
        PdfViewerError,
        version: SDK_VERSION,
        protocolVersion: PROTOCOL_VERSION,
    };
    // For pdfviewer-embed.mjs: a module knows its own URL, a classic script reads currentScript.
    Object.defineProperty(PdfViewer, '_setScriptUrl', {
        value: url => { if (!scriptBase) scriptBase = new URL('.', url).href; },
    });
    global.PdfViewer = Object.freeze(PdfViewer);
})(globalThis);
