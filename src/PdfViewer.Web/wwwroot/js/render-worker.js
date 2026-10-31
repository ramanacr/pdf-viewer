// The opt-in render worker (index.html?renderInWorker=1): a dedicated module worker that starts a
// second, render-only .NET runtime from the app's own _framework and draws pages there, so that
// the page's thread stays free for scrolling and input. It needs no SharedArrayBuffer and no
// COOP/COEP headers: the two runtimes share nothing and talk by postMessage. The PDF bytes come in
// as an ArrayBuffer; each page goes back as an ImageBitmap (transferred, not copied). It runs no
// document JavaScript and sends nothing over the network but same-origin requests for the
// runtime's files and the bundled fonts.
//
// Messages in:  { op: 'open', id, bytes, password } | { op: 'render', id, page, scale, night } | { op: 'close', id }
// Messages out: { op: 'ready' } | { op: 'failed', message } | { id, ok, result | message }

const queue = [];
let exports = null;

// dotnet.js (9.0) decides when it is evaluated what kind of worker it runs in: a worker that has
// a message handler then, and no dotnetSidecar flag, is taken for one of the multithreaded
// runtime's pthread workers, and its start waits forever for the core assemblies (the promise
// coreAssetsInMemory is never resolved there). The flag says this worker runs a runtime of its own.
globalThis.dotnetSidecar = true;
// One message at a time, in order: a render waits for the open before it.
let chain = Promise.resolve();
const run = msg => (chain = chain.then(() => handle(msg)));
self.onmessage = e => (exports ? run(e.data) : queue.push(e.data));

const pending = new Map();   // render id -> the bitmap delivered during that call
const fonts = new Map();     // font file -> bytes, between fetchFont and takeFont

function deliver(id, view, width, height, scale, interpretMs, drawMs, textWithoutFont, imagesNotDecoded) {
    const bytes = view.slice();                       // a copy out of WebAssembly memory
    const words = new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >> 2);
    for (let i = 0; i < words.length; i++) {
        const p = words[i];                           // BGRA to RGBA, as viewer.js does
        words[i] = (p & 0xff00ff00) | ((p & 0xff) << 16) | ((p >>> 16) & 0xff);
    }
    pending.set(id, { pixels: new ImageData(new Uint8ClampedArray(bytes.buffer), width, height), width, height, scale, interpretMs, drawMs, textWithoutFont, imagesNotDecoded });
}

// The embed SDK's fontBaseUrl, as viewer.js passed it: an absolute path on this origin, or the
// default folder next to the app.
const fontBase = (() => {
    const value = new URL(import.meta.url).searchParams.get('fonts');
    return value && value.length <= 512 && /^\/(?:[A-Za-z0-9_~-][A-Za-z0-9._~-]*\/)*$/.test(value)
        && !value.split('/').some(s => s === '.' || s === '..') ? value : '../fonts/';
})();

async function fetchFont(file) {
    const parts = String(file).split('/');
    if (parts.some(p => p === '' || p === '.' || p === '..')) return -1;
    try {
        const response = await fetch(new URL(fontBase + parts.map(encodeURIComponent).join('/'), import.meta.url), { credentials: 'same-origin' });
        if (!response.ok) return -1;
        const bytes = new Uint8Array(await response.arrayBuffer());
        fonts.set(file, bytes);
        return bytes.length;
    } catch {
        return -1;
    }
}

function takeFont(file, view) {
    const bytes = fonts.get(file);
    fonts.delete(file);
    if (bytes) view.set(bytes);
}

async function handle(msg) {
    try {
        switch (msg.op) {
            case 'open': {
                const pages = await exports.Open(new Uint8Array(msg.bytes), msg.password ?? null);
                self.postMessage({ id: msg.id, ok: true, result: { pages } });
                break;
            }
            case 'render': {
                const started = performance.now();
                await exports.Render(msg.id, msg.page, msg.scale, msg.night);
                const r = pending.get(msg.id);
                pending.delete(msg.id);
                const bitmap = await createImageBitmap(r.pixels);
                self.postMessage({
                    id: msg.id, ok: true,
                    result: { bitmap, width: r.width, height: r.height, scale: r.scale, interpretMs: r.interpretMs, drawMs: r.drawMs,
                              textWithoutFont: r.textWithoutFont, imagesNotDecoded: r.imagesNotDecoded, workerMs: performance.now() - started },
                }, [bitmap]);
                break;
            }
            case 'close':
                await exports.Close();
                self.postMessage({ id: msg.id, ok: true, result: null });
                break;
        }
    } catch (e) {
        pending.delete(msg.id);
        // The failure's type and message only; never document content.
        self.postMessage({ id: msg.id, ok: false, message: String(e?.message ?? e) });
    }
}

try {
    const { dotnet } = await import('../_framework/dotnet.js');
    const runtime = await dotnet
        .withConfigSrc('blazor.boot.json')
        .withConfig({ cacheBootResources: false })
        .create();
    runtime.setModuleImports('render-worker', { deliver, fetchFont, takeFont });
    const config = runtime.getConfig();
    const all = await runtime.getAssemblyExports(config.mainAssemblyName + '.dll');
    exports = all.PdfViewer.Web.Interop.RenderWorkerExports;
    self.postMessage({ op: 'ready' });
    for (const msg of queue.splice(0)) run(msg);
} catch (e) {
    self.postMessage({ op: 'failed', message: String(e?.message ?? e) });
}
