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
    const SDK_VERSION = '0.4.0';
    const MAX_FILE_BYTES = 512 * 1024 * 1024;
    // Range loading (docs/sdk, "Large files"): the viewer asks for at most this many bytes at a
    // time, with at most this many requests waiting; files smaller than RANGE_MIN_BYTES are read whole.
    const MAX_RANGE_BYTES = 4 * 1024 * 1024;
    const MAX_RANGE_REQUESTS = 6;
    const RANGE_MIN_BYTES = 1024 * 1024;
    const RANGE_TAIL_BYTES = 1024 * 1024;
    const BOOT_TIMEOUT_MS = 60000;
    // A host fetch that gets no answer in this time fails, as the viewer's own wait for a range does.
    const FETCH_TIMEOUT_MS = 60000;
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
        APP_RENDERING_DONE: 'APP_RENDERING_DONE',
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

    /**
     * What the viewer gets: { buffer } (the whole file, an ArrayBuffer this SDK owns, transferred
     * to the viewer), or { range } (a RangeSource: the viewer asks for byte ranges as pages need
     * them). A range source is made for a location whose server answers range requests (unless
     * rangeRequests is false) and for Adobe-style linearizationInfo.
     */
    async function prepareContent(content, signal) {
        if (!content || typeof content !== 'object') throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content must be an object with location, promise or arrayBuffer.');
        const given = ['location', 'promise', 'arrayBuffer'].filter(k => content[k] !== undefined && content[k] !== null);
        if (given.length !== 1) throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content must have exactly one of location, promise and arrayBuffer.');
        if (given[0] === 'location') {
            const location = content.location;
            const init = requestInit(location);
            if (location.rangeRequests !== undefined && typeof location.rangeRequests !== 'boolean') {
                throw fail(ErrorCode.INVALID_ARGUMENT, 'file.content.location.rangeRequests must be true or false.');
            }
            if (location.rangeRequests !== false) {
                const ranged = await UrlRangeSource.open(init, signal);
                if (ranged.source) return { range: ranged.source };
                if (ranged.buffer) return { buffer: ranged.buffer };
            }
            return { buffer: await fetchWhole(init, signal) };
        }
        if (given[0] === 'promise' && content.linearizationInfo !== undefined && content.linearizationInfo !== null) {
            try {
                return { range: await LinearizedRangeSource.open(content.linearizationInfo, content.promise) };
            } catch (e) {
                // linearizationInfo failed: the whole file from the promise, as without it.
                if (e instanceof PdfViewerError && e.code === ErrorCode.TOO_LARGE) throw e;
                console.warn('PdfViewer: linearizationInfo failed, reading content.promise whole.', e);
            }
        }
        const value = given[0] === 'promise' ? await untilAborted(Promise.resolve(content.promise), signal) : content.arrayBuffer;
        return { buffer: await ownBuffer(value) };
    }

    /** The error of an aborted signal: its reason when it is ours (a timeout, a close), else CLOSED. */
    function abortError(signal) {
        return signal && signal.reason instanceof PdfViewerError ? signal.reason : fail(ErrorCode.CLOSED, 'The request was stopped: the viewer was closed or opened another file.');
    }

    /** `promise`, but rejected as soon as `signal` aborts (for sources that cannot be stopped themselves). */
    function untilAborted(promise, signal) {
        if (!signal) return promise;
        if (signal.aborted) return Promise.reject(abortError(signal));
        return new Promise((resolve, reject) => {
            const onAbort = () => reject(abortError(signal));
            signal.addEventListener('abort', onAbort, { once: true });
            promise.then(
                value => { signal.removeEventListener('abort', onAbort); resolve(value); },
                error => { signal.removeEventListener('abort', onAbort); reject(error); });
        });
    }

    /**
     * An AbortController that aborts when `parent` aborts and, until clearTimer(), after `ms`
     * (a NETWORK_ERROR that says it timed out). dispose() ends both links.
     */
    function timedController(ms, parent) {
        const controller = new AbortController();
        const onParent = () => controller.abort(parent.reason instanceof PdfViewerError ? parent.reason : abortError(parent));
        if (parent) {
            if (parent.aborted) onParent();
            else parent.addEventListener('abort', onParent, { once: true });
        }
        let timer = ms > 0 ? setTimeout(() => controller.abort(fail(ErrorCode.NETWORK_ERROR, `The server did not answer in ${Math.round(ms / 1000)} s.`)), ms) : null;
        const clearTimer = () => { if (timer !== null) clearTimeout(timer); timer = null; };
        return {
            signal: controller.signal,
            abort: reason => controller.abort(reason),
            clearTimer,
            dispose: () => { clearTimer(); if (parent) parent.removeEventListener('abort', onParent); },
        };
    }

    /** The whole file of a range source, for a viewer that does not read ranges. */
    async function readWhole(prepared, signal) {
        return prepared.buffer || prepared.range.readWhole(signal);
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
     * The URL and fetch options of a location: the file is fetched in this page, so that this
     * page's cookies, the headers given and the server's CORS rules apply, exactly as for any
     * other fetch of this page.
     */
    function requestInit(location) {
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
        return { href: url.href, headers, credentials: location.credentials === 'include' ? 'include' : 'same-origin' };
    }

    /**
     * One fetch of the location (with a Range header when `range` is given); network failures and
     * HTTP errors as PdfViewerErrors. It stops when `signal` aborts, and fails when the server sends
     * no answer (its headers) in FETCH_TIMEOUT_MS.
     */
    async function fetchFrom(init, range, signal) {
        const headers = new Headers(init.headers);
        if (range) headers.set('Range', range);
        const timed = timedController(FETCH_TIMEOUT_MS, signal);
        let response;
        try {
            // The body is read under `signal` alone: a long download of a whole file is not a timeout.
            response = await untilAborted(fetch(init.href, { headers, credentials: init.credentials, signal: timed.signal }), timed.signal);
        } catch (e) {
            timed.dispose();
            if (timed.signal.aborted) throw abortError(timed.signal);
            throw fail(ErrorCode.NETWORK_ERROR,
                'The file could not be fetched: the network failed, or the server does not let this page read it (CORS).', String(e && e.message || e));
        }
        timed.clearTimer();
        if (response.status === 404 || response.status === 410) throw fail(ErrorCode.FILE_NOT_FOUND, `The file was not found (HTTP ${response.status}).`, { status: response.status });
        if (!response.ok) throw fail(ErrorCode.HTTP_ERROR, `The server answered HTTP ${response.status}.`, { status: response.status });
        return response;
    }

    /** The whole file of a location, read with the size cap. */
    async function fetchWhole(init, signal) {
        return readResponse(await fetchFrom(init, null, signal), signal);
    }

    /** A full response's body, refused at once when its readable Content-Length is too large. */
    function readResponse(response, signal) {
        // A readable Content-Length that is too large fails at once. A missing or bad one is
        // unknown, not 0: readCapped counts the bytes as they arrive in all cases.
        const header = response.headers.get('Content-Length');
        const length = header !== null && /^\s*\d+\s*$/.test(header) ? Number(header) : NaN;
        if (Number.isFinite(length) && length > MAX_FILE_BYTES) {
            if (response.body) response.body.cancel().catch(() => { });
            throw tooLarge();
        }
        return readCapped(response, MAX_FILE_BYTES, signal);
    }

    /** "bytes 0-0/12345" → { start, end (inclusive), total }, or null. */
    function parseContentRange(value) {
        const m = typeof value === 'string' ? /^\s*bytes\s+(\d+)-(\d+)\/(\d+)\s*$/i.exec(value) : null;
        if (!m) return null;
        const r = { start: Number(m[1]), end: Number(m[2]), total: Number(m[3]) };
        return Number.isSafeInteger(r.total) && r.start <= r.end && r.end < r.total ? r : null;
    }

    /** Checks a range the viewer asked for: whole numbers, inside the file, not empty, not too long. */
    function checkRange(start, end, length) {
        return Number.isSafeInteger(start) && Number.isSafeInteger(end) && start >= 0 && end > start && end <= length && end - start <= MAX_RANGE_BYTES;
    }

    /**
     * A file on a web server that answers HTTP range requests. Only the URL given to previewFile
     * is ever fetched: the viewer's requests name bytes, never a URL. open() asks for one byte
     * (bytes=0-0, a CORS-safelisted Range) to learn the length (Content-Range, which a
     * cross-origin server must expose) and whether ranges work; then the end of the file, which
     * holds the cross-reference data, goes to the viewer with the open message.
     */
    class UrlRangeSource {
        static async open(init, signal) {
            const probe = await fetchFrom(init, 'bytes=0-0', signal);
            if (probe.status !== 206) {
                // No range support: this answer is the whole file already.
                return { buffer: await readResponse(probe, signal) };
            }
            const range = parseContentRange(probe.headers.get('Content-Range'));
            if (probe.body) probe.body.cancel().catch(() => { });
            if (!range || range.start !== 0) {
                // Content-Range unreadable (a cross-origin server that does not expose it): the file is read whole.
                return { buffer: await fetchWhole(init, signal) };
            }
            if (range.total > MAX_FILE_BYTES) throw tooLarge();
            if (range.total <= RANGE_MIN_BYTES) return { buffer: await fetchWhole(init, signal) };
            const source = new UrlRangeSource(init, range.total);
            const tail = Math.min(RANGE_TAIL_BYTES, range.total);
            source.initialOffset = range.total - tail;
            source.initial = await source.read(source.initialOffset, range.total, signal);
            return { source };
        }

        constructor(init, length) {
            this._init = init;
            this.length = length;
            this.initial = null;
            this.initialOffset = 0;
            this.bytesFetched = 0;
        }

        /**
         * Bytes [start, end) of the file. The answer must be exactly them (HTTP 206 with a matching
         * Content-Range), within FETCH_TIMEOUT_MS for the whole range; `signal` stops the read.
         */
        async read(start, end, signal) {
            if (!checkRange(start, end, this.length)) {
                throw fail(ErrorCode.INVALID_ARGUMENT, 'The range is not inside the file.');
            }
            // A range is at most 4 MB: its body has the same time limit as its headers.
            const timed = timedController(FETCH_TIMEOUT_MS, signal);
            try {
                const response = await fetchFrom(this._init, `bytes=${start}-${end - 1}`, timed.signal);
                if (response.status !== 206) {
                    if (response.body) response.body.cancel().catch(() => { });
                    throw fail(ErrorCode.HTTP_ERROR, `The server did not answer a range request (HTTP ${response.status}).`, { status: response.status });
                }
                const range = parseContentRange(response.headers.get('Content-Range'));
                if (range && (range.start !== start || range.end !== end - 1 || range.total !== this.length)) {
                    if (response.body) response.body.cancel().catch(() => { });
                    throw fail(ErrorCode.HTTP_ERROR, 'The server answered another range than the one asked for, or the file changed.');
                }
                const buffer = await readCapped(response, end - start, timed.signal);
                if (buffer.byteLength !== end - start) throw fail(ErrorCode.HTTP_ERROR, 'The server sent fewer bytes than the range asked for.');
                this.bytesFetched += buffer.byteLength;
                return buffer;
            } finally {
                timed.dispose();
            }
        }

        /** The whole file, for a viewer that does not read ranges. */
        readWhole(signal) {
            return fetchWhole(this._init, signal);
        }
    }

    /**
     * Adobe-style linearizationInfo: { getInfo(), getInitialBuffer(), getFileBufferRanges(ranges) }.
     * getInfo() resolves with { fileSize }; getInitialBuffer() (optional) with the start of the
     * file (an ArrayBuffer or typed array) or { buffer, start }; getFileBufferRanges([{ start, end }])
     * (end inclusive, as in an HTTP Range header) with { bufferList: [ArrayBuffer] } or an array of
     * buffers. content.promise still gives the whole file when the viewer needs all of it at once.
     */
    class LinearizedRangeSource {
        static async open(info, whole) {
            if (!info || typeof info !== 'object' || typeof info.getInfo !== 'function' || typeof info.getFileBufferRanges !== 'function') {
                throw fail(ErrorCode.INVALID_ARGUMENT, 'linearizationInfo needs getInfo() and getFileBufferRanges(ranges).');
            }
            const meta = await info.getInfo();
            const length = meta && [meta.fileSize, meta.size, meta.length].find(v => v !== undefined);
            if (!Number.isSafeInteger(length) || length < 1) throw fail(ErrorCode.INVALID_ARGUMENT, 'linearizationInfo.getInfo() must resolve with { fileSize } (a whole number of bytes).');
            if (length > MAX_FILE_BYTES) throw tooLarge();
            const source = new LinearizedRangeSource(info, length, whole);
            if (typeof info.getInitialBuffer === 'function') {
                const initial = await info.getInitialBuffer();
                const start = initial && typeof initial === 'object' && !(initial instanceof ArrayBuffer) && !ArrayBuffer.isView(initial)
                    ? (initial.start ?? initial.offset ?? 0) : 0;
                const value = initial && initial.buffer instanceof ArrayBuffer && !ArrayBuffer.isView(initial) ? initial.buffer : initial;
                // Only what fits the file is kept, and at most 16 MB of it is copied.
                const size = sizeOf(value);
                if (size !== null && Number.isSafeInteger(start) && start >= 0 && start + size <= length) {
                    source.initial = await bufferOf(value, MAX_RANGE_BYTES * 4);
                    source.initialOffset = start;
                }
            }
            return source;
        }

        constructor(info, length, whole) {
            this._info = info;
            this._whole = whole;
            this.length = length;
            this.initial = null;
            this.initialOffset = 0;
            this.bytesFetched = 0;
        }

        /** Bytes [start, end): the host's getFileBufferRanges cannot be stopped, but `signal` ends the wait for it. */
        async read(start, end, signal) {
            if (!checkRange(start, end, this.length)) throw fail(ErrorCode.INVALID_ARGUMENT, 'The range is not inside the file.');
            const result = await untilAborted(Promise.resolve(this._info.getFileBufferRanges([{ start, end: end - 1 }])), signal);
            const list = Array.isArray(result) ? result : result && (result.bufferList || result.buffers);
            const value = Array.isArray(list) ? list[0] : null;
            // The size is checked before anything is copied.
            if (sizeOf(value) !== end - start) {
                throw fail(ErrorCode.INVALID_ARGUMENT, `getFileBufferRanges must resolve with ${end - start} bytes for the range ${start}-${end - 1}.`);
            }
            const buffer = await bufferOf(value, end - start);
            this.bytesFetched += buffer.byteLength;
            return buffer;
        }

        async readWhole(signal) {
            if (!this._whole) throw fail(ErrorCode.INVALID_ARGUMENT, 'content.promise is required with linearizationInfo.');
            return ownBuffer(await untilAborted(Promise.resolve(this._whole), signal));
        }
    }

    /** The byte length of an ArrayBuffer, a typed array or a Blob; null for anything else. */
    function sizeOf(value) {
        if (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) return value.byteLength;
        if (typeof Blob !== 'undefined' && value instanceof Blob) return value.size;
        return null;
    }

    /**
     * An ArrayBuffer of its own (copied) of at most the first `limit` bytes of an ArrayBuffer, a
     * typed array or a Blob; null for anything else. The size is known before the copy, so a huge
     * value is never copied whole.
     */
    async function bufferOf(value, limit) {
        const size = sizeOf(value);
        if (size === null) return null;
        const n = Math.min(size, limit);
        if (value instanceof ArrayBuffer) return value.slice(0, n);
        if (ArrayBuffer.isView(value)) return value.buffer.slice(value.byteOffset, value.byteOffset + n);
        return (n < size ? value.slice(0, n) : value).arrayBuffer();
    }

    /**
     * The host's side of the ranges of one file: it checks each fetchRange message of the viewer,
     * reads the bytes from the source given to previewFile (never another), and keeps at most
     * MAX_RANGE_REQUESTS waiting. Each read has its own AbortController and the FETCH_TIMEOUT_MS
     * limit; close() aborts them all. A slot is free again only when its read ends or is aborted.
     */
    class RangeServer {
        constructor(file, source, timeoutMs = FETCH_TIMEOUT_MS) {
            this.file = file;
            this.source = source;
            this._timeoutMs = timeoutMs;
            this._inflight = new Map();
            this.closed = false;
        }

        /** The reads waiting now. */
        get inflight() {
            return this._inflight.size;
        }

        /**
         * Serves one fetchRange message. Resolves with { ok: true, data }, { ok: false, error },
         * or null when the message gets no answer (a bad id, or a request already waiting).
         */
        async serve(m) {
            const refuse = (code, message) => ({ ok: false, error: { code, message } });
            if (!m || !Number.isSafeInteger(m.requestId) || m.requestId < 1 || !Number.isSafeInteger(m.file)) return null;
            if (this.closed || m.file !== this.file) return refuse(ErrorCode.NO_DOCUMENT, 'This file is no longer open.');
            if (this._inflight.has(m.requestId)) return null;
            if (!checkRange(m.start, m.end, this.source.length)) return refuse(ErrorCode.INVALID_ARGUMENT, 'The range is not a valid range of the file.');
            if (this._inflight.size >= MAX_RANGE_REQUESTS) return refuse(ErrorCode.INVALID_ARGUMENT, `At most ${MAX_RANGE_REQUESTS} range requests may wait at once.`);
            const timed = timedController(this._timeoutMs);
            this._inflight.set(m.requestId, timed);
            try {
                const data = await untilAborted(Promise.resolve().then(() => this.source.read(m.start, m.end, timed.signal)), timed.signal);
                return { ok: true, data };
            } catch (e) {
                const error = e instanceof PdfViewerError ? e : fail(ErrorCode.NETWORK_ERROR, String(e && e.message || e));
                return refuse(error.code, error.message);
            } finally {
                timed.dispose();
                this._inflight.delete(m.requestId);
            }
        }

        /** The file is closed or replaced: every read waiting is aborted, and new ones are refused. */
        close() {
            this.closed = true;
            const reason = fail(ErrorCode.CLOSED, 'The viewer was closed or opened another file.');
            for (const timed of this._inflight.values()) timed.abort(reason);
        }
    }

    /**
     * Reads a response body, but stops (cancels the stream) and rejects with TOO_LARGE as soon as
     * more than `limit` bytes have arrived. Content-Length can be missing or wrong (compressed or
     * chunked responses), so only the bytes counted here are trusted.
     */
    async function readCapped(response, limit, signal) {
        if (!response.body || typeof response.body.getReader !== 'function') {
            const buffer = await untilAborted(response.arrayBuffer(), signal);   // no stream in this browser
            if (buffer.byteLength > limit) throw tooLarge();
            return buffer;
        }
        const reader = response.body.getReader();
        const chunks = [];
        let total = 0;
        for (;;) {
            let step;
            try {
                step = await untilAborted(reader.read(), signal);
            } catch (e) {
                if (signal && signal.aborted) {
                    reader.cancel().catch(() => { });
                    throw abortError(signal);
                }
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

    /** One viewer in one element of the page: new PdfViewer.View({ divId, locale, baseUrl, isolation, renderInWorker }). */
    class View {
        constructor(options) {
            const { divId, locale = 'en-US', baseUrl, isolation = 'warn', renderInWorker = false } = options || {};
            if (isolation !== 'warn' && isolation !== 'require') throw fail(ErrorCode.INVALID_ARGUMENT, "isolation must be 'warn' or 'require'.");
            if (typeof renderInWorker !== 'boolean') throw fail(ErrorCode.INVALID_ARGUMENT, 'renderInWorker must be true or false.');
            const document = global.document;
            const container = typeof divId === 'string' ? document.getElementById(divId) : null;
            if (!container) throw fail(ErrorCode.INVALID_ARGUMENT, `No element has the id "${divId}".`);
            if (global.location.origin === 'null' || !/^https?:$/.test(global.location.protocol)) {
                throw fail(ErrorCode.INVALID_ARGUMENT, 'The page must be served over http or https (not opened from a file) to embed the viewer.');
            }
            this._container = container;
            this._locale = typeof locale === 'string' ? locale : 'en-US';
            this._renderInWorker = renderInWorker;
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
            this._viewerFeatures = new Set();
            this._range = null;        // the RangeServer of the file shown (or opening)
            this._preparing = null;    // the AbortController of a previewFile still fetching its file
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
            // A previewFile still fetching its file is replaced: its fetches stop (it rejects with CLOSED).
            if (this._preparing) this._preparing.abort(fail(ErrorCode.CLOSED, 'Another file was opened before this one was ready.'));
            this._preparing = null;
            // The frame first: a new frame closes the old one, and with it the old file's fetches,
            // which must not include this file's.
            const ready = this._ensureFrame(mode, fileName);
            const preparing = new AbortController();
            this._preparing = preparing;
            const signal = preparing.signal;
            const content = prepareContent(file.content, signal);
            // Both run at once: the viewer boots while the file (or its end) downloads.
            let prepared, buffer;
            try {
                [prepared] = await Promise.all([content, ready]);
                if (signal.aborted) throw abortError(signal);
                const ranged = prepared.range && this._viewerFeatures.has('range') ? prepared.range : null;
                buffer = ranged ? (ranged.initial || new ArrayBuffer(0)) : await readWhole(prepared, signal);
                if (signal.aborted) throw abortError(signal);
            } finally {
                if (this._preparing === preparing) this._preparing = null;
            }
            const ranged = prepared.range && this._viewerFeatures.has('range') ? prepared.range : null;
            if (!ranged && buffer.byteLength > MAX_FILE_BYTES) throw tooLarge();

            const requestId = this._nextId++;
            const opened = this._request(requestId);
            // A new file: the ranges of the one before are no longer served, and its reads waiting stop.
            if (this._range) this._range.close();
            this._range = ranged ? new RangeServer(requestId, ranged) : null;
            this._post({
                type: 'open', requestId, fileName,
                fileId: typeof meta.id === 'string' ? meta.id : null,
                readOnly: meta.hasReadOnlyAccess === true,
                locale: this._locale,
                config: settings,
                save: this._saveOptions,
                range: ranged ? { length: ranged.length, initialOffset: ranged.initialOffset } : null,
                data: buffer,
            }, [buffer]);
            if (ranged) ranged.initial = null;
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
            if (this._renderInWorker) src.searchParams.set('renderInWorker', '1');
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
            // Every host fetch of this viewer stops: the file being prepared, and the ranges waiting.
            if (this._preparing) this._preparing.abort(fail(ErrorCode.CLOSED, 'The viewer was closed.'));
            this._preparing = null;
            if (this._range) this._range.close();
            this._range = null;
            if (!this._frame) return;
            if (this._lightBox) this._lightBox.close();
            else this._frame.remove();
            this._lightBox = null;
            this._frame = null;
            this._frameMode = null;
            this._document = 0;
            this._range = null;
            this._viewerFeatures = new Set();
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

        /**
         * The viewer asks for bytes of the file it shows (or opens). Checked (by RangeServer) before
         * any fetch: the file is the current one, the request id is a new whole number, the range is
         * whole numbers inside the file and at most MAX_RANGE_BYTES long, and at most
         * MAX_RANGE_REQUESTS wait at once. The bytes come from the source given to previewFile, never another.
         */
        async _serveRange(m, frame) {
            const r = this._range;
            const answer = r ? await r.serve(m)
                : Number.isSafeInteger(m.requestId) && m.requestId >= 1 && Number.isSafeInteger(m.file)
                    ? { ok: false, error: { code: ErrorCode.NO_DOCUMENT, message: 'This file is no longer open.' } } : null;
            // A file replaced or closed meanwhile gets no answer: the viewer ended its requests already.
            if (!answer || this._range !== r || this._frame !== frame || !frame.contentWindow) return;
            const extra = answer.ok ? { data: answer.data } : { error: answer.error };
            frame.contentWindow.postMessage({ protocol: PROTOCOL, version: PROTOCOL_VERSION, type: 'range', file: m.file, requestId: m.requestId, ok: answer.ok, ...extra },
                this._viewerOrigin, answer.ok ? [answer.data] : []);
        }

        /** Only the viewer's own frame, from the viewer's origin, with this protocol, is heard. */
        _onMessage(e) {
            const frame = this._frame;
            if (!frame || e.source !== frame.contentWindow || e.origin !== this._viewerOrigin) return;
            const m = e.data;
            if (!m || typeof m !== 'object' || m.protocol !== PROTOCOL || m.version !== PROTOCOL_VERSION || typeof m.type !== 'string') return;
            switch (m.type) {
                case 'ready':
                    this._viewerFeatures = new Set(Array.isArray(m.features) ? m.features.filter(f => typeof f === 'string') : []);
                    if (this._resolveReady) this._resolveReady();
                    break;
                case 'fetchRange':
                    this._serveRange(m, frame);
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
    // For tests of the range sources (tests/sdk); not part of the API.
    Object.defineProperty(PdfViewer, '_internals', {
        value: Object.freeze({ UrlRangeSource, LinearizedRangeSource, RangeServer, checkRange, parseContentRange, bufferOf, MAX_RANGE_BYTES, MAX_RANGE_REQUESTS, FETCH_TIMEOUT_MS }),
    });
    // For pdfviewer-embed.mjs: a module knows its own URL, a classic script reads currentScript.
    Object.defineProperty(PdfViewer, '_setScriptUrl', {
        value: url => { if (!scriptBase) scriptBase = new URL('.', url).href; },
    });
    global.PdfViewer = Object.freeze(PdfViewer);
})(globalThis);
