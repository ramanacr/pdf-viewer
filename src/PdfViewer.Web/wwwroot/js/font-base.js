// Where the bundled fonts are fetched from: the one rule that viewer.js and render-worker.js share.
// A font folder that the embed SDK names (its fontBaseUrl option, ?fonts= in the frame's address)
// must be a plain absolute path on this origin: it starts and ends with '/', and each segment has
// only letters, digits and . _ ~ - and does not start with '.'. The same rule is in
// EmbedFrame.ParseFontBase (C#) and pdfviewer-embed.js (fontBasePath); tests/sdk/font-base-cases.json
// is the shared list of inputs that both test suites check.

const FONT_BASE = /^\/(?:[A-Za-z0-9_~-][A-Za-z0-9._~-]*\/)*$/;

/** The font folder if the value is a valid one, else null (then the default folder is used). */
export function fontBaseOf(value) {
    return typeof value === 'string' && value.length <= 512 && FONT_BASE.test(value) ? value : null;
}

/**
 * The address of a bundled font file ('LiberationSans-Regular.ttf', 'noto/index.json') in the
 * folder `base` (checked by fontBaseOf) or else in `fallback`; null for a file name that is not plain.
 */
export function fontFilePath(file, base, fallback) {
    const parts = String(file).split('/');
    if (parts.some(p => p === '' || p === '.' || p === '..')) return null;
    return (fontBaseOf(base) ?? fallback) + parts.map(encodeURIComponent).join('/');
}
