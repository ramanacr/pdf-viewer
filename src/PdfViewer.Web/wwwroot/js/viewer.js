// The page's only script besides the .NET runtime. It draws the pixels the engine renders, reads
// the files the user picks or drops (in the browser, never uploaded), reports which pages are in
// view, forwards keyboard shortcuts and mouse selection, and saves and prints the open document.
// It sends nothing over the network. When the page is embedded (index.html?embed=1, see
// pdfviewer-embed.js), it also carries the messages between the host page and the viewer.

import { fontBaseOf, fontFilePath } from './font-base.js?v=10';

let app = null;            // DotNetObjectReference of the viewer component
let scroller = null;       // the element that scrolls the pages
let observer = null;
const visible = new Set();
const ratios = new Map();  // in line: how much of each page is in view
let viewportQueued = false;
let inline = false;        // embedded in line: the host page scrolls, not the pages element

/** RGBA image data from a BGRA bitmap in WebAssembly memory (one copy, two channels swapped). */
function imageData(view, width, height) {
    const bytes = view.slice();                       // a copy out of WebAssembly memory
    const words = new Uint32Array(bytes.buffer, bytes.byteOffset, bytes.byteLength >> 2);
    for (let i = 0; i < words.length; i++) {
        const p = words[i];                           // little endian: 0xAARRGGBB from B,G,R,A bytes
        words[i] = (p & 0xff00ff00) | ((p & 0xff) << 16) | ((p >>> 16) & 0xff);
    }
    return new ImageData(new Uint8ClampedArray(bytes.buffer, bytes.byteOffset, bytes.byteLength), width, height);
}

function drawInto(canvas, view, width, height) {
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    canvas.getContext('2d', { alpha: false }).putImageData(imageData(view, width, height), 0, 0);
}

/** Copies a BGRA page bitmap into the page's canvas. */
export function drawPage(page, view, width, height) {
    const canvas = document.querySelector(`#page-${page} canvas`);
    if (!canvas) return;
    drawInto(canvas, view, width, height);
    canvas.closest('.page')?.classList.add('drawn');
}

/** Copies a small page bitmap into its thumbnail in the side panel. */
export function drawThumbnail(page, view, width, height) {
    const canvas = document.querySelector(`#thumb-${page} canvas`);
    if (canvas) drawInto(canvas, view, width, height);
}

/** Frees a page's canvas memory (the page scrolled far away). */
export function clearPage(page) {
    const canvas = document.querySelector(`#page-${page} canvas`);
    if (!canvas) return;
    canvas.width = 0;
    canvas.height = 0;
    canvas.closest('.page')?.classList.remove('drawn');
}

/**
 * Starts the viewer's page script. files: the file picker and drag and drop (the demo; an embedded
 * viewer shows only what the host page gives it). inlineMode: embedded in line.
 */
