// Tests of the embed SDK's fontBaseUrl option (src/PdfViewer.Web/wwwroot/pdfviewer-embed.js): the
// path the viewer frame is given, and the URLs refused. No packages and no network: run with
// `node --test tests/sdk`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInThisContext } from 'node:vm';
import { fileURLToPath } from 'node:url';

const sdkPath = fileURLToPath(new URL('../../src/PdfViewer.Web/wwwroot/pdfviewer-embed.js', import.meta.url));
globalThis.document = { baseURI: 'https://host.test/page.html', currentScript: null };
runInThisContext(readFileSync(sdkPath, 'utf8'), { filename: sdkPath });
const { fontBasePath } = globalThis.PdfViewer._internals;

const viewer = new URL('https://pdf.example.com/viewer/');

test('no fontBaseUrl: the default fonts folder next to the viewer', () => {
    assert.equal(fontBasePath(undefined, viewer), null);
    assert.equal(fontBasePath(null, viewer), null);
});

test('a folder on the viewer origin becomes an absolute path', () => {
    assert.equal(fontBasePath('https://pdf.example.com/static/pdf-fonts/', viewer), '/static/pdf-fonts/');
    assert.equal(fontBasePath('https://pdf.example.com/static/pdf-fonts', viewer), '/static/pdf-fonts/');
    assert.equal(fontBasePath('/shared/fonts/', viewer), '/shared/fonts/');
    assert.equal(fontBasePath('fonts-v2/', viewer), '/viewer/fonts-v2/');      // relative to the viewer folder
    assert.equal(fontBasePath('../assets/fonts/', viewer), '/assets/fonts/');   // normalised by URL
    assert.equal(fontBasePath(new URL('https://pdf.example.com/f/?x=1#y'), viewer), '/f/');
});

test('another origin is refused: fonts never come from a third party', () => {
    for (const bad of ['https://fonts.googleapis.com/css2', 'https://fonts.gstatic.com/s/', 'http://pdf.example.com/fonts/',
        'https://pdf.example.com:8443/fonts/', '//cdn.example.com/fonts/', 'data:font/ttf;base64,AAAA']) {
        assert.throws(() => fontBasePath(bad, viewer), e => e.code === 'INVALID_ARGUMENT', bad);
    }
});

test('a path the viewer would not accept is refused', () => {
    for (const bad of ['/fonts%20here/', '/fönts/', '/.hidden/', '/a//b/']) {
        assert.throws(() => fontBasePath(bad, viewer), e => e.code === 'INVALID_ARGUMENT', bad);
    }
    assert.throws(() => fontBasePath(42, viewer), e => e.code === 'INVALID_ARGUMENT');
    assert.throws(() => fontBasePath('/' + 'a'.repeat(600) + '/', viewer), e => e.code === 'INVALID_ARGUMENT');
});
