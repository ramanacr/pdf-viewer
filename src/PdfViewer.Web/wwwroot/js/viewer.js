// The page's only script besides the .NET runtime. It draws the pixels the engine renders, reads
// the files the user picks or drops (in the browser, never uploaded), reports which pages are in
// view and forwards keyboard shortcuts. It sends nothing over the network.

let app = null;            // DotNetObjectReference of the viewer component
let scroller = null;       // the element that scrolls the pages
let observer = null;
const visible = new Set();
let viewportQueued = false;

/** Copies a BGRA page bitmap into the page's canvas (swapping to the RGBA order ImageData wants). */
export function drawPage(page, view, width, height) {
    const canvas = document.querySelector(`#page-${page} canvas`);
    if (!canvas) return;
    const bytes = view.slice();                       // a copy out of WebAssembly memory
    const words = new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >> 2);
    for (let i = 0; i < words.length; i++) {
        const p = words[i];                           // little endian: 0xAARRGGBB from B,G,R,A bytes
        words[i] = (p & 0xff00ff00) | ((p & 0xff) << 16) | ((p >>> 16) & 0xff);
    }
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    const ctx = canvas.getContext('2d', { alpha: false });
    ctx.putImageData(new ImageData(new Uint8ClampedArray(bytes.buffer, bytes.byteOffset, bytes.byteLength), width, height), 0, 0);
    canvas.closest('.page')?.classList.add('drawn');
}

/** Frees a page's canvas memory (the page scrolled far away). */
export function clearPage(page) {
    const canvas = document.querySelector(`#page-${page} canvas`);
    if (!canvas) return;
    canvas.width = 0;
    canvas.height = 0;
    canvas.closest('.page')?.classList.remove('drawn');
}

export function init(dotnet, scrollerId) {
    app = dotnet;
    scroller = document.getElementById(scrollerId);
    document.addEventListener('keydown', onKey);
    scroller.addEventListener('scroll', queueViewport, { passive: true });
    window.addEventListener('resize', () => { queueViewport(); app.invokeMethodAsync('OnResize'); });

    // Files: the hidden input and drag-and-drop anywhere on the page.
    const input = document.getElementById('file-input');
    input.addEventListener('change', () => { if (input.files.length) openFile(input.files[0]); input.value = ''; });
    let depth = 0;
    const hasFiles = e => Array.from(e.dataTransfer?.types ?? []).includes('Files');
    document.addEventListener('dragenter', e => { if (!hasFiles(e)) return; e.preventDefault(); if (depth++ === 0) document.body.classList.add('dropping'); });
    document.addEventListener('dragleave', e => { if (!hasFiles(e)) return; if (--depth <= 0) { depth = 0; document.body.classList.remove('dropping'); } });
    document.addEventListener('dragover', e => { if (hasFiles(e)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; } });
    document.addEventListener('drop', e => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth = 0;
        document.body.classList.remove('dropping');
        const file = e.dataTransfer.files[0];
        if (file) openFile(file);
    });
}

const MaxFileBytes = 512 * 1024 * 1024;

async function openFile(file) {
    if (file.size > MaxFileBytes) {
        app.invokeMethodAsync('OnFileRejected', file.name, 'This file is larger than 512 MB, the most this viewer opens.');
        return;
    }
    app.invokeMethodAsync('OnFileReading', file.name);
    try {
        const bytes = new Uint8Array(await file.arrayBuffer());
        await app.invokeMethodAsync('OnFileOpened', file.name, bytes);
    } catch {
        app.invokeMethodAsync('OnFileRejected', file.name, 'The browser could not read this file.');
    }
}

export function pickFile() {
    document.getElementById('file-input').click();
}

/** Starts reporting the pages in view; called again after the page elements change. */
export function observePages() {
    observer?.disconnect();
    visible.clear();
    observer = new IntersectionObserver(entries => {
        for (const e of entries) {
            const n = Number(e.target.dataset.page);
            if (e.isIntersecting) visible.add(n); else visible.delete(n);
        }
        queueViewport();
    }, { root: scroller, rootMargin: '50% 0px' });
    scroller.querySelectorAll('.page').forEach(p => observer.observe(p));
    queueViewport();
}

function queueViewport() {
    if (viewportQueued || !app) return;
    viewportQueued = true;
    requestAnimationFrame(() => {
        viewportQueued = false;
        app.invokeMethodAsync('OnViewport', Array.from(visible).sort((a, b) => a - b), currentPage());
    });
}

/** The page under the middle of the view. */
function currentPage() {
    if (!scroller) return 1;
    const middle = scroller.getBoundingClientRect().top + scroller.clientHeight / 2;
    let best = 1, bestDistance = Infinity;
    for (const n of visible) {
        const el = document.getElementById(`page-${n}`);
        if (!el) continue;
        const r = el.getBoundingClientRect();
        const d = middle < r.top ? r.top - middle : middle > r.bottom ? middle - r.bottom : 0;
        if (d < bestDistance) { bestDistance = d; best = n; }
    }
    return best;
}