export function init(dotnet, scrollerId, files, inlineMode) {
    app = dotnet;
    scroller = document.getElementById(scrollerId);
    inline = !!inlineMode;
    document.addEventListener('keydown', onKey);
    document.addEventListener('copy', onCopy);
    scroller.addEventListener('scroll', queueViewport, { passive: true });
    window.addEventListener('resize', () => { queueViewport(); app.invokeMethodAsync('OnResize'); });
    initSelection();
    if (inline) {
        // The host page sizes the frame to the content and scrolls the pages into view.
        new ResizeObserver(() => app.invokeMethodAsync('OnContentHeight', document.documentElement.scrollHeight))
            .observe(document.body);
    }
    if (!files) return;

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
    ratios.clear();
    // In line, the frame is as tall as the document: what is in view is decided by the host
    // page's viewport, which an observer without a root measures against.
    observer = new IntersectionObserver(entries => {
        for (const e of entries) {
            const n = Number(e.target.dataset.page);
            if (e.isIntersecting) visible.add(n); else visible.delete(n);
            ratios.set(n, e.isIntersecting ? e.intersectionRatio : 0);
        }
        queueViewport();
    }, inline ? { threshold: [0, 0.25, 0.5, 0.75, 1] } : { root: scroller, rootMargin: '50% 0px' });
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

/** The page under the middle of the view (in line: the page most in view). */
function currentPage() {
    if (!scroller) return 1;
    if (inline) {
        let best = visible.size ? Math.min(...visible) : 1, bestRatio = 0;
        for (const n of Array.from(visible).sort((a, b) => a - b)) {
            const r = ratios.get(n) ?? 0;
            if (r > bestRatio + 0.01) { bestRatio = r; best = n; }
        }
        return best;
    }
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
    if (inline) {
        app.invokeMethodAsync('OnScrollHost', el.getBoundingClientRect().top + window.scrollY - 12 + Math.max(0, offsetY - 40));
        return;
    }
    // From the rectangles, not offsetTop: the pages element is not the offset parent of the pages.
    const top = scroller.scrollTop + el.getBoundingClientRect().top - scroller.getBoundingClientRect().top - 12 + (offsetY > 0 ? Math.max(0, offsetY - scroller.clientHeight / 3) : 0);
    scroller.scrollTo({ top, behavior: smooth ? 'smooth' : 'auto' });
}

/**
 * Scrolls so that a point of a page (CSS pixels from its top-left corner) is at the top-left of
 * the view. In line, the host page scrolls instead: the point's offset from the top of the frame
 * goes to it.
 */
export function scrollToLocation(page, x, y) {
    const el = document.getElementById(`page-${page}`);
    if (!el || !scroller) return;
    const r = el.getBoundingClientRect();
    if (inline) {
        app.invokeMethodAsync('OnScrollHost', r.top + window.scrollY + y);
        return;
    }
    const view = scroller.getBoundingClientRect();
    scroller.scrollTo({ top: scroller.scrollTop + r.top - view.top + y, left: scroller.scrollLeft + r.left - view.left + x, behavior: 'auto' });
}

/** Keeps the same place in view across a zoom change: the scroll position scales with the pages. */
export function rescaleScroll(ratio) {
    if (!scroller || inline) return;
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

export function canFullscreen() {
    return document.fullscreenEnabled === true;
}

/** Shows the viewer full screen, or leaves full screen. The host's iframe must allow it. */
export function toggleFullscreen() {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else document.documentElement.requestFullscreen?.().catch(() => {});
}

/**
 * One of the fonts bundled with the app (wwwroot/fonts, relative to the base href, for example
 * 'LiberationSans-Regular.ttf' or 'noto/index.json'), fetched when a page first needs it; the
 * browser's HTTP cache keeps it. Same origin only: `base` (the embed SDK's fontBaseUrl, as
 * EmbedFrame.ParseFontBase read it) is a plain path, checked again here (font-base.js), so the
 * request cannot leave this origin. With `reload`, the HTTP cache is bypassed: the engine asks
 * so once when a file did not match its size and SHA-256. Null when it cannot be had; the engine
 * then reports the text as missing.
 */
export async function fetchFont(file, base, reload) {
    const path = fontFilePath(file, base, 'fonts/');
    if (path === null) return null;
    try {
        const response = await fetch(path, { credentials: 'same-origin', cache: reload === true ? 'reload' : 'default' });
        return response.ok ? new Uint8Array(await response.arrayBuffer()) : null;
    } catch {
        return null;
    }
}

export function hasLocalFonts() {
    return typeof window.queryLocalFonts === 'function';
}

/** The width below which app.css shows the side panel as a drawer over the pages. */
export function isNarrow() {
    return window.matchMedia('(max-width: 760px)').matches;
}

/** Keyboard movement in a menu: key is Home, End, ArrowDown or ArrowUp; disabled items are skipped. */
export function moveMenuFocus(menuId, key) {
    const items = Array.from(document.getElementById(menuId)?.querySelectorAll('[role^="menuitem"]:not(:disabled)') ?? []);
    if (items.length === 0) return;
    const at = items.indexOf(document.activeElement);
    const next = key === 'Home' ? 0
        : key === 'End' ? items.length - 1
        : key === 'ArrowUp' ? (at <= 0 ? items.length - 1 : at - 1)
        : (at + 1) % items.length;
    items[next].focus();
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

/** The focus is in a text field: it keeps its own editing keys (select all, copy, undo). */
function inField(e) {
    const t = e.target;
    return t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t?.isContentEditable;
}

/** The focus is in a text field or a list box (zoom, stamp, shape style): it keeps its letters, arrows and Delete. */
function inFieldOrList(e) {
    // A menu's Home, End and letter keys are its own, not page shortcuts.
    return inField(e) || e.target instanceof HTMLSelectElement || !!e.target.closest?.('[role="menu"]');
}

function onKey(e) {
    if (!app) return;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'a' || e.key === 'A') && !inField(e)) {
        e.preventDefault();
        app.invokeMethodAsync('OnShortcut', 'select-all');
        return;
    }
    // Ctrl+C with no browser selection may copy nothing: run the copy command, whose copy event
    // (onCopy) puts the viewer's selection on the clipboard.
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'c' || e.key === 'C') && !inField(e) && selectionText !== null) {
        e.preventDefault();
        if (!document.execCommand('copy')) navigator.clipboard?.writeText(selectionText).then(() => app.invokeMethodAsync('OnCopied'), () => {});
        return;
    }
    const mod = e.ctrlKey || e.metaKey;
    let action = null;
    if (mod && !e.altKey && (e.key === 's' || e.key === 'S')) {
        // Save, also from a form field: the browser's "save page" is never what the reader wants here.
        e.preventDefault();
        app.invokeMethodAsync('OnShortcut', 'save');
        return;
    }
    if (mod && !e.altKey && !inField(e) && (e.key === 'z' || e.key === 'Z' || e.key === 'y' || e.key === 'Y')) {
        e.preventDefault();
        app.invokeMethodAsync('OnShortcut', (e.key === 'y' || e.key === 'Y' || e.shiftKey) ? 'redo' : 'undo');
        return;
    }
    // Alt+Left: back to where a link was followed from, only when there is such a place; else
    // the browser's own Back is left alone.
    if (canGoBackByLink && e.altKey && !mod && !e.shiftKey && e.key === 'ArrowLeft' && !inFieldOrList(e)) {
        e.preventDefault();
        app.invokeMethodAsync('OnShortcut', 'back');
        return;
    }
    // Enter or Space on a link reached with Tab follows it.
    if ((e.key === 'Enter' || e.key === ' ') && !mod && !e.altKey && e.target instanceof Element && e.target.matches?.('.link-hit[data-link]')) {
        e.preventDefault();
        followLink(e.target);
        return;
    }
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
    } else if (!inFieldOrList(e) && !e.altKey) {
        switch (e.key) {
            case 'Delete': case 'Backspace': action = 'delete'; break;
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

// ------------------------------------------------------------------ text selection and copy

let selectionText = null;  // the viewer's selected text, for the copy event; null when none
let dragging = null;       // { page, el } while the mouse button is down on a page
let linkPress = null;      // { el, x, y } while a press on a link may still be a click
let canGoBackByLink = false; // the viewer has a place to go back to (Alt+Left is then the viewer's)

/** The viewer's link history became empty (false) or not (true). */
export function setLinkBack(value) {
    canGoBackByLink = !!value;
}
let moveQueued = null;

/** The text the next copy puts on the clipboard (null: nothing selected, or the document does not allow copying). */
export function setSelectionText(text) {
    selectionText = text && text.length > 0 ? text : null;
}

/** Asks the viewer to follow the link this element stands for (the viewer checks the tool and the link). */
function followLink(el) {
    const page = Number(el.closest('.page')?.dataset.page);
    const index = Number(el.dataset.link);
    if (Number.isInteger(page) && Number.isInteger(index)) app.invokeMethodAsync('OnLinkActivate', page, index);
}

/**
 * Opens a web link the reader agreed to open, in a new tab with no access back to this page.
 * Only http, https and mailto: the viewer checked already, and this checks again.
 */
export function openExternal(url) {
    let parsed;
    try { parsed = new URL(url); } catch { return false; }
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:' && parsed.protocol !== 'mailto:') return false;
    window.open(url, '_blank', 'noopener,noreferrer'); // the string the dialog showed
    return true;
}

/** The page at the view's top-left corner and the point of it there, in CSS pixels from the page's corner: [page, x, y]. */
export function viewLocation() {
    if (!scroller || inline) return [currentPage(), 0, 0];
    const page = currentPage();
    const el = document.getElementById(`page-${page}`);
    if (!el) return [page, 0, 0];
    const r = el.getBoundingClientRect();
    const view = scroller.getBoundingClientRect();
    return [page, Math.max(0, view.left - r.left), Math.max(0, view.top - r.top)];
}

function pointOn(el, e) {
    const r = el.getBoundingClientRect();
    return [(e.clientX - r.left) / r.width, (e.clientY - r.top) / r.height];
}

// ------------------------------------------------------------------ annotation tools

let tool = 'select';       // the annotation tool (Viewer.Annotations.cs): select, highlight, note, ink, square, …
let drawing = null;        // { page, el, points, preview } while a shape or a stroke is drawn
let moving = null;         // { id, el, x, y, dx, dy } while an annotation is dragged
let anchorMoving = null;   // { id, el, page, x, y, dx, dy } while a callout's anchor handle is dragged
let calloutAnchor = null;  // { el, page, x, y, mark } the spot the callout tool took, until its box is placed
const markupTools = new Set(['select', 'highlight', 'underline', 'strikeout']);
const clickTools = new Set(['note', 'stamp']);       // a click places them
const polyTools = new Set(['polygon', 'polyline']);  // a click a point, a double-click (or Enter) ends
const MaxInkPoints = 10000;

/** The tool the pages follow: text tools select text, the others draw. */
export function setTool(name) {
    tool = String(name);
    if (scroller) scroller.dataset.tool = tool;
    cancelDrawing();
    clearCalloutAnchor();
}

/** The callout tool's first press: the spot the callout points to, marked until the box is placed. */
function setCalloutAnchor(el, e) {
    clearCalloutAnchor();
    const [x, y] = pointOn(el, e);
    const mark = document.createElement('span');
    mark.className = 'callout-mark';
    mark.setAttribute('aria-hidden', 'true');
    mark.style.left = `${x * 100}%`;
    mark.style.top = `${y * 100}%`;
    el.appendChild(mark);
    calloutAnchor = { el, page: Number(el.dataset.page), x, y, mark };
}

function clearCalloutAnchor() {
    calloutAnchor?.mark.remove();
    calloutAnchor = null;
}

function cancelDrawing() {
    drawing?.preview?.ownerSVGElement?.remove();
    drawing = null;
}

function startDrawing(el, e) {
    const [x, y] = pointOn(el, e);
    drawing = { page: Number(el.dataset.page), el, points: [x, y], hover: [x, y], preview: null };
    if (clickTools.has(tool)) return;
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('class', 'draw-preview');
    svg.setAttribute('viewBox', '0 0 1 1');
    svg.setAttribute('preserveAspectRatio', 'none');
    svg.setAttribute('aria-hidden', 'true');
    const kind = { ink: 'polyline', polyline: 'polyline', polygon: 'polygon', circle: 'ellipse', line: 'line', arrow: 'line' }[tool] ?? 'rect';
    const shape = document.createElementNS(ns, kind);
    svg.appendChild(shape);
    el.appendChild(svg);
    drawing.preview = shape;
    drawShape();
}

function drawShape() {
    const d = drawing;
    if (!d || !d.preview) return;
    const p = polyTools.has(tool) ? d.points.concat(d.hover) : d.points;
    if (tool === 'ink' || polyTools.has(tool)) {
        let s = '';
        for (let i = 0; i + 1 < p.length; i += 2) s += `${p[i]},${p[i + 1]} `;
        d.preview.setAttribute('points', s);
        return;
    }
    const [x0, y0] = p, x1 = p[p.length - 2], y1 = p[p.length - 1];
    if (tool === 'line' || tool === 'arrow') {
        d.preview.setAttribute('x1', x0); d.preview.setAttribute('y1', y0);
        d.preview.setAttribute('x2', x1); d.preview.setAttribute('y2', y1);
    } else if (tool === 'circle') {
        d.preview.setAttribute('cx', (x0 + x1) / 2); d.preview.setAttribute('cy', (y0 + y1) / 2);
        d.preview.setAttribute('rx', Math.abs(x1 - x0) / 2); d.preview.setAttribute('ry', Math.abs(y1 - y0) / 2);
    } else {
        d.preview.setAttribute('x', Math.min(x0, x1)); d.preview.setAttribute('y', Math.min(y0, y1));
        d.preview.setAttribute('width', Math.abs(x1 - x0)); d.preview.setAttribute('height', Math.abs(y1 - y0));
    }
}

function finishDrawing() {
    const d = drawing;
    drawing = null;
    if (!d) return;
    d.preview?.ownerSVGElement?.remove();
    let points = d.points;
    if (tool !== 'ink' && !polyTools.has(tool)) points = points.length >= 4 ? [points[0], points[1], points[points.length - 2], points[points.length - 1]] : [points[0], points[1], points[0], points[1]];
    if (tool === 'callout') {
        // The spot it points to first, then the text box's corners.
        const a = calloutAnchor;
        clearCalloutAnchor();
        if (!a || a.page !== d.page) return;
        points = [a.x, a.y].concat(points);
    }
    app.invokeMethodAsync('OnToolStroke', tool, d.page, points);
}

let lastPolyClick = null;   // { time, x, y } of the last press, to tell a double-click

/**
 * A polygon or polyline tool: a click adds a point; the second press of a double-click, or
 * Enter, ends it. The presses are counted here: a pointerdown's detail is 0 in some browsers.
 */
function polyClick(el, e) {
    const last = lastPolyClick;
    lastPolyClick = { time: e.timeStamp, x: e.clientX, y: e.clientY };
    const double = last !== null && e.timeStamp - last.time < 400 && Math.abs(e.clientX - last.x) + Math.abs(e.clientY - last.y) < 8;
    if (drawing && drawing.el !== el) cancelDrawing();
    if (!drawing) {
        startDrawing(el, e);
        return;
    }
    if (double) {
        lastPolyClick = null;
        finishDrawing();
        return;
    }
    const [x, y] = pointOn(el, e);
    if (drawing.points.length < 2 * MaxInkPoints) drawing.points.push(x, y);
    drawShape();
}

function moveAnnotation(dx, dy) {
    const m = moving;
    if (!m) return;
    m.dx = dx;
    m.dy = dy;
    m.el.style.transform = `translate(${dx}px, ${dy}px)`;
}

/**
 * The link a finger meant: the one under it, else the nearest one within 22 px (half of a 44 px
 * touch target). Links in a contents list can be 2 px apart at phone zoom, so a link's area is not
 * simply made bigger: that would cover its neighbour. Taps on fields and annotations are theirs.
 */
function linkForTap(e) {
    const under = e.target.closest?.('.link-hit[data-link]');
    if (under) return under;
    const page = e.target.closest?.('.page');
    if (!page || e.target.closest('.form-field, .annot-hit, .annot-editor')) return null;
    let nearest = null, distance = 22;
    for (const el of page.querySelectorAll('.link-hit[data-link]')) {
        const r = el.getBoundingClientRect();
        const d = Math.hypot(Math.max(r.left - e.clientX, 0, e.clientX - r.right), Math.max(r.top - e.clientY, 0, e.clientY - r.bottom));
        if (d < distance) { nearest = el; distance = d; }
    }
    return nearest;
}

/** A mouse or a pen selects text on a page (touch scrolls). The engine finds the characters. */
function initSelection() {
    scroller.dataset.tool = tool;
    let touched = false;
    // A tap (touch scrolls, so it does not start a selection) on a link follows it.
    scroller.addEventListener('click', e => {
        if (!touched || tool !== 'select') return;
        const link = linkForTap(e);
        if (link) { e.preventDefault(); followLink(link); }
    });
    scroller.addEventListener('pointerdown', e => {
        touched = e.pointerType === 'touch';
        if (e.button !== 0 || e.pointerType === 'touch') return;
        // Form fields and the note editor take their own input.
        if (e.target.closest?.('.form-field, .annot-editor')) return;
        // A selected callout's anchor handle: dragged to move the spot the callout points to.
        const anchorHit = e.target.closest?.('.callout-anchor[data-anchor]');
        if (anchorHit && tool === 'select') {
            e.preventDefault();
            scroller.focus({ preventScroll: true });
            anchorMoving = { id: anchorHit.dataset.anchor, el: anchorHit, page: anchorHit.closest('.page'), x: e.clientX, y: e.clientY, dx: 0, dy: 0 };
            scroller.setPointerCapture(e.pointerId);
            return;
        }
        const hit = e.target.closest?.('.annot-hit[data-annot]');
        if (hit && tool === 'select') {
            e.preventDefault();
            scroller.focus({ preventScroll: true });
            moving = { id: hit.dataset.annot, el: hit, x: e.clientX, y: e.clientY, dx: 0, dy: 0, page: hit.closest('.page') };
            scroller.setPointerCapture(e.pointerId);
            app.invokeMethodAsync('OnAnnotationPointer', moving.id);
            return;
        }
        const el = e.target.closest?.('.page');
        if (!el) {
            if (selectionText !== null) app.invokeMethodAsync('OnSelectClear');
            return;
        }
        // A press on a link: followed on release unless it becomes a drag (which selects text).
        const link = tool === 'select' && e.detail <= 1 && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey ? e.target.closest?.('.link-hit[data-link]') : null;
        linkPress = link ? { el: link, x: e.clientX, y: e.clientY } : null;
        if (polyTools.has(tool)) {
            e.preventDefault();
            scroller.focus({ preventScroll: true });
            polyClick(el, e);
            return;
        }
        // The callout tool: the first press takes the spot it points to (on this page); the next one places the box.
        if (tool === 'callout' && (!calloutAnchor || calloutAnchor.page !== Number(el.dataset.page))) {
            e.preventDefault();
            scroller.focus({ preventScroll: true });
            setCalloutAnchor(el, e);
            return;
        }
        if (!markupTools.has(tool)) {
            e.preventDefault();
            scroller.focus({ preventScroll: true });
            scroller.setPointerCapture(e.pointerId);
            startDrawing(el, e);
            return;
        }
        e.preventDefault();               // no native selection or drag of the canvas
        scroller.focus({ preventScroll: true });
        dragging = { page: Number(el.dataset.page), el };
        scroller.setPointerCapture(e.pointerId);
        const [x, y] = pointOn(el, e);
        app.invokeMethodAsync('OnSelectStart', dragging.page, x, y, e.detail || 1);
    });
    scroller.addEventListener('pointermove', e => {
        if (anchorMoving) {
            anchorMoving.dx = e.clientX - anchorMoving.x;
            anchorMoving.dy = e.clientY - anchorMoving.y;
            anchorMoving.el.style.transform = `translate(${anchorMoving.dx}px, ${anchorMoving.dy}px)`;
            return;
        }
        if (moving) {
            moveAnnotation(e.clientX - moving.x, e.clientY - moving.y);
            return;
        }
        if (drawing) {
            const [x, y] = pointOn(drawing.el, e);
            if (polyTools.has(tool)) {
                drawing.hover = [x, y];
            } else if (tool === 'ink') {
                if (drawing.points.length < 2 * MaxInkPoints) drawing.points.push(x, y);
            } else {
                drawing.points = [drawing.points[0], drawing.points[1], x, y];
            }
            drawShape();
            return;
        }
        if (linkPress && (Math.abs(e.clientX - linkPress.x) > 4 || Math.abs(e.clientY - linkPress.y) > 4)) linkPress = null;
        if (!dragging) return;
        const queued = moveQueued !== null;
        moveQueued = pointOn(dragging.el, e);
        if (queued) return;
        requestAnimationFrame(() => {
            if (!dragging || moveQueued === null) { moveQueued = null; return; }
            const [x, y] = moveQueued;
            moveQueued = null;
            app.invokeMethodAsync('OnSelectMove', dragging.page, x, y);
        });
    });
    const end = e => {
        if (anchorMoving) {
            const m = anchorMoving;
            anchorMoving = null;
            m.el.style.transform = '';
            if (m.page && e.type === 'pointerup' && (Math.abs(m.dx) > 3 || Math.abs(m.dy) > 3)) {
                const [x, y] = pointOn(m.page, e);
                app.invokeMethodAsync('OnCalloutAnchorDrag', m.id, x, y);
            }
            return;
        }
        if (moving) {
            const m = moving;
            moving = null;
            const r = m.page?.getBoundingClientRect();
            m.el.style.transform = '';
            if (r && r.width > 0 && (Math.abs(m.dx) > 3 || Math.abs(m.dy) > 3) && e.type === 'pointerup') {
                app.invokeMethodAsync('OnAnnotationDrag', m.id, m.dx / r.width, m.dy / r.height);
            }
            return;
        }
        if (drawing) {
            if (polyTools.has(tool) && e.type === 'pointerup') return; // the polygon goes on until a double-click
            if (e.type === 'pointerup') finishDrawing();
            else cancelDrawing();
            return;
        }
        const wasSelecting = dragging !== null;
        dragging = null;
        moveQueued = null;
        const pressed = linkPress;
        linkPress = null;
        if (pressed && e.type === 'pointerup') {
            followLink(pressed.el);
            return;
        }
        if (wasSelecting && e.type === 'pointerup' && tool !== 'select') app.invokeMethodAsync('OnSelectEnd');
    };
    scroller.addEventListener('pointerup', end);
    scroller.addEventListener('pointercancel', end);
    scroller.addEventListener('keydown', e => {
        // Esc drops the callout's spot, and its box while it is dragged.
        if (e.key === 'Escape' && calloutAnchor) { e.preventDefault(); cancelDrawing(); clearCalloutAnchor(); return; }
        if (!drawing || !polyTools.has(tool)) return;
        if (e.key === 'Enter') { e.preventDefault(); finishDrawing(); }
        else if (e.key === 'Escape') { e.preventDefault(); cancelDrawing(); }
    });
}

function onCopy(e) {
    if (selectionText === null || inField(e) || !e.clipboardData) return;
    e.clipboardData.setData('text/plain', selectionText);
    e.preventDefault();
    app.invokeMethodAsync('OnCopied');
}

// ------------------------------------------------------------------ thumbnails

let thumbObserver = null;
const thumbsVisible = new Set();

/** Starts reporting the thumbnails in view in the side panel; called again when the panel changes. */
export function observeThumbnails() {
    thumbObserver?.disconnect();
    thumbObserver = null;
    thumbsVisible.clear();
    const panel = document.getElementById('panel-thumbnails');
    if (!panel) return;
    thumbObserver = new IntersectionObserver(entries => {
        for (const e of entries) {
            const n = Number(e.target.dataset.thumb);
            if (e.isIntersecting) {
                thumbsVisible.add(n);
            } else if (thumbsVisible.delete(n)) {
                const canvas = e.target.querySelector('canvas');
                if (canvas) canvas.width = canvas.height = 0;   // its memory back; drawn again when it returns
            }
        }
        app.invokeMethodAsync('OnThumbnails', Array.from(thumbsVisible).sort((a, b) => a - b));
    }, { root: panel, rootMargin: '100% 0px' });
    panel.querySelectorAll('[data-thumb]').forEach(t => thumbObserver.observe(t));
}

// ------------------------------------------------------------------ download and print

/** Saves the document's original bytes under its name: a Blob download, nothing leaves the device. */
export function downloadBytes(view, name) {
    const blob = new Blob([view.slice()], { type: 'application/pdf' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
}

let printImages = [];
let printEncoding = [];

export function printBegin() {
    printEnd();
}

/**
 * Adds one rendered page to the print job: a PNG image, printed at the page's size in points. The
 * pixels are copied at once; the PNG is encoded after (see printPending).
 */
export function printAddPage(view, width, height, widthPt, heightPt) {
    const canvas = document.createElement('canvas');
    drawInto(canvas, view, width, height);
    const slot = { url: null, widthPt, heightPt };
    printImages.push(slot);
    printEncoding.push(new Promise(resolve => canvas.toBlob(blob => {
        if (blob) slot.url = URL.createObjectURL(blob);
        canvas.width = canvas.height = 0;
        resolve();
    }, 'image/png')));
}

/** Waits for the pages added so far to be encoded (so that only one page's pixels are held at a time). */
export async function printPending() {
    const pending = printEncoding;
    printEncoding = [];
    await Promise.all(pending);
    return printImages.length;
}

/**
 * Prints the pages added since printBegin through a hidden iframe, one page per sheet. The sheet
 * size is the first page's. Resolves when the print dialog has closed.
 */
export function printRun(title) {
    const frame = document.createElement('iframe');
    frame.setAttribute('aria-hidden', 'true');
    frame.tabIndex = -1;
    frame.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0;pointer-events:none';
    document.body.appendChild(frame);
    const doc = frame.contentDocument;
    doc.title = title;
    const first = printImages[0];
    const style = doc.createElement('style');
    style.textContent = `@page { margin: 0; ${first ? `size: ${first.widthPt}pt ${first.heightPt}pt;` : ''} }
html, body { margin: 0; padding: 0; background: #fff; }
img { display: block; break-after: page; }
img:last-child { break-after: auto; }`;
    doc.head.appendChild(style);
    const loads = printImages.filter(p => p.url).map(p => new Promise(resolve => {
        const img = doc.createElement('img');
        img.style.width = `${p.widthPt}pt`;
        img.style.height = `${p.heightPt}pt`;
        img.alt = '';
        img.onload = img.onerror = resolve;
        img.src = p.url;
        doc.body.appendChild(img);
    }));
    return Promise.all(loads).then(() => new Promise(resolve => {
        let finished = false;
        const done = () => {
            if (finished) return;
            finished = true;
            frame.remove();
            printEnd();
            resolve(true);
        };
        const win = frame.contentWindow;
        win.addEventListener('afterprint', () => setTimeout(done, 0), { once: true });
        try {
            win.focus();
            win.print();          // in most browsers this returns when the dialog closes
        } catch {
            done();
            return;
        }
        // For browsers whose print() returns at once and that send no afterprint.
        setTimeout(done, 120000);
    }));
}

function printEnd() {
    for (const p of printImages) if (p.url) URL.revokeObjectURL(p.url);
    printImages = [];
    printEncoding = [];
}

// ------------------------------------------------------------------ custom stamps
//
// A custom stamp is made from a picture or a PDF page the reader picks (read in the browser,
// never uploaded). The reader may keep a made stamp in this browser's stamp library: IndexedDB
// of this site, on this device only, with a count and size cap. Every storage call is guarded:
// in a private window or with storage blocked the library is simply not there.

const MaxStampFileBytes = 20 * 1024 * 1024;   // ViewerCustomStamp.MaxPdfBytes
const StampDbName = 'pdfviewer-stamps';
const MaxSavedStamps = 30;
const MaxSavedStampBytes = 64 * 1024 * 1024;

/** Opens the file picker for a custom stamp's picture or PDF; the bytes go to OnStampFile. */
export function pickStampFile() {
    const input = document.getElementById('stamp-input');
    if (!input) return;
    if (!input.dataset.ready) {
        input.dataset.ready = '1';
        input.addEventListener('change', async () => {
            const file = input.files[0];
            input.value = '';
            if (!file) return;
            if (file.size > MaxStampFileBytes) {
                app.invokeMethodAsync('OnStampFileRejected', 'This file is larger than 20 MB, the most a stamp is made from.');
                return;
            }
            try {
                const bytes = new Uint8Array(await file.arrayBuffer());
                await app.invokeMethodAsync('OnStampFile', file.name, bytes);
            } catch {
                app.invokeMethodAsync('OnStampFileRejected', 'The browser could not read this file.');
            }
        });
    }
    input.click();
}

/** Draws the custom stamp dialog's preview (a BGRA bitmap). */
export function drawStampPreview(view, width, height) {
    const canvas = document.getElementById('stamp-preview');
    if (!canvas) return;
    drawInto(canvas, view, width, height);
    // Drawn at the device's pixel ratio, shown at its size in CSS pixels.
    const ratio = window.devicePixelRatio || 1;
    canvas.style.width = `${width / ratio}px`;
    canvas.style.height = `${height / ratio}px`;
}

// Two stores: "meta" holds a small record per stamp (id, name, kind, size, created), listed
// without reading any stamp's bytes; "data" holds the bytes (id, data), read only to use one.
const StampMeta = 'meta';
const StampData = 'data';

function openStampDb() {
    return new Promise((resolve, reject) => {
        let request;
        try {
            request = indexedDB.open(StampDbName, 2);
        } catch (e) {
            reject(e);
            return;
        }
        let blocked = false;
        request.onupgradeneeded = () => {
            const db = request.result;
            for (const name of Array.from(db.objectStoreNames)) db.deleteObjectStore(name);
            db.createObjectStore(StampMeta, { keyPath: 'id' });
            db.createObjectStore(StampData, { keyPath: 'id' });
        };
        request.onsuccess = () => {
            const db = request.result;
            // Opened after it was blocked: the caller has given up, so the connection is closed at once.
            if (blocked) {
                db.close();
                return;
            }
            db.onversionchange = () => db.close();
            resolve(db);
        };
        request.onerror = () => reject(request.error);
        request.onblocked = () => {
            blocked = true;
            reject(new Error('The stamp library is blocked.'));
        };
    });
}

/** Runs work(stores) in one transaction over both stores; resolves with what work's result holds when the transaction ends. */
async function withStamps(mode, work) {
    const db = await openStampDb();
    try {
        return await new Promise((resolve, reject) => {
            const tx = db.transaction([StampMeta, StampData], mode);
            const result = work(tx.objectStore(StampMeta), tx.objectStore(StampData), tx);
            tx.oncomplete = () => resolve(result && typeof result.value === 'function' ? result.value() : result);
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error);
        });
    } finally {
        db.close();
    }
}

/** The saved stamps as JSON [{ id, name, kind, size }], oldest first; "null" when this browser keeps none. Reads no stamp's bytes. */
export async function stampLibraryList() {
    try {
        const all = await withStamps('readonly', meta => {
            const request = meta.getAll();
            return { value: () => request.result };
        });
        return JSON.stringify(all
            .filter(r => r && typeof r.id === 'string' && typeof r.size === 'number')
            .sort((a, b) => (a.created || 0) - (b.created || 0))
            .map(r => ({ id: r.id, name: String(r.name || 'Stamp'), kind: r.kind === 'Page' ? 'Page' : 'Image', size: r.size })));
    } catch {
        return 'null';
    }
}

/**
 * Keeps a made stamp (its one-page PDF); '' when kept (or kept already), else why not. The caps
 * are checked in the same transaction as the write, so two tabs saving at once cannot pass them.
 */
export async function stampLibrarySave(id, name, kind, view) {
    const data = view.slice();   // a copy out of WebAssembly memory, before the first await
    try {
        return await withStamps('readwrite', (meta, store, tx) => {
            let answer = '';
            const request = meta.getAll();
            request.onsuccess = () => {
                const all = request.result || [];
                if (all.some(r => r.id === id)) return;
                if (all.length >= MaxSavedStamps) {
                    answer = `The stamp library keeps at most ${MaxSavedStamps} stamps. Delete one first.`;
                    return;
                }
                const total = all.reduce((n, r) => n + (typeof r.size === 'number' ? r.size : 0), 0);
                if (total + data.byteLength > MaxSavedStampBytes) {
                    answer = 'The stamp library is full (64 MB). Delete a stamp first.';
                    return;
                }
                store.put({ id, data });
                meta.put({ id, name, kind, size: data.byteLength, created: Date.now() });
            };
            return { value: () => answer };
        });
    } catch {
        return 'This browser does not let the viewer keep stamps (its storage is off or full).';
    }
}

/** Gives a saved stamp's bytes to OnStampLibraryBytes; false when it is not there. */
export async function stampLibraryLoad(id) {
    try {
        const r = await withStamps('readonly', (meta, store) => {
            const m = meta.get(id), d = store.get(id);
            return { value: () => (m.result && d.result ? { meta: m.result, data: d.result.data } : null) };
        });
        if (!r || !(r.data instanceof Uint8Array)) return false;
        await app.invokeMethodAsync('OnStampLibraryBytes', r.meta.id, r.meta.kind === 'Page' ? 'Page' : 'Image', String(r.meta.name || 'Stamp'), r.data);
        return true;
    } catch {
        return false;
    }
}

/** Deletes a saved stamp; false when the browser's storage refused. */
export async function stampLibraryDelete(id) {
    try {
        await withStamps('readwrite', (meta, store) => {
            meta.delete(id);
            store.delete(id);
        });
        return true;
    } catch {
        return false;
    }
}

// ------------------------------------------------------------------ embedding

const PROTOCOL = 'pdfviewer-embed';
const PROTOCOL_VERSION = 1;
let hostOrigin = null;     // the only origin heard and written to

/**
 * Listens to the host page. Only the parent window, from the origin the frame's URL names, with
 * this protocol, is heard; the .NET side then validates the message (EmbedProtocol). The file's
 * bytes come as a transferred ArrayBuffer beside the message.
 */
export function initEmbed(dotnet, origin, readyJson) {
    hostOrigin = origin;
    window.addEventListener('message', e => {
        if (e.source !== window.parent || e.origin !== hostOrigin) return;
        const m = e.data;
        if (!m || typeof m !== 'object' || m.protocol !== PROTOCOL || m.version !== PROTOCOL_VERSION) return;
        if (m.type === 'open') {
            const bytes = m.data instanceof ArrayBuffer ? new Uint8Array(m.data) : new Uint8Array(0);
            const { data, ...rest } = m;
            dotnet.invokeMethodAsync('OnEmbedOpen', JSON.stringify(rest), bytes);
        } else if (m.type === 'call') {
            const { data, ...rest } = m;
            if (m.method === 'addCustomStamp' && data instanceof ArrayBuffer && data.byteLength <= MaxStampFileBytes) {
                // addCustomStamp's picture or PDF beside the call; bytes beside any other call are dropped.
                dotnet.invokeMethodAsync('OnEmbedCallBytes', JSON.stringify(rest), new Uint8Array(data));
            } else {
                dotnet.invokeMethodAsync('OnEmbedCall', JSON.stringify(rest));
            }
        } else if (m.type === 'range') {
            // The answer to a fetchRange request: its bytes beside it, checked against the request in .NET.
            const bytes = m.data instanceof ArrayBuffer ? new Uint8Array(m.data) : new Uint8Array(0);
            const { data, ...rest } = m;
            dotnet.invokeMethodAsync('OnEmbedRange', JSON.stringify(rest), bytes);
        }
    });
    // The reader went back to the host page: with SAVE_API focus polling, unsaved edits are saved.
    window.addEventListener('blur', () => dotnet.invokeMethodAsync('OnViewerBlur'));
    embedPost(readyJson);
}

/**
 * Posts a message with the document's bytes beside it: a copy of the bytes in WebAssembly memory,
 * transferred to the host page (its SAVE_API callback), to its origin only.
 */
export function embedPostBytes(json, view) {
    if (!hostOrigin || window.parent === window) return;
    const message = JSON.parse(json);
    const bytes = view.slice();
    message.data = bytes.buffer;
    window.parent.postMessage(message, hostOrigin, [message.data]);
}

/**
 * A file read by ranges showed its first page: a User Timing mark, so that a test (or the
 * developer tools) can read the time and the bytes read. Nothing is sent anywhere.
 */
export function markRender(page, interpretMs, drawMs) {
    try {
        performance.mark('pdfviewer:render', { detail: { page, interpretMs, drawMs } });
    } catch { /* no User Timing level 3 */ }
}

export function markFirstPage(ms, bytes) {
    try {
        performance.mark('pdfviewer:first-page', { detail: { ms, bytes } });
    } catch { /* no User Timing level 3 */ }
}

/** Posts a message that the .NET side built (EmbedProtocol) to the host page, and to no other origin. */
export function embedPost(json) {
    if (hostOrigin && window.parent !== window) window.parent.postMessage(JSON.parse(json), hostOrigin);
}

// ---------------------------------------------------------------- the render worker (opt-in)
//
// index.html?renderInWorker=1 draws pages in js/render-worker.js: a second .NET runtime in a
// dedicated worker, so that interpreting and drawing a page never blocks this thread. Pages
// come back as ImageBitmaps and wait here until the .NET side asks for them to be shown. When
// the worker cannot start, startRenderWorker answers false and the pages are drawn here, as
// without the flag.

let renderWorker = null;
let workerCalls = 0;
const workerWaits = new Map();     // call id -> { resolve, reject }
const workerBitmaps = new Map();   // page -> ImageBitmap rendered and not shown yet

// A call that gets no answer in this time means the worker's runtime is stuck or gone.
const WORKER_CALL_MS = { open: 30000, render: 20000, close: 5000 };

function workerCall(msg, transfer) {
    return new Promise((resolve, reject) => {
        if (!renderWorker) {
            reject(new Error('The render worker is not running.'));
            return;
        }
        msg.id = ++workerCalls;
        const timer = setTimeout(() => workerGone('The render worker did not answer.'), WORKER_CALL_MS[msg.op] ?? 20000);
        workerWaits.set(msg.id, { resolve, reject, timer });
        renderWorker.postMessage(msg, transfer ?? []);
    });
}

/** Stops a worker that crashed or stopped answering: every call waiting on it fails, so the pages fall back to this thread. */
function workerGone(message) {
    renderWorker?.terminate();
    renderWorker = null;
    for (const wait of workerWaits.values()) {
        clearTimeout(wait.timer);
        wait.reject(new Error(message));
    }
    workerWaits.clear();
    for (const bitmap of workerBitmaps.values()) bitmap.close();
    workerBitmaps.clear();
}

/**
 * Starts the render worker; true once its runtime is up, false when it fails or takes over 20 s.
 * `fontBase` is the font folder of the embedded frame (EmbedFrame.FontBase), or null.
 */
export function startRenderWorker(fontBase) {
    if (typeof Worker !== 'function' || typeof createImageBitmap !== 'function') return Promise.resolve(false);
    return new Promise(resolve => {
        let settled = false;
        const settle = ok => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            if (!ok) {
                renderWorker?.terminate();
                renderWorker = null;
            }
            resolve(ok);
        };
        const timer = setTimeout(() => settle(false), 20000);
        try {
            // The worker carries this module's version query (?v=), so both are replaced together.
            const url = new URL('render-worker.js', import.meta.url);
            url.search = new URL(import.meta.url).search;
            const fonts = fontBaseOf(fontBase);
            if (fonts) url.searchParams.set('fonts', fonts);
            renderWorker = new Worker(url, { type: 'module', name: 'render' });
        } catch {
            settle(false);
            return;
        }
        renderWorker.onerror = () => {
            if (settled) workerGone('The render worker failed.');
            else settle(false);
        };
        renderWorker.onmessage = e => {
            const d = e.data;
            if (d.op === 'ready') return settle(true);
            if (d.op === 'failed') return settled ? workerGone('The render worker failed.') : settle(false);
            const wait = workerWaits.get(d.id);
            if (!wait) return;
            clearTimeout(wait.timer);
            workerWaits.delete(d.id);
            if (d.ok) wait.resolve(d.result);
            else wait.reject(new Error(d.message));
        };
    });
}

/** Opens a copy of the document in the worker (the bytes are copied out of WebAssembly memory now). */
export async function workerOpen(view) {
    const bytes = view.slice();
    for (const bitmap of workerBitmaps.values()) bitmap.close();
    workerBitmaps.clear();
    const result = await workerCall({ op: 'open', bytes: bytes.buffer }, [bytes.buffer]);
    return result.pages;
}

/** Renders a page in the worker and keeps it here; its size and timings as JSON. */
export async function workerRender(page, scale, night) {
    const r = await workerCall({ op: 'render', page, scale, night });
    workerBitmaps.get(page)?.close();
    workerBitmaps.set(page, r.bitmap);
    return JSON.stringify({ width: r.width, height: r.height, interpretMs: r.interpretMs, drawMs: r.drawMs,
                            textWithoutFont: r.textWithoutFont, imagesNotDecoded: r.imagesNotDecoded });
}

/** Shows a page that the worker rendered in the page's canvas. */
export function workerDraw(page) {
    const bitmap = workerBitmaps.get(page);
    workerBitmaps.delete(page);
    if (!bitmap) return;
    const canvas = document.querySelector(`#page-${page} canvas`);
    if (canvas) {
        if (canvas.width !== bitmap.width) canvas.width = bitmap.width;
        if (canvas.height !== bitmap.height) canvas.height = bitmap.height;
        canvas.getContext('2d', { alpha: false }).drawImage(bitmap, 0, 0);
        canvas.closest('.page')?.classList.add('drawn');
    }
    bitmap.close();
}

/** Drops a page that the worker rendered and that is not wanted any more. */
export function workerDiscard(page) {
    workerBitmaps.get(page)?.close();
    workerBitmaps.delete(page);
}

/** Closes the worker's copy of the document. */
export function workerClose() {
    for (const bitmap of workerBitmaps.values()) bitmap.close();
    workerBitmaps.clear();
    if (renderWorker) workerCall({ op: 'close' }).catch(() => { });
}

/** Whether the render worker is running. */
export function workerRunning() {
    return renderWorker !== null;
}

/** Stops the render worker and its copy of the document. */
export function workerTerminate() {
    workerGone('The render worker was stopped.');
}