/** Scrolls so that the page (and, when given, a point on it in CSS pixels from its top) is in view. */
export function scrollToPage(page, offsetY, smooth) {
    const el = document.getElementById(`page-${page}`);
    if (!el || !scroller) return;
    const top = el.offsetTop - 12 + (offsetY > 0 ? Math.max(0, offsetY - scroller.clientHeight / 3) : 0);
    scroller.scrollTo({ top, behavior: smooth ? 'smooth' : 'auto' });
}

/** Keeps the same place in view across a zoom change: the scroll position scales with the pages. */
export function rescaleScroll(ratio) {
    if (!scroller) return;
    const middle = scroller.scrollTop + scroller.clientHeight / 2;
    scroller.scrollTop = middle * ratio - scroller.clientHeight / 2;
}

/** The scroller's inner size in CSS pixels, for fit-width and fit-page. */
export function viewportSize() {
    if (!scroller) return [800, 600];
    const style = getComputedStyle(scroller);
    return [scroller.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight),
            scroller.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom)];
}

export function devicePixelRatio() {
    return window.devicePixelRatio || 1;
}

export function focusElement(id, select) {
    const el = document.getElementById(id);
    if (!el) return;
    el.focus();
    if (select && el.select) el.select();
}

/** Per-viewer conveniences only (theme, sidebar); the viewer works the same without storage. */
export function readSetting(key) {
    try { return localStorage.getItem('pdfviewer.' + key); } catch { return null; }
}

export function writeSetting(key, value) {
    try { localStorage.setItem('pdfviewer.' + key, value); } catch { /* private mode: not kept */ }
}

export function setTheme(theme) {
    document.documentElement.dataset.theme = theme;   // 'light', 'dark' or 'system'
}

/**
 * One of the fonts bundled with the app (wwwroot/fonts, relative to the base href, for example
 * 'LiberationSans-Regular.ttf' or 'noto/index.json'), fetched when a page first needs it; the
 * browser's HTTP cache keeps it. Same origin only. Null when it cannot be had.
 */
export async function fetchFont(file) {
    const parts = String(file).split('/');
    if (parts.some(p => p === '' || p === '.' || p === '..')) return null;
    try {
        const response = await fetch('fonts/' + parts.map(encodeURIComponent).join('/'), { credentials: 'same-origin' });
        return response.ok ? new Uint8Array(await response.arrayBuffer()) : null;
    } catch {
        return null;
    }
}

export function hasLocalFonts() {
    return typeof window.queryLocalFonts === 'function';
}

/**
 * Hands the engine the device's fonts of the given families (Local Font Access API; the browser
 * asks the user first). The files go to the .NET runtime in this tab and nowhere else. Returns the
 * number of faces found, or -1 when the browser refused.
 */
export async function loadLocalFonts(families, maxBytes) {
    let fonts;
    try {
        fonts = await window.queryLocalFonts();
    } catch {
        return -1;
    }
    // A browser that denies the permission may answer with an empty list rather than an error.
    if (fonts.length === 0) return -1;
    const wanted = families.map(f => f.toLowerCase());
    const picked = fonts
        .filter(f => wanted.includes(f.family.toLowerCase()))
        .sort((a, b) => wanted.indexOf(a.family.toLowerCase()) - wanted.indexOf(b.family.toLowerCase()));
    let total = 0;
    const seen = new Set();
    for (const font of picked) {
        const blob = await font.blob();
        if (total + blob.size > maxBytes) continue;
        const bytes = new Uint8Array(await blob.arrayBuffer());
        // One file per face name (each face of a collection brings the whole collection).
        const key = font.postscriptName;
        if (seen.has(key)) continue;
        seen.add(key);
        total += blob.size;
        await app.invokeMethodAsync('OnLocalFont', font.postscriptName, bytes);
    }
    return picked.length;
}

function inField(e) {
    const t = e.target;
    return t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t?.isContentEditable;
}

function onKey(e) {
    if (!app) return;
    const mod = e.ctrlKey || e.metaKey;
    let action = null;
    if (mod && !e.altKey) {
        switch (e.key) {
            case 'f': case 'F': action = 'find'; break;
            case 'o': case 'O': action = 'open'; break;
            case '+': case '=': action = 'zoom-in'; break;
            case '-': case '_': action = 'zoom-out'; break;
            case '0': action = 'fit-width'; break;
            case '9': action = 'fit-page'; break;
        }
    } else if (e.key === 'F3') {
        action = e.shiftKey ? 'find-previous' : 'find-next';
    } else if (e.key === 'Escape') {
        action = 'escape';
    } else if (!inField(e) && !e.altKey) {
        switch (e.key) {
            case 'ArrowRight': case 'n': case 'j': action = 'next-page'; break;
            case 'ArrowLeft': case 'p': case 'k': action = 'previous-page'; break;
            case 'Home': action = 'first-page'; break;
            case 'End': action = 'last-page'; break;
            case 'd': action = 'toggle-theme'; break;
            case 'i': action = 'toggle-night'; break;
            case 'o': action = 'toggle-sidebar'; break;
            case '?': action = 'help'; break;
        }
    }
    if (action) {
        e.preventDefault();
        app.invokeMethodAsync('OnShortcut', action);
    }
}
